namespace GitHubReviewApp.ReviewPreview.Tests;

public class PreviewRunnerTests
{
    private const string Token = "gho_token";
    private const string HeadSha = "0123456789abcdef0123456789abcdef01234567";
    private const string BaseSha = "1111111111111111111111111111111111111111";
    private const string Title = "Add retries";
    private const string CommentBody = "PASS\n\nAdds retries.";

    private static readonly PullRequestLink _link = new("my-org", "my-service", 7);

    private readonly Mock<IGitHubAppAuthService> _auth = new();
    private readonly Mock<IGitHubService> _github = new();
    private readonly Mock<IReviewOrchestrator> _orchestrator = new();
    private readonly Mock<ISlackPoster> _slack = new();
    private readonly PreviewCapture _capture = new();

    public PreviewRunnerTests()
    {
        _auth.Setup(a => a.GetInstallationTokenAsync(It.IsAny<long>(), "my-service", It.IsAny<CancellationToken>())).ReturnsAsync(Token);
        _github.Setup(g => g.GetPullRequestAsync("my-org", "my-service", 7, Token, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PullRequestInfo("open", false, Title, HeadSha, BaseSha, 3, 40, 10));
    }

    private PreviewRunner CreateSut()
    {
        return new PreviewRunner(_auth.Object, _github.Object, _orchestrator.Object, _capture, _slack.Object, NullLogger<PreviewRunner>.Instance);
    }

    [Fact]
    public async Task RunAsync_GivenAPullRequest_Should_ReviewItAtItsHeadAsTheQueueWould()
    {
        // Arrange
        _orchestrator.Setup(o => o.ReviewPullRequestAsync(It.IsAny<ReviewQueueMessage>(), It.IsAny<CancellationToken>()))
            .Callback(() => _capture.RecordComment(CommentBody))
            .ReturnsAsync(ReviewRunResult.Posted(ReviewVerdict.Pass, 2, 3, false));

        // Act
        await CreateSut().RunAsync(_link, CancellationToken.None);

        // Assert
        _orchestrator.Verify(o => o.ReviewPullRequestAsync(
            new ReviewQueueMessage("my-org", "my-service", 7, Title, BaseSha, HeadSha, 0), It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task RunAsync_GivenAPostedReview_Should_SendTheCommentToSlack()
    {
        // Arrange
        _orchestrator.Setup(o => o.ReviewPullRequestAsync(It.IsAny<ReviewQueueMessage>(), It.IsAny<CancellationToken>()))
            .Callback(() => _capture.RecordComment(CommentBody))
            .ReturnsAsync(ReviewRunResult.Posted(ReviewVerdict.Pass, 2, 3, false));

        // Act
        var result = await CreateSut().RunAsync(_link, CancellationToken.None);

        // Assert
        result.Verdict.Should().Be(ReviewVerdict.Pass);
        _slack.Verify(s => s.PostAsync(SlackMessageFormatter.FormatReview(_link, Title, CommentBody), It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task RunAsync_GivenAFailedReview_Should_SendTheReviewFailedCommentToSlack()
    {
        // Arrange — "Review failed" is a comment too
        const string failedBody = "Review failed\n\nThe automated review could not be completed.";
        _orchestrator.Setup(o => o.ReviewPullRequestAsync(It.IsAny<ReviewQueueMessage>(), It.IsAny<CancellationToken>()))
            .Callback(() => _capture.RecordComment(failedBody))
            .ReturnsAsync(ReviewRunResult.Failed());

        // Act
        await CreateSut().RunAsync(_link, CancellationToken.None);

        // Assert
        _slack.Verify(s => s.PostAsync(It.Is<string>(text => text.Contains("*Review failed*")), It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task RunAsync_GivenASkippedReview_Should_SayWhyInSlack()
    {
        // Arrange
        _orchestrator.Setup(o => o.ReviewPullRequestAsync(It.IsAny<ReviewQueueMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ReviewRunResult.Skipped(ReviewSkipReason.NothingToReview));

        // Act
        await CreateSut().RunAsync(_link, CancellationToken.None);

        // Assert
        _slack.Verify(
            s => s.PostAsync(SlackMessageFormatter.FormatSkip(_link, Title, ReviewSkipReason.NothingToReview), It.IsAny<CancellationToken>()),
            Times.Once());
    }

    [Fact]
    public async Task RunAsync_GivenAReviewThatLeftNoComment_Should_ThrowAndPostNothing()
    {
        // Arrange
        _orchestrator.Setup(o => o.ReviewPullRequestAsync(It.IsAny<ReviewQueueMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ReviewRunResult.Posted(ReviewVerdict.Pass, 2, 3, false));

        // Act
        var act = () => CreateSut().RunAsync(_link, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*without the comment*");
        _slack.Verify(s => s.PostAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task RunAsync_GivenTheReviewThrows_Should_PostNothing()
    {
        // Arrange
        _orchestrator.Setup(o => o.ReviewPullRequestAsync(It.IsAny<ReviewQueueMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("the review ran out of time"));

        // Act
        var act = () => CreateSut().RunAsync(_link, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<TimeoutException>();
        _slack.Verify(s => s.PostAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
    }
}
