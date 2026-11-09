namespace GitHubReviewApp.Tests.Models;

/// <summary>A run's outcome is written as its name into the runs metric and the review record, so the names are part of what dashboards read.</summary>
public class RunOutcomeTests
{
    [Fact]
    public void RunOutcome_GivenItsValues_Should_KeepTheNamesTheRunsMetricIsTaggedWith()
    {
        // Arrange
        var expectedNames = new[] { "Completed", "OutputTruncated", "Refused", "InvalidOutput", "Rejected" };

        // Act
        var names = Enum.GetNames<RunOutcome>();

        // Assert
        names.Should().Equal(expectedNames);
    }
}
