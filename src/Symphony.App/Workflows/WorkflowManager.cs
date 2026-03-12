using Microsoft.Extensions.Logging;

namespace Symphony.App.Workflows;

public sealed class WorkflowManager : IDisposable
{
    private readonly WorkflowLoader _loader;
    private readonly ILogger<WorkflowManager> _logger;
    private readonly WorkflowPath _path;
    private FileSystemWatcher? _watcher;
    private WorkflowDefinition? _current;
    private Exception? _lastError;

    public WorkflowManager(WorkflowLoader loader, ILogger<WorkflowManager> logger, WorkflowPath path)
    {
        _loader = loader;
        _logger = logger;
        _path = path;
    }

    public WorkflowDefinition Current => _current ?? throw new InvalidOperationException("Workflow not loaded.");
    public Exception? LastError => _lastError;

    public void StartWatching()
    {
        LoadInitial();

        var directory = Path.GetDirectoryName(_path.Value) ?? Environment.CurrentDirectory;
        var fileName = Path.GetFileName(_path.Value);
        _watcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
        };

        _watcher.Changed += (_, _) => Reload();
        _watcher.Created += (_, _) => Reload();
        _watcher.Renamed += (_, _) => Reload();
        _watcher.EnableRaisingEvents = true;
    }

    public void Reload()
    {
        try
        {
            var definition = _loader.Load(_path.Value);
            _current = definition;
            _lastError = null;
            _logger.LogInformation("Reloaded workflow from {Path}", _path.Value);
        }
        catch (Exception ex)
        {
            _lastError = ex;
            _logger.LogError(ex, "Failed to reload workflow; keeping last known good configuration.");
        }
    }

    private void LoadInitial()
    {
        _current = _loader.Load(_path.Value);
        _lastError = null;
        _logger.LogInformation("Loaded workflow from {Path}", _path.Value);
    }

    public void Dispose()
    {
        _watcher?.Dispose();
    }
}
