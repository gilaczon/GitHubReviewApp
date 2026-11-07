namespace GitHubReviewApp.Tests.Models;

/// <summary>Behavior carried by ReviewPlan: its computed members and factories.</summary>
public class ReviewPlanTests
{
    [Fact]
    public void Counts_GivenAPlanWithPartialAndSkippedFiles_Should_DeriveTheTotals()
    {
        // Arrange
        var partial = new PlannedFile(new PullRequestFile("a.cs", "modified", 1, 0, "p"), "12345", IsPartial: true);
        var whole = new PlannedFile(new PullRequestFile("b.cs", "modified", 1, 0, "p"), "123", IsPartial: false);
        var plan = new ReviewPlan(
            [new ReviewChunk(0, [partial]), new ReviewChunk(1, [whole])],
            [new SkippedFile("c.lock", FileSkipReason.DependencyLockfile)],
            TotalFileCount: 3);

        // Act
        var counts = (plan.ReviewedFileCount, plan.PartialFileCount, plan.HasReviewableContent, plan.Chunks[0].TotalBytes, plan.Chunks[1].TotalBytes);

        // Assert
        counts.Should().Be((2, 1, true, 5, 3));
    }

    [Fact]
    public void HasReviewableContent_GivenAPlanWithNoChunks_Should_BeFalse()
    {
        // Arrange
        var plan = new ReviewPlan([], [new SkippedFile("a.lock", FileSkipReason.DependencyLockfile)], 1);

        // Act
        var result = plan.HasReviewableContent;

        // Assert
        result.Should().BeFalse();
    }
}
