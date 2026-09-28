using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.VisualStudio.Services.WebApi;
using CoreIdentity = Farm.Core.Domain.Identity;
using CorePullRequestComment = Farm.Core.Domain.PullRequestComment;
using CorePullRequestReviewer = Farm.Core.Domain.PullRequestReviewer;
using CorePullRequestSummary = Farm.Core.Domain.PullRequestSummary;
using CorePullRequestThread = Farm.Core.Domain.PullRequestThread;

namespace Farm.Azure;

public static class AzurePullRequestMapper
{
    // Azure DevOps comment thread statuses that mean a discussion is resolved, vs still needing attention.
    private static readonly HashSet<string> ResolvedThreadStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Fixed", "WontFix", "Closed", "ByDesign"
    };

    public static CorePullRequestSummary ToSummary(GitPullRequest pullRequest)
    {
        ArgumentNullException.ThrowIfNull(pullRequest);

        var reviewers = (pullRequest.Reviewers ?? [])
            .Select(ToReviewer)
            .ToArray();

        return new CorePullRequestSummary(
            pullRequest.PullRequestId,
            pullRequest.Title,
            pullRequest.Status.ToString(),
            ToIdentity(pullRequest.CreatedBy),
            new Uri(pullRequest.Url),
            reviewers);
    }

    public static CorePullRequestReviewer ToReviewer(IdentityRefWithVote reviewer)
    {
        ArgumentNullException.ThrowIfNull(reviewer);

        return new CorePullRequestReviewer(
            new CoreIdentity(reviewer.Id, reviewer.DisplayName, reviewer.UniqueName),
            reviewer.Vote,
            reviewer.IsRequired,
            reviewer.IsContainer);
    }

    private static CoreIdentity? ToIdentity(IdentityRef? identity) =>
        identity is null ? null : new CoreIdentity(identity.Id, identity.DisplayName, identity.UniqueName);

    // Azure DevOps vote codes: 10=approved, 5=approved-with-suggestions, 0=none, -5=waiting-for-author, -10=rejected.
    public static bool IsApprovedByReviewer(IReadOnlyList<CorePullRequestReviewer> reviewers, string currentUserId)
    {
        ArgumentNullException.ThrowIfNull(reviewers);

        return reviewers.Any(reviewer =>
            reviewer.Identity.Id == currentUserId && reviewer.Vote >= 5);
    }

    /// <summary>Matches direct reviewer entries for the user, or group reviewer entries the user is a member of.</summary>
    public static bool IsAssignedToReviewer(
        IReadOnlyList<CorePullRequestReviewer> reviewers,
        string currentUserId,
        IReadOnlyList<string> currentUserGroupIds)
    {
        ArgumentNullException.ThrowIfNull(reviewers);
        ArgumentNullException.ThrowIfNull(currentUserGroupIds);

        return reviewers.Any(reviewer =>
            reviewer.Identity.Id == currentUserId ||
            (reviewer.IsContainer && currentUserGroupIds.Contains(reviewer.Identity.Id)));
    }

    /// <summary>Assigned directly or via a group whose applicable reviewer vote is still 0.</summary>
    public static bool IsPendingReviewByCurrentUser(
        IReadOnlyList<CorePullRequestReviewer> reviewers,
        string currentUserId,
        IReadOnlyList<string> currentUserGroupIds)
    {
        return reviewers.Any(reviewer =>
            reviewer.Vote == 0 &&
            (reviewer.Identity.Id == currentUserId ||
             (reviewer.IsContainer && currentUserGroupIds.Contains(reviewer.Identity.Id))));
    }

    public static bool IsThreadResolved(string status) => ResolvedThreadStatuses.Contains(status);

    public static CorePullRequestThread ToThread(GitPullRequestCommentThread thread)
    {
        ArgumentNullException.ThrowIfNull(thread);

        var comments = (thread.Comments ?? [])
            .Where(comment => comment.IsDeleted != true)
            .Select(ToComment)
            .ToArray();

        return new CorePullRequestThread(thread.Id, thread.Status.ToString(), comments);
    }

    private static CorePullRequestComment ToComment(Comment comment) =>
        new(
            comment.Id,
            comment.Content ?? string.Empty,
            new DateTimeOffset(comment.PublishedDate.ToUniversalTime()),
            ToIdentity(comment.Author));
}
