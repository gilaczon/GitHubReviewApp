namespace GitHubReviewApp.Tests.Review;

/// <summary>
/// The comment the app posts on a pull request, and the check run text made from it (design 5.8): the spec's layout line for line, the
/// "Review failed" notice, and the model's and the author's text made inert and free of credentials wherever it is shown.
/// </summary>
public class ChecklistCommentFormatterTests
{
    private const string HeadSha = "0123456789abcdef0123456789abcdef01234567";

    // Text that would mention someone, link out, show an image and turn a code span into math, if GitHub rendered it as written.
    private const string Hostile = "@octocat see https://evil.example <img src=x> $`x`$ #12";

    private const string Summary = "Adds retries to the refund client.";
    private const string TestingNote = "No test for the failure path";
    private const string Issue = "No test covers a failed POST.";
    private const string Fix = "Add a test for the failure path.";
    private const string CoverageNote = "One lockfile was not read.";

    private static readonly ChecklistFinding _doubleRefund = new(
        CheckId.Functionality,
        "src/RefundClient.cs",
        42,
        "The retry resends the POST without an idempotency key.",
        "Send an idempotency key with every attempt.");

    private static readonly ChecklistFinding _untestedFailure = new(CheckId.Testing, "tests/RefundClientTests.cs", 0, Issue, Fix);

