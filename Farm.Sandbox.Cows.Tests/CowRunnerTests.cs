using Farm.Core.Chickens;
using Farm.Core.Cows;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.Sandbox.Cows.Tests;

public sealed class CowRunnerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"cow-runner-{Guid.NewGuid():N}");
    private readonly FakeCloud cloud = new();
    private readonly FakeStore store = new();
    private readonly FakeAuthor author = new();

    [Fact]
    public async Task First_run_patches_assigned_items_and_second_run_keeps_them_without_copilot()
    {
        cloud.AssignedIds = [1, 2];
        cloud.Items[1] = CowSyncPolicyTests.Snapshot(1, 1, "Active", [100]);
        cloud.Items[2] = CowSyncPolicyTests.Snapshot(2, 1, "New");
        cloud.PullRequests[100] = CowSyncPolicyTests.Pr(100, "Active");
        var runner = CreateRunner();

        var first = await runner.RunOnceAsync();
        var second = await runner.RunOnceAsync();

        Assert.Equal(new CowCycleResult(2, 0, 2, 0, 0, 0), first);
        Assert.Equal(new CowCycleResult(2, 2, 0, 0, 0, 0), second);
        Assert.Equal(2, author.Requests.Count);
        Assert.Equal([100], author.Requests.Single(request => request.Snapshot.WorkItem.Id == 1).PullRequests.Select(pr => pr.Id));
        var note = await File.ReadAllTextAsync(new CowNoteStore(root).GetNotePath(1));
        Assert.Contains("work-item=1 revision=1", note);
        Assert.Contains("# 1: Item 1", note);
    }

    [Fact]
    public async Task Assigned_items_and_their_one_hop_related_items_are_targeted_once()
    {
        cloud.AssignedIds = [1, 2];
        cloud.Items[1] = CowSyncPolicyTests.Snapshot(1, 1, "Active", related: [10, 2, 11]);
        cloud.Items[2] = CowSyncPolicyTests.Snapshot(2, 1, "Active", related: [10]);
        cloud.Items[10] = CowSyncPolicyTests.Snapshot(10, 1, "Active", related: [20]);
        cloud.Items[11] = CowSyncPolicyTests.Snapshot(11, 1, "Done");
        cloud.Items[20] = CowSyncPolicyTests.Snapshot(20, 1, "Active");
        cloud.Items[99] = CowSyncPolicyTests.Snapshot(99, 1, "Active");
        var runner = CreateRunner();

        var result = await runner.RunOnceAsync();

        Assert.Equal(new CowCycleResult(4, 0, 4, 0, 0, 0), result);
        Assert.Equal([1, 2, 10, 11], author.Requests.Select(request => request.Snapshot.WorkItem.Id).Order());
        Assert.Equal([1, 2], cloud.Requested[0].Order());
        Assert.Equal([10, 11], cloud.Requested[1].Order());
    }

    [Fact]
    public async Task Related_items_are_capped_in_assigned_order()
    {
        cloud.AssignedIds = [2, 1];
        cloud.Items[1] = CowSyncPolicyTests.Snapshot(1, 1, "Active", related: [10, 11]);
        cloud.Items[2] = CowSyncPolicyTests.Snapshot(2, 1, "Active", related: [12]);
        foreach (var id in new[] { 10, 11, 12 })
        {
            cloud.Items[id] = CowSyncPolicyTests.Snapshot(id, 1, "Active");
        }

        var result = await CreateRunner(maxRelated: 2).RunOnceAsync();

        Assert.Equal(4, result.TargetCount);
        Assert.Equal([1, 2, 10, 12], author.Requests.Select(request => request.Snapshot.WorkItem.Id).Order());
    }

    [Fact]
    public async Task Unassigned_item_is_marked_out_of_scope_keeps_its_note_and_is_kept_unchanged_when_reassigned()
    {
        cloud.AssignedIds = [1];
        cloud.Items[1] = CowSyncPolicyTests.Snapshot(1, 1, "Active");
        var runner = CreateRunner();
        await runner.RunOnceAsync();
        var notePath = new CowNoteStore(root).GetNotePath(1);
        var note = await File.ReadAllTextAsync(notePath);
        var requestsBefore = cloud.Requested.Count;

        cloud.AssignedIds = [];
        var unassigned = await runner.RunOnceAsync();
        var again = await runner.RunOnceAsync();

        Assert.Equal(new CowCycleResult(1, 0, 0, 1, 0, 0), unassigned);
        Assert.Equal(new CowCycleResult(1, 1, 0, 0, 0, 0), again);
        Assert.NotNull(store.Records[1].RemovedAt);
        Assert.Equal(note, await File.ReadAllTextAsync(notePath));
        Assert.Single(author.Requests);
        Assert.All(cloud.Requested.Skip(requestsBefore), ids => Assert.DoesNotContain(1, ids));

        cloud.AssignedIds = [1];
        Assert.Equal(new CowCycleResult(1, 1, 0, 0, 0, 0), await runner.RunOnceAsync());
        Assert.Null(store.Records[1].RemovedAt);
        Assert.Single(author.Requests);
        Assert.Equal(note, await File.ReadAllTextAsync(notePath));

        cloud.Items[1] = cloud.Items[1] with { Revision = 2 };
        Assert.Equal(1, (await runner.RunOnceAsync()).PatchedCount);
    }

    [Fact]
    public async Task Assigned_item_missing_from_cloud_is_marked_removed()
    {
        cloud.AssignedIds = [5];

        var result = await CreateRunner().RunOnceAsync();

        Assert.Equal(new CowCycleResult(1, 0, 0, 1, 0, 0), result);
        Assert.NotNull(store.Records[5].RemovedAt);
        Assert.Empty(author.Requests);
    }

    [Fact]
    public async Task Cloud_change_pull_request_change_and_local_edit_each_trigger_a_patch()
    {
        cloud.AssignedIds = [1];
        cloud.Items[1] = CowSyncPolicyTests.Snapshot(1, 1, "Active", [100]);
        cloud.PullRequests[100] = CowSyncPolicyTests.Pr(100, "Active");
        var runner = CreateRunner();
        await runner.RunOnceAsync();

        cloud.Items[1] = cloud.Items[1] with { Revision = 2 };
        Assert.Equal(1, (await runner.RunOnceAsync()).PatchedCount);

        cloud.PullRequests[100] = CowSyncPolicyTests.Pr(100, "Completed");
        Assert.Equal(1, (await runner.RunOnceAsync()).PatchedCount);

        var notePath = new CowNoteStore(root).GetNotePath(1);
        await File.WriteAllTextAsync(notePath, "LOCAL-EDIT-MARKER");
        Assert.Equal(1, (await runner.RunOnceAsync()).PatchedCount);
        Assert.DoesNotContain("LOCAL-EDIT-MARKER", await File.ReadAllTextAsync(notePath));
        Assert.Equal(4, author.Requests.Count);
    }

    [Fact]
    public async Task Comment_edit_triggers_a_patch_with_the_current_comments()
    {
        cloud.AssignedIds = [1];
        cloud.Items[1] = CowSyncPolicyTests.Snapshot(1, 1, "Active");
        cloud.Comments[1] = [CowSyncPolicyTests.Comment(7, "original")];
        var runner = CreateRunner();
        await runner.RunOnceAsync();
        Assert.Equal(0, (await runner.RunOnceAsync()).PatchedCount);

        cloud.Comments[1] = [CowSyncPolicyTests.Comment(7, "edited")];
        Assert.Equal(1, (await runner.RunOnceAsync()).PatchedCount);
        Assert.Equal("edited", author.Requests[^1].Comments.Single().Content);
    }

    [Fact]
    public async Task Note_older_than_max_age_is_regenerated_and_counts_toward_the_patch_cap()
    {
        cloud.AssignedIds = [1, 2];
        cloud.Items[1] = CowSyncPolicyTests.Snapshot(1, 1, "Active");
        cloud.Items[2] = CowSyncPolicyTests.Snapshot(2, 1, "Active");
        var runner = CreateRunner(maxPatches: 1, maxNoteAge: TimeSpan.FromHours(24));
        await runner.RunOnceAsync();
        await runner.RunOnceAsync();
        Assert.Equal(2, author.Requests.Count);

        foreach (var id in new[] { 1, 2 })
        {
            store.Records[id] = store.Records[id] with { SyncedAt = DateTimeOffset.UtcNow.AddHours(-25) };
        }

        var result = await runner.RunOnceAsync();

        Assert.Equal(new CowCycleResult(2, 0, 1, 0, 1, 0), result);
        Assert.Equal(3, author.Requests.Count);
        Assert.Equal(0, (await CreateRunner(maxNoteAge: TimeSpan.Zero).RunOnceAsync()).PatchedCount);
    }

    [Fact]
    public async Task Local_notes_outside_scope_are_marked_out_of_scope_and_keep_their_note()
    {
        var notes = new CowNoteStore(root);
        await notes.WriteAtomicAsync(9, "hand written");
        cloud.AssignedIds = [];
        var runner = CreateRunner();

        var first = await runner.RunOnceAsync();
        var second = await runner.RunOnceAsync();

        Assert.Equal(new CowCycleResult(1, 0, 0, 1, 0, 0), first);
        Assert.Equal(new CowCycleResult(1, 1, 0, 0, 0, 0), second);
        Assert.Equal("hand written", await File.ReadAllTextAsync(notes.GetNotePath(9)));
        Assert.NotNull(store.Records[9].RemovedAt);
        Assert.Empty(author.Requests);
    }

    [Fact]
    public async Task Failed_author_leaves_state_untouched_so_the_next_run_retries()
    {
        cloud.AssignedIds = [1];
        cloud.Items[1] = CowSyncPolicyTests.Snapshot(1, 1, "Active");
        author.Fail = true;
        var runner = CreateRunner();

        Assert.Equal(1, (await runner.RunOnceAsync()).FailedCount);
        Assert.Empty(store.Records);

        author.Fail = false;
        Assert.Equal(1, (await runner.RunOnceAsync()).PatchedCount);
    }

    [Fact]
    public async Task Note_content_is_redacted_and_patches_are_capped_per_run()
    {
        cloud.AssignedIds = [1, 2, 3];
        foreach (var id in cloud.AssignedIds)
        {
            cloud.Items[id] = CowSyncPolicyTests.Snapshot(id, 1, "Active");
        }

        author.Extra = "leaked secret-value-123";
        var runner = CreateRunner(maxPatches: 2);

        var result = await runner.RunOnceAsync();

        Assert.Equal(2, result.PatchedCount);
        Assert.Equal(1, result.DeferredCount);
        var note = await File.ReadAllTextAsync(new CowNoteStore(root).GetNotePath(1));
        Assert.DoesNotContain("secret-value-123", note);
        Assert.Contains("[REDACTED]", note);
    }

    [Fact]
    public async Task Failed_patch_attempts_count_toward_the_patch_cap()
    {
        cloud.AssignedIds = [1, 2, 3];
        foreach (var id in cloud.AssignedIds)
        {
            cloud.Items[id] = CowSyncPolicyTests.Snapshot(id, 1, "Active");
        }

        author.Fail = true;

        var result = await CreateRunner(maxPatches: 2).RunOnceAsync();

        Assert.Equal(new CowCycleResult(3, 0, 0, 0, 1, 2), result);
        Assert.Equal(2, author.Requests.Count);
    }

    [Fact]
    public async Task Transient_pull_request_failure_defers_existing_note()
    {
        cloud.AssignedIds = [1];
        cloud.Items[1] = CowSyncPolicyTests.Snapshot(1, 1, "Active", [100]);
        cloud.PullRequests[100] = CowSyncPolicyTests.Pr(100, "Active");
        var runner = CreateRunner();
        await runner.RunOnceAsync();

        cloud.PullRequests.Remove(100);
        var result = await runner.RunOnceAsync();

        Assert.Equal(1, result.DeferredCount);
        Assert.Single(author.Requests);
    }

    [Fact]
    public async Task Transient_pull_request_failure_defers_a_hand_written_note_without_state()
    {
        var notes = new CowNoteStore(root);
        await notes.WriteAtomicAsync(1, "hand written");
        cloud.AssignedIds = [1];
        cloud.Items[1] = CowSyncPolicyTests.Snapshot(1, 1, "Active", [100]);

        var result = await CreateRunner().RunOnceAsync();

        Assert.Equal(new CowCycleResult(1, 0, 0, 0, 1, 0), result);
        Assert.Equal("hand written", await File.ReadAllTextAsync(notes.GetNotePath(1)));
        Assert.Empty(author.Requests);
    }

    [Fact]
    public async Task Missing_or_forbidden_pull_request_patches_and_records_it_as_unavailable()
    {
        cloud.AssignedIds = [1];
        cloud.Items[1] = CowSyncPolicyTests.Snapshot(1, 1, "Active", [100, 101]);
        cloud.PullRequests[100] = CowSyncPolicyTests.Pr(100, "Active");
        var runner = CreateRunner();
        await runner.RunOnceAsync();

        cloud.PullRequests[100] = CloudPullRequestState.Unavailable(100);
        cloud.PullRequests[101] = CloudPullRequestState.Unavailable(101);
        var result = await runner.RunOnceAsync();

        Assert.Equal(1, result.PatchedCount);
        Assert.Empty(author.Requests[^1].PullRequests);
        Assert.Equal([100, 101], author.Requests[^1].UnavailablePullRequestIds);
        Assert.Equal(new CowCycleResult(1, 1, 0, 0, 0, 0), await runner.RunOnceAsync());
    }

    private CowRunner CreateRunner(int maxPatches = 20, int maxRelated = 50, TimeSpan? maxNoteAge = null)
    {
        var redactor = new SensitiveDataRedactor(["secret-value-123"]);
        var options = new CowOptions
        {
            NotesPath = root, RepositoryPath = root, MaxPatchesPerRun = maxPatches, MaxRelatedItems = maxRelated,
            MaxNoteAge = maxNoteAge ?? TimeSpan.FromHours(24)
        };
        return new CowRunner(
            cloud, store, author, new CowNoteStore(root), options, redactor, SensitiveContentScanner.Empty,
            new CowStatusWriter(Path.Combine(root, "state", "status.json"), redactor), NullLogger<CowRunner>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class FakeCloud : ICloudWorkItemSource
    {
        public IReadOnlyList<int> AssignedIds { get; set; } = [];
        public Dictionary<int, CloudWorkItemSnapshot> Items { get; } = [];
        public Dictionary<int, CloudPullRequestState> PullRequests { get; } = [];
        public Dictionary<int, IReadOnlyList<WorkItemComment>> Comments { get; } = [];
        public List<int[]> Requested { get; } = [];

        public Task<IReadOnlyList<int>> GetAssignedToMeWorkItemIdsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(AssignedIds);

        public Task<IReadOnlyList<CloudWorkItemSnapshot>> GetWorkItemsAsync(IReadOnlyCollection<int> ids, CancellationToken cancellationToken = default)
        {
            Requested.Add(ids.ToArray());
            return Task.FromResult<IReadOnlyList<CloudWorkItemSnapshot>>(ids.Where(Items.ContainsKey).Select(id => Items[id]).ToArray());
        }

        public Task<CloudPullRequestState?> GetPullRequestAsync(int pullRequestId, CancellationToken cancellationToken = default) =>
            Task.FromResult(PullRequests.GetValueOrDefault(pullRequestId));

        public Task<IReadOnlyList<WorkItemComment>> GetCommentsAsync(int workItemId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Comments.GetValueOrDefault(workItemId) ?? []);
    }

    private sealed class FakeStore : INoteSyncStore
    {
        public Dictionary<int, NoteSyncRecord> Records { get; } = [];

        public Task<IReadOnlyDictionary<int, NoteSyncRecord>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<int, NoteSyncRecord>>(new Dictionary<int, NoteSyncRecord>(Records));

        public Task UpsertAsync(NoteSyncRecord record, CancellationToken cancellationToken = default)
        {
            Records[record.WorkItemId] = record;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAuthor : IInvestigationAuthor
    {
        public List<InvestigationRequest> Requests { get; } = [];
        public bool Fail { get; set; }
        public string Extra { get; set; } = string.Empty;

        public Task<InvestigationResult> WriteAsync(InvestigationRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var item = request.Snapshot.WorkItem;
            return Task.FromResult(Fail
                ? InvestigationResult.Failure("boom")
                : InvestigationResult.Success($"# {item.Id}: {item.Title}\n\nStatus: {item.State} rev {request.Snapshot.Revision} {Extra}\n"));
        }
    }
}
