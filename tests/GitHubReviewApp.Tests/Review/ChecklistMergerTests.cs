namespace GitHubReviewApp.Tests.Review;

/// <summary>
/// Merging the runs of one review (design 5.7): each check takes the stricter rating, with the note of a serious run first; a check no run
/// rated is not assessed; findings appear once each, the stricter run's first; the summary and coverage note are the stricter run's; and the
/// runs agreed only when both rated every check with the same status.
/// </summary>
public class ChecklistMergerTests
{
    [Theory]
    [InlineData(CheckId.Testing, CheckStatus.Pass, CheckStatus.Concern, CheckStatus.Concern)]
    [InlineData(CheckId.Performance, CheckStatus.NotApplicable, CheckStatus.Pass, CheckStatus.Pass)]
    [InlineData(CheckId.Security, CheckStatus.Concern, CheckStatus.Fail, CheckStatus.Fail)]
    [InlineData(CheckId.Security, CheckStatus.Fail, CheckStatus.NotApplicable, CheckStatus.Fail)]
    public void Merge_GivenTwoRunsThatDiffer_Should_KeepTheStricterStatus(CheckId id, CheckStatus runAStatus, CheckStatus runBStatus, CheckStatus expectedStatus)
    {
        // Arrange
        var runA = Run((id, runAStatus, "", false));
        var runB = Run((id, runBStatus, "", false));

        // Act
        var merged = ChecklistMerger.Merge([runA, runB]);

        // Assert
        merged.Checks.Single(check => check.Id == id).Status.Should().Be(expectedStatus);
    }

    [Fact]
    public void Merge_GivenBothRunsHoldTheWinningStatus_Should_TakeTheNoteFromTheSeriousOne()
    {
        // Arrange
        var runA = Run((CheckId.Functionality, CheckStatus.Concern, "Note from run A.", false));
        var runB = Run((CheckId.Functionality, CheckStatus.Concern, "Note from run B.", true));

        // Act
        var merged = ChecklistMerger.Merge([runA, runB]);

        // Assert
        merged.Checks.Single(check => check.Id == CheckId.Functionality)
            .Should().Be(new RatedCheck(CheckId.Functionality, CheckStatus.Concern, "Note from run B.", true));
    }

    [Fact]
    public void Merge_GivenBothRunsHoldTheWinningStatusAndNeitherIsSerious_Should_TakeRunAsNote()
    {
        // Arrange — run B fails Security, so it is the stricter run: the note follows the run order, not the strictness
        var runA = Run((CheckId.Testing, CheckStatus.Concern, "Note from run A.", false));
        var runB = Run(
            (CheckId.Testing, CheckStatus.Concern, "Note from run B.", false),
            (CheckId.Security, CheckStatus.Fail, "Logs the access token.", false));

        // Act
        var merged = ChecklistMerger.Merge([runA, runB]);

        // Assert
        merged.Checks.Single(check => check.Id == CheckId.Testing)
            .Should().Be(new RatedCheck(CheckId.Testing, CheckStatus.Concern, "Note from run A.", false));
    }

    [Fact]
    public void Merge_GivenRunAFailsSecurityWithoutANote_Should_TakeRunBsNote()
    {
        // Arrange — both runs fail Security, and only run B says why
        var runA = Run((CheckId.Security, CheckStatus.Fail, "", false));
        var runB = Run((CheckId.Security, CheckStatus.Fail, "Logs the access token.", false));

        // Act
        var merged = ChecklistMerger.Merge([runA, runB]);

        // Assert
        merged.Checks.Single(check => check.Id == CheckId.Security)
            .Should().Be(new RatedCheck(CheckId.Security, CheckStatus.Fail, "Logs the access token.", false));
    }

    [Fact]
    public void Merge_GivenSecurityFailInOneRunAndASeriousConcernInTheOther_Should_BeFailAndNotSerious()
    {
        // Arrange
        var runA = Run((CheckId.Security, CheckStatus.Concern, "Note from run A.", true));
        var runB = Run((CheckId.Security, CheckStatus.Fail, "Note from run B.", false));

        // Act
        var merged = ChecklistMerger.Merge([runA, runB]);

        // Assert — the fail wins with its own note; run A's serious flag went with the concern that lost
        merged.Checks.Single(check => check.Id == CheckId.Security)
            .Should().Be(new RatedCheck(CheckId.Security, CheckStatus.Fail, "Note from run B.", false));
    }

