namespace GitHubReviewApp.Review;

/// <summary>
/// Everything the model writes is influenced by the pull request author, who controls the diff it reads, and it is posted to
/// GitHub in the app's name. This makes that text inert: it cannot notify people, render HTML, links or images, or leave a
/// code fence open that would swallow the rest of the review.
/// <para>
/// It re-emits the text instead of trying to spot danger in it. A code span or a fence is kept only when it is well formed
/// by the CommonMark rules. Every other backtick, bracket, angle bracket and ampersand is escaped, so a renderer's reading
/// of the output cannot differ from this class's reading of the input.
/// </para>
/// <para>
/// A web address or an issue reference written out in plain text is broken with a zero-width space, because GitHub turns it into a link
/// (or into a cross-reference on another issue); one inside a code span or a code block is left as written.
/// </para>
/// <para>
/// That holds for a reader that works line by line, and a line inside a list item or a block quote is read by other rules: a
/// fence in a container is closed by a line that this class would take for code. So the model's lists and quotes are shown as
/// plain lines (the marker is escaped), and the tabs in the indentation of a line that could open or close a fence are written
/// out as spaces, because the width of a tab depends on where a renderer meets it.
/// </para>
/// </summary>
public static class MarkdownSanitizer
{
    public const char ZeroWidthSpace = '\u200B';

    private const string Ellipsis = "…";

    // A literal backtick written as an entity. Renderers disagree about "\`" next to another backtick, but nobody reads an entity as a delimiter.
    private const string LiteralBacktick = "&#96;";
    private const int MaxFenceIndent = 3;
    private const int MinFenceLength = 3;
    private const int TabStop = 4;

