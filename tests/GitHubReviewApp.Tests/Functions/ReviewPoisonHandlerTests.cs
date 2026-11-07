namespace GitHubReviewApp.Tests.Functions;

[Collection(TelemetryCollection.Name)]
public class ReviewPoisonHandlerTests
{
    private static readonly ReviewQueueMessage _sampleMessage =
        new("my-org", "my-service", 7, "title", "basesha", "headsha", 12345L);

    [Fact]
    public async Task RunAsync_GivenAMessageThatExhaustedItsRetries_Should_TellThePullRequestAuthorWhy()
    {
        // Arrange
        var notifier = new Mock<IReviewFailureNotifier>();
        var sut = new ReviewPoisonHandler(notifier.Object, NullLogger<ReviewPoisonHandler>.Instance);
        using var cancellation = new CancellationTokenSource();

        // Act
        await sut.RunAsync(_sampleMessage, cancellation.Token);

        // Assert
        notifier.Verify(n => n.NotifyFailureAsync(_sampleMessage, ReviewFailureReasons.RetriesExhausted, cancellation.Token), Times.Once());
    }

    [Fact]
    public async Task RunAsync_GivenAMessageThatExhaustedItsRetries_Should_CountItAsAFailureOfKindExhausted()
    {
        // Arrange
        using var metrics = new MetricsRecorder();
        var notifier = new Mock<IReviewFailureNotifier>();
        var sut = new ReviewPoisonHandler(notifier.Object, NullLogger<ReviewPoisonHandler>.Instance);

        // Act
        await sut.RunAsync(_sampleMessage, CancellationToken.None);

        // Assert
        metrics.Sum("github_review_app.reviews.failed", ("kind", "exhausted")).Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_GivenAMessageThatExhaustedItsRetries_Should_RecordASpanNamingThePullRequestAndMarkingItExhausted()
    {
        // Arrange
        using var spans = new SpanRecorder(ActivitySources.ReviewProcessorName);
        var sut = new ReviewPoisonHandler(new Mock<IReviewFailureNotifier>().Object, NullLogger<ReviewPoisonHandler>.Instance);

        // Act
        await sut.RunAsync(_sampleMessage, CancellationToken.None);

        // Assert
        var span = spans.Spans.Should().ContainSingle().Subject;
        span.OperationName.Should().Be("ReviewPoisonHandler.RunAsync");
        span.Status.Should().Be(ActivityStatusCode.Ok);
        span.GetTagItem("pr.owner").Should().Be("my-org");
        span.GetTagItem("pr.repo").Should().Be("my-service");
        span.GetTagItem("pr.number").Should().Be(7);
        span.GetTagItem("pr.head_sha").Should().Be("headsha");
        span.GetTagItem("review.exhausted").Should().Be(true);
    }

    [Fact]
    public async Task RunAsync_GivenAHostActivity_Should_StartTheSpanAsARoot()
    {
        // Arrange — the host's own invocation span is never exported, so a child of it would be an orphan
        using var spans = new SpanRecorder(ActivitySources.ReviewProcessorName);
        using var hostActivity = new Activity("host.invocation").Start();
        var sut = new ReviewPoisonHandler(new Mock<IReviewFailureNotifier>().Object, NullLogger<ReviewPoisonHandler>.Instance);

        // Act
        await sut.RunAsync(_sampleMessage, CancellationToken.None);

        // Assert
        spans.Spans.Should().ContainSingle().Which.ParentId.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_GivenTheNoticeCannotBePosted_Should_MarkTheSpanAsAnErrorAndRecordTheFailure()
    {
        // Arrange
        using var spans = new SpanRecorder(ActivitySources.ReviewProcessorName);
        var notifier = new Mock<IReviewFailureNotifier>();
        notifier
            .Setup(n => n.NotifyFailureAsync(It.IsAny<ReviewQueueMessage>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("bad gateway", HttpStatusCode.BadGateway, false));
        var sut = new ReviewPoisonHandler(notifier.Object, NullLogger<ReviewPoisonHandler>.Instance);

        // Act
        var act = () => sut.RunAsync(_sampleMessage, CancellationToken.None);
        await act.Should().ThrowAsync<GitHubApiException>();

        // Assert
        var span = spans.Spans.Should().ContainSingle().Subject;
        span.Status.Should().Be(ActivityStatusCode.Error);
        span.Events.Should().Contain(spanEvent => spanEvent.Name == "exception");
    }

    [Fact]
    public async Task RunAsync_GivenTheNoticeIsRefusedForGood_Should_NotThrowBecauseRetryingCannotChangeTheAnswer()
    {
        // Arrange — a poison message that is retried is read again as often as the host allows, and every read would meet the same refusal
        var notifier = new Mock<IReviewFailureNotifier>();
        notifier
            .Setup(n => n.NotifyFailureAsync(It.IsAny<ReviewQueueMessage>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("forbidden", HttpStatusCode.Forbidden, false));
        var sut = new ReviewPoisonHandler(notifier.Object, NullLogger<ReviewPoisonHandler>.Instance);

        // Act
        var act = () => sut.RunAsync(_sampleMessage, CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RunAsync_GivenTheNoticeIsRefusedForGood_Should_CountAndLogTheLostNoticeAndRecordTheFailureOnTheSpan()
    {
        // Arrange — the author was not told, which only the operator can learn from here
        using var metrics = new MetricsRecorder();
        using var spans = new SpanRecorder(ActivitySources.ReviewProcessorName);
        var logger = new ListLogger<ReviewPoisonHandler>();
        var notifier = new Mock<IReviewFailureNotifier>();
        notifier
            .Setup(n => n.NotifyFailureAsync(It.IsAny<ReviewQueueMessage>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("forbidden", HttpStatusCode.Forbidden, false));
        var sut = new ReviewPoisonHandler(notifier.Object, logger);

        // Act
        await sut.RunAsync(_sampleMessage, CancellationToken.None);

        // Assert
        metrics.Sum("github_review_app.failure_notices.failed").Should().Be(1);
        metrics.Sum("github_review_app.failure_notices.failed", ("outcome", "refused")).Should().Be(1);
        logger.Entries.Should().Contain(entry => entry.Level == LogLevel.Error && entry.Message.Contains("not been told", StringComparison.Ordinal));
        var span = spans.Spans.Should().ContainSingle().Subject;
        span.Status.Should().Be(ActivityStatusCode.Error);
        span.Events.Should().Contain(spanEvent => spanEvent.Name == "exception");
    }

    [Fact]
    public async Task RunAsync_GivenTheNoticeHitsATransientError_Should_CountTheFailedAttemptSoAnOutageShowsInTheMetric()
    {
        // Arrange — the message is retried and, if GitHub stays down, ends in the queue nobody reads. Without this count a long outage leaves authors
        // untold and the notice counter at zero.
        using var metrics = new MetricsRecorder();
        var notifier = new Mock<IReviewFailureNotifier>();
        notifier
            .Setup(n => n.NotifyFailureAsync(It.IsAny<ReviewQueueMessage>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("bad gateway", HttpStatusCode.BadGateway, false));
        var sut = new ReviewPoisonHandler(notifier.Object, NullLogger<ReviewPoisonHandler>.Instance);

        // Act
        var act = () => sut.RunAsync(_sampleMessage, CancellationToken.None);
        await act.Should().ThrowAsync<GitHubApiException>();

        // Assert
        metrics.Sum("github_review_app.failure_notices.failed", ("outcome", "retry")).Should().Be(1);
        metrics.Sum("github_review_app.failure_notices.failed", ("outcome", "refused")).Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_GivenTheHostIsShuttingDownWhileTheNoticeIsPosted_Should_RethrowWithoutCountingAFailedNotice()
    {
        // Arrange — the cancellation says nothing about the notice, so the message must come back rather than be given up on
        using var metrics = new MetricsRecorder();
        using var cancellation = new CancellationTokenSource();
        var notifier = new Mock<IReviewFailureNotifier>();
        notifier
            .Setup(n => n.NotifyFailureAsync(It.IsAny<ReviewQueueMessage>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                cancellation.Cancel();
                throw new ObjectDisposedException("client torn down under the call");
            });
        var sut = new ReviewPoisonHandler(notifier.Object, NullLogger<ReviewPoisonHandler>.Instance);

        // Act
        var act = () => sut.RunAsync(_sampleMessage, cancellation.Token);

        // Assert
        await act.Should().ThrowAsync<ObjectDisposedException>();
        metrics.Sum("github_review_app.failure_notices.failed").Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_GivenTheNoticeCannotBePosted_Should_ThrowSoTheMessageIsRetriedInsteadOfLost()
    {
        // Arrange
        var notifier = new Mock<IReviewFailureNotifier>();
        notifier
            .Setup(n => n.NotifyFailureAsync(It.IsAny<ReviewQueueMessage>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("bad gateway", HttpStatusCode.BadGateway, false));
        var sut = new ReviewPoisonHandler(notifier.Object, NullLogger<ReviewPoisonHandler>.Instance);

        // Act
        var act = () => sut.RunAsync(_sampleMessage, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<GitHubApiException>();
    }
}
