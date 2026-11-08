namespace GitHubReviewApp.Review;

/// <summary>
/// The hidden line that ends the app's comment on a pull request (design 5.8). It names the commit the comment is about, and whether the comment
/// holds a review of it or a "Review failed" notice, which is how the app knows a commit has been reviewed (design 5.2). It counts only as the
/// comment's last line, where the app writes it after its own footer: a marker quoted anywhere else, such as in a file name a finding names, is
/// not read.
/// </summary>
public static class ReviewCommentMarker
{
    private const string ReviewedText = "reviewed";
    private const string FailedText = "failed";

    private static readonly Regex _marker = new(
        @"\A<!-- moneyme-ai-review sha=(?<sha>[0-9A-Fa-f]{40}) outcome=(?<outcome>reviewed|failed) -->\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The marker for a commit, for example <c>&lt;!-- moneyme-ai-review sha=0123…4567 outcome=reviewed --&gt;</c>.</summary>
    public static string For(string headSha, ReviewRecordOutcome outcome)
    {
        return $"<!-- moneyme-ai-review sha={headSha} outcome={OutcomeText(outcome)} -->";
    }

    /// <summary>
    /// Reads the marker from the last line of a comment that is not blank. It is read only when it is the whole of that line, apart from the
    /// spaces and line breaks around it, with a commit sha of 40 hex characters and an outcome the app writes. When it returns false,
    /// <paramref name="headSha"/> is empty and <paramref name="outcome"/> holds no meaningful value.
    /// </summary>
    public static bool TryRead(string? body, out string headSha, out ReviewRecordOutcome outcome)
    {
        headSha = string.Empty;
        outcome = default;

        // Trimmed at the end, the text after its last line break is the last line that is not blank.
        var text = body?.TrimEnd() ?? string.Empty;
        var match = _marker.Match(text[(text.LastIndexOf('\n') + 1)..].Trim());

        if (!match.Success)
        {
            return false;
        }

        headSha = match.Groups["sha"].Value;
        outcome = match.Groups["outcome"].Value == ReviewedText ? ReviewRecordOutcome.Reviewed : ReviewRecordOutcome.Failed;

        return true;
    }

    private static string OutcomeText(ReviewRecordOutcome outcome)
    {
        return outcome switch
        {
            ReviewRecordOutcome.Reviewed => ReviewedText,
            ReviewRecordOutcome.Failed => FailedText,
            _ => throw new UnreachableException($"The review record outcome {outcome} has no text in the comment marker.")
        };
    }
}
