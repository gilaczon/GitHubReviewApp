namespace GitHubReviewApp.Review;

/// <summary>
/// Parses the per-file <c>patch</c> text returned by the GitHub pull request files API
/// into numbered lines. Tolerant by design: unrecognised lines are skipped, never thrown on.
/// </summary>
public static class PatchParser
{
    private static readonly Regex _hunkHeader = new(
        @"^@@ -(?<oldStart>\d+)(?:,\d+)? \+(?<newStart>\d+)(?:,\d+)? @@",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<PatchLine> Parse(string? patch)
    {
        if (string.IsNullOrWhiteSpace(patch))
        {
            return [];
        }

        var rawLines = patch.Split('\n');
        var lineCount = rawLines.Length;

        // A patch that ends with a newline splits into a trailing empty element that is not a real line.
        if (rawLines[lineCount - 1].Length == 0)
        {
            lineCount--;
        }

        var parsed = new List<PatchLine>(lineCount);
        var oldLine = 0;
        var newLine = 0;
        var isInsideHunk = false;

        for (var index = 0; index < lineCount; index++)
        {
            var raw = rawLines[index];

            var header = _hunkHeader.Match(raw);
            if (header.Success)
            {
                isInsideHunk = int.TryParse(header.Groups["oldStart"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out oldLine)
                    && int.TryParse(header.Groups["newStart"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out newLine);

                if (isInsideHunk)
                {
                    parsed.Add(new PatchLine(PatchLineKind.HunkHeader, null, null, raw.TrimEnd('\r')));
                }

                continue;
            }

            // Content lines always start with a marker character, so a line starting with "@@"
            // that did not parse is a damaged header. Stop attributing lines to the previous hunk.
            if (raw.StartsWith("@@", StringComparison.Ordinal))
            {
                isInsideHunk = false;
                continue;
            }

            if (!isInsideHunk)
            {
                continue;
            }

            // The CR of a CRLF line ending is not part of the line. Taken off first, so that the line an empty context line leaves behind
            // once a tool has stripped its marker space (a bare CR here) is empty like the one with no CR, and not skipped, which would shift
            // every later line number in the hunk by one.
            var content = raw.EndsWith('\r') ? raw[..^1] : raw;

            // Some tools strip the single trailing space of an empty context line.
            var marker = content.Length == 0 ? ' ' : content[0];
            var text = content.Length == 0 ? string.Empty : content[1..];

            switch (marker)
            {
                case '+':
                    parsed.Add(new PatchLine(PatchLineKind.Added, null, newLine++, text));
                    break;
                case '-':
                    parsed.Add(new PatchLine(PatchLineKind.Removed, oldLine++, null, text));
                    break;
                case ' ':
                    parsed.Add(new PatchLine(PatchLineKind.Context, oldLine++, newLine++, text));
                    break;
                case '\\':
                    parsed.Add(new PatchLine(PatchLineKind.NoNewlineMarker, null, null, text));
                    break;
            }
        }

        return parsed;
    }
}
