using Microsoft.Extensions.Logging;
using Symphony.App.Config;
using Symphony.App.Orchestration;
using Symphony.App.Utils;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

namespace Symphony.App.Agent;

record TurnResult(string? TurnId);
record CodexEvent(string EventName, string? Summary, DateTimeOffset Timestamp, JsonElement Payload);

class AppServerSession
{
    public AppServerSession(Process process, JsonRpcProtocol protocol, string threadId)
    {
        Process = process;
        Protocol = protocol;
        ThreadId = threadId;
    }

    public Process Process { get; }
    public JsonRpcProtocol Protocol { get; }
    public string ThreadId { get; }
}

class AppServerClientFactory
{
    readonly ILogger<AppServerClient> logger;

    public AppServerClientFactory(ILogger<AppServerClient> logger)
    {
        this.logger = logger;
    }

    public AppServerClient Create() => new(logger);
}

class AppServerClient
{
    static CancellationTokenSource withReadTimeout(ServiceConfig config, CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (config.Codex.ReadTimeoutMs > 0)
        {
            cts.CancelAfter(config.Codex.ReadTimeoutMs);
        }

        return cts;
    }

    static string? extractId(JsonElement response)
    {
        if (response.ValueKind == JsonValueKind.Object)
        {
            if (response.TryGetProperty("thread_id", out var threadId) && threadId.ValueKind == JsonValueKind.String)
            {
                return threadId.GetString();
            }

            if (response.TryGetProperty("turn_id", out var turnId) && turnId.ValueKind == JsonValueKind.String)
            {
                return turnId.GetString();
            }

            if (response.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            {
                return id.GetString();
            }
        }

        return null;
    }

    static void tryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch
        {
            // ignored
        }
    }

    readonly ILogger<AppServerClient> _logger;

    public AppServerClient(ILogger<AppServerClient> logger)
    {
        _logger = logger;
    }

    public async Task<AppServerSession> StartSessionAsync(ServiceConfig config,
        string workspacePath, LiveSession liveSession, Action<CodexEvent> onEvent, CancellationToken cancellationToken)
    {
        var startInfo = ShellCommandRunner.BuildShellStartInfo(config.Codex.Command, workspacePath);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Start();
        liveSession.CodexAppServerPid = process.Id.ToString();

        var protocol = new JsonRpcProtocol(process, _logger, onEvent, liveSession);
        await protocol.StartAsync(cancellationToken);

        using (var cts = withReadTimeout(config, cancellationToken))
        {
            await protocol.RequestAsync("initialize", new
            {
                client_info = new { name = "symphony", version = "1.0" },
                capabilities = new { }
            }, cts.Token);
        }

        using (var cts = withReadTimeout(config, cancellationToken))
        {
            await protocol.NotifyAsync("initialized", new { }, cts.Token);
        }

        JsonElement threadResult;
        using (var cts = withReadTimeout(config, cancellationToken))
        {
            threadResult = await protocol.RequestAsync("thread/start", new
            {
                approval_policy = config.Codex.ApprovalPolicy,
                thread_sandbox = config.Codex.ThreadSandbox
            }, cts.Token);
        }

        var threadId = extractId(threadResult) ?? Guid.NewGuid().ToString("N");
        liveSession.ThreadId = threadId;

        return new AppServerSession(process, protocol, threadId);
    }

    public async Task<TurnResult> RunTurnAsync(AppServerSession session, ServiceConfig config,
        string prompt, LiveSession liveSession, CancellationToken cancellationToken)
    {
        JsonElement response;
        using (var cts = withReadTimeout(config, cancellationToken))
        {
            response = await session.Protocol.RequestAsync("turn/start", new
            {
                thread_id = session.ThreadId,
                prompt,
                turn_timeout_ms = config.Codex.TurnTimeoutMs,
                turn_sandbox_policy = config.Codex.TurnSandboxPolicy
            }, cts.Token);
        }

        var turnId = extractId(response);
        liveSession.TurnId = turnId;
        if (!string.IsNullOrWhiteSpace(turnId))
        {
            liveSession.SessionId = $"{session.ThreadId}-{turnId}";
        }

        return new TurnResult(turnId);
    }

    public async Task StopSessionAsync(AppServerSession session, CancellationToken cancellationToken)
    {
        try
        {
            await session.Protocol.NotifyAsync("thread/stop", new { thread_id = session.ThreadId }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to send thread/stop.");
        }

        session.Protocol.Dispose();
        tryKill(session.Process);
    }
}

class JsonRpcProtocol : IDisposable
{
    readonly Process process;
    readonly ILogger logger;
    readonly Action<CodexEvent> onEvent;
    readonly LiveSession liveSession;
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pending = new();
    readonly Channel<JsonDocument> incoming = Channel.CreateUnbounded<JsonDocument>();
    int nextId = 1;
    Task? readerTask;
    Task? dispatcherTask;

    public JsonRpcProtocol(Process process, ILogger logger, Action<CodexEvent> onEvent, LiveSession liveSession)
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

        _ = Task.Run(async () =>
        {
            while (!stderr.EndOfStream && !cancellationToken.IsCancellationRequested)
            {
                var line = await stderr.ReadLineAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(line))
                {
                    logger.LogWarning("codex stderr: {Line}", line);
                }
            }
        }, cancellationToken);

        while (!stdout.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await stdout.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

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
                    {
                        tcs.TrySetException(new InvalidOperationException(errorNode.ToString()));
                    }
                    else if (root.TryGetProperty("result", out var resultNode))
                    {
                        tcs.TrySetResult(resultNode);
                    }
                    else
                    {
                        tcs.TrySetResult(root);
                    }
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
                onEvent(new CodexEvent(method, payload.ToString(), DateTimeOffset.UtcNow, payload));
            }
        }
    }

    void updateUsage(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (payload.TryGetProperty("usage", out var usage))
        {
            updateUsageFrom(usage);
        }
        else if (payload.TryGetProperty("metrics", out var metrics) && metrics.TryGetProperty("usage", out var metricsUsage))
        {
            updateUsageFrom(metricsUsage);
        }
    }

    void updateUsageFrom(JsonElement usage)
    {
        if (usage.TryGetProperty("input_tokens", out var inputNode) && inputNode.TryGetInt32(out var input))
        {
            liveSession.CodexInputTokens = input;
        }

        if (usage.TryGetProperty("output_tokens", out var outputNode) && outputNode.TryGetInt32(out var output))
        {
            liveSession.CodexOutputTokens = output;
        }

        if (usage.TryGetProperty("total_tokens", out var totalNode) && totalNode.TryGetInt32(out var total))
        {
            liveSession.CodexTotalTokens = total;
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        readerTask = Task.Run(() => readLoopAsync(cancellationToken), cancellationToken);
        dispatcherTask = Task.Run(() => dispatchLoopAsync(cancellationToken), cancellationToken);
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
