namespace GitHubReviewApp.Tests.Models;

/// <summary>The words a verdict and its serious count are shown as: the headline, the meaning and the verdict's own name.</summary>
public class ReviewOutcomeTests
{
    [Theory]
    [InlineData(ReviewVerdict.Pass, 0, "PASS")]
    [InlineData(ReviewVerdict.PassWithComments, 0, "PASS WITH COMMENTS")]
    [InlineData(ReviewVerdict.PassWithComments, 1, "PASS WITH COMMENTS (1 serious)")]
    [InlineData(ReviewVerdict.Fail, 0, "FAIL")]
    [InlineData(ReviewVerdict.Fail, 2, "FAIL (2 serious)")]
    public void Headline_GivenAVerdictAndSeriousCount_Should_MatchTheSpec(ReviewVerdict verdict, int seriousCount, string expectedHeadline)
    {
        // Arrange
        var outcome = new ReviewOutcome(verdict, seriousCount);

        // Act
        var headline = outcome.Headline;

        // Assert
        headline.Should().Be(expectedHeadline);
    }

    [Theory]
    [InlineData(ReviewVerdict.Fail, 0, "changes required")]
    [InlineData(ReviewVerdict.Fail, 1, "changes required")]
    [InlineData(ReviewVerdict.PassWithComments, 0, "fine to approve; the author should look at the comments")]
    [InlineData(ReviewVerdict.PassWithComments, 2, "no security problem, but read the serious comment before approving")]
    [InlineData(ReviewVerdict.Pass, 0, "no concerns")]
    public void Meaning_GivenAVerdictAndSeriousCount_Should_MatchTheSpec(ReviewVerdict verdict, int seriousCount, string expectedMeaning)
    {
        // Arrange
        var outcome = new ReviewOutcome(verdict, seriousCount);

        // Act
        var meaning = outcome.Meaning;

        // Assert
        meaning.Should().Be(expectedMeaning);
    }

    [Theory]
    [InlineData(ReviewVerdict.Pass, "PASS")]
    [InlineData(ReviewVerdict.PassWithComments, "PASS WITH COMMENTS")]
    [InlineData(ReviewVerdict.Fail, "FAIL")]
    public void VerdictText_GivenAVerdict_Should_ReturnItsName(ReviewVerdict verdict, string expectedText)
    {
        // Arrange
        var outcome = new ReviewOutcome(verdict, SeriousCount: 0);

        // Act
        var verdictText = outcome.VerdictText;

        // Assert
        verdictText.Should().Be(expectedText);
    }
}
