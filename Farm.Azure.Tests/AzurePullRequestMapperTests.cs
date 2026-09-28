using Microsoft.TeamFoundation.SourceControl.WebApi;
using Xunit;
using CoreIdentity = Farm.Core.Domain.Identity;
using CorePullRequestReviewer = Farm.Core.Domain.PullRequestReviewer;

namespace Farm.Azure.Tests;

public sealed class AzurePullRequestMapperTests
{
    [Fact]
    public void ToSummary_maps_known_fields_and_reviewers()
    {
        var pullRequest = new GitPullRequest
        {
            PullRequestId = 7,
            Title = "Add feature",
            Status = PullRequestStatus.Active,
            Url = "https://dev.azure.com/example/_apis/git/repositories/repo/pullRequests/7",
            CreatedBy = new Microsoft.VisualStudio.Services.WebApi.IdentityRef
            {
                Id = "author-id",
                DisplayName = "Author Example",
                UniqueName = "author@example.com"
            },
            Reviewers =
            [
                new IdentityRefWithVote
                {
                    Id = "reviewer-id",
                    DisplayName = "Reviewer Example",
                    UniqueName = "reviewer@example.com",
                    Vote = 10,
                    IsRequired = true
                }
            ]
        };

        var result = AzurePullRequestMapper.ToSummary(pullRequest);

        Assert.Equal(7, result.Id);
        Assert.Equal("Add feature", result.Title);
        Assert.Equal("Active", result.Status);
        Assert.Equal("author-id", result.CreatedBy?.Id);
        Assert.Single(result.Reviewers);
        Assert.Equal("reviewer-id", result.Reviewers[0].Identity.Id);
        Assert.Equal(10, result.Reviewers[0].Vote);
        Assert.True(result.Reviewers[0].IsRequired);
    }

    [Fact]
    public void ToReviewer_maps_vote_and_identity()
    {
        var reviewer = new IdentityRefWithVote
        {
            Id = "reviewer-id",
            DisplayName = "Reviewer Example",
            UniqueName = "reviewer@example.com",
            Vote = -10,
            IsRequired = false
        };

        var result = AzurePullRequestMapper.ToReviewer(reviewer);

        Assert.Equal("reviewer-id", result.Identity.Id);
        Assert.Equal("Reviewer Example", result.Identity.DisplayName);
        Assert.Equal(-10, result.Vote);
        Assert.False(result.IsRequired);
    }

    [Theory]
    [InlineData(10, true)]
    [InlineData(5, true)]
    [InlineData(0, false)]
    [InlineData(-10, false)]
    [InlineData(-5, false)]
    public void IsApprovedByReviewer_matches_current_user_by_vote_threshold(int vote, bool expected)
    {
        var reviewers = new CorePullRequestReviewer[]
        {
            new(new CoreIdentity("current-user-id", "Current User", "current@example.com"), vote, IsRequired: true)
        };

        var result = AzurePullRequestMapper.IsApprovedByReviewer(reviewers, "current-user-id");

        Assert.Equal(expected, result);
    }

    [Fact]
    public void IsApprovedByReviewer_returns_false_when_no_reviewer_matches_current_user()
    {
        var reviewers = new CorePullRequestReviewer[]
        {
            new(new CoreIdentity("other-user-id", "Other User", "other@example.com"), 10, IsRequired: true)
        };

        var result = AzurePullRequestMapper.IsApprovedByReviewer(reviewers, "current-user-id");

        Assert.False(result);
    }
}
