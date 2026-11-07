namespace GitHubReviewApp.Tests.Review;

public class MarkdownSanitizerTests
{
    private const string ZeroWidthSpace = "\u200B";

    /// <summary>Inputs a markdown renderer turns into a live mention, each found by a reviewer or by reading the CommonMark rules.</summary>
    public static TheoryData<string> KnownMentionBypasses => new()
    {
        "\\`@octocat\\`",
        "para\n\n    ```\n@octocat",
        "para\n\n\t```\n@octocat",
        "``` x`y\n@octocat",
        "`a\nb` @octocat `c`",
        "\\@octocat",
        "&#64;octocat",
        "&#x40;octocat",
        "&commat;octocat",
        "```\r@a\r```\r@octocat"
    };

    [Theory]
    [InlineData("@octocat", "@\u200Boctocat")]
    [InlineData("ping @octocat now", "ping @\u200Boctocat now")]
    [InlineData("ask @my-org/platform-team", "ask @\u200Bmy-org/platform-team")]
    [InlineData("(@octocat)", "(@\u200Boctocat)")]
    [InlineData("@a and @b", "@\u200Ba and @\u200Bb")]
    [InlineData("contact me@example.com", "contact me@\u200Bexample.com")]
    [InlineData("xmpp:user@example.com", "xmpp:user@\u200Bexample.com")]
    [InlineData("a.b+c@d-e.org", "a.b+c@\u200Bd-e.org")]
    [InlineData("a@_b.com", "a@" + ZeroWidthSpace + "_b.com")]
    [InlineData("a@-b.com", "a@" + ZeroWidthSpace + "-b.com")]
    [InlineData("a@.b.com", "a@" + ZeroWidthSpace + ".b.com")]
    [InlineData("xmpp:a@_b.com", "xmpp:a@" + ZeroWidthSpace + "_b.com")]
    public void Neutralize_GivenMentionOrEmailAddress_Should_InsertAZeroWidthSpaceSoGitHubDoesNotNotifyAnyoneOrMakeALink(string input, string expected)
    {
        // Arrange

        // Act
        var result = MarkdownSanitizer.Neutralize(input);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("a @ b")]
    [InlineData("price @ 5")]
    [InlineData("a@+b.com")]
    [InlineData("no mentions here")]
    public void Neutralize_GivenTextThatIsNotAMention_Should_LeaveItUnchanged(string input)
    {
        // Arrange

        // Act
        var result = MarkdownSanitizer.Neutralize(input);

        // Assert
        result.Should().Be(input);
    }

    [Fact]
    public void Neutralize_GivenMentionInsideInlineCode_Should_LeaveTheCodeUnchanged()
    {
        // Arrange
        const string input = "Use `@Injectable()` here, thanks @octocat";

        // Act
        var result = MarkdownSanitizer.Neutralize(input);

        // Assert
        result.Should().Be("Use `@Injectable()` here, thanks @\u200Boctocat");
    }

    [Fact]
    public void Neutralize_GivenMentionInsideAFencedCodeBlock_Should_LeaveTheBlockUnchanged()
    {
        // Arrange
        var input = string.Join("\n", "before @octocat", "```java", "@Override", "void run() {}", "```", "after @octocat");

        // Act
        var result = MarkdownSanitizer.Neutralize(input);

        // Assert
        result.Should().Be(string.Join("\n", "before @\u200Boctocat", "```java", "@Override", "void run() {}", "```", "after @\u200Boctocat"));
    }

    [Fact]
    public void Neutralize_GivenAnUnclosedFence_Should_CloseItSoTheRestOfThePostedReviewIsNotSwallowed()
    {
        // Arrange
        var input = string.Join("\n", "```", "@Override");

        // Act
        var result = MarkdownSanitizer.Neutralize(input);

        // Assert
        result.Should().Be(string.Join("\n", "```", "@Override", "```"));
        MarkdownOracle.SwallowsFollowingText(result).Should().BeFalse();
    }

    [Fact]
    public void Neutralize_GivenAnUnclosedBacktick_Should_StillNeutralizeMentionsOnThatLine()
    {
        // Arrange — an attacker leaving a backtick open must not smuggle a mention through
        const string input = "`oops @octocat";

        // Act
        var result = MarkdownSanitizer.Neutralize(input);

        // Assert
        result.Should().Be("&#96;oops @\u200Boctocat");
    }

