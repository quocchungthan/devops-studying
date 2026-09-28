using Microsoft.TeamFoundation.SourceControl.WebApi;
using Xunit;
using CoreIdentity = Farm.Core.Domain.Identity;
using CorePullRequestReviewer = Farm.Core.Domain.PullRequestReviewer;

namespace Farm.Azure.Tests;

public sealed class AzurePullRequestAssignmentTests
{
    private const string CurrentUserId = "current-user-id";
    private static readonly string[] GroupIds = ["group-a-id", "group-b-id"];

    [Fact]
    public void IsAssignedToReviewer_matches_direct_reviewer()
    {
        CorePullRequestReviewer[] reviewers =
        [
            new(new CoreIdentity(CurrentUserId, "Current User", "current@example.com"), 0, IsRequired: true)
        ];

        Assert.True(AzurePullRequestMapper.IsAssignedToReviewer(reviewers, CurrentUserId, GroupIds));
    }

    [Fact]
    public void IsAssignedToReviewer_matches_group_reviewer_user_belongs_to()
    {
        CorePullRequestReviewer[] reviewers =
        [
            new(new CoreIdentity("group-a-id", "Team A", null), 0, IsRequired: true, IsContainer: true)
        ];

        Assert.True(AzurePullRequestMapper.IsAssignedToReviewer(reviewers, CurrentUserId, GroupIds));
    }

    [Fact]
    public void IsAssignedToReviewer_ignores_group_reviewer_user_does_not_belong_to()
    {
        CorePullRequestReviewer[] reviewers =
        [
            new(new CoreIdentity("other-group-id", "Other Team", null), 0, IsRequired: true, IsContainer: true)
        ];

        Assert.False(AzurePullRequestMapper.IsAssignedToReviewer(reviewers, CurrentUserId, GroupIds));
    }

    [Fact]
    public void IsAssignedToReviewer_ignores_non_container_reviewer_with_group_id()
    {
        CorePullRequestReviewer[] reviewers =
        [
            new(new CoreIdentity("group-a-id", "Not really a group", null), 0, IsRequired: true, IsContainer: false)
        ];

        Assert.False(AzurePullRequestMapper.IsAssignedToReviewer(reviewers, CurrentUserId, GroupIds));
    }

    [Fact]
    public void IsPendingReviewByCurrentUser_true_when_assigned_directly_with_no_vote()
    {
        CorePullRequestReviewer[] reviewers =
        [
            new(new CoreIdentity(CurrentUserId, "Current User", null), 0, IsRequired: true)
        ];

        Assert.True(AzurePullRequestMapper.IsPendingReviewByCurrentUser(reviewers, CurrentUserId, GroupIds));
    }

    [Fact]
    public void IsPendingReviewByCurrentUser_false_when_current_user_already_voted()
    {
        CorePullRequestReviewer[] reviewers =
        [
            new(new CoreIdentity(CurrentUserId, "Current User", null), 10, IsRequired: true)
        ];

        Assert.False(AzurePullRequestMapper.IsPendingReviewByCurrentUser(reviewers, CurrentUserId, GroupIds));
    }

    [Fact]
    public void IsPendingReviewByCurrentUser_true_when_matching_group_has_no_vote()
    {
        CorePullRequestReviewer[] reviewers =
        [
            new(new CoreIdentity("group-a-id", "Team A", null), 0, IsRequired: true, IsContainer: true)
        ];

        Assert.True(AzurePullRequestMapper.IsPendingReviewByCurrentUser(reviewers, CurrentUserId, GroupIds));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    public void IsPendingReviewByCurrentUser_false_when_matching_group_has_voted(int vote)
    {
        CorePullRequestReviewer[] reviewers =
        [
            new(new CoreIdentity("group-a-id", "Team A", null), vote, IsRequired: true, IsContainer: true)
        ];

        Assert.False(AzurePullRequestMapper.IsPendingReviewByCurrentUser(reviewers, CurrentUserId, GroupIds));
    }

    [Fact]
    public void IsPendingReviewByCurrentUser_does_not_match_graph_descriptor_to_legacy_group_id()
    {
        CorePullRequestReviewer[] reviewers =
        [
            new(new CoreIdentity("vssgp.group-a-descriptor", "Team A", null), 0, IsRequired: true, IsContainer: true)
        ];

        Assert.False(AzurePullRequestMapper.IsPendingReviewByCurrentUser(reviewers, CurrentUserId, GroupIds));
    }

    [Fact]
    public void IsPendingReviewByCurrentUser_false_when_not_assigned_at_all()
    {
        CorePullRequestReviewer[] reviewers =
        [
            new(new CoreIdentity("someone-else", "Someone Else", null), 0, IsRequired: true),
            new(new CoreIdentity("other-group-id", "Other Team", null), 0, IsRequired: true, IsContainer: true)
        ];

        Assert.False(AzurePullRequestMapper.IsPendingReviewByCurrentUser(reviewers, CurrentUserId, GroupIds));
    }

    [Theory]
    [InlineData("Fixed", true)]
    [InlineData("WontFix", true)]
    [InlineData("Closed", true)]
    [InlineData("ByDesign", true)]
    [InlineData("Active", false)]
    [InlineData("Pending", false)]
    [InlineData("Unknown", false)]
    public void IsThreadResolved_classifies_known_statuses(string status, bool expected)
    {
        Assert.Equal(expected, AzurePullRequestMapper.IsThreadResolved(status));
    }

    [Fact]
    public void ToThread_maps_status_and_filters_deleted_comments()
    {
        var thread = new GitPullRequestCommentThread
        {
            Id = 3,
            Status = CommentThreadStatus.Active,
            Comments =
            [
                new Comment
                {
                    Id = 1,
                    Content = "First comment",
                    PublishedDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    Author = new Microsoft.VisualStudio.Services.WebApi.IdentityRef
                    {
                        Id = "author-id",
                        DisplayName = "Author Example"
                    }
                },
                new Comment
                {
                    Id = 2,
                    Content = "Deleted comment",
                    IsDeleted = true
                }
            ]
        };

        var result = AzurePullRequestMapper.ToThread(thread);

        Assert.Equal(3, result.Id);
        Assert.Equal("Active", result.Status);
        Assert.Single(result.Comments);
        Assert.Equal("First comment", result.Comments[0].Content);
        Assert.Equal("author-id", result.Comments[0].Author?.Id);
    }
}
