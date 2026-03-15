using Microsoft.Extensions.Logging;
using Symphony.App.Config;
using Symphony.App.Orchestration;
using Symphony.App.Utils;
using System.Diagnostics;
using System.Text.Json;

namespace Symphony.App.Agent.Codex;

/// <summary>
/// A custom implementation for Codex (local code execution agent).
/// This provides an abstraction layer that can be extended to support different agent backends.
/// </summary>
class CodexAgent : ICodingAgent
{
    static CancellationTokenSource withReadTimeout(ServiceConfig config, CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (config?.Codex?.ReadTimeoutMs > 0)
            cts.CancelAfter(config.Codex.ReadTimeoutMs);

        return cts;
    }

    static CancellationTokenSource withInitializationTimeout(ServiceConfig config, CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var initTimeoutMs = Math.Max(config?.Codex?.TurnTimeoutMs ?? 0, 30000);

        if (initTimeoutMs > 0)
            cts.CancelAfter(initTimeoutMs);

        return cts;
    }

    static string? extractId(JsonElement response)
    {
        if (response.ValueKind == JsonValueKind.Object)
        {
            if (response.TryGetProperty("thread_id", out var threadId) && threadId.ValueKind == JsonValueKind.String)
                return threadId.GetString();

            if (response.TryGetProperty("turn_id", out var turnId) && turnId.ValueKind == JsonValueKind.String)
                return turnId.GetString();

            if (response.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                return id.GetString();
        }

        return null;
    }

    static void tryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(true);
        }
        catch
        {
            // ignored
        }
    }

    readonly ILogger<CodexAgent> logger;
    readonly ServiceConfig config;
    AppServerSession? session;

    public CodexAgent(ILogger<CodexAgent> logger, ServiceConfig config)
    {
        this.logger = logger;
        this.config = config;
    }

    async Task initializeCodexAsync(ServiceConfig config, string workspacePath, LiveSession liveSession, Action<CodingAgentEvent> onEvent, CancellationToken cancellationToken)
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
                    approval_policy = config?.Codex?.ApprovalPolicy,
                    thread_sandbox = config?.Codex?.ThreadSandbox
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

        session = new AppServerSession(process, protocol, threadId);
    }

    async Task shutdownCodexAsync(CancellationToken cancellationToken)
    {
        if (session is not null)
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
            session = null;
        }
    }

    public async Task InitializeAsync(string workspacePath, LiveSession liveSession, Action<CodingAgentEvent> onEvent, CancellationToken cancellationToken)
        => await initializeCodexAsync(config, workspacePath, liveSession, onEvent, cancellationToken);

    public async Task<string?> ExecuteTurnAsync(string prompt, LiveSession liveSession, CancellationToken cancellationToken)
        => await ExecuteCodexTurnAsync(config, prompt, liveSession, cancellationToken);

    public async Task ShutdownAsync(CancellationToken cancellationToken)
        => await shutdownCodexAsync(cancellationToken);

    public async Task<string?> ExecuteCodexTurnAsync(ServiceConfig config, string prompt, LiveSession liveSession, CancellationToken cancellationToken)
    {
        if (session is null)
            throw new InvalidOperationException("Agent has not been initialized. Call InitializeAsync first.");

        JsonElement response;
        using (var cts = withReadTimeout(config, cancellationToken))
        {
            response = await session.Protocol.RequestAsync("turn/start", new
            {
                thread_id = session.ThreadId,
                prompt,
                turn_timeout_ms = config?.Codex?.TurnTimeoutMs,
                turn_sandbox_policy = config?.Codex?.TurnSandboxPolicy
            }, cts.Token);
        }

        var turnId = extractId(response);
        liveSession.TurnId = turnId;

        if (!string.IsNullOrWhiteSpace(turnId))
            liveSession.SessionId = $"{session.ThreadId}-{turnId}";

        return turnId;
    }
}
