namespace GitHubReviewApp.Models;

/// <summary>
/// An existing review on a pull request, used to avoid duplicate reviews and to clear stale drafts. <paramref name="AuthorLogin"/>
/// is the account that wrote it, which for a GitHub App is "{slug}[bot]".
/// </summary>
public sealed record PullRequestReviewSummary(
    long Id,
    string State,
    string Body,
    string CommitId,
    string AuthorLogin)
{
    public bool IsPending => string.Equals(State, "PENDING", StringComparison.OrdinalIgnoreCase);
}
