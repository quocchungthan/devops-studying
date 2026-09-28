using Farm.Core.Chickens;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.VisualStudio.Services.WebApi;
using Xunit;
using AzureComment = Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models.Comment;
using AzureRelation = Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models.WorkItemRelation;
using AzureWorkItem = Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models.WorkItem;

namespace Farm.Azure.Tests;

public sealed class AzureReviewContextSourceTests
{
    [Fact]
    public void MapThread_carries_both_file_sides_and_iteration_context_into_core()
    {
        var thread = new GitPullRequestCommentThread
        {
            Id = 7,
            Status = CommentThreadStatus.Active,
            ThreadContext = new CommentThreadContext
            {
                FilePath = "/file.cs",
                LeftFileStart = new CommentPosition { Line = 10, Offset = 2 },
                LeftFileEnd = new CommentPosition { Line = 11, Offset = 3 },
                RightFileStart = new CommentPosition { Line = 20, Offset = 4 },
                RightFileEnd = new CommentPosition { Line = 21, Offset = 5 }
            },
            PullRequestThreadContext = new GitPullRequestCommentThreadContext
            {
                ChangeTrackingId = 42,
                IterationContext = new CommentIterationContext
                {
                    FirstComparingIteration = 3,
                    SecondComparingIteration = 4
                }
            },
            Comments = []
        };

        FeedbackThread mapped = AzureDevOpsClient.MapThread(thread, "left-sha", "right-sha");

        Assert.Equal(10, mapped.Anchor!.LeftStartLine);
        Assert.Equal(21, mapped.Anchor.RightEndLine);
        Assert.Equal(3, mapped.Anchor.FirstComparingIteration);
        Assert.Equal(4, mapped.Anchor.SecondComparingIteration);
        Assert.Equal(42, mapped.Anchor.ChangeTrackingId);
        Assert.Equal("left-sha", mapped.Anchor.LeftCommitId);
        Assert.Equal("right-sha", mapped.Anchor.RightCommitId);
        Assert.Equal(typeof(FeedbackAnchor).Assembly, mapped.Anchor.GetType().Assembly);
    }

    [Fact]
    public void MapCandidate_carries_full_target_sha_into_core()
    {
        var targetSha = new string('b', 40);

        var candidate = AzureDevOpsClient.MapCandidate(
            PullRequest(new string('a', 40), targetSha), [], [], new Uri("https://dev.azure.com/org"), "project");

        Assert.Equal(targetSha, candidate.TargetSha);
    }

    [Fact]
    public void MapCandidate_uses_target_repository_as_source_for_non_fork()
    {
        var pullRequest = PullRequest(new string('a', 40), new string('b', 40));

        var candidate = AzureDevOpsClient.MapCandidate(
            pullRequest, [], [], new Uri("https://dev.azure.com/org"), "project");

        Assert.Equal(candidate.RepositoryId, candidate.SourceRepositoryId);
        Assert.Equal(candidate.RepositoryName, candidate.SourceRepositoryName);
        Assert.Equal(candidate.RepositoryUrl, candidate.SourceRepositoryUrl);
    }

    [Fact]
    public void MapCandidate_uses_fork_repository_as_source()
    {
        var pullRequest = PullRequest(new string('a', 40), new string('b', 40));
        var forkRepository = new GitRepository
        {
            Id = Guid.NewGuid(),
            Name = "fork-repo",
            RemoteUrl = "https://fork.example.test/repo"
        };
        pullRequest.ForkSource = new GitForkRef { Repository = forkRepository };

        var candidate = AzureDevOpsClient.MapCandidate(
            pullRequest, [], [], new Uri("https://dev.azure.com/org"), "project");

        Assert.Equal(pullRequest.Repository.Id.ToString(), candidate.RepositoryId);
        Assert.Equal(new Uri("https://example.test/repo"), candidate.RepositoryUrl);
        Assert.Equal(forkRepository.Id.ToString(), candidate.SourceRepositoryId);
        Assert.Equal("fork-repo", candidate.SourceRepositoryName);
        Assert.Equal(new Uri("https://fork.example.test/repo"), candidate.SourceRepositoryUrl);
    }