    [Fact]
    public void Merge_GivenACheckOnlyOneRunRated_Should_TakeThatRunsRating()
    {
        // Arrange — run A rated Security a pass and run B left it out
        var runA = Run();
        var runB = LeaveOut(Run(), CheckId.Security);

        // Act
        var merged = ChecklistMerger.Merge([runA, runB]);

        // Assert — a check one run assessed is not made a concern because the other run left it out
        merged.Checks.Single(check => check.Id == CheckId.Security)
            .Should().Be(new RatedCheck(CheckId.Security, CheckStatus.Pass, "", false));
    }

    [Fact]
    public void Merge_GivenACheckNeitherRunRated_Should_BeAConcernThatIsNotAssessed()
    {
        // Arrange — both runs left Security out
        var runA = LeaveOut(Run(), CheckId.Security);
        var runB = LeaveOut(Run(), CheckId.Security);

        // Act
        var merged = ChecklistMerger.Merge([runA, runB]);

        // Assert — never a pass
        merged.Checks.Single(check => check.Id == CheckId.Security).Should().Be(NotAssessed(CheckId.Security));
    }

    [Fact]
    public void Merge_GivenOneRun_Should_FillItsMissingChecksAsNotAssessedConcerns()
    {
        // Arrange — the only run that finished rated six checks a pass and left Security and Testing out
        var run = LeaveOut(Run(), CheckId.Security, CheckId.Testing);

        // Act
        var merged = ChecklistMerger.Merge([run]);

        // Assert — the two it left out are concerns, so the review is not a pass
        merged.Checks.Should().Equal(
            new RatedCheck(CheckId.StandardsDesign, CheckStatus.Pass, "", false),
            new RatedCheck(CheckId.Functionality, CheckStatus.Pass, "", false),
            new RatedCheck(CheckId.Performance, CheckStatus.Pass, "", false),
            NotAssessed(CheckId.Security),
            NotAssessed(CheckId.Testing),
            new RatedCheck(CheckId.ErrorHandling, CheckStatus.Pass, "", false),
            new RatedCheck(CheckId.Scalability, CheckStatus.Pass, "", false),
            new RatedCheck(CheckId.Dependencies, CheckStatus.Pass, "", false));
        merged.RunCount.Should().Be(1);
        merged.IsUnanimous.Should().BeFalse();
        merged.StricterRunVerdict.Should().Be(ReviewVerdict.PassWithComments);
        merged.OtherRunVerdict.Should().BeNull();
    }

    [Fact]
    public void Merge_GivenTheSameFindingInBothRuns_Should_KeepItOnce()
    {
        // Arrange — the same check and line; the path is written with a backslash in one run, and with "./" and in lower case in the other
        var findingA = new ChecklistFinding(CheckId.Functionality, @"src\Foo.cs", 12, "Retries the refund twice.", "Retry it once.");
        var findingB = new ChecklistFinding(CheckId.Functionality, "./src/foo.cs", 12, "The refund is retried twice.", "Retry the refund once.");
        var runA = Run() with { Findings = [findingA] };
        var runB = Run() with { Findings = [findingB] };

        // Act
        var merged = ChecklistMerger.Merge([runA, runB]);

        // Assert — the stricter run's copy is kept, which on a tie is run A's
        merged.Findings.Should().Equal(findingA);
    }

    [Theory]
    [InlineData(CheckId.Testing, "src/Foo.cs", 12)]
    [InlineData(CheckId.Functionality, "src/Bar.cs", 12)]
    [InlineData(CheckId.Functionality, "src/Foo.cs", 13)]
    public void Merge_GivenFindingsThatDifferInCheckFileOrLine_Should_KeepBoth(CheckId check, string file, int line)
    {
        // Arrange — run B's finding differs from run A's in one of the three
        var findingA = new ChecklistFinding(CheckId.Functionality, "src/Foo.cs", 12, "Retries the refund twice.", "Retry it once.");
        var findingB = new ChecklistFinding(check, file, line, "Retries the refund twice.", "Retry it once.");
        var runA = Run() with { Findings = [findingA] };
        var runB = Run() with { Findings = [findingB] };

        // Act
        var merged = ChecklistMerger.Merge([runA, runB]);

        // Assert
        merged.Findings.Should().Equal(findingA, findingB);
    }

