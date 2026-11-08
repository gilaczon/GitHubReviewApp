namespace GitHubReviewApp.Tests.Review;

/// <summary>
/// Reading one run's answer by the rules of design 5.6.3. The answer is model text, so the tests send what a model could send and not only
/// what the schema asks for: ids that name no check, statuses and flags of the wrong kind, repeated and missing checks, too many findings,
/// and text that is too long or spread over lines.
/// </summary>
public class ChecklistAnswerReaderTests
{
    // A JSON escape is built from this and the rest of the escape, so no tool along the way can turn it into the character it stands for.
    private const string Backslash = "\\";

    /// <summary>Answers in which no entry rates one of the eight checks: an empty list, only ids that name no check, no list, and a list of the wrong kind.</summary>
    public static TheoryData<string> AnswersThatRateNoCheck => new()
    {
        Answer("[]"),
        Answer(Entries(Check("secure"), Check("Security"), """{"id":4,"status":"pass","note":"","serious":false}""")),
        """{"summary":"Adds retries.","findings":[],"coverage_note":""}""",
        Answer("\"x\"")
    };

    [Fact]
    public void Read_GivenAnAnswerRatingAllEightChecks_Should_ReturnEveryRatingTheSummaryAndCoverage()
    {
        // Arrange
        var text = Answer(
            Entries(
                Check("standards-design"),
                Check("functionality", "concern", "Charges the card twice on a retry.", "true"),
                Check("performance", "na"),
                Check("security", "fail", "Logs the access token."),
                Check("testing", "concern", "No test for the retry."),
                Check("error-handling"),
                Check("scalability", "na"),
                Check("dependencies")),
            summary: "Adds retries to the payment client.",
            coverage: "Two pipeline files were left out.");

        // Act
        var result = ChecklistAnswerReader.Read(text);

        // Assert
        result.Error.Should().BeNull();
        result.Answer!.Rated.Should().BeEquivalentTo(Ratings(
            new RatedCheck(CheckId.StandardsDesign, CheckStatus.Pass, "", false),
            new RatedCheck(CheckId.Functionality, CheckStatus.Concern, "Charges the card twice on a retry.", true),
            new RatedCheck(CheckId.Performance, CheckStatus.NotApplicable, "", false),
            new RatedCheck(CheckId.Security, CheckStatus.Fail, "Logs the access token.", false),
            new RatedCheck(CheckId.Testing, CheckStatus.Concern, "No test for the retry.", false),
            new RatedCheck(CheckId.ErrorHandling, CheckStatus.Pass, "", false),
            new RatedCheck(CheckId.Scalability, CheckStatus.NotApplicable, "", false),
            new RatedCheck(CheckId.Dependencies, CheckStatus.Pass, "", false)));
        result.Answer.Summary.Should().Be("Adds retries to the payment client.");
        result.Answer.CoverageNote.Should().Be("Two pipeline files were left out.");
    }

    [Fact]
    public void Read_GivenAnUnknownCheckId_Should_DropThatEntry()
    {
        // Arrange — the security entry names "secure", which is no check
        var text = Answer(AllEight(CheckId.Security, Check("secure")));

        // Act
        var result = ChecklistAnswerReader.Read(text);

        // Assert
        result.Answer!.Rated.Keys.Should().BeEquivalentTo(Checklist.Order.Where(id => id != CheckId.Security));
    }

    [Fact]
    public void Read_GivenTwoEntriesForTheSameCheck_Should_KeepTheFirst()
    {
        // Arrange
        var text = Answer(Entries(Check("testing", "concern", "No test for the retry."), Check("testing")));

        // Act
        var result = ChecklistAnswerReader.Read(text);

        // Assert
        result.Answer!.Rated.Should().BeEquivalentTo(Ratings(new RatedCheck(CheckId.Testing, CheckStatus.Concern, "No test for the retry.", false)));
    }

