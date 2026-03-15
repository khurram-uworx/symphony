using Symphony.App.Domain;

namespace Symphony.App.IssueTracker;

interface IIssueTracker
{
    Task<IReadOnlyList<Issue>> FetchCandidateIssuesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<Issue>> FetchTerminalIssuesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<Issue>> FetchIssueStatesByIdsAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken);
}
