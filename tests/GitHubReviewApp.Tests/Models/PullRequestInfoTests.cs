namespace GitHubReviewApp.Tests.Models;

/// <summary>Behavior carried by PullRequestInfo: its computed members and factories.</summary>
public class PullRequestInfoTests
{
    [Theory]
    [InlineData("open", true)]
    [InlineData("OPEN", true)]
    [InlineData("closed", false)]
    [InlineData("", false)]
    public void IsOpen_GivenAState_Should_BeTrueOnlyForOpenIgnoringCase(string state, bool isExpected)
    {
        // Arrange
        var pullRequest = new PullRequestInfo(state, false, "t", "h", "b", 1, 1, 1);

        // Act
        var result = pullRequest.IsOpen;

        // Assert
        result.Should().Be(isExpected);
    }

    [Fact]
    public void ChangedLineCount_GivenAdditionsAndDeletions_Should_SumThem()
    {
        // Arrange
        var pullRequest = new PullRequestInfo("open", false, "t", "h", "b", 3, 420, 90);

        // Act
        var result = pullRequest.ChangedLineCount;

        // Assert
        result.Should().Be(510);
    }
}
