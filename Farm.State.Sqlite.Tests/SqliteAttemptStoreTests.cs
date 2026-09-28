using Farm.Core.Chickens;
using Xunit;

namespace Farm.State.Sqlite.Tests;

public sealed class SqliteAttemptStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"farm-state-{Guid.NewGuid():N}");

    [Fact]
    public async Task Lease_is_exclusive_and_completion_is_idempotent()
    {
        var store = new SqliteAttemptStore(Path.Combine(root, "state.db"));
        var key = new AttemptKey("org", "project", "repo", 1, "sha", "fingerprint");

        var first = await store.TryAcquireLeaseAsync(key, "owner-1", TimeSpan.FromMinutes(5));
        var second = await store.TryAcquireLeaseAsync(key, "owner-2", TimeSpan.FromMinutes(5));

        Assert.NotNull(first);
        Assert.Null(second);
        await store.CompleteAsync(first!, new AttemptState(key, ReviewOutcomeKind.ChangesProduced, DateTimeOffset.UtcNow, root));
        Assert.True(await store.HasCompletedAttemptAsync(key));
        Assert.Null(await store.TryAcquireLeaseAsync(key, "owner-2", TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task Concurrent_acquisition_has_one_winner()
    {
        var store = new SqliteAttemptStore(Path.Combine(root, "race.db"));
        var key = Key();

        var leases = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(index => store.TryAcquireLeaseAsync(key, $"owner-{index}", TimeSpan.FromMinutes(5))));

        Assert.Single(leases, lease => lease is not null);
    }

    [Fact]
    public async Task Expired_lease_can_be_reacquired_but_stale_owner_cannot_complete()
    {
        var store = new SqliteAttemptStore(Path.Combine(root, "expired.db"));
        var key = Key();
        var stale = await store.TryAcquireLeaseAsync(key, "stale", TimeSpan.FromMilliseconds(20));
        await Task.Delay(50);

        var current = await store.TryAcquireLeaseAsync(key, "current", TimeSpan.FromMinutes(1));

        Assert.NotNull(current);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CompleteAsync(
            stale!, new AttemptState(key, ReviewOutcomeKind.ChangesProduced, DateTimeOffset.UtcNow, root)));
    }

    [Fact]
    public async Task Live_owner_can_renew_lease()
    {
        var store = new SqliteAttemptStore(Path.Combine(root, "renew.db"));
        var lease = await store.TryAcquireLeaseAsync(Key(), "owner", TimeSpan.FromMinutes(1));

        var renewed = await store.RenewLeaseAsync(lease!, TimeSpan.FromMinutes(5));

        Assert.NotNull(renewed);
        Assert.True(renewed!.ExpiresAt > lease!.ExpiresAt);
    }

    [Fact]
    public async Task Legacy_completion_is_adopted_once_and_then_superseded_by_work_item_aware_attempts()
    {
        var path = Path.Combine(root, "legacy.db");
        var store = new SqliteAttemptStore(path);
        var legacy = Key();
        var legacyLease = await store.TryAcquireLeaseAsync(legacy, "old", TimeSpan.FromMinutes(5));
        await store.CompleteAsync(legacyLease!, new AttemptState(legacy, ReviewOutcomeKind.ExplanationOnly, DateTimeOffset.UtcNow, root));
        store = new SqliteAttemptStore(path);
        var first = legacy with { WorkItemFingerprint = "wi-v1:first" };
        var second = legacy with { WorkItemFingerprint = "wi-v1:second" };

        Assert.Equal(new AttemptBaseline(null, null), await store.GetBaselineAsync(first));
        Assert.True(await store.AdoptLegacyCompletionAsync(first, new Dictionary<int, string> { [7] = "h7" }));
        Assert.False(await store.AdoptLegacyCompletionAsync(second, new Dictionary<int, string>()));
        Assert.Null(await store.TryAcquireLeaseAsync(first, "owner", TimeSpan.FromMinutes(5)));

        var baseline = await store.GetBaselineAsync(second);
        Assert.Equal("wi-v1:first", baseline!.WorkItemFingerprint);
        Assert.Equal("h7", baseline.WorkItemHashes![7]);

        var lease = await store.TryAcquireLeaseAsync(second, "owner", TimeSpan.FromMinutes(5));
        Assert.NotNull(lease);
        await store.CompleteAsync(lease!, new AttemptState(second, ReviewOutcomeKind.ChangesProduced, DateTimeOffset.UtcNow.AddSeconds(1), root, new Dictionary<int, string> { [7] = "h7b" }));
        Assert.Equal("wi-v1:second", (await store.GetBaselineAsync(first))!.WorkItemFingerprint);
        Assert.True(await store.HasCompletedAttemptAsync(legacy));
    }

    [Fact]
    public async Task Baseline_is_scoped_to_head_sha_and_pr_thread_fingerprint()
    {
        var store = new SqliteAttemptStore(Path.Combine(root, "scope.db"));
        var key = Key() with { WorkItemFingerprint = "wi-v1:a" };
        var lease = await store.TryAcquireLeaseAsync(key, "owner", TimeSpan.FromMinutes(5));
        await store.CompleteAsync(lease!, new AttemptState(key, ReviewOutcomeKind.ExplanationOnly, DateTimeOffset.UtcNow, root, new Dictionary<int, string>()));

        Assert.Null(await store.GetBaselineAsync(key with { HeadSha = "other" }));
        Assert.Null(await store.GetBaselineAsync(key with { FeedbackFingerprint = "fingerprint2" }));
        Assert.NotNull(await store.GetBaselineAsync(key with { WorkItemFingerprint = null }));
    }

    private static AttemptKey Key() => new("org", "project", "repo", 1, "sha", "fingerprint");

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}