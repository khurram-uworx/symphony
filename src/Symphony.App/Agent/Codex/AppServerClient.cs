using Microsoft.Extensions.Logging;
using Symphony.App.Config;
using Symphony.App.Orchestration;
using Symphony.App.Utils;
using System.Diagnostics;
using System.Text.Json;

namespace Symphony.App.Agent.Codex;

record TurnResult(string? TurnId);

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
        if (config?.Codex?.ReadTimeoutMs > 0)
        {
            cts.CancelAfter(config.Codex.ReadTimeoutMs);
        }

        return cts;
    }

    static CancellationTokenSource withInitializationTimeout(ServiceConfig config, CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Use a longer timeout for initialization operations since the process startup may take longer
        // Use the turn timeout (default 3600000ms/1hr) or a reasonable minimum of 30 seconds
        var initTimeoutMs = Math.Max(config?.Codex?.TurnTimeoutMs ?? 0, 30000);
        if (initTimeoutMs > 0)
        {
            cts.CancelAfter(initTimeoutMs);
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

    readonly ILogger<AppServerClient> logger;

    public AppServerClient(ILogger<AppServerClient> logger)
    {
        this.logger = logger;
    }

    public async Task<AppServerSession> StartSessionAsync(ServiceConfig config,
        string workspacePath, LiveSession liveSession, Action<CodingAgentEvent> onEvent, CancellationToken cancellationToken)
    {
        var startInfo = ShellCommandRunner.BuildShellStartInfo(config?.Codex?.Command ?? "", workspacePath);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Start();
        liveSession.CodexAppServerPid = process.Id.ToString();

        var protocol = new JsonRpcProtocol(process, logger, onEvent, liveSession);
        await protocol.StartAsync(cancellationToken);

        try
        {
            using (var cts = withInitializationTimeout(config, cancellationToken))
            {
                await protocol.RequestAsync("initialize", new
                {
                    client_info = new { name = "symphony", version = "1.0" },
                    capabilities = new { }
                }, cts.Token);
            }
        }
        catch (OperationCanceledException ex)
        {
            tryKill(process);
            throw new InvalidOperationException("Initialization timeout: codex server failed to respond within the expected time", ex);
        }

        using (var cts = withInitializationTimeout(config, cancellationToken))
        {
            await protocol.NotifyAsync("initialized", new { }, cts.Token);
        }

        JsonElement threadResult;
        try
        {
            using (var cts = withInitializationTimeout(config, cancellationToken))
            {
                threadResult = await protocol.RequestAsync("thread/start", new
                {
                    approval_policy = config.Codex.ApprovalPolicy,
                    thread_sandbox = config.Codex.ThreadSandbox
                }, cts.Token);
            }
        }
        catch (OperationCanceledException ex)
        {
            tryKill(process);
            throw new InvalidOperationException("Thread initialization timeout: codex server failed to respond within the expected time", ex);
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
            liveSession.SessionId = $"{session.ThreadId}-{turnId}";

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
            logger.LogDebug(ex, "Failed to send thread/stop.");
        }

        session.Protocol.Dispose();
        tryKill(session.Process);
    }
}