    // The names a code block may keep. GitHub draws a few names as something other than code (mermaid as a diagram, geojson and topojson as a map,
    // stl as a 3D model, math as typeset text) and "suggestion" adds a one-click "Commit suggestion" button, so a name stays only when it is a
    // plain programming, markup or data language on this list. Anything else, including a language that is not listed, is dropped and the block
    // is shown as plain code.
    private static readonly HashSet<string> _codeLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "text", "txt", "plaintext", "console", "shell", "sh", "bash", "zsh", "fish", "powershell", "ps1", "pwsh", "bat", "batch", "cmd",
        "c", "h", "cpp", "c++", "cc", "cxx", "hpp", "cs", "csharp", "c#", "fsharp", "f#", "fs", "vb", "vbnet", "razor", "cshtml",
        "java", "kotlin", "kt", "scala", "groovy", "gradle", "js", "javascript", "jsx", "mjs", "cjs", "ts", "typescript", "tsx", "node",
        "json", "jsonc", "json5", "yaml", "yml", "toml", "ini", "xml", "html", "htm", "xhtml", "css", "scss", "sass", "less", "vue", "svelte",
        "py", "python", "python3", "rb", "ruby", "php", "go", "golang", "rs", "rust", "swift", "objc", "objective-c", "dart", "lua", "perl",
        "pl", "r", "julia", "elixir", "ex", "exs", "erlang", "haskell", "hs", "clojure", "scheme", "lisp",
        "sql", "tsql", "plsql", "pgsql", "mysql", "graphql", "gql", "proto", "protobuf",
        "diff", "patch", "markdown", "md", "dockerfile", "docker", "makefile", "make", "cmake", "nginx", "apache", "regex", "csv", "log", "http",
        "terraform", "tf", "hcl", "bicep"
    };

    // The character that makes a line a block quote, a list item or a numbered list item. A bullet or a number needs a space, a tab or the end
    // of the line after it; a quote marker needs nothing. Escaping that one character makes the line plain text.
    private static readonly Regex _containerMarker = new(
        @"^ *(?:(?<marker>>)|(?<marker>[-+*])(?=[ \t]|$)|[0-9]{1,9}(?<marker>[.)])(?=[ \t]|$))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // A line of three or more of the same "-", "*" or "_", with spaces allowed between them, is a thematic break. Renderers disagree about how much of
    // an open list one ends (some close the list around it, some keep it), so a line like that is shown as plain text instead.
    private static readonly Regex _thematicBreak = new(
        @"^ *(?<marker>[-*_])(?: *\k<marker>){2,} *$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly record struct Fence(char Marker, int Length, int Indent);

    /// <summary>
    /// Makes block markdown safe to post. <paramref name="maxChars"/> bounds the text that is kept (escaping can make the result
    /// longer); the cut is made first, so a fence it leaves open is closed again.
    /// </summary>
    public static string Neutralize(string? text, int maxChars = int.MaxValue)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var lines = Truncate(NormalizeLineEndings(text), maxChars).Split('\n');
        var hasTable = lines.Any(IsTableDelimiterRow);
        var output = new List<string>(lines.Length + 1);
        Fence? openFence = null;

        foreach (var rawLine in lines)
        {
            if (openFence is { } fence)
            {
                // Code is kept as written, except a line that could close the fence: its tabs become spaces, so that wherever a renderer reads it
                // (the formatter puts it inside a list item or a quote) it is the line this class judged, not a different one.
                var line = StartsWithFenceMarker(rawLine) ? ExpandLeadingTabs(rawLine) : rawLine;
                var isClosing = IsClosingFence(line, fence);

                output.Add(isClosing ? line.TrimEnd(' ', '\t') : line);
                openFence = isClosing ? null : fence;
                continue;
            }

            var expanded = ExpandLeadingTabs(rawLine);

            if (TryOpenFence(expanded, out var opened, out var openingLine))
            {
                output.Add(openingLine);
                openFence = opened;
                continue;
            }

            // A list or a quote in the model's text is shown as plain lines. Inside one, a renderer ends a fence by rules that differ from the
            // top level, so a reader that goes line by line cannot say which lines are code. With no container left to read, it can.
            output.Add(EscapeContainerMarker(SanitizeInline(expanded, isTableRow: hasTable && expanded.Contains('|'), canFormSpans: true)));
        }

        if (openFence is { } unclosed)
        {
            output.Add(ClosingLine(unclosed));
        }

        return string.Join('\n', output);
    }

    /// <summary>
    /// Makes one line of text safe to post inside a larger line, such as a finding title. Line breaks are collapsed first, so
    /// nothing in the text can start a block.
    /// </summary>
    public static string NeutralizeInline(string? text, int maxChars = int.MaxValue)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var flattened = text.ReplaceLineEndings(" ").Trim();

        return SanitizeInline(Truncate(flattened, maxChars), isTableRow: false, canFormSpans: true);
    }

    /// <summary>
    /// Prepares attacker-controlled text, such as a file path, to sit inside a backtick-delimited code span written by this app.
    /// It cannot end the span, and it cannot carry an HTML comment opener.
    /// </summary>
    public static string InlineCode(string? text, int maxChars = int.MaxValue)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var flattened = text.ReplaceLineEndings(" ").Replace('`', '\'').Trim();

        return Truncate(flattened, maxChars).Replace("<!--", "<" + ZeroWidthSpace + "!--", StringComparison.Ordinal);
    }

    /// <summary>Appends a closing fence when the markdown ends inside a fenced code block.</summary>
    public static string CloseOpenFences(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return string.Empty;
        }

        var normalized = NormalizeLineEndings(markdown);
        Fence? openFence = null;

        foreach (var line in normalized.Split('\n'))
        {
            if (openFence is { } fence)
            {
                openFence = IsClosingFence(line, fence) ? null : fence;
            }
            else if (TryOpenFence(line, out var opened, out _))
            {
                openFence = opened;
            }
        }

        return openFence is { } unclosed ? normalized + "\n" + ClosingLine(unclosed) : normalized;
    }

    private static string NormalizeLineEndings(string text)
    {
        // Only \n, \r and \r\n end a line for a markdown renderer, so only those are normalized. Other Unicode line
        // separators are not line breaks to GitHub and must not become one here.
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    private static string Truncate(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }

        var cut = Math.Max(maxChars - Ellipsis.Length, 0);

        // Do not split a surrogate pair.
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return text[..cut].TrimEnd() + Ellipsis;
    }

    private static bool StartsWithFenceMarker(string line)
    {
        var index = 0;

        while (index < line.Length && line[index] is ' ' or '\t')
        {
            index++;
        }

        return index < line.Length && line[index] is '`' or '~';
    }

    // A tab in the leading whitespace is as wide as the distance to the next multiple of four columns, which depends on where it sits.
    // Written out as spaces, the indentation is the same wherever the line is read.
    private static string ExpandLeadingTabs(string line)
    {
        var column = 0;
        var index = 0;
        var hasTab = false;

        while (index < line.Length && line[index] is ' ' or '\t')
        {
            if (line[index] == '\t')
            {
                hasTab = true;
                column = ((column / TabStop) + 1) * TabStop;
            }
            else
            {
                column++;
            }

            index++;
        }

        return hasTab ? new string(' ', column) + line[index..] : line;
    }

    private static string EscapeContainerMarker(string line)
    {
        var match = _containerMarker.Match(line);

        if (!match.Success)
        {
            match = _thematicBreak.Match(line);
        }

        return match.Success ? line.Insert(match.Groups["marker"].Index, @"\") : line;
    }

    private static bool TryOpenFence(string line, out Fence fence, out string openingLine)
    {
        fence = default;
        openingLine = string.Empty;

        // A tab in the leading whitespace counts as four columns, so it never leaves room for a fence.
        var indent = CountRun(line, 0, ' ');

        if (indent > MaxFenceIndent || indent >= line.Length || line[indent] is not ('`' or '~'))
        {
            return false;
        }

        var marker = line[indent];
        var length = CountRun(line, indent, marker);
        var info = line[(indent + length)..];

        // A backtick fence cannot have a backtick in its info string; such a line is inline code.
        if (length < MinFenceLength || (marker == '`' && info.Contains('`')))
        {
            return false;
        }

        fence = new Fence(marker, length, indent);
        openingLine = new string(' ', indent) + new string(marker, length) + ExtractLanguage(info);

        return true;
    }

    private static string ExtractLanguage(string info)
    {
        var word = info.Trim().Split([' ', '\t'], 2)[0];

        return _codeLanguages.Contains(word) ? word : string.Empty;
    }

    private static bool IsClosingFence(string line, Fence fence)
    {
        var indent = CountRun(line, 0, ' ');

        if (indent > MaxFenceIndent || indent >= line.Length || line[indent] != fence.Marker)
        {
            return false;
        }

        var length = CountRun(line, indent, fence.Marker);

        return length >= fence.Length && IsBlank(line.AsSpan(indent + length));
    }

    private static bool IsBlank(ReadOnlySpan<char> text)
    {
        foreach (var character in text)
        {
            if (character is not (' ' or '\t'))
            {
                return false;
            }
        }

        return true;
    }

    private static string ClosingLine(Fence fence)
    {
        return new string(' ', fence.Indent) + new string(fence.Marker, fence.Length);
    }

    // GitHub reads a row of only hyphens, colons, pipes and blanks as the line under a table's header, and it cuts the rows below at a pipe before it
    // reads a code span. A header of one cell needs no pipe of its own, so neither does the row. With no pipe, a row of hyphens alone is the underline of a
    // heading, or a rule, so such a row counts only with a colon in it, or with a vertical tab or a form feed, which a heading underline does not allow.
    private static bool IsTableDelimiterRow(string line)
    {
        return line.Contains('-')
            && line.All(character => character is '|' or '-' or ':' or ' ' or '\t' or '\v' or '\f')
            && line.Any(character => character is '|' or ':' or '\v' or '\f');
    }

    private static string SanitizeInline(string text, bool isTableRow, bool canFormSpans)
    {
        var builder = new StringBuilder(text.Length + 8);
        var index = 0;

        while (index < text.Length)
        {
            index = text[index] switch
            {
                '\\' => AppendEscape(builder, text, index),
                '`' => AppendBackticks(builder, text, index, isTableRow, canFormSpans),
                _ => AppendPlain(builder, text, index)
            };
        }

        return builder.ToString();
    }

    private static int AppendEscape(StringBuilder builder, string text, int index)
    {
        if (index + 1 >= text.Length || !IsAsciiPunctuation(text[index + 1]))
        {
            builder.Append('\\');
            return index + 1;
        }

        var escaped = text[index + 1];

        switch (escaped)
        {
            case '@':
                // The renderer shows "@" for "\@", so the text after it can still be a mention.
                AppendAtSign(builder, text, index + 1);
                break;
            case ':' when text.AsSpan(index + 1).StartsWith("://", StringComparison.Ordinal):
                // Likewise "\:" is shown as ":", which leaves a web address that GitHub turns into a link.
                builder.Append(':').Append(ZeroWidthSpace);
                break;
            case '#' when index + 2 < text.Length && char.IsAsciiDigit(text[index + 2]):
                builder.Append('#').Append(ZeroWidthSpace);
                break;
            case '-' when IsEscapedIssueHyphen(text, index):
                // "GH\-12" is shown as "GH-12", which GitHub links, and GitHub reads the text the renderer shows. The two letters are already out, so
                // the backslash goes and the space follows the hyphen, as it does for the plain form.
                builder.Append('-').Append(ZeroWidthSpace);
                break;
            case '$':
                // "\$" is shown as "$", which next to a backtick still opens or closes a math region.
                AppendDollar(builder, text, index, 2);
                break;
            case '<':
                builder.Append("&lt;");
                break;
            case '&':
                builder.Append("&amp;");
                break;
            case '`':
                builder.Append(LiteralBacktick);
                break;
            default:
                builder.Append('\\').Append(escaped);
                break;
        }

        return index + 2;
    }

    private static int AppendBackticks(StringBuilder builder, string text, int index, bool isTableRow, bool canFormSpans)
    {
        var length = CountRun(text, index, '`');
        var closer = canFormSpans ? FindClosingRun(text, index + length, length) : -1;

        // An unmatched run is literal text. Written as an entity it cannot be paired with a backtick on a later line.
        if (closer < 0)
        {
            for (var count = 0; count < length; count++)
            {
                builder.Append(LiteralBacktick);
            }

            return index + length;
        }

        var end = closer + length;
        var span = text[index..end];

        // GitHub splits a table row into cells before it reads code spans, so a pipe inside a span ends the cell and
        // leaves the rest as plain text. Such a span is therefore not code and gets the plain-text treatment.
        if (isTableRow && span.Contains('|'))
        {
            builder.Append(SanitizeInline(span, isTableRow: false, canFormSpans: false));
        }
        else
        {
            builder.Append(span);
        }

        return end;
    }

    private static int AppendPlain(StringBuilder builder, string text, int index)
    {
        var current = text[index];

        switch (current)
        {
            case '@':
                AppendAtSign(builder, text, index);
                break;
            case ':' when text.AsSpan(index).StartsWith("://", StringComparison.Ordinal):
                builder.Append(':').Append(ZeroWidthSpace);
                break;
            case '#' when index + 1 < text.Length && char.IsAsciiDigit(text[index + 1]):
                builder.Append('#').Append(ZeroWidthSpace);
                break;
            case '$':
                AppendDollar(builder, text, index, 1);
                break;
            case >= '0' and <= '9' when IsAfterIssuePath(text, index):
                builder.Append(ZeroWidthSpace).Append(current);
                break;
            case 'w' or 'W' when StartsWord(text, index) && text.AsSpan(index).StartsWith("www.", StringComparison.OrdinalIgnoreCase):
                // The three letters go out as they are and the space is put before the dot, which is where GitHub looks for the address.
                builder.Append(text, index, 3).Append(ZeroWidthSpace);
                return index + 3;
            case 'g' or 'G' when StartsWord(text, index) && IsGitHubIssueReference(text, index):
                builder.Append(text, index, 3).Append(ZeroWidthSpace);
                return index + 3;
            case '<':
                builder.Append("&lt;");
                break;
            case '&':
                builder.Append("&amp;");
                break;
            case '[':
            case ']':
                builder.Append('\\').Append(current);
                break;
            default:
                builder.Append(current);
                break;
        }

        return index + 1;
    }

    private static bool StartsWord(string text, int index)
    {
        return index == 0 || !char.IsAsciiLetterOrDigit(text[index - 1]);
    }

    // "GH-12" is another way to write "#12" that GitHub links.
    private static bool IsGitHubIssueReference(string text, int index)
    {
        return text.AsSpan(index).StartsWith("gh-", StringComparison.OrdinalIgnoreCase)
            && index + 3 < text.Length
            && char.IsAsciiDigit(text[index + 3]);
    }

    // "GH\-12": a "gh" that starts a word, the escaped hyphen and a digit. The backslash index is that of the backslash.
    private static bool IsEscapedIssueHyphen(string text, int backslashIndex)
    {
        return backslashIndex >= 2
            && text.AsSpan(backslashIndex - 2, 2).Equals("gh", StringComparison.OrdinalIgnoreCase)
            && StartsWord(text, backslashIndex - 2)
            && backslashIndex + 2 < text.Length
            && char.IsAsciiDigit(text[backslashIndex + 2]);
    }

    private static void AppendAtSign(StringBuilder builder, string text, int atIndex)
    {
        builder.Append('@');

        // GitHub reads "@name" as a mention, and "name@host.tld" as an e-mail address that it links (a "mailto:" link, or "xmpp:" before the name).
        // Both start with a letter or digit right after the "@", and an address also links after an underscore, a hyphen or a dot there, so that is
        // where the space goes, whatever comes before the "@".
        if (atIndex + 1 < text.Length && (char.IsAsciiLetterOrDigit(text[atIndex + 1]) || text[atIndex + 1] is '_' or '-' or '.'))
        {
            builder.Append(ZeroWidthSpace);
        }
    }

    // "$`" opens and "`$" closes a math region, in which GitHub links the mentions and the references in every code span the region holds. The
    // dollar sign is kept apart from a backtick next to it on either side, which ends the region before it starts. What is written is the dollar sign
    // alone, or a backslash and the dollar sign, and the length says which.
    private static void AppendDollar(StringBuilder builder, string text, int index, int length)
    {
        if (index > 0 && text[index - 1] == '`')
        {
            builder.Append(ZeroWidthSpace);
        }

        builder.Append(text, index, length);

        if (index + length < text.Length && text[index + length] == '`')
        {
            builder.Append(ZeroWidthSpace);
        }
    }

    // GitHub links "owner/repo/issues/12" as it links "#12", with the host name before it or without. The space goes before the first digit.
    private static bool IsAfterIssuePath(string text, int index)
    {
        var before = text.AsSpan(0, index);

        return before.EndsWith("/issues/", StringComparison.OrdinalIgnoreCase)
            || before.EndsWith("/pull/", StringComparison.OrdinalIgnoreCase)
            || before.EndsWith("/discussions/", StringComparison.OrdinalIgnoreCase);
    }

    private static int FindClosingRun(string text, int start, int length)
    {
        var position = start;

        while (position < text.Length)
        {
            if (text[position] != '`')
            {
                position++;
                continue;
            }

            var runLength = CountRun(text, position, '`');

            if (runLength == length)
            {
                return position;
            }

            position += runLength;
        }

        return -1;
    }

    private static int CountRun(string text, int start, char marker)
    {
        var position = start;

        while (position < text.Length && text[position] == marker)
        {
            position++;
        }

        return position - start;
    }

    private static bool IsAsciiPunctuation(char character)
    {
        return character is (>= '!' and <= '/') or (>= ':' and <= '@') or (>= '[' and <= '`') or (>= '{' and <= '~');
    }
}
