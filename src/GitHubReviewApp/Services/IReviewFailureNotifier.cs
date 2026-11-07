namespace GitHubReviewApp.Services;

public interface IReviewFailureNotifier
{
    /// <summary>
    /// Tells the pull request author that the automated review could not be completed. Does nothing when the
    /// pull request is closed, is a draft, or has a newer commit (the notice would be stale).
    /// </summary>
    Task NotifyFailureAsync(ReviewQueueMessage message, string reason, CancellationToken cancellationToken);
}
