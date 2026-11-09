namespace GitHubReviewApp.Tests.Infrastructure;

/// <summary>
/// Applies a unified diff to the lines of the old file, the way <c>patch</c> would, from the numbers in its hunk headers and the markers at the start
/// of its lines and nothing else. It does not use the app's patch parser, so a patch that rebuilds the new file here is right whatever the parser makes of it.
/// </summary>
internal static class PatchApplier
{
    private static readonly Regex _hunkHeader = new(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@", RegexOptions.CultureInvariant);

    /// <exception cref="InvalidOperationException">A removed or unchanged line of the patch is not the line the old file has at that place.</exception>
    public static IReadOnlyList<string> Apply(IReadOnlyList<string> oldLines, string patch)
    {
        var result = new List<string>();
        var nextOldIndex = 0;

        foreach (var line in patch.Length == 0 ? [] : patch.Split('\n'))
        {
            var header = _hunkHeader.Match(line);

            if (header.Success)
            {
                var oldStart = int.Parse(header.Groups[1].Value, CultureInfo.InvariantCulture);
                var oldCount = header.Groups[2].Success ? int.Parse(header.Groups[2].Value, CultureInfo.InvariantCulture) : 1;

                // An empty old side is numbered by the line before the hunk, and every other one by its first line.
                var hunkFirstOldIndex = oldCount == 0 ? oldStart : oldStart - 1;
                result.AddRange(oldLines.Skip(nextOldIndex).Take(hunkFirstOldIndex - nextOldIndex));
                nextOldIndex = hunkFirstOldIndex;
                continue;
            }

            if (line.StartsWith('+'))
            {
                result.Add(line[1..]);
                continue;
            }

            if (!line.StartsWith('-') && !line.StartsWith(' '))
            {
                continue;
            }

            var text = line[1..];

            if (nextOldIndex >= oldLines.Count || oldLines[nextOldIndex] != text)
            {
                throw new InvalidOperationException($"The patch line '{line}' is not line {nextOldIndex + 1} of the old file.");
            }

            nextOldIndex++;

            if (line.StartsWith(' '))
            {
                result.Add(text);
            }
        }

        result.AddRange(oldLines.Skip(nextOldIndex));

        return result;
    }
}
