using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Symphony.App.Agent;
using Symphony.App.Config;
using Symphony.App.Domain;
using Symphony.App.IssueTracker;
using Symphony.App.Workflows;
using Symphony.App.Workspaces;

namespace Symphony.App.Orchestration;

class OrchestratorService : BackgroundService
{
    static readonly TimeSpan ContinuationDelay = TimeSpan.FromSeconds(5);
    static readonly TimeSpan RetryBaseDelay = TimeSpan.FromSeconds(10);

    static TimeSpan calculateRetryDelay(int attempt, int maxBackoffMs)
    {
        var delay = TimeSpan.FromMilliseconds(RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
        if (delay.TotalMilliseconds > maxBackoffMs)
        {
            delay = TimeSpan.FromMilliseconds(maxBackoffMs);
        }

        return delay;
    }

    static List<Issue> sortForDispatch(IEnumerable<Issue> issues)
    {
        return issues
            .OrderBy(issue => issue.Priority ?? int.MaxValue)
            .ThenBy(issue => issue.CreatedAt ?? DateTimeOffset.MaxValue)
            .ToList();
    }

    readonly WorkflowManager workflowManager;
    readonly ServiceConfigProvider configProvider;
    readonly LinearClient linearClient;
    readonly WorkspaceManager workspaceManager;
    readonly AgentRunner agentRunner;
    readonly ILogger<OrchestratorService> logger;

    readonly object theLock = new();
    readonly OrchestratorState state = new();

    public OrchestratorService(
        WorkflowManager workflowManager,
        ServiceConfigProvider configProvider,
        LinearClient linearClient,
        WorkspaceManager workspaceManager,
        AgentRunner agentRunner,
        ILogger<OrchestratorService> logger)
    {
        this.workflowManager = workflowManager;
        this.configProvider = configProvider;
        this.linearClient = linearClient;
        this.workspaceManager = workspaceManager;
        this.agentRunner = agentRunner;
        this.logger = logger;
    }

    async Task tickAsync(CancellationToken cancellationToken)
    {
        try
        {
            await reconcileRunningIssuesAsync(cancellationToken);

            var config = configProvider.GetConfig();
            var validation = configProvider.ValidateForDispatch(config);
            if (!validation.IsOk)
            {
                configProvider.LogValidationFailure(validation);
                return;
            }

            updateDynamicConfig(config);

            var issues = await linearClient.FetchCandidateIssuesAsync(cancellationToken);
            var sorted = sortForDispatch(issues);
            foreach (var issue in sorted)
            {
                if (!hasAvailableSlots(config, issue))
                    break;

                if (shouldDispatch(config, issue))
                    dispatchIssue(config, issue, null, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Tick failed.");
        }
    }

    async Task reconcileRunningIssuesAsync(CancellationToken cancellationToken)
    {
        await reconcileStalledRunsAsync(cancellationToken);

        List<string> runningIds;
        lock (theLock)
        {
            runningIds = state.Running.Keys.ToList();
        }

        if (runningIds.Count == 0)
            return;

        ServiceConfig config;
        try
        {
            config = configProvider.GetConfig();
        }
        catch
        {
            return;
        }

        IReadOnlyList<Issue> refreshed;
        try
        {
            refreshed = await linearClient.FetchIssueStatesByIdsAsync(runningIds, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to refresh running issues; keeping workers running.");
            return;
        }

        var refreshedById = refreshed.ToDictionary(issue => issue.Id, issue => issue);

        foreach (var id in runningIds)
        {
            if (!refreshedById.TryGetValue(id, out var issue))
                continue;

            if (config.TerminalStates.Contains(issue.NormalizedState))
                await terminateRunningIssueAsync(id, cleanupWorkspace: true, cancellationToken);
            else if (config.ActiveStates.Contains(issue.NormalizedState))
            {
                lock (theLock)
                {
                    if (state.Running.TryGetValue(id, out var running))
                        running.Issue = issue;
                }
            }
            else
                await terminateRunningIssueAsync(id, cleanupWorkspace: false, cancellationToken);
        }
    }

    async Task reconcileStalledRunsAsync(CancellationToken cancellationToken)
    {
        var config = configProvider.GetConfig();

        if (config.Codex is null || config.Codex.StallTimeoutMs <= 0)
            return;

        var now = DateTimeOffset.UtcNow;
        List<string> stalled = new();
        lock (theLock)
        {
            foreach (var (id, entry) in state.Running)
            {
                var last = entry.Session.LastCodexTimestamp ?? entry.StartedAt;

                if (now - last > TimeSpan.FromMilliseconds(config.Codex.StallTimeoutMs))
                    stalled.Add(id);
            }
        }

        foreach (var id in stalled)
        {
            logger.LogWarning("Stall detected for {issue_id}", id);
            await terminateRunningIssueAsync(id, cleanupWorkspace: false, cancellationToken);
        }
    }

    bool shouldDispatch(ServiceConfig config, Issue issue)
    {
        if (!config.ActiveStates.Contains(issue.NormalizedState))
            return false;

        lock (theLock)
        {
            if (state.Claimed.Contains(issue.Id))
                return false;
        }

        if (string.Equals(issue.NormalizedState, "todo", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var blocker in issue.BlockedBy)
            {
                var blockerState = blocker.State?.Trim().ToLowerInvariant();

                if (string.IsNullOrWhiteSpace(blockerState))
                    return false;

                if (!config.TerminalStates.Contains(blockerState))
                    return false;
            }
        }

        return true;
    }

    bool hasAvailableSlots(ServiceConfig config, Issue issue)
    {
        lock (theLock)
        {
            if (state.Running.Count >= state.MaxConcurrentAgents)
                return false;

            var normalized = issue.NormalizedState;
            if (config.Agent.MaxConcurrentByState.TryGetValue(normalized, out var limit))
            {
                var runningForState = state.Running.Values.Count(entry => entry.Issue.NormalizedState == normalized);
                return runningForState < limit;
            }
        }

        return true;
    }

    void dispatchIssue(ServiceConfig config, Issue issue, int? attempt, CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var liveSession = new LiveSession();

        var task = Task.Run(async () =>
        {
            var result = await agentRunner.RunAttemptAsync(issue, attempt, liveSession, evt =>
            {
                updateCodexMetrics(issue.Id, liveSession, evt);
                if (!string.IsNullOrWhiteSpace(liveSession.SessionId))
                    logger.LogDebug("codex_event {event_name} {issue_id} {issue_identifier} {session_id}", evt.EventName, issue.Id, issue.Identifier, liveSession.SessionId);

            }, cts.Token);

            await handleWorkerExitAsync(issue.Id, issue.Identifier, result, attempt, cancellationToken);
        }, cancellationToken);

        lock (theLock)
        {
            state.Running[issue.Id] = new RunningEntry
            {
                Issue = issue,
                Identifier = issue.Identifier,
                RetryAttempt = attempt,
                StartedAt = DateTimeOffset.UtcNow,
                Cancellation = cts,
                WorkerTask = task
            };
            state.Claimed.Add(issue.Id);
            state.RetryAttempts.Remove(issue.Id);
        }

        logger.LogInformation("Dispatched {issue_identifier} ({issue_id}) attempt {attempt}", issue.Identifier, issue.Id, attempt ?? 0);
    }

    async Task handleWorkerExitAsync(string issueId, string identifier, AgentRunResult result, int? attempt, CancellationToken cancellationToken)
    {
        RunningEntry? running;
        lock (theLock)
        {
            state.Running.Remove(issueId, out running);
        }

        if (running is not null)
        {
            var elapsed = DateTimeOffset.UtcNow - running.StartedAt;
            lock (theLock)
            {
                state.CodexTotals.SecondsRunning += elapsed.TotalSeconds;
            }
        }

        if (result.Success)
        {
            lock (theLock)
            {
                state.Completed.Add(issueId);
            }

            scheduleRetry(issueId, identifier, 1, ContinuationDelay, "continuation", null, cancellationToken);
            return;
        }

        var nextAttempt = (attempt ?? 0) + 1;
        var delay = calculateRetryDelay(nextAttempt, configProvider.GetConfig().Agent.MaxRetryBackoffMs);
        scheduleRetry(issueId, identifier, nextAttempt, delay, "worker failure", result.Reason, cancellationToken);
    }

    void scheduleRetry(string issueId, string identifier, int attempt, TimeSpan delay, string delayType, string? error, CancellationToken cancellationToken)
    {
        var dueAt = Environment.TickCount64 + (long)delay.TotalMilliseconds;
        Timer? timer = null;
        timer = new Timer(async _ =>
        {
            timer?.Dispose();
            await onRetryTimerAsync(issueId, cancellationToken);

        }, null, delay, Timeout.InfiniteTimeSpan);

        var entry = new RetryEntry
        {
            IssueId = issueId,
            Identifier = identifier,
            Attempt = attempt,
            DueAtMs = dueAt,
            TimerHandle = timer,
            Error = error
        };

        lock (theLock)
        {
            state.RetryAttempts[issueId] = entry;
        }

        logger.LogInformation("Scheduled retry for {issue_identifier} ({issue_id}) in {delay_ms}ms ({delay_type})", identifier, issueId, delay.TotalMilliseconds, delayType);
    }

    async Task onRetryTimerAsync(string issueId, CancellationToken cancellationToken)
    {
        RetryEntry? retry;
        lock (theLock)
        {
            state.RetryAttempts.Remove(issueId, out retry);
        }

        if (retry is null)
            return;

        ServiceConfig config;
        try
        {
            config = configProvider.GetConfig();
        }
        catch
        {
            return;
        }

        IReadOnlyList<Issue> candidates;
        try
        {
            candidates = await linearClient.FetchCandidateIssuesAsync(cancellationToken);
        }
        catch
        {
            scheduleRetry(issueId, retry.Identifier, retry.Attempt + 1, calculateRetryDelay(retry.Attempt + 1, config.Agent.MaxRetryBackoffMs), "retry poll failed", "retry poll failed", cancellationToken);
            return;
        }

        var issue = candidates.FirstOrDefault(candidate => candidate.Id == issueId);
        if (issue is null)
        {
            lock (theLock)
            {
                state.Claimed.Remove(issueId);
            }

            return;
        }

        if (!hasAvailableSlots(config, issue))
        {
            scheduleRetry(issueId, retry.Identifier, retry.Attempt + 1, calculateRetryDelay(retry.Attempt + 1, config.Agent.MaxRetryBackoffMs), "no slots", "no available orchestrator slots", cancellationToken);
            return;
        }

        dispatchIssue(config, issue, retry.Attempt, cancellationToken);
    }

    async Task terminateRunningIssueAsync(string issueId, bool cleanupWorkspace, CancellationToken cancellationToken)
    {
        RunningEntry? running;
        lock (theLock)
        {
            state.Running.Remove(issueId, out running);
        }

        if (running is null)
            return;

        running.Cancellation.Cancel();

        if (cleanupWorkspace)
            await workspaceManager.CleanupWorkspaceAsync(running.Identifier, cancellationToken);
    }

    void updateDynamicConfig(ServiceConfig config)
    {
        lock (theLock)
        {
            state.PollIntervalMs = config.Polling.IntervalMs;
            state.MaxConcurrentAgents = config.Agent.MaxConcurrentAgents;
        }
    }

    void updateCodexMetrics(string issueId, LiveSession session, CodingAgentEvent evt)
    {
        lock (theLock)
        {
            state.CodexTotals.InputTokens = Math.Max(state.CodexTotals.InputTokens, session.CodexInputTokens);
            state.CodexTotals.OutputTokens = Math.Max(state.CodexTotals.OutputTokens, session.CodexOutputTokens);
            state.CodexTotals.TotalTokens = Math.Max(state.CodexTotals.TotalTokens, session.CodexTotalTokens);
        }
    }

    async Task cleanupTerminalWorkspacesAsync(ServiceConfig config, CancellationToken cancellationToken)
    {
        try
        {
            var terminal = await linearClient.FetchTerminalIssuesAsync(cancellationToken);

            foreach (var issue in terminal)
                await workspaceManager.CleanupWorkspaceAsync(issue.Identifier, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to perform startup workspace cleanup.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await tickAsync(stoppingToken);

            int delayMs;
            lock (theLock)
            {
                delayMs = state.PollIntervalMs;
            }

            await Task.Delay(delayMs, stoppingToken);
        }
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        workflowManager.StartWatching();
        var config = configProvider.GetConfig();
        var validation = configProvider.ValidateForDispatch(config);
        if (!validation.IsOk)
        {
            configProvider.LogValidationFailure(validation);
            throw new InvalidOperationException(validation.ErrorMessage);
        }

        await cleanupTerminalWorkspacesAsync(config, cancellationToken);

        lock (theLock)
        {
            state.PollIntervalMs = config.Polling.IntervalMs;
            state.MaxConcurrentAgents = config.Agent.MaxConcurrentAgents;
        }

        await base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (theLock)
        {
            foreach (var retry in state.RetryAttempts.Values)
                retry.TimerHandle.Dispose();

            foreach (var entry in state.Running.Values)
                entry.Cancellation.Cancel();
        }

        await base.StopAsync(cancellationToken);
    }
}
