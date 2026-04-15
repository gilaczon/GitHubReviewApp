namespace GitHubReviewApp.Tests;

public class ReviewProcessorTests
{
    private static readonly ReviewQueueMessage SampleMessage = new(
        Owner: "my-org",
        Repo: "my-service",
        PrNumber: 7,
        PrTitle: "feat: add feature",
        BaseSha: "abc123",
        HeadSha: "def456",
        InstallationId: 12345678L);

    private const string FakeToken  = "ghs_fake_installation_token";
    private const string FakeDiff   = "diff --git a/foo.cs b/foo.cs\n+var x = 1;";
    private const string FakeReview = "## Review\n\nLooks good!";

    // ── Installation token ───────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_GivenValidMessage_Should_GetInstallationTokenWithCorrectInstallationId()
    {
        // Arrange
        var (sut, auth, _, _) = BuildSut();

        // Act
        await sut.RunAsync(SampleMessage);

        // Assert
        auth.Verify(x => x.GetInstallationTokenAsync(SampleMessage.InstallationId), Times.Once());
    }

    // ── Diff fetching ────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_GivenValidMessage_Should_FetchDiffForCorrectOwnerRepoPrNumber()
    {
        // Arrange
        var (sut, _, github, _) = BuildSut();

        // Act
        await sut.RunAsync(SampleMessage);

        // Assert
        github.Verify(x => x.GetPullRequestDiffAsync(
            SampleMessage.Owner, SampleMessage.Repo, SampleMessage.PrNumber,
            It.IsAny<string>()), Times.Once());
    }

