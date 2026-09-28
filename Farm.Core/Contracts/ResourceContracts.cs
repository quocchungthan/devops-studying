using Farm.Core.Domain;
using Farm.Core.Exports;

namespace Farm.Core.Contracts;

public interface IWorkItemSource
{
    Task<IReadOnlyList<WorkItem>> GetWorkItemsAsync(
        IEnumerable<int> ids,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkItem>> GetWorkItemsAssignedToAsync(
        string assignee,
        CancellationToken cancellationToken = default);

    /// <summary>Unassigned, non-terminal work items in the current iteration.</summary>
    Task<IReadOnlyList<WorkItem>> GetNeedsAttentionAsync(
        CancellationToken cancellationToken = default);
}

public interface IWorkItemAssignmentService
{
    Task<WorkItem> AssignAsync(
        int id,
        string assignee,
        CancellationToken cancellationToken = default);

    Task<WorkItem> UnassignAsync(
        int id,
        CancellationToken cancellationToken = default);
}

public interface IWorkItemCommentService
{
    Task AddCommentAsync(
        int id,
        string text,
        CancellationToken cancellationToken = default);
}

public interface IWorkItemDescriptionService
{
    Task UpdateDescriptionAsync(
        int id,
        string description,
        CancellationToken cancellationToken = default);
}

public interface IPullRequestSource
{
    Task<IReadOnlyList<PullRequestSummary>> ListActiveAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PullRequestSummary>> ListApprovedByCurrentUserAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Active PRs where the current user is a reviewer, directly or via a group membership.</summary>
    Task<IReadOnlyList<PullRequestSummary>> ListAssignedToCurrentUserAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Of the PRs assigned to the current user, those where their own vote is still 0 (no-vote).</summary>
    Task<IReadOnlyList<PullRequestSummary>> ListPendingReviewByCurrentUserAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PullRequestSummary>> ListCreatedByCurrentUserAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PullRequestThread>> ListThreadsAsync(
        int pullRequestId,
        CancellationToken cancellationToken = default);
}

public interface IIdentitySource
{
    Task<IReadOnlyList<Identity>> GetIdentitiesAsync(
        IEnumerable<string> ids,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Group>> GetGroupsAsync(
        IEnumerable<string> ids,
        CancellationToken cancellationToken = default);
}

public interface IIdentityDirectory
{
    Task<Identity> GetCurrentUserAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Group>> GetGroupsForCurrentUserAsync(CancellationToken cancellationToken = default);
}

    public interface IWorkspaceExportService
    {
        Task<WorkspaceExportManifest> ExportAsync(
        WorkspaceExportSpecification specification,
        CancellationToken cancellationToken = default);
    }