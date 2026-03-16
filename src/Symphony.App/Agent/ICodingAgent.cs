using Symphony.App.Orchestration;
using System.Text.Json;

namespace Symphony.App.Agent;

record CodingAgentEvent(string EventName, string? Summary, DateTimeOffset Timestamp, JsonElement Payload);

class CodingAgentException : Exception
{
    public CodingAgentException(string message)
        : base(message) { }
    public CodingAgentException(string message, Exception innerException)
        : base(message, innerException) { }
}

interface ICodingAgent
{
    Task InitializeAsync(string workspacePath, LiveSession liveSession,
        Action<CodingAgentEvent> onEvent, CancellationToken cancellationToken);
    Task<string?> ExecuteTurnAsync(string prompt, LiveSession liveSession,
        CancellationToken cancellationToken);
    Task ShutdownAsync(CancellationToken cancellationToken);
}
