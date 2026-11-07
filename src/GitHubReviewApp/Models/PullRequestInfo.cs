namespace GitHubReviewApp.Models;

/// <summary>The current state of a pull request as reported by the GitHub API.</summary>
public sealed record PullRequestInfo(
    string State,
    bool IsDraft,
    string Title,
    string HeadSha,
    string BaseSha,
    int ChangedFileCount,
    int Additions,
    int Deletions)
{
    public int ChangedLineCount => Additions + Deletions;

    public bool IsOpen => string.Equals(State, "open", StringComparison.OrdinalIgnoreCase);
}
