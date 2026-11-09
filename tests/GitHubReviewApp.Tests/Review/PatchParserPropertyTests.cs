namespace GitHubReviewApp.Tests.Review;

/// <summary>
/// Checks the patch parser against ground truth it did not write. Pairs of files are generated at random, an independent diff
/// (<see cref="LcsDiff"/>) turns them into a patch, and the parser must recover from the patch the line numbers the files themselves have:
/// an added line is the line of the new file it says, a removed line is the line of the old file, and a context line is both. Seeds are fixed,
/// so a failure names the case and can be run again.
/// </summary>
public class PatchParserPropertyTests
{
    private const int CaseCount = 250;

    private static readonly string[] _alphabet = ["a", "b", "c", "d", string.Empty, "foo()", "    x = 1;", "}", "// note"];

    private static string[] RandomLines(Random random, int maxLength)
    {
        return Enumerable.Range(0, random.Next(0, maxLength + 1)).Select(_ => _alphabet[random.Next(_alphabet.Length)]).ToArray();
    }

    // Edits the old file at random, so the pair always has plenty in common and plenty of repeated lines to confuse a careless diff.
    private static string[] Edit(Random random, string[] oldLines)
    {
        var result = new List<string>();

        foreach (var line in oldLines)
        {
            var roll = random.NextDouble();

            if (roll < 0.12)
            {
                continue;
            }

            result.Add(roll < 0.27 ? _alphabet[random.Next(_alphabet.Length)] : line);

            if (random.NextDouble() < 0.1)
            {
                result.Add(_alphabet[random.Next(_alphabet.Length)]);
            }
        }

        return result.ToArray();
    }

    private static string? Check(int seed, bool hasShortCounts, bool hasCrlfLineEndings, bool isEmptyContextSpaceStripped)
    {
        var random = new Random(seed);
        var oldLines = RandomLines(random, 40);
        var newLines = Edit(random, oldLines);
        var contextLines = random.Next(0, 5);

        var patch = LcsDiff.CreatePatch(oldLines, newLines, contextLines, hasShortCounts);
        var patchLines = patch.Length == 0 ? [] : patch.Split('\n');

        var shaped = patchLines
            .Select(line => isEmptyContextSpaceStripped && line == " " ? string.Empty : line)
            .Select(line => hasCrlfLineEndings ? line + "\r" : line);
        var parsed = PatchParser.Parse(string.Join("\n", shaped));

        var expectedAdded = patchLines.Count(line => line.StartsWith('+'));
        var expectedRemoved = patchLines.Count(line => line.StartsWith('-'));

        if (parsed.Count(line => line.Kind == PatchLineKind.Added) != expectedAdded
            || parsed.Count(line => line.Kind == PatchLineKind.Removed) != expectedRemoved)
        {
            return $"seed {seed}: the parser counts a different number of added or removed lines than the patch holds";
        }

        var wrong = parsed.FirstOrDefault(line => !IsConsistentWithTheFiles(line, oldLines, newLines));

        return wrong is null ? null : $"seed {seed}: {wrong.Kind} line {wrong.OldLine}/{wrong.NewLine} '{wrong.Text}' does not match the files";
    }

    private static bool IsConsistentWithTheFiles(PatchLine line, string[] oldLines, string[] newLines)
    {
        return line.Kind switch
        {
            PatchLineKind.Added => line.NewLine is { } added && added >= 1 && added <= newLines.Length && newLines[added - 1] == line.Text,
            PatchLineKind.Removed => line.OldLine is { } removed && removed >= 1 && removed <= oldLines.Length && oldLines[removed - 1] == line.Text,
            PatchLineKind.Context => line.OldLine is { } oldNumber && line.NewLine is { } newNumber
                && oldNumber >= 1 && oldNumber <= oldLines.Length && newNumber >= 1 && newNumber <= newLines.Length
                && oldLines[oldNumber - 1] == line.Text && newLines[newNumber - 1] == line.Text,
            _ => true
        };
    }

    private static List<string> Failures(bool hasShortCounts, bool hasCrlfLineEndings, bool isEmptyContextSpaceStripped)
    {
        return Enumerable.Range(1, CaseCount)
            .Select(seed => Check(seed, hasShortCounts, hasCrlfLineEndings, isEmptyContextSpaceStripped))
            .OfType<string>()
            .Take(5)
            .ToList();
    }

