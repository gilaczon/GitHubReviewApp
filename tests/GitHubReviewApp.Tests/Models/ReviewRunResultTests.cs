namespace GitHubReviewApp.Tests.Models;

/// <summary>Behavior carried by ReviewRunResult: its factories.</summary>
public class ReviewRunResultTests
{
    [Fact]
    public void Factories_GivenSkippedPostedAndFailedOutcomes_Should_FillOnlyTheRelevantFields()
    {
        // Arrange
        var skipped = ReviewRunResult.Skipped(ReviewSkipReason.Draft);
        var failed = ReviewRunResult.Failed();

        // Act
        var posted = ReviewRunResult.Posted(ReviewVerdict.PassWithComments, runCount: 2, includedFileCount: 4, isPartial: true);

        // Assert
        skipped.Should().Be(new ReviewRunResult(ReviewRunStatus.Skipped, ReviewSkipReason.Draft, null, 0, 0, false));
        posted.Should().Be(new ReviewRunResult(ReviewRunStatus.Posted, null, ReviewVerdict.PassWithComments, 2, 4, true));
        failed.Should().Be(new ReviewRunResult(ReviewRunStatus.Failed, null, null, 0, 0, false));
    }
}
