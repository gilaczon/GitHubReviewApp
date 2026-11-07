namespace GitHubReviewApp.Tests.Infrastructure;

/// <summary>
/// Which lines of a file a review comment may be placed on, worked out from the numbers in the hunk headers of a patch and nothing else. It does
/// not use the app's patch parser, so the fake GitHub that relies on it can tell when the parser and the app agree with each other and are both wrong.
/// </summary>
internal static class IndependentDiffLines
{
    private static readonly Regex _hunkHeader = new(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@", RegexOptions.CultureInvariant);

    /// <summary>The line numbers in the new version of the file that a comment on the RIGHT side can be placed on: added and unchanged lines in a hunk.</summary>
    public static IReadOnlySet<int> NewSideLines(string? patch)
    {
        var lines = new HashSet<int>();
        var nextNewLine = 0;
        var isInsideHunk = false;

        foreach (var line in (patch ?? string.Empty).Split('\n'))
        {
            var header = _hunkHeader.Match(line);

            if (header.Success)
            {
                nextNewLine = int.Parse(header.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
                isInsideHunk = true;
                continue;
            }

            if (!isInsideHunk || line.StartsWith('\\'))
            {
                continue;
            }

            // A blank unchanged line is written as a single space, so an empty string is only what is left after the last line break.
            if (line.StartsWith('+') || line.StartsWith(' '))
            {
                lines.Add(nextNewLine);
                nextNewLine++;
            }
        }

        return lines;
    }
}
