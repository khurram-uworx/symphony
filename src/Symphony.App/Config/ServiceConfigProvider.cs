using Microsoft.Extensions.Logging;
using Symphony.App.Workflows;

namespace Symphony.App.Config;

record ServiceConfig(
    WorkflowDefinition Workflow,
    TrackerConfig Tracker,
    PollingConfig Polling,
    WorkspaceConfig Workspace,
    HooksConfig Hooks,
    AgentConfig Agent,
    CodexConfig Codex)
{
    public HashSet<string> ActiveStates => Tracker.ActiveStates;
    public HashSet<string> TerminalStates => Tracker.TerminalStates;
}

record TrackerConfig(
    string Kind,
    string Endpoint,
    string ApiKey,
    string ProjectSlug,
    HashSet<string> ActiveStates,
    HashSet<string> TerminalStates)
{
    static HashSet<string> normalizeStates(IEnumerable<string> states)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var state in states)
        {
            if (!string.IsNullOrWhiteSpace(state))
            {
                set.Add(state.Trim().ToLowerInvariant());
            }
        }

        return set;
    }

    public static TrackerConfig From(IReadOnlyDictionary<string, object> root)
    {
        var tracker = ConfigReader.ReadMap(root, "tracker");
        var kind = ConfigReader.ReadString(tracker, "kind") ?? string.Empty;
        var endpoint = ConfigReader.ReadString(tracker, "endpoint") ?? "https://api.linear.app/graphql";
        var apiKeyRaw = ConfigReader.ReadString(tracker, "api_key")
            ?? Environment.GetEnvironmentVariable("LINEAR_API_KEY")
            ?? string.Empty;
        var apiKey = ConfigReader.ResolveEnvValue(apiKeyRaw);
        var projectSlug = ConfigReader.ReadString(tracker, "project_slug") ?? string.Empty;
        var activeStates = ConfigReader.ReadStringList(tracker, "active_states")
            ?? new List<string> { "Todo", "In Progress" };
        var terminalStates = ConfigReader.ReadStringList(tracker, "terminal_states")
            ?? new List<string> { "Closed", "Cancelled", "Canceled", "Duplicate", "Done" };

        return new TrackerConfig(
            kind,
            endpoint,
            apiKey,
            projectSlug,
            normalizeStates(activeStates),
            normalizeStates(terminalStates));
    }
}

record PollingConfig(int IntervalMs)
{
    public static PollingConfig From(IReadOnlyDictionary<string, object> root)
    {
        var polling = ConfigReader.ReadMap(root, "polling");
        var interval = ConfigReader.ReadInt(polling, "interval_ms") ?? 30000;
        if (interval <= 0)
        {
            interval = 30000;
        }

        return new PollingConfig(interval);
    }
}

record WorkspaceConfig(string Root)
{
    public static WorkspaceConfig From(IReadOnlyDictionary<string, object> root)
    {
        var workspace = ConfigReader.ReadMap(root, "workspace");
        var rawRoot = ConfigReader.ReadString(workspace, "root") ?? Path.Combine(Path.GetTempPath(), "symphony_workspaces");
        var expanded = ConfigReader.ExpandPath(rawRoot);
        return new WorkspaceConfig(expanded);
    }
}

record HooksConfig(
    string? AfterCreate,
    string? BeforeRun,
    string? AfterRun,
    string? BeforeRemove,
    int TimeoutMs)
{
    public static HooksConfig From(IReadOnlyDictionary<string, object> root)
    {
        var hooks = ConfigReader.ReadMap(root, "hooks");
        var timeout = ConfigReader.ReadInt(hooks, "timeout_ms") ?? 60000;
        if (timeout <= 0)
        {
            timeout = 60000;
        }

        return new HooksConfig(
            ConfigReader.ReadString(hooks, "after_create"),
            ConfigReader.ReadString(hooks, "before_run"),
            ConfigReader.ReadString(hooks, "after_run"),
            ConfigReader.ReadString(hooks, "before_remove"),
            timeout);
    }
}

record AgentConfig(
    int MaxConcurrentAgents,
    int MaxRetryBackoffMs,
    int MaxTurns,
    IReadOnlyDictionary<string, int> MaxConcurrentByState)
{
    public static AgentConfig From(IReadOnlyDictionary<string, object> root)
    {
        var agent = ConfigReader.ReadMap(root, "agent");
        var max = ConfigReader.ReadInt(agent, "max_concurrent_agents") ?? 10;
        if (max <= 0)
        {
            max = 10;
        }

        var backoff = ConfigReader.ReadInt(agent, "max_retry_backoff_ms") ?? 300000;
        if (backoff <= 0)
        {
            backoff = 300000;
        }

        var maxTurns = ConfigReader.ReadInt(agent, "max_turns") ?? 1;
        if (maxTurns <= 0)
        {
            maxTurns = 1;
        }

        var perState = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (agent.TryGetValue("max_concurrent_agents_by_state", out var rawMap) && rawMap is IReadOnlyDictionary<string, object> map)
        {
            foreach (var (key, value) in map)
            {
                var normalizedKey = key.Trim().ToLowerInvariant();
                if (value is int intValue && intValue > 0)
                {
                    perState[normalizedKey] = intValue;
                }
                else if (value is string str && int.TryParse(str, out var parsed) && parsed > 0)
                {
                    perState[normalizedKey] = parsed;
                }
            }
        }

        return new AgentConfig(max, backoff, maxTurns, perState);
    }
}

