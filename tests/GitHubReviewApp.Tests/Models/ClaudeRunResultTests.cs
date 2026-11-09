namespace GitHubReviewApp.Tests.Models;

/// <summary>A run's total usage is what every attempt the API made for it used, each declined or fallback attempt included.</summary>
public class ClaudeRunResultTests
{
    [Fact]
    public void TotalUsage_GivenAnAttemptOnEachOfTwoModels_Should_AddUpTheTokensOfBoth()
    {
        // Arrange
        var result = new ClaudeRunResult(
            RunOutcome.Completed,
            Answer: null,
            "claude-sonnet-5",
            [
                new ModelAttempt("claude-sonnet-5-5", new ClaudeUsage(1_000, 20, 300, 50)),
                new ModelAttempt("claude-sonnet-5", new ClaudeUsage(1_100, 400, 0, 900))
            ]);

        // Act
        var total = result.TotalUsage;

        // Assert
        total.Should().Be(new ClaudeUsage(2_100, 420, 300, 950));
    }

    [Fact]
    public void TotalUsage_GivenNoAttempts_Should_BeEmpty()
    {
        // Arrange — a request the service refused ran nothing
        var result = new ClaudeRunResult(RunOutcome.Rejected, Answer: null, string.Empty, [], "HTTP 400");

        // Act
        var total = result.TotalUsage;

        // Assert
        total.Should().Be(ClaudeUsage.Empty);
    }
}
