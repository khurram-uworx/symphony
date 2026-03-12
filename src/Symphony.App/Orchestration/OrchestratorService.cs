using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Symphony.App.Agent;
using Symphony.App.Config;
using Symphony.App.Domain;
using Symphony.App.Linear;
using Symphony.App.Workflows;
using Symphony.App.Workspaces;

namespace Symphony.App.Orchestration;

public sealed class OrchestratorService : BackgroundService
{
    private static readonly TimeSpan ContinuationDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryBaseDelay = TimeSpan.FromSeconds(10);

    private readonly WorkflowManager _workflowManager;
    private readonly ServiceConfigProvider _configProvider;
    private readonly LinearClient _linearClient;
    private readonly WorkspaceManager _workspaceManager;
    private readonly AgentRunner _agentRunner;
    private readonly ILogger<OrchestratorService> _logger;

    private readonly object _lock = new();
    private readonly OrchestratorState _state = new();

    public OrchestratorService(
        WorkflowManager workflowManager,
        ServiceConfigProvider configProvider,
        LinearClient linearClient,
        WorkspaceManager workspaceManager,
        AgentRunner agentRunner,
        ILogger<OrchestratorService> logger)
    {
        _workflowManager = workflowManager;
        _configProvider = configProvider;
        _linearClient = linearClient;
        _workspaceManager = workspaceManager;
        _agentRunner = agentRunner;
        _logger = logger;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _workflowManager.StartWatching();
        var config = _configProvider.GetConfig();
        var validation = _configProvider.ValidateForDispatch(config);
        if (!validation.IsOk)
        {
            _configProvider.LogValidationFailure(validation);
            throw new InvalidOperationException(validation.ErrorMessage);
        }

        await CleanupTerminalWorkspacesAsync(config, cancellationToken);

        lock (_lock)
        {
            _state.PollIntervalMs = config.Polling.IntervalMs;
            _state.MaxConcurrentAgents = config.Agent.MaxConcurrentAgents;
        }

        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await TickAsync(stoppingToken);

            int delayMs;
            lock (_lock)
            {
                delayMs = _state.PollIntervalMs;
            }

            await Task.Delay(delayMs, stoppingToken);
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ReconcileRunningIssuesAsync(cancellationToken);

            var config = _configProvider.GetConfig();
            var validation = _configProvider.ValidateForDispatch(config);
            if (!validation.IsOk)
            {
                _configProvider.LogValidationFailure(validation);
                return;
            }

            UpdateDynamicConfig(config);

            var issues = await _linearClient.FetchCandidateIssuesAsync(config, cancellationToken);
            var sorted = SortForDispatch(issues);
            foreach (var issue in sorted)
            {
                if (!HasAvailableSlots(config, issue))
                {
                    break;
                }

                if (ShouldDispatch(config, issue))
                {
                    DispatchIssue(config, issue, null, cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tick failed.");
        }
    }

    private async Task ReconcileRunningIssuesAsync(CancellationToken cancellationToken)
    {
        await ReconcileStalledRunsAsync(cancellationToken);

        List<string> runningIds;
        lock (_lock)
        {
            runningIds = _state.Running.Keys.ToList();
        }

        if (runningIds.Count == 0)
        {
            return;
        }

        ServiceConfig config;
        try
        {
            config = _configProvider.GetConfig();
        }
        catch
        {
            return;
        }

        IReadOnlyList<Issue> refreshed;
        try
        {
            refreshed = await _linearClient.FetchIssueStatesByIdsAsync(config, runningIds, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to refresh running issues; keeping workers running.");
            return;
        }

        var refreshedById = refreshed.ToDictionary(issue => issue.Id, issue => issue);

        foreach (var id in runningIds)
        {
            if (!refreshedById.TryGetValue(id, out var issue))
            {
                continue;
            }

            if (config.TerminalStates.Contains(issue.NormalizedState))
            {
                await TerminateRunningIssueAsync(id, cleanupWorkspace: true, cancellationToken);
            }
            else if (config.ActiveStates.Contains(issue.NormalizedState))
            {
                lock (_lock)
                {
                    if (_state.Running.TryGetValue(id, out var running))
                    {
                        running.Issue = issue;
                    }
                }
            }
            else
            {
                await TerminateRunningIssueAsync(id, cleanupWorkspace: false, cancellationToken);
            }
        }
    }

    private async Task ReconcileStalledRunsAsync(CancellationToken cancellationToken)
    {
        var config = _configProvider.GetConfig();
        if (config.Codex.StallTimeoutMs <= 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        List<string> stalled = new();
        lock (_lock)
        {
            foreach (var (id, entry) in _state.Running)
            {
                var last = entry.Session.LastCodexTimestamp ?? entry.StartedAt;
                if (now - last > TimeSpan.FromMilliseconds(config.Codex.StallTimeoutMs))
                {
                    stalled.Add(id);
                }
            }
        }

        foreach (var id in stalled)
        {
            _logger.LogWarning("Stall detected for {issue_id}", id);
            await TerminateRunningIssueAsync(id, cleanupWorkspace: false, cancellationToken);
        }
    }

    private bool ShouldDispatch(ServiceConfig config, Issue issue)
    {
        if (!config.ActiveStates.Contains(issue.NormalizedState))
        {
            return false;
        }

        lock (_lock)
        {
            if (_state.Claimed.Contains(issue.Id))
            {
                return false;
            }
        }

        if (string.Equals(issue.NormalizedState, "todo", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var blocker in issue.BlockedBy)
            {
                var blockerState = blocker.State?.Trim().ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(blockerState))
                {
                    return false;
                }

                if (!config.TerminalStates.Contains(blockerState))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private bool HasAvailableSlots(ServiceConfig config, Issue issue)
    {
        lock (_lock)
        {
            if (_state.Running.Count >= _state.MaxConcurrentAgents)
            {
                return false;
            }

            var normalized = issue.NormalizedState;
            if (config.Agent.MaxConcurrentByState.TryGetValue(normalized, out var limit))
            {
                var runningForState = _state.Running.Values.Count(entry => entry.Issue.NormalizedState == normalized);
                return runningForState < limit;
            }
        }

        return true;
    }

    private void DispatchIssue(ServiceConfig config, Issue issue, int? attempt, CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var liveSession = new LiveSession();

        var task = Task.Run(async () =>
        {
            var result = await _agentRunner.RunAttemptAsync(issue, attempt, liveSession, evt =>
            {
                UpdateCodexMetrics(issue.Id, liveSession, evt);
                if (!string.IsNullOrWhiteSpace(liveSession.SessionId))
                {
                    _logger.LogDebug("codex_event {event_name} {issue_id} {issue_identifier} {session_id}", evt.EventName, issue.Id, issue.Identifier, liveSession.SessionId);
                }
            }, cts.Token);

            await HandleWorkerExitAsync(issue.Id, issue.Identifier, result, attempt, cancellationToken);
        }, cancellationToken);

        lock (_lock)
        {
            _state.Running[issue.Id] = new RunningEntry
            {
                Issue = issue,
                Identifier = issue.Identifier,
                RetryAttempt = attempt,
                StartedAt = DateTimeOffset.UtcNow,
                Cancellation = cts,
                WorkerTask = task
            };
            _state.Claimed.Add(issue.Id);
            _state.RetryAttempts.Remove(issue.Id);
        }

        _logger.LogInformation("Dispatched {issue_identifier} ({issue_id}) attempt {attempt}", issue.Identifier, issue.Id, attempt ?? 0);
    }

    private async Task HandleWorkerExitAsync(string issueId, string identifier, AgentRunResult result, int? attempt, CancellationToken cancellationToken)
    {
        RunningEntry? running;
        lock (_lock)
        {
            _state.Running.Remove(issueId, out running);
        }

        if (running is not null)
        {
            var elapsed = DateTimeOffset.UtcNow - running.StartedAt;
            lock (_lock)
            {
                _state.CodexTotals.SecondsRunning += elapsed.TotalSeconds;
            }
        }

        if (result.Success)
        {
            lock (_lock)
            {
                _state.Completed.Add(issueId);
            }

            ScheduleRetry(issueId, identifier, 1, ContinuationDelay, "continuation", null, cancellationToken);
            return;
        }

        var nextAttempt = (attempt ?? 0) + 1;
        var delay = CalculateRetryDelay(nextAttempt, _configProvider.GetConfig().Agent.MaxRetryBackoffMs);
        ScheduleRetry(issueId, identifier, nextAttempt, delay, "worker failure", result.Reason, cancellationToken);
    }

    private void ScheduleRetry(string issueId, string identifier, int attempt, TimeSpan delay, string delayType, string? error, CancellationToken cancellationToken)
    {
        var dueAt = Environment.TickCount64 + (long)delay.TotalMilliseconds;
        Timer? timer = null;
        timer = new Timer(async _ =>
        {
            timer?.Dispose();
            await OnRetryTimerAsync(issueId, cancellationToken);
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

        lock (_lock)
        {
            _state.RetryAttempts[issueId] = entry;
        }

        _logger.LogInformation("Scheduled retry for {issue_identifier} ({issue_id}) in {delay_ms}ms ({delay_type})", identifier, issueId, delay.TotalMilliseconds, delayType);
    }

    private async Task OnRetryTimerAsync(string issueId, CancellationToken cancellationToken)
    {
        RetryEntry? retry;
        lock (_lock)
        {
            _state.RetryAttempts.Remove(issueId, out retry);
        }

        if (retry is null)
        {
            return;
        }

        ServiceConfig config;
        try
        {
            config = _configProvider.GetConfig();
        }
        catch
        {
            return;
        }

        IReadOnlyList<Issue> candidates;
        try
        {
            candidates = await _linearClient.FetchCandidateIssuesAsync(config, cancellationToken);
        }
        catch
        {
            ScheduleRetry(issueId, retry.Identifier, retry.Attempt + 1, CalculateRetryDelay(retry.Attempt + 1, config.Agent.MaxRetryBackoffMs), "retry poll failed", "retry poll failed", cancellationToken);
            return;
        }

        var issue = candidates.FirstOrDefault(candidate => candidate.Id == issueId);
        if (issue is null)
        {
            lock (_lock)
            {
                _state.Claimed.Remove(issueId);
            }

            return;
        }

        if (!HasAvailableSlots(config, issue))
        {
            ScheduleRetry(issueId, retry.Identifier, retry.Attempt + 1, CalculateRetryDelay(retry.Attempt + 1, config.Agent.MaxRetryBackoffMs), "no slots", "no available orchestrator slots", cancellationToken);
            return;
        }

        DispatchIssue(config, issue, retry.Attempt, cancellationToken);
    }

    private async Task TerminateRunningIssueAsync(string issueId, bool cleanupWorkspace, CancellationToken cancellationToken)
    {
        RunningEntry? running;
        lock (_lock)
        {
            _state.Running.Remove(issueId, out running);
        }

        if (running is null)
        {
            return;
        }

        running.Cancellation.Cancel();

        if (cleanupWorkspace)
        {
            await _workspaceManager.CleanupWorkspaceAsync(running.Identifier, cancellationToken);
        }
    }

    private static TimeSpan CalculateRetryDelay(int attempt, int maxBackoffMs)
    {
        var delay = TimeSpan.FromMilliseconds(RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
        if (delay.TotalMilliseconds > maxBackoffMs)
        {
            delay = TimeSpan.FromMilliseconds(maxBackoffMs);
        }

        return delay;
    }

    private void UpdateDynamicConfig(ServiceConfig config)
    {
        lock (_lock)
        {
            _state.PollIntervalMs = config.Polling.IntervalMs;
            _state.MaxConcurrentAgents = config.Agent.MaxConcurrentAgents;
        }
    }

    private void UpdateCodexMetrics(string issueId, LiveSession session, CodexEvent evt)
    {
        lock (_lock)
        {
            _state.CodexTotals.InputTokens = Math.Max(_state.CodexTotals.InputTokens, session.CodexInputTokens);
            _state.CodexTotals.OutputTokens = Math.Max(_state.CodexTotals.OutputTokens, session.CodexOutputTokens);
            _state.CodexTotals.TotalTokens = Math.Max(_state.CodexTotals.TotalTokens, session.CodexTotalTokens);
        }
    }

    private static List<Issue> SortForDispatch(IEnumerable<Issue> issues)
    {
        return issues
            .OrderBy(issue => issue.Priority ?? int.MaxValue)
            .ThenBy(issue => issue.CreatedAt ?? DateTimeOffset.MaxValue)
            .ToList();
    }

    private async Task CleanupTerminalWorkspacesAsync(ServiceConfig config, CancellationToken cancellationToken)
    {
        try
        {
            var terminal = await _linearClient.FetchTerminalIssuesAsync(config, cancellationToken);
            foreach (var issue in terminal)
            {
                await _workspaceManager.CleanupWorkspaceAsync(issue.Identifier, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to perform startup workspace cleanup.");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            foreach (var retry in _state.RetryAttempts.Values)
            {
                retry.TimerHandle.Dispose();
            }

            foreach (var entry in _state.Running.Values)
            {
                entry.Cancellation.Cancel();
            }
        }

        await base.StopAsync(cancellationToken);
    }
}











