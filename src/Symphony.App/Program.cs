using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Symphony.App;
using Symphony.App.Agent;
using Symphony.App.Config;
using Symphony.App.Linear;
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
            services.AddSingleton<LinearClient>();
            services.AddSingleton<AppServerClientFactory>();
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
