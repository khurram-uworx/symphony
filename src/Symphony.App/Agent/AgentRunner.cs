using Microsoft.Extensions.Logging;
using Symphony.App.Config;
using Symphony.App.Domain;
using Symphony.App.Linear;
using Symphony.App.Orchestration;
using Symphony.App.Workflows;
using Symphony.App.Workspaces;

namespace Symphony.App.Agent;

record AgentRunResult(bool Success, string? Reason, Exception? Exception)
{
    public static AgentRunResult CreateSuccess() => new(true, null, null);
    public static AgentRunResult Fail(string reason, Exception? ex = null) => new(false, reason, ex);
}

class AgentRunner
{
    readonly ServiceConfigProvider configProvider;
    readonly WorkspaceManager workspaceManager;
    readonly AppServerClientFactory clientFactory;
    readonly LinearClient linearClient;
    readonly ILogger<AgentRunner> logger;
    readonly PromptRenderer promptRenderer;

    public AgentRunner(
        ServiceConfigProvider configProvider,
        WorkspaceManager workspaceManager,
        AppServerClientFactory clientFactory,
        LinearClient linearClient,
        ILogger<AgentRunner> logger)
    {
        this.configProvider = configProvider;
        this.workspaceManager = workspaceManager;
        this.clientFactory = clientFactory;
        this.linearClient = linearClient;
        this.logger = logger;
        promptRenderer = new PromptRenderer();
    }

    public async Task<AgentRunResult> RunAttemptAsync(Issue issue, int? attempt, LiveSession liveSession, Action<CodexEvent> onEvent, CancellationToken cancellationToken)
    {
        var config = configProvider.GetConfig();
        using var scope = logger.BeginScope(new Dictionary<string, object> { { "issue_id", issue.Id }, { "issue_identifier", issue.Identifier } });
        Workspace workspace;
        try
        {
            workspace = await workspaceManager.CreateForIssueAsync(issue.Identifier, cancellationToken);
        }
        catch (Exception ex)
        {
            return AgentRunResult.Fail("workspace error", ex);
        }

        if (!await workspaceManager.RunHookAsync(config.Hooks.BeforeRun, workspace.Path, config.Hooks.TimeoutMs, cancellationToken))
        {
            return AgentRunResult.Fail("before_run hook error");
        }

        var client = clientFactory.Create();
        AppServerSession? session = null;
        try
        {
            session = await client.StartSessionAsync(config, workspace.Path, liveSession, onEvent, cancellationToken);
        }
        catch (Exception ex)
        {
            await workspaceManager.RunHookBestEffortAsync(config.Hooks.AfterRun, workspace.Path, config.Hooks.TimeoutMs, cancellationToken);
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
                    : promptRenderer.Render(config.Workflow.PromptTemplate, issue, attempt);

                await client.RunTurnAsync(session, config, prompt, liveSession, cancellationToken);
                liveSession.TurnCount++;

                var refreshed = await linearClient.FetchIssueStatesByIdsAsync(config, new[] { issue.Id }, cancellationToken);
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
            await workspaceManager.RunHookBestEffortAsync(config.Hooks.AfterRun, workspace.Path, config.Hooks.TimeoutMs, cancellationToken);
            return AgentRunResult.Fail("prompt error", ex);
        }
        catch (Exception ex)
        {
            await workspaceManager.RunHookBestEffortAsync(config.Hooks.AfterRun, workspace.Path, config.Hooks.TimeoutMs, cancellationToken);
            return AgentRunResult.Fail("agent turn error", ex);
        }
        finally
        {
            if (session is not null)
            {
                await client.StopSessionAsync(session, cancellationToken);
            }
        }

        await workspaceManager.RunHookBestEffortAsync(config.Hooks.AfterRun, workspace.Path, config.Hooks.TimeoutMs, cancellationToken);
        return AgentRunResult.CreateSuccess();
    }
}
