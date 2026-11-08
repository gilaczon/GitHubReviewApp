namespace GitHubReviewApp.Tests.Models;

/// <summary>The counts ReviewInput gives of its files, by what happened to each file's diff.</summary>
public class ReviewInputTests
{
    [Fact]
    public void Counts_GivenFilesOfEveryInclusion_Should_CountEachKindInItsOwnTotal()
    {
        // Arrange — a different number of each kind, so a count that took the wrong kind shows a different number
        var files = Files(DiffInclusion.Included, 2)
            .Concat(Files(DiffInclusion.Partial, 1))
            .Concat(Files(DiffInclusion.OverBudget, 4))
            .Concat(Files(DiffInclusion.Lockfile, 2))
            .Concat(Files(DiffInclusion.BinaryOrMinified, 3))
            .Concat(Files(DiffInclusion.NoDiff, 6))
            .ToList();

        // Act
        var input = new ReviewInput("message", files);

        // Assert — included counts the partial files too, and not read counts lockfiles with binaries
        input.IncludedFileCount.Should().Be(3);
        input.PartialFileCount.Should().Be(1);
        input.OverBudgetFileCount.Should().Be(4);
        input.NotReadFileCount.Should().Be(5);
        input.NoDiffFileCount.Should().Be(6);
    }

    private static IEnumerable<ReviewInputFile> Files(DiffInclusion inclusion, int count)
    {
        return Enumerable.Range(0, count)
            .Select(index => new ReviewInputFile(new PullRequestFile($"src/{inclusion}{index}.cs", "modified", 1, 0, null), RiskTier.Source, inclusion));
    }
}
