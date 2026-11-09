namespace GitHubReviewApp.Tests.Services;

public class ReviewFailureReasonsTests
{
    public static IEnumerable<object[]> Notices()
    {
        yield return [ReviewFailureReasons.PermanentError];
        yield return [ReviewFailureReasons.RetriesExhausted];
        yield return [ReviewFailureReasons.AllRefused];
        yield return [ReviewFailureReasons.AllTruncated];
        yield return [ReviewFailureReasons.AllInvalid];
        yield return [ReviewFailureReasons.AllRejected];
        yield return [ReviewFailureReasons.NoneFinished];
    }

    [Theory]
    [MemberData(nameof(Notices))]
    public void Notice_GivenAPullRequestAuthorWhoCannotSeeTheAppsLogs_Should_NotSendThemThere(string notice)
    {
        // Arrange

        // Act
        var hasMentionOfLogs = notice.Contains("log", StringComparison.OrdinalIgnoreCase);

        // Assert
        hasMentionOfLogs.Should().BeFalse("the author is usually not the person who runs the app and cannot read its logs");
    }

    [Theory]
    [MemberData(nameof(Notices))]
    public void Notice_GivenAnyFailure_Should_StayGenericBecauseExceptionDetailBelongsInTheLogs(string notice)
    {
        // Arrange

        // Act
        var hasTechnicalWording = new[] { "exception", "stack", "token", "secret", "key vault", "http " }
            .Any(word => notice.Contains(word, StringComparison.OrdinalIgnoreCase));

        // Assert
        hasTechnicalWording.Should().BeFalse();
    }

    [Fact]
    public void PermanentError_GivenAProblemRetryingWillNotFix_Should_SayItWasStoppedAndWhoToTell()
    {
        // Arrange

        // Act
        var notice = ReviewFailureReasons.PermanentError;

        // Assert
        notice.Should().Contain("retrying would not fix").And.Contain("whoever runs this review app");
    }

    [Fact]
    public void RetriesExhausted_GivenAnyCauseOfTheFailures_Should_NameNoneOfThem()
    {
        // Arrange
        var causes = new[] { "GitHub", "AI service", "network", "temporary problem" };

        // Act
        var notice = ReviewFailureReasons.RetriesExhausted;

        // Assert — the app only knows that every attempt failed: a timeout under its own limits, a spend cap or a changed reply also look like this,
        // so a notice that names an outage would sometimes be wrong
        notice.Should().Contain("after several attempts");
        causes.Where(cause => notice.Contains(cause, StringComparison.OrdinalIgnoreCase)).Should().BeEmpty();
    }

    [Fact]
    public void ModelLevelReasons_GivenEachWayBothReviewsCanEnd_Should_SayWhatHappenedInTheDesignsWords()
    {
        // Arrange

        // Act
        var reasons = new[]
        {
            ReviewFailureReasons.AllRefused,
            ReviewFailureReasons.AllTruncated,
            ReviewFailureReasons.AllInvalid,
            ReviewFailureReasons.AllRejected,
            ReviewFailureReasons.NoneFinished
        };

        // Assert
        reasons.Should().Equal(
            "Both reviews were declined by the AI service's safety checks.",
            "Both reviews ran out of output space before finishing.",
            "Neither review returned a readable answer.",
            "The AI service refused the review request.",
            "Neither review finished.");
    }

    [Fact]
    public void RetriesExhausted_GivenAnAuthorWhoWantsAnotherTry_Should_SayHowAndWhoToTellIfItKeepsHappening()
    {
        // Arrange

        // Act
        var notice = ReviewFailureReasons.RetriesExhausted;

        // Assert
        notice.Should().Contain("new commit").And.Contain("whoever runs this review app");
    }
}