    [Theory]
    [InlineData("\"PASS\"")]
    [InlineData("\"ok\"")]
    [InlineData("\"\"")]
    [InlineData("\"na \"")]
    [InlineData("1")]
    public void Read_GivenAStatusOutsideTheFour_Should_ReadItAsConcern(string statusJson)
    {
        // Arrange — the status is raw JSON, so the last case sends a number
        var text = Answer($$"""[{"id":"testing","status":{{statusJson}},"note":"","serious":false}]""");

        // Act
        var result = ChecklistAnswerReader.Read(text);

        // Assert — never a pass: a status the reader cannot be sure of is something to look at
        result.Answer!.Rated[CheckId.Testing].Status.Should().Be(CheckStatus.Concern);
    }

    [Theory]
    [InlineData("standards-design", CheckId.StandardsDesign)]
    [InlineData("functionality", CheckId.Functionality)]
    [InlineData("performance", CheckId.Performance)]
    [InlineData("testing", CheckId.Testing)]
    [InlineData("error-handling", CheckId.ErrorHandling)]
    [InlineData("scalability", CheckId.Scalability)]
    [InlineData("dependencies", CheckId.Dependencies)]
    public void Read_GivenFailOnACheckOtherThanSecurity_Should_ReadItAsConcernAndKeepTheNote(string wireId, CheckId id)
    {
        // Arrange
        var text = Answer(AllEight(id, Check(wireId, "fail", "Charges the card twice on a retry.")));

        // Act
        var result = ChecklistAnswerReader.Read(text);

        // Assert — only Security can fail a review
        result.Answer!.Rated[id].Should().Be(new RatedCheck(id, CheckStatus.Concern, "Charges the card twice on a retry.", false));
    }

    [Fact]
    public void Read_GivenFailOnSecurity_Should_KeepFail()
    {
        // Arrange
        var text = Answer(AllEight(CheckId.Security, Check("security", "fail", "Logs the access token.")));

        // Act
        var result = ChecklistAnswerReader.Read(text);

        // Assert
        result.Answer!.Rated[CheckId.Security].Should().Be(new RatedCheck(CheckId.Security, CheckStatus.Fail, "Logs the access token.", false));
    }