    [Fact]
    public void Neutralize_GivenHtmlCommentOpener_Should_EscapeItSoTheDedupeMarkerCannotBeForged()
    {
        // Arrange
        const string input = "<!-- ai-review:sha=abc -->";

        // Act
        var result = MarkdownSanitizer.Neutralize(input);

        // Assert
        result.Should().NotContain("<!--");
        result.Should().Contain("&lt;!--");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Neutralize_GivenNullOrEmptyText_Should_ReturnEmpty(string? input)
    {
        // Arrange

        // Act
        var result = MarkdownSanitizer.Neutralize(input);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void Neutralize_GivenOrdinaryMarkdown_Should_NotAddAnyInvisibleCharacters()
    {
        // Arrange
        const string input = "**Heading**\n\nA paragraph with *emphasis* and `code`.\n\n```cs\nvar a = 1;\n```\n";

        // Act
        var result = MarkdownSanitizer.Neutralize(input);

        // Assert
        result.Should().Be(input);
        result.Should().NotContain(ZeroWidthSpace);
    }

    [Theory]
    [MemberData(nameof(KnownMentionBypasses))]
    public void HasLiveMention_GivenAKnownBypassInput_Should_BeTrueSoTheOracleCanFail(string input)
    {
        // Arrange

        // Act
        var isLive = MarkdownOracle.HasLiveMention(input);

        // Assert
        isLive.Should().BeTrue();
    }

    [Theory]
    [InlineData("$`x`$")]
    [InlineData("a `b`$ c")]
    [InlineData("a $`b` c")]
    public void HasMathNextToCode_GivenADollarSignTouchingASpan_Should_BeTrueSoTheOracleCanFail(string input)
    {
        // Arrange

        // Act
        var isFound = MarkdownOracle.HasMathNextToCode(input);

        // Assert
        isFound.Should().BeTrue();
    }

    [Theory]
    [InlineData("$ `x` $")]
    [InlineData("it costs $5 and `x`")]
    [InlineData("`$HOME` is set")]
    public void HasMathNextToCode_GivenNoDollarSignTouchingASpan_Should_BeFalse(string input)
    {
        // Arrange

        // Act
        var isFound = MarkdownOracle.HasMathNextToCode(input);

        // Assert
        isFound.Should().BeFalse();
    }

    [Theory]
    [InlineData("write to a@.b.com now")]
    [InlineData("write to a@_b.com now")]
    [InlineData("see octocat/Hello-World/pull/1 here")]
    [InlineData("see /issues/12 here")]
    public void HasAutoLinkedText_GivenAnAddressOrPathThatGitHubLinks_Should_BeTrueSoTheOracleCanFail(string input)
    {
        // Arrange

        // Act
        var isFound = MarkdownOracle.HasAutoLinkedText(input);

        // Assert
        isFound.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(KnownMentionBypasses))]
    public void Neutralize_GivenAKnownMentionBypass_Should_LeaveNoLiveMention(string input)
    {
        // Arrange

        // Act
        var result = MarkdownSanitizer.Neutralize(input);

        // Assert
        MarkdownOracle.HasLiveMention(result).Should().BeFalse();
    }

    [Fact]
    public void Neutralize_GivenACodeSpanWithAPipeInsideATableRow_Should_TreatItAsPlainTextBecauseGitHubSplitsTheCellFirst()
    {
        // Arrange — the GFM spec splits a table row on every unescaped pipe before it reads code spans (Markdig does not, so the oracle cannot judge this)
        const string input = "| a | b |\n|---|---|\n| `@octocat|x` | `<b>` |";

        // Act
        var result = MarkdownSanitizer.Neutralize(input);

        // Assert
        result.Should().Be("| a | b |\n|---|---|\n| &#96;@\u200Boctocat|x&#96; | `<b>` |");
    }

    [Theory]
    [InlineData("|---|---|")]
    [InlineData("|:--|--:|")]
    [InlineData("|:-:|:-:|")]
    [InlineData("-|-")]
    [InlineData("| --- | --- |")]
    [InlineData(":-|-:")]
    public void Neutralize_GivenATableWhoseDelimiterRowIsWrittenInAnyCommonWay_Should_TreatACodeSpanWithAPipeInARowAsPlainText(string delimiterRow)
    {
        // Arrange — the oracle cannot judge this (it reads the span before the cell), so the exact output is pinned for every way of writing the row
        var input = "| a | b |\n" + delimiterRow + "\n| `@octocat|x` | y |";

        // Act
        var result = MarkdownSanitizer.Neutralize(input);

        // Assert
        result.Should().Be("| a | b |\n" + delimiterRow + "\n| &#96;@" + ZeroWidthSpace + "octocat|x&#96; | y |");
    }

    [Theory]
    [InlineData("-|-\nnot a table row `x` here")]
    [InlineData("a | b\n| : |\n`x|y`")]
    [InlineData("||\n`x|y`")]
    public void Neutralize_GivenALineThatOnlyLooksLikeADelimiterRow_Should_LeaveCodeSpansAlone(string input)
    {
        // Arrange — pipes and colons with no dash are not a delimiter row, and a row with no pipe after one is not a table row: neither is a reason
        // to treat a span as text

        // Act
        var result = MarkdownSanitizer.Neutralize(input);

        // Assert
        result.Should().Be(input);
    }

    [Theory]
    [InlineData("---")]
    [InlineData("--")]
    [InlineData("- - -")]
    [InlineData("  ---  ")]
    public void Neutralize_GivenARowOfHyphensAloneUnderAHeader_Should_LeaveACodeSpanWithAPipeAsCode(string row)
    {
        // Arrange — with no pipe, no colon and only spaces and tabs, GitHub reads this as the underline of a heading or as a rule, not as a table
        var input = "h\n" + row + "\n`x|y`";

        // Act
        var result = MarkdownSanitizer.Neutralize(input);

        // Assert
        result.Split('\n')[^1].Should().Be("`x|y`");
    }

    [Theory]
    [InlineData(":--")]
    [InlineData(":-:")]
    [InlineData("-:")]
    [InlineData("  :--  ")]
    [InlineData("\v-")]
    [InlineData("-\f")]
    [InlineData("|\v-\v|")]
    public void Neutralize_GivenADelimiterRowWithNoPipeUnderAHeaderOfOneCell_Should_TreatACodeSpanWithAPipeAsPlainText(string delimiterRow)
    {
        // Arrange — GitHub reads "h" over ":--" as a table of one column, with no pipe needed, and cuts its rows at a pipe before it reads a code span.
        // A row of hyphens with a vertical tab or a form feed is a table too, because a heading underline allows only spaces and tabs
        var input = "h\n" + delimiterRow + "\n`see https://evil.example/login|y`";

        // Act
        var result = MarkdownSanitizer.Neutralize(input);

        // Assert
        result.Split('\n')[^1].Should().Be("&#96;see https:" + ZeroWidthSpace + "//evil.example/login|y&#96;");
    }

    [Fact]
    public void Neutralize_GivenAHeaderOfTwoCellsOverADelimiterRowOfOne_Should_StillTreatSpansWithPipesAsTextBecauseTheWidthsAreNotCompared()
    {
        // Arrange — GitHub shows no table here and leaves the span as code. Telling that apart needs the cell counts of two lines in whatever
        // container they sit in. Reading the row as a table costs only how a span with a pipe is shown; reading a real table as plain text
        // would cost a live mention
        const string input = "a | b\n:-:\n`x|y`";

        // Act
        var result = MarkdownSanitizer.Neutralize(input);

        // Assert
        result.Split('\n')[^1].Should().Be("&#96;x|y&#96;");
    }

    [Theory]
    [InlineData("$`@octocat`$")]
    [InlineData("$`a` `@octocat` `b`$")]
    [InlineData("$``@octocat``$")]
    [InlineData("\\$`@octocat`\\$")]
    [InlineData("x `a`$ y")]
    [InlineData("x $`a` y")]
    public void Neutralize_GivenADollarSignNextToABacktick_Should_PutASpaceBetweenThemSoGitHubReadsNoMathRegion(string input)
    {
        // Arrange — "$`" opens and "`$" closes a math region, and GitHub links the mentions and references in every span the region holds, the spans
        // in the middle that no dollar sign touches included

        // Act
        var result = MarkdownSanitizer.Neutralize(input);

        // Assert
        result.Should().NotContain("$`").And.NotContain("`$");
    }

    [Fact]
    public void Neutralize_GivenASpanBetweenTwoDollarSigns_Should_PutASpaceOnBothSidesOfTheSpanAndChangeNothingElse()
    {
        // Arrange

        // Act
        var result = MarkdownSanitizer.Neutralize("$`x`$");

        // Assert
        result.Should().Be("$" + ZeroWidthSpace + "`x`" + ZeroWidthSpace + "$");
    }

    [Fact]
    public void Neutralize_GivenASpanBetweenTwoEscapedDollarSigns_Should_KeepTheEscapesAndPutASpaceOnBothSidesOfTheSpan()
    {
        // Arrange — "\$" is shown as "$", so it is kept apart from the backtick like a plain one, and is still written as it was

        // Act
        var result = MarkdownSanitizer.Neutralize("\\$`x`\\$");

        // Assert
        result.Should().Be("\\$" + ZeroWidthSpace + "`x`" + ZeroWidthSpace + "\\$");
    }

    [Theory]
    [InlineData("it costs $5 and `y`")]
    [InlineData("`$HOME` is set")]
    [InlineData("$ `x` $")]
    [InlineData("US$ and `x`")]
    public void Neutralize_GivenADollarSignThatTouchesNoBacktick_Should_LeaveTheTextAlone(string input)
    {
        // Arrange

        // Act
        var result = MarkdownSanitizer.Neutralize(input);

        // Assert
        result.Should().Be(input);
    }

    [Theory]
    [InlineData("see octocat/Hello-World/pull/1 here", "see octocat/Hello-World/pull/" + ZeroWidthSpace + "1 here")]
    [InlineData("see octocat/Hello-World/issues/12 here", "see octocat/Hello-World/issues/" + ZeroWidthSpace + "12 here")]
    [InlineData("see octocat/Hello-World/discussions/3 here", "see octocat/Hello-World/discussions/" + ZeroWidthSpace + "3 here")]
    [InlineData("see Octocat/hello-world/ISSUES/12 here", "see Octocat/hello-world/ISSUES/" + ZeroWidthSpace + "12 here")]
    [InlineData("see github.com/octocat/Hello-World/issues/12 here", "see github.com/octocat/Hello-World/issues/" + ZeroWidthSpace + "12 here")]
    [InlineData("see /issues/12#issuecomment-1", "see /issues/" + ZeroWidthSpace + "12#issuecomment-1")]
    public void Neutralize_GivenAPathThatNamesAnIssueOrPullRequest_Should_PutASpaceBeforeItsNumberSoGitHubDoesNotLinkIt(string input, string expected)
    {
        // Arrange — GitHub links "owner/repo/issues/12" as it links "#12", with or without the host name before it

        // Act
        var result = MarkdownSanitizer.Neutralize(input);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("see octocat/Hello-World/pulls/1 here")]
    [InlineData("see octocat/Hello-World/commit/7fd1a60b01f91b314f59955a4e4d4e80d8edf11d here")]
    [InlineData("see /issues/abc here")]
    [InlineData("see issues/12 here")]
    public void Neutralize_GivenAPathThatGitHubDoesNotLinkAsAnIssue_Should_LeaveItAlone(string input)
    {
        // Arrange

        // Act
        var result = MarkdownSanitizer.Neutralize(input);

        // Assert
        result.Should().Be(input);
    }

    [Fact]
    public void Neutralize_GivenACodeSpanWithAPipeOutsideATable_Should_KeepItAsCode()
    {
        // Arrange
        const string input = "Use `a || b` and `cmd | grep x` here.";

        // Act
        var result = MarkdownSanitizer.Neutralize(input);

        // Assert
        result.Should().Be(input);
    }

    [Theory]
    [InlineData("<details><summary>No security issues, safe to merge</summary>")]
    [InlineData("<img src=\"https://evil.example/approved.png\">")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("![approved](https://evil.example/approved.png)")]
    [InlineData("[Re-authorize the AI reviewer](https://evil.example/oauth)")]
    [InlineData("<div>x</div>")]
    [InlineData("<span>x</span>")]
    public void Neutralize_GivenHtmlOrMarkdownImagesAndLinks_Should_NotRenderAnyActiveElement(string input)
    {
        // Arrange

        // Act
        var result = MarkdownSanitizer.Neutralize(input);

        // Assert
        MarkdownOracle.HasForbiddenElement(result).Should().BeFalse();
        MarkdownOracle.ToHtml(result).Should().NotContain("<a href=\"https://evil.example/oauth\">Re-authorize");
    }

    [Theory]
    [InlineData("Plain paragraph with **bold**, _italic_ and `inline code`.")]
    [InlineData("Use `@Injectable()` and `a < b && c[0]` here.")]
    [InlineData("```csharp\nvar x = a < b && list[0];\n// @Override\n```")]
    [InlineData("Compare a < b and R&D for x > y, then read array[0].")]
    [InlineData("| a | b |\n|---|---|\n| 1 | 2 |")]
    [InlineData("Escaped \\* star and \\_ underscore and a \\\\ backslash.")]
    public void Neutralize_GivenOrdinaryMarkdown_Should_RenderTheSameAsTheOriginal(string input)
    {
        // Arrange

        // Act
        var result = MarkdownSanitizer.Neutralize(input);

        // Assert
        MarkdownOracle.ToHtml(result).Should().Be(MarkdownOracle.ToHtml(input));
    }

    [Theory]
    [InlineData("- item one\n- item two", @"\- item one" + "\n" + @"\- item two")]
    [InlineData("* star", @"\* star")]
    [InlineData("+ plus", @"\+ plus")]
    [InlineData("-", @"\-")]
    [InlineData("1. first", @"1\. first")]
    [InlineData("2) second", @"2\) second")]
    [InlineData("123456789. nine digits", @"123456789\. nine digits")]
    [InlineData("> quoted", @"\> quoted")]
    [InlineData(">no space", @"\>no space")]
    [InlineData("> - nested", @"\> - nested")]
    [InlineData("   - indented", @"   \- indented")]
    [InlineData("\t- tabbed", @"    \- tabbed")]
    [InlineData("-\ttab after the marker", @"\-" + "\ttab after the marker")]
    public void Neutralize_GivenAListOrQuoteMarker_Should_EscapeItSoTheLineIsPlainText(string input, string expected)
    {
        // Arrange

        // Act
        var result = MarkdownSanitizer.Neutralize(input);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("---", @"\---")]
    [InlineData("-----", @"\-----")]
    [InlineData("***", @"\***")]
    [InlineData("___", @"\___")]
    [InlineData("  ---", @"  \---")]
    [InlineData("-- -", @"\-- -")]
    [InlineData("_ _ _  ", @"\_ _ _  ")]
    [InlineData("- - -", @"\- - -")]
    [InlineData("a\n---\nb", "a\n" + @"\---" + "\nb")]
    public void Neutralize_GivenAThematicBreak_Should_ShowItAsPlainTextBecauseRenderersDisagreeAboutWhatItEndsAroundAnOpenList(string input, string expected)
    {
        // Arrange — one renderer closes the list a break appears in and another keeps it, so which lines belong to the list (and which are code)
        // would depend on the renderer. As text, the line cannot end anything.

        // Act
        var result = MarkdownSanitizer.Neutralize(input);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("--")]
    [InlineData("**bold**")]
    [InlineData("--- text")]
    [InlineData("a---")]
    [InlineData("-*-")]
    [InlineData("```\n---\n```")]
    public void Neutralize_GivenALineThatOnlyLooksLikeAThematicBreakOrIsCode_Should_LeaveItUnchanged(string input)
    {
        // Arrange

        // Act
        var result = MarkdownSanitizer.Neutralize(input);

        // Assert
        result.Should().Be(input);
    }

    [Theory]
    [InlineData("-x")]
    [InlineData("+x")]
    [InlineData("*emphasis*")]
    [InlineData("1.5 is a number")]
    [InlineData("1234567890. ten digits are not a list")]
    [InlineData("a - b")]
    [InlineData("x > y")]
    [InlineData("3 + 4")]
    public void Neutralize_GivenTextThatOnlyLooksLikeAListMarker_Should_LeaveItUnchanged(string input)
    {
        // Arrange

        // Act
        var result = MarkdownSanitizer.Neutralize(input);

        // Assert
        result.Should().Be(input);
    }

    [Theory]
    [InlineData("- item one\n- item two")]
    [InlineData("1. first\n2. second")]
    [InlineData("> quoted\n> more quoted")]
    [InlineData("- outer\n  - inner\n    > quote in a list")]
    [InlineData("> 1. numbered in a quote\n> - bullet in a quote")]
    [InlineData("- [ ] open task\n- [x] done task")]
    public void Neutralize_GivenListsAndQuotes_Should_RenderNoListOrQuoteElementAndKeepEveryWord(string input)
    {
        // Arrange
        string[] containerElements = ["ul", "ol", "li", "blockquote"];

        // Act
        var result = MarkdownSanitizer.Neutralize(input);

        // Assert
        containerElements.Sum(name => MarkdownOracle.CountElements(result, name)).Should().Be(0);
        CollapseWhitespace(MarkdownOracle.VisibleText(result)).Should().Be(CollapseWhitespace(input));
    }

    [Fact]
    public void Neutralize_GivenAFencedCodeBlockInsideAListItem_Should_StillRenderItAsACodeBlock()
    {
        // Arrange
        const string input = "1. Do this:\n\n   ```cs\n   var a = 1;\n   ```\n\n2. Then that.";

        // Act
        var result = MarkdownSanitizer.Neutralize(input);

        // Assert
        MarkdownOracle.CountElements(result, "pre").Should().Be(1);
        MarkdownOracle.ToHtml(result).Should().Contain("language-cs").And.Contain("var a = 1;");
    }

    [Theory]
    [InlineData("```\n\t```\ncode", "```\n    ```\ncode\n```")]
    [InlineData("```\n  \t~~~\ncode", "```\n    ~~~\ncode\n```")]
    [InlineData("```\n\tint x;\n```", "```\n\tint x;\n```")]
    [InlineData("para\n\t```\ntext", "para\n    &#96;&#96;&#96;\ntext")]
    public void Neutralize_GivenATabInTheIndentationOfALineThatCouldOpenOrCloseAFence_Should_WriteTheTabAsSpaces(string input, string expected)
    {
        // Arrange

        // Act
        var result = MarkdownSanitizer.Neutralize(input);

        // Assert
        result.Should().Be(expected);
    }

    private static string CollapseWhitespace(string text)
    {
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Neutralize_GivenGeneratedHostileFragments_Should_BreakNoInvariantAnIndependentRendererCanSee(int seed)
    {
        // Arrange
        var fragments = MarkdownFragmentGenerator.Generate(seed, count: 3000);

        // Act
        var violations = MarkdownOracle.FindViolations(fragments, text => MarkdownSanitizer.Neutralize(text));

        // Assert
        violations.Should().BeEmpty();
    }

    [Theory]
    [InlineData("a < b", "a &lt; b")]
    [InlineData("R&D", "R&amp;D")]
    [InlineData("\\<b>", "&lt;b>")]
    [InlineData("\\@octocat", "@\u200Boctocat")]
    [InlineData("a\\@b", "a@\u200Bb")]
    [InlineData("_@octocat_", "_@\u200Boctocat_")]
    [InlineData("\\`code", "&#96;code")]
    [InlineData("\\[x\\]", "\\[x\\]")]
    [InlineData("[x](y)", "\\[x\\](y)")]
    [InlineData("C:\\Users\\me", "C:\\Users\\me")]
    [InlineData("trailing\\", "trailing\\")]
    public void Neutralize_GivenCharactersThatAreSpecialToMarkdown_Should_EscapeThemWithoutChangingWhatIsRendered(string input, string expected)
    {
        // Arrange

        // Act
        var result = MarkdownSanitizer.Neutralize(input);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("```suggestion\ncode\n```", "```\ncode\n```")]
    [InlineData("```Suggestion\ncode\n```", "```\ncode\n```")]
    [InlineData("```c#\ncode\n```", "```c#\ncode\n```")]
    [InlineData("``` csharp extra words\ncode\n```", "```csharp\ncode\n```")]
    [InlineData("```@octocat\ncode\n```", "```\ncode\n```")]
    [InlineData("~~~diff\n-a\n+b\n~~~", "~~~diff\n-a\n+b\n~~~")]
    [InlineData("````\n```\ninner\n```\n````", "````\n```\ninner\n```\n````")]
    [InlineData("  ```cs\n  code\n  ```", "  ```cs\n  code\n  ```")]
    [InlineData("```js\ncode\n```", "```js\ncode\n```")]
    [InlineData("```C++\ncode\n```", "```C++\ncode\n```")]
    [InlineData("```PowerShell\ncode\n```", "```PowerShell\ncode\n```")]
    [InlineData("```mermaid\ngraph TD\n```", "```\ngraph TD\n```")]
    [InlineData("```MERMAID\ngraph TD\n```", "```\ngraph TD\n```")]
    [InlineData("```geojson\n{}\n```", "```\n{}\n```")]
    [InlineData("```topojson\n{}\n```", "```\n{}\n```")]
    [InlineData("```stl\nsolid x\n```", "```\nsolid x\n```")]
    [InlineData("```math\nx^2\n```", "```\nx^2\n```")]
    [InlineData("```somethingnew\ncode\n```", "```\ncode\n```")]
    public void Neutralize_GivenAFenceWithAnInfoString_Should_KeepOnlyAHarmlessLanguageTag(string input, string expected)
    {
        // Arrange

        // Act
        var result = MarkdownSanitizer.Neutralize(input);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("see https://evil.example/login now", "see https:" + ZeroWidthSpace + "//evil.example/login now")]
    [InlineData("HTTP://EVIL.EXAMPLE", "HTTP:" + ZeroWidthSpace + "//EVIL.EXAMPLE")]
    [InlineData("ftp://host/file", "ftp:" + ZeroWidthSpace + "//host/file")]
    [InlineData("go to www.evil.example", "go to www" + ZeroWidthSpace + ".evil.example")]
    [InlineData("(WWW.evil.example)", "(WWW" + ZeroWidthSpace + ".evil.example)")]
    [InlineData("a http://x.y and https://z.w", "a http:" + ZeroWidthSpace + "//x.y and https:" + ZeroWidthSpace + "//z.w")]
    [InlineData(@"https\://evil.example", "https:" + ZeroWidthSpace + "//evil.example")]
    public void Neutralize_GivenABareWebAddress_Should_BreakItSoGitHubDoesNotMakeItALink(string input, string expected)
    {
        // Arrange

        // Act
        var result = MarkdownSanitizer.Neutralize(input);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("`https://api.example.com/v1`")]
    [InlineData("a `www.example.com` b")]
    [InlineData("```\nhttps://api.example.com/v1\n```")]
    [InlineData("the www host is down")]
    [InlineData("awww.example is not an address")]
    [InlineData("a::b and c:d and 12:30")]
    [InlineData("x//y")]
    public void Neutralize_GivenAWebAddressInsideCodeOrTextThatOnlyLooksLikeOne_Should_LeaveItUnchanged(string input)
    {
        // Arrange

        // Act
        var result = MarkdownSanitizer.Neutralize(input);

        // Assert
        result.Should().Be(input);
    }

    [Theory]
    [InlineData("fixes #123", "fixes #" + ZeroWidthSpace + "123")]
    [InlineData("see #1, #22 and #333.", "see #" + ZeroWidthSpace + "1, #" + ZeroWidthSpace + "22 and #" + ZeroWidthSpace + "333.")]
    [InlineData("other-org/other-repo#45", "other-org/other-repo#" + ZeroWidthSpace + "45")]
    [InlineData("see GH-7", "see GH-" + ZeroWidthSpace + "7")]
    [InlineData("gh-8", "gh-" + ZeroWidthSpace + "8")]
    [InlineData(@"\#9", "#" + ZeroWidthSpace + "9")]
    [InlineData(@"GH\-10", "GH-" + ZeroWidthSpace + "10")]
    [InlineData(@"see gh\-11 now", "see gh-" + ZeroWidthSpace + "11 now")]
    [InlineData(@"(Gh\-12)", "(Gh-" + ZeroWidthSpace + "12)")]
    public void Neutralize_GivenAnIssueReference_Should_BreakItSoGitHubDoesNotLinkOrCrossReferenceIt(string input, string expected)
    {
        // Arrange

        // Act
        var result = MarkdownSanitizer.Neutralize(input);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("# Heading")]
    [InlineData("C# and F# code")]
    [InlineData("#hashtag")]
    [InlineData("issue #")]
    [InlineData("`#123` in code")]
    [InlineData("a high-8 and a gh8")]
    [InlineData(@"high\-8 and egh\-9")]
    [InlineData(@"GH\- 1 and GH\-x")]
    public void Neutralize_GivenTextThatOnlyLooksLikeAnIssueReference_Should_LeaveItUnchanged(string input)
    {
        // Arrange

        // Act
        var result = MarkdownSanitizer.Neutralize(input);

        // Assert
        result.Should().Be(input);
    }

    [Fact]
    public void Neutralize_GivenWindowsAndOldMacLineEndings_Should_ReadThemAsLineBreaks()
    {
        // Arrange — a bare carriage return must not hide a closing fence from the sanitizer
        const string input = "a\r\nb\rc";

        // Act
        var result = MarkdownSanitizer.Neutralize(input);

        // Assert
        result.Should().Be("a\nb\nc");
    }

    [Fact]
    public void Neutralize_GivenAUnicodeLineSeparator_Should_NotReadItAsALineBreak()
    {
        // Arrange — GitHub does not end a line at U+2028, so a fence line that ends with it is not a closing fence
        const string input = "```\n@a\u2028```\n@octocat";

        // Act
        var result = MarkdownSanitizer.Neutralize(input);

        // Assert
        result.Should().Be(input + "\n```");
    }

    [Fact]
    public void Neutralize_GivenTextLongerThanTheLimit_Should_CutItAndMarkTheCut()
    {
        // Arrange
        var input = new string('a', 50);

        // Act
        var result = MarkdownSanitizer.Neutralize(input, maxChars: 20);

        // Assert
        result.Should().Be(new string('a', 19) + "…");
    }

    [Fact]
    public void Neutralize_GivenACutThatLandsInsideAFence_Should_CloseTheFenceSoTheReviewIsNotSwallowed()
    {
        // Arrange
        var input = "```cs\n" + new string('x', 100) + "\n```";

        // Act
        var result = MarkdownSanitizer.Neutralize(input, maxChars: 30);

        // Assert
        result.Should().EndWith("\n```");
        MarkdownOracle.SwallowsFollowingText(result).Should().BeFalse();
    }

    [Fact]
    public void Neutralize_GivenACutBetweenTheHalvesOfASurrogatePair_Should_NotLeaveALoneSurrogate()
    {
        // Arrange — each emoji is two UTF-16 code units, and a limit of 10 would keep half of the fifth
        var input = string.Concat(Enumerable.Repeat("\U0001F600", 20));

        // Act
        var result = MarkdownSanitizer.Neutralize(input, maxChars: 10);

        // Assert
        result.Should().Be(string.Concat(Enumerable.Repeat("\U0001F600", 4)) + "…");
    }

    [Fact]
    public void NeutralizeInline_GivenTextWithLineBreaks_Should_CollapseThemBeforeSanitizingSoNothingCanStartABlock()
    {
        // Arrange
        const string input = "first\n```\n@octocat\n```";

        // Act
        var result = MarkdownSanitizer.NeutralizeInline(input);

        // Assert
        result.Should().NotContain("\n");
        MarkdownOracle.HasLiveMention(result).Should().BeFalse();
        MarkdownOracle.SwallowsFollowingText(result).Should().BeFalse();
    }

    [Fact]
    public void NeutralizeInline_GivenAMention_Should_NeutralizeIt()
    {
        // Arrange

        // Act
        var result = MarkdownSanitizer.NeutralizeInline("thanks @octocat for `@Override`");

        // Assert
        result.Should().Be("thanks @\u200Boctocat for `@Override`");
    }

    [Fact]
    public void NeutralizeInline_GivenALongTitle_Should_CutItAtTheLimit()
    {
        // Arrange

        // Act
        var result = MarkdownSanitizer.NeutralizeInline(new string('t', 300), maxChars: 200);

        // Assert
        result.Should().HaveLength(200);
        result.Should().EndWith("…");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void NeutralizeInline_GivenNothingToShow_Should_ReturnEmpty(string? input)
    {
        // Arrange

        // Act
        var result = MarkdownSanitizer.NeutralizeInline(input);

        // Assert
        result.Should().BeEmpty();
    }

    [Theory]
    [InlineData("src/Foo.cs", "src/Foo.cs")]
    [InlineData("a`b", "a'b")]
    [InlineData("a\nb", "a b")]
    [InlineData("  padded  ", "padded")]
    [InlineData("", "")]
    public void InlineCode_GivenText_Should_BeSafeInsideACodeSpan(string input, string expected)
    {
        // Arrange

        // Act
        var result = MarkdownSanitizer.InlineCode(input);

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void InlineCode_GivenAnHtmlCommentOpener_Should_BreakItSoNoMarkerCanBeForgedFromAFileName()
    {
        // Arrange
        const string path = "assets/<!-- ai-review:sha=0123456789abcdef0123456789abcdef01234567 -->.png";

        // Act
        var result = MarkdownSanitizer.InlineCode(path);

        // Assert
        result.Should().NotContain("<!--");
        result.Should().Contain("<\u200B!--");
    }

    [Fact]
    public void InlineCode_GivenAVeryLongName_Should_CutItAtTheLimit()
    {
        // Arrange

        // Act
        var result = MarkdownSanitizer.InlineCode(new string('p', 500), maxChars: 120);

        // Assert
        result.Should().HaveLength(120);
        result.Should().EndWith("…");
    }

    [Theory]
    [InlineData("```\ncode", "```\ncode\n```")]
    [InlineData("~~~~\ncode", "~~~~\ncode\n~~~~")]
    [InlineData("  ```cs\ncode", "  ```cs\ncode\n  ```")]
    [InlineData("```\ncode\n```", "```\ncode\n```")]
    [InlineData("no fence here", "no fence here")]
    [InlineData("````\n```\ninner\n```", "````\n```\ninner\n```\n````")]
    [InlineData("    ```\nnot a fence", "    ```\nnot a fence")]
    [InlineData("``` x`y\nnot a fence", "``` x`y\nnot a fence")]
    public void CloseOpenFences_GivenMarkdown_Should_CloseOnlyAFenceThatIsLeftOpen(string input, string expected)
    {
        // Arrange

        // Act
        var result = MarkdownSanitizer.CloseOpenFences(input);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void CloseOpenFences_GivenNullOrEmptyText_Should_ReturnEmpty(string? input)
    {
        // Arrange

        // Act
        var result = MarkdownSanitizer.CloseOpenFences(input);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void FindViolations_GivenFragmentsThatAreNotSanitized_Should_ReportManyBreaches()
    {
        // Arrange — a positive control: the oracle must be able to fail, or the property test above proves nothing
        var fragments = MarkdownFragmentGenerator.Generate(seed: 1, count: 3000);

        // Act
        var violations = MarkdownOracle.FindViolations(fragments, text => text);

        // Assert
        violations.Should().HaveCountGreaterThan(10);
    }
}
