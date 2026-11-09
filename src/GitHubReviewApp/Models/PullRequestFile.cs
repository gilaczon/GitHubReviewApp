namespace GitHubReviewApp.Models;

/// <summary>One changed file of a pull request. <see cref="Patch"/> is null for binary or oversized diffs.</summary>
public sealed record PullRequestFile(
    string Path,
    string Status,
    int Additions,
    int Deletions,
    string? Patch,
    string? PreviousPath = null)
{
    public bool IsRemoved => string.Equals(Status, "removed", StringComparison.OrdinalIgnoreCase);
}
