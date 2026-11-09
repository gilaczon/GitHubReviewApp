namespace GitHubReviewApp.Tests.Models;

/// <summary>Behavior carried by ClaudeUsage: its computed members and factories.</summary>
public class ClaudeUsageTests
{
    [Fact]
    public void Plus_GivenTwoUsages_Should_AddEveryCounter()
    {
        // Arrange
        var first = new ClaudeUsage(100, 10, 5, 1);
        var second = new ClaudeUsage(200, 20, 6, 2);

        // Act
        var total = first.Plus(second);

        // Assert
        total.Should().Be(new ClaudeUsage(300, 30, 11, 3));
        ClaudeUsage.Empty.Plus(first).Should().Be(first);
    }
}
