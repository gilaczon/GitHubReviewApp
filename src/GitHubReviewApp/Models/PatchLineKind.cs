namespace GitHubReviewApp.Models;

/// <summary>The kind of a line inside a unified-diff patch.</summary>
public enum PatchLineKind
{
    HunkHeader = 0,
    Context = 1,
    Added = 2,
    Removed = 3,
    NoNewlineMarker = 4
}
