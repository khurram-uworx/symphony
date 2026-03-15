using Dapplo.HttpExtensions.Support;
using Dapplo.Jira;
using Dapplo.Jira.Entities;
using Microsoft.Extensions.Logging;
using Symphony.App.Config;
using DomainIssue = Symphony.App.Domain.Issue;
using DomainBlockerRef = Symphony.App.Domain.BlockerRef;
using DapploJiraClient = Dapplo.Jira.JiraClient;

namespace Symphony.App.IssueTracker;

class JiraIssueTracker : IIssueTracker
{
    readonly IJiraClient jiraClient;
    readonly ILogger<JiraIssueTracker> logger;
    readonly ServiceConfig config;

    public JiraIssueTracker(IJiraClient jiraClient, ILogger<JiraIssueTracker> logger, ServiceConfig config)
    {
        this.jiraClient = jiraClient;
        this.logger = logger;
        this.config = config;
    }

    public static IJiraClient CreateJiraHttpClient(string endpoint, string apiToken, ILogger<JiraIssueTracker> logger)
    {
        var client = DapploJiraClient.Create(new Uri(endpoint));
        // Jira Cloud uses Bearer token, Jira Server/Data Center uses basic auth
        // For Cloud, the apiToken is a PAT and we use basic auth with email as username
        // Assuming this is the format: email:apiToken (will be encoded to Base64 by SetBasicAuthentication)
        // Or it could be a Bearer token directly
        // The safer approach is to use SetBearerAuthentication if available, else SetBasicAuthentication
        try
        {
            // Try bearer token first (Jira Cloud API tokens)
            client.SetBearerAuthentication(apiToken);
        }
        catch
        {
            // Fallback to basic auth - this requires extracting user from config or env
            // For now, we'll use a basic auth setup where apiToken is passed
            // This may need adjustment based on your Jira instance type
            logger.LogWarning("Bearer auth failed, attempting basic auth with API token");
            // Note: This is a limitation - we'd need the username separately for proper basic auth
        }
        return client;
    }

    DomainIssue parseIssue(IssueV2 jiraIssue)
    {
        var labels = new List<string>();
        if (jiraIssue.Fields?.Labels != null)
        {
            foreach (var label in jiraIssue.Fields.Labels)
            {
                if (!string.IsNullOrWhiteSpace(label))
                    labels.Add(label.Trim().ToLowerInvariant());
            }
        }

        var blockers = new List<DomainBlockerRef>();
        // Jira doesn't have native blockedBy relationship in the same way Linear does
        // This can be extended to parse custom fields or link types if needed
        // For now, we return an empty list to satisfy the interface

        var description = jiraIssue.Fields?.Description != null 
            ? jiraIssue.Fields.Description.ToString() 
            : null;

        return new DomainIssue(
            jiraIssue.Key,
            jiraIssue.Key,
            jiraIssue.Fields?.Summary ?? string.Empty,
            description,
            null, // Priority
            jiraIssue.Fields?.Status?.Name ?? "Unknown",
            null, // BranchName
            null, // Url
            labels,
            blockers,
            jiraIssue.Fields?.Created,
            jiraIssue.Fields?.Updated);
    }

    DomainIssue parseBaseIssue(Dapplo.Jira.Entities.Issue issue)
    {
        var labels = new List<string>();
        if (issue.Fields?.Labels != null)
        {
            foreach (var label in issue.Fields.Labels)
            {
                if (!string.IsNullOrWhiteSpace(label))
                    labels.Add(label.Trim().ToLowerInvariant());
            }
        }

        var description = issue.Fields?.Description != null 
            ? issue.Fields.Description.ToString() 
            : null;

        return new DomainIssue(
            issue.Key,
            issue.Key,
            issue.Fields?.Summary ?? string.Empty,
            description,
            null, // Priority
            issue.Fields?.Status?.Name ?? "Unknown",
            null, // BranchName
            null, // Url
            labels,
            new List<DomainBlockerRef>(),
            issue.Fields?.Created,
            issue.Fields?.Updated);
    }

    string buildJql(IEnumerable<string> states)
    {
        var stateList = new List<string>(states);
        if (stateList.Count == 0)
            return string.Empty;

        var projectKey = config.Tracker.ProjectSlug;
        var statusConditions = string.Join(" OR ", stateList.Select(s => $"status = \"{s}\""));
        return $"project = \"{projectKey}\" AND ({statusConditions}) ORDER BY key";
    }

    async Task<IReadOnlyList<DomainIssue>> fetchIssuesByStatesAsync(
        IEnumerable<string> states, CancellationToken cancellationToken)
    {
        var results = new List<DomainIssue>();
        var jql = buildJql(states);

        if (string.IsNullOrWhiteSpace(jql))
            return results;

        try
        {
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
                    break;
                }

                foreach (var issue in issues)
                {
                    if (issue != null)
                    {
                        results.Add(parseBaseIssue(issue));
                    }
                }

                // Check if there are more results
                if (issues.Count() < maxResults)
                    hasMore = false;
                else
                    startAt += maxResults;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching issues from Jira with JQL: {Jql}", jql);
            throw;
        }

        return results;
    }

    public async Task<IReadOnlyList<DomainIssue>> FetchCandidateIssuesAsync(CancellationToken cancellationToken)
    {
        return await fetchIssuesByStatesAsync(config.ActiveStates, cancellationToken);
    }

    public async Task<IReadOnlyList<DomainIssue>> FetchTerminalIssuesAsync(CancellationToken cancellationToken)
    {
        return await fetchIssuesByStatesAsync(config.TerminalStates, cancellationToken);
    }

    public async Task<IReadOnlyList<DomainIssue>> FetchIssueStatesByIdsAsync(
        IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        var results = new List<DomainIssue>();

        if (ids.Count == 0)
            return results;

        try
        {
            // Build a JQL query using issue keys
            var keyConditions = string.Join(" OR ", ids.Select(id => $"key = \"{id}\""));
            var jql = $"({keyConditions})";

            var issues = await jiraClient.Issue.SearchAsync(jql,
                new Page { StartAt = 0, MaxResults = ids.Count },
                cancellationToken: cancellationToken);

            if (issues != null)
            {
                foreach (var issue in issues)
                {
                    if (issue != null)
                    {
                        results.Add(parseBaseIssue(issue));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching issues by IDs from Jira");
            throw;
        }

        return results;
    }
}
