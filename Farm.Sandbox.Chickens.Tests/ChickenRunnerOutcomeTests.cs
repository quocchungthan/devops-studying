using Farm.Core.Chickens;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Farm.Sandbox.Chickens.Tests;

public sealed class ChickenRunnerOutcomeTests
{
    [Fact]
    public async Task Candidate_skip_is_logged_with_safe_reason_and_next_candidate_is_processed()
    {
        var logger = new CapturingLogger();
        var candidate = new ReviewCandidate(
            "org", "project", "repo", "repo", new Uri("https://example.test/repo"), 2, "PR",
            "refs/heads/feature", "refs/heads/main", new string('a', 40), "author", [], []);
        var runner = new ChickenRunner(
            new ContextSource(candidate),
            new UnusedWorkspaceManager(),
            new UnusedBrain(),
            new UnusedAttemptStore(),
            new ChickenOptions { ArtifactsPath = Path.GetTempPath(), QuietPeriod = TimeSpan.Zero },
            SensitiveDataRedactor.Empty,
            SensitiveContentScanner.Empty,
            logger);

        var result = await runner.RunOnceAsync(CancellationToken.None);

        var skipped = Assert.Single(logger.Entries, entry => entry.EventId.Name == "candidate_skipped");
        Assert.Contains("pull_request_id=1", skipped.Message);
        Assert.Contains("reason=repository_metadata_missing", skipped.Message);
        Assert.Contains(logger.Entries, entry => entry.EventId.Name == "candidate_deferred" && entry.Message.Contains("pull_request_id=2", StringComparison.Ordinal));
        Assert.Equal(1, result.DeferredCount);
    }

    [Fact]
    public void Failed_brain_outcome_is_preserved_even_without_a_patch()
    {
        var failed = new ReviewOutcome(ReviewOutcomeKind.Failed, "empty response");

        var outcome = ChickenRunner.DetermineOutcome(failed, string.Empty, new ValidationResult(false, -1, "not run"));

        Assert.Equal(failed, outcome);
    }

    [Fact]
    public void Changes_require_successful_configured_validation()
    {
        var explanation = new ReviewOutcome(ReviewOutcomeKind.ExplanationOnly, "done");

        var outcome = ChickenRunner.DetermineOutcome(explanation, "patch", new ValidationResult(false, -1, "No validation commands are configured."));

        Assert.Equal(ReviewOutcomeKind.Failed, outcome.Kind);
    }

    private sealed class ContextSource(ReviewCandidate candidate) : IReviewContextSource
    {
        public Task<string> GetCurrentUserIdAsync(CancellationToken cancellationToken = default) => Task.FromResult("current-user");
        public Task<ReviewCandidateDiscoveryResult> GetCandidatesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReviewCandidateDiscoveryResult([candidate], [new ReviewCandidateSkip(1, "repository_metadata_missing")]));
        public Task<ReviewContext> GetContextAsync(ReviewCandidate value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class UnusedWorkspaceManager : IRepositoryWorkspaceManager
    {
        public Task<RepositoryWorkspace> PrepareAsync(ReviewCandidate candidate, FeedbackFingerprint fingerprint, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ValidationResult> VerifyReadyAsync(RepositoryWorkspace workspace, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> CreatePatchAsync(RepositoryWorkspace workspace, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ValidationResult> ValidateAsync(RepositoryWorkspace workspace, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PublicationResult> CommitAndPushAsync(RepositoryWorkspace workspace, ReviewCandidate candidate, FeedbackFingerprint fingerprint, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CleanupAsync(RepositoryWorkspace workspace, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class UnusedBrain : IReviewBrain
    {
        public Task<ReviewOutcome> ResolveAsync(CopilotReviewRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class UnusedAttemptStore : IAttemptStore
    {
        public Task<bool> HasCompletedAttemptAsync(AttemptKey key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Lease?> TryAcquireLeaseAsync(AttemptKey key, string ownerId, TimeSpan duration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Lease?> RenewLeaseAsync(Lease lease, TimeSpan duration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CompleteAsync(Lease lease, AttemptState attempt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReleaseAsync(Lease lease, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AttemptBaseline?> GetBaselineAsync(AttemptKey key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> AdoptLegacyCompletionAsync(AttemptKey key, IReadOnlyDictionary<int, string> workItemHashes, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class CapturingLogger : ILogger<ChickenRunner>
    {
        public List<LogEntry> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add(new LogEntry(formatter(state, exception), eventId));
    }

    private sealed record LogEntry(string Message, EventId EventId);
}