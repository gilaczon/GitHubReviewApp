namespace GitHubReviewApp.Review;

/// <summary>
/// The review prompt wraps untrusted pull request content in XML-style tags. This stops that content
/// from closing or forging those tags (for example a diff line that says <c>&lt;/file&gt;</c> followed by
/// fake instructions) by inserting a backslash after the opening angle bracket.
/// </summary>
public static class PromptSanitizer
{
    // The checklist prompt's tags: pr_metadata, pr_description_untrusted, changed_files, diff, and file inside diff.
    private static readonly Regex _wrapperTag = new(
        @"<(/?)(pr_metadata|pr_description_untrusted|changed_files|diff|file)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string NeutralizeTags(string text)
    {
        return _wrapperTag.Replace(text, @"<\$1$2");
    }
}
