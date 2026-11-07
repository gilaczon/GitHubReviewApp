namespace GitHubReviewApp.Tests.Models;

/// <summary>Behavior carried by ReviewRunResult: its computed members and factories.</summary>
public class ReviewRunResultTests
{
    [Fact]
    public void Factories_GivenSkippedAndPostedOutcomes_Should_FillOnlyTheRelevantFields()
    {
        // Arrange
        var skipped = ReviewRunResult.Skipped(ReviewSkipReason.Draft);

        // Act
        var posted = ReviewRunResult.Posted(filesReviewed: 4, findingCount: 2, isPartial: true);

        // Assert
        skipped.Should().Be(new ReviewRunResult(ReviewRunStatus.Skipped, ReviewSkipReason.Draft, 0, 0, false));
        posted.Should().Be(new ReviewRunResult(ReviewRunStatus.Posted, null, 4, 2, true));
    }
}
