using System.Net;
using Farm.Core.Cows;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using Xunit;
using AzureComment = Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models.Comment;
using AzureRelation = Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models.WorkItemRelation;
using AzureWorkItem = Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models.WorkItem;

namespace Farm.Azure.Tests;

public sealed class AzureCloudWorkItemSourceTests
{
    [Fact]
    public void Linked_pull_request_ids_come_from_artifact_links_only()
    {
        AzureRelation[] relations =
        [
            new() { Rel = "ArtifactLink", Url = "vstfs:///Git/PullRequestId/project-guid%2Frepo-guid%2F321" },
            new() { Rel = "ArtifactLink", Url = "vstfs:///Git/PullRequestId/project-guid%2Frepo-guid%2F12" },
            new() { Rel = "ArtifactLink", Url = "vstfs:///Git/Commit/project-guid%2Frepo-guid%2Fabc" },
            new() { Rel = "System.LinkTypes.Related", Url = "https://dev.azure.com/org/_apis/wit/workItems/5" }
        ];

        Assert.Equal([12, 321], AzureDevOpsClient.GetLinkedPullRequestIds(relations));
        Assert.Empty(AzureDevOpsClient.GetLinkedPullRequestIds(null));
    }

    [Fact]
    public async Task Work_items_omit_missing_and_forbidden_items_and_map_revision()
    {
        var items = new Dictionary<int, AzureWorkItem>
        {
            [1] = Item(1, 7, "Active"),
            [2] = Item(2, 3, "Removed")
        };
        var forbidden = new HashSet<int> { 3 };
        var client = CreateClient(items, forbidden);

        var snapshots = await client.GetWorkItemsAsync([1, 2, 3, 4]);

        Assert.Equal([1, 2], snapshots.Select(snapshot => snapshot.WorkItem.Id).Order());
        var first = snapshots.Single(snapshot => snapshot.WorkItem.Id == 1);
        Assert.Equal(7, first.Revision);
        Assert.Equal("Active", first.WorkItem.State);
        Assert.Equal([99], first.LinkedPullRequestIds);
    }

    [Fact]
    public void Assigned_to_wiql_keeps_sam_query_and_cows_adds_terminal_state_filter()
    {
        Assert.Equal(
            "SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = 'O''Brien' AND [System.AssignedTo] = @Me ORDER BY [System.ChangedDate] DESC",
            AzureDevOpsClient.BuildAssignedToWiql("O'Brien", "me"));
        Assert.Equal(
            "SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = 'P' AND [System.AssignedTo] = @Me AND [System.State] NOT IN ('Done', 'Won''t Fix') ORDER BY [System.ChangedDate] DESC",
            AzureDevOpsClient.BuildAssignedToWiql("P", "ME", ["Done", "Won't Fix"]));
        Assert.Contains("[System.AssignedTo] = 'a''b@example.com'", AzureDevOpsClient.BuildAssignedToWiql("P", "a'b@example.com"));
    }

    [Fact]
    public void Snapshot_exposes_one_hop_related_work_items_without_self_or_duplicates()
    {
        var item = Item(1, 1, "Active");
        item.Relations =
        [
            new AzureRelation { Rel = "System.LinkTypes.Hierarchy-Reverse", Url = "https://dev.azure.com/org/_apis/wit/workItems/10" },
            new AzureRelation { Rel = "System.LinkTypes.Hierarchy-Forward", Url = "https://dev.azure.com/org/_apis/wit/workItems/11" },
            new AzureRelation { Rel = "System.LinkTypes.Related", Url = "https://dev.azure.com/org/_apis/wit/workItems/10" },
            new AzureRelation { Rel = "System.LinkTypes.Dependency-Forward", Url = "https://dev.azure.com/org/_apis/wit/workItems/12" },
            new AzureRelation { Rel = "System.LinkTypes.Dependency-Reverse", Url = "https://dev.azure.com/org/_apis/wit/workItems/13" },
            new AzureRelation { Rel = "System.LinkTypes.Related", Url = "https://dev.azure.com/org/_apis/wit/workItems/1" },
            new AzureRelation { Rel = "ArtifactLink", Url = "vstfs:///Git/PullRequestId/p%2Fr%2F99" }
        ];

        var snapshot = AzureDevOpsClient.ToSnapshot(item);

        Assert.Equal([10, 11, 12, 13], snapshot.RelatedWorkItemIds);
        Assert.Equal([99], snapshot.LinkedPullRequestIds);
    }

    [Fact]
    public async Task Non_404_403_batch_error_fails_the_cycle_instead_of_marking_items_removed()
    {
        var client = CreateClient(_ => throw new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetWorkItemsAsync([1, 2]));
    }

