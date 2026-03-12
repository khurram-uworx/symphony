using Microsoft.Extensions.Logging;
using Symphony.App.Config;
using Symphony.App.Domain;
using Symphony.App.Linear;
using Symphony.App.Orchestration;
using Symphony.App.Workflows;
using Symphony.App.Workspaces;

namespace Symphony.App.Agent;

public sealed class AgentRunner
{
    private readonly ServiceConfigProvider _configProvider;
    private readonly WorkspaceManager _workspaceManager;
    private readonly AppServerClientFactory _clientFactory;
    private readonly LinearClient _linearClient;
    private readonly ILogger<AgentRunner> _logger;
    private readonly PromptRenderer _promptRenderer;

    public AgentRunner(
        ServiceConfigProvider configProvider,
        WorkspaceManager workspaceManager,
        AppServerClientFactory clientFactory,
        LinearClient linearClient,
        ILogger<AgentRunner> logger)
    {
        _configProvider = configProvider;
        _workspaceManager = workspaceManager;
        _clientFactory = clientFactory;
        _linearClient = linearClient;
        _logger = logger;
        _promptRenderer = new PromptRenderer();
    }

    public async Task<AgentRunResult> RunAttemptAsync(Issue issue, int? attempt, LiveSession liveSession, Action<CodexEvent> onEvent, CancellationToken cancellationToken)
    {
        var config = _configProvider.GetConfig();
        using var scope = _logger.BeginScope(new Dictionary<string, object> { { "issue_id", issue.Id }, { "issue_identifier", issue.Identifier } });
        Workspace workspace;
        try
        {
            workspace = await _workspaceManager.CreateForIssueAsync(issue.Identifier, cancellationToken);
        }
        catch (Exception ex)
        {
            return AgentRunResult.Fail("workspace error", ex);
        }

        if (!await _workspaceManager.RunHookAsync(config.Hooks.BeforeRun, workspace.Path, config.Hooks.TimeoutMs, cancellationToken))
        {
            return AgentRunResult.Fail("before_run hook error");
        }

        var client = _clientFactory.Create();
        AppServerSession? session = null;
        try
        {
            session = await client.StartSessionAsync(config, workspace.Path, liveSession, onEvent, cancellationToken);
        }
        catch (Exception ex)
        {
            await _workspaceManager.RunHookBestEffortAsync(config.Hooks.AfterRun, workspace.Path, config.Hooks.TimeoutMs, cancellationToken);
            return AgentRunResult.Fail("agent session startup error", ex);
        }

        try
        {
            var maxTurns = config.Agent.MaxTurns;
            var turn = 1;
            while (true)
            {
                var prompt = string.IsNullOrWhiteSpace(config.Workflow.PromptTemplate)
                    ? "You are working on an issue from Linear."
                    : _promptRenderer.Render(config.Workflow.PromptTemplate, issue, attempt);

                await client.RunTurnAsync(session, config, prompt, liveSession, cancellationToken);
                liveSession.TurnCount++;

                var refreshed = await _linearClient.FetchIssueStatesByIdsAsync(config, new[] { issue.Id }, cancellationToken);
                if (refreshed.Count == 0)
                {
                    break;
                }

                issue = refreshed[0];
                if (!config.ActiveStates.Contains(issue.NormalizedState))
                {
                    break;
                }

                if (turn >= maxTurns)
                {
                    break;
                }

                turn++;
            }
        }
        catch (WorkflowException ex)
        {
            await _workspaceManager.RunHookBestEffortAsync(config.Hooks.AfterRun, workspace.Path, config.Hooks.TimeoutMs, cancellationToken);
            return AgentRunResult.Fail("prompt error", ex);
        }
        catch (Exception ex)
        {
            await _workspaceManager.RunHookBestEffortAsync(config.Hooks.AfterRun, workspace.Path, config.Hooks.TimeoutMs, cancellationToken);
            return AgentRunResult.Fail("agent turn error", ex);
        }
        finally
        {
            if (session is not null)
            {
                await client.StopSessionAsync(session, cancellationToken);
            }
        }

        await _workspaceManager.RunHookBestEffortAsync(config.Hooks.AfterRun, workspace.Path, config.Hooks.TimeoutMs, cancellationToken);
        return AgentRunResult.CreateSuccess();
    }
}

public sealed record AgentRunResult(bool Success, string? Reason, Exception? Exception)
{
    public static AgentRunResult CreateSuccess() => new(true, null, null);
    public static AgentRunResult Fail(string reason, Exception? ex = null) => new(false, reason, ex);
}



