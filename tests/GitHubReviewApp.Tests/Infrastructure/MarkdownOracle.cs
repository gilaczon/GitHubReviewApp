namespace GitHubReviewApp.Tests.Infrastructure;

/// <summary>
/// An independent CommonMark/GFM renderer (Markdig) that shows what a reader of posted markdown would see.
/// The code under test never calls it, so a mistake in the sanitizer's own idea of "inside code" cannot hide.
/// </summary>
internal static class MarkdownOracle
{
    private const string Sentinel = "ZZSENTINELZZ";
    private const int MaxViolationsReported = 15;

    // No autolink extension on purpose. Markdig autolinks inside the inline parser, so "http://x.y```code```" lets the URL swallow the
    // backticks. cmark-gfm, which GitHub uses, autolinks afterwards over text nodes that are left, so code spans win there.
    private static readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseTaskLists()
        .UseEmphasisExtras()
        .UseFootnotes()
        .Build();

    private static readonly Regex _codeOrPre = new(
        @"<pre\b.*?</pre>|<code\b.*?</code>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex _tag = new(@"<[^>]*>", RegexOptions.CultureInvariant);

    // GitHub links a mention found in a single text node: an @ followed by a name start, not preceded by a word character.
    private static readonly Regex _liveMention = new(@"(?<![A-Za-z0-9_])@[A-Za-z0-9]", RegexOptions.CultureInvariant);

    // GitHub also links a web address, an issue reference and an e-mail address that it finds in plain text, which is how text the app did not write
    // can send a reader somewhere or write on another issue. This markdown parser has no autolink extension, so the text itself is what is looked at,
    // after the renderer has read its escapes: "GH\-1" is shown as "GH-1" and linked.
    private static readonly Regex _autoLinkedText = new(
        @"://|(?<![A-Za-z0-9])www\.|#[0-9]|(?<![A-Za-z0-9])gh-[0-9]|/(?:issues|pull|discussions)/[0-9]|[A-Za-z0-9._+-]+@\.?[A-Za-z0-9_-]+\.[A-Za-z0-9]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex _activeElement = new(
        @"<(img|script|iframe|div|span|style|form|object|embed|svg|video|audio|link|meta|base|button|input|textarea|select|font|center|marquee)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex _forbiddenElement = new(
        @"<(img|details|summary|script|iframe|div|span|style|form|object|embed|svg|video|audio|link|meta|base|button|input|textarea|select|font|center|marquee)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string ToHtml(string markdown)
    {
        return Markdown.ToHtml(markdown, _pipeline);
    }

    /// <summary>The text nodes outside code, entities decoded. Each node is separate, as in GitHub's mention filter.</summary>
    public static IReadOnlyList<string> TextNodesOutsideCode(string markdown)
    {
        var withoutCode = _codeOrPre.Replace(ToHtml(markdown), "<x>");

        return _tag.Split(withoutCode)
            .Select(node => WebUtility.HtmlDecode(node) ?? string.Empty)
            .ToList();
    }

    public static bool HasLiveMention(string markdown)
    {
        return TextNodesOutsideCode(markdown).Any(node => _liveMention.IsMatch(node));
    }

    public static bool HasForbiddenElement(string markdown)
    {
        return _forbiddenElement.IsMatch(ToHtml(markdown));
    }

    /// <summary>An element that could carry content from outside: an image, a script, a frame. The formatter's own details and summary are not one.</summary>
    public static bool HasActiveElement(string markdown)
    {
        return _activeElement.IsMatch(ToHtml(markdown));
    }

    /// <summary>Anything a reader could be notified by, sent by, or shown from text the app did not write.</summary>
    public static bool HasLiveContent(string markdown)
    {
        return HasLiveMention(markdown) || HasActiveElement(markdown) || HasLink(markdown) || HasAutoLinkedText(markdown) || HasMathNextToCode(markdown);
    }

    /// <summary>
    /// A dollar sign right next to a code span. GitHub reads "$`x`$" as math and links the mentions and references inside it, including those of the
    /// spans between the two signs. This parser has no math, so the adjacency itself is what is looked for, in the HTML it renders.
    /// </summary>
    public static bool HasMathNextToCode(string markdown)
    {
        var html = ToHtml(markdown);

        return html.Contains("$<code", StringComparison.Ordinal) || html.Contains("</code>$", StringComparison.Ordinal);
    }

    /// <summary>Plain text that GitHub turns into a link: a web address, a "www." address, an issue reference or an e-mail address.</summary>
    public static bool HasAutoLinkedText(string markdown)
    {
        return TextNodesOutsideCode(markdown).Any(node => _autoLinkedText.IsMatch(node));
    }

    /// <summary>A hyperlink: text the app did not write that sends a reader somewhere. The app's own wording contains none.</summary>
    public static bool HasLink(string markdown)
    {
        return ToHtml(markdown).Contains("<a href", StringComparison.OrdinalIgnoreCase);
    }

    public static int CountElements(string markdown, string name)
    {
        return Regex.Matches(ToHtml(markdown), $"<{Regex.Escape(name)}\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Count;
    }

    /// <summary>The text a reader sees outside code, with every text node joined by a space.</summary>
    public static string VisibleText(string markdown)
    {
        return string.Join(' ', TextNodesOutsideCode(markdown));
    }

    /// <summary>
    /// Checks documents that were composed from hostile text together with the app's own wording: none may notify anyone or render active content,
    /// and the app's own statements (the coverage, the footer) must stay visible rather than end up inside the model's code block or quote.
    /// </summary>
    public static IReadOnlyList<string> FindComposedViolations(
        IEnumerable<string> fragments,
        Func<string, IReadOnlyList<(string Markdown, bool IsReviewBody)>> compose,
        IReadOnlyList<string> textThatMustStayVisible)
    {
        var violations = new List<string>();

        foreach (var fragment in fragments)
        {
            foreach (var (markdown, isReviewBody) in compose(fragment))
            {
                var input = JsonSerializer.Serialize(fragment);

                if (HasLiveMention(markdown))
                {
                    violations.Add($"live mention for {input}");
                }

                if (HasActiveElement(markdown))
                {
                    violations.Add($"active element for {input}");
                }

                if (HasLink(markdown))
                {
                    violations.Add($"link for {input}");
                }

                if (HasAutoLinkedText(markdown))
                {
                    violations.Add($"text GitHub would link for {input}");
                }

                if (HasMathNextToCode(markdown))
                {
                    violations.Add($"a dollar sign touches a code span for {input}");
                }

                if (isReviewBody && CountElements(markdown, "details") > 1)
                {
                    violations.Add($"extra details element for {input}");
                }

                if (!isReviewBody && CountElements(markdown, "details") > 0)
                {
                    violations.Add($"details element in an inline comment for {input}");
                }

                var visible = VisibleText(markdown);

                if (isReviewBody && textThatMustStayVisible.Any(text => !visible.Contains(text, StringComparison.Ordinal)))
                {
                    violations.Add($"app text hidden for {input}");
                }
            }

            if (violations.Count >= MaxViolationsReported)
            {
                break;
            }
        }

        return violations;
    }

    /// <summary>True when the markdown leaves a following paragraph inside code or another container.</summary>
    public static bool SwallowsFollowingText(string markdown)
    {
        var html = ToHtml(markdown + "\n\n" + Sentinel).TrimEnd();

        return !html.EndsWith($"<p>{Sentinel}</p>", StringComparison.Ordinal);
    }

    /// <summary>Runs every invariant over each fragment and reports the breaches, quoting the offending input.</summary>
    public static IReadOnlyList<string> FindViolations(IEnumerable<string> fragments, Func<string, string> sanitize)
    {
        var violations = new List<string>();

        foreach (var fragment in fragments)
        {
            var sanitized = sanitize(fragment);
            var report = $"{JsonSerializer.Serialize(fragment)} => {JsonSerializer.Serialize(sanitized)}";

            if (HasLiveMention(sanitized))
            {
                var node = TextNodesOutsideCode(sanitized).First(text => _liveMention.IsMatch(text));
                violations.Add($"live mention in node {JsonSerializer.Serialize(node)}: {report}");
            }

            if (HasForbiddenElement(sanitized))
            {
                violations.Add($"forbidden element: {report}");
            }

            if (HasLink(sanitized))
            {
                violations.Add($"link: {report}");
            }

            if (HasAutoLinkedText(sanitized))
            {
                violations.Add($"text GitHub would link: {report}");
            }

            if (HasMathNextToCode(sanitized))
            {
                violations.Add($"a dollar sign touches a code span: {report}");
            }

            if (SwallowsFollowingText(sanitized))
            {
                violations.Add($"swallows following text: {report}");
            }

            if (violations.Count >= MaxViolationsReported)
            {
                break;
            }
        }

        return violations;
    }
}
