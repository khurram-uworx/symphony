using Symphony.App.Domain;

namespace Symphony.App.Orchestration;

class LiveSession
{
    public string? SessionId { get; set; }
    public string? ThreadId { get; set; }
    public string? TurnId { get; set; }
    public string? CodexAppServerPid { get; set; }
    public string? LastCodexEvent { get; set; }
    public DateTimeOffset? LastCodexTimestamp { get; set; }
    public string? LastCodexMessage { get; set; }
    public int CodexInputTokens { get; set; }
    public int CodexOutputTokens { get; set; }
    public int CodexTotalTokens { get; set; }
    public int LastReportedInputTokens { get; set; }
    public int LastReportedOutputTokens { get; set; }
    public int LastReportedTotalTokens { get; set; }
    public int TurnCount { get; set; }
}

class RunningEntry
{
    public required Issue Issue { get; set; }
    public required string Identifier { get; init; }
    public required int? RetryAttempt { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required CancellationTokenSource Cancellation { get; init; }
    public required Task WorkerTask { get; init; }
    public LiveSession Session { get; } = new();
}

class RetryEntry
{
    public required string IssueId { get; init; }
    public required string Identifier { get; init; }
    public required int Attempt { get; init; }
    public required long DueAtMs { get; init; }
    public required Timer TimerHandle { get; init; }
    public string? Error { get; init; }
}

class OrchestratorState
{
    public int PollIntervalMs { get; set; }
    public int MaxConcurrentAgents { get; set; }
    public Dictionary<string, RunningEntry> Running { get; } = new();
    public HashSet<string> Claimed { get; } = new();
    public Dictionary<string, RetryEntry> RetryAttempts { get; } = new();
    public HashSet<string> Completed { get; } = new();
    public CodexTotals CodexTotals { get; } = new();
    public CodexRateLimits? RateLimits { get; set; }
}

class CodexTotals
{
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int TotalTokens { get; set; }
    public double SecondsRunning { get; set; }
}

class CodexRateLimits
{
    public int? RequestsRemaining { get; set; }
    public int? TokensRemaining { get; set; }
    public DateTimeOffset? ResetAt { get; set; }
}
