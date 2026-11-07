namespace GitHubReviewApp.Tests.Infrastructure;

/// <summary>
/// Builds markdown documents line by line from the pieces a container-confusing attacker uses: list and quote markers, tab and space
/// indentation, fences of both kinds, and payload lines that would notify, link or embed if they were ever read as text. A random token soup
/// (<see cref="MarkdownFragmentGenerator"/>) almost never forms the three or four aligned lines these attacks need, so this one builds
/// them on purpose. The seed is fixed by each caller, so a failure always reproduces.
/// </summary>
internal static class StructuredMarkdownGenerator
{
    private const int MaxLinesPerDocument = 7;

    private static readonly string[] _containerPrefixes =
    [
        string.Empty, string.Empty, string.Empty, string.Empty, "> ", ">", "- ", "* ", "+ ", "1. ", "1) ", "  - ", "    - ", "\t- ", "> > ", "> - ", "- > ",
        "- [ ] ", "[^1]: ", "  ", "   ", "    ", "     ", "\t", "> \t", "  > ", "10. ", "-\t"
    ];

    private static readonly string[] _fences =
    [
        "```", "```", "````", "~~~", "~~~", "~~~~", "``` java", "```suggestion", "~~~ x", "``` x`y", "  ```", "   ~~~", "    ```", "\t```", "```mermaid", "``` ```"
    ];

    private static readonly string[] _payloads =
    [
        "@victim", "ping @org/team now", "<img src=\"https://attacker.example/x.png\">", "<b>bold</b>", "[l](https://evil.example)",
        "![i](https://evil.example/x.png)", "https://evil.example/login", "<details>", "</details>", "&#64;victim", "<script>x</script>",
        "see www.evil.example", "fixes other-org/other-repo#31", "GH-9"
    ];

    private static readonly string[] _filler = ["text", "word", string.Empty, string.Empty, "a | b", "---", "# heading", "| a | b |", "|---|---|", "=== ", "* * *"];

    public static IReadOnlyList<string> Generate(int seed, int count)
    {
        var random = new Random(seed);
        var documents = new List<string>(count);

        for (var index = 0; index < count; index++)
        {
            var lineCount = random.Next(1, MaxLinesPerDocument + 1);
            var lines = Enumerable.Range(0, lineCount).Select(_ => Line(random)).ToList();

            documents.Add(string.Join(random.Next(8) == 0 ? "\r\n" : "\n", lines));
        }

        return documents;
    }

    private static string Line(Random random)
    {
        var prefix = _containerPrefixes[random.Next(_containerPrefixes.Length)];
        var roll = random.Next(100);

        var body = roll switch
        {
            < 40 => _fences[random.Next(_fences.Length)],
            < 75 => _payloads[random.Next(_payloads.Length)],
            _ => _filler[random.Next(_filler.Length)]
        };

        return prefix + body;
    }
}
