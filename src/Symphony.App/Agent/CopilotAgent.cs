using GitHub.Copilot.SDK;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Symphony.App.Config;
using Symphony.App.Orchestration;

namespace Symphony.App.Agent;

class CopilotAgent : ICodingAgent
{
    readonly ServiceConfig config;
    CopilotClient? copilotClient;
    AIAgent? agent;

    public CopilotAgent(ServiceConfig config)
    {
        this.config = config;
    }

    public async Task InitializeAsync(string workspacePath, LiveSession liveSession, Action<CodingAgentEvent> onEvent, CancellationToken cancellationToken)
    {
        copilotClient = new CopilotClient();
        await copilotClient.StartAsync();

        var sessionConfig = new SessionConfig
        {
            OnPermissionRequest = (req, inv) => Task.FromResult(new PermissionRequestResult()
            {
                Kind = PermissionRequestResultKind.Approved
            }),
            McpServers = new Dictionary<string, object>
            {
                ["linear"] = new McpLocalServerConfig
                {
                    Type = "local",
                    Command = "npx",
                    Args = new List<string> { "-y", "mcp-remote", "https://mcp.linear.app/mcp" },
                    Tools = new List<string> { "*" },
                },
            },
            WorkingDirectory = workspacePath
        };

        agent = copilotClient.AsAIAgent(sessionConfig);
        liveSession.SessionId = Guid.NewGuid().ToString();
    }

    public async Task<string?> ExecuteTurnAsync(string prompt, LiveSession liveSession, CancellationToken cancellationToken)
    {
        if (agent == null)
            throw new InvalidOperationException("Agent not initialized. Call InitializeAsync first.");

        try
        {
            var response = await agent.RunAsync(prompt, cancellationToken: cancellationToken);
            liveSession.TurnCount++;
            return response.Text;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        if (copilotClient != null)
            await copilotClient.DisposeAsync();
    }
}