record CodexConfig(
    string Command,
    string? ApprovalPolicy,
    string? ThreadSandbox,
    string? TurnSandboxPolicy,
    int TurnTimeoutMs,
    int ReadTimeoutMs,
    int StallTimeoutMs)
{
    public static CodexConfig From(IReadOnlyDictionary<string, object> root)
    {
        var codex = ConfigReader.ReadMap(root, "codex");
        var command = ConfigReader.ReadString(codex, "command") ?? "codex app-server";
        var approval = ConfigReader.ReadString(codex, "approval_policy");
        var threadSandbox = ConfigReader.ReadString(codex, "thread_sandbox");
        var turnSandbox = ConfigReader.ReadString(codex, "turn_sandbox_policy");
        var turnTimeout = ConfigReader.ReadInt(codex, "turn_timeout_ms") ?? 3600000;
        var readTimeout = ConfigReader.ReadInt(codex, "read_timeout_ms") ?? 5000;
        var stallTimeout = ConfigReader.ReadInt(codex, "stall_timeout_ms") ?? 300000;
        return new CodexConfig(command, approval, threadSandbox, turnSandbox, turnTimeout, readTimeout, stallTimeout);
    }
}

record ConfigValidationResult(bool IsOk, string? ErrorMessage)
{
    public static ConfigValidationResult Ok() => new(true, null);
    public static ConfigValidationResult Fail(string message) => new(false, message);
}

class ServiceConfigProvider
{
    readonly WorkflowManager workflowManager;
    readonly ILogger<ServiceConfigProvider> logger;

    public ServiceConfigProvider(WorkflowManager workflowManager, ILogger<ServiceConfigProvider> logger)
    {
        this.workflowManager = workflowManager;
        this.logger = logger;
    }

    public ServiceConfig GetConfig()
    {
        var definition = workflowManager.Current;
        var config = definition.Config;

        var tracker = TrackerConfig.From(config);
        var polling = PollingConfig.From(config);
        var workspace = WorkspaceConfig.From(config);
        var hooks = HooksConfig.From(config);
        var agent = AgentConfig.From(config);
        var codex = CodexConfig.From(config);

        return new ServiceConfig(definition, tracker, polling, workspace, hooks, agent, codex);
    }

    public ConfigValidationResult ValidateForDispatch(ServiceConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Tracker.Kind))
        {
            return ConfigValidationResult.Fail("tracker.kind is missing");
        }

        if (!string.Equals(config.Tracker.Kind, "linear", StringComparison.OrdinalIgnoreCase))
        {
            return ConfigValidationResult.Fail($"Unsupported tracker.kind '{config.Tracker.Kind}'.");
        }

        if (string.IsNullOrWhiteSpace(config.Tracker.ApiKey))
        {
            return ConfigValidationResult.Fail("tracker.api_key is missing");
        }

        if (string.IsNullOrWhiteSpace(config.Tracker.ProjectSlug))
        {
            return ConfigValidationResult.Fail("tracker.project_slug is missing");
        }

        if (string.IsNullOrWhiteSpace(config.Codex.Command))
        {
            return ConfigValidationResult.Fail("codex.command is missing");
        }

        return ConfigValidationResult.Ok();
    }

    public void LogValidationFailure(ConfigValidationResult validation)
    {
        logger.LogError("Configuration validation failed: {Error}", validation.ErrorMessage);
    }
}

static class ConfigReader
{
    public static IReadOnlyDictionary<string, object> ReadMap(IReadOnlyDictionary<string, object> root, string key)
    {
        if (root.TryGetValue(key, out var value) && value is IReadOnlyDictionary<string, object> map)
        {
            return map;
        }

        if (root.TryGetValue(key, out value) && value is Dictionary<string, object> dict)
        {
            return dict;
        }

        return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
    }

    public static string? ReadString(IReadOnlyDictionary<string, object> map, string key)
    {
        if (!map.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        if (value is string str)
        {
            return str;
        }

        return value.ToString();
    }

    public static int? ReadInt(IReadOnlyDictionary<string, object> map, string key)
    {
        if (!map.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        if (value is int i)
        {
            return i;
        }

        if (value is long l)
        {
            return (int)l;
        }

        if (value is string str && int.TryParse(str, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    public static List<string>? ReadStringList(IReadOnlyDictionary<string, object> map, string key)
    {
        if (!map.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        if (value is List<object> list)
        {
            var result = new List<string>();
            foreach (var item in list)
            {
                if (item is null)
                {
                    continue;
                }
                result.Add(item.ToString() ?? string.Empty);
            }

            return result;
        }

        return null;
    }

    public static string ResolveEnvValue(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        if (raw.StartsWith("$", StringComparison.Ordinal))
        {
            var varName = raw.Substring(1);
            var value = Environment.GetEnvironmentVariable(varName) ?? string.Empty;
            return value;
        }

        return raw;
    }

    public static string ExpandPath(string raw)
    {
        var resolved = ResolveEnvValue(raw);
        if (resolved.StartsWith("~"))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            resolved = Path.Combine(home, resolved.Substring(1).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        return Path.GetFullPath(resolved);
    }
}
