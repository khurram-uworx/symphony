using GitHub.Copilot.SDK;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Symphony.App.Config;
using Symphony.App.Orchestration;
using System.Text.Json;

namespace Symphony.App.Agent;

class CopilotAgent : ICodingAgent
{
    readonly ServiceConfig config;
    readonly ILogger<CopilotAgent> logger;
    CopilotClient? copilotClient;
    AIAgent? agent;

    public CopilotAgent(ILogger<CopilotAgent> logger, ServiceConfig config)
    {
        this.logger = logger;
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
        copilotClient.On(evt =>
        {
            onEvent(new CodingAgentEvent(evt.Type, evt.Metadata.ToString(), DateTimeOffset.Now, new JsonElement { }));
            this.logger.LogInformation("[Copilot:{Session}] {Type}: {Summary} [{Started}-{Modified}]",
                evt.SessionId, evt.Type,
                evt.Metadata.Summary, evt.Metadata.StartTime, evt.Metadata.ModifiedTime);
        });

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
        catch (InvalidOperationException ex)
        {
            //Session error: 402 You have no quota
            if (ex.Message.Contains("You have no quota"))
                throw new CodingAgentException("No remaining quota", ex);

            throw;
        }
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        if (copilotClient != null)
            await copilotClient.DisposeAsync();
    }
}
