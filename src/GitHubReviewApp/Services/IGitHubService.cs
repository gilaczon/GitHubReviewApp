namespace GitHubReviewApp.Services;

public interface IGitHubService
{
    /// <summary>Reads the pull request's current state, including the commit its head points at.</summary>
    Task<PullRequestInfo> GetPullRequestAsync(
        string owner, string repo, int prNumber, string token, CancellationToken cancellationToken);

    /// <summary>Reads every changed file with its patch, across all pages (GitHub returns at most 3,000 files).</summary>
    Task<IReadOnlyList<PullRequestFile>> GetPullRequestFilesAsync(
        string owner, string repo, int prNumber, string token, CancellationToken cancellationToken);

    /// <summary>
    /// Lists the comments on the pull request's conversation, oldest first, reading every page of 100 up to 30 pages. A comment GitHub gives no
    /// author keeps an empty login.
    /// </summary>
    Task<IReadOnlyList<IssueComment>> ListIssueCommentsAsync(
        string owner, string repo, int prNumber, string token, CancellationToken cancellationToken);

    /// <summary>Posts a comment on the pull request conversation and returns its id.</summary>
    Task<long> CreateIssueCommentAsync(
        string owner, string repo, int prNumber, string body, string token, CancellationToken cancellationToken);

    /// <summary>Replaces the body of a comment on the pull request conversation. A comment that is gone is an error.</summary>
    Task UpdateIssueCommentAsync(
        string owner, string repo, long commentId, string body, string token, CancellationToken cancellationToken);

    /// <summary>Creates a completed check run on the request's commit and returns its id.</summary>
    Task<long> CreateCheckRunAsync(
        string owner, string repo, CheckRunRequest checkRun, string token, CancellationToken cancellationToken);
}
