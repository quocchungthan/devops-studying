using Farm.Core.Contracts;
using CorePullRequestSummary = Farm.Core.Domain.PullRequestSummary;
using CorePullRequestThread = Farm.Core.Domain.PullRequestThread;

namespace Farm.Azure;

public interface IAzurePullRequestClient
{
    Task<IReadOnlyList<CorePullRequestSummary>> ListActivePullRequestsAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CorePullRequestSummary>> ListPullRequestsApprovedByCurrentUserAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CorePullRequestSummary>> ListPullRequestsAssignedToCurrentUserAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CorePullRequestSummary>> ListPullRequestsPendingReviewByCurrentUserAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CorePullRequestSummary>> ListPullRequestsCreatedByCurrentUserAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CorePullRequestThread>> GetPullRequestThreadsAsync(
        int pullRequestId,
        CancellationToken cancellationToken = default);
}

public sealed class AzurePullRequestSource(IAzurePullRequestClient client) : IPullRequestSource
{
    public Task<IReadOnlyList<CorePullRequestSummary>> ListActiveAsync(
        CancellationToken cancellationToken = default) =>
        client.ListActivePullRequestsAsync(cancellationToken);

    public Task<IReadOnlyList<CorePullRequestSummary>> ListApprovedByCurrentUserAsync(
        CancellationToken cancellationToken = default) =>
        client.ListPullRequestsApprovedByCurrentUserAsync(cancellationToken);

    public Task<IReadOnlyList<CorePullRequestSummary>> ListAssignedToCurrentUserAsync(
        CancellationToken cancellationToken = default) =>
        client.ListPullRequestsAssignedToCurrentUserAsync(cancellationToken);

    public Task<IReadOnlyList<CorePullRequestSummary>> ListPendingReviewByCurrentUserAsync(
        CancellationToken cancellationToken = default) =>
        client.ListPullRequestsPendingReviewByCurrentUserAsync(cancellationToken);

    public Task<IReadOnlyList<CorePullRequestSummary>> ListCreatedByCurrentUserAsync(
        CancellationToken cancellationToken = default) =>
        client.ListPullRequestsCreatedByCurrentUserAsync(cancellationToken);

    public Task<IReadOnlyList<CorePullRequestThread>> ListThreadsAsync(
        int pullRequestId,
        CancellationToken cancellationToken = default) =>
        client.GetPullRequestThreadsAsync(pullRequestId, cancellationToken);
}
