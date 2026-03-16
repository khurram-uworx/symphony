using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Symphony.App;
using Symphony.App.Agent;
using Symphony.App.Agent.Codex;
using Symphony.App.Config;
using Symphony.App.IssueTracker;
using Symphony.App.Orchestration;
using Symphony.App.Workflows;
using Symphony.App.Workspaces;

try
{
    var workflowPath = WorkflowPathResolver.Resolve(args);

    using var host = Host.CreateDefaultBuilder(args)
        .ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
            });
        })
        .ConfigureServices(services =>
        {
            services.AddSingleton(new WorkflowPath(workflowPath));
            services.AddSingleton<WorkflowLoader>();
            services.AddSingleton<WorkflowManager>();
            services.AddSingleton<ServiceConfigProvider>();
            services.AddSingleton<Symphony.App.Utils.ShellCommandRunner>();
            services.AddSingleton<WorkspaceManager>();
            services.AddSingleton<IIssueTracker>(provider =>
            {
                var configProvider = provider.GetRequiredService<ServiceConfigProvider>();
                var workflowManager = provider.GetRequiredService<WorkflowManager>();
                workflowManager.StartWatching();
                var config = configProvider.GetConfig();

                if (string.Equals(config.Tracker.Kind, "jira", StringComparison.OrdinalIgnoreCase))
                {
                    var logger = provider.GetRequiredService<ILogger<JiraClient>>();
                    return new JiraClient(logger, config);
                }
                else
                {
                    var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
                    var logger = provider.GetRequiredService<ILogger<LinearClient>>();
                    return new LinearClient(httpClientFactory, logger, config);
                }
            });
            services.AddSingleton<ICodingAgent>(provider =>
            {
                var configProvider = provider.GetRequiredService<ServiceConfigProvider>();
                var config = configProvider.GetConfig();

                // Use CodexAgent if codex section exists, otherwise use CopilotAgent
                if (config.Codex is not null)
                {
                    var logger = provider.GetRequiredService<ILogger<CodexAgent>>();
                    return new CodexAgent(logger, config);
                }
                else
                {
                    var logger = provider.GetRequiredService<ILogger<CopilotAgent>>();
                    return new CopilotAgent(logger, config);
                }
            });
            services.AddSingleton<AgentRunner>();
            services.AddHostedService<OrchestratorService>();
            services.AddHttpClient();
        })
        .Build();

    await host.RunAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Startup failed: {ex.Message}");
    Environment.ExitCode = 1;
}
