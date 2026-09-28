using Farm.Core.Chickens;
using Farm.State.Sqlite;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Farm.Sandbox.Chickens.Tests;

public sealed class ChickenRunnerWorkItemFeedbackTests : IDisposable
{
    private static readonly DateTimeOffset Old = DateTimeOffset.UtcNow.AddDays(-2);
    private readonly string root = Path.Combine(Path.GetTempPath(), $"chicken-work-items-{Guid.NewGuid():N}");

    [Fact]
    public async Task Unchanged_pr_and_work_items_are_not_picked_up_again()
    {
        var harness = new Harness(root, [Item(7, [Comment(1, "reviewer")])]);

        await harness.RunAsync();
        await harness.RunAsync();

        Assert.Equal(1, harness.Brain.Calls);
        Assert.DoesNotContain(harness.Logger.Entries, entry => entry.EventId.Name == "feedback_changed_work_items");
    }

    [Fact]
    public async Task New_work_item_comment_triggers_pickup_with_unchanged_pr()
    {
        var harness = new Harness(root, [Item(7, [Comment(1, "reviewer")]), Item(8, [])]);
        await harness.RunAsync();

        harness.WorkItems[1] = harness.WorkItems[1] with { Comments = [Comment(5, "reviewer")] };
        await harness.RunAsync();

        Assert.Equal(2, harness.Brain.Calls);
        var changed = Assert.Single(harness.Logger.Entries, entry => entry.EventId.Name == "feedback_changed_work_items");
        Assert.Contains("pull_request_id=1 changed_work_item_ids=8", changed.Message);
    }

    [Fact]
    public async Task Edited_work_item_comment_triggers_pickup()
    {
        var harness = new Harness(root, [Item(7, [Comment(1, "reviewer")])]);
        await harness.RunAsync();

        harness.WorkItems[0] = harness.WorkItems[0] with
        {
            Comments = [Comment(1, "reviewer") with { Version = 2, ModifiedAt = Old.AddHours(1), Content = "edited" }]
        };
        await harness.RunAsync();

        Assert.Equal(2, harness.Brain.Calls);
    }

    [Theory]
    [InlineData("title")]
    [InlineData("description")]
    [InlineData("repro")]
    [InlineData("criteria")]
    public async Task Content_field_change_triggers_pickup(string field)
    {
        var harness = new Harness(root, [Item(7, [])]);
        await harness.RunAsync();

        var item = harness.WorkItems[0];
        harness.WorkItems[0] = field switch
        {
            "title" => item with { Title = "changed" },
            "description" => item with { Description = "changed" },
            "repro" => item with { ReproSteps = "changed" },
            _ => item with { AcceptanceCriteria = "changed" }
        };
        await harness.RunAsync();

        Assert.Equal(2, harness.Brain.Calls);
    }