    [Fact]
    public void Parse_GivenIndependentlyGeneratedPatches_Should_RecoverTheLineNumbersOfTheFilesThemselves()
    {
        // Arrange

        // Act
        var failures = Failures(hasShortCounts: false, hasCrlfLineEndings: false, isEmptyContextSpaceStripped: false);

        // Assert
        failures.Should().BeEmpty();
    }

    [Fact]
    public void Parse_GivenHunkHeadersThatLeaveOutACountOfOne_Should_RecoverTheLineNumbersOfTheFilesThemselves()
    {
        // Arrange

        // Act
        var failures = Failures(hasShortCounts: true, hasCrlfLineEndings: false, isEmptyContextSpaceStripped: false);

        // Assert
        failures.Should().BeEmpty();
    }

    [Fact]
    public void Parse_GivenCrlfLineEndings_Should_RecoverTheLineNumbersAndTheTextWithoutTheCarriageReturn()
    {
        // Arrange

        // Act
        var failures = Failures(hasShortCounts: false, hasCrlfLineEndings: true, isEmptyContextSpaceStripped: false);

        // Assert
        failures.Should().BeEmpty();
    }

    [Fact]
    public void Parse_GivenEmptyContextLinesThatLostTheirMarkerSpaceInACrlfPatch_Should_StillRecoverTheLineNumbersOfTheFilesThemselves()
    {
        // Arrange — a bare carriage return is what such a line leaves; skipping it shifts every number after it

        // Act
        var failures = Failures(hasShortCounts: false, hasCrlfLineEndings: true, isEmptyContextSpaceStripped: true);

        // Assert
        failures.Should().BeEmpty();
    }

    // The ground truth for the checks above has to be right, or they prove nothing. These three check it without the parser: against what
    // GNU diff and git print for the same files, by applying the patches with code that shares nothing with the parser, and with a control
    // showing that the applier can fail.

    private static string? ApplyingFailure(int seed, bool hasShortCounts)
    {
        var random = new Random(seed);
        var oldLines = RandomLines(random, 40);
        var newLines = Edit(random, oldLines);
        var contextLines = random.Next(0, 5);
        var patch = LcsDiff.CreatePatch(oldLines, newLines, contextLines, hasShortCounts);

        try
        {
            return PatchApplier.Apply(oldLines, patch).SequenceEqual(newLines)
                ? null
                : $"seed {seed}: applying the patch to the old file does not give the new file";
        }
        catch (InvalidOperationException exception)
        {
            return $"seed {seed}: {exception.Message}";
        }
    }

    [Fact]
    public void LcsDiff_GivenAPairOfFiles_Should_ProduceTheSamePatchGnuDiffAndGitDo()
    {
        // Arrange — the expected text is what `diff -U1` and `git diff --no-index -U1` print for these two files, one line each, run on 2026-11-05
        var oldLines = new[] { "a", "b", "c", "d", "e", "f" };
        var newLines = new[] { "a", "c", "d", "x", "e", "f", "g" };

        // Act
        var patch = LcsDiff.CreatePatch(oldLines, newLines, contextLines: 1);

        // Assert
        patch.Should().Be("@@ -1,6 +1,7 @@\n a\n-b\n c\n d\n+x\n e\n f\n+g");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LcsDiff_GivenRandomPairsOfFiles_Should_ProduceAPatchThatRebuildsTheNewFileWhenAppliedToTheOldOne(bool hasShortCounts)
    {
        // Arrange — the same pairs the parser checks above run on

        // Act
        var failures = Enumerable.Range(1, CaseCount).Select(seed => ApplyingFailure(seed, hasShortCounts)).OfType<string>().Take(5).ToList();

        // Assert
        failures.Should().BeEmpty();
    }

    [Fact]
    public void PatchApplier_GivenAPatchThatDoesNotMatchTheOldFile_Should_Throw()
    {
        // Arrange — the control: an applier that accepted any patch would pass every pair above
        var oldLines = new[] { "a", "b", "c" };
        const string patch = "@@ -1,3 +1,3 @@\n a\n-x\n+y\n c";

        // Act
        var act = () => PatchApplier.Apply(oldLines, patch);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*'-x'*line 2*");
    }

    [Fact]
    public void PatchApplier_GivenAPatchThatChangesTheFile_Should_GiveTheNewLines()
    {
        // Arrange — the other half of the control: it must also be able to succeed with a result that differs from the old file
        var oldLines = new[] { "a", "b", "c" };
        const string patch = "@@ -1,3 +1,3 @@\n a\n-b\n+y\n c";

        // Act
        var rebuilt = PatchApplier.Apply(oldLines, patch);

        // Assert
        rebuilt.Should().Equal("a", "y", "c");
    }
}
