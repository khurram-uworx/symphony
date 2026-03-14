namespace Symphony.App.Domain;

record Workspace(
    string Path,
    string WorkspaceKey,
    bool CreatedNow);