    [Fact]
    public void FormatReview_GivenATypicalReview_Should_MatchTheSpecLayoutExactly()
    {
        // Arrange — inputs that sanitising leaves as they are, so the comment is the spec's layout word for word
        var merged = TypicalReview();

        // Act
        var body = Format(Context(), merged, [_doubleRefund, _untestedFailure]);

        // Assert — compared with \n line breaks, which the formatter writes whatever line endings this file was checked out with
        const string Expected = """
            PASS WITH COMMENTS (1 serious) - no security problem, but read the serious comment before approving

            Adds retries to the refund client.

            Reviewed twice; both reviews agreed.

            Checklist (MoneyMe Code Review Checklist)
            - ✅ Coding standards & design
            - ⚠️ Functionality & implementation: SERIOUS: Retries can post the same refund twice
            - ➖ Performance: not applicable
            - ✅ Security
            - ⚠️ Testing: No test for the failure path
            - ✅ Error handling
            - ➖ Scalability: not applicable
            - ✅ Dependencies

            4 pass, 2 concerns, 2 n/a

            To fix, worst first
            - ⚠️ Functionality & implementation `src/RefundClient.cs:42`: The retry resends the POST without an idempotency key. Fix: Send an idempotency key with every attempt.
            - ⚠️ Testing `tests/RefundClientTests.cs`: No test covers a failed POST. Fix: Add a test for the failure path.

            Coverage: One lockfile was not read.

            my-org/my-service #7 · feature/retries → main · by dev1 · 3 files +40 -10 (2 reviewed) · claude-sonnet-5-5 at 0123456789
            <!-- moneyme-ai-review sha=0123456789abcdef0123456789abcdef01234567 outcome=reviewed -->
            """;
        body.Should().Be(Expected.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void FormatReview_GivenACleanPass_Should_LeaveOutToFixAndCoverage()
    {
        // Arrange — every check a pass or n/a, no findings, and every file read in full
        var merged = WithChecks(
            TypicalReview(),
            new RatedCheck(CheckId.Functionality, CheckStatus.Pass, "", false),
            new RatedCheck(CheckId.Testing, CheckStatus.Pass, "", false)) with
        {
            Findings = [],
            Summary = "Adds a health check endpoint.",
            CoverageNote = string.Empty,
            StricterRunVerdict = ReviewVerdict.Pass,
            OtherRunVerdict = ReviewVerdict.Pass
        };
        var context = Context() with { IncludedFileCount = 3, LeftOutFileCount = 0 };

        // Act
        var body = Format(context, merged, []);

        // Assert — the spec keeps a clean PASS to about 15 lines; the blank lines between the parts only separate them
        body.Should().NotContain("To fix").And.NotContain("Coverage:");
        body.Split('\n').Count(line => line.Length > 0).Should().BeLessThanOrEqualTo(15);
    }

    [Fact]
    public void FormatReview_GivenOneRun_Should_SayItWasReviewedOnce()
    {
        // Arrange — the other run ended without a readable answer
        var merged = TypicalReview() with { RunCount = 1, IsUnanimous = false, OtherRunVerdict = null };

        // Act
        var body = Format(Context(), merged, [_doubleRefund, _untestedFailure]);

        // Assert
        body.Split('\n').Should().Contain("Reviewed once: the other review did not finish.");
        body.Should().NotContain("Reviewed twice");
    }

    [Fact]
    public void FormatReview_GivenRunsWithDifferentVerdicts_Should_NameBothStricterFirst()
    {
        // Arrange — one run failed Security and the other rated it a pass
        var merged = WithChecks(TypicalReview(), new RatedCheck(CheckId.Security, CheckStatus.Fail, "Logs the access token", false)) with
        {
            IsUnanimous = false,
            StricterRunVerdict = ReviewVerdict.Fail,
            OtherRunVerdict = ReviewVerdict.PassWithComments
        };

        // Act
        var body = Format(Context(), merged, [_doubleRefund, _untestedFailure]);

        // Assert
        body.Split('\n').Should().Contain("Reviewed twice; the stricter rating of each check is shown. They differed: FAIL and PASS WITH COMMENTS.");
    }

    [Fact]
    public void FormatReview_GivenRunsThatDifferedOnlyOnSomeChecks_Should_NotNameVerdicts()
    {
        // Arrange — the runs rated a check differently, and both still came to PASS WITH COMMENTS
        var merged = TypicalReview() with { IsUnanimous = false };

        // Act
        var body = Format(Context(), merged, [_doubleRefund, _untestedFailure]);

        // Assert
        body.Split('\n').Should().Contain("Reviewed twice; the stricter rating of each check is shown.");
        body.Should().NotContain("They differed");
    }

    [Theory]
    [InlineData("Logs the access token", "- ❌ Security: Logs the access token")]
    [InlineData("", "- ❌ Security")]
    public void FormatReview_GivenASecurityFail_Should_MarkItWithACross(string note, string expectedLine)
    {
        // Arrange — the reader gives a concern without a note a placeholder, but not a fail, so a fail can come without one
        var merged = WithChecks(TypicalReview(), new RatedCheck(CheckId.Security, CheckStatus.Fail, note, false)) with
        {
            StricterRunVerdict = ReviewVerdict.Fail,
            OtherRunVerdict = ReviewVerdict.Fail
        };

        // Act
        var body = Format(Context(), merged, [_doubleRefund, _untestedFailure]);

        // Assert
        body.Split('\n').Should().Contain(expectedLine);
    }

    [Fact]
    public void FormatReview_GivenOneConcernAndOneFail_Should_CountEachInTheSingular()
    {
        // Arrange — Functionality is now a pass, Security a fail and Testing the one concern left
        var merged = WithChecks(
            TypicalReview(),
            new RatedCheck(CheckId.Functionality, CheckStatus.Pass, "", false),
            new RatedCheck(CheckId.Security, CheckStatus.Fail, "Logs the access token", false)) with
        {
            StricterRunVerdict = ReviewVerdict.Fail,
            OtherRunVerdict = ReviewVerdict.Fail
        };

        // Act
        var body = Format(Context(), merged, [_untestedFailure]);

        // Assert
        body.Split('\n').Should().Contain("4 pass, 1 concern, 1 fail, 2 n/a");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void FormatReview_GivenAFindingOnLineZero_Should_ShowTheFileOnly(int line)
    {
        // Arrange — a finding about a file as a whole, such as a test file that is missing a case
        var finding = _untestedFailure with { Line = line };

        // Act
        var body = Format(Context(), TypicalReview(), [finding]);

        // Assert
        body.Should().Contain("Testing `tests/RefundClientTests.cs`: No test covers a failed POST.");
        body.Should().NotContain("RefundClientTests.cs:");
    }

    [Fact]
    public void FormatReview_GivenAModelPathForAChangedFile_Should_ShowTheChangedFilesPath()
    {
        // Arrange — the model wrote the path with a leading "./"
        var finding = new ChecklistFinding(CheckId.Functionality, "./src/Foo.cs", 12, "Retries without a limit.", "Stop after three attempts.");
        var paths = new PathResolver([new PullRequestFile("src/Foo.cs", "modified", 3, 1, "@@ -1 +1,3 @@")]);

        // Act
        var body = Format(Context(), TypicalReview(), [finding], paths);

        // Assert
        body.Should().Contain("Functionality & implementation `src/Foo.cs:12`: Retries without a limit.");
    }

    [Fact]
    public void FormatReview_GivenAFindingWithNoFile_Should_LeaveOutTheLocation()
    {
        // Arrange — a location with no path would leave an empty pair of backticks, which would pair with the double backticks that open the
        // issue's own code span and leave the text inside that span live
        var finding = new ChecklistFinding(CheckId.Testing, "", 0, "No test covers ``@octocat``.", "Add one.");

        // Act
        var body = Format(Context(), TypicalReview(), [finding]);

        // Assert
        body.Should().Contain("Testing: No test covers ``@octocat``. Fix: Add one.");
        MarkdownOracle.HasLiveMention(body).Should().BeFalse();
    }

    [Fact]
    public void FormatReview_GivenAFindingWithNoFix_Should_LeaveOutTheFixLabel()
    {
        // Arrange
        var finding = _untestedFailure with { Fix = string.Empty };

        // Act
        var body = Format(Context(), TypicalReview(), [finding]);

        // Assert
        body.Should().Contain("Testing `tests/RefundClientTests.cs`: No test covers a failed POST.\n");
        body.Should().NotContain("Fix:");
    }

    [Fact]
    public void FormatReview_GivenNoSummary_Should_LeaveOutItsLine()
    {
        // Arrange
        var merged = TypicalReview() with { Summary = " " };

        // Act
        var body = Format(Context(), merged, [_doubleRefund, _untestedFailure]);

        // Assert — the headline is followed straight by the agreement line, with one blank line between them
        body.Should().StartWith("PASS WITH COMMENTS (1 serious) - no security problem, but read the serious comment before approving\n\nReviewed twice;");
    }

    [Fact]
    public void FormatReview_GivenLeftOutFilesAndNoCoverageNote_Should_WriteTheCoverageLineItself()
    {
        // Arrange
        var merged = TypicalReview() with { CoverageNote = string.Empty };
        var context = Context() with { FileCount = 5, LeftOutFileCount = 2 };

        // Act
        var body = Format(context, merged, [_doubleRefund, _untestedFailure]);

        // Assert
        body.Split('\n').Should().Contain("Coverage: 2 of 5 changed files were not reviewed in full.");
    }

    [Theory]
    [InlineData(5, "Coverage: 1 of 5 changed files was not reviewed in full.", "5 files")]
    [InlineData(1, "Coverage: 1 of 1 changed file was not reviewed in full.", "1 file")]
    public void FormatReview_GivenACountOfOneFile_Should_WriteItInTheSingular(int fileCount, string expectedCoverageLine, string expectedFiles)
    {
        // Arrange — one file not reviewed in full, out of five or out of one
        var merged = TypicalReview() with { CoverageNote = string.Empty };
        var context = Context() with { FileCount = fileCount, IncludedFileCount = 1, LeftOutFileCount = 1 };

        // Act
        var body = Format(context, merged, [_doubleRefund, _untestedFailure]);

        // Assert
        body.Split('\n').Should().Contain(expectedCoverageLine);
        body.Should().Contain($" · by dev1 · {expectedFiles} +40 -10 (1 reviewed) · ");
    }

    [Fact]
    public void FormatReview_GivenNoFileLeftOut_Should_LeaveOutTheCoverageLineEvenWithACoverageNote()
    {
        // Arrange — the model wrote a coverage note although every file was read in full
        var context = Context() with { IncludedFileCount = 3, LeftOutFileCount = 0 };

        // Act
        var body = Format(context, TypicalReview(), [_doubleRefund, _untestedFailure]);

        // Assert
        body.Should().NotContain("Coverage:").And.NotContain(CoverageNote);
    }

    [Fact]
    public void FormatReview_GivenTwoAnsweringModels_Should_JoinThemInTheFooter()
    {
        // Arrange — one run was answered by the model the AI service fell back to
        var context = Context() with { Models = ["claude-sonnet-5-5", "claude-sonnet-5"] };

        // Act
        var body = Format(context, TypicalReview(), [_doubleRefund, _untestedFailure]);

        // Assert
        body.Should().Contain("· claude-sonnet-5-5 + claude-sonnet-5 at 0123456789");
    }

    [Theory]
    [InlineData(Hostile, TestingNote, Issue, Fix, CoverageNote)]
    [InlineData(Summary, Hostile, Issue, Fix, CoverageNote)]
    [InlineData(Summary, TestingNote, Hostile, Fix, CoverageNote)]
    [InlineData(Summary, TestingNote, Issue, Hostile, CoverageNote)]
    [InlineData(Summary, TestingNote, Issue, Fix, Hostile)]
    public void FormatReview_GivenHostileModelText_Should_RenderNothingLive(string summary, string note, string issue, string fix, string coverageNote)
    {
        // Arrange — the summary, the Testing check's note, the finding's issue and fix, and the coverage note in turn
        var merged = WithChecks(TypicalReview(), new RatedCheck(CheckId.Testing, CheckStatus.Concern, note, false)) with
        {
            Summary = summary,
            CoverageNote = coverageNote
        };
        var finding = _untestedFailure with { Issue = issue, Fix = fix };

        // Act
        var body = Format(Context(), merged, [finding]);
        var checkRunSummary = ChecklistCommentFormatter.CheckRunSummary(body);

        // Assert — the text is still shown, and none of it is live in the comment or in the check run
        MarkdownOracle.VisibleText(body).Should().Contain("octocat see https");
        MarkdownOracle.HasLiveContent(WithoutOwnReference(body)).Should().BeFalse();
        MarkdownOracle.HasLiveMention(body).Should().BeFalse();
        MarkdownOracle.HasLiveContent(WithoutOwnReference(checkRunSummary)).Should().BeFalse();
        MarkdownOracle.HasLiveMention(checkRunSummary).Should().BeFalse();
    }

    [Fact]
    public void FormatReview_GivenASummaryThatOpensACodeFence_Should_KeepTheRestOfTheCommentOutOfIt()
    {
        // Arrange — the summary has a line of its own, where a run of tildes opens a code block that would run to the end of the comment
        var merged = TypicalReview() with { Summary = "~~~ @octocat" };

        // Act
        var body = Format(Context(), merged, [_doubleRefund, _untestedFailure]);

        // Assert
        MarkdownOracle.VisibleText(body).Should().Contain("Checklist (MoneyMe Code Review Checklist)").And.Contain("by dev1");
    }

    [Fact]
    public void FormatReview_GivenASecretInAFinding_Should_RedactIt()
    {
        // Arrange — the model repeated a key it read in the diff
        var secret = SecretExamples.ByKind.Values.First();
        var finding = _untestedFailure with { Issue = secret.Text };

        // Act
        var body = Format(Context(), TypicalReview(), [finding]);

        // Assert — the brackets of the replacement are escaped in the markdown, so it is looked for in what a reader sees
        body.Should().NotContain(secret.Secret);
        MarkdownOracle.VisibleText(body).Should().Contain(SecretRedactor.Replacement);
    }

    [Fact]
    public void FormatReview_GivenASecretInEveryTextTheAppDidNotWrite_Should_PostItNowhere()
    {
        // Arrange — the same key in the model's summary, note, finding and coverage note, in the author's branches and login, and in the model id
        var secret = SecretExamples.ByKind.Values.First();
        var merged = WithChecks(TypicalReview(), new RatedCheck(CheckId.Testing, CheckStatus.Concern, secret.Text, false)) with
        {
            Summary = secret.Text,
            CoverageNote = secret.Text
        };
        var finding = new ChecklistFinding(CheckId.Testing, secret.Text, 3, secret.Text, secret.Text);
        var context = Context() with { HeadRef = secret.Text, BaseRef = secret.Text, Author = secret.Text, Models = [secret.Text] };

        // Act
        var body = Format(context, merged, [finding]);
        var checkRunSummary = ChecklistCommentFormatter.CheckRunSummary(body);

        // Assert
        body.Should().NotContain(secret.Secret);
        checkRunSummary.Should().NotContain(secret.Secret);
    }

    [Theory]
    [InlineData("fix/@everyone", "main", "dev1", "everyone")]
    [InlineData("feature/retries", Hostile, "dev1", "evil.example")]
    [InlineData("feature/retries", "main", Hostile, "evil.example")]
    public void FormatReview_GivenAHostileBranchNameOrAuthor_Should_RenderNothingLive(string headRef, string baseRef, string author, string visibleText)
    {
        // Arrange — the author names the head branch, and the base branch and the login are text the app did not write either
        var context = Context() with { HeadRef = headRef, BaseRef = baseRef, Author = author };

        // Act
        var body = Format(context, TypicalReview(), [_doubleRefund, _untestedFailure]);
        var checkRunSummary = ChecklistCommentFormatter.CheckRunSummary(body);

        // Assert
        MarkdownOracle.VisibleText(body).Should().Contain(visibleText);
        MarkdownOracle.HasLiveContent(WithoutOwnReference(body)).Should().BeFalse();
        MarkdownOracle.HasLiveMention(body).Should().BeFalse();
        MarkdownOracle.HasLiveContent(WithoutOwnReference(checkRunSummary)).Should().BeFalse();
    }

    [Fact]
    public void FormatReview_GivenAHostileModelId_Should_RenderNothingLive()
    {
        // Arrange — the answering model's id is text the AI service reports, which the app did not write
        var context = Context() with { Models = ["claude-sonnet-5-5", Hostile] };

        // Act
        var body = Format(context, TypicalReview(), [_doubleRefund, _untestedFailure]);

        // Assert
        MarkdownOracle.VisibleText(body).Should().Contain("evil.example");
        MarkdownOracle.HasLiveContent(WithoutOwnReference(body)).Should().BeFalse();
    }

    [Fact]
    public void FormatFailure_GivenAReason_Should_MatchTheFailedLayoutExactly()
    {
        // Arrange — no run answered, so there is no model to name
        var context = Context() with { Models = [] };

        // Act
        var body = ChecklistCommentFormatter.FormatFailure(context, "Both reviews were declined by the AI service's safety checks.");

        // Assert
        const string Expected = """
            Review failed - no verdict

            Both reviews were declined by the AI service's safety checks.

            Push a new commit, or close and re-open this pull request, to try again.

            my-org/my-service #7 · feature/retries → main · by dev1 · 3 files +40 -10 · at 0123456789
            <!-- moneyme-ai-review sha=0123456789abcdef0123456789abcdef01234567 outcome=failed -->
            """;
        body.Should().Be(Expected.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void CheckRunSummary_GivenACommentBody_Should_DropTheMarkerLine()
    {
        // Arrange
        var body = "PASS - no concerns\n\nmy-org/my-service #7 · feature/retries → main\n" + ReviewCommentMarker.For(HeadSha, ReviewRecordOutcome.Reviewed) + "\n";

        // Act
        var summary = ChecklistCommentFormatter.CheckRunSummary(body);

        // Assert
        summary.Should().Be("PASS - no concerns\n\nmy-org/my-service #7 · feature/retries → main");
    }

    [Fact]
    public void CheckRunSummary_GivenABodyWithoutAMarker_Should_KeepEveryLine()
    {
        // Arrange — a last line that is not the marker is text the check run must show
        var body = "PASS - no concerns\n\nmy-org/my-service #7 · feature/retries → main\n";

        // Act
        var summary = ChecklistCommentFormatter.CheckRunSummary(body);

        // Assert
        summary.Should().Be("PASS - no concerns\n\nmy-org/my-service #7 · feature/retries → main");
    }

    private static string Format(ReviewContext context, MergedReview merged, IReadOnlyList<ChecklistFinding> shownFindings, PathResolver? paths = null)
    {
        return ChecklistCommentFormatter.FormatReview(context, merged, ChecklistVerdict.Derive(merged.Checks), shownFindings, paths ?? Paths());
    }

    /// <summary>The pull request of the spec's example: my-org/my-service #7, three changed files, two of them read and a lockfile left out.</summary>
    private static ReviewContext Context()
    {
        return new ReviewContext("my-org", "my-service", 7, "feature/retries", "main", "dev1", HeadSha, 3, 40, 10, 2, 1, ["claude-sonnet-5-5"]);
    }

    /// <summary>The changed files of <see cref="Context"/>.</summary>
    private static PathResolver Paths()
    {
        return new PathResolver(
        [
            new PullRequestFile("src/RefundClient.cs", "modified", 30, 8, "@@ -1,8 +1,30 @@"),
            new PullRequestFile("tests/RefundClientTests.cs", "modified", 10, 2, "@@ -1,2 +1,10 @@"),
            new PullRequestFile("package-lock.json", "modified", 0, 0, null)
        ]);
    }

    /// <summary>The spec's example review: two runs that agreed on PASS WITH COMMENTS with a serious Functionality concern.</summary>
    private static MergedReview TypicalReview()
    {
        RatedCheck[] checks =
        [
            new(CheckId.StandardsDesign, CheckStatus.Pass, "", false),
            new(CheckId.Functionality, CheckStatus.Concern, "Retries can post the same refund twice", true),
            new(CheckId.Performance, CheckStatus.NotApplicable, "", false),
            new(CheckId.Security, CheckStatus.Pass, "", false),
            new(CheckId.Testing, CheckStatus.Concern, TestingNote, false),
            new(CheckId.ErrorHandling, CheckStatus.Pass, "", false),
            new(CheckId.Scalability, CheckStatus.NotApplicable, "", false),
            new(CheckId.Dependencies, CheckStatus.Pass, "", false)
        ];

        return new MergedReview(
            checks,
            [_doubleRefund, _untestedFailure],
            Summary,
            CoverageNote,
            2,
            true,
            ReviewVerdict.PassWithComments,
            ReviewVerdict.PassWithComments);
    }

    /// <summary>The review with the checks given in place of those with the same ids, in checklist order still.</summary>
    private static MergedReview WithChecks(MergedReview merged, params RatedCheck[] replacements)
    {
        return merged with
        {
            Checks = merged.Checks.Select(check => replacements.FirstOrDefault(replacement => replacement.Id == check.Id) ?? check).ToList()
        };
    }

    /// <summary>
    /// The markdown without the footer's "#7": the app's own reference to the pull request the comment is on, which GitHub links as the spec's
    /// layout means it to. The oracle cannot tell it from a reference in text the app did not write, so it is taken out before the oracle looks.
    /// </summary>
    private static string WithoutOwnReference(string markdown)
    {
        return markdown.Replace("my-service #7 · ", "my-service · ", StringComparison.Ordinal);
    }
}
