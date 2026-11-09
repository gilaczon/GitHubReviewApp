namespace GitHubReviewApp.Tests.Models;

/// <summary>Behavior carried by PullRequestFile: its computed members and factories.</summary>
public class PullRequestFileTests
{
    [Theory]
    [InlineData("removed", true)]
    [InlineData("REMOVED", true)]
    [InlineData("modified", false)]
    [InlineData("renamed", false)]
    public void IsRemoved_GivenAFileStatus_Should_BeTrueOnlyForRemoved(string status, bool isExpected)
    {
        // Arrange
        var file = new PullRequestFile("a.cs", status, 0, 0, null);

        // Act
        var result = file.IsRemoved;

        // Assert
        result.Should().Be(isExpected);
    }
}