    [Fact]
    public void MapCandidate_derives_escaped_target_repository_url_when_absolute_urls_are_missing()
    {
        var pullRequest = PullRequest(new string('a', 40), new string('b', 40));
        pullRequest.Repository.RemoteUrl = null;
        pullRequest.Repository.WebUrl = null;
        pullRequest.Repository.Name = "repo/name";

        var candidate = AzureDevOpsClient.MapCandidate(
            pullRequest, [], [], new Uri("https://dev.azure.com/example"), "Project / One");

        Assert.Equal(
            new Uri("https://dev.azure.com/example/Project%20%2F%20One/_git/repo%2Fname"),
            candidate.RepositoryUrl);
    }

    [Fact]
    public void MapCandidate_derives_fork_source_url_separately_from_target_url()
    {
        var pullRequest = PullRequest(new string('a', 40), new string('b', 40));
        pullRequest.Repository.RemoteUrl = null;
        pullRequest.Repository.WebUrl = null;
        pullRequest.ForkSource = new GitForkRef
        {
            Repository = new GitRepository
            {
                Id = Guid.NewGuid(),
                Name = "fork/repo"
            }
        };

        var candidate = AzureDevOpsClient.MapCandidate(
            pullRequest, [], [], new Uri("https://dev.azure.com/example"), "Project");

        Assert.Equal(new Uri("https://dev.azure.com/example/Project/_git/repo"), candidate.RepositoryUrl);
        Assert.Equal(new Uri("https://dev.azure.com/example/Project/_git/fork%2Frepo"), candidate.SourceRepositoryUrl);
    }

    [Fact]
    public void TryMapCandidate_skips_malformed_candidate_without_blocking_valid_candidate()
    {
        var malformed = PullRequest(new string('a', 40), null);
        var valid = PullRequest(new string('a', 40), new string('b', 40));

        Assert.False(AzureDevOpsClient.TryMapCandidate(
            malformed, [], [], new Uri("https://dev.azure.com/example"), "Project", out _));
        Assert.True(AzureDevOpsClient.TryMapCandidate(
            valid, [], [], new Uri("https://dev.azure.com/example"), "Project", out var candidate));
        Assert.NotNull(candidate);
    }

