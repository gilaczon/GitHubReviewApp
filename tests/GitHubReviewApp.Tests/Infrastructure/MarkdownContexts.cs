namespace GitHubReviewApp.Tests.Infrastructure;

/// <summary>
/// The places the formatter puts text the model wrote, each shown as a complete document, written out here independently of the formatter so
/// that a test can judge sanitized text in every setting a reader could meet it in. A renderer that reads the text inside a list item or a
/// quote does not read it as it does at the top level, which is where a sanitizer that tracks code by line alone goes wrong.
/// </summary>
internal static class MarkdownContexts
{
    public static IReadOnlyList<(string Name, string Markdown)> Around(string sanitized)
    {
        return
        [
            ("an inline comment", sanitized),
            ("a finding in the review body", "- **[High | bug]** `a.cs`:1: title\n\n  " + sanitized.Replace("\n", "\n  ", StringComparison.Ordinal)),
            ("a summary in a block quote", string.Join('\n', sanitized.Split('\n').Select(line => line.Length == 0 ? ">" : "> " + line)))
        ];
    }

    /// <summary>The settings in which the sanitized text renders something live, each with the markdown that was judged. Empty when it is safe everywhere.</summary>
    public static IReadOnlyList<string> LiveSettings(string sanitized)
    {
        return Around(sanitized)
            .Where(context => MarkdownOracle.HasLiveContent(context.Markdown))
            .Select(context => context.Name)
            .ToList();
    }
}
