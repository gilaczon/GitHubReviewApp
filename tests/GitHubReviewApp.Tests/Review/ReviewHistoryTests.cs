namespace GitHubReviewApp.Tests.Review;

public class ReviewHistoryTests
{
    private const string HeadSha = "0123456789abcdef0123456789abcdef01234567";
    private const string EarlierSha = "1111111111111111111111111111111111111111";
    private const string AppLogin = "my-reviewer[bot]";

    private static string StampedBody(string sha = HeadSha) => "## AI code review\n\nLooks fine.\n\n" + ReviewMarker.For(sha);

    private static PullRequestReviewSummary Review(
        string body, string author = AppLogin, string commitId = HeadSha, string state = "COMMENTED", long id = 1)
    {
        return new PullRequestReviewSummary(id, state, body, commitId, author);
    }

    [Fact]
    public void HasCompletedReview_GivenThisAppsReviewOfThisCommitEndingWithTheMarker_Should_BeTrue()
    {
        // Arrange
        var reviews = new[] { Review(StampedBody()) };

        // Act
        var result = ReviewHistory.HasCompletedReview(reviews, HeadSha, AppLogin);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void HasCompletedReview_GivenTheAppLoginInADifferentCase_Should_BeTrueBecauseGitHubLoginsIgnoreCase()
    {
        // Arrange
        var reviews = new[] { Review(StampedBody(), author: "My-Reviewer[BOT]") };

        // Act
        var result = ReviewHistory.HasCompletedReview(reviews, HeadSha, AppLogin);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void HasCompletedReview_GivenTheSameBodyFromAnotherBot_Should_BeFalse()
    {
        // Arrange — a second environment of this app, or any other bot, must not stand in for this app
        var reviews = new[] { Review(StampedBody(), author: "other-reviewer[bot]") };

        // Act
        var result = ReviewHistory.HasCompletedReview(reviews, HeadSha, AppLogin);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void HasCompletedReview_GivenAHumanQuotingTheMarker_Should_BeFalse()
    {
        // Arrange
        var reviews = new[] { Review(StampedBody(), author: "octocat") };

        // Act
        var result = ReviewHistory.HasCompletedReview(reviews, HeadSha, AppLogin);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void HasCompletedReview_GivenThisAppsReviewOfAnEarlierCommitQuotingThisCommitsMarker_Should_BeFalse()
    {
        // Arrange — the forgery: a review of an earlier commit lists a file named like the marker of the commit about to be force-pushed
        var forged = $"Not reviewed: `x{ReviewMarker.For(HeadSha)}.png`\n\n{ReviewMarker.For(EarlierSha)}";
        var reviews = new[] { Review(forged, commitId: EarlierSha) };

        // Act
        var result = ReviewHistory.HasCompletedReview(reviews, HeadSha, AppLogin);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void HasCompletedReview_GivenAReviewOfAnotherCommitThatEndsWithThisCommitsMarker_Should_BeFalse()
    {
        // Arrange — GitHub fixes the commit a review belongs to, so the commit id is checked as well as the text
        var reviews = new[] { Review(StampedBody(), commitId: EarlierSha) };

        // Act
        var result = ReviewHistory.HasCompletedReview(reviews, HeadSha, AppLogin);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void HasCompletedReview_GivenThisAppsPendingDraftWithTheMarker_Should_BeFalse()
    {
        // Arrange
        var reviews = new[] { Review(StampedBody(), state: "PENDING") };

        // Act
        var result = ReviewHistory.HasCompletedReview(reviews, HeadSha, AppLogin);

        // Assert
        result.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void HasCompletedReview_GivenNoKnownAppLogin_Should_BeFalseRatherThanMatchEveryone(string appLogin)
    {
        // Arrange
        var reviews = new[] { Review(StampedBody(), author: string.Empty) };

        // Act
        var result = ReviewHistory.HasCompletedReview(reviews, HeadSha, appLogin);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void HasCompletedReview_GivenNoReviews_Should_BeFalse()
    {
        // Arrange

        // Act
        var result = ReviewHistory.HasCompletedReview([], HeadSha, AppLogin);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void OwnPendingReviews_GivenDraftsFromSeveralAuthors_Should_ReturnOnlyThisAppsDrafts()
    {
        // Arrange
        var own = Review(string.Empty, state: "PENDING", id: 1);
        var reviews = new[]
        {
            own,
            Review(string.Empty, author: "octocat", state: "PENDING", id: 2),
            Review(string.Empty, author: "other-reviewer[bot]", state: "PENDING", id: 3),
            Review("done", state: "COMMENTED", id: 4)
        };

        // Act
        var result = ReviewHistory.OwnPendingReviews(reviews, AppLogin);

        // Assert
        result.Should().Equal(own);
    }
}
