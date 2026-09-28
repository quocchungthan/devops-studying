using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Farm.Core.Chickens;

public sealed record ReviewCandidate(
    string Organization,
    string Project,
    string RepositoryId,
    string RepositoryName,
    Uri RepositoryUrl,
    int PullRequestId,
    string Title,
    string SourceRef,
    string TargetRef,
    string HeadSha,
    string AuthorId,
    IReadOnlyList<FeedbackThread> Threads,
    IReadOnlyList<int> LinkedWorkItemIds,
    string? TargetSha = null,
    string? SourceRepositoryId = null,
    string? SourceRepositoryName = null,
    Uri? SourceRepositoryUrl = null);

public sealed record FeedbackThread(
    int Id,
    bool IsResolved,
    FeedbackAnchor? Anchor,
    IReadOnlyList<FeedbackComment> Comments);

public sealed record FeedbackAnchor(
    string? FilePath,
    int? StartLine,
    int? EndLine,
    string? CommitId,
    int? LeftStartLine = null,
    int? LeftStartOffset = null,
    int? LeftEndLine = null,
    int? LeftEndOffset = null,
    int? RightStartLine = null,
    int? RightStartOffset = null,
    int? RightEndLine = null,
    int? RightEndOffset = null,
    int? FirstComparingIteration = null,
    int? SecondComparingIteration = null,
    int? ChangeTrackingId = null,
    string? LeftCommitId = null,
    string? RightCommitId = null);

public sealed record FeedbackComment(
    int Id,
    string AuthorId,
    string AuthorDisplayName,
    string Content,
    DateTimeOffset PublishedAt,
    bool IsDeleted = false);

public sealed record WorkItemContext(
    int Id,
    string? Title,
    string? State,
    string? Description,
    IReadOnlyList<WorkItemComment> Comments,
    IReadOnlyList<WorkItemRelation> Relations,
    IReadOnlyList<WorkItemAttachment> Attachments,
    string? WorkItemType = null,
    string? ReproSteps = null,
    string? AcceptanceCriteria = null,
    int Depth = 0,
    string? RelationPath = null,
    DateTimeOffset? ChangedAt = null);

public sealed record WorkItemComment(
    int Id,
    string? Author,
    string Content,
    DateTimeOffset PublishedAt,
    string? AuthorId = null,
    int? Version = null,
    DateTimeOffset? ModifiedAt = null);

public sealed record WorkItemRelation(string RelationType, Uri Url, string? Name);

public sealed record WorkItemAttachment(string Name, Uri DownloadUrl, string? Comment);

public sealed record WorkItemContextSkip(int WorkItemId, int Depth, string Reason);

public sealed record ReviewContext(
    ReviewCandidate Candidate,
    IReadOnlyList<WorkItemContext> WorkItems,
    IReadOnlyList<WorkItemContextSkip>? SkippedWorkItems = null);

public sealed record ReviewCandidateSkip(int PullRequestId, string Reason);

public sealed record ReviewCandidateDiscoveryResult(
    IReadOnlyList<ReviewCandidate> Candidates,
    IReadOnlyList<ReviewCandidateSkip> SkippedCandidates);

