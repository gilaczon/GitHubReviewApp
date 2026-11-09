namespace GitHubReviewApp.Tests.Functions;

[Collection(TelemetryCollection.Name)]
public class ReviewProcessorTests
{
    private static readonly ReviewQueueMessage _sampleMessage = new(
        Owner: "my-org",
        Repo: "my-service",
        PrNumber: 7,
        PrTitle: "feat: add feature",
        BaseSha: "abc123",
        HeadSha: "def456",
        InstallationId: 12345678L);

    private static (ReviewProcessor Sut, Mock<IReviewOrchestrator> Orchestrator, Mock<IReviewFailureNotifier> Notifier) BuildSut(
        ReviewRunResult? result = null,
        Mock<IGitHubAppAuthService>? auth = null,
        ILogger<ReviewProcessor>? logger = null,
        TimeProvider? timeProvider = null,
        bool isReviewEnabled = true)
    {
        var orchestrator = new Mock<IReviewOrchestrator>();
        orchestrator
            .Setup(o => o.ReviewPullRequestAsync(It.IsAny<ReviewQueueMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result ?? ReviewRunResult.Posted(ReviewVerdict.PassWithComments, 2, 2, false));

        var notifier = new Mock<IReviewFailureNotifier>();
        notifier
            .Setup(n => n.NotifyFailureAsync(It.IsAny<ReviewQueueMessage>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var runtime = new Mock<IRuntimeSettings>();
        runtime.Setup(r => r.Current).Returns(RuntimeSettings.Default with { IsReviewEnabled = isReviewEnabled });

        var sut = new ReviewProcessor(
            orchestrator.Object,
            notifier.Object,
            (auth ?? new Mock<IGitHubAppAuthService>()).Object,
            runtime.Object,
            logger ?? NullLogger<ReviewProcessor>.Instance,
            timeProvider);
        return (sut, orchestrator, notifier);
    }

    private static void SetupOrchestratorToThrow(Mock<IReviewOrchestrator> orchestrator, Exception exception)
    {
        orchestrator
            .Setup(o => o.ReviewPullRequestAsync(It.IsAny<ReviewQueueMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(exception);
    }

    private static void SetupNotifierToThrow(Mock<IReviewFailureNotifier> notifier, Exception exception)
    {
        notifier
            .Setup(n => n.NotifyFailureAsync(It.IsAny<ReviewQueueMessage>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(exception);
    }

    public static IEnumerable<object[]> TransientFailures()
    {
        yield return [new HttpRequestException("connection reset")];
        yield return [new GitHubApiException("bad gateway", HttpStatusCode.BadGateway, false)];
        yield return [new GitHubApiException("rate limited", HttpStatusCode.Forbidden, true)];
        yield return [new TimeoutException("timed out")];
        yield return [new TaskCanceledException("The request was canceled due to the configured timeout.")];
    }

    public static IEnumerable<object[]> PermanentFailures()
    {
        yield return [new InvalidOperationException("Secret 'GitHubReviewAppAnthropicApiKey' is not configured.")];
        yield return [new GitHubApiException("not found", HttpStatusCode.NotFound, false)];
        yield return [new GitHubApiException("forbidden", HttpStatusCode.Forbidden, false)];
        yield return [new JsonException("bad payload")];
    }

    // ── Normal processing ────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_GivenValidMessage_Should_ReviewItWithTheHostsCancellationToken()
    {
        // Arrange
        var (sut, orchestrator, _) = BuildSut();
        using var cancellation = new CancellationTokenSource();

        // Act
        await sut.RunAsync(_sampleMessage, cancellation.Token);

        // Assert
        orchestrator.Verify(o => o.ReviewPullRequestAsync(_sampleMessage, cancellation.Token), Times.Once());
    }

    [Theory]
    [InlineData(2, false, "full")]
    [InlineData(2, true, "partial")]
    [InlineData(0, true, "none")]
    public async Task RunAsync_GivenAPostedReview_Should_CountItAsProcessedAndTagHowMuchOfThePullRequestItCovered(int includedFileCount, bool isPartial, string coverage)
    {
        // Arrange — a review that reviewed nothing is posted, but it is not a success an operator should count as one
        using var metrics = new MetricsRecorder();
        var (sut, _, _) = BuildSut(ReviewRunResult.Posted(ReviewVerdict.Pass, 2, includedFileCount, isPartial));

        // Act
        await sut.RunAsync(_sampleMessage, CancellationToken.None);

        // Assert
        metrics.Sum("github_review_app.reviews.processed", ("coverage", coverage)).Should().Be(1);
        metrics.Sum("github_review_app.reviews.processed").Should().Be(1);
        metrics.Sum("github_review_app.reviews.failed").Should().Be(0);
    }

    public static IEnumerable<object[]> FinishedReviews()
    {
        yield return [ReviewRunResult.Posted(ReviewVerdict.Pass, 2, 2, false), "pass", "2"];
        yield return [ReviewRunResult.Posted(ReviewVerdict.PassWithComments, 1, 2, false), "pass_with_comments", "1"];
        yield return [ReviewRunResult.Posted(ReviewVerdict.Fail, 2, 2, false), "fail", "2"];
        yield return [ReviewRunResult.Failed(), "review_failed", "0"];
    }

    [Theory]
    [MemberData(nameof(FinishedReviews))]
    public async Task RunAsync_GivenAFinishedReview_Should_CountItAsProcessedTaggedWithItsResultAndCompletedRuns(
        ReviewRunResult runResult, string expectedResult, string expectedRuns)
    {
        // Arrange
        using var metrics = new MetricsRecorder();
        var (sut, _, notifier) = BuildSut(runResult);

        // Act
        await sut.RunAsync(_sampleMessage, CancellationToken.None);

        // Assert
        metrics.Sum("github_review_app.reviews.processed", ("result", expectedResult), ("runs", expectedRuns)).Should().Be(1);
        metrics.Sum("github_review_app.reviews.processed").Should().Be(1);
        metrics.Sum("github_review_app.reviews.failed").Should().Be(0, "a review that ended as Review failed has already told the author");
        notifier.Verify(n => n.NotifyFailureAsync(It.IsAny<ReviewQueueMessage>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task RunAsync_GivenAReviewThatFailed_Should_LogItAndMarkTheSpanOk()
    {
        // Arrange — the review ran and its "Review failed" result was published, which is the app working as designed
        using var spans = new SpanRecorder(ActivitySources.ReviewProcessorName);
        var logger = new ListLogger<ReviewProcessor>();
        var (sut, _, _) = BuildSut(ReviewRunResult.Failed(), logger: logger);

        // Act
        await sut.RunAsync(_sampleMessage, CancellationToken.None);

        // Assert
        spans.Spans.Should().ContainSingle().Which.Status.Should().Be(ActivityStatusCode.Ok);
        logger.Entries.Should().Contain(entry => entry.Level == LogLevel.Information && entry.Message.Contains("Review failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_GivenASkippedReview_Should_NotCountItAsProcessedOrFailedAndShouldTagTheSpanWithTheReason()
    {
        // Arrange
        using var metrics = new MetricsRecorder();
        using var spans = new SpanRecorder(ActivitySources.ReviewProcessorName);
        var (sut, _, notifier) = BuildSut(ReviewRunResult.Skipped(ReviewSkipReason.Draft));

        // Act
        await sut.RunAsync(_sampleMessage, CancellationToken.None);

        // Assert
        metrics.Sum("github_review_app.reviews.processed").Should().Be(0);
        metrics.Sum("github_review_app.reviews.failed").Should().Be(0);
        var span = spans.Spans.Should().ContainSingle().Subject;
        span.GetTagItem("review.skipped_reason").Should().Be("Draft");
        span.Status.Should().Be(ActivityStatusCode.Ok);
        notifier.Verify(n => n.NotifyFailureAsync(It.IsAny<ReviewQueueMessage>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task RunAsync_GivenReviewsSwitchedOff_Should_SkipWithoutCallingTheOrchestrator()
    {
        // Arrange
        using var metrics = new MetricsRecorder();
        using var spans = new SpanRecorder(ActivitySources.ReviewProcessorName);
        var (sut, orchestrator, notifier) = BuildSut(isReviewEnabled: false);

        // Act
        await sut.RunAsync(_sampleMessage, CancellationToken.None);

        // Assert
        orchestrator.Verify(o => o.ReviewPullRequestAsync(It.IsAny<ReviewQueueMessage>(), It.IsAny<CancellationToken>()), Times.Never());
        notifier.Verify(n => n.NotifyFailureAsync(It.IsAny<ReviewQueueMessage>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
        var span = spans.Spans.Should().ContainSingle().Subject;
        span.GetTagItem("review.skipped_reason").Should().Be("SwitchedOff");
        span.Status.Should().Be(ActivityStatusCode.Ok);
        metrics.Sum("github_review_app.reviews.processed").Should().Be(0);
        metrics.Sum("github_review_app.reviews.failed").Should().Be(0);
        metrics.Sum("github_review_app.reviews.skipped", ("reason", "SwitchedOff")).Should().Be(1);
        metrics.Has("github_review_app.review.duration", ("outcome", "skipped")).Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_GivenASuccessAndAFailure_Should_RecordTheReviewDurationForBothTaggedWithTheirOutcome()
    {
        // Arrange
        using var metrics = new MetricsRecorder();
        var (okSut, _, _) = BuildSut();
        var (failingSut, failingOrchestrator, _) = BuildSut();
        SetupOrchestratorToThrow(failingOrchestrator, new InvalidOperationException("permanent"));

        // Act
        await okSut.RunAsync(_sampleMessage, CancellationToken.None);
        await failingSut.RunAsync(_sampleMessage, CancellationToken.None);

        // Assert
        metrics.Has("github_review_app.review.duration", ("outcome", "posted")).Should().BeTrue();
        metrics.Has("github_review_app.review.duration", ("outcome", "failed")).Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_GivenAReviewThatTakesAKnownTime_Should_RecordTheDurationInMilliseconds()
    {
        // Arrange — the histogram's buckets are in milliseconds, so any other unit would put every review in the first or the last bucket
        using var metrics = new MetricsRecorder();
        var time = new FakeTimeProvider();
        var (sut, orchestrator, _) = BuildSut(timeProvider: time);
        orchestrator
            .Setup(o => o.ReviewPullRequestAsync(It.IsAny<ReviewQueueMessage>(), It.IsAny<CancellationToken>()))
            .Callback(() => time.Advance(TimeSpan.FromMilliseconds(1_500)))
            .ReturnsAsync(ReviewRunResult.Posted(ReviewVerdict.Pass, 2, 2, false));

        // Act
        await sut.RunAsync(_sampleMessage, CancellationToken.None);

        // Assert
        metrics.Sum("github_review_app.review.duration", ("outcome", "posted")).Should().Be(1_500);
    }

    public static IEnumerable<object[]> SkippedPostedAndFailedRuns()
    {
        yield return [ReviewRunResult.Skipped(ReviewSkipReason.Draft), "skipped"];
        yield return [ReviewRunResult.Posted(ReviewVerdict.Pass, 2, 1, false), "posted"];
        yield return [ReviewRunResult.Failed(), "review_failed"];
    }

    [Theory]
    [MemberData(nameof(SkippedPostedAndFailedRuns))]
    public async Task RunAsync_GivenAReviewThatWasSkippedPostedOrFailed_Should_TagTheDurationWithThatOutcome(ReviewRunResult runResult, string expectedOutcome)
    {
        // Arrange
        using var metrics = new MetricsRecorder();
        var (sut, _, _) = BuildSut(runResult);

        // Act
        await sut.RunAsync(_sampleMessage, CancellationToken.None);

        // Assert
        metrics.Has("github_review_app.review.duration", ("outcome", expectedOutcome)).Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_GivenValidMessage_Should_EmitASpanWithPullRequestContextTags()
    {
        // Arrange
        using var spans = new SpanRecorder(ActivitySources.ReviewProcessorName);
        var (sut, _, _) = BuildSut();

        // Act
        await sut.RunAsync(_sampleMessage, CancellationToken.None);

        // Assert
        var span = spans.Spans.Should().ContainSingle().Subject;
        span.GetTagItem("pr.owner").Should().Be(_sampleMessage.Owner);
        span.GetTagItem("pr.repo").Should().Be(_sampleMessage.Repo);
        span.GetTagItem("pr.number").Should().Be(_sampleMessage.PrNumber);
        span.GetTagItem("pr.head_sha").Should().Be(_sampleMessage.HeadSha);
        span.Status.Should().Be(ActivityStatusCode.Ok);
    }

    [Fact]
    public async Task RunAsync_GivenAPostedReview_Should_TagTheSpanWithItsResultRunsFilesAndPartialFlag()
    {
        // Arrange — ids and counts only, nothing the model or the author wrote
        using var spans = new SpanRecorder(ActivitySources.ReviewProcessorName);
        var (sut, _, _) = BuildSut(ReviewRunResult.Posted(ReviewVerdict.Fail, runCount: 1, includedFileCount: 3, isPartial: true));

        // Act
        await sut.RunAsync(_sampleMessage, CancellationToken.None);

        // Assert
        var span = spans.Spans.Should().ContainSingle().Subject;
        span.GetTagItem("review.result").Should().Be("fail");
        span.GetTagItem("review.runs").Should().Be(1);
        span.GetTagItem("review.files_included").Should().Be(3);
        span.GetTagItem("review.partial").Should().Be(true);
    }

    [Fact]
    public async Task RunAsync_GivenAReviewThatFailed_Should_TagTheSpanAsReviewFailedWithNoRuns()
    {
        // Arrange
        using var spans = new SpanRecorder(ActivitySources.ReviewProcessorName);
        var (sut, _, _) = BuildSut(ReviewRunResult.Failed());

        // Act
        await sut.RunAsync(_sampleMessage, CancellationToken.None);

        // Assert
        var span = spans.Spans.Should().ContainSingle().Subject;
        span.GetTagItem("review.result").Should().Be("review_failed");
        span.GetTagItem("review.runs").Should().Be(0);
        span.GetTagItem("review.files_included").Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_GivenValidMessage_Should_NotExportThePullRequestTitleBecauseOwnerRepoNumberAndCommitAlreadyIdentifyIt()
    {
        // Arrange — the title is the author's free text from what may be a private repository, and a span is sent to a third-party service
        using var spans = new SpanRecorder(ActivitySources.ReviewProcessorName);
        var (sut, _, _) = BuildSut();

        // Act
        await sut.RunAsync(_sampleMessage, CancellationToken.None);

        // Assert
        var span = spans.Spans.Should().ContainSingle().Subject;
        span.Tags.Should().NotContain(tag => tag.Key.Contains("title", StringComparison.OrdinalIgnoreCase));
        span.Tags.Should().NotContain(tag => Equals(tag.Value, _sampleMessage.PrTitle));
    }

    [Fact]
    public async Task RunAsync_GivenAHostActivity_Should_StartTheSpanAsARoot()
    {
        // Arrange — the host's own invocation span is never exported, so a child of it would be an orphan
        using var spans = new SpanRecorder(ActivitySources.ReviewProcessorName);
        using var hostActivity = new Activity("host.invocation").Start();
        var (sut, _, _) = BuildSut();

        // Act
        await sut.RunAsync(_sampleMessage, CancellationToken.None);

        // Assert
        spans.Spans.Should().ContainSingle().Which.ParentId.Should().BeNull();
    }

    // ── Transient failures: retry ────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(TransientFailures))]
    public async Task RunAsync_GivenATransientFailure_Should_RethrowSoTheQueueRetriesTheMessage(Exception failure)
    {
        // Arrange
        var (sut, orchestrator, _) = BuildSut();
        SetupOrchestratorToThrow(orchestrator, failure);

        // Act
        var act = () => sut.RunAsync(_sampleMessage, CancellationToken.None);

        // Assert
        (await act.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(failure);
    }

    [Fact]
    public async Task RunAsync_GivenATransientFailure_Should_NotTellThePullRequestAuthorYetBecauseTheQueueWillRetry()
    {
        // Arrange
        var (sut, orchestrator, notifier) = BuildSut();
        SetupOrchestratorToThrow(orchestrator, new HttpRequestException("connection reset"));

        // Act
        var act = () => sut.RunAsync(_sampleMessage, CancellationToken.None);
        await act.Should().ThrowAsync<HttpRequestException>();

        // Assert
        notifier.Verify(n => n.NotifyFailureAsync(It.IsAny<ReviewQueueMessage>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task RunAsync_GivenATransientFailure_Should_CountATransientFailureAndMarkTheSpanAsAnError()
    {
        // Arrange
        using var metrics = new MetricsRecorder();
        using var spans = new SpanRecorder(ActivitySources.ReviewProcessorName);
        var (sut, orchestrator, _) = BuildSut();
        SetupOrchestratorToThrow(orchestrator, new HttpRequestException("connection reset"));

        // Act
        var act = () => sut.RunAsync(_sampleMessage, CancellationToken.None);
        await act.Should().ThrowAsync<HttpRequestException>();

        // Assert
        metrics.Sum("github_review_app.reviews.failed", ("kind", "transient")).Should().Be(1);
        metrics.Sum("github_review_app.reviews.processed").Should().Be(0);
        metrics.Has("github_review_app.review.duration", ("outcome", "retry")).Should().BeTrue();
        spans.Spans.Should().ContainSingle().Which.Status.Should().Be(ActivityStatusCode.Error);
    }

    // ── Permanent failures: stop and tell the author ─────────────────────────

    [Theory]
    [MemberData(nameof(PermanentFailures))]
    public async Task RunAsync_GivenAPermanentFailure_Should_NotRethrowBecauseRetryingCannotHelp(Exception failure)
    {
        // Arrange
        var (sut, orchestrator, _) = BuildSut();
        SetupOrchestratorToThrow(orchestrator, failure);

        // Act
        var act = () => sut.RunAsync(_sampleMessage, CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RunAsync_GivenAPermanentFailure_Should_TellThePullRequestAuthorWithoutLeakingTheExceptionMessage()
    {
        // Arrange
        var (sut, orchestrator, notifier) = BuildSut();
        SetupOrchestratorToThrow(orchestrator, new InvalidOperationException("Secret 'GitHubReviewAppAnthropicApiKey' is not configured."));

        // Act
        await sut.RunAsync(_sampleMessage, CancellationToken.None);

        // Assert
        notifier.Verify(n => n.NotifyFailureAsync(
            _sampleMessage,
            It.Is<string>(reason => reason == ReviewFailureReasons.PermanentError && !reason.Contains("GitHubReviewAppAnthropicApiKey")),
            It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task RunAsync_GivenAPermanentFailure_Should_CountAPermanentFailureAndMarkTheSpanAsAnError()
    {
        // Arrange
        using var metrics = new MetricsRecorder();
        using var spans = new SpanRecorder(ActivitySources.ReviewProcessorName);
        var (sut, orchestrator, _) = BuildSut();
        SetupOrchestratorToThrow(orchestrator, new InvalidOperationException("config"));

        // Act
        await sut.RunAsync(_sampleMessage, CancellationToken.None);

        // Assert
        metrics.Sum("github_review_app.reviews.failed", ("kind", "permanent")).Should().Be(1);
        spans.Spans.Should().ContainSingle().Which.Status.Should().Be(ActivityStatusCode.Error);
    }

    public static IEnumerable<object[]> OneTransientAndOnePermanentFailure()
    {
        yield return [new HttpRequestException("connection reset")];
        yield return [new InvalidOperationException("config")];
    }

    [Theory]
    [MemberData(nameof(OneTransientAndOnePermanentFailure))]
    public async Task RunAsync_GivenAFailure_Should_RecordTheExceptionOnTheSpanSoTheTraceSaysWhatWentWrong(Exception failure)
    {
        // Arrange — the error status says that the review failed, and the exception event carries the type and the message that say why
        using var spans = new SpanRecorder(ActivitySources.ReviewProcessorName);
        var (sut, orchestrator, _) = BuildSut();
        SetupOrchestratorToThrow(orchestrator, failure);

        // Act
        await Record.ExceptionAsync(() => sut.RunAsync(_sampleMessage, CancellationToken.None));

        // Assert
        var span = spans.Spans.Should().ContainSingle().Subject;
        span.StatusDescription.Should().Be(failure.Message);
        var recorded = span.Events.Should().ContainSingle(spanEvent => spanEvent.Name == "exception").Subject;
        recorded.Tags.Should().Contain(tag => tag.Key == "exception.type" && Equals(tag.Value, failure.GetType().FullName));
        recorded.Tags.Should().Contain(tag => tag.Key == "exception.message" && Equals(tag.Value, failure.Message));
    }

    // ── A token GitHub no longer accepts ─────────────────────────────────────

    [Fact]
    public async Task RunAsync_GivenGitHubRefusesTheInstallationTokenAsUnauthorized_Should_ForgetItAndRethrowSoTheRetryAsksForANewOne()
    {
        // Arrange — the cached copy would be handed out again until its own expiry, failing the retry and every other review of the repository the same way
        var failure = new GitHubApiException("Bad credentials", HttpStatusCode.Unauthorized, false);
        var auth = new Mock<IGitHubAppAuthService>();
        var (sut, orchestrator, notifier) = BuildSut(auth: auth);
        SetupOrchestratorToThrow(orchestrator, failure);

        // Act
        var act = () => sut.RunAsync(_sampleMessage, CancellationToken.None);

        // Assert
        (await act.Should().ThrowAsync<GitHubApiException>()).Which.Should().BeSameAs(failure);
        auth.Verify(a => a.ForgetInstallationToken(_sampleMessage.InstallationId, _sampleMessage.Repo), Times.Once());
        notifier.Verify(
            n => n.NotifyFailureAsync(It.IsAny<ReviewQueueMessage>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never(),
            "a retry will usually succeed with a new token, so the author is not told yet");
    }

    [Theory]
    [MemberData(nameof(TransientFailures))]
    public async Task RunAsync_GivenATransientFailureThatIsNotAnUnauthorizedResponse_Should_KeepTheCachedToken(Exception failure)
    {
        // Arrange — a rate limit or an outage says nothing against the token
        var auth = new Mock<IGitHubAppAuthService>();
        var (sut, orchestrator, _) = BuildSut(auth: auth);
        SetupOrchestratorToThrow(orchestrator, failure);

        // Act
        var act = () => sut.RunAsync(_sampleMessage, CancellationToken.None);
        await act.Should().ThrowAsync<Exception>();

        // Assert
        auth.Verify(a => a.ForgetInstallationToken(It.IsAny<long>(), It.IsAny<string>()), Times.Never());
    }

    // ── The AI provider's monthly spend limit ────────────────────────────────

    [Fact]
    public async Task RunAsync_GivenTheAiSpendLimitIsReached_Should_NotRetryAndShouldTellTheAuthorLikeAnyOtherFailureThatRetryingCannotFix()
    {
        // Arrange — the provider reports its monthly cap as a 429, but every attempt fails until the cap is raised or the month ends
        var spendLimit = await AnthropicSdkFailure.CaptureAsync(AnthropicFixtures.SpendLimitReached());
        var (sut, orchestrator, notifier) = BuildSut();
        SetupOrchestratorToThrow(orchestrator, spendLimit);

        // Act
        var act = () => sut.RunAsync(_sampleMessage, CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();
        notifier.Verify(n => n.NotifyFailureAsync(_sampleMessage, ReviewFailureReasons.PermanentError, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task RunAsync_GivenTheAiSpendLimitIsReached_Should_CountItUnderItsOwnKindSoAnOperatorCanAlertOnIt()
    {
        // Arrange — every pull request fails the same way until someone acts, which no other permanent failure implies
        using var metrics = new MetricsRecorder();
        var spendLimit = await AnthropicSdkFailure.CaptureAsync(AnthropicFixtures.SpendLimitReached());
        var (sut, orchestrator, _) = BuildSut();
        SetupOrchestratorToThrow(orchestrator, spendLimit);

        // Act
        await sut.RunAsync(_sampleMessage, CancellationToken.None);

        // Assert
        metrics.Sum("github_review_app.reviews.failed", ("kind", "ai_spend_limit")).Should().Be(1);
        metrics.Sum("github_review_app.reviews.failed", ("kind", "permanent")).Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_GivenTheAiSpendLimitIsReached_Should_LogThatTheLimitIsTheCauseAtErrorLevel()
    {
        // Arrange
        var logger = new ListLogger<ReviewProcessor>();
        var spendLimit = await AnthropicSdkFailure.CaptureAsync(AnthropicFixtures.SpendLimitReached());
        var (sut, orchestrator, _) = BuildSut(logger: logger);
        SetupOrchestratorToThrow(orchestrator, spendLimit);

        // Act
        await sut.RunAsync(_sampleMessage, CancellationToken.None);

        // Assert
        logger.Entries.Should().Contain(entry => entry.Level == LogLevel.Error && entry.Message.Contains("spend limit", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(PermanentFailures))]
    public async Task RunAsync_GivenAPermanentFailureThatIsNotAnUnauthorizedResponse_Should_KeepTheCachedToken(Exception failure)
    {
        // Arrange — a missing permission or a missing resource says nothing against the token
        var auth = new Mock<IGitHubAppAuthService>();
        var (sut, orchestrator, _) = BuildSut(auth: auth);
        SetupOrchestratorToThrow(orchestrator, failure);

        // Act
        await sut.RunAsync(_sampleMessage, CancellationToken.None);

        // Assert
        auth.Verify(a => a.ForgetInstallationToken(It.IsAny<long>(), It.IsAny<string>()), Times.Never());
    }

    // ── A notice that cannot be posted ───────────────────────────────────────

    [Fact]
    public async Task RunAsync_GivenAPermanentFailureAndTheNoticeIsRefusedForGood_Should_NotThrowButCountAndLogTheLostNotice()
    {
        // Arrange — retrying cannot make GitHub accept it, so the message is not retried; the operator is told instead
        using var metrics = new MetricsRecorder();
        var logger = new ListLogger<ReviewProcessor>();
        var (sut, orchestrator, notifier) = BuildSut(logger: logger);
        SetupOrchestratorToThrow(orchestrator, new InvalidOperationException("config"));
        SetupNotifierToThrow(notifier, new GitHubApiException("forbidden", HttpStatusCode.Forbidden, false));

        // Act
        var act = () => sut.RunAsync(_sampleMessage, CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();
        metrics.Sum("github_review_app.failure_notices.failed").Should().Be(1);
        metrics.Sum("github_review_app.failure_notices.failed", ("outcome", "refused")).Should().Be(1);
        logger.Entries.Should().Contain(entry => entry.Level == LogLevel.Error && entry.Message.Contains("not been told", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_GivenAPermanentFailureAndTheNoticeHitsATransientError_Should_CountTheFailedAttemptSoAnOutageShowsInTheMetric()
    {
        // Arrange — the message is retried, so the author may still be told, but a long outage must not leave the notice counter at zero
        using var metrics = new MetricsRecorder();
        var (sut, orchestrator, notifier) = BuildSut();
        SetupOrchestratorToThrow(orchestrator, new InvalidOperationException("config"));
        SetupNotifierToThrow(notifier, new GitHubApiException("bad gateway", HttpStatusCode.BadGateway, false));

        // Act
        var act = () => sut.RunAsync(_sampleMessage, CancellationToken.None);
        await act.Should().ThrowAsync<GitHubApiException>();

        // Assert
        metrics.Sum("github_review_app.failure_notices.failed", ("outcome", "retry")).Should().Be(1);
        metrics.Sum("github_review_app.failure_notices.failed", ("outcome", "refused")).Should().Be(0);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task RunAsync_GivenAPermanentFailureAndTheNoticeHitsATransientError_Should_RethrowSoTheQueueTriesAgainRatherThanLeaveTheAuthorUntold(HttpStatusCode status)
    {
        // Arrange
        var (sut, orchestrator, notifier) = BuildSut();
        SetupOrchestratorToThrow(orchestrator, new InvalidOperationException("config"));
        SetupNotifierToThrow(notifier, new GitHubApiException("unavailable", status, status == HttpStatusCode.TooManyRequests));

        // Act
        var act = () => sut.RunAsync(_sampleMessage, CancellationToken.None);

        // Assert
        (await act.Should().ThrowAsync<GitHubApiException>()).Which.StatusCode.Should().Be(status);
    }

    [Fact]
    public async Task RunAsync_GivenAPermanentFailureAndTheNoticeIsInterruptedByShutdown_Should_RethrowWithoutCountingAFailedNotice()
    {
        // Arrange — a shutdown says nothing against the notice: the message comes back and the notice is tried again
        using var metrics = new MetricsRecorder();
        using var cancellation = new CancellationTokenSource();
        var (sut, orchestrator, notifier) = BuildSut();
        SetupOrchestratorToThrow(orchestrator, new InvalidOperationException("config"));
        notifier
            .Setup(n => n.NotifyFailureAsync(It.IsAny<ReviewQueueMessage>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await cancellation.CancelAsync();
                throw new ObjectDisposedException("HttpClient");
            });

        // Act
        var act = () => sut.RunAsync(_sampleMessage, cancellation.Token);

        // Assert
        await act.Should().ThrowAsync<ObjectDisposedException>();
        metrics.Sum("github_review_app.failure_notices.failed").Should().Be(0);
    }

    // ── Shutdown ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_GivenTheHostIsShuttingDown_Should_RethrowWithoutCountingAFailureOrNotifyingAnyone()
    {
        // Arrange
        using var metrics = new MetricsRecorder();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var (sut, orchestrator, notifier) = BuildSut();
        SetupOrchestratorToThrow(orchestrator, new OperationCanceledException(cancellation.Token));

        // Act
        var act = () => sut.RunAsync(_sampleMessage, cancellation.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        metrics.Sum("github_review_app.reviews.failed").Should().Be(0);
        metrics.Sum("github_review_app.reviews.interrupted").Should().Be(1);
        metrics.Has("github_review_app.review.duration", ("outcome", "interrupted")).Should().BeTrue();
        notifier.Verify(n => n.NotifyFailureAsync(It.IsAny<ReviewQueueMessage>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task RunAsync_GivenAnyExceptionWhileTheHostIsShuttingDown_Should_RethrowAndNotTellTheAuthorTheReviewFailedForGood()
    {
        // Arrange — a client disposed underneath the call during teardown throws something that is not a cancellation
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var (sut, orchestrator, notifier) = BuildSut();
        SetupOrchestratorToThrow(orchestrator, new ObjectDisposedException("HttpClient"));

        // Act
        var act = () => sut.RunAsync(_sampleMessage, cancellation.Token);

        // Assert
        await act.Should().ThrowAsync<ObjectDisposedException>();
        notifier.Verify(n => n.NotifyFailureAsync(It.IsAny<ReviewQueueMessage>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task RunAsync_GivenTheSameExceptionWhileTheHostIsRunning_Should_TreatItAsAPermanentFailure()
    {
        // Arrange — the shutdown rule depends on the host token, not on the exception type
        var (sut, orchestrator, notifier) = BuildSut();
        SetupOrchestratorToThrow(orchestrator, new ObjectDisposedException("HttpClient"));

        // Act
        await sut.RunAsync(_sampleMessage, CancellationToken.None);

        // Assert
        notifier.Verify(n => n.NotifyFailureAsync(_sampleMessage, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once());
    }
}
