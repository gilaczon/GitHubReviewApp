namespace GitHubReviewApp.Tests.Models;

/// <summary>Behavior carried by PullRequestReviewSummary: its computed members and factories.</summary>
public class PullRequestReviewSummaryTests
{
    [Theory]
    [InlineData("PENDING", true)]
    [InlineData("pending", true)]
    [InlineData("COMMENTED", false)]
    [InlineData("APPROVED", false)]
    public void IsPending_GivenAReviewState_Should_BeTrueOnlyForPendingIgnoringCase(string state, bool isExpected)
    {
        // Arrange
        var review = new PullRequestReviewSummary(1, state, "body", "sha", "octocat");

        // Act
        var result = review.IsPending;

        // Assert
        result.Should().Be(isExpected);
    }
}
