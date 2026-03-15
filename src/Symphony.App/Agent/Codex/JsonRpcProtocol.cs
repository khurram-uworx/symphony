using Microsoft.Extensions.Logging;
using Symphony.App.Orchestration;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

namespace Symphony.App.Agent.Codex;

class JsonRpcProtocol : IDisposable
{
    readonly Process process;
    readonly ILogger logger;
    readonly Action<CodingAgentEvent> onEvent;
    readonly LiveSession liveSession;
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pending = new();
    readonly Channel<JsonDocument> incoming = Channel.CreateUnbounded<JsonDocument>();
    int nextId = 1;
    Task? readerTask;
    Task? dispatcherTask;

    public JsonRpcProtocol(Process process, ILogger logger, Action<CodingAgentEvent> onEvent, LiveSession liveSession)
    {
        this.process = process;
        this.logger = logger;
        this.onEvent = onEvent;
        this.liveSession = liveSession;
    }

    async Task sendAsync(object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload);
        await process.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
    }

    async Task readLoopAsync(CancellationToken cancellationToken)
    {
        var stdout = process.StandardOutput;
        var stderr = process.StandardError;

        // Read stderr asynchronously without blocking stdout
        var stderrTask = Task.Run(async () =>
        {
            try
            {
                while (!stderr.EndOfStream && !cancellationToken.IsCancellationRequested)
                {
                    var line = await stderr.ReadLineAsync(cancellationToken);
                    if (!string.IsNullOrWhiteSpace(line))
                        logger.LogWarning("codex stderr: {Line}", line);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error reading stderr from codex process");
            }
        }, cancellationToken);

        try
        {
            while (!stdout.EndOfStream && !cancellationToken.IsCancellationRequested)
            {
                var line = await stdout.ReadLineAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    var doc = JsonDocument.Parse(line);
                    await incoming.Writer.WriteAsync(doc, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to parse codex JSON line.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation is requested
        }
        finally
        {
            incoming.Writer.TryComplete();
            // Wait for stderr task to complete
            try
            {
                await stderrTask;
            }
            catch
            {
                // Ignore exceptions from stderr task on shutdown
            }
        }
    }

    async Task dispatchLoopAsync(CancellationToken cancellationToken)
    {
        await foreach (var doc in incoming.Reader.ReadAllAsync(cancellationToken))
        {
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var idNode) && idNode.ValueKind == JsonValueKind.Number)
            {
                var id = idNode.GetInt32();
                if (pending.TryRemove(id, out var tcs))
                {
                    if (root.TryGetProperty("error", out var errorNode))
                        tcs.TrySetException(new InvalidOperationException(errorNode.ToString()));
                    else if (root.TryGetProperty("result", out var resultNode))
                        tcs.TrySetResult(resultNode);
                    else
                        tcs.TrySetResult(root);
                }

                continue;
            }

            if (root.TryGetProperty("method", out var methodNode) && methodNode.ValueKind == JsonValueKind.String)
            {
                var method = methodNode.GetString() ?? "unknown";
                var payload = root.TryGetProperty("params", out var paramsNode) ? paramsNode : root;
                updateUsage(payload);
                liveSession.LastCodexEvent = method;
                liveSession.LastCodexTimestamp = DateTimeOffset.UtcNow;
                liveSession.LastCodexMessage = payload.ToString();
                onEvent(new CodingAgentEvent(method, payload.ToString(), DateTimeOffset.UtcNow, payload));
            }
        }
    }

    void updateUsage(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return;

        if (payload.TryGetProperty("usage", out var usage))
            updateUsageFrom(usage);
        else if (payload.TryGetProperty("metrics", out var metrics) && metrics.TryGetProperty("usage", out var metricsUsage))
            updateUsageFrom(metricsUsage);
    }

    void updateUsageFrom(JsonElement usage)
    {
        if (usage.TryGetProperty("input_tokens", out var inputNode) && inputNode.TryGetInt32(out var input))
            liveSession.CodexInputTokens = input;

        if (usage.TryGetProperty("output_tokens", out var outputNode) && outputNode.TryGetInt32(out var output))
            liveSession.CodexOutputTokens = output;

        if (usage.TryGetProperty("total_tokens", out var totalNode) && totalNode.TryGetInt32(out var total))
            liveSession.CodexTotalTokens = total;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        readerTask = Task.Run(() => readLoopAsync(cancellationToken), cancellationToken);
        dispatcherTask = Task.Run(() => dispatchLoopAsync(cancellationToken), cancellationToken);
        return Task.CompletedTask;
    }

    public async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = tcs;

        var payload = new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params = parameters
        };

        await sendAsync(payload, cancellationToken);
        using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        return await tcs.Task;
    }

    public Task NotifyAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        var payload = new
        {
            jsonrpc = "2.0",
            method,
            @params = parameters
        };

        return sendAsync(payload, cancellationToken);
    }

    public void Dispose()
    {
        try
        {
            incoming.Writer.TryComplete();
        }
        catch
        {
            // ignored
        }
    }
}
