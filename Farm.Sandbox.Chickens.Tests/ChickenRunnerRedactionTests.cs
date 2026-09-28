using Farm.Core.Chickens;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.Sandbox.Chickens.Tests;

public sealed class ChickenRunnerRedactionTests : IDisposable
{
    private const string Secret = "artifact-secret-sentinel";
    private readonly string root = Path.Combine(Path.GetTempPath(), $"chicken-redaction-{Guid.NewGuid():N}");

    [Fact]
    public async Task Configured_secrets_never_appear_in_artifacts_results_or_validation_output()
    {
        var candidate = Candidate();
        var runner = new ChickenRunner(
            new ContextSource(candidate),
            new WorkspaceManager(root),
            new Brain(),
            new AttemptStore(),
            new ChickenOptions
            {
                ArtifactsPath = Path.Combine(root, "artifacts"),
                QuietPeriod = TimeSpan.Zero,
                LeaseDuration = TimeSpan.FromMinutes(10)
            },
            new SensitiveDataRedactor([Secret]),
            SensitiveContentScanner.Empty,
            NullLogger<ChickenRunner>.Instance);

        await runner.RunOnceAsync(CancellationToken.None);

        var artifacts = Directory.GetFiles(Path.Combine(root, "artifacts"), "*", SearchOption.AllDirectories);
        Assert.Contains(artifacts, path => Path.GetFileName(path) == "validation.txt");
        Assert.Contains(artifacts, path => Path.GetFileName(path) == "result.json");
        foreach (var artifact in artifacts)
        {
            var content = await File.ReadAllTextAsync(artifact);
            Assert.DoesNotContain(Secret, content);
        }
        Assert.Contains(artifacts, path => File.ReadAllText(path).Contains("[REDACTED]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Deferred_preparation_stops_before_copilot_and_remains_retryable()
    {
        var candidate = Candidate();
        var brain = new RecordingBrain();
        var store = new AttemptStore();
        var runner = new ChickenRunner(
            new ContextSource(candidate),
            new DeferredWorkspaceManager(),
            brain,
            store,
            new ChickenOptions
            {
                ArtifactsPath = Path.Combine(root, "deferred-artifacts"),
                QuietPeriod = TimeSpan.Zero,
                LeaseDuration = TimeSpan.FromMinutes(10)
            },
            SensitiveDataRedactor.Empty,
            SensitiveContentScanner.Empty,
            NullLogger<ChickenRunner>.Instance);

        await runner.RunOnceAsync(CancellationToken.None);

        var resultPath = Directory.GetFiles(Path.Combine(root, "deferred-artifacts"), "result.json", SearchOption.AllDirectories).Single();
        Assert.Contains("Deferred", await File.ReadAllTextAsync(resultPath));
        Assert.False(brain.WasCalled);
        Assert.True(store.WasReleased);
        Assert.False(store.WasCompleted);
    }

    [Fact]
    public async Task High_confidence_secret_is_rejected_before_artifact_persistence()
    {
        var token = "github_" + "pat_" + new string('A', 24);
        var candidate = Candidate() with { Title = token };
        var artifactsPath = Path.Combine(root, "scanner-artifacts");
        var runner = new ChickenRunner(
            new ContextSource(candidate),
            new WorkspaceManager(root),
            new RecordingBrain(),
            new AttemptStore(),
            new ChickenOptions
            {
                ArtifactsPath = artifactsPath,
                QuietPeriod = TimeSpan.Zero,
                LeaseDuration = TimeSpan.FromMinutes(10)
            },
            SensitiveDataRedactor.Empty,
            new SensitiveContentScanner([]),
            NullLogger<ChickenRunner>.Instance);

        await runner.RunOnceAsync(CancellationToken.None);

        var artifacts = Directory.GetFiles(artifactsPath, "*", SearchOption.AllDirectories);
        Assert.DoesNotContain(artifacts, path => Path.GetFileName(path) == "context.json");
        Assert.All(artifacts, path => Assert.DoesNotContain(token, File.ReadAllText(path), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Work_item_text_is_redacted_before_artifacts_and_copilot()
    {
        var candidate = Candidate();
        var brain = new RecordingBrain();
        var workItem = new WorkItemContext(
            7, $"title {Secret}", "Active", $"description {Secret}",
            [new WorkItemComment(1, "Reviewer", $"comment {Secret}", DateTimeOffset.UtcNow)],
            [], [], "Bug", $"repro {Secret}", $"criteria {Secret}", 2, "linked to PR #1 > child of #5");
        var artifactsPath = Path.Combine(root, "work-item-artifacts");
        var runner = new ChickenRunner(
            new ContextSource(candidate, [workItem]),
            new WorkspaceManager(root),
            brain,
            new AttemptStore(),
            new ChickenOptions
            {
                ArtifactsPath = artifactsPath,
                QuietPeriod = TimeSpan.Zero,
                LeaseDuration = TimeSpan.FromMinutes(10)
            },
            new SensitiveDataRedactor([Secret]),
            SensitiveContentScanner.Empty,
            NullLogger<ChickenRunner>.Instance);

        await runner.RunOnceAsync(CancellationToken.None);

        var sent = Assert.Single(brain.Request!.Context.WorkItems);
        Assert.All(
            new[] { sent.Title, sent.Description, sent.ReproSteps, sent.AcceptanceCriteria, sent.Comments[0].Content },
            value => Assert.Equal("[REDACTED]", value!.Split(' ')[1]));
        Assert.Equal(2, sent.Depth);
        Assert.Equal("linked to PR #1 > child of #5", sent.RelationPath);
        var contextJson = await File.ReadAllTextAsync(
            Directory.GetFiles(artifactsPath, "context.json", SearchOption.AllDirectories).Single());
        Assert.DoesNotContain(Secret, contextJson);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private static ReviewCandidate Candidate() => new(
        "org", "project", "repo", "repo", new Uri("https://example.test/repo"), 1, $"PR {Secret}",
        "refs/heads/feature", "refs/heads/main", new string('a', 40), "author",
        [new FeedbackThread(1, false, null,
            [new FeedbackComment(1, "reviewer", "Reviewer", $"fix {Secret}", DateTimeOffset.UtcNow.AddHours(-2))])],
        [], new string('b', 40));

    private sealed class ContextSource(ReviewCandidate candidate, IReadOnlyList<WorkItemContext>? workItems = null) : IReviewContextSource
    {
        public Task<string> GetCurrentUserIdAsync(CancellationToken cancellationToken = default) => Task.FromResult("author");
        public Task<ReviewCandidateDiscoveryResult> GetCandidatesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReviewCandidateDiscoveryResult([candidate], []));
        public Task<ReviewContext> GetContextAsync(ReviewCandidate value, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReviewContext(value, workItems ?? []));
    }

    private sealed class WorkspaceManager(string rootPath) : IRepositoryWorkspaceManager
    {
        public Task<RepositoryWorkspace> PrepareAsync(ReviewCandidate candidate, FeedbackFingerprint fingerprint, CancellationToken cancellationToken = default)
        {
            var cache = Path.Combine(rootPath, "cache");
            var worktree = Path.Combine(rootPath, "worktree");
            Directory.CreateDirectory(cache);
            Directory.CreateDirectory(worktree);
            return Task.FromResult(new RepositoryWorkspace(cache, worktree, "chickens/test", candidate.SourceRef, candidate.HeadSha, candidate.TargetSha!));
        }

        public Task<ValidationResult> VerifyReadyAsync(RepositoryWorkspace workspace, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ValidationResult(true, 0, string.Empty));
        public Task<string> CreatePatchAsync(RepositoryWorkspace workspace, CancellationToken cancellationToken = default) =>
            Task.FromResult($"patch {Secret}");
        public Task<ValidationResult> ValidateAsync(RepositoryWorkspace workspace, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ValidationResult(true, 0, $"validation {Secret}"));
        public Task<PublicationResult> CommitAndPushAsync(RepositoryWorkspace workspace, ReviewCandidate candidate, FeedbackFingerprint fingerprint, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PublicationResult(false, false, null, $"publication {Secret}"));
        public Task CleanupAsync(RepositoryWorkspace workspace, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Brain : IReviewBrain
    {
        public Task<ReviewOutcome> ResolveAsync(CopilotReviewRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReviewOutcome(ReviewOutcomeKind.ExplanationOnly, $"summary {Secret}", $"transcript {Secret}"));
    }

    private sealed class RecordingBrain : IReviewBrain
    {
        public bool WasCalled { get; private set; }
        public CopilotReviewRequest? Request { get; private set; }

        public Task<ReviewOutcome> ResolveAsync(CopilotReviewRequest request, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            Request = request;
            return Task.FromResult(new ReviewOutcome(ReviewOutcomeKind.ExplanationOnly, "unexpected"));
        }
    }

    private sealed class DeferredWorkspaceManager : IRepositoryWorkspaceManager
    {
        public Task<RepositoryWorkspace> PrepareAsync(ReviewCandidate candidate, FeedbackFingerprint fingerprint, CancellationToken cancellationToken = default) =>
            throw new ReviewDeferredException("source repository is unavailable");
        public Task<ValidationResult> VerifyReadyAsync(RepositoryWorkspace workspace, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> CreatePatchAsync(RepositoryWorkspace workspace, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ValidationResult> ValidateAsync(RepositoryWorkspace workspace, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PublicationResult> CommitAndPushAsync(RepositoryWorkspace workspace, ReviewCandidate candidate, FeedbackFingerprint fingerprint, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CleanupAsync(RepositoryWorkspace workspace, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class AttemptStore : IAttemptStore
    {
        public bool WasReleased { get; private set; }
        public bool WasCompleted { get; private set; }

        public Task<bool> HasCompletedAttemptAsync(AttemptKey key, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<Lease?> TryAcquireLeaseAsync(AttemptKey key, string ownerId, TimeSpan duration, CancellationToken cancellationToken = default) =>
            Task.FromResult<Lease?>(new Lease(ownerId, key, DateTimeOffset.UtcNow.Add(duration)));
        public Task<Lease?> RenewLeaseAsync(Lease lease, TimeSpan duration, CancellationToken cancellationToken = default) =>
            Task.FromResult<Lease?>(lease with { ExpiresAt = DateTimeOffset.UtcNow.Add(duration) });
        public Task CompleteAsync(Lease lease, AttemptState attempt, CancellationToken cancellationToken = default)
        {
            WasCompleted = true;
            return Task.CompletedTask;
        }

        public Task ReleaseAsync(Lease lease, CancellationToken cancellationToken = default)
        {
            WasReleased = true;
            return Task.CompletedTask;
        }

        public Task<AttemptBaseline?> GetBaselineAsync(AttemptKey key, CancellationToken cancellationToken = default) =>
            Task.FromResult<AttemptBaseline?>(null);
        public Task<bool> AdoptLegacyCompletionAsync(AttemptKey key, IReadOnlyDictionary<int, string> workItemHashes, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }
}