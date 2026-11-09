namespace GitHubReviewApp.Services;

public interface IReviewOrchestrator
{
    /// <summary>
    /// Reviews one queued pull request end to end and posts the result. Returns a skipped result when no
    /// review is needed. Failures of GitHub or Claude are thrown so the caller can decide whether to retry.
    /// </summary>
    Task<ReviewRunResult> ReviewPullRequestAsync(ReviewQueueMessage message, CancellationToken cancellationToken);
}
