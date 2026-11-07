namespace GitHubReviewApp.Tests.Services;

public class ReviewFailureNotifierTests
{
    private const string Token = "ghs_token";
    private const string HeadSha = "0123456789abcdef0123456789abcdef01234567";
    private const string EarlierSha = "1111111111111111111111111111111111111111";
    private const string AppLogin = "my-reviewer[bot]";

    private static readonly ReviewQueueMessage _message = new("my-org", "my-service", 7, "title", "basesha", HeadSha, 12345L);

    private static (ReviewFailureNotifier Sut, Mock<IGitHubService> GitHub) Build(
        PullRequestInfo? pullRequest = null,
        IReadOnlyList<PullRequestReviewSummary>? reviews = null)
    {
        var auth = new Mock<IGitHubAppAuthService>();
        auth.Setup(a => a.GetInstallationTokenAsync(12345L, "my-service", It.IsAny<CancellationToken>())).ReturnsAsync(Token);
        auth.Setup(a => a.GetAppLoginAsync(It.IsAny<CancellationToken>())).ReturnsAsync(AppLogin);

        var github = new Mock<IGitHubService>();
        github.Setup(g => g.GetPullRequestAsync("my-org", "my-service", 7, Token, It.IsAny<CancellationToken>()))
            .ReturnsAsync(pullRequest ?? new PullRequestInfo("open", false, "title", HeadSha, "basesha", 1, 1, 1));
        github.Setup(g => g.ListReviewsAsync("my-org", "my-service", 7, Token, It.IsAny<CancellationToken>()))
            .ReturnsAsync(reviews ?? []);
        github.Setup(g => g.PostCommentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = new ReviewFailureNotifier(auth.Object, github.Object, NullLogger<ReviewFailureNotifier>.Instance);
        return (sut, github);
    }

    private static void VerifyNoComment(Mock<IGitHubService> github)
    {
        github.Verify(g => g.PostCommentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    private static void VerifyOneComment(Mock<IGitHubService> github)
    {
        github.Verify(g => g.PostCommentAsync("my-org", "my-service", 7, It.IsAny<string>(), Token, It.IsAny<CancellationToken>()), Times.Once());
    }

    private static PullRequestReviewSummary Review(string author, string body, string commitId = HeadSha)
    {
        return new PullRequestReviewSummary(1, "COMMENTED", body, commitId, author);
    }

    [Fact]
    public async Task NotifyFailureAsync_GivenAnOpenPullRequestAtTheSameCommit_Should_PostACommentWithTheReasonAndTheShortCommit()
    {
        // Arrange
        string? posted = null;
        var (sut, github) = Build();
        github.Setup(g => g.PostCommentAsync("my-org", "my-service", 7, It.IsAny<string>(), Token, It.IsAny<CancellationToken>()))
            .Callback<string, string, int, string, string, CancellationToken>((_, _, _, body, _, _) => posted = body)
            .Returns(Task.CompletedTask);

        // Act
        await sut.NotifyFailureAsync(_message, "The AI service was unavailable.", CancellationToken.None);

        // Assert
        posted.Should().NotBeNull();
        posted.Should().Contain("AI code review did not complete").And.Contain("`0123456`").And.Contain("The AI service was unavailable.");
        posted.Should().Contain("Push a new commit");
    }

    [Fact]
    public async Task NotifyFailureAsync_GivenAnOpenPullRequest_Should_NotPostAReviewMarkerSoTheCommentIsNeverTakenForACompletedReview()
    {
        // Arrange
        string? posted = null;
        var (sut, github) = Build();
        github.Setup(g => g.PostCommentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, int, string, string, CancellationToken>((_, _, _, body, _, _) => posted = body)
            .Returns(Task.CompletedTask);

        // Act
        await sut.NotifyFailureAsync(_message, "reason", CancellationToken.None);

        // Assert
        ReviewMarker.IsAtEndOf(posted, HeadSha).Should().BeFalse();
        posted.Should().NotContain("<!--");
    }

    [Theory]
    [InlineData("closed", false, HeadSha)]
    [InlineData("open", true, HeadSha)]
    [InlineData("open", false, "ffffffffffffffffffffffffffffffffffffffff")]
    public async Task NotifyFailureAsync_GivenAClosedDraftOrMovedOnPullRequest_Should_StayQuiet(string state, bool isDraft, string headSha)
    {
        // Arrange
        var (sut, github) = Build(new PullRequestInfo(state, isDraft, "title", headSha, "basesha", 1, 1, 1));

        // Act
        await sut.NotifyFailureAsync(_message, "reason", CancellationToken.None);

        // Assert
        VerifyNoComment(github);
    }

    [Fact]
    public async Task NotifyFailureAsync_GivenThisAppsReviewForThisCommitAlreadyExists_Should_StayQuietBecauseTheReviewDidSucceed()
    {
        // Arrange — e.g. a second queued message for the same commit completed while this one was failing
        var existing = Review(AppLogin, "done\n" + ReviewMarker.For(HeadSha));
        var (sut, github) = Build(reviews: [existing]);

        // Act
        await sut.NotifyFailureAsync(_message, "reason", CancellationToken.None);

        // Assert
        VerifyNoComment(github);
    }

    [Fact]
    public async Task NotifyFailureAsync_GivenOnlyAHumanReviewQuotesTheMarker_Should_StillTellTheAuthor()
    {
        // Arrange — only this app's own review proves the commit was reviewed; anyone can paste the marker text
        var quoted = Review("octocat", ReviewMarker.For(HeadSha));
        var (sut, github) = Build(reviews: [quoted]);

        // Act
        await sut.NotifyFailureAsync(_message, "reason", CancellationToken.None);

        // Assert
        VerifyOneComment(github);
    }

    [Fact]
    public async Task NotifyFailureAsync_GivenAnotherBotsReviewEndsWithTheMarker_Should_StillTellTheAuthor()
    {
        // Arrange
        var other = Review("other-reviewer[bot]", ReviewMarker.For(HeadSha));
        var (sut, github) = Build(reviews: [other]);

        // Act
        await sut.NotifyFailureAsync(_message, "reason", CancellationToken.None);

        // Assert
        VerifyOneComment(github);
    }

    [Fact]
    public async Task NotifyFailureAsync_GivenThisAppsReviewOfAnEarlierCommitQuotingThisCommitsMarker_Should_StillTellTheAuthor()
    {
        // Arrange — a file name in an earlier review of this app can carry the marker text of a later commit
        var forged = Review(AppLogin, $"Not reviewed: `x{ReviewMarker.For(HeadSha)}.png`\n\n{ReviewMarker.For(EarlierSha)}", EarlierSha);
        var (sut, github) = Build(reviews: [forged]);

        // Act
        await sut.NotifyFailureAsync(_message, "reason", CancellationToken.None);

        // Assert
        VerifyOneComment(github);
    }

    [Fact]
    public async Task NotifyFailureAsync_GivenGitHubRefusesTheComment_Should_Throw()
    {
        // Arrange
        var (sut, github) = Build();
        github.Setup(g => g.PostCommentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("forbidden", HttpStatusCode.Forbidden, false));

        // Act
        var act = () => sut.NotifyFailureAsync(_message, "reason", CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<GitHubApiException>();
    }
}
