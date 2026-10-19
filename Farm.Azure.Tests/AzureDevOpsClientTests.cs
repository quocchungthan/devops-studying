using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using Xunit;
using CoreWorkItemComment = Farm.Core.Chickens.WorkItemComment;

namespace Farm.Azure.Tests;

public sealed class AzureDevOpsClientTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task GetWorkItemsAsync_rejects_non_positive_ids(int id)
    {
        IAzureDevOpsClient client = CreateClient();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => client.GetWorkItemsAsync([id]));

        Assert.Contains("positive", exception.Message);
    }

    [Fact]
    public async Task GetWorkItemsAsync_rejects_duplicate_ids()
    {
        IAzureDevOpsClient client = CreateClient();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => client.GetWorkItemsAsync([1, 1]));

        Assert.Contains("duplicates", exception.Message);
    }

    [Fact]
    public async Task GetDomainWorkItemsAsync_requests_detail_fields_and_relations_and_maps_description()
    {
        string? requestedProject = null;
        IReadOnlyList<int>? requestedIds = null;
        IReadOnlyList<string>? requestedFields = null;
        WorkItemExpand requestedExpand = WorkItemExpand.None;
        using var client = new AzureDevOpsClient(
            CreateSettings(),
            (project, ids, fields, expand, _) =>
            {
                requestedProject = project;
                requestedIds = ids;
                requestedFields = fields;
                requestedExpand = expand;
                IReadOnlyList<WorkItem> result =
                [
                    new WorkItem
                    {
                        Id = 42,
                        Fields = new Dictionary<string, object>
                        {
                            ["System.Description"] = "<p>Detailed &amp; readable</p>"
                        }
                    }
                ];
                return Task.FromResult(result);
            });

        var result = await client.GetDomainWorkItemsAsync([42]);

        Assert.Equal("Project", requestedProject);
        Assert.Equal([42], requestedIds);
        Assert.Contains("System.Description", requestedFields!);
        Assert.Contains("System.Reason", requestedFields!);
        Assert.Contains("System.AreaPath", requestedFields!);
        Assert.Contains("System.IterationPath", requestedFields!);
        Assert.Contains("System.CreatedDate", requestedFields!);
        Assert.Contains("System.ChangedDate", requestedFields!);
        Assert.Contains("System.CreatedBy", requestedFields!);
        Assert.Contains("System.ChangedBy", requestedFields!);
        Assert.Contains("System.Tags", requestedFields!);
        Assert.Equal(WorkItemExpand.Relations, requestedExpand);
        Assert.Equal("Detailed & readable", Assert.Single(result).Description);
    }

    [Fact]
    public async Task GetWorkItemCommentsAsync_reads_all_pages_and_returns_comments_in_time_order()
    {
        var continuationTokens = new List<string?>();
        using var client = new AzureDevOpsClient(
            CreateSettings(),
            (project, id, continuationToken, _) =>
            {
                Assert.Equal("Project", project);
                Assert.Equal(42, id);
                continuationTokens.Add(continuationToken);
                return Task.FromResult(continuationToken is null
                    ? new CommentList
                    {
                        Comments =
                        [
                            new Comment { Id = 9, Text = "Later", CreatedDate = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc) },
                            new Comment { Id = 3, Text = "<p>Earlier &amp; first</p>", CreatedDate = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc) }
                        ],
                        ContinuationToken = "next-page"
                    }
                    : new CommentList
                    {
                        Comments =
                        [
                            new Comment { Id = 12, Text = "Deleted", IsDeleted = true, CreatedDate = new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc) }
                        ]
                    });
            });

        IReadOnlyList<CoreWorkItemComment> comments = await client.GetWorkItemCommentsAsync(42);

        Assert.Equal([null, "next-page"], continuationTokens);
        Assert.Equal([3, 9], comments.Select(comment => comment.Id));
        Assert.Equal("Earlier & first", comments[0].Content);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetWorkItemsAssignedToAsync_rejects_blank_assignee(string assignee)
    {
        IAzureDomainWorkItemClient client = CreateClient();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => client.GetWorkItemsAssignedToAsync(assignee));

        Assert.Contains("empty", exception.Message);
    }

    [Fact]
    public async Task GetNeedsAttentionWorkItemsAsync_requires_team_context()
    {
        IAzureDomainWorkItemClient client = CreateClient();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetNeedsAttentionWorkItemsAsync());

        Assert.Contains("FARM_AZURE_DEVOPS_TEAM", exception.Message);
        Assert.Contains("@CurrentIteration", exception.Message);
    }

    [Fact]
    public void BuildNeedsAttentionWiql_escapes_project_and_each_terminal_state()
    {
        var query = AzureDevOpsClient.BuildNeedsAttentionWiql(
            "Owner's Project",
            ["Done", "Ready for O'Brien"]);

        Assert.Contains("[System.TeamProject] = 'Owner''s Project'", query);
        Assert.Contains("[System.State] NOT IN ('Done', 'Ready for O''Brien')", query);
    }

    private static AzureDevOpsClient CreateClient() => new(CreateSettings());

    private static AzureDevOpsSettings CreateSettings() => new()
    {
        OrganizationUrl = new Uri("https://dev.azure.com/example"),
        Project = "Project",
        PersonalAccessToken = "token"
    };
}