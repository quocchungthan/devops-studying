using Farm.Core.Chickens;
using Farm.Core.Cows;
using Xunit;

namespace Farm.Sandbox.Cows.Tests;

public sealed class CowSyncPolicyTests
{
    private static readonly DateTimeOffset Synced = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    [Fact]
    public void Unchanged_fingerprint_with_matching_note_is_kept() =>
        AssertDecision(CowSyncAction.Keep, CowSyncReason.Unchanged,
            CowSyncPolicy.Decide(false, "fp", true, Record("fp", "hash"), "hash"));

    [Fact]
    public void New_item_is_patched() =>
        AssertDecision(CowSyncAction.Patch, CowSyncReason.New, CowSyncPolicy.Decide(false, "fp", true, null, "hash"));

    [Fact]
    public void Changed_fingerprint_is_patched() =>
        AssertDecision(CowSyncAction.Patch, CowSyncReason.CloudChanged,
            CowSyncPolicy.Decide(false, "fp-2", true, Record("fp-1", "hash"), "hash"));

    [Fact]
    public void Missing_note_is_patched() =>
        AssertDecision(CowSyncAction.Patch, CowSyncReason.NoteMissing,
            CowSyncPolicy.Decide(false, "fp", true, Record("fp", "hash"), null));

    [Fact]
    public void Locally_edited_note_is_patched_because_cloud_wins() =>
        AssertDecision(CowSyncAction.Patch, CowSyncReason.NoteEditedLocally,
            CowSyncPolicy.Decide(false, "fp", true, Record("fp", "hash"), "edited"));

    [Fact]
    public void Removed_item_is_marked_once_then_kept()
    {
        AssertDecision(CowSyncAction.MarkRemoved, CowSyncReason.RemovedFromCloud,
            CowSyncPolicy.Decide(true, null, true, Record("fp", "hash"), "hash"));
        AssertDecision(CowSyncAction.Keep, CowSyncReason.AlreadyRemoved,
            CowSyncPolicy.Decide(true, null, true, Record("fp", "hash") with { RemovedAt = Synced }, "hash"));
    }

    [Fact]
    public void Out_of_scope_item_is_marked_once_then_kept()
    {
        AssertDecision(CowSyncAction.MarkRemoved, CowSyncReason.OutOfScope, CowSyncPolicy.DecideOutOfScope(Record("fp", "hash")));
        AssertDecision(CowSyncAction.MarkRemoved, CowSyncReason.OutOfScope, CowSyncPolicy.DecideOutOfScope(null));
        AssertDecision(CowSyncAction.Keep, CowSyncReason.AlreadyRemoved,
            CowSyncPolicy.DecideOutOfScope(Record("fp", "hash") with { RemovedAt = Synced }));
    }

    [Fact]
    public void Restored_item_clears_marker_then_falls_through_to_normal_checks()
    {
        var removed = Record("fp", "hash") with { RemovedAt = Synced };

        var unchanged = CowSyncPolicy.Decide(false, "fp", true, removed, "hash");
        var changed = CowSyncPolicy.Decide(false, "fp-2", true, removed, "hash");
        var degraded = CowSyncPolicy.Decide(false, "fp", false, removed, "hash");

        Assert.Equal(new CowSyncDecision(CowSyncAction.Keep, CowSyncReason.Unchanged, ClearRemovedMarker: true), unchanged);
        Assert.Equal(new CowSyncDecision(CowSyncAction.Patch, CowSyncReason.CloudChanged, ClearRemovedMarker: true), changed);
        Assert.Equal(new CowSyncDecision(CowSyncAction.Defer, CowSyncReason.CloudPartiallyUnavailable, ClearRemovedMarker: true), degraded);
        Assert.False(CowSyncPolicy.Decide(false, "fp", true, Record("fp", "hash"), "hash").ClearRemovedMarker);
    }

    [Fact]
    public void Partial_cloud_snapshot_defers_any_existing_note_but_still_creates_a_new_note()
    {
        AssertDecision(CowSyncAction.Defer, CowSyncReason.CloudPartiallyUnavailable,
            CowSyncPolicy.Decide(false, "fp-2", false, Record("fp-1", "hash"), "hash"));
        AssertDecision(CowSyncAction.Defer, CowSyncReason.CloudPartiallyUnavailable,
            CowSyncPolicy.Decide(false, "fp", false, null, "hand-written-hash"));
        AssertDecision(CowSyncAction.Patch, CowSyncReason.New, CowSyncPolicy.Decide(false, "fp", false, null, null));
    }