    [Fact]
    public void Merge_GivenFindings_Should_ListTheStricterRunsFindingsFirst()
    {
        // Arrange — run B fails Security, so it is the stricter run; each run lists its findings against line order
        var untestedFailure = new ChecklistFinding(CheckId.Testing, "tests/RefundClientTests.cs", 40, "No test for a failed refund.", "Add one.");
        var untestedRetry = new ChecklistFinding(CheckId.Testing, "tests/RefundClientTests.cs", 8, "No test for the retry.", "Add one.");
        var loggedToken = new ChecklistFinding(CheckId.Security, "src/RefundClient.cs", 30, "Logs the access token.", "Log the token's id instead.");
        var doubleRefund = new ChecklistFinding(CheckId.Functionality, "src/RefundClient.cs", 12, "The retry posts the refund twice.", "Send an idempotency key.");
        var runA = Run((CheckId.Testing, CheckStatus.Concern, "No test for the failure path.", false)) with { Findings = [untestedFailure, untestedRetry] };
        var runB = Run((CheckId.Security, CheckStatus.Fail, "Logs the access token.", false)) with { Findings = [loggedToken, doubleRefund] };

        // Act
        var merged = ChecklistMerger.Merge([runA, runB]);

        // Assert — each run's own order is kept
        merged.Findings.Should().Equal(loggedToken, doubleRefund, untestedFailure, untestedRetry);
    }

    [Fact]
    public void Merge_GivenRunsWithDifferentOwnVerdicts_Should_TakeSummaryAndCoverageFromTheStricterRun()
    {
        // Arrange — run A's own result is PASS WITH COMMENTS and run B's is FAIL
        var runA = Run((CheckId.Testing, CheckStatus.Concern, "No test for the failure path.", false))
            with { Summary = "Summary from run A.", CoverageNote = "Coverage note from run A." };
        var runB = Run((CheckId.Security, CheckStatus.Fail, "Logs the access token.", false))
            with { Summary = "Summary from run B.", CoverageNote = "Coverage note from run B." };

        // Act
        var merged = ChecklistMerger.Merge([runA, runB]);

        // Assert
        merged.Summary.Should().Be("Summary from run B.");
        merged.CoverageNote.Should().Be("Coverage note from run B.");
        merged.StricterRunVerdict.Should().Be(ReviewVerdict.Fail);
        merged.OtherRunVerdict.Should().Be(ReviewVerdict.PassWithComments);
    }

    [Fact]
    public void Merge_GivenATieOnOwnVerdict_Should_TakeSummaryAndCoverageFromRunA()
    {
        // Arrange — both runs' own results are PASS WITH COMMENTS, from concerns on different checks
        var runA = Run((CheckId.Testing, CheckStatus.Concern, "No test for the failure path.", false))
            with { Summary = "Summary from run A.", CoverageNote = "Coverage note from run A." };
        var runB = Run((CheckId.Performance, CheckStatus.Concern, "Retries without a backoff.", false))
            with { Summary = "Summary from run B.", CoverageNote = "Coverage note from run B." };

        // Act
        var merged = ChecklistMerger.Merge([runA, runB]);

        // Assert
        merged.Summary.Should().Be("Summary from run A.");
        merged.CoverageNote.Should().Be("Coverage note from run A.");
        merged.StricterRunVerdict.Should().Be(ReviewVerdict.PassWithComments);
        merged.OtherRunVerdict.Should().Be(ReviewVerdict.PassWithComments);
    }

    [Fact]
    public void Merge_GivenAPassRunThenAPassWithCommentsRun_Should_TreatRunBAsTheStricterRun()
    {
        // Arrange — run A rates every check a pass, so its own result is PASS; run B has a concern on Testing, so its own result is
        // PASS WITH COMMENTS. Neither fails Security, and each run lists one finding of its own.
        var unnamedUnit = new ChecklistFinding(CheckId.StandardsDesign, "src/RefundClient.cs", 5, "The retry delay hides its unit.", "Put the unit in its name.");
        var untestedFailure = new ChecklistFinding(CheckId.Testing, "tests/RefundClientTests.cs", 40, "No test for a failed refund.", "Add one.");
        var runA = Run()
            with { Findings = [unnamedUnit], Summary = "Summary from run A.", CoverageNote = "Coverage note from run A." };
        var runB = Run((CheckId.Testing, CheckStatus.Concern, "No test for the failure path.", false))
            with { Findings = [untestedFailure], Summary = "Summary from run B.", CoverageNote = "Coverage note from run B." };

        // Act
        var merged = ChecklistMerger.Merge([runA, runB]);

        // Assert — PASS WITH COMMENTS is worse than PASS, so the run that found the concern leads
        merged.Summary.Should().Be("Summary from run B.");
        merged.CoverageNote.Should().Be("Coverage note from run B.");
        merged.StricterRunVerdict.Should().Be(ReviewVerdict.PassWithComments);
        merged.OtherRunVerdict.Should().Be(ReviewVerdict.Pass);
        merged.Findings.Should().Equal(untestedFailure, unnamedUnit);
    }

