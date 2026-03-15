using Dapplo.HttpExtensions.Support;
using Dapplo.Jira;
using Dapplo.Jira.Entities;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using static Symphony.Tests.NUnitConstants;

namespace Symphony.Tests;

[TestFixture, Category(TestCatory.Integration)]
class DapploJiraTests
{
    NUnitLogger<DapploJiraTests> logger = new();

    [Test]
    public async Task FieldsTest()
    {
        IJiraClient jiraClient = JiraClient.Create(new Uri(Environment.GetEnvironmentVariable("JIRA_URL")));
        jiraClient.SetBasicAuthentication(Environment.GetEnvironmentVariable("JIRA_USER"), Environment.GetEnvironmentVariable("JIRA_TOKEN"));

        var fields = await jiraClient.Server.GetFieldsAsync();
        Assert.That(fields, Is.Not.Null);
    }

    [Test]
    public async Task SearchTest()
    {
        var jiraClient = JiraClient.Create(new Uri(Environment.GetEnvironmentVariable("JIRA_URL")),
            new HttpSettings
            {
                IgnoreSslCertificateErrors = true
            });
        jiraClient.SetBasicAuthentication(Environment.GetEnvironmentVariable("JIRA_USER"), Environment.GetEnvironmentVariable("JIRA_TOKEN"));

        var jql = "project = 'KFX' and sprint in openSprints() ORDER BY key";

        var issues = await jiraClient.Issue.SearchAsync(jql,
            new Page { StartAt = 0, MaxResults = 100 },
            fields: [.. JiraConfig.SearchFields, "customfield_10016"]);
        Assert.That(issues, Is.Not.Null);

        var items = issues.Select(i => (i.Key,
            i.Fields.Summary, i.Fields.Status.Name,
            (((IssueV2)i).GetCustomField("customfield_10016") ?? "0").ToString() ?? "0",
            i.Fields.Assignee?.DisplayName ?? "Unassigned"));

        this.logger.LogInformation($"👉 Fetched {items.Count()} items from JQL: {jql}");
    }

    [Test]
    public async Task SprintsTest()
    {
        var jiraClient = JiraClient.Create(new Uri(Environment.GetEnvironmentVariable("JIRA_URL")),
            new HttpSettings
            {
                IgnoreSslCertificateErrors = true
            });
        jiraClient.SetBasicAuthentication(Environment.GetEnvironmentVariable("JIRA_USER"), Environment.GetEnvironmentVariable("JIRA_TOKEN"));

        var boards = await jiraClient.Agile.GetBoardsAsync(projectKeyOrId: "KFX");
        foreach (var board in boards)//.Where(b => b.Type == BoardTypes.Scrum))
        {
            var sprints = await jiraClient.Agile.GetSprintsAsync(board.Id);
            foreach (var sprint in sprints)
                this.logger.LogInformation($"Board Id: {board.Id} Board Name: {board.Name}, Type: {board.Type}, Sprint Id: {sprint.Id}, Sprint Name: {sprint.Name}, State: {sprint.State}");
        }
    }

    [Test]
    public async Task WeeksWorklogs()
    {
        var author = "Khurram Aziz";
        var since = new DateTime(2025, 9, 29);

        var jiraClient = JiraClient.Create(new Uri(Environment.GetEnvironmentVariable("JIRA_URL")),
            new HttpSettings
            {
                IgnoreSslCertificateErrors = true
            });
        jiraClient.SetBasicAuthentication(Environment.GetEnvironmentVariable("JIRA_USER"), Environment.GetEnvironmentVariable("JIRA_TOKEN"));

        var jql = $"worklogAuthor = currentUser() AND worklogDate >= {since:yyyy-MM-dd}";

        // paged iteration: https://github.com/khurram-uworx/jiraworklogs/blob/main/JiraWorkLogsService/Helpers/JiraHelper.cs
        var issues = await jiraClient.Issue.SearchAsync(jql,
            new Page { StartAt = 0, MaxResults = 100 });
        Assert.That(issues, Is.Not.Null);
        Assert.That(issues.Count(), Is.GreaterThan(0));

        Func<long, string> formatHours = seconds =>
        {
            if (seconds < 60)
                return $"{seconds}s";
            if (seconds < 3600)
                return $"{seconds / 60.0:N0}m";

            var hours = Math.Floor(seconds / 3600.0);
            var remaining = seconds - (hours * 3600);
            if (remaining > 0)
                return $"{hours}h {remaining / 60.0:N0}m";

            return $"{hours}h";
        };

        long total = 0;
        var tickets = new Dictionary<string, long>();
        var days = new Dictionary<DateOnly, long>();
        var summary = new Dictionary<string, Dictionary<DateOnly, List<(string time, string comment)>>>();

        foreach (var issue in issues)
        {
            var worklogs = await jiraClient.WorkLog.GetAsync(issue.Key);
            foreach (var worklog in worklogs)
            {
                if (worklog.Author.DisplayName != author) continue;
                if (!worklog.Started.HasValue) continue;

                var date = DateOnly.FromDateTime(worklog.Started.Value.Date);
                if (date < DateOnly.FromDateTime(since)) continue;

                if (summary.ContainsKey(issue.Key) == false)
                    summary[issue.Key] = new Dictionary<DateOnly, List<(string, string)>>();
                if (summary[issue.Key].ContainsKey(date) == false)
                    summary[issue.Key][date] = new List<(string, string)>();

                if (days.ContainsKey(date) == false)
                    days[date] = 0;

                if (tickets.ContainsKey(issue.Key) == false)
                    tickets[issue.Key] = 0;

                total += worklog.TimeSpentSeconds ?? 0;
                tickets[issue.Key] += worklog.TimeSpentSeconds ?? 0;
                days[date] += worklog.TimeSpentSeconds ?? 0;

                var s1 = formatHours(worklog.TimeSpentSeconds ?? 0);
                summary[summary.Keys.Last()][date].Add((s1, worklog.Comment));
            }
        }

        this.logger.LogInformation($"👉 Total hours: {formatHours(total)}");
        foreach (var day in days.Keys.OrderBy(d => d))
            this.logger.LogInformation($"  📅 {day:yyyy-MM-dd} {formatHours(days[day])}");
        this.logger.LogInformation("");

        foreach (var issueKey in summary.Keys.OrderBy(k => k))
        {
            this.logger.LogInformation($"{issueKey} 🕧 {formatHours(tickets.First(i => i.Key == issueKey).Value)} 📋 {issues.First(i => i.Key == issueKey).Fields.Summary}");
            foreach (var day in summary[issueKey].Keys.OrderBy(d => d))
                foreach (var (time, comment) in summary[issueKey][day])
                    this.logger.LogInformation($"  📅 {day:yyyy-MM-dd} {time} {comment}");
        }
    }
}