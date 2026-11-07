namespace GitHubReviewApp.Review;

/// <summary>
/// Reads a pull request's existing reviews to decide whether this app has already reviewed a commit. Anyone can post a review
/// containing any text, so a review counts only when this app wrote it, for this commit, and ended it with the marker.
/// </summary>
public static class ReviewHistory
{
    public static bool HasCompletedReview(IEnumerable<PullRequestReviewSummary> reviews, string headSha, string appLogin)
    {
        return reviews.Any(review => IsOwnReview(review, appLogin)
            && !review.IsPending
            && string.Equals(review.CommitId, headSha, StringComparison.OrdinalIgnoreCase)
            && ReviewMarker.IsAtEndOf(review.Body, headSha));
    }

    /// <summary>Unfinished drafts this app left behind. A draft by anyone else is never ours to delete.</summary>
    public static IReadOnlyList<PullRequestReviewSummary> OwnPendingReviews(IEnumerable<PullRequestReviewSummary> reviews, string appLogin)
    {
        return reviews.Where(review => review.IsPending && IsOwnReview(review, appLogin)).ToList();
    }

    private static bool IsOwnReview(PullRequestReviewSummary review, string appLogin)
    {
        return !string.IsNullOrWhiteSpace(appLogin)
            && string.Equals(review.AuthorLogin, appLogin, StringComparison.OrdinalIgnoreCase);
    }
}