    [Fact]
    public async Task State_or_board_only_change_does_not_trigger_pickup()
    {
        var harness = new Harness(root, [Item(7, [])]);
        await harness.RunAsync();

        harness.WorkItems[0] = harness.WorkItems[0] with { State = "Resolved", ChangedAt = DateTimeOffset.UtcNow };
        harness.QuietPeriod = TimeSpan.FromHours(1);
        await harness.RunAsync();

        Assert.Equal(1, harness.Brain.Calls);
        Assert.DoesNotContain(harness.Logger.Entries, entry => entry.Message.Contains("WorkItemFeedbackStillCoolingDown", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Bot_authored_work_item_comment_does_not_trigger_pickup_or_quiet_period()
    {
        var harness = new Harness(root, [Item(7, [Comment(1, "reviewer")])]);
        await harness.RunAsync();

        harness.WorkItems[0] = harness.WorkItems[0] with
        {
            Comments = [Comment(1, "reviewer"), Comment(2, "AUTHOR") with { PublishedAt = DateTimeOffset.UtcNow }]
        };
        harness.QuietPeriod = TimeSpan.FromHours(1);
        await harness.RunAsync();

        Assert.Equal(1, harness.Brain.Calls);
        Assert.DoesNotContain(harness.Logger.Entries, entry => entry.EventId.Name == "feedback_changed_work_items");
    }

    [Fact]
    public async Task Recent_external_work_item_comment_defers_until_quiet_period_elapses()
    {
        var harness = new Harness(root, [Item(7, [Comment(1, "reviewer") with { PublishedAt = DateTimeOffset.UtcNow.AddMinutes(-5) }])]);
        harness.QuietPeriod = TimeSpan.FromHours(1);

        await harness.RunAsync();

        Assert.Equal(0, harness.Brain.Calls);
        Assert.Contains(harness.Logger.Entries, entry => entry.Message.Contains("reason=WorkItemFeedbackStillCoolingDown", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Legacy_completion_is_adopted_as_baseline_once_then_work_item_changes_trigger()
    {
        var harness = new Harness(root, [Item(7, [Comment(1, "reviewer")])]);
        var legacyKey = new AttemptKey(
            "org", "project", "repo", 1, new string('a', 40),
            FeedbackFingerprint.Create(harness.Candidate.Threads, "author").Value);
        var lease = await harness.Store.TryAcquireLeaseAsync(legacyKey, "legacy", TimeSpan.FromMinutes(5));
        await harness.Store.CompleteAsync(lease!, new AttemptState(legacyKey, ReviewOutcomeKind.ExplanationOnly, Old, root));

        await harness.RunAsync();
        await harness.RunAsync();

        Assert.Equal(0, harness.Brain.Calls);
        Assert.Single(harness.Logger.Entries, entry => entry.EventId.Name == "attempt_baseline_adopted");

        harness.WorkItems[0] = harness.WorkItems[0] with { Comments = [Comment(1, "reviewer"), Comment(2, "reviewer")] };
        await harness.RunAsync();

        Assert.Equal(1, harness.Brain.Calls);
        Assert.Single(harness.Logger.Entries, entry => entry.EventId.Name == "attempt_baseline_adopted");
        Assert.Contains(harness.Logger.Entries, entry => entry.Message.Contains("changed_work_item_ids=7", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Flapping_related_item_availability_does_not_retrigger()
    {
        var harness = new Harness(root, [Item(7, []), Item(9, [Comment(3, "reviewer")])]);
        await harness.RunAsync();

        harness.WorkItems.RemoveAt(1);
        harness.Skipped = [new WorkItemContextSkip(9, 1, "work_item_unavailable")];
        await harness.RunAsync();
        harness.WorkItems.Add(Item(9, []));
        harness.Skipped = [new WorkItemContextSkip(9, 1, "comments_unavailable")];
        await harness.RunAsync();

        Assert.Equal(1, harness.Brain.Calls);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private static WorkItemContext Item(int id, IReadOnlyList<WorkItemComment> comments) => new(
        id, $"Item {id}", "Active", "description", comments, [], [], "Bug", "repro", "criteria", 0, "linked to PR #1", Old);

    private static WorkItemComment Comment(int id, string authorId) =>
        new(id, authorId, $"comment {id}", Old, authorId, 1, Old);

    private sealed class Harness
    {
        public Harness(string root, IEnumerable<WorkItemContext> workItems)
        {
            Root = root;
            WorkItems = [.. workItems];
            Store = new SqliteAttemptStore(Path.Combine(root, "state.db"));
        }

        public string Root { get; }
        public List<WorkItemContext> WorkItems { get; }
        public IReadOnlyList<WorkItemContextSkip> Skipped { get; set; } = [];
        public SqliteAttemptStore Store { get; }
        public TimeSpan QuietPeriod { get; set; } = TimeSpan.Zero;
        public CountingBrain Brain { get; } = new();
        public CapturingLogger Logger { get; } = new();

        public ReviewCandidate Candidate { get; } = new(
            "org", "project", "repo", "repo", new Uri("https://example.test/repo"), 1, "PR",
            "refs/heads/feature", "refs/heads/main", new string('a', 40), "author",
            [new FeedbackThread(1, false, null, [new FeedbackComment(1, "reviewer", "Reviewer", "fix", Old)])],
            [7], new string('b', 40));

        public Task<ChickenCycleResult> RunAsync() => new ChickenRunner(
            new ContextSource(this),
            new WorkspaceManager(Root),
            Brain,
            Store,
            new ChickenOptions
            {
                ArtifactsPath = Path.Combine(Root, "artifacts"),
                QuietPeriod = QuietPeriod,
                LeaseDuration = TimeSpan.FromMinutes(10)
            },
            SensitiveDataRedactor.Empty,
            SensitiveContentScanner.Empty,
            Logger).RunOnceAsync(CancellationToken.None);
    }

    private sealed class ContextSource(Harness harness) : IReviewContextSource
    {
        public Task<string> GetCurrentUserIdAsync(CancellationToken cancellationToken = default) => Task.FromResult("author");
        public Task<ReviewCandidateDiscoveryResult> GetCandidatesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReviewCandidateDiscoveryResult([harness.Candidate], []));
        public Task<ReviewContext> GetContextAsync(ReviewCandidate value, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReviewContext(value, [.. harness.WorkItems], harness.Skipped));
    }

    private sealed class WorkspaceManager(string rootPath) : IRepositoryWorkspaceManager
    {
        public Task<RepositoryWorkspace> PrepareAsync(ReviewCandidate candidate, FeedbackFingerprint fingerprint, CancellationToken cancellationToken = default)
        {
            var worktree = Path.Combine(rootPath, "worktree");
            Directory.CreateDirectory(worktree);
            return Task.FromResult(new RepositoryWorkspace(rootPath, worktree, "chickens/test", candidate.SourceRef, candidate.HeadSha, candidate.TargetSha!));
        }

        public Task<ValidationResult> VerifyReadyAsync(RepositoryWorkspace workspace, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ValidationResult(true, 0, string.Empty));
        public Task<string> CreatePatchAsync(RepositoryWorkspace workspace, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);
        public Task<ValidationResult> ValidateAsync(RepositoryWorkspace workspace, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ValidationResult(true, 0, string.Empty));
        public Task<PublicationResult> CommitAndPushAsync(RepositoryWorkspace workspace, ReviewCandidate candidate, FeedbackFingerprint fingerprint, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task CleanupAsync(RepositoryWorkspace workspace, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class CountingBrain : IReviewBrain
    {
        public int Calls { get; private set; }

        public Task<ReviewOutcome> ResolveAsync(CopilotReviewRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ReviewOutcome(ReviewOutcomeKind.ExplanationOnly, "explained"));
        }
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
