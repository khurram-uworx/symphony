namespace Symphony.App.Domain;

public sealed record BlockerRef(
    string? Id,
    string? Identifier,
    string? State);

public sealed record Issue(
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
    public string NormalizedState => State.Trim().ToLowerInvariant();
}
