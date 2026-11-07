namespace GitHubReviewApp.Review;

/// <summary>
/// The set of new-side line numbers visible in a patch. GitHub only accepts an inline review comment
/// on a line that appears in the diff, and rejects the whole review otherwise.
/// </summary>
public sealed class DiffLineIndex
{
    private readonly HashSet<int> _lines;

    private DiffLineIndex(HashSet<int> lines)
    {
        _lines = lines;
    }

    public int Count => _lines.Count;

    public static DiffLineIndex FromPatch(string? patch)
    {
        var lines = new HashSet<int>();

        foreach (var patchLine in PatchParser.Parse(patch))
        {
            // Only added and context lines carry a new-side number.
            if (patchLine.NewLine is { } newLine)
            {
                lines.Add(newLine);
            }
        }

        return new DiffLineIndex(lines);
    }

    public bool Contains(int line)
    {
        return _lines.Contains(line);
    }
}
