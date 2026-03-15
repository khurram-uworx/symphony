namespace Symphony.App.Domain;

record Workspace(
    string Path,
    string WorkspaceKey,
    bool CreatedNow);

record BlockerRef(
    string? Id,
    string? Identifier,
    string? State);

record Issue(
    string Id,
    string Identifier,
    string Title,
    string? Description,
    int? Priority,
    string State,
    string? BranchName,
    string? Url,
    IReadOnlyList<string> Labels,
    IReadOnlyList<BlockerRef> BlockedBy,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt)
{
    public string NormalizedState => State.Trim(); //.ToLowerInvariant();
}
