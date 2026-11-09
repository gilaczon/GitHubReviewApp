namespace GitHubReviewApp.ReviewPreview;

/// <summary>
/// Turns the review comment, written for GitHub, into a Slack message. Slack's mrkdwn is not GitHub markdown: the headline and the two section
/// headings are made bold, list items get a bullet (Slack shows a leading "- " as typed), the hidden record marker is dropped, GitHub's backslash escapes and HTML entities outside code are undone, and
/// <c>&amp;</c>, <c>&lt;</c> and <c>&gt;</c> are escaped as Slack requires, which also stops text from the pull request forming a Slack link or
/// mention. Inline code stays inline code.
/// </summary>
public static class SlackMessageFormatter
{
    /// <summary>Slack cuts a message longer than 40,000 characters; the preview cuts it first, with a note, a little below that.</summary>
    public const int MaxTextLength = 38000;

    private const string RecordMarkerStart = "<!-- moneyme-ai-review";
    private const string ChecklistHeadingStart = "Checklist (";
    private const string FindingsHeading = "To fix, worst first";
    private const string ListItemStart = "- ";
    private const string Bullet = "• ";
    private const string CutNote = "\n… (cut here: the review is longer than one Slack message can hold)";

    // The ASCII punctuation CommonMark lets a backslash escape.
    private const string EscapablePunctuation = "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";

    /// <summary>The message for a finished review: a header naming the pull request, then the comment as Slack shows it.</summary>
    public static string FormatReview(PullRequestLink link, string title, string commentBody)
    {
        var builder = new StringBuilder(Header(link, title));
        var lines = commentBody.ReplaceLineEndings("\n").Split('\n')
            .Where(line => !line.StartsWith(RecordMarkerStart, StringComparison.Ordinal))
            .ToList();

        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        for (var index = 0; index < lines.Count; index++)
        {
            var text = ToSlack(lines[index]);

            if (text.StartsWith(ListItemStart, StringComparison.Ordinal))
            {
                text = Bullet + text[ListItemStart.Length..];
            }

            var isHeading = index == 0 || lines[index] == FindingsHeading || lines[index].StartsWith(ChecklistHeadingStart, StringComparison.Ordinal);

            builder.Append('\n').Append(isHeading && text.Length > 0 ? $"*{text}*" : text);
        }

        return Cut(builder.ToString());
    }

    /// <summary>The message for a pull request the review did not look at, saying why.</summary>
    public static string FormatSkip(PullRequestLink link, string title, ReviewSkipReason reason)
    {
        return Header(link, title) + "\nNothing was reviewed: " + SkipText(reason);
    }

    /// <summary>
    /// One line of the comment in Slack's mrkdwn: outside inline code, backslash escapes and HTML entities are undone (a backtick an entity
    /// stood for becomes an apostrophe, so it cannot open a code span), and in all of it &amp;, &lt; and &gt; are escaped.
    /// </summary>
    internal static string ToSlack(string line)
    {
        var builder = new StringBuilder(line.Length);
        var segments = line.Split('`');

        for (var index = 0; index < segments.Length; index++)
        {
            // Split on backticks: odd segments sit between a pair. A last odd segment has no closing backtick, so it is not code.
            var isCode = index % 2 == 1 && index < segments.Length - 1;

            if (index > 0)
            {
                builder.Append('`');
            }

            builder.Append(isCode ? EscapeForSlack(segments[index]) : EscapeForSlack(WebUtility.HtmlDecode(Unescape(segments[index])).Replace('`', '\'')));
        }

        return builder.ToString();
    }

    internal static string EscapeForSlack(string text)
    {
        return text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
    }

    private static string Header(PullRequestLink link, string title)
    {
        var flatTitle = title.ReplaceLineEndings(" ").Trim();

        return $":mag: *Review preview* of <{link.Url}|{EscapeForSlack(link.Name)}>: {EscapeForSlack(flatTitle)}\n"
            + "_Posted here only: nothing was written to the pull request._\n";
    }

    private static string Unescape(string text)
    {
        var builder = new StringBuilder(text.Length);

        for (var index = 0; index < text.Length; index++)
        {
            var isEscape = text[index] == '\\' && index + 1 < text.Length && EscapablePunctuation.Contains(text[index + 1], StringComparison.Ordinal);

            if (isEscape)
            {
                index++;
            }

            builder.Append(text[index]);
        }

        return builder.ToString();
    }

    private static string SkipText(ReviewSkipReason reason)
    {
        return reason switch
        {
            ReviewSkipReason.NothingToReview => "the pull request has no changed files.",
            ReviewSkipReason.Superseded => "a new commit was pushed while the review ran. Run the preview again.",
            ReviewSkipReason.SwitchedOff => "reviews are switched off in the runtime settings.",
            _ => $"{reason}."
        };
    }

    private static string Cut(string text)
    {
        return text.Length <= MaxTextLength ? text : text[..(MaxTextLength - CutNote.Length)] + CutNote;
    }
}
