namespace GitHubReviewApp.Services;

public interface IGitHubService
{
    /// <summary>Reads the pull request's current state, including the commit its head points at.</summary>
    Task<PullRequestInfo> GetPullRequestAsync(
        string owner, string repo, int prNumber, string token, CancellationToken cancellationToken);

    /// <summary>Reads every changed file with its patch, across all pages (GitHub returns at most 3,000 files).</summary>
    Task<IReadOnlyList<PullRequestFile>> GetPullRequestFilesAsync(
        string owner, string repo, int prNumber, string token, CancellationToken cancellationToken);

    /// <summary>Lists the pull request's reviews. Pending (draft) reviews are only visible to their author.</summary>
    Task<IReadOnlyList<PullRequestReviewSummary>> ListReviewsAsync(
        string owner, string repo, int prNumber, string token, CancellationToken cancellationToken);

    /// <summary>Deletes a pending review. A review that is already gone is not an error.</summary>
    Task DeleteReviewAsync(
        string owner, string repo, int prNumber, long reviewId, string token, CancellationToken cancellationToken);

    /// <summary>Posts a non-blocking review tied to one commit, with optional inline comments.</summary>
    Task PostReviewAsync(
        string owner, string repo, int prNumber, ReviewSubmission submission, string token, CancellationToken cancellationToken);

    /// <summary>Posts a plain comment on the pull request conversation.</summary>
    Task PostCommentAsync(
        string owner, string repo, int prNumber, string body, string token, CancellationToken cancellationToken);

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
        string owner, string repo, CheckRunRequest request, string token, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the repository's review guidelines at the given commit. No guidelines file is not an error. A refusal that will not
    /// change (the app may not read the file) is reported in the result so the review can say the rules were not applied. A failure that can
    /// clear up on its own (GitHub unavailable, rate limited, a timeout) is thrown so the message is retried, since this runs before any model call.
    /// </summary>
    Task<RepositoryGuidelines> GetRepositoryGuidelinesAsync(
        string owner, string repo, string gitRef, string token, CancellationToken cancellationToken);
}
