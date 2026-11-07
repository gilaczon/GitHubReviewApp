namespace GitHubReviewApp.Review;

/// <summary>
/// The review prompt wraps untrusted pull request content in XML-style tags. This stops that content
/// from closing or forging those tags (for example a diff line that says <c>&lt;/file&gt;</c> followed by
/// fake instructions) by inserting a backslash after the opening angle bracket.
/// </summary>
public static class PromptSanitizer
{
    private static readonly Regex _wrapperTag = new(
        @"<(/?)(files|file|pull_request|title|stats|repository_guidelines)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string NeutralizeTags(string text)
    {
        return _wrapperTag.Replace(text, @"<\$1$2");
    }
}
