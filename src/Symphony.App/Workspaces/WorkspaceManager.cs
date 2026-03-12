using Microsoft.Extensions.Logging;
using Symphony.App.Config;
using Symphony.App.Domain;
using Symphony.App.Utils;

namespace Symphony.App.Workspaces;

public sealed class WorkspaceManager
{
    private readonly ServiceConfigProvider _configProvider;
    private readonly ShellCommandRunner _shell;
    private readonly ILogger<WorkspaceManager> _logger;

    public WorkspaceManager(ServiceConfigProvider configProvider, ShellCommandRunner shell, ILogger<WorkspaceManager> logger)
    {
        _configProvider = configProvider;
        _shell = shell;
        _logger = logger;
    }

    public async Task<Workspace> CreateForIssueAsync(string identifier, CancellationToken cancellationToken)
    {
        var config = _configProvider.GetConfig();
        var root = config.Workspace.Root;
        Directory.CreateDirectory(root);

        var workspaceKey = SanitizeKey(identifier);
        var path = Path.Combine(root, workspaceKey);
        var fullPath = Path.GetFullPath(path);
        var rootFullPath = Path.GetFullPath(root);

        if (!fullPath.StartsWith(rootFullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Workspace path escapes configured root.");
        }

        var createdNow = false;
        if (File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            throw new IOException($"Workspace path exists and is not a directory: {fullPath}");
        }

        if (!Directory.Exists(fullPath))
        {
            Directory.CreateDirectory(fullPath);
            createdNow = true;
        }

        CleanupTempArtifacts(fullPath);

        if (createdNow && !string.IsNullOrWhiteSpace(config.Hooks.AfterCreate))
        {
            var result = await _shell.RunAsync(config.Hooks.AfterCreate, fullPath, config.Hooks.TimeoutMs, cancellationToken);
            if (!result.Success)
            {
                throw new InvalidOperationException("after_create hook failed.");
            }
        }

        return new Workspace(fullPath, workspaceKey, createdNow);
    }

    public async Task<bool> RunHookAsync(string? hook, string workspacePath, int timeoutMs, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(hook))
        {
            return true;
        }

        var result = await _shell.RunAsync(hook, workspacePath, timeoutMs, cancellationToken);
        return result.Success;
    }

    public async Task RunHookBestEffortAsync(string? hook, string workspacePath, int timeoutMs, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(hook))
        {
            return;
        }

        var result = await _shell.RunAsync(hook, workspacePath, timeoutMs, cancellationToken);
        if (!result.Success)
        {
            _logger.LogWarning("Hook failed (ignored): {Hook}", hook);
        }
    }

    public async Task CleanupWorkspaceAsync(string identifier, CancellationToken cancellationToken)
    {
        var config = _configProvider.GetConfig();
        var root = config.Workspace.Root;
        var workspaceKey = SanitizeKey(identifier);
        var path = Path.Combine(root, workspaceKey);
        if (!Directory.Exists(path))
        {
            return;
        }

        await RunHookBestEffortAsync(config.Hooks.BeforeRemove, path, config.Hooks.TimeoutMs, cancellationToken);
        try
        {
            Directory.Delete(path, true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove workspace {Path}", path);
        }
    }

    private static void CleanupTempArtifacts(string path)
    {
        var tmp = Path.Combine(path, "tmp");
        if (Directory.Exists(tmp))
        {
            Directory.Delete(tmp, true);
        }

        var elixir = Path.Combine(path, ".elixir_ls");
        if (Directory.Exists(elixir))
        {
            Directory.Delete(elixir, true);
        }
    }

    public static string SanitizeKey(string identifier)
    {
        var chars = identifier.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (!(char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-'))
            {
                chars[i] = '_';
            }
        }

        return new string(chars);
    }
}