    [Fact]
    public void Note_older_than_max_age_is_patched_even_when_fingerprint_is_unchanged()
    {
        var maxAge = TimeSpan.FromHours(24);
        var stored = Record("fp", "hash");

        AssertDecision(CowSyncAction.Patch, CowSyncReason.NoteExpired,
            CowSyncPolicy.Decide(false, "fp", true, stored, "hash", Synced + maxAge, maxAge));
        AssertDecision(CowSyncAction.Keep, CowSyncReason.Unchanged,
            CowSyncPolicy.Decide(false, "fp", true, stored, "hash", Synced + maxAge - TimeSpan.FromMinutes(1), maxAge));
        AssertDecision(CowSyncAction.Keep, CowSyncReason.Unchanged,
            CowSyncPolicy.Decide(false, "fp", true, stored, "hash", Synced + TimeSpan.FromDays(30), TimeSpan.Zero));
        AssertDecision(CowSyncAction.Defer, CowSyncReason.CloudPartiallyUnavailable,
            CowSyncPolicy.Decide(false, "fp", false, stored, "hash", Synced + maxAge, maxAge));
    }

    [Fact]
    public void Removed_state_and_missing_snapshot_count_as_removed()
    {
        Assert.True(CowSyncPolicy.IsRemoved(null));
        Assert.True(CowSyncPolicy.IsRemoved(Snapshot(1, 1, "Removed")));
        Assert.False(CowSyncPolicy.IsRemoved(Snapshot(1, 1, "Done")));
    }

    [Fact]
    public void Fingerprint_is_order_independent_and_tracks_revision_and_pull_request_state()
    {
        var snapshot = Snapshot(5, 3, "Active", [11, 10]);
        var reordered = snapshot with { LinkedPullRequestIds = [10, 11] };
        var active = new Dictionary<int, CloudPullRequestState?> { [10] = Pr(10, "Active"), [11] = Pr(11, "Active") };
        var completed = new Dictionary<int, CloudPullRequestState?> { [10] = Pr(10, "Completed"), [11] = Pr(11, "Active") };

        var baseline = CowSyncPolicy.ComputeFingerprint(snapshot, active, []);

        Assert.StartsWith("cow-v2:", baseline);
        Assert.Equal(baseline, CowSyncPolicy.ComputeFingerprint(reordered, active, []));
        Assert.NotEqual(baseline, CowSyncPolicy.ComputeFingerprint(snapshot, completed, []));
        Assert.NotEqual(baseline, CowSyncPolicy.ComputeFingerprint(snapshot with { Revision = 4 }, active, []));
    }

    [Fact]
    public void Fingerprint_distinguishes_unavailable_pull_request_from_transient_failure()
    {
        var snapshot = Snapshot(5, 3, "Active", [10]);

        var unavailable = CowSyncPolicy.ComputeFingerprint(snapshot, new Dictionary<int, CloudPullRequestState?> { [10] = CloudPullRequestState.Unavailable(10) }, []);
        var transient = CowSyncPolicy.ComputeFingerprint(snapshot, new Dictionary<int, CloudPullRequestState?> { [10] = null }, []);

        Assert.NotEqual(unavailable, transient);
        Assert.Equal(unavailable, CowSyncPolicy.ComputeFingerprint(snapshot, new Dictionary<int, CloudPullRequestState?> { [10] = CloudPullRequestState.Unavailable(10) }, []));
    }

    [Fact]
    public void Fingerprint_tracks_comment_edits_and_is_order_independent()
    {
        var snapshot = Snapshot(5, 3, "Active");
        var none = new Dictionary<int, CloudPullRequestState?>();
        WorkItemComment[] comments = [Comment(1, "first"), Comment(2, "second")];

        var baseline = CowSyncPolicy.ComputeFingerprint(snapshot, none, comments);

        Assert.NotEqual(CowSyncPolicy.ComputeFingerprint(snapshot, none, []), baseline);
        Assert.Equal(baseline, CowSyncPolicy.ComputeFingerprint(snapshot, none, [comments[1], comments[0]]));
        Assert.NotEqual(baseline, CowSyncPolicy.ComputeFingerprint(snapshot, none, [comments[0], Comment(2, "second (edited)")]));
        Assert.NotEqual(baseline, CowSyncPolicy.ComputeFingerprint(snapshot, none, [comments[0], comments[1] with { ModifiedAt = Synced.AddHours(1) }]));
    }

    internal static WorkItemComment Comment(int id, string text) => new(id, "Author", text, Synced);

    internal static CloudWorkItemSnapshot Snapshot(
        int id,
        int revision,
        string state,
        IReadOnlyList<int>? pullRequests = null,
        IReadOnlyList<int>? related = null) =>
        new(revision,
            new WorkItemContext(id, $"Item {id}", state, "description", [], [], [], "Product Backlog Item", ChangedAt: Synced),
            pullRequests ?? [])
        {
            RelatedWorkItemIds = related ?? []
        };

    internal static CloudPullRequestState Pr(int id, string status) => new(id, status, $"PR {id}", new string('a', 40), null, null);

    private static NoteSyncRecord Record(string fingerprint, string hash) => new(1, fingerprint, hash, Synced);

    private static void AssertDecision(CowSyncAction action, CowSyncReason reason, CowSyncDecision decision)
    {
        Assert.Equal(action, decision.Action);
        Assert.Equal(reason, decision.Reason);
    }
}
