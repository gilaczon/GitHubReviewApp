namespace GitHubReviewApp.Tests.Infrastructure;

/// <summary>
/// Builds markdown-shaped strings from the tokens an attacker would use to confuse a sanitizer: backticks, escapes, fences,
/// indentation, containers, tables, HTML, entities, links and mentions. The seed is fixed by each caller, so a failure always reproduces.
/// </summary>
internal static class MarkdownFragmentGenerator
{
    private const int MaxTokensPerFragment = 22;

    private static readonly string[] _tokens =
    [
        "`", "``", "```", "````", "~~~", "~~", "\\", "\\`", "\\@", "\\\\", "@octocat", "@a", "x@a", "@",
        " ", "  ", "    ", "\t", "\n", "\n", "\n\n", "\r", "\r\n", "> ", "- ", "1. ", "# ", "| ", "|", "a|b", "-|-", "---",
        "[", "]", "![", "](", "(", ")", "<", ">", "<b>", "<!--", "-->", "<details>", "</details>", "&", "&#64;", "&amp;", "&commat;",
        "*", "**", "_", "$", "=", ":", "word", "ok", "http://x.y/@z", "www.x.y", "java", "x`y",
        "https://evil.example/login", "://", "#12", "owner/repo#3", "GH-5", "\\#7", "\\:",
        "GH\\-6", "gh\\-7", "u@e.com", "xmpp:u@e.com", "|-|-|", "-|-",
        "$`", "`$", "u@_e.com", "u@-e.com", "u@.e.com", "octocat/Hello-World/pull/1", "/issues/12", ":--", ":-:", "-:", "h"
    ];

    public static IReadOnlyList<string> Generate(int seed, int count)
    {
        var random = new Random(seed);
        var fragments = new List<string>(count);

        for (var fragmentIndex = 0; fragmentIndex < count; fragmentIndex++)
        {
            var tokenCount = random.Next(1, MaxTokensPerFragment);
            var builder = new StringBuilder();

            for (var tokenIndex = 0; tokenIndex < tokenCount; tokenIndex++)
            {
                builder.Append(_tokens[random.Next(_tokens.Length)]);
            }

            fragments.Add(builder.ToString());
        }

        return fragments;
    }
}
