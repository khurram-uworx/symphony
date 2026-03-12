using Microsoft.Extensions.Logging;
using Symphony.App.Config;
using Symphony.App.Orchestration;
using Symphony.App.Utils;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

namespace Symphony.App.Agent;

public sealed class AppServerClientFactory
{
    private readonly ILogger<AppServerClient> _logger;

    public AppServerClientFactory(ILogger<AppServerClient> logger)
    {
        _logger = logger;
    }

    public AppServerClient Create() => new(_logger);
}

public sealed class AppServerClient
{
    private readonly ILogger<AppServerClient> _logger;

    public AppServerClient(ILogger<AppServerClient> logger)
    {
        _logger = logger;
    }

    public async Task<AppServerSession> StartSessionAsync(ServiceConfig config, string workspacePath, LiveSession liveSession, Action<CodexEvent> onEvent, CancellationToken cancellationToken)
    {
        var startInfo = ShellCommandRunner.BuildShellStartInfo(config.Codex.Command, workspacePath);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Start();
        liveSession.CodexAppServerPid = process.Id.ToString();

        var protocol = new JsonRpcProtocol(process, _logger, onEvent, liveSession);
        await protocol.StartAsync(cancellationToken);

        using (var cts = WithReadTimeout(config, cancellationToken))
        {
            await protocol.RequestAsync("initialize", new
            {
                client_info = new { name = "symphony", version = "1.0" },
                capabilities = new { }
            }, cts.Token);
        }

        using (var cts = WithReadTimeout(config, cancellationToken))
        {
            await protocol.NotifyAsync("initialized", new { }, cts.Token);
        }

        JsonElement threadResult;
        using (var cts = WithReadTimeout(config, cancellationToken))
        {
            threadResult = await protocol.RequestAsync("thread/start", new
            {
                approval_policy = config.Codex.ApprovalPolicy,
                thread_sandbox = config.Codex.ThreadSandbox
            }, cts.Token);
        }

        var threadId = ExtractId(threadResult) ?? Guid.NewGuid().ToString("N");
        liveSession.ThreadId = threadId;

        return new AppServerSession(process, protocol, threadId);
    }

    public async Task<TurnResult> RunTurnAsync(AppServerSession session, ServiceConfig config, string prompt, LiveSession liveSession, CancellationToken cancellationToken)
    {
        JsonElement response;
        using (var cts = WithReadTimeout(config, cancellationToken))
        {
            response = await session.Protocol.RequestAsync("turn/start", new
            {
                thread_id = session.ThreadId,
                prompt,
                turn_timeout_ms = config.Codex.TurnTimeoutMs,
                turn_sandbox_policy = config.Codex.TurnSandboxPolicy
            }, cts.Token);
        }

        var turnId = ExtractId(response);
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
        TryKill(session.Process);
    }

    private static CancellationTokenSource WithReadTimeout(ServiceConfig config, CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (config.Codex.ReadTimeoutMs > 0)
        {
            cts.CancelAfter(config.Codex.ReadTimeoutMs);
        }

        return cts;
    }

    private static string? ExtractId(JsonElement response)
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

    private static void TryKill(Process process)
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
}

public sealed class AppServerSession
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

public sealed record TurnResult(string? TurnId);

public sealed record CodexEvent(string EventName, string? Summary, DateTimeOffset Timestamp, JsonElement Payload);

public sealed class JsonRpcProtocol : IDisposable
{
    private readonly Process _process;
    private readonly ILogger _logger;
    private readonly Action<CodexEvent> _onEvent;
    private readonly LiveSession _liveSession;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly Channel<JsonDocument> _incoming = Channel.CreateUnbounded<JsonDocument>();
    private int _nextId = 1;
    private Task? _readerTask;
    private Task? _dispatcherTask;

    public JsonRpcProtocol(Process process, ILogger logger, Action<CodexEvent> onEvent, LiveSession liveSession)
    {
        _process = process;
        _logger = logger;
        _onEvent = onEvent;
        _liveSession = liveSession;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _readerTask = Task.Run(() => ReadLoopAsync(cancellationToken), cancellationToken);
        _dispatcherTask = Task.Run(() => DispatchLoopAsync(cancellationToken), cancellationToken);
    }

    public async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var payload = new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params = parameters
        };

        await SendAsync(payload, cancellationToken);
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

        return SendAsync(payload, cancellationToken);
    }

    private async Task SendAsync(object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload);
        await _process.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken);
        await _process.StandardInput.FlushAsync(cancellationToken);
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var stdout = _process.StandardOutput;
        var stderr = _process.StandardError;

        _ = Task.Run(async () =>
        {
            while (!stderr.EndOfStream && !cancellationToken.IsCancellationRequested)
            {
                var line = await stderr.ReadLineAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(line))
                {
                    _logger.LogWarning("codex stderr: {Line}", line);
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
                await _incoming.Writer.WriteAsync(doc, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse codex JSON line.");
            }
        }
    }

    private async Task DispatchLoopAsync(CancellationToken cancellationToken)
    {
        await foreach (var doc in _incoming.Reader.ReadAllAsync(cancellationToken))
        {
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var idNode) && idNode.ValueKind == JsonValueKind.Number)
            {
                var id = idNode.GetInt32();
                if (_pending.TryRemove(id, out var tcs))
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
                UpdateUsage(payload);
                _liveSession.LastCodexEvent = method;
                _liveSession.LastCodexTimestamp = DateTimeOffset.UtcNow;
                _liveSession.LastCodexMessage = payload.ToString();
                _onEvent(new CodexEvent(method, payload.ToString(), DateTimeOffset.UtcNow, payload));
            }
        }
    }

    private void UpdateUsage(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (payload.TryGetProperty("usage", out var usage))
        {
            UpdateUsageFrom(usage);
        }
        else if (payload.TryGetProperty("metrics", out var metrics) && metrics.TryGetProperty("usage", out var metricsUsage))
        {
            UpdateUsageFrom(metricsUsage);
        }
    }

    private void UpdateUsageFrom(JsonElement usage)
    {
        if (usage.TryGetProperty("input_tokens", out var inputNode) && inputNode.TryGetInt32(out var input))
        {
            _liveSession.CodexInputTokens = input;
        }

        if (usage.TryGetProperty("output_tokens", out var outputNode) && outputNode.TryGetInt32(out var output))
        {
            _liveSession.CodexOutputTokens = output;
        }

        if (usage.TryGetProperty("total_tokens", out var totalNode) && totalNode.TryGetInt32(out var total))
        {
            _liveSession.CodexTotalTokens = total;
        }
    }

    public void Dispose()
    {
        try
        {
            _incoming.Writer.TryComplete();
        }
        catch
        {
            // ignored
        }
    }
}