public sealed record FeedbackFingerprint(string Value)
{
    public static FeedbackFingerprint Create(IEnumerable<FeedbackThread> threads, string? excludedAuthorId = null)
    {
        ArgumentNullException.ThrowIfNull(threads);

        var canonical = threads
            .Where(thread => !thread.IsResolved)
            .OrderBy(thread => thread.Id)
            .SelectMany(thread => thread.Comments
                .Where(comment => !comment.IsDeleted &&
                    (excludedAuthorId is null || !string.Equals(comment.AuthorId, excludedAuthorId, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(comment => comment.Id)
                .Select(comment => new
                {
                    ThreadId = thread.Id,
                    thread.Anchor,
                    CommentId = comment.Id,
                    comment.AuthorId,
                    PublishedAt = comment.PublishedAt.ToUniversalTime(),
                    Content = comment.Content.Replace("\r\n", "\n", StringComparison.Ordinal)
                }))
            .ToArray();
        var encoded = JsonSerializer.SerializeToUtf8Bytes(canonical);

        return new FeedbackFingerprint(Convert.ToHexString(SHA256.HashData(encoded)).ToLowerInvariant());
    }

    // Workspace branch names, commit dates and artifact paths need one 64-hex value covering both signals.
    public FeedbackFingerprint WithWorkItems(WorkItemFingerprint workItems)
    {
        ArgumentNullException.ThrowIfNull(workItems);
        return new FeedbackFingerprint(Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{Value}\n{workItems.Value}"))).ToLowerInvariant());
    }
}

public sealed record WorkItemFingerprint(string Value, IReadOnlyDictionary<int, string> ItemHashes)
{
    public const string FormatVersion = "wi-v1";

    // Skipped items and, on a degraded fetch, missing items keep their baseline hash so flapping access cannot alternate the value.
    public static WorkItemFingerprint Create(
        ReviewContext context,
        string? excludedAuthorId = null,
        IReadOnlyDictionary<int, string>? baseline = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var skipped = (context.SkippedWorkItems ?? []).Select(skip => skip.WorkItemId).ToHashSet();
        var hashes = new SortedDictionary<int, string>();
        foreach (var item in context.WorkItems)
        {
            if (!skipped.Contains(item.Id))
            {
                hashes[item.Id] = HashItem(item, excludedAuthorId);
            }
            else if (baseline is not null && baseline.TryGetValue(item.Id, out var previous))
            {
                hashes[item.Id] = previous;
            }
        }

        if (skipped.Count > 0 && baseline is not null)
        {
            foreach (var (id, hash) in baseline)
            {
                hashes.TryAdd(id, hash);
            }
        }

        var encoded = JsonSerializer.SerializeToUtf8Bytes(hashes.Select(pair => new { Id = pair.Key, Hash = pair.Value }));
        return new WorkItemFingerprint($"{FormatVersion}:{Sha256Hex(encoded)}", hashes);
    }

    public static string HashItem(WorkItemContext item, string? excludedAuthorId = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        var canonical = new
        {
            item.Id,
            Title = Normalize(item.Title),
            Description = Normalize(item.Description),
            ReproSteps = Normalize(item.ReproSteps),
            AcceptanceCriteria = Normalize(item.AcceptanceCriteria),
            Comments = item.Comments
                .Where(comment => IsExternal(comment, excludedAuthorId))
                .OrderBy(comment => comment.Id)
                .Select(comment => new
                {
                    comment.Id,
                    Revision = comment.Version?.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        ?? comment.ModifiedAt?.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    TextHash = Sha256Hex(Encoding.UTF8.GetBytes(Normalize(comment.Content) ?? string.Empty))
                })
                .ToArray()
        };

        return Sha256Hex(JsonSerializer.SerializeToUtf8Bytes(canonical));
    }

    public IReadOnlyList<int> ChangedSince(IReadOnlyDictionary<int, string> baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        return ItemHashes.Keys.Union(baseline.Keys)
            .Where(id => !ItemHashes.TryGetValue(id, out var current) ||
                !baseline.TryGetValue(id, out var previous) ||
                !string.Equals(current, previous, StringComparison.Ordinal))
            .Order()
            .ToArray();
    }

    // Mirrors the PR-thread quiet period: every external comment counts, field edits count only for content-changed items.
    public static DateTimeOffset? LatestActivity(
        ReviewContext context,
        string? excludedAuthorId,
        IEnumerable<int> changedWorkItemIds)
    {
        ArgumentNullException.ThrowIfNull(context);
        var changed = changedWorkItemIds.ToHashSet();
        var timestamps = context.WorkItems
            .SelectMany(item => item.Comments
                .Where(comment => IsExternal(comment, excludedAuthorId))
                .Select(comment => comment.ModifiedAt is { } modified && modified > comment.PublishedAt
                    ? modified
                    : comment.PublishedAt))
            .Concat(context.WorkItems
                .Where(item => item.ChangedAt is not null && changed.Contains(item.Id))
                .Select(item => item.ChangedAt!.Value))
            .ToArray();
        return timestamps.Length == 0 ? null : timestamps.Max();
    }

    private static bool IsExternal(WorkItemComment comment, string? excludedAuthorId) =>
        excludedAuthorId is null || !string.Equals(comment.AuthorId, excludedAuthorId, StringComparison.OrdinalIgnoreCase);

    private static string? Normalize(string? value) =>
        value?.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    private static string Sha256Hex(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
}

public enum ReviewEligibilityReason
{
    Eligible,
    NotAuthoredByCurrentUser,
    NoUnresolvedExternalFeedback,
    FeedbackStillCoolingDown,
    WorkItemFeedbackStillCoolingDown
}

public sealed record ReviewEligibility(bool IsEligible, ReviewEligibilityReason Reason, FeedbackFingerprint Fingerprint);

public static class ReviewEligibilityEvaluator
{
    public static ReviewEligibility Evaluate(
        ReviewCandidate candidate,
        string currentUserId,
        DateTimeOffset now,
        TimeSpan schedulePeriod)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentUserId);
        if (schedulePeriod < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(schedulePeriod));
        }

        var unresolved = candidate.Threads.Where(thread => !thread.IsResolved).ToArray();
        var fingerprint = FeedbackFingerprint.Create(unresolved, currentUserId);
        if (!string.Equals(candidate.AuthorId, currentUserId, StringComparison.OrdinalIgnoreCase))
        {
            return new(false, ReviewEligibilityReason.NotAuthoredByCurrentUser, fingerprint);
        }

        var externalComments = unresolved
            .SelectMany(thread => thread.Comments)
            .Where(comment => !comment.IsDeleted &&
                !string.Equals(comment.AuthorId, currentUserId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (externalComments.Length == 0)
        {
            return new(false, ReviewEligibilityReason.NoUnresolvedExternalFeedback, fingerprint);
        }

        if (externalComments.Max(comment => comment.PublishedAt) > now - schedulePeriod)
        {
            return new(false, ReviewEligibilityReason.FeedbackStillCoolingDown, fingerprint);
        }

        return new(true, ReviewEligibilityReason.Eligible, fingerprint);
    }
}

public enum ReviewOutcomeKind
{
    ChangesProduced,
    ExplanationOnly,
    Deferred,
    Failed
}

public sealed record ReviewOutcome(
    ReviewOutcomeKind Kind,
    string Summary,
    string? Transcript = null,
    string? CommitSha = null,
    bool Published = false);

public sealed record AttemptKey(
    string Organization,
    string Project,
    string RepositoryId,
    int PullRequestId,
    string HeadSha,
    string FeedbackFingerprint,
    string? WorkItemFingerprint = null);

public sealed record AttemptState(
    AttemptKey Key,
    ReviewOutcomeKind Outcome,
    DateTimeOffset CompletedAt,
    string ArtifactDirectory,
    IReadOnlyDictionary<int, string>? WorkItemHashes = null);

public sealed record AttemptBaseline(string? WorkItemFingerprint, IReadOnlyDictionary<int, string>? WorkItemHashes);

public sealed record Lease(string OwnerId, AttemptKey Key, DateTimeOffset ExpiresAt);

public sealed record RepositoryWorkspace(
    string RepositoryPath,
    string WorktreePath,
    string BranchName,
    string SourceRef,
    string ExpectedOldHeadSha,
    string BaseSha,
    bool HasRebaseConflicts = false);

public sealed record ValidationResult(bool Succeeded, int ExitCode, string Output);

public sealed record PublicationResult(bool Succeeded, bool Pushed, string? CommitSha, string Summary);

public sealed class ReviewDeferredException(string message, Exception? innerException = null) : Exception(message, innerException);

public sealed record CopilotReviewRequest(ReviewContext Context, RepositoryWorkspace Workspace, string Purpose, string ArtifactDirectory);

public interface IReviewContextSource
{
    Task<string> GetCurrentUserIdAsync(CancellationToken cancellationToken = default);
    Task<ReviewCandidateDiscoveryResult> GetCandidatesAsync(CancellationToken cancellationToken = default);
    Task<ReviewContext> GetContextAsync(ReviewCandidate candidate, CancellationToken cancellationToken = default);
}

public interface IAttemptStore
{
    Task<bool> HasCompletedAttemptAsync(AttemptKey key, CancellationToken cancellationToken = default);
    Task<Lease?> TryAcquireLeaseAsync(AttemptKey key, string ownerId, TimeSpan duration, CancellationToken cancellationToken = default);
    Task<Lease?> RenewLeaseAsync(Lease lease, TimeSpan duration, CancellationToken cancellationToken = default);
    Task CompleteAsync(Lease lease, AttemptState attempt, CancellationToken cancellationToken = default);
    Task ReleaseAsync(Lease lease, CancellationToken cancellationToken = default);

    // Latest successful attempt for the same PR, head SHA and PR-thread fingerprint, whatever its work item component.
    Task<AttemptBaseline?> GetBaselineAsync(AttemptKey key, CancellationToken cancellationToken = default);

    // Marks key complete by inheriting a pre-work-item completion, only if no work-item-aware attempt exists yet.
    Task<bool> AdoptLegacyCompletionAsync(
        AttemptKey key,
        IReadOnlyDictionary<int, string> workItemHashes,
        CancellationToken cancellationToken = default);
}

public interface IRepositoryWorkspaceManager
{
    Task<RepositoryWorkspace> PrepareAsync(
        ReviewCandidate candidate,
        FeedbackFingerprint fingerprint,
        CancellationToken cancellationToken = default);
    Task<ValidationResult> VerifyReadyAsync(RepositoryWorkspace workspace, CancellationToken cancellationToken = default);
    Task<string> CreatePatchAsync(RepositoryWorkspace workspace, CancellationToken cancellationToken = default);
    Task<ValidationResult> ValidateAsync(RepositoryWorkspace workspace, CancellationToken cancellationToken = default);
    Task<PublicationResult> CommitAndPushAsync(
        RepositoryWorkspace workspace,
        ReviewCandidate candidate,
        FeedbackFingerprint fingerprint,
        CancellationToken cancellationToken = default);
    Task CleanupAsync(RepositoryWorkspace workspace, CancellationToken cancellationToken = default);
}

public interface IReviewBrain
{
    Task<ReviewOutcome> ResolveAsync(CopilotReviewRequest request, CancellationToken cancellationToken = default);
}