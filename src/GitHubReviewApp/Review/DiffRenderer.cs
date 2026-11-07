namespace GitHubReviewApp.Review;

/// <summary>
/// Turns one file's patch into the text the model reads. Every added or unchanged line is prefixed with its
/// line number in the new version of the file, so the model can cite lines accurately instead of counting
/// them from hunk headers.
/// </summary>
public static class DiffRenderer
{
    private const string Footer = "</file>";

    // Room kept free for the "diff truncated" notice so a truncated file still fits its limit.
    private const int NoticeReserve = 80;

    /// <summary>
    /// Renders one file. The limit is in UTF-8 bytes, not characters: a token is at least one byte, so bytes bound what the model is asked to read
    /// however unusual the text, and characters do not (an emoji or a rare character can be several tokens). Lines are kept or cut whole, so a
    /// multi-byte character is never split.
    /// </summary>
    public static PlannedFile Render(PullRequestFile file, int maxBytes)
    {
        var header = BuildHeader(file);
        var patchLines = PatchParser.Parse(file.Patch);
        var formattedLines = patchLines.Select(FormatLine).ToList();
        var lineBytes = formattedLines.Select(Encoding.UTF8.GetByteCount).ToList();

        // Room for the truncation notice is only needed if something has to be cut. A file that fits,
        // however tightly, is rendered whole and is not reported as partial.
        var headerBytes = Encoding.UTF8.GetByteCount(header);
        var fullBytes = headerBytes + lineBytes.Sum(bytes => 1 + bytes) + 1 + Footer.Length;
        var reserve = fullBytes > maxBytes ? NoticeReserve : 0;

        var builder = new StringBuilder(header);
        var usedBytes = headerBytes;
        var renderedCount = 0;
        var isPartial = false;

        for (var index = 0; index < formattedLines.Count; index++)
        {
            var projectedBytes = usedBytes + 1 + lineBytes[index] + 1 + Footer.Length + reserve;

            if (projectedBytes > maxBytes)
            {
                isPartial = true;
                break;
            }

            builder.Append('\n').Append(formattedLines[index]);
            usedBytes += 1 + lineBytes[index];
            renderedCount++;
        }

        if (isPartial)
        {
            builder
                .Append('\n')
                .Append("[diff truncated: ")
                .Append(CountChangedAndContextLines(patchLines.Skip(renderedCount)))
                .Append(" more lines not shown]");
        }

        builder.Append('\n').Append(Footer);

        return new PlannedFile(file, builder.ToString(), isPartial);
    }

    // The notice tells the model how much code it was not shown. Hunk headers and end-of-file markers are not code, and counting them
    // would overstate it.
    private static int CountChangedAndContextLines(IEnumerable<PatchLine> lines)
    {
        return lines.Count(line => line.Kind is PatchLineKind.Added or PatchLineKind.Removed or PatchLineKind.Context);
    }

    private static string BuildHeader(PullRequestFile file)
    {
        var builder = new StringBuilder("<file path=\"");
        builder.Append(EscapeAttribute(file.Path));
        builder.Append("\" status=\"").Append(EscapeAttribute(file.Status));
        builder.Append("\" additions=\"").Append(file.Additions.ToString(CultureInfo.InvariantCulture));
        builder.Append("\" deletions=\"").Append(file.Deletions.ToString(CultureInfo.InvariantCulture));
        builder.Append('"');

        if (!string.IsNullOrEmpty(file.PreviousPath))
        {
            builder.Append(" previous_path=\"").Append(EscapeAttribute(file.PreviousPath)).Append('"');
        }

        builder.Append('>');
        return builder.ToString();
    }

    private static string FormatLine(PatchLine line)
    {
        var text = PromptSanitizer.NeutralizeTags(line.Text);

        return line.Kind switch
        {
            PatchLineKind.HunkHeader => text,
            PatchLineKind.Context => $"{line.NewLine,6} | {text}",
            PatchLineKind.Added => $"{line.NewLine,6} |+{text}",
            PatchLineKind.Removed => $"{string.Empty,6} |-{text}",
            _ => "\\" + text
        };
    }

    private static string EscapeAttribute(string value)
    {
        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
    }
}
