namespace GitHubReviewApp.Tests.Models;

/// <summary>The numeric order of ReviewVerdict is its badness, and the merge takes the run whose own verdict is the larger value as the stricter run.</summary>
public class ReviewVerdictTests
{
    [Fact]
    public void ReviewVerdict_GivenItsValues_Should_RankPassBelowPassWithCommentsBelowFail()
    {
        // Arrange
        var badnessOrder = new[] { ReviewVerdict.Pass, ReviewVerdict.PassWithComments, ReviewVerdict.Fail };

        // Act
        var values = Enum.GetValues<ReviewVerdict>();

        // Assert
        values.Should().BeInAscendingOrder();
        values.Should().Equal(badnessOrder);
    }
}