    [Fact]
    public void TryMapCandidate_returns_stable_skip_reason_for_missing_repository_metadata()
    {
        var pullRequest = PullRequest(new string('a', 40), new string('b', 40));
        pullRequest.Repository.Name = null;
        pullRequest.Repository.Id = Guid.Empty;
        pullRequest.Repository.RemoteUrl = null;
        pullRequest.Repository.WebUrl = null;

        var mapped = AzureDevOpsClient.TryMapCandidate(
            pullRequest, [], [], new Uri("https://dev.azure.com/example"), "Project", out var candidate, out var reason);

        Assert.False(mapped);
        Assert.Null(candidate);
        Assert.Equal("repository_metadata_missing", reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("000000000000000000000000000000000000000g")]
    public void MapCandidate_rejects_invalid_target_sha(string? targetSha)
    {
        Assert.Throws<InvalidOperationException>(() => AzureDevOpsClient.MapCandidate(
            PullRequest(new string('a', 40), targetSha), [], [], new Uri("https://dev.azure.com/org"), "project"));
    }

    [Fact]
    public async Task GetContextAsync_follows_links_breadth_first_and_stops_at_max_depth()
    {
        var graph = new FakeWorkItemGraph(
            Item(1, (ChildLink, 2)),
            Item(2, (ChildLink, 3)),
            Item(3, (ChildLink, 4)),
            Item(4, (ChildLink, 5)),
            Item(5));
        using var client = graph.CreateClient(maxDepth: 3);

        var context = await client.GetContextAsync(Candidate(1));

        Assert.Equal([1, 2, 3, 4], context.WorkItems.Select(workItem => workItem.Id));
        Assert.Equal([0, 1, 2, 3], context.WorkItems.Select(workItem => workItem.Depth));
        Assert.Equal(
            "linked to PR #42 > child of #1 > child of #2 > child of #3",
            context.WorkItems.Single(workItem => workItem.Id == 4).RelationPath);
        Assert.DoesNotContain(5, graph.RequestedIds);
        Assert.Equal([[1], [2], [3], [4]], graph.Batches);
    }

    [Fact]
    public async Task GetContextAsync_batches_each_level_and_labels_relation_direction()
    {
        var graph = new FakeWorkItemGraph(
            Item(1, (ParentLink, 2), (RelatedLink, 3), (SuccessorLink, 4), (PredecessorLink, 5)),
            Item(2), Item(3), Item(4), Item(5));
        using var client = graph.CreateClient();

        var context = await client.GetContextAsync(Candidate(1));

        Assert.Equal([[1], [2, 3, 4, 5]], graph.Batches);
        var paths = context.WorkItems.ToDictionary(workItem => workItem.Id, workItem => workItem.RelationPath);
        Assert.Equal("linked to PR #42", paths[1]);
        Assert.Equal("linked to PR #42 > parent of #1", paths[2]);
        Assert.Equal("linked to PR #42 > related to #1", paths[3]);
        Assert.Equal("linked to PR #42 > successor of #1", paths[4]);
        Assert.Equal("linked to PR #42 > predecessor of #1", paths[5]);
    }

    [Fact]
    public async Task GetContextAsync_does_not_loop_on_cycles_or_duplicate_links()
    {
        var graph = new FakeWorkItemGraph(
            Item(1, (RelatedLink, 2), (ChildLink, 2)),
            Item(2, (RelatedLink, 1), (ParentLink, 1), (ChildLink, 3)),
            Item(3, (ParentLink, 2), (RelatedLink, 1)));
        using var client = graph.CreateClient(maxDepth: 10);

        var context = await client.GetContextAsync(Candidate(1, 1));

        Assert.Equal([1, 2, 3], context.WorkItems.Select(workItem => workItem.Id));
        Assert.Equal([1, 2, 3], graph.RequestedIds);
    }

    [Fact]
    public async Task GetContextAsync_respects_total_item_cap()
    {
        var graph = new FakeWorkItemGraph(
            [Item(1, Enumerable.Range(2, 10).Select(id => (ChildLink, id)).ToArray()),
             .. Enumerable.Range(2, 10).Select(id => Item(id, (ChildLink, id + 100)))]);
        using var client = graph.CreateClient(maxWorkItems: 4);

        var context = await client.GetContextAsync(Candidate(1));

        Assert.Equal(4, context.WorkItems.Count);
        Assert.Equal([1, 2, 3, 4], graph.RequestedIds);
    }

    [Fact]
    public async Task GetContextAsync_skips_non_work_item_relations()
    {
        var graph = new FakeWorkItemGraph(
            Item(1,
                ("ArtifactLink", 97),
                ("Hyperlink", 98),
                ("AttachedFile", 99)),
            Item(97), Item(98), Item(99));
        using var client = graph.CreateClient();

        var context = await client.GetContextAsync(Candidate(1));

        Assert.Equal([1], context.WorkItems.Select(workItem => workItem.Id));
        Assert.Equal([1], graph.RequestedIds);
        Assert.Equal(3, context.WorkItems[0].Relations.Count);
    }

    [Fact]
    public async Task GetContextAsync_captures_type_description_repro_steps_acceptance_criteria_and_comments()
    {
        var bug = Item(1);
        bug.Fields["System.WorkItemType"] = "Bug";
        bug.Fields["System.Description"] = "<p>Broken &amp; slow</p>";
        bug.Fields["Microsoft.VSTS.TCM.ReproSteps"] = "<ol><li>Open</li><li>Click</li></ol>";
        bug.Fields["Microsoft.VSTS.Common.AcceptanceCriteria"] = "<div>Fast</div>";
        bug.Fields["System.Title"] = new string('t', 10);
        var graph = new FakeWorkItemGraph(bug);
        graph.Comments[1] =
        [
            Comment(2, "<b>second</b>", new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)),
            Comment(1, $"<p>{new string('x', AzureDevOpsClient.MaxContextFieldLength + 10)}</p>", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc))
        ];
        using var client = graph.CreateClient();

        var workItem = Assert.Single((await client.GetContextAsync(Candidate(1))).WorkItems);

