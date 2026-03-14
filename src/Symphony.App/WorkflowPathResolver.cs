namespace Symphony.App;

sealed record WorkflowPath(string Value);

static class WorkflowPathResolver
{
    public static string Resolve(string[] args)
    {
        if (args.Length > 1)
        {
            throw new ArgumentException("Usage: symphony [path-to-WORKFLOW.md]");
        }

        var path = args.Length == 1
            ? args[0]
            : Path.Combine(Environment.CurrentDirectory, "WORKFLOW.md");

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"WORKFLOW.md not found at {path}");
        }

        return Path.GetFullPath(path);
    }
}
