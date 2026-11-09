namespace GitHubReviewApp.Review;

/// <summary>
/// Finds the app's record on a pull request (design 5.2): its oldest comment there that ends with a readable marker. That is the comment the app
/// replaces with each new result, so its marker says what the pull request was last given. Nobody else can post as the app's bot login, so a
/// comment by anyone else is never the record, whatever it holds.
/// </summary>
public static class ReviewRecord
{
    /// <summary>
    /// The oldest comment written by <paramref name="appLogin"/> (compared ignoring case, as GitHub compares logins) whose body ends with a
    /// readable marker, or null when there is none. A review and a "Review failed" notice both count.
    /// </summary>
    public static IssueComment? FindOwnComment(IEnumerable<IssueComment> comments, string appLogin)
    {
        return comments
            .Where(comment => IsWrittenBy(comment, appLogin) && ReviewCommentMarker.TryRead(comment.Body, out _, out _))
            .MinBy(comment => comment.Id);
    }

    /// <summary>
    /// True when the app's record on the pull request names <paramref name="headSha"/> (compared ignoring case) as reviewed. A "Review failed"
    /// notice for it does not count, so a new push or a reopen tries again.
    /// </summary>
    public static bool HasReviewed(IEnumerable<IssueComment> comments, string appLogin, string headSha)
    {
        return FindOwnComment(comments, appLogin) is { } record
            && ReviewCommentMarker.TryRead(record.Body, out var markedSha, out var outcome)
            && outcome == ReviewRecordOutcome.Reviewed
            && string.Equals(markedSha, headSha, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWrittenBy(IssueComment comment, string appLogin)
    {
        // An empty login names no one. It is what the app reads for a comment GitHub gives no author, such as one from a deleted account.
        return !string.IsNullOrWhiteSpace(appLogin) && string.Equals(comment.AuthorLogin, appLogin, StringComparison.OrdinalIgnoreCase);
    }
}
