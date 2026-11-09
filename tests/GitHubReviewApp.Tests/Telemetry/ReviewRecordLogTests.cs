namespace GitHubReviewApp.Tests.Telemetry;

public class ReviewRecordLogTests
{
    private const string HeadSha = "0123456789abcdef0123456789abcdef01234567";

    private static readonly ReviewQueueMessage _message = new("my-org", "my-service", 7, "a title", "base", HeadSha, 12345L);

    private static readonly ModelChoice _choice = new("claude-sonnet-5-5", IsOpus: false, "small_change");

    private static readonly ReviewInput _input = new(
        "the user message",
        [
            new ReviewInputFile(new PullRequestFile("src/Foo.cs", "modified", 1, 0, "@@ -1 +1 @@\n+a"), RiskTier.Source, DiffInclusion.Included),
            new ReviewInputFile(new PullRequestFile("src/Big.cs", "modified", 9, 0, "@@ -1 +1 @@\n+a"), RiskTier.Source, DiffInclusion.Partial),
            new ReviewInputFile(new PullRequestFile("package-lock.json", "modified", 5, 5, "@@ -1 +1 @@\n+a"), RiskTier.NotReadable, DiffInclusion.Lockfile)
        ]);

    private static RunRecord CompletedRun()
    {
        return new RunRecord(
            0,
            "Completed",
            "claude-sonnet-5-5",
            ReviewVerdict.PassWithComments,
            "standards-design=pass functionality=concern* performance=pass security=pass testing=pass error-handling=pass scalability=pass dependencies=na",
            new ClaudeUsage(1_000, 500, 200, 100),
            0.007m);
    }

    private static MergedReview Merged()
    {
        var checks = ChecklistVerdict.Complete(Checklist.Order.ToDictionary(id => id, id => new RatedCheck(id, CheckStatus.Pass, string.Empty, false)));

        return new MergedReview(checks, [], "a summary", string.Empty, 1, false, ReviewVerdict.PassWithComments, null);
    }

    [Fact]
    public void Write_GivenARecord_Should_LogOneInformationLineWithTheIdsVerdictCostAndStatuses()
    {
        // Arrange
        var logger = new ListLogger<ReviewRecordLogTests>();
        var runs = new[]
        {
            CompletedRun(),
            new RunRecord(1, "transient_failure", "claude-sonnet-5-5", null, string.Empty, ClaudeUsage.Empty, null)
        };

        // Act
        ReviewRecordLog.Write(logger, _message, _choice, runs, Merged(), new ReviewOutcome(ReviewVerdict.PassWithComments, 1), _input, 0.012m, TimeSpan.FromMilliseconds(1_500));

        // Assert
        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Information);
        entry.Message.Should().StartWith("Review record for my-org/my-service#7 at " + HeadSha);
        entry.Message.Should().Contain("model claude-sonnet-5-5 (sonnet, small_change)");
        entry.Message.Should().Contain("run 0 Completed claude-sonnet-5-5 pass_with_comments [standards-design=pass functionality=concern* ");
        entry.Message.Should().Contain("tokens in 1000 out 500 cache_read 200 cache_write 100 cost 0.007");
        entry.Message.Should().Contain("run 1 transient_failure claude-sonnet-5-5 none [] tokens in 0 out 0 cache_read 0 cache_write 0 cost unknown");
        entry.Message.Should().Contain("result pass_with_comments, 1 serious, runs agreed False");
        entry.Message.Should().Contain("files 2 included, 1 partial, 1 left out");
        entry.Message.Should().Contain("cost 0.012 USD").And.Contain("took 1500 ms");
    }

    [Fact]
    public void Write_GivenAReviewThatFailed_Should_LogReviewFailedAndAnUnknownCost()
    {
        // Arrange
        var logger = new ListLogger<ReviewRecordLogTests>();
        var runs = new[] { new RunRecord(0, "Refused", "claude-sonnet-5-5", null, string.Empty, ClaudeUsage.Empty, null) };

        // Act
        ReviewRecordLog.Write(logger, _message, _choice, runs, merged: null, outcome: null, _input, totalCost: null, TimeSpan.Zero);

        // Assert
        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Message.Should().Contain("result review_failed, 0 serious, runs agreed False").And.Contain("cost unknown USD");
    }

    [Theory]
    [InlineData(null, "review_failed")]
    [InlineData(ReviewVerdict.Pass, "pass")]
    [InlineData(ReviewVerdict.PassWithComments, "pass_with_comments")]
    [InlineData(ReviewVerdict.Fail, "fail")]
    public void ResultText_GivenAVerdictOrNone_Should_GiveTheMetricsName(ReviewVerdict? verdict, string expected)
    {
        // Arrange — the same names tag the reviews counter, so the record and the metrics can be read side by side
        var input = verdict;

        // Act
        var text = ReviewRecordLog.ResultText(input);

        // Assert
        text.Should().Be(expected);
    }

    [Fact]
    public void Statuses_GivenAnAnswer_Should_ListEveryCheckInOrderMarkingSeriousConcernsAndChecksLeftOut()
    {
        // Arrange — the answer rates seven checks, one of them a serious concern, and leaves Dependencies out
        var rated = Checklist.Order
            .Where(id => id != CheckId.Dependencies)
            .ToDictionary(id => id, id => new RatedCheck(id, CheckStatus.Pass, string.Empty, false));
        rated[CheckId.Functionality] = new RatedCheck(CheckId.Functionality, CheckStatus.Concern, "note", true);
        rated[CheckId.Security] = new RatedCheck(CheckId.Security, CheckStatus.Fail, "note", false);
        rated[CheckId.Scalability] = new RatedCheck(CheckId.Scalability, CheckStatus.NotApplicable, string.Empty, false);
        var answer = new RunAnswer(rated, [], "summary", string.Empty);

        // Act
        var statuses = ReviewRecordLog.Statuses(answer);

        // Assert
        statuses.Should().Be(
            "standards-design=pass functionality=concern* performance=pass security=fail testing=pass error-handling=pass scalability=na dependencies=missing");
    }
}
