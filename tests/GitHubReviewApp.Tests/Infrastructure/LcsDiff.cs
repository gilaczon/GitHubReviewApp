namespace GitHubReviewApp.Tests.Infrastructure;

/// <summary>
/// An independent unified-diff generator for tests: a longest-common-subsequence diff written from scratch, sharing nothing with the code under
/// test. Its patches are right by construction (applying them to the old lines gives the new lines), so they can serve as ground truth for the
/// patch parser, which must recover from them the same line numbers the files themselves have.
/// </summary>
internal static class LcsDiff
{
    private enum Operation
    {
        Equal,
        Delete,
        Insert
    }

    private readonly record struct Step(Operation Operation, int OldIndex, int NewIndex);

    /// <summary>The unified diff of two files, as GitHub's files API would return it for one file: hunks only, no file headers.</summary>
    /// <param name="hasShortCounts">Leave out a count of one, as git does ("@@ -3 +3 @@").</param>
    public static string CreatePatch(IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines, int contextLines = 3, bool hasShortCounts = false)
    {
        var steps = Diff(oldLines, newLines);
        var changedPositions = Enumerable.Range(0, steps.Count).Where(index => steps[index].Operation != Operation.Equal).ToList();
        var lines = new List<string>();

        foreach (var (from, to) in Group(changedPositions, steps.Count, contextLines))
        {
            var slice = steps.Skip(from).Take(to - from + 1).ToList();
            var oldCount = slice.Count(step => step.Operation != Operation.Insert);
            var newCount = slice.Count(step => step.Operation != Operation.Delete);

            // git numbers an empty side by the line before the hunk, and every other side by its first line
            var oldStart = oldCount == 0 ? slice[0].OldIndex : slice.First(step => step.Operation != Operation.Insert).OldIndex + 1;
            var newStart = newCount == 0 ? slice[0].NewIndex : slice.First(step => step.Operation != Operation.Delete).NewIndex + 1;

            lines.Add($"@@ -{oldStart}{Count(oldCount, hasShortCounts)} +{newStart}{Count(newCount, hasShortCounts)} @@");
            lines.AddRange(slice.Select(step => step.Operation switch
            {
                Operation.Equal => " " + oldLines[step.OldIndex],
                Operation.Delete => "-" + oldLines[step.OldIndex],
                _ => "+" + newLines[step.NewIndex]
            }));
        }

        return string.Join("\n", lines);
    }

    private static string Count(int count, bool hasShortCounts)
    {
        return hasShortCounts && count == 1 ? string.Empty : $",{count}";
    }

    // Runs of changed positions, each widened by the context on both sides; runs that touch or overlap once widened become one hunk.
    private static IEnumerable<(int From, int To)> Group(List<int> changedPositions, int stepCount, int contextLines)
    {
        var hunks = new List<(int From, int To)>();

        foreach (var position in changedPositions)
        {
            var from = Math.Max(0, position - contextLines);
            var to = Math.Min(stepCount - 1, position + contextLines);

            if (hunks.Count > 0 && from <= hunks[^1].To + 1)
            {
                hunks[^1] = (hunks[^1].From, to);
            }
            else
            {
                hunks.Add((from, to));
            }
        }

        return hunks;
    }

    private static List<Step> Diff(IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines)
    {
        var lengths = new int[oldLines.Count + 1, newLines.Count + 1];

        for (var i = oldLines.Count - 1; i >= 0; i--)
        {
            for (var j = newLines.Count - 1; j >= 0; j--)
            {
                lengths[i, j] = oldLines[i] == newLines[j]
                    ? lengths[i + 1, j + 1] + 1
                    : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            }
        }

        var steps = new List<Step>();
        int row = 0;
        int column = 0;

        while (row < oldLines.Count || column < newLines.Count)
        {
            if (row < oldLines.Count && column < newLines.Count && oldLines[row] == newLines[column])
            {
                steps.Add(new Step(Operation.Equal, row++, column++));
            }
            else if (column < newLines.Count && (row == oldLines.Count || lengths[row, column + 1] >= lengths[row + 1, column]))
            {
                steps.Add(new Step(Operation.Insert, row, column++));
            }
            else
            {
                steps.Add(new Step(Operation.Delete, row++, column));
            }
        }

        return steps;
    }
}
