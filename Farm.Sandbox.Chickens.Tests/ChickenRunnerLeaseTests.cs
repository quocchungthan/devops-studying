using Farm.Core.Chickens;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.Sandbox.Chickens.Tests;

public sealed class ChickenRunnerLeaseTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"chicken-runner-{Guid.NewGuid():N}");

    [Fact]
    public async Task Cancellation_releases_owner_lease_with_non_cancelable_token()
    {
        var candidate = Candidate();
        var store = new RecordingAttemptStore();
        var brain = new CancelingBrain();
        var runner = new ChickenRunner(
            new ContextSource(candidate),
            new WorkspaceManager(root),
            brain,
            store,
            new ChickenOptions
            {
                ArtifactsPath = Path.Combine(root, "artifacts"),
                QuietPeriod = TimeSpan.Zero,
                LeaseDuration = TimeSpan.FromMinutes(10)
            },
            SensitiveDataRedactor.Empty,
            SensitiveContentScanner.Empty,
            NullLogger<ChickenRunner>.Instance);
        using var cancellation = new CancellationTokenSource();
        var run = runner.RunOnceAsync(cancellation.Token);
        await brain.Started.Task;

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.NotNull(store.Acquired);
        Assert.Equal(store.Acquired, store.Released);
        Assert.False(store.ReleaseToken.CanBeCanceled);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private static ReviewCandidate Candidate() => new(
        "org", "project", "repo", "repo", new Uri("https://example.test/repo"), 1, "PR",
        "refs/heads/feature", "refs/heads/main", new string('a', 40), "author",
        [new FeedbackThread(1, false, null,
            [new FeedbackComment(1, "reviewer", "Reviewer", "fix", DateTimeOffset.UtcNow.AddMinutes(-1))])],
        [], new string('b', 40));

    private sealed class ContextSource(ReviewCandidate candidate) : IReviewContextSource
    {
        public Task<string> GetCurrentUserIdAsync(CancellationToken cancellationToken = default) => Task.FromResult("author");
        public Task<ReviewCandidateDiscoveryResult> GetCandidatesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReviewCandidateDiscoveryResult([candidate], []));
        public Task<ReviewContext> GetContextAsync(ReviewCandidate value, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReviewContext(value, []));
    }

    private sealed class WorkspaceManager(string rootPath) : IRepositoryWorkspaceManager
    {
        public Task<RepositoryWorkspace> PrepareAsync(
            ReviewCandidate candidate,
            FeedbackFingerprint fingerprint,
            CancellationToken cancellationToken = default)
        {
            var path = Path.Combine(rootPath, "worktree");
            Directory.CreateDirectory(path);
            return Task.FromResult(new RepositoryWorkspace(
                rootPath, path, "chickens/test", "refs/heads/feature", new string('a', 40), new string('b', 40)));
        }

        public Task<ValidationResult> VerifyReadyAsync(
            RepositoryWorkspace workspace,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ValidationResult(true, 0, string.Empty));
        public Task<string> CreatePatchAsync(RepositoryWorkspace workspace, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);
        public Task<ValidationResult> ValidateAsync(RepositoryWorkspace workspace, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ValidationResult(true, 0, string.Empty));
        public Task<PublicationResult> CommitAndPushAsync(
            RepositoryWorkspace workspace,
            ReviewCandidate candidate,
            FeedbackFingerprint fingerprint,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new PublicationResult(false, false, null, "Not expected."));
        public Task CleanupAsync(RepositoryWorkspace workspace, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class CancelingBrain : IReviewBrain
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ReviewOutcome> ResolveAsync(
            CopilotReviewRequest request,
            CancellationToken cancellationToken = default)
        {
            Started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Cancellation was expected.");
        }
    }

    private sealed class RecordingAttemptStore : IAttemptStore
    {
        public Lease? Acquired { get; private set; }
        public Lease? Released { get; private set; }
        public CancellationToken ReleaseToken { get; private set; }

        public Task<bool> HasCompletedAttemptAsync(AttemptKey key, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<Lease?> TryAcquireLeaseAsync(
            AttemptKey key,
            string ownerId,
            TimeSpan duration,
            CancellationToken cancellationToken = default)
        {
            Acquired = new Lease(ownerId, key, DateTimeOffset.UtcNow.Add(duration));
            return Task.FromResult<Lease?>(Acquired);
        }

        public Task<Lease?> RenewLeaseAsync(
            Lease lease,
            TimeSpan duration,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Lease?>(lease with { ExpiresAt = DateTimeOffset.UtcNow.Add(duration) });

        public Task CompleteAsync(Lease lease, AttemptState attempt, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ReleaseAsync(Lease lease, CancellationToken cancellationToken = default)
        {
            Released = lease;
            ReleaseToken = cancellationToken;
            return Task.CompletedTask;
        }

        public Task<AttemptBaseline?> GetBaselineAsync(AttemptKey key, CancellationToken cancellationToken = default) =>
            Task.FromResult<AttemptBaseline?>(null);

        public Task<bool> AdoptLegacyCompletionAsync(
            AttemptKey key,
            IReadOnlyDictionary<int, string> workItemHashes,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }
}