    [Fact]
    public async Task Transient_error_on_a_single_item_retry_fails_the_cycle()
    {
        var client = CreateClient(ids => ids.Count > 1 ? throw Forbidden()
            : ids[0] == 2 ? throw new HttpRequestException("timeout")
            : [Item(ids[0], 1, "Active")]);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetWorkItemsAsync([1, 2, 3]));
    }

    [Theory]
    [MemberData(nameof(TransientErrorCases))]
    public async Task Non_404_403_error_on_a_single_id_chunk_is_rethrown(int errorCase)
    {
        var error = TransientErrors[errorCase];
        var client = CreateClient(_ => throw error);

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => client.GetWorkItemsAsync([1]));
        Assert.Same(error, thrown);
    }

    [Theory]
    [MemberData(nameof(NotFoundOrForbiddenErrorCases))]
    public async Task Not_found_or_forbidden_single_id_chunk_is_reported_missing(int errorCase)
    {
        var client = CreateClient(_ => throw NotFoundOrForbiddenErrors[errorCase]);

        Assert.Empty(await client.GetWorkItemsAsync([1]));
    }

    [Theory]
    [MemberData(nameof(NotFoundOrForbiddenErrorCases))]
    public async Task Missing_or_forbidden_pull_request_is_an_explicit_unavailable_state(int errorCase)
    {
        var client = CreatePullRequestClient(_ => throw NotFoundOrForbiddenErrors[errorCase]);

        var state = await client.GetPullRequestAsync(42);

        Assert.Equal(CloudPullRequestState.Unavailable(42), state);
        Assert.True(state!.IsUnavailable);
    }

    [Theory]
    [MemberData(nameof(TransientErrorCases))]
    public async Task Transient_pull_request_error_returns_null_so_the_note_is_deferred(int errorCase)
    {
        var client = CreatePullRequestClient(_ => throw TransientErrors[errorCase]);

        Assert.Null(await client.GetPullRequestAsync(42));
    }

    [Fact]
    public async Task Pull_request_state_is_mapped()
    {
        var client = CreatePullRequestClient(id => new GitPullRequest
        {
            PullRequestId = id,
            Status = PullRequestStatus.Completed,
            Title = "Title",
            LastMergeSourceCommit = new GitCommitRef { CommitId = "abc" }
        });

        var state = await client.GetPullRequestAsync(42);

        Assert.Equal("Completed", state!.Status);
        Assert.Equal("abc", state.SourceCommitId);
        Assert.False(state.IsUnavailable);
    }

    private static readonly Exception[] NotFoundOrForbiddenErrors =
    [
        new VssServiceResponseException(HttpStatusCode.NotFound, "not found", null),
        Forbidden(),
        new HttpRequestException("forbidden", null, HttpStatusCode.Forbidden),
        new VssServiceException("TF401232: Work item 1 does not exist, or you do not have permissions to read it."),
        new VssServiceException("TF401180: The requested pull request was not found.")
    ];

    private static readonly Exception[] TransientErrors =
    [
        new VssServiceResponseException(HttpStatusCode.InternalServerError, "boom", null),
        new VssServiceResponseException(HttpStatusCode.TooManyRequests, "throttled", null),
        new HttpRequestException("timeout"),
        new VssUnauthorizedException("TF401232: expired PAT"),
        new VssServiceException("TF400898: internal error"),
        new InvalidOperationException("TF401232: not an ADO service error")
    ];

    public static TheoryData<int> NotFoundOrForbiddenErrorCases() => [.. Enumerable.Range(0, NotFoundOrForbiddenErrors.Length)];

    public static TheoryData<int> TransientErrorCases() => [.. Enumerable.Range(0, TransientErrors.Length)];

    private static VssServiceResponseException Forbidden() => new(HttpStatusCode.Forbidden, "forbidden", null);

    private static AzureDevOpsSettings Settings() => new()
    {
        OrganizationUrl = new Uri("https://dev.azure.com/org"),
        Project = "Project",
        PersonalAccessToken = "token"
    };

    private static AzureDevOpsClient CreateClient(Dictionary<int, AzureWorkItem> items, HashSet<int> forbidden) =>
        CreateClient(ids => ids.Any(forbidden.Contains)
            ? throw Forbidden()
            : ids.Select(id => items.TryGetValue(id, out var item) ? item : null).ToArray());

    private static AzureDevOpsClient CreateClient(Func<IReadOnlyList<int>, IReadOnlyList<AzureWorkItem?>> load) => new(
        Settings(),
        (_, ids, _, _) => Task.FromResult(load(ids)),
        (_, _, _, _) => Task.FromResult<IReadOnlyList<AzureComment>>([]));

    private static AzureDevOpsClient CreatePullRequestClient(Func<int, GitPullRequest> load) => new(
        Settings(),
        (_, _, _, _) => Task.FromResult<IReadOnlyList<AzureWorkItem?>>([]),
        (_, _, _, _) => Task.FromResult<IReadOnlyList<AzureComment>>([]),
        (id, _) => Task.FromResult(load(id)));

    private static AzureWorkItem Item(int id, int revision, string state) => new()
    {
        Id = id,
        Rev = revision,
        Fields = new Dictionary<string, object>
        {
            ["System.Title"] = $"Item {id}",
            ["System.State"] = state,
            ["System.ChangedDate"] = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        },
        Relations =
        [
            new AzureRelation { Rel = "ArtifactLink", Url = "vstfs:///Git/PullRequestId/p%2Fr%2F99" }
        ]
    };
}