    [Fact]
    public void Merge_GivenAPassWithCommentsRunThenAPassRun_Should_TreatRunAAsTheStricterRun()
    {
        // Arrange — the answers of the test above in the other order: run A has the concern on Testing and run B rates every check a pass
        var untestedFailure = new ChecklistFinding(CheckId.Testing, "tests/RefundClientTests.cs", 40, "No test for a failed refund.", "Add one.");
        var unnamedUnit = new ChecklistFinding(CheckId.StandardsDesign, "src/RefundClient.cs", 5, "The retry delay hides its unit.", "Put the unit in its name.");
        var runA = Run((CheckId.Testing, CheckStatus.Concern, "No test for the failure path.", false))
            with { Findings = [untestedFailure], Summary = "Summary from run A.", CoverageNote = "Coverage note from run A." };
        var runB = Run()
            with { Findings = [unnamedUnit], Summary = "Summary from run B.", CoverageNote = "Coverage note from run B." };

        // Act
        var merged = ChecklistMerger.Merge([runA, runB]);

        // Assert — a run B that differs but is milder does not take over from run A
        merged.Summary.Should().Be("Summary from run A.");
        merged.CoverageNote.Should().Be("Coverage note from run A.");
        merged.StricterRunVerdict.Should().Be(ReviewVerdict.PassWithComments);
        merged.OtherRunVerdict.Should().Be(ReviewVerdict.Pass);
        merged.Findings.Should().Equal(untestedFailure, unnamedUnit);
    }

    [Fact]
    public void Merge_GivenTheSameStatusForEveryCheck_Should_ReportAgreement()
    {
        // Arrange — agreement is about statuses: the runs' notes on Testing and their serious flags differ
        var runA = Run((CheckId.Testing, CheckStatus.Concern, "Note from run A.", false));
        var runB = Run((CheckId.Testing, CheckStatus.Concern, "Note from run B.", true));

        // Act
        var merged = ChecklistMerger.Merge([runA, runB]);

        // Assert
        merged.RunCount.Should().Be(2);
        merged.IsUnanimous.Should().BeTrue();
    }

    [Fact]
    public void Merge_GivenAMissingCheckInOneRun_Should_NotReportAgreement()
    {
        // Arrange — run B rated the checks as run A did but left Security out; both runs' own results are PASS WITH COMMENTS
        var runA = Run((CheckId.Testing, CheckStatus.Concern, "No test for the failure path.", false));
        var runB = LeaveOut(Run((CheckId.Testing, CheckStatus.Concern, "No test for the failure path.", false)), CheckId.Security);

        // Act
        var merged = ChecklistMerger.Merge([runA, runB]);

        // Assert
        merged.IsUnanimous.Should().BeFalse();
    }

    [Fact]
    public void Merge_GivenACheckBothRunsLeftOut_Should_NotReportAgreement()
    {
        // Arrange — the runs rated the other seven checks alike
        var runA = LeaveOut(Run(), CheckId.Security);
        var runB = LeaveOut(Run(), CheckId.Security);

        // Act
        var merged = ChecklistMerger.Merge([runA, runB]);

        // Assert — agreement needs both runs to have rated every check
        merged.IsUnanimous.Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Merge_GivenNoneOrThreeAnswers_Should_Throw(int answerCount)
    {
        // Arrange
        var answers = Enumerable.Repeat(Run(), answerCount).ToList();

        // Act & Assert
        var act = () => ChecklistMerger.Merge(answers);
        act.Should().Throw<ArgumentException>().WithParameterName("answers");
    }

    /// <summary>
    /// One run's answer from short ratings: every check rated a pass, except those given as (check, status, note, serious). It has no
    /// findings, a summary and no coverage note; a test that needs other ones sets them with <c>with</c>.
    /// </summary>
    private static RunAnswer Run(params (CheckId Id, CheckStatus Status, string Note, bool IsSerious)[] ratings)
    {
        var given = ratings.ToDictionary(rating => rating.Id, rating => new RatedCheck(rating.Id, rating.Status, rating.Note, rating.IsSerious));
        var rated = Checklist.Order.ToDictionary(id => id, id => given.GetValueOrDefault(id) ?? new RatedCheck(id, CheckStatus.Pass, "", false));

        return new RunAnswer(rated, [], "Adds retries to the refund client.", "");
    }

    /// <summary>The same answer without its ratings of the checks given, as if the run had left them out.</summary>
    private static RunAnswer LeaveOut(RunAnswer answer, params CheckId[] ids)
    {
        return answer with { Rated = answer.Rated.Where(rating => !ids.Contains(rating.Key)).ToDictionary() };
    }

    /// <summary>The rating of a check that was not assessed, with the note the spec gives it.</summary>
    private static RatedCheck NotAssessed(CheckId id)
    {
        return new RatedCheck(id, CheckStatus.Concern, "Not assessed by the reviewer.", false);
    }
}
