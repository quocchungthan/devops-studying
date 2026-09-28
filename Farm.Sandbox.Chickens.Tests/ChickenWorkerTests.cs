using System.Text.Json;
using Farm.Core.Chickens;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Farm.Sandbox.Chickens.Tests;

public sealed class ChickenWorkerTests
{
    private static readonly string[] Secrets =
    [
        "azure-pat-sentinel",
        "copilot-token-sentinel",
        "git-secret-sentinel"
    ];

    [Fact]
    public async Task Cycle_failure_logs_only_redacted_exception_text()
    {
        var redactor = new SensitiveDataRedactor(Secrets);
        var logger = new CapturingLogger<ChickenWorker>();
        var options = new ChickenOptions
        {
            ArtifactsPath = Path.GetTempPath(),
            SchedulePeriod = TimeSpan.FromHours(1),
            RunImmediately = true
        };
        var runner = new ChickenRunner(
            new ThrowingContextSource(string.Join(" ", Secrets)),
            new UnusedWorkspaceManager(),
            new UnusedBrain(),
            new UnusedAttemptStore(),
            options,
            redactor,
            SensitiveContentScanner.Empty,
            new CapturingLogger<ChickenRunner>());
        var worker = new ChickenWorker(
            runner,
            options,
            redactor,
            new ChickenStatusWriter(Path.Combine(Path.GetTempPath(), $"status-{Guid.NewGuid():N}.json"), redactor),
            logger);

        await worker.StartAsync(CancellationToken.None);
        await logger.WaitForRedactedLogAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None);

        var captured = string.Join(Environment.NewLine, logger.Entries.SelectMany(entry =>
            new[] { entry.Message, entry.Exception?.ToString() ?? string.Empty }));
        Assert.Contains("[REDACTED]", captured);
        Assert.All(Secrets, secret => Assert.DoesNotContain(secret, captured, StringComparison.Ordinal));
        Assert.All(logger.Entries, entry => Assert.Null(entry.Exception));
        Assert.Contains(logger.Entries, entry => entry.EventId.Name == "cycle_failed");
    }

    [Fact]
    public async Task Cancellation_persists_stopped_status()
    {
        var path = Path.Combine(Path.GetTempPath(), $"status-{Guid.NewGuid():N}.json");
        var options = new ChickenOptions { ArtifactsPath = Path.GetTempPath(), StatusPath = path, SchedulePeriod = TimeSpan.FromHours(1), RunImmediately = false };
        var redactor = SensitiveDataRedactor.Empty;
        var runner = new ChickenRunner(
            new ThrowingContextSource("unused"), new UnusedWorkspaceManager(), new UnusedBrain(), new UnusedAttemptStore(), options,
            redactor, SensitiveContentScanner.Empty, new CapturingLogger<ChickenRunner>());
        var worker = new ChickenWorker(runner, options, redactor, new ChickenStatusWriter(path, redactor), new CapturingLogger<ChickenWorker>());

        await worker.StartAsync(CancellationToken.None);
        for (var attempt = 0; attempt < 500 && !File.Exists(path); attempt++)
        {
            await Task.Delay(10);
        }
        Assert.True(File.Exists(path));
        await worker.StopAsync(CancellationToken.None);

        var status = JsonSerializer.Deserialize<ChickenStatusSnapshot>(await File.ReadAllTextAsync(path), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("stopped", status!.ServiceStatus);
        Assert.NotNull(status.LastHeartbeatAt);
        File.Delete(path);
    }

    private sealed class ThrowingContextSource(string message) : IReviewContextSource
    {
        public Task<string> GetCurrentUserIdAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<string>(new InvalidOperationException(message));

        public Task<ReviewCandidateDiscoveryResult> GetCandidatesAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ReviewContext> GetContextAsync(ReviewCandidate candidate, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
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

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly TaskCompletionSource firstLog = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource redactedLog = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(formatter(state, exception), exception, eventId));
            firstLog.TrySetResult();
            if (formatter(state, exception).Contains("[REDACTED]", StringComparison.Ordinal))
            {
                redactedLog.TrySetResult();
            }
        }

        public Task WaitForLogAsync(TimeSpan timeout) => firstLog.Task.WaitAsync(timeout);
        public Task WaitForRedactedLogAsync(TimeSpan timeout) => redactedLog.Task.WaitAsync(timeout);
    }

    private sealed record LogEntry(string Message, Exception? Exception, EventId EventId);
}