    [Fact]
    public async Task RunAsync_GivenValidMessage_Should_FetchDiffUsingInstallationToken()
    {
        // Arrange
        var (sut, _, github, _) = BuildSut();

        // Act
        await sut.RunAsync(SampleMessage);

        // Assert
        github.Verify(x => x.GetPullRequestDiffAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), FakeToken), Times.Once());
    }

    // ── Claude call ──────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_GivenDiffIsAvailable_Should_CallClaudeWithPrTitleAndDiff()
    {
        // Arrange
        var (sut, _, _, claude) = BuildSut(diff: FakeDiff);

        // Act
        await sut.RunAsync(SampleMessage);

        // Assert
        claude.Verify(x => x.ReviewDiffAsync(SampleMessage.PrTitle, FakeDiff), Times.Once());
    }

    // ── Review posting ───────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_GivenReviewGenerated_Should_PostReviewToCorrectRepo()
    {
        // Arrange
        var (sut, _, github, _) = BuildSut(review: FakeReview);

        // Act
        await sut.RunAsync(SampleMessage);

        // Assert
        github.Verify(x => x.PostReviewAsync(
            SampleMessage.Owner, SampleMessage.Repo, SampleMessage.PrNumber,
            FakeReview, FakeToken), Times.Once());
    }

    [Fact]
    public async Task RunAsync_GivenReviewGenerated_Should_PostReviewWithInstallationToken()
    {
        // Arrange
        var (sut, _, github, _) = BuildSut();

        // Act
        await sut.RunAsync(SampleMessage);

        // Assert
        github.Verify(x => x.PostReviewAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), FakeToken), Times.Once());
    }

    // ── Empty / whitespace diff ──────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_GivenEmptyDiff_Should_NotCallClaude()
    {
        // Arrange
        var (sut, _, _, claude) = BuildSut(diff: string.Empty);

        // Act
        await sut.RunAsync(SampleMessage);

        // Assert
        claude.Verify(x => x.ReviewDiffAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never());
    }

    [Fact]
    public async Task RunAsync_GivenEmptyDiff_Should_NotPostReview()
    {
        // Arrange
        var (sut, _, github, _) = BuildSut(diff: string.Empty);

        // Act
        await sut.RunAsync(SampleMessage);

        // Assert
        github.Verify(x => x.PostReviewAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<string>(), It.IsAny<string>()), Times.Never());
    }

    [Fact]
    public async Task RunAsync_GivenWhitespaceDiff_Should_NotCallClaude()
    {
        // Arrange
        var (sut, _, _, claude) = BuildSut(diff: "   \n\t  ");

        // Act
        await sut.RunAsync(SampleMessage);

        // Assert
        claude.Verify(x => x.ReviewDiffAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never());
    }

    [Fact]
    public async Task RunAsync_GivenWhitespaceDiff_Should_NotPostReview()
    {
        // Arrange
        var (sut, _, github, _) = BuildSut(diff: "   \n\t  ");

        // Act
        await sut.RunAsync(SampleMessage);

        // Assert
        github.Verify(x => x.PostReviewAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<string>(), It.IsAny<string>()), Times.Never());
    }

    // ── Metrics ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_GivenSuccessfulReview_Should_IncrementReviewsProcessedCounter()
    {
        // Arrange
        var counts = new Dictionary<string, long>();
        using var listener = BuildMeterListener(counts);

        var (sut, _, _, _) = BuildSut();

        // Act
        await sut.RunAsync(SampleMessage);

        // Assert
        counts.Should().ContainKey("github_review_app.reviews.processed")
            .WhoseValue.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_GivenAuthServiceThrows_Should_IncrementReviewsFailedCounter()
    {
        // Arrange
        var counts = new Dictionary<string, long>();
        using var listener = BuildMeterListener(counts);

        var (sut, auth, _, _) = BuildSut();
        auth.Setup(x => x.GetInstallationTokenAsync(It.IsAny<long>()))
            .ThrowsAsync(new HttpRequestException("GitHub unavailable"));

        // Act
        await sut.RunAsync(SampleMessage);

        // Assert
        counts.Should().ContainKey("github_review_app.reviews.failed")
            .WhoseValue.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_GivenValidMessage_Should_RecordReviewDurationMetric()
    {
        // Arrange
        var durations = new List<double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == AppMeters.MeterName &&
                instrument.Name == "github_review_app.review.duration")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, measurement, _, _) =>
            durations.Add(measurement));
        listener.Start();

        var (sut, _, _, _) = BuildSut();

        // Act
        await sut.RunAsync(SampleMessage);

        // Assert
        durations.Should().ContainSingle(d => d >= 0);
    }

    // ── Activity / span attributes ───────────────────────────────────────────

    [Fact]
    public async Task RunAsync_GivenValidMessage_Should_EmitActivityWithPrContextTags()
    {
        // Arrange
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo  = source => source.Name == ActivitySources.ReviewProcessorName,
            Sample          = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add
        };
        ActivitySource.AddActivityListener(listener);

        var (sut, _, _, _) = BuildSut();

        // Act
        await sut.RunAsync(SampleMessage);

        // Assert
        var span = activities.Should().ContainSingle().Subject;
        span.GetTagItem("pr.owner").Should().Be(SampleMessage.Owner);
        span.GetTagItem("pr.repo").Should().Be(SampleMessage.Repo);
        span.GetTagItem("pr.number").Should().Be(SampleMessage.PrNumber);
        span.GetTagItem("pr.title").Should().Be(SampleMessage.PrTitle);
    }

    [Fact]
    public async Task RunAsync_GivenClaudeServiceThrows_Should_SetActivityStatusToError()
    {
        // Arrange
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo  = source => source.Name == ActivitySources.ReviewProcessorName,
            Sample          = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add
        };
        ActivitySource.AddActivityListener(listener);

        var (sut, _, _, claude) = BuildSut();
        claude.Setup(x => x.ReviewDiffAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new HttpRequestException("Claude API error"));

        // Act
        await sut.RunAsync(SampleMessage);

        // Assert
        var span = activities.Should().ContainSingle().Subject;
        span.Status.Should().Be(ActivityStatusCode.Error);
    }

    // ── Exception handling ───────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_GivenAuthServiceThrows_Should_NotThrow()
    {
        // Arrange
        var (sut, auth, _, _) = BuildSut();
        auth.Setup(x => x.GetInstallationTokenAsync(It.IsAny<long>()))
            .ThrowsAsync(new HttpRequestException("GitHub API unavailable"));

        // Act & Assert
        await sut.Invoking(x => x.RunAsync(SampleMessage)).Should().NotThrowAsync();
    }

    [Fact]
    public async Task RunAsync_GivenGitHubServiceThrows_Should_NotThrow()
    {
        // Arrange
        var (sut, _, github, _) = BuildSut();
        github.Setup(x => x.GetPullRequestDiffAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()))
            .ThrowsAsync(new HttpRequestException("GitHub API error"));

        // Act & Assert
        await sut.Invoking(x => x.RunAsync(SampleMessage)).Should().NotThrowAsync();
    }

    [Fact]
    public async Task RunAsync_GivenClaudeServiceThrows_Should_NotThrow()
    {
        // Arrange
        var (sut, _, _, claude) = BuildSut();
        claude.Setup(x => x.ReviewDiffAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new HttpRequestException("Claude API error"));

        // Act & Assert
        await sut.Invoking(x => x.RunAsync(SampleMessage)).Should().NotThrowAsync();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static MeterListener BuildMeterListener(Dictionary<string, long> counts)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == AppMeters.MeterName)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
            counts[instrument.Name] = counts.GetValueOrDefault(instrument.Name) + measurement);
        listener.Start();
        return listener;
    }

    // ── Factory helper ───────────────────────────────────────────────────────

    private static (ReviewProcessor Sut, Mock<IGitHubAppAuthService> Auth, Mock<IGitHubService> Github, Mock<IClaudeService> Claude)
        BuildSut(string diff = FakeDiff, string review = FakeReview)
    {
        var auth = new Mock<IGitHubAppAuthService>();
        auth.Setup(x => x.GetInstallationTokenAsync(It.IsAny<long>())).ReturnsAsync(FakeToken);

        var github = new Mock<IGitHubService>();
        github.Setup(x => x.GetPullRequestDiffAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(diff);
        github.Setup(x => x.PostReviewAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        var claude = new Mock<IClaudeService>();
        claude.Setup(x => x.ReviewDiffAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(review);

        var sut = new ReviewProcessor(
            auth.Object, github.Object, claude.Object,
            NullLogger<ReviewProcessor>.Instance);

        return (sut, auth, github, claude);
    }
}
