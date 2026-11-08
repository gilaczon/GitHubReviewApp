namespace GitHubReviewApp.Models;

/// <summary>
/// The current state of a pull request as reported by the GitHub API. The description (<paramref name="Body"/>), the author's login and the
/// branch names are written into the checklist review's prompt; they default to empty for callers that do not read them.
/// </summary>
public sealed record PullRequestInfo(
    string State,
    bool IsDraft,
    string Title,
    string HeadSha,
    string BaseSha,
    int ChangedFileCount,
    int Additions,
    int Deletions,
    string Body = "",
    string AuthorLogin = "",
    string BaseRef = "",
    string HeadRef = "")
{
    public int ChangedLineCount => Additions + Deletions;

    public bool IsOpen => string.Equals(State, "open", StringComparison.OrdinalIgnoreCase);
}