    [Theory]
    [InlineData("pass", CheckStatus.Pass)]
    [InlineData("na", CheckStatus.NotApplicable)]
    public void Read_GivenANoteOnPassOrNa_Should_DropIt(string status, CheckStatus expectedStatus)
    {
        // Arrange
        var text = Answer(Entries(Check("performance", status, "Looks fine.")));

        // Act
        var result = ChecklistAnswerReader.Read(text);

        // Assert
        result.Answer!.Rated[CheckId.Performance].Should().Be(new RatedCheck(CheckId.Performance, expectedStatus, "", false));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \r\n\t ")]
    public void Read_GivenAConcernWithoutANote_Should_UseTheNotAssessedNote(string note)
    {
        // Arrange — a note of nothing but whitespace is no note
        var text = Answer(Entries(Check("scalability", "concern", note)));

        // Act
        var result = ChecklistAnswerReader.Read(text);

        // Assert
        result.Answer!.Rated[CheckId.Scalability].Note.Should().Be(Checklist.NotAssessedNote);
    }

    [Fact]
    public void Read_GivenALongMultiLineNote_Should_KeepOneLineOfAtMost100Characters()
    {
        // Arrange — four lines of the same 35-character sentence, the first with a tab and a double space in it
        var note = "Retries  the charge\tafter a timeout.\r\nRetries the charge after a timeout.\nRetries the charge after a timeout.\r\nRetries the charge after a timeout.";
        var text = Answer(Entries(Check("functionality", "concern", note)));

        // Act
        var kept = ChecklistAnswerReader.Read(text).Answer!.Rated[CheckId.Functionality].Note;

        // Assert
        kept.Should().NotContainAny("\n", "\r")
            .And.HaveLength(100)
            .And.StartWith("Retries the charge after a timeout. Retries the charge after a timeout.")
            .And.EndWith("…");
    }

    [Fact]
    public void Read_GivenSeriousTrueOnAConcern_Should_MarkItSerious()
    {
        // Arrange
        var text = Answer(Entries(Check("functionality", "concern", "Charges the card twice on a retry.", "true")));

        // Act
        var result = ChecklistAnswerReader.Read(text);

        // Assert
        result.Answer!.Rated[CheckId.Functionality].Should().Be(new RatedCheck(CheckId.Functionality, CheckStatus.Concern, "Charges the card twice on a retry.", true));
    }

    [Theory]
    [InlineData("\"true\"")]
    [InlineData("1")]
    [InlineData("null")]
    [InlineData("false")]
    public void Read_GivenSeriousThatIsNotTheBooleanTrue_Should_NotMarkItSerious(string serious)
    {
        // Arrange — the flag is raw JSON: a string, a number, a null and the boolean false
        var text = Answer(Entries(Check("functionality", "concern", "Charges the card twice on a retry.", serious)));

        // Act
        var result = ChecklistAnswerReader.Read(text);

        // Assert
        result.Answer!.Rated[CheckId.Functionality].Should().Be(new RatedCheck(CheckId.Functionality, CheckStatus.Concern, "Charges the card twice on a retry.", false));
    }

    [Theory]
    [InlineData("functionality", "pass")]
    [InlineData("functionality", "na")]
    [InlineData("security", "fail")]
    public void Read_GivenSeriousTrueOnPassNaOrSecurityFail_Should_NotMarkItSerious(string wireId, string status)
    {
        // Arrange
        var text = Answer(Entries(Check(wireId, status, "Charges the card twice on a retry.", "true")));

        // Act
        var result = ChecklistAnswerReader.Read(text);

        // Assert — serious names a concern; a security problem is a fail, not a serious comment
        result.Answer!.Rated.Values.Should().ContainSingle().Which.IsSerious.Should().BeFalse();
    }

    [Fact]
    public void Read_GivenSeriousTrueOnANonSecurityFail_Should_MarkItSeriousBecauseItBecameAConcern()
    {
        // Arrange
        var text = Answer(Entries(Check("functionality", "fail", "Charges the card twice on a retry.", "true")));

        // Act
        var result = ChecklistAnswerReader.Read(text);

        // Assert
        result.Answer!.Rated[CheckId.Functionality].Should().Be(new RatedCheck(CheckId.Functionality, CheckStatus.Concern, "Charges the card twice on a retry.", true));
    }

    [Fact]
    public void Read_GivenAMissingCheck_Should_LeaveItOutOfTheRatings()
    {
        // Arrange — no entry for security at all
        var text = Answer(AllEight(CheckId.Security, replacement: string.Empty));

        // Act
        var result = ChecklistAnswerReader.Read(text);

        // Assert — left out, so the merge treats it as not assessed and never as a pass
        result.Answer!.Rated.Should().NotContainKey(CheckId.Security).And.HaveCount(7);
    }

    [Theory]
    [MemberData(nameof(AnswersThatRateNoCheck))]
    public void Read_GivenNoRatedCheck_Should_FailAsInvalidOutput(string text)
    {
        // Arrange — the answer comes from the test data

        // Act
        var result = ChecklistAnswerReader.Read(text);

        // Assert — an answer that assessed nothing must not reach the merge, where every check would read as not assessed
        result.Answer.Should().BeNull();
        result.Error.Should().Be("The answer rated none of the eight checks.");
    }

    [Theory]
    [InlineData(null, "The answer was empty.")]
    [InlineData("", "The answer was empty.")]
    [InlineData("not json", "The answer was not valid JSON.")]
    [InlineData("[]", "The answer was not a JSON object.")]
    [InlineData("null", "The answer was not a JSON object.")]
    public void Read_GivenTextThatIsNotAJsonObject_Should_Fail(string? text, string expectedError)
    {
        // Arrange — the text comes from the test data

        // Act
        var result = ChecklistAnswerReader.Read(text);

        // Assert — the error is fixed text and quotes none of the answer
        result.Answer.Should().BeNull();
        result.Error.Should().Be(expectedError);
    }

    [Fact]
    public void Read_GivenHalfASurrogatePairInTheText_Should_FailInsteadOfThrowing()
    {
        // Arrange — a lone high surrogate is not valid UTF-16, so the text cannot be parsed at all
        var text = $$"""{"summary":"Adds retries{{(char)0xD800}}","checks":{{AllEight()}},"findings":[],"coverage_note":""}""";

        // Act
        var result = ChecklistAnswerReader.Read(text);

        // Assert
        result.Answer.Should().BeNull();
        result.Error.Should().Be("The answer held text that is not valid Unicode.");
    }

    [Fact]
    public void Read_GivenHalfASurrogatePairWrittenAsAJsonEscape_Should_ReadThatValueAsEmptyText()
    {
        // Arrange — the escape is valid JSON, but the summary it is in stands for no valid UTF-16 text
        var text = $$"""{"summary":"Adds retries{{Backslash}}ud800","checks":{{AllEight()}},"findings":[],"coverage_note":""}""";

        // Act
        var result = ChecklistAnswerReader.Read(text);

        // Assert — like a value of the wrong kind, and the rest of the answer is still read
        result.Error.Should().BeNull();
        result.Answer!.Summary.Should().BeEmpty();
        result.Answer.Rated.Should().HaveCount(8);
    }

    [Fact]
    public void Read_GivenAnOverlongSummaryAndCoverageNote_Should_CutThemTo240And160Characters()
    {
        // Arrange — one character over each limit
        var text = Answer(AllEight(), summary: new string('s', 241), coverage: new string('c', 161));

        // Act
        var answer = ChecklistAnswerReader.Read(text).Answer!;

        // Assert — the last character that fits gives way to the ellipsis
        answer.Summary.Should().Be(new string('s', 239) + "…");
        answer.CoverageNote.Should().Be(new string('c', 159) + "…");
    }

    [Fact]
    public void Read_GivenTextExactlyAtItsLimit_Should_KeepItWhole()
    {
        // Arrange — every text exactly as long as its limit allows
        var text = Answer(
            Entries(Check("testing", "concern", new string('n', 100))),
            Entries(Finding(check: "testing", file: new string('f', 250), issue: new string('i', 160), fix: new string('x', 120))),
            summary: new string('s', 240),
            coverage: new string('c', 160));

        // Act
        var answer = ChecklistAnswerReader.Read(text).Answer!;

        // Assert — nothing is cut and nothing gains an ellipsis
        answer.Rated[CheckId.Testing].Note.Should().Be(new string('n', 100));
        answer.Findings.Should().Equal(new ChecklistFinding(CheckId.Testing, new string('f', 250), 11, new string('i', 160), new string('x', 120)));
        answer.Summary.Should().Be(new string('s', 240));
        answer.CoverageNote.Should().Be(new string('c', 160));
    }

    [Fact]
    public void Read_GivenACutThatWouldSplitASurrogatePair_Should_CutBeforeThePair()
    {
        // Arrange — an emoji is two UTF-16 characters; here they are the 99th and 100th, so the cut to 99 falls between them
        var note = new string('a', 98) + char.ConvertFromUtf32(0x1F600) + " and more after it.";
        var text = Answer(Entries(Check("functionality", "concern", note)));

        // Act
        var kept = ChecklistAnswerReader.Read(text).Answer!.Rated[CheckId.Functionality].Note;

        // Assert — the emoji goes whole, so no half of it is left before the ellipsis
        kept.Should().Be(new string('a', 98) + "…");
    }

    [Fact]
    public void Read_GivenTextSpreadOverLinesWithRunsOfWhitespace_Should_MakeEveryFieldOneLineWithSingleSpaces()
    {
        // Arrange
        var text = Answer(
            Entries(Check("functionality", "concern", "Retries\r\n  the charge twice.")),
            Entries(Finding(file: "\tsrc/Payments/Client.cs\r\n", issue: "Retries the charge\r\ntwice.", fix: "Retry  once.")),
            summary: "  Adds retries\r\nto the payment client.  ",
            coverage: "Two pipeline\tfiles  were left out.\n");

        // Act
        var answer = ChecklistAnswerReader.Read(text).Answer!;

        // Assert
        answer.Rated[CheckId.Functionality].Note.Should().Be("Retries the charge twice.");
        answer.Findings.Should().Equal(new ChecklistFinding(CheckId.Functionality, "src/Payments/Client.cs", 11, "Retries the charge twice.", "Retry once."));
        answer.Summary.Should().Be("Adds retries to the payment client.");
        answer.CoverageNote.Should().Be("Two pipeline files were left out.");
    }

    [Fact]
    public void Read_GivenTextFieldsOfTheWrongKind_Should_ReadThemAsEmptyText()
    {
        // Arrange — a number, an array, a null, an object and a boolean where text belongs
        var text = """{"summary":42,"checks":[{"id":"testing","status":"concern","note":["No test."],"serious":false}],"findings":[{"check":"testing","file":7,"line":3,"issue":null,"fix":{}}],"coverage_note":true}""";

        // Act
        var answer = ChecklistAnswerReader.Read(text).Answer!;

        // Assert — the concern, left with no note, takes the not-assessed one
        answer.Summary.Should().BeEmpty();
        answer.CoverageNote.Should().BeEmpty();
        answer.Rated[CheckId.Testing].Note.Should().Be(Checklist.NotAssessedNote);
        answer.Findings.Should().Equal(new ChecklistFinding(CheckId.Testing, "", 3, "", ""));
    }

    [Fact]
    public void Read_GivenEntriesThatAreNotObjects_Should_DropThemAndReadTheRest()
    {
        // Arrange — a string, a null, a number and an array where entries belong, next to one real entry of each kind
        var text = Answer(
            Entries("\"security\"", "null", "7", Check("testing", "concern", "No test for the retry.")),
            Entries("\"src/Foo.cs\"", "[]", Finding()));

        // Act
        var answer = ChecklistAnswerReader.Read(text).Answer!;

        // Assert
        answer.Rated.Should().BeEquivalentTo(Ratings(new RatedCheck(CheckId.Testing, CheckStatus.Concern, "No test for the retry.", false)));
        answer.Findings.Should().Equal(new ChecklistFinding(CheckId.Functionality, "src/Foo.cs", 11, "Retries twice.", "Retry once."));
    }

    [Fact]
    public void Read_GivenNineValidFindings_Should_KeepTheFirstEight()
    {
        // Arrange — the findings differ only in their line, 1 to 9
        var findings = Enumerable.Range(1, 9).Select(line => Finding(line: line.ToString(CultureInfo.InvariantCulture))).ToArray();
        var text = Answer(AllEight(), Entries(findings));

        // Act
        var result = ChecklistAnswerReader.Read(text);

        // Assert
        result.Answer!.Findings.Should().Equal(
            Enumerable.Range(1, 8).Select(line => new ChecklistFinding(CheckId.Functionality, "src/Foo.cs", line, "Retries twice.", "Retry once.")));
    }

    [Fact]
    public void Read_GivenAFindingWithOverlongFields_Should_CutFileIssueAndFixToTheirLimits()
    {
        // Arrange — one character over each limit
        var text = Answer(AllEight(), Entries(Finding(file: new string('f', 251), issue: new string('i', 161), fix: new string('x', 121))));

        // Act
        var result = ChecklistAnswerReader.Read(text);

        // Assert
        result.Answer!.Findings.Should().Equal(
            new ChecklistFinding(CheckId.Functionality, new string('f', 249) + "…", 11, new string('i', 159) + "…", new string('x', 119) + "…"));
    }

    [Fact]
    public void Read_GivenAFindingForAnUnknownCheck_Should_DropIt()
    {
        // Arrange — the finding for "secure" comes first, followed by as many real findings as are kept
        var findings = Enumerable.Range(1, 8)
            .Select(line => Finding(check: "security", line: line.ToString(CultureInfo.InvariantCulture)))
            .Prepend(Finding(check: "secure"))
            .ToArray();
        var text = Answer(AllEight(), Entries(findings));

        // Act
        var result = ChecklistAnswerReader.Read(text);

        // Assert — dropped before the limit applies, so it takes no place from the eight after it
        result.Answer!.Findings.Select(finding => finding.Line).Should().Equal(1, 2, 3, 4, 5, 6, 7, 8);
        result.Answer.Findings.Should().OnlyContain(finding => finding.Check == CheckId.Security);
    }

    [Theory]
    [InlineData("\"42\"")]
    [InlineData("4.5")]
    [InlineData("null")]
    public void Read_GivenAFindingWhoseLineIsNotAnInteger_Should_KeepItWithLineZero(string lineJson)
    {
        // Arrange — the line is raw JSON: a string, a fraction and a null
        var text = Answer(AllEight(), Entries(Finding(line: lineJson)));

        // Act
        var result = ChecklistAnswerReader.Read(text);

        // Assert — 0 is no line, so the comment shows the file alone
        result.Answer!.Findings.Should().Equal(new ChecklistFinding(CheckId.Functionality, "src/Foo.cs", 0, "Retries twice.", "Retry once."));
    }

    [Fact]
    public void Read_GivenAVerdictFieldTheModelAddedAnyway_Should_IgnoreIt()
    {
        // Arrange — the code derives the verdict, so a verdict in the answer is only a property the schema does not have
        var answer = JsonNode.Parse(Answer(AllEight()))!.AsObject();
        answer["verdict"] = "FAIL";
        var text = answer.ToJsonString();

        // Act
        var result = ChecklistAnswerReader.Read(text);

        // Assert
        result.Error.Should().BeNull();
        result.Answer!.Rated.Should().HaveCount(8);
        result.Answer.Rated.Values.Should().OnlyContain(check => check.Status == CheckStatus.Pass);
        result.Answer.Summary.Should().Be("Adds retries.");
    }

    /// <summary>A whole answer as JSON text. The checks and findings are JSON arrays; the summary and coverage note are plain text.</summary>
    private static string Answer(string checksJson, string findingsJson = "[]", string summary = "Adds retries.", string coverage = "")
    {
        return $$"""{"summary":{{JsonSerializer.Serialize(summary)}},"checks":{{checksJson}},"findings":{{findingsJson}},"coverage_note":{{JsonSerializer.Serialize(coverage)}}}""";
    }

    /// <summary>One check entry as JSON text. <paramref name="serious"/> is raw JSON, so a test can send a flag that is not a boolean.</summary>
    private static string Check(string id, string status = "pass", string note = "", string serious = "false")
    {
        return $$"""{"id":{{JsonSerializer.Serialize(id)}},"status":{{JsonSerializer.Serialize(status)}},"note":{{JsonSerializer.Serialize(note)}},"serious":{{serious}}}""";
    }

    /// <summary>
    /// The eight checks as a JSON array in checklist order, each rated pass, except that the entry for <paramref name="replaced"/> is
    /// <paramref name="replacement"/>, or is left out when that is empty.
    /// </summary>
    private static string AllEight(CheckId? replaced = null, string replacement = "")
    {
        var entries = Checklist.Order
            .Select(id => id == replaced ? replacement : Check(Checklist.WireId(id)))
            .Where(entry => entry.Length > 0)
            .ToArray();

        return Entries(entries);
    }

    /// <summary>One finding as JSON text. <paramref name="line"/> is raw JSON, so a test can send a line that is not an integer.</summary>
    private static string Finding(string check = "functionality", string file = "src/Foo.cs", string line = "11", string issue = "Retries twice.", string fix = "Retry once.")
    {
        return $$"""{"check":{{JsonSerializer.Serialize(check)}},"file":{{JsonSerializer.Serialize(file)}},"line":{{line}},"issue":{{JsonSerializer.Serialize(issue)}},"fix":{{JsonSerializer.Serialize(fix)}}}""";
    }

    /// <summary>A JSON array of entries that are each JSON text already.</summary>
    private static string Entries(params string[] entries)
    {
        return $"[{string.Join(",", entries)}]";
    }

    private static Dictionary<CheckId, RatedCheck> Ratings(params RatedCheck[] checks)
    {
        return checks.ToDictionary(check => check.Id);
    }
}
