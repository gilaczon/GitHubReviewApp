namespace GitHubReviewApp.Tests.Services;

public class ReviewFailureNotifierTests
{
    private const string Token = "ghs_token";
    private const string HeadSha = "0123456789abcdef0123456789abcdef01234567";
    private const string EarlierSha = "1111111111111111111111111111111111111111";
    private const string AppLogin = "my-reviewer[bot]";
    private const string Reason = "The AI service could not be reached.";

    private static readonly ReviewQueueMessage _message = new("my-org", "my-service", 7, "title", "basesha", HeadSha, 12345L);

    private static (ReviewFailureNotifier Sut, Mock<IGitHubService> GitHub, Mock<IGitHubAppAuthService> Auth) Build(
        PullRequestInfo? pullRequest = null,
        IReadOnlyList<IssueComment>? comments = null,
        bool canWriteChecks = true)
    {
        var auth = new Mock<IGitHubAppAuthService>();
        auth.Setup(a => a.GetInstallationTokenAsync(12345L, "my-service", It.IsAny<CancellationToken>())).ReturnsAsync(Token);
        auth.Setup(a => a.GetAppLoginAsync(It.IsAny<CancellationToken>())).ReturnsAsync(AppLogin);
        auth.Setup(a => a.CanWriteChecks(12345L, "my-service")).Returns(canWriteChecks);

        var github = new Mock<IGitHubService>();
        github.Setup(g => g.GetPullRequestAsync("my-org", "my-service", 7, Token, It.IsAny<CancellationToken>()))
            .ReturnsAsync(pullRequest ?? OpenPullRequest());
        github.Setup(g => g.ListIssueCommentsAsync("my-org", "my-service", 7, Token, It.IsAny<CancellationToken>()))
            .ReturnsAsync(comments ?? []);
        github.Setup(g => g.CreateCheckRunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CheckRunRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(900L);
        github.Setup(g => g.CreateIssueCommentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(901L);
        github.Setup(g => g.UpdateIssueCommentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var publisher = new ReviewPublisher(github.Object, NullLogger<ReviewPublisher>.Instance);
        var sut = new ReviewFailureNotifier(auth.Object, github.Object, publisher, NullLogger<ReviewFailureNotifier>.Instance);

        return (sut, github, auth);
    }

    private static PullRequestInfo OpenPullRequest(string state = "open", bool isDraft = false, string headSha = HeadSha)
    {
        return new PullRequestInfo(state, isDraft, "title", headSha, "basesha", 3, 40, 10, "description", "dev1", "main", "feature/retries");
    }

    private static IssueComment Comment(long id, string author, string sha, ReviewRecordOutcome outcome)
    {
        return new IssueComment(id, author, $"text\n\n{ReviewCommentMarker.For(sha, outcome)}");
    }

    private static void VerifyNothingPublished(Mock<IGitHubService> github)
    {
        github.Verify(
            g => g.CreateCheckRunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CheckRunRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never());
        github.Verify(
            g => g.CreateIssueCommentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never());
        github.Verify(
            g => g.UpdateIssueCommentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never());
    }

    [Fact]
    public async Task NotifyFailureAsync_GivenAnOpenPullRequest_Should_PublishReviewFailedWithANeutralCheck()
    {
        // Arrange
        CheckRunRequest? checkRun = null;
        string? posted = null;
        var (sut, github, _) = Build();
        github.Setup(g => g.CreateCheckRunAsync("my-org", "my-service", It.IsAny<CheckRunRequest>(), Token, It.IsAny<CancellationToken>()))
            .Callback<string, string, CheckRunRequest, string, CancellationToken>((_, _, request, _, _) => checkRun = request)
            .ReturnsAsync(900L);
        github.Setup(g => g.CreateIssueCommentAsync("my-org", "my-service", 7, It.IsAny<string>(), Token, It.IsAny<CancellationToken>()))
            .Callback<string, string, int, string, string, CancellationToken>((_, _, _, body, _, _) => posted = body)
            .ReturnsAsync(901L);

        // Act
        await sut.NotifyFailureAsync(_message, Reason, CancellationToken.None);

        // Assert
        posted.Should().NotBeNull();
        posted.Should().StartWith("Review failed - no verdict").And.Contain(Reason);
        ReviewCommentMarker.TryRead(posted, out var markedSha, out var outcome).Should().BeTrue();
        markedSha.Should().Be(HeadSha);
        outcome.Should().Be(ReviewRecordOutcome.Failed);
        checkRun.Should().NotBeNull();
        checkRun!.HeadSha.Should().Be(HeadSha);
        checkRun.Conclusion.Should().Be(CheckRunConclusion.Neutral);
        checkRun.Title.Should().Be("Review failed");
        checkRun.ExternalId.Should().Be("7");
        checkRun.Summary.Should().Be(ChecklistCommentFormatter.CheckRunSummary(posted!)).And.Contain(Reason).And.NotContain("<!--");
    }

    [Fact]
    public async Task NotifyFailureAsync_GivenThePullRequestsDetails_Should_NameThemInTheFooter()
    {
        // Arrange
        string? posted = null;
        var (sut, github, _) = Build();
        github.Setup(g => g.CreateIssueCommentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, int, string, string, CancellationToken>((_, _, _, body, _, _) => posted = body)
            .ReturnsAsync(901L);

        // Act
        await sut.NotifyFailureAsync(_message, Reason, CancellationToken.None);

        // Assert
        posted.Should().Contain("my-org/my-service #7").And.Contain("feature/retries → main · by dev1").And.Contain("3 files +40 -10");
    }

    [Fact]
    public async Task NotifyFailureAsync_GivenNoChecksPermission_Should_PostOnlyTheComment()
    {
        // Arrange
        var (sut, github, _) = Build(canWriteChecks: false);

        // Act
        await sut.NotifyFailureAsync(_message, Reason, CancellationToken.None);

        // Assert
        github.Verify(
            g => g.CreateCheckRunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CheckRunRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never());
        github.Verify(g => g.CreateIssueCommentAsync("my-org", "my-service", 7, It.IsAny<string>(), Token, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task NotifyFailureAsync_GivenThisPullRequestWasReviewedAtTheHead_Should_StayQuiet()
    {
        // Arrange — e.g. a second queued message for the same commit completed while this one was failing
        var (sut, github, _) = Build(comments: [Comment(12, AppLogin, HeadSha, ReviewRecordOutcome.Reviewed)]);

        // Act
        await sut.NotifyFailureAsync(_message, Reason, CancellationToken.None);

        // Assert
        VerifyNothingPublished(github);
    }

    [Theory]
    [InlineData("closed", false, HeadSha)]
    [InlineData("open", true, HeadSha)]
    [InlineData("open", false, "ffffffffffffffffffffffffffffffffffffffff")]
    public async Task NotifyFailureAsync_GivenAClosedDraftOrSupersededPullRequest_Should_StayQuiet(string state, bool isDraft, string headSha)
    {
        // Arrange
        var (sut, github, _) = Build(OpenPullRequest(state, isDraft, headSha));

        // Act
        await sut.NotifyFailureAsync(_message, Reason, CancellationToken.None);

        // Assert
        VerifyNothingPublished(github);
    }

    [Fact]
    public async Task NotifyFailureAsync_GivenOurEarlierComment_Should_UpdateIt()
    {
        // Arrange
        var (sut, github, _) = Build(comments:
        [
            Comment(30, AppLogin, EarlierSha, ReviewRecordOutcome.Reviewed),
            Comment(12, AppLogin, EarlierSha, ReviewRecordOutcome.Failed)
        ]);

        // Act
        await sut.NotifyFailureAsync(_message, Reason, CancellationToken.None);

        // Assert
        github.Verify(g => g.UpdateIssueCommentAsync("my-org", "my-service", 12L, It.IsAny<string>(), Token, It.IsAny<CancellationToken>()), Times.Once());
        github.Verify(
            g => g.CreateIssueCommentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never());
    }

    [Fact]
    public async Task NotifyFailureAsync_GivenOnlyAHumanCommentQuotesTheReviewedMarker_Should_StillTellTheAuthor()
    {
        // Arrange — only this app's own comment proves the commit was reviewed; anyone can paste the marker text
        var (sut, github, _) = Build(comments: [Comment(12, "octocat", HeadSha, ReviewRecordOutcome.Reviewed)]);

        // Act
        await sut.NotifyFailureAsync(_message, Reason, CancellationToken.None);

        // Assert
        github.Verify(g => g.CreateIssueCommentAsync("my-org", "my-service", 7, It.IsAny<string>(), Token, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task NotifyFailureAsync_GivenOurEarlierFailureNoticeForThisCommit_Should_TryToPublishAgain()
    {
        // Arrange — a "Review failed" notice never counts as a review, so the next failure replaces it
        var (sut, github, _) = Build(comments: [Comment(12, AppLogin, HeadSha, ReviewRecordOutcome.Failed)]);

        // Act
        await sut.NotifyFailureAsync(_message, Reason, CancellationToken.None);

        // Assert
        github.Verify(g => g.UpdateIssueCommentAsync("my-org", "my-service", 12L, It.IsAny<string>(), Token, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Theory]
    [InlineData("pull-request", HttpStatusCode.Unauthorized, 1)]
    [InlineData("comments", HttpStatusCode.Unauthorized, 1)]
    [InlineData("pull-request", HttpStatusCode.Forbidden, 0)]
    public async Task NotifyFailureAsync_GivenGitHubAnswers401_Should_ForgetTheTokenBeforeRethrowingSoTheRetryFetchesANewOne(
        string failingCall, HttpStatusCode status, int expectedForgetCount)
    {
        // Arrange — a 401 is retried on the promise that the token is dropped first; without this, the poison handler would retry the notice
        // with the same rejected token until its attempts ran out. A 403 says nothing against the token, so it is kept.
        var (sut, github, auth) = Build();
        var failure = new GitHubApiException("refused", status, false);
        github.Setup(g => g.GetPullRequestAsync("my-org", "my-service", 7, Token, It.IsAny<CancellationToken>()))
            .Returns(() => failingCall == "pull-request" ? Task.FromException<PullRequestInfo>(failure) : Task.FromResult(OpenPullRequest()));
        github.Setup(g => g.ListIssueCommentsAsync("my-org", "my-service", 7, Token, It.IsAny<CancellationToken>()))
            .Returns(() => failingCall == "comments" ? Task.FromException<IReadOnlyList<IssueComment>>(failure) : Task.FromResult<IReadOnlyList<IssueComment>>([]));

        // Act
        var act = () => sut.NotifyFailureAsync(_message, Reason, CancellationToken.None);

        // Assert
        (await act.Should().ThrowAsync<GitHubApiException>()).Which.Should().BeSameAs(failure);
        auth.Verify(a => a.ForgetInstallationToken(12345L, "my-service"), Times.Exactly(expectedForgetCount));
    }

    [Fact]
    public async Task NotifyFailureAsync_GivenGitHubRefusesTheComment_Should_Throw()
    {
        // Arrange
        var (sut, github, _) = Build();
        github.Setup(g => g.CreateIssueCommentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("forbidden", HttpStatusCode.Forbidden, false));

        // Act
        var act = () => sut.NotifyFailureAsync(_message, Reason, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<GitHubApiException>();
    }
}
