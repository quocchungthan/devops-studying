using Farm.Core.Chickens;

namespace Farm.Core.Cows;

public sealed record CloudWorkItemSnapshot(
    int Revision,
    WorkItemContext WorkItem,
    IReadOnlyList<int> LinkedPullRequestIds)
{
    // Parent/child/related/predecessor/successor work items, in relation order.
    public IReadOnlyList<int> RelatedWorkItemIds { get; init; } = [];
}

public sealed record CloudPullRequestState(
    int Id,
    string Status,
    string? Title,
    string? SourceCommitId,
    DateTimeOffset? ClosedAt,
    Uri? Url)
{
    public const string UnavailableStatus = "unavailable";

    public bool IsUnavailable => string.Equals(Status, UnavailableStatus, StringComparison.Ordinal);

    // The PR is missing or forbidden (404/403): a stable cloud state, not a transient failure.
    public static CloudPullRequestState Unavailable(int id) => new(id, UnavailableStatus, null, null, null, null);
}

public interface ICloudWorkItemSource
{
    // Non-terminal work items assigned to the PAT owner (WIQL @Me), most recently changed first.
    Task<IReadOnlyList<int>> GetAssignedToMeWorkItemIdsAsync(CancellationToken cancellationToken = default);

    // Deleted or inaccessible (404/403) items are omitted from the result; any other failure throws.
    Task<IReadOnlyList<CloudWorkItemSnapshot>> GetWorkItemsAsync(
        IReadOnlyCollection<int> ids,
        CancellationToken cancellationToken = default);

    // Returns an Unavailable state for a missing/forbidden PR and null for a transient failure.
    Task<CloudPullRequestState?> GetPullRequestAsync(int pullRequestId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkItemComment>> GetCommentsAsync(int workItemId, CancellationToken cancellationToken = default);
}

public sealed record NoteSyncRecord(
    int WorkItemId,
    string Fingerprint,
    string? NoteHash,
    DateTimeOffset SyncedAt,
    DateTimeOffset? RemovedAt = null);

public interface INoteSyncStore
{
    Task<IReadOnlyDictionary<int, NoteSyncRecord>> GetAllAsync(CancellationToken cancellationToken = default);

    Task UpsertAsync(NoteSyncRecord record, CancellationToken cancellationToken = default);
}

public sealed record InvestigationRequest(
    CloudWorkItemSnapshot Snapshot,
    IReadOnlyList<CloudPullRequestState> PullRequests,
    IReadOnlyList<int> UnavailablePullRequestIds,
    IReadOnlyList<WorkItemComment> Comments,
    string RepositoryPath);

public sealed record InvestigationResult(bool Succeeded, string? Markdown, string? Error)
{
    public static InvestigationResult Success(string markdown) => new(true, markdown, null);

    public static InvestigationResult Failure(string error) => new(false, null, error);
}

public interface IInvestigationAuthor
{
    Task<InvestigationResult> WriteAsync(InvestigationRequest request, CancellationToken cancellationToken = default);
}