        Assert.Equal("Bug", workItem.WorkItemType);
        Assert.Equal("Active", workItem.State);
        Assert.Equal("Broken & slow", workItem.Description);
        Assert.Equal($"Open{Environment.NewLine}Click", workItem.ReproSteps);
        Assert.Equal("Fast", workItem.AcceptanceCriteria);
        Assert.Equal([1, 2], workItem.Comments.Select(comment => comment.Id));
        Assert.EndsWith(" [truncated]", workItem.Comments[0].Content);
        Assert.Equal(AzureDevOpsClient.MaxContextFieldLength + " [truncated]".Length, workItem.Comments[0].Content.Length);
        Assert.Equal("second", workItem.Comments[1].Content);
        Assert.Equal([AzureDevOpsClient.MaxCommentsPerWorkItem], graph.CommentTops.Distinct());
    }

    [Fact]
    public async Task GetContextAsync_skips_unavailable_related_items_and_comments_without_failing()
    {
        var graph = new FakeWorkItemGraph(
            Item(1, (ChildLink, 2), (ChildLink, 3), (ChildLink, 4)),
            Item(2, (ChildLink, 5)),
            Item(3),
            Item(5));
        graph.ForbiddenIds.Add(3);
        graph.CommentFailureIds.Add(2);
        using var client = graph.CreateClient();

        var context = await client.GetContextAsync(Candidate(1));

        Assert.Equal([1, 2, 5], context.WorkItems.Select(workItem => workItem.Id));
        Assert.Empty(context.WorkItems.Single(workItem => workItem.Id == 2).Comments);
        Assert.Equal(
            [new WorkItemContextSkip(2, 1, "comments_unavailable"),
             new WorkItemContextSkip(3, 1, "work_item_unavailable"),
             new WorkItemContextSkip(4, 1, "work_item_unavailable")],
            context.SkippedWorkItems!.OrderBy(skip => skip.WorkItemId));
    }

    [Fact]
    public async Task GetContextAsync_still_fails_when_pr_linked_item_or_its_comments_are_unavailable()
    {
        var missing = new FakeWorkItemGraph(Item(1));
        missing.ForbiddenIds.Add(1);
        using var missingClient = missing.CreateClient();
        await Assert.ThrowsAsync<InvalidOperationException>(() => missingClient.GetContextAsync(Candidate(1)));

        var commentFailure = new FakeWorkItemGraph(Item(1));
        commentFailure.CommentFailureIds.Add(1);
        using var commentClient = commentFailure.CreateClient();
        await Assert.ThrowsAsync<InvalidOperationException>(() => commentClient.GetContextAsync(Candidate(1)));
    }

    [Fact]
    public async Task GetContextAsync_with_zero_depth_fetches_only_pr_linked_items()
    {
        var graph = new FakeWorkItemGraph(Item(1, (ChildLink, 2)), Item(2));
        using var client = graph.CreateClient(maxDepth: 0);

        var context = await client.GetContextAsync(Candidate(1));

        Assert.Equal([1], context.WorkItems.Select(workItem => workItem.Id));
        Assert.Equal([1], graph.RequestedIds);
    }

    [Fact]
    public async Task GetContextAsync_maps_comment_author_id_version_and_change_timestamps()
    {
        var item = Item(1);
        item.Fields["System.ChangedDate"] = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var graph = new FakeWorkItemGraph(item);
        var comment = Comment(1, "text", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        comment.CreatedBy.Id = "bot-id";
        comment.Version = 3;
        comment.ModifiedDate = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        graph.Comments[1] = [comment];
        using var client = graph.CreateClient();

        var workItem = Assert.Single((await client.GetContextAsync(Candidate(1))).WorkItems);

        Assert.Equal(new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero), workItem.ChangedAt);
        var mapped = Assert.Single(workItem.Comments);
        Assert.Equal("bot-id", mapped.AuthorId);
        Assert.Equal(3, mapped.Version);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), mapped.ModifiedAt);
    }

    private const string ChildLink = "System.LinkTypes.Hierarchy-Forward";
    private const string ParentLink = "System.LinkTypes.Hierarchy-Reverse";
    private const string RelatedLink = "System.LinkTypes.Related";
    private const string SuccessorLink = "System.LinkTypes.Dependency-Forward";
    private const string PredecessorLink = "System.LinkTypes.Dependency-Reverse";

    private static AzureWorkItem Item(int id, params (string Rel, int Target)[] links) => new()
    {
        Id = id,
        Fields = new Dictionary<string, object>
        {
            ["System.Title"] = $"Item {id}",
            ["System.State"] = "Active"
        },
        Relations = links.Select(link => new AzureRelation
        {
            Rel = link.Rel,
            Url = link.Rel switch
            {
                "ArtifactLink" => $"vstfs:///Git/PullRequestId/project%2Frepo%2F{link.Target}",
                "Hyperlink" => $"https://example.test/_apis/wit/workItems/{link.Target}",
                "AttachedFile" => $"https://dev.azure.com/org/_apis/wit/attachments/{link.Target}",
                _ => $"https://dev.azure.com/org/_apis/wit/workItems/{link.Target}"
            }
        }).ToList()
    };

    private static AzureComment Comment(int id, string text, DateTime createdDate) => new()
    {
        Id = id,
        Text = text,
        CreatedDate = createdDate,
        CreatedBy = new IdentityRef { DisplayName = "Reviewer" }
    };

    private static ReviewCandidate Candidate(params int[] linkedWorkItemIds) => new(
        "https://dev.azure.com/org", "Project", "repo", "repo", new Uri("https://example.test/repo"), 42, "PR",
        "refs/heads/feature", "refs/heads/main", new string('a', 40), "author", [], linkedWorkItemIds);

    private sealed class FakeWorkItemGraph(params AzureWorkItem[] items)
    {
        private readonly Dictionary<int, AzureWorkItem> itemsById = items.ToDictionary(item => item.Id!.Value);

        public HashSet<int> ForbiddenIds { get; } = [];
        public HashSet<int> CommentFailureIds { get; } = [];
        public Dictionary<int, IReadOnlyList<AzureComment>> Comments { get; } = [];
        public List<int[]> Batches { get; } = [];
        public List<int> CommentTops { get; } = [];
        public List<int> RequestedIds => Batches.SelectMany(batch => batch).Distinct().ToList();

        public AzureDevOpsClient CreateClient(int maxDepth = 3, int maxWorkItems = 50) => new(
            new AzureDevOpsSettings
            {
                OrganizationUrl = new Uri("https://dev.azure.com/org"),
                Project = "Project",
                PersonalAccessToken = "token",
                ReviewContextMaxDepth = maxDepth,
                ReviewContextMaxWorkItems = maxWorkItems
            },
            LoadBatch,
            LoadComments);

        private Task<IReadOnlyList<AzureWorkItem?>> LoadBatch(
            string project, IReadOnlyList<int> ids, bool omitUnavailable, CancellationToken cancellationToken)
        {
            Batches.Add(ids.ToArray());
            // Mirrors Azure DevOps: a permission failure rejects the whole batch even with the Omit policy.
            if (ids.Any(ForbiddenIds.Contains))
            {
                throw new InvalidOperationException("TF401232: forbidden");
            }

            if (!omitUnavailable && ids.Any(id => !itemsById.ContainsKey(id)))
            {
                throw new InvalidOperationException("TF401232: not found");
            }

            IReadOnlyList<AzureWorkItem?> result = ids
                .Select(id => itemsById.TryGetValue(id, out var item) ? item : null)
                .ToArray();
            return Task.FromResult(result);
        }

        private Task<IReadOnlyList<AzureComment>> LoadComments(
            string project, int id, int top, CancellationToken cancellationToken)
        {
            CommentTops.Add(top);
            if (CommentFailureIds.Contains(id))
            {
                throw new InvalidOperationException("comments forbidden");
            }

            return Task.FromResult(Comments.TryGetValue(id, out var comments) ? comments : []);
        }
    }

    private static GitPullRequest PullRequest(string headSha, string? targetSha) => new()
    {
        PullRequestId = 42,
        Title = "PR",
        SourceRefName = "refs/heads/feature",
        TargetRefName = "refs/heads/main",
        Repository = new GitRepository
        {
            Id = Guid.NewGuid(),
            Name = "repo",
            RemoteUrl = "https://example.test/repo"
        },
        CreatedBy = new IdentityRef { Id = "author" },
        LastMergeSourceCommit = new GitCommitRef { CommitId = headSha },
        LastMergeTargetCommit = targetSha is null ? null : new GitCommitRef { CommitId = targetSha }
    };
}