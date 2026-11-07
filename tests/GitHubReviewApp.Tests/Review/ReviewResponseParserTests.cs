namespace GitHubReviewApp.Tests.Review;

public class ReviewResponseParserTests
{
    private const string ValidJson = """
        {
          "summary": "Adds retry handling.",
          "findings": [
            {
              "path": "src/Foo.cs",
              "line": 42,
              "severity": "high",
              "category": "bug",
              "title": "Off-by-one in retry loop",
              "body": "The loop runs one time too many when attempts equals zero."
            }
          ]
        }
        """;

    [Fact]
    public void Parse_GivenValidJson_Should_ReturnSummaryAndFindings()
    {
        // Arrange

        // Act
        var result = ReviewResponseParser.Parse(ValidJson);

        // Assert
        result.IsStructured.Should().BeTrue();
        result.Error.Should().BeNull();
        result.Summary.Should().Be("Adds retry handling.");
        result.Findings.Should().ContainSingle().Which.Should().Be(new ReviewFinding(
            "src/Foo.cs", 42, ReviewSeverity.High, "bug",
            "Off-by-one in retry loop", "The loop runs one time too many when attempts equals zero."));
    }

    [Fact]
    public void Parse_GivenJsonInsideAMarkdownFence_Should_StripTheFenceAndParse()
    {
        // Arrange
        var fenced = "```json\n" + ValidJson + "\n```";

        // Act
        var result = ReviewResponseParser.Parse(fenced);

        // Assert
        result.IsStructured.Should().BeTrue();
        result.Findings.Should().HaveCount(1);
    }

