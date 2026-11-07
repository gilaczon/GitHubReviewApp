namespace GitHubReviewApp.Models;

/// <summary>
/// One problem reported by the model. <see cref="Line"/> is a line number in the new version of the file,
/// or 0 when the finding concerns the whole file.
/// </summary>
public sealed record ReviewFinding(
    string Path,
    int Line,
    ReviewSeverity Severity,
    string Category,
    string Title,
    string Body)
{
    /// <summary>True when <see cref="Line"/> is a line GitHub will accept an inline comment on.</summary>
    public bool IsAnchored { get; init; }
}
