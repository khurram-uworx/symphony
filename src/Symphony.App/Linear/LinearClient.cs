using Microsoft.Extensions.Logging;
using Symphony.App.Config;
using Symphony.App.Domain;
using System.Text;
using System.Text.Json;

namespace Symphony.App.Linear;

public sealed class LinearClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LinearClient> _logger;

    public LinearClient(IHttpClientFactory httpClientFactory, ILogger<LinearClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<Issue>> FetchCandidateIssuesAsync(ServiceConfig config, CancellationToken cancellationToken)
    {
        return await FetchIssuesByStatesAsync(config, config.ActiveStates, cancellationToken);
    }

    public async Task<IReadOnlyList<Issue>> FetchTerminalIssuesAsync(ServiceConfig config, CancellationToken cancellationToken)
    {
        return await FetchIssuesByStatesAsync(config, config.TerminalStates, cancellationToken);
    }

    public async Task<IReadOnlyList<Issue>> FetchIssueStatesByIdsAsync(ServiceConfig config, IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return Array.Empty<Issue>();
        }

        var query = @"
query IssuesById($ids: [ID!]) {
  issues(filter: { id: { in: $ids } }) {
    nodes {
      id
      identifier
      title
      description
      priority
      branchName
      url
      createdAt
      updatedAt
      state { name }
      labels { nodes { name } }
      blockedBy {
        nodes {
          id
          identifier
          state { name }
        }
      }
    }
  }
}
";

        var payload = new
        {
            query,
            variables = new { ids }
        };

        var response = await SendRequestAsync(config, payload, cancellationToken);
        return ParseIssues(response, "issues");
    }

    private async Task<IReadOnlyList<Issue>> FetchIssuesByStatesAsync(ServiceConfig config, IEnumerable<string> states, CancellationToken cancellationToken)
    {
        var results = new List<Issue>();
        var stateList = new List<string>(states);
        if (stateList.Count == 0)
        {
            return results;
        }

        string? cursor = null;
        var query = @"
query Issues($projectSlug: String!, $states: [String!], $after: String) {
  issues(
    filter: {
      project: { slugId: { eq: $projectSlug } }
      state: { name: { in: $states } }
    }
    first: 50
    after: $after
  ) {
    nodes {
      id
      identifier
      title
      description
      priority
      branchName
      url
      createdAt
      updatedAt
      state { name }
      labels { nodes { name } }
      blockedBy {
        nodes {
          id
          identifier
          state { name }
        }
      }
    }
    pageInfo { hasNextPage endCursor }
  }
}
";

        while (true)
        {
            var payload = new
            {
                query,
                variables = new { projectSlug = config.Tracker.ProjectSlug, states = stateList, after = cursor }
            };

            var response = await SendRequestAsync(config, payload, cancellationToken);
            var pageIssues = ParseIssues(response, "issues");
            results.AddRange(pageIssues);

            var pageInfo = response.RootElement
                .GetProperty("data")
                .GetProperty("issues")
                .GetProperty("pageInfo");

            var hasNext = pageInfo.GetProperty("hasNextPage").GetBoolean();
            cursor = pageInfo.GetProperty("endCursor").GetString();
            if (!hasNext || string.IsNullOrWhiteSpace(cursor))
            {
                break;
            }
        }

        return results;
    }

    private async Task<JsonDocument> SendRequestAsync(ServiceConfig config, object payload, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, config.Tracker.Endpoint);
        request.Headers.Add("Authorization", config.Tracker.ApiKey);

        var json = JsonSerializer.Serialize(payload);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await client.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Linear API error: {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        var doc = JsonDocument.Parse(content);
        if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
        {
            _logger.LogWarning("Linear GraphQL errors: {Errors}", errors.ToString());
            throw new InvalidOperationException("Linear GraphQL returned errors.");
        }

        return doc;
    }

    private static IReadOnlyList<Issue> ParseIssues(JsonDocument doc, string rootField)
    {
        var result = new List<Issue>();
        var nodes = doc.RootElement.GetProperty("data").GetProperty(rootField).GetProperty("nodes");
        foreach (var node in nodes.EnumerateArray())
        {
            result.Add(ParseIssue(node));
        }

        return result;
    }

    private static Issue ParseIssue(JsonElement node)
    {
        var labels = new List<string>();
        if (node.TryGetProperty("labels", out var labelsNode))
        {
            foreach (var label in labelsNode.GetProperty("nodes").EnumerateArray())
            {
                var name = label.GetProperty("name").GetString() ?? string.Empty;
                labels.Add(name.Trim().ToLowerInvariant());
            }
        }

        var blockers = new List<BlockerRef>();
        if (node.TryGetProperty("blockedBy", out var blockedByNode))
        {
            foreach (var blocker in blockedByNode.GetProperty("nodes").EnumerateArray())
            {
                blockers.Add(new BlockerRef(
                    blocker.GetProperty("id").GetString(),
                    blocker.GetProperty("identifier").GetString(),
                    blocker.GetProperty("state").GetProperty("name").GetString()));
            }
        }

        return new Issue(
            node.GetProperty("id").GetString() ?? string.Empty,
            node.GetProperty("identifier").GetString() ?? string.Empty,
            node.GetProperty("title").GetString() ?? string.Empty,
            node.GetProperty("description").GetString(),
            node.TryGetProperty("priority", out var priorityNode) && priorityNode.ValueKind != JsonValueKind.Null ? priorityNode.GetInt32() : null,
            node.GetProperty("state").GetProperty("name").GetString() ?? string.Empty,
            node.TryGetProperty("branchName", out var branchNode) ? branchNode.GetString() : null,
            node.TryGetProperty("url", out var urlNode) ? urlNode.GetString() : null,
            labels,
            blockers,
            ParseDate(node, "createdAt"),
            ParseDate(node, "updatedAt"));
    }

    private static DateTimeOffset? ParseDate(JsonElement node, string name)
    {
        if (node.TryGetProperty(name, out var dateNode) && dateNode.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(dateNode.GetString(), out var dto))
        {
            return dto;
        }

        return null;
    }
}