    [Fact]
    public void Parse_GivenFencedJsonWhoseFindingBodyQuotesACodeFence_Should_KeepTheWholeBody()
    {
        // Arrange — a finding that quotes code carries fences of its own, so the fence that closes the reply is the last one and not the first
        const string json = """
            {"summary": "Adds a retry.", "findings": [{"path": "a.cs", "line": 1, "severity": "low", "category": "bug", "title": "Off by one",
              "body": "Use this:\n```csharp\nvar x = 1;\n```\nthen retry."}]}
            """;
        var fenced = "```json\n" + json + "\n```";

        // Act
        var result = ReviewResponseParser.Parse(fenced);

        // Assert
        result.IsStructured.Should().BeTrue();
        result.Findings.Should().ContainSingle().Which.Body.Should().Be("Use this:\n```csharp\nvar x = 1;\n```\nthen retry.");
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public void Parse_GivenJsonInsideALongerFenceThanThreeBackticks_Should_StripTheWholeFenceAndParse(int fenceLength)
    {
        // Arrange — a reply that quotes code fences of its own is wrapped in a longer one, which closes with a run as long as the one that opened it
        var fence = new string('`', fenceLength);
        var fenced = fence + "json\n" + ValidJson + "\n" + fence;

        // Act
        var result = ReviewResponseParser.Parse(fenced);

        // Assert
        result.IsStructured.Should().BeTrue();
        result.Findings.Should().HaveCount(1);
    }

    [Fact]
    public void Parse_GivenALongerFenceFollowedByTextThatQuotesAShorterRun_Should_CloseOnTheRunAsLongAsTheOpeningOne()
    {
        // Arrange — only a run as long as the one that opened the fence closes it, so three backticks in what follows are not taken for the close
        var fenced = "````json\n" + ValidJson + "\n````\nThe fence above is four backticks, not ```.";

        // Act
        var result = ReviewResponseParser.Parse(fenced);

        // Assert
        result.IsStructured.Should().BeTrue();
        result.Findings.Should().HaveCount(1);
    }

    [Fact]
    public void Parse_GivenAFenceThatClosesWithALongerRunThanItOpenedWith_Should_StripTheWholeRun()
    {
        // Arrange
        var fenced = "```json\n" + ValidJson + "\n````";

        // Act
        var result = ReviewResponseParser.Parse(fenced);

        // Assert
        result.IsStructured.Should().BeTrue();
        result.Findings.Should().HaveCount(1);
    }

    [Fact]
    public void Parse_GivenAnOpeningFenceThatIsNeverClosed_Should_StillParseTheJson()
    {
        // Arrange — a reply that stopped before its closing fence
        var unclosed = "```json\n" + ValidJson;

        // Act
        var result = ReviewResponseParser.Parse(unclosed);

        // Assert
        result.IsStructured.Should().BeTrue();
        result.Findings.Should().HaveCount(1);
    }

    [Fact]
    public void Parse_GivenSurroundingWhitespace_Should_StillParse()
    {
        // Arrange

        // Act
        var result = ReviewResponseParser.Parse("\n\n  " + ValidJson + "  \n");

        // Assert
        result.IsStructured.Should().BeTrue();
    }

    [Theory]
    [InlineData("High", ReviewSeverity.High)]
    [InlineData("CRITICAL", ReviewSeverity.Critical)]
    [InlineData("low", ReviewSeverity.Low)]
    [InlineData("medium", ReviewSeverity.Medium)]
    [InlineData("blocker", ReviewSeverity.Medium)]
    [InlineData("", ReviewSeverity.Medium)]
    public void Parse_GivenSeverityText_Should_MapItCaseInsensitivelyAndDefaultToMedium(string severity, ReviewSeverity expected)
    {
        // Arrange
        var json = $$"""{"summary":"s","findings":[{"path":"a.cs","line":1,"severity":"{{severity}}","category":"bug","title":"t","body":"b"}]}""";

        // Act
        var result = ReviewResponseParser.Parse(json);

        // Assert
        result.Findings.Single().Severity.Should().Be(expected);
    }

    [Theory]
    [InlineData("-5", 0)]
    [InlineData("0", 0)]
    [InlineData("\"17\"", 17)]
    [InlineData("12.0", 12)]
    [InlineData("null", 0)]
    [InlineData("\"abc\"", 0)]
    public void Parse_GivenOddLineValues_Should_NormalizeThemToAPositiveLineOrZero(string lineLiteral, int expected)
    {
        // Arrange
        var json = $$"""{"summary":"s","findings":[{"path":"a.cs","line":{{lineLiteral}},"severity":"low","category":"bug","title":"t","body":"b"}]}""";

        // Act
        var result = ReviewResponseParser.Parse(json);

        // Assert
        result.Findings.Single().Line.Should().Be(expected);
    }

    [Fact]
    public void Parse_GivenMissingLineProperty_Should_UseZero()
    {
        // Arrange
        const string Json = """{"summary":"s","findings":[{"path":"a.cs","severity":"low","category":"bug","title":"t","body":"b"}]}""";

        // Act
        var result = ReviewResponseParser.Parse(Json);

        // Assert
        result.Findings.Single().Line.Should().Be(0);
    }

    [Fact]
    public void Parse_GivenMissingOrBlankCategory_Should_UseOther()
    {
        // Arrange
        const string Json = """{"summary":"s","findings":[{"path":"a.cs","line":1,"severity":"low","category":"  ","title":"t","body":"b"}]}""";

        // Act
        var result = ReviewResponseParser.Parse(Json);

        // Assert
        result.Findings.Single().Category.Should().Be("other");
    }

    [Fact]
    public void Parse_GivenFindingWithNeitherTitleNorBody_Should_DropIt()
    {
        // Arrange
        const string Json = """{"summary":"s","findings":[{"path":"a.cs","line":1,"severity":"low","category":"bug","title":" ","body":""},{"path":"b.cs","line":2,"severity":"low","category":"bug","title":"real","body":""}]}""";

        // Act
        var result = ReviewResponseParser.Parse(Json);

        // Assert
        result.Findings.Should().ContainSingle().Which.Path.Should().Be("b.cs");
    }

    [Fact]
    public void Parse_GivenFindingTextWithPaddingWhitespace_Should_TrimIt()
    {
        // Arrange
        const string Json = """{"summary":"  s  ","findings":[{"path":" a.cs ","line":1,"severity":"low","category":" Bug ","title":"  t  ","body":"  b\n"}]}""";

        // Act
        var result = ReviewResponseParser.Parse(Json);

        // Assert
        result.Summary.Should().Be("s");
        result.Findings.Single().Should().Be(new ReviewFinding("a.cs", 1, ReviewSeverity.Low, "bug", "t", "b"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"summary":"Only a summary"}""")]
    [InlineData("""{"summary":"s","findings":null}""")]
    [InlineData("""{"summary":"s","findings":"none"}""")]
    [InlineData("""{"summary":"s","findings":{}}""")]
    public void Parse_GivenJsonWithoutAFindingsList_Should_ReportAnErrorSoItIsNeverMistakenForACleanReview(string json)
    {
        // Arrange

        // Act
        var result = ReviewResponseParser.Parse(json);

        // Assert — "no findings list" is not "no findings": if the schema is not being enforced, this is not a review at all
        result.IsStructured.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
        result.Findings.Should().BeEmpty();
    }

    [Fact]
    public void Parse_GivenAnEmptyFindingsListAndNoSummary_Should_StillBeAReviewWithNoFindings()
    {
        // Arrange

        // Act
        var result = ReviewResponseParser.Parse("""{"findings":[]}""");

        // Assert
        result.IsStructured.Should().BeTrue();
        result.Error.Should().BeNull();
        result.Summary.Should().BeEmpty();
        result.Findings.Should().BeEmpty();
    }

    [Fact]
    public void Parse_GivenPlainProse_Should_ReportAnErrorBecauseItIsNotAStructuredReview()
    {
        // Arrange — "Looks good overall." must not become a completed review that says no issues were found
        const string Prose = "The change looks good overall. One small note about naming.";

        // Act
        var result = ReviewResponseParser.Parse(Prose);

        // Assert
        result.IsStructured.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
        result.Error.Should().NotContain("looks good", "the model's own text stays out of error messages");
        result.Summary.Should().BeEmpty();
        result.Findings.Should().BeEmpty();
    }

    [Theory]
    [InlineData("3", 3)]
    [InlineData("0", 0)]
    [InlineData("-2", 0)]
    [InlineData("\"4\"", 0)]
    [InlineData("null", 0)]
    [InlineData("1.5", 0)]
    [InlineData("1000", 1000)]
    [InlineData("1001", 1000)]
    [InlineData("2147483647", 1000)]
    public void Parse_GivenAnOmittedFindingsCount_Should_ReadItAsANonNegativeWholeNumberNoLargerThanAThousand(string literal, int expected)
    {
        // Arrange
        var json = $$"""{"summary":"s","findings":[],"omitted_findings":{{literal}}}""";

        // Act
        var result = ReviewResponseParser.Parse(json);

        // Assert
        result.OmittedFindingCount.Should().Be(expected);
    }

    [Fact]
    public void Parse_GivenNoOmittedFindingsProperty_Should_ReportNoneOmitted()
    {
        // Arrange

        // Act
        var result = ReviewResponseParser.Parse(ValidJson);

        // Assert
        result.OmittedFindingCount.Should().Be(0);
    }

    [Fact]
    public void Parse_GivenSomeFindingsThatCannotBeRead_Should_KeepTheOthersAndCountTheDroppedOnes()
    {
        // Arrange
        const string Json = """{"summary":"s","findings":["a string","b",{"path":"a.cs","line":1,"severity":"low","category":"bug","title":" ","body":""},{"path":"b.cs","line":2,"severity":"low","category":"bug","title":"real","body":""}]}""";

        // Act
        var result = ReviewResponseParser.Parse(Json);

        // Assert
        result.Error.Should().BeNull();
        result.Findings.Should().ContainSingle().Which.Path.Should().Be("b.cs");
        result.DroppedEntryCount.Should().Be(3);
    }

    [Fact]
    public void Parse_GivenEveryFindingCannotBeRead_Should_ReportAnErrorBecauseTheModelDidReportSomething()
    {
        // Arrange — an empty result here would read as "no issues" although the model listed some
        const string Json = """{"summary":"s","findings":["a string",{"title":"","body":""}]}""";

        // Act
        var result = ReviewResponseParser.Parse(Json);

        // Assert
        result.IsStructured.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Parse_GivenTruncatedJson_Should_ReportAnErrorAndNoSummary()
    {
        // Arrange — what a response cut off by the token limit looks like
        const string Truncated = """{"summary":"s","findings":[{"path":"a.cs","line":1,"seve""";

        // Act
        var result = ReviewResponseParser.Parse(Truncated);

        // Assert
        result.IsStructured.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
        result.Summary.Should().BeEmpty();
        result.Findings.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n")]
    public void Parse_GivenEmptyText_Should_ReportAnError(string? text)
    {
        // Arrange

        // Act
        var result = ReviewResponseParser.Parse(text);

        // Assert
        result.IsStructured.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Parse_GivenJsonArrayInsteadOfObject_Should_ReportAnError()
    {
        // Arrange

        // Act
        var result = ReviewResponseParser.Parse("[1,2,3]");

        // Assert
        result.IsStructured.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
    }
}
