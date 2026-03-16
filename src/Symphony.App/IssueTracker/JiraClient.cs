using Dapplo.Jira;
using Dapplo.Jira.Entities;
using Microsoft.Extensions.Logging;
using Symphony.App.Config;
using Symphony.App.Domain;

namespace Symphony.App.IssueTracker;

class JiraClient : IIssueTracker
{
    readonly IJiraClient jiraClient;
    readonly ILogger<JiraClient> logger;
    readonly ServiceConfig config;

    public JiraClient(ILogger<JiraClient> logger, ServiceConfig config)
    {
        this.logger = logger;
        this.config = config;

        this.jiraClient = Dapplo.Jira.JiraClient.Create(new Uri(config.Tracker.Endpoint));
        
        // Parse credentials from api_key (format: "username:token" or just "token")
        var credentials = config.Tracker.ApiKey;
        if (credentials.Contains(':'))
        {
            var parts = credentials.Split(':', 2);
            this.jiraClient.SetBasicAuthentication(parts[0], parts[1]);
        }
        else
        {
            // Assume it's a bearer token or API token
            // For Jira Cloud, we typically use email:apitoken
            this.jiraClient.SetBasicAuthentication(config.Tracker.ProjectSlug, credentials);
        }
    }

    static Domain.Issue parseIssue(IssueV2 jiraIssue)
    {
        var labels = jiraIssue.Fields?.Labels?.Select(l => l.Trim().ToLowerInvariant()).ToList() ?? new List<string>();
        
        var blockers = new List<BlockerRef>();
        // Jira doesn't have a direct "blockedBy" relationship in the same way Linear does
        // This would need to be extracted from issue links if needed
        // For now, we'll leave it empty to maintain compatibility
        
        return new Domain.Issue(
            jiraIssue.Key ?? string.Empty,
            jiraIssue.Key ?? string.Empty,
            jiraIssue.Fields?.Summary ?? string.Empty,
            jiraIssue.Fields?.Description,
            mapJiraPriorityToInt(jiraIssue.Fields?.Priority?.Name),
            jiraIssue.Fields?.Status?.Name ?? "Unknown",
            null, // Jira doesn't have branch name in standard fields
            null, // Could be constructed from Endpoint + Key if needed
            labels,
            blockers,
            jiraIssue.Fields?.Created,
            jiraIssue.Fields?.Updated);
    }

    static int? mapJiraPriorityToInt(string? jiraPriority)
    {
        if (string.IsNullOrWhiteSpace(jiraPriority))
            return null;

        // Map Jira priority names to integers (lower number = higher priority)
        // Linear uses: 1=Urgent, 2=High, 3=Normal, 4=Low
        return jiraPriority.ToLowerInvariant() switch
        {
            "blocker" or "highest" => 1,  // Urgent
            "critical" or "high" => 2,     // High
            "major" or "medium" => 3,      // Normal
            "minor" or "low" => 4,         // Low
            "trivial" or "lowest" => 4,    // Low
            _ => null
        };
    }

    async Task<IReadOnlyList<Domain.Issue>> fetchIssuesByJqlAsync(
        string jql, CancellationToken cancellationToken)
    {
        try
        {
            var results = new List<Domain.Issue>();
            var startAt = 0;
            const int maxResults = 100;
            bool hasMore = true;

            while (hasMore)
            {
                var issues = await jiraClient.Issue.SearchAsync(jql,
                    new Page { StartAt = startAt, MaxResults = maxResults },
                    cancellationToken: cancellationToken);

                if (issues == null || !issues.Any())
                {
                    hasMore = false;
                }
                else
                {
                    foreach (var issue in issues)
                    {
                        results.Add(parseIssue((IssueV2)issue));
                    }

                    // Check if there are more pages
                    if (issues.Count() < maxResults)
                        hasMore = false;
                    else
                        startAt += maxResults;
                }
            }

            return results;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch issues using JQL: {Jql}", jql);
            throw;
        }
    }

    public async Task<IReadOnlyList<Domain.Issue>> FetchCandidateIssuesAsync(CancellationToken cancellationToken)
    {
        // Build JQL query for active states
        var stateConditions = new List<string>();
        foreach (var state in config.ActiveStates)
        {
            stateConditions.Add($"status = '{escapedJqlString(state)}'");
        }

        if (!stateConditions.Any())
        {
            logger.LogWarning("No active states configured for Jira tracker");
            return Array.Empty<Domain.Issue>();
        }

        var jql = $"project = '{escapedJqlString(config.Tracker.ProjectSlug)}' AND ({string.Join(" OR ", stateConditions)})";
        logger.LogInformation("Fetching candidate issues with JQL: {Jql}", jql);

        return await fetchIssuesByJqlAsync(jql, cancellationToken);
    }

    public async Task<IReadOnlyList<Domain.Issue>> FetchTerminalIssuesAsync(CancellationToken cancellationToken)
    {
        // Build JQL query for terminal states
        var stateConditions = new List<string>();
        foreach (var state in config.TerminalStates)
        {
            stateConditions.Add($"status = '{escapedJqlString(state)}'");
        }

        if (!stateConditions.Any())
        {
            logger.LogWarning("No terminal states configured for Jira tracker");
            return Array.Empty<Domain.Issue>();
        }

        var jql = $"project = '{escapedJqlString(config.Tracker.ProjectSlug)}' AND ({string.Join(" OR ", stateConditions)})";
        logger.LogInformation("Fetching terminal issues with JQL: {Jql}", jql);

        return await fetchIssuesByJqlAsync(jql, cancellationToken);
    }

    public async Task<IReadOnlyList<Domain.Issue>> FetchIssueStatesByIdsAsync(
        IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
            return Array.Empty<Domain.Issue>();

        try
        {
            var results = new List<Domain.Issue>();
            
            // Jira keys are like "PROJ-123", so we can use them directly
            var keyConditions = ids.Select(id => $"key = '{escapedJqlString(id)}'").ToList();
            var jql = $"({string.Join(" OR ", keyConditions)})";

            logger.LogInformation("Fetching issue states for {Count} issue keys", ids.Count);
            return await fetchIssuesByJqlAsync(jql, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch issue states by IDs");
            throw;
        }
    }

    static string escapedJqlString(string value)
    {
        // Escape special characters in JQL strings
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
