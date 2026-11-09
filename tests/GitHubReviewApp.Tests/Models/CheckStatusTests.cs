namespace GitHubReviewApp.Tests.Models;

/// <summary>The numeric order of CheckStatus is its strictness, and the merge keeps the larger value.</summary>
public class CheckStatusTests
{
    [Fact]
    public void CheckStatus_GivenItsValues_Should_RankNotApplicableBelowPassBelowConcernBelowFail()
    {
        // Arrange
        var strictnessOrder = new[] { CheckStatus.NotApplicable, CheckStatus.Pass, CheckStatus.Concern, CheckStatus.Fail };

        // Act
        var values = Enum.GetValues<CheckStatus>();

        // Assert
        values.Should().BeInAscendingOrder();
        values.Should().Equal(strictnessOrder);
    }
}
