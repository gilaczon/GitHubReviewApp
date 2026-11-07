namespace GitHubReviewApp.Review;

/// <summary>
/// An invisible marker added to every posted review so a redelivered webhook or a retried message can
/// recognise that this exact commit has already been reviewed.
/// </summary>
public static class ReviewMarker
{
    public static string For(string headSha)
    {
        return $"<!-- ai-review:sha={headSha} -->";
    }

    /// <summary>
    /// True when the marker is the last thing in the body, which is where the formatter always puts it. A marker quoted anywhere
    /// else, for example inside a file name that the review lists, does not count.
    /// </summary>
    public static bool IsAtEndOf(string? body, string headSha)
    {
        return !string.IsNullOrEmpty(body) && body.TrimEnd().EndsWith(For(headSha), StringComparison.Ordinal);
    }
}
