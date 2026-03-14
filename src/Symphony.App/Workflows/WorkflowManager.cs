using Microsoft.Extensions.Logging;

namespace Symphony.App.Workflows;

class WorkflowManager : IDisposable
{
    readonly WorkflowLoader loader;
    readonly ILogger<WorkflowManager> logger;
    readonly WorkflowPath path;
    FileSystemWatcher? watcher;
    WorkflowDefinition? current;
    Exception? lastError;

    public WorkflowManager(WorkflowLoader loader, ILogger<WorkflowManager> logger, WorkflowPath path)
    {
        this.loader = loader;
        this.logger = logger;
        this.path = path;
    }

    public WorkflowDefinition Current => current ?? throw new InvalidOperationException("Workflow not loaded.");

    public Exception? LastError => lastError;

    void loadInitial()
    {
        current = loader.Load(path.Value);
        lastError = null;
        logger.LogInformation("Loaded workflow from {Path}", path.Value);
    }

    public void StartWatching()
    {
        loadInitial();

        var directory = Path.GetDirectoryName(path.Value) ?? Environment.CurrentDirectory;
        var fileName = Path.GetFileName(path.Value);
        watcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
        };

        watcher.Changed += (_, _) => Reload();
        watcher.Created += (_, _) => Reload();
        watcher.Renamed += (_, _) => Reload();
        watcher.EnableRaisingEvents = true;
    }

    public void Reload()
    {
        try
        {
            var definition = loader.Load(path.Value);
            current = definition;
            lastError = null;
            logger.LogInformation("Reloaded workflow from {Path}", path.Value);
        }
        catch (Exception ex)
        {
            lastError = ex;
            logger.LogError(ex, "Failed to reload workflow; keeping last known good configuration.");
        }
    }

    public void Dispose()
    {
        watcher?.Dispose();
    }
}
