namespace Symphony.App.Domain;

public sealed record Workspace(
    string Path,
    string WorkspaceKey,
    bool CreatedNow);
