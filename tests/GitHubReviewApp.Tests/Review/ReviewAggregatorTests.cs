namespace GitHubReviewApp.Tests.Review;

public class ReviewAggregatorTests
{
    // New-side lines 10 (context), 11 (added), 12 (context), 13 (context)
    private const string SamplePatch = "@@ -1,3 +10,4 @@\n a\n+b\n c\n d";

    private static PlannedFile Planned(string path, string? patch = SamplePatch)
    {
        return new PlannedFile(new PullRequestFile(path, "modified", 1, 0, patch), "rendered", false);
    }

    private static ReviewPlan PlanOf(params ReviewChunk[] chunks)
    {
        return new ReviewPlan(chunks, [], chunks.Sum(chunk => chunk.Files.Count));
    }

    private static ReviewPlan SingleFilePlan(string path = "src/Foo.cs")
    {
        return PlanOf(new ReviewChunk(0, [Planned(path)]));
    }

    private static ReviewFinding Finding(
        string path = "src/Foo.cs",
        int line = 11,
        ReviewSeverity severity = ReviewSeverity.High,
        string title = "A problem")
    {
        return new ReviewFinding(path, line, severity, "bug", title, "Details.");
    }

    private static ClaudeReviewResult Completed(
        string summary = "Summary.",
        IEnumerable<ReviewFinding>? findings = null,
        ClaudeUsage? usage = null,
        int omittedFindingCount = 0,
        int droppedFindingCount = 0)
    {
        return new ClaudeReviewResult(
            ChunkOutcome.Completed,
            summary,
            findings?.ToList() ?? [],
            "test-model",
            usage ?? ClaudeUsage.Empty,
            OmittedFindingCount: omittedFindingCount,
            DroppedFindingCount: droppedFindingCount);
    }

    private static ReviewAggregator BuildSut(ReviewOptions? options = null)
    {
        return new ReviewAggregator(Options.Create(options ?? new ReviewOptions()));
    }

    // ── Findings the model says it left out ──────────────────────────────────

    [Fact]
    public void Aggregate_GivenPartsThatEachReportAnAbsurdOmittedCount_Should_NotLetTheSumWrapAroundToANegativeNumber()
    {
        // Arrange — the count comes from the model, and two of them at the largest int would add up to a negative one
        var plan = PlanOf(new ReviewChunk(0, [Planned("src/A.cs")]), new ReviewChunk(1, [Planned("src/B.cs")]));
        var results = new[]
        {
            Completed(omittedFindingCount: int.MaxValue),
            Completed(omittedFindingCount: int.MaxValue)
        };

        // Act
        var report = BuildSut().Aggregate(plan, results);

        // Assert
        report.OmittedFindingCount.Should().Be(2_000);
    }

    [Fact]
    public void Aggregate_GivenPartsThatDroppedFindingsTheyCouldNotRead_Should_AddUpTheCountSoTheReviewCanSayHowManyWereLeftOut()
    {
        // Arrange
        var plan = PlanOf(new ReviewChunk(0, [Planned("src/A.cs")]), new ReviewChunk(1, [Planned("src/B.cs")]));
        var results = new[] { Completed(droppedFindingCount: 2), Completed(droppedFindingCount: 3) };

        // Act
        var report = BuildSut().Aggregate(plan, results);

        // Assert
        report.DroppedFindingCount.Should().Be(5);
    }

    [Fact]
    public void Aggregate_GivenAPartThatDidNotCompleteAndCarriesADroppedCount_Should_NotCountItBecauseItsFindingsWereNeverRead()
    {
        // Arrange
        var plan = PlanOf(new ReviewChunk(0, [Planned("src/A.cs")]), new ReviewChunk(1, [Planned("src/B.cs")]));
        var refused = new ClaudeReviewResult(ChunkOutcome.Refused, string.Empty, [], "test-model", ClaudeUsage.Empty, "cyber", DroppedFindingCount: 9);
        var results = new[] { Completed(droppedFindingCount: 1), refused };

        // Act
        var report = BuildSut().Aggregate(plan, results);

        // Assert
        report.DroppedFindingCount.Should().Be(1);
    }

    [Fact]
    public void Aggregate_GivenPartsThatReportSensibleOmittedCounts_Should_AddThemUp()
    {
        // Arrange
        var plan = PlanOf(new ReviewChunk(0, [Planned("src/A.cs")]), new ReviewChunk(1, [Planned("src/B.cs")]));
        var results = new[] { Completed(omittedFindingCount: 4), Completed(omittedFindingCount: 3) };

        // Act
        var report = BuildSut().Aggregate(plan, results);

        // Assert
        report.OmittedFindingCount.Should().Be(7);
    }

    // ── Anchoring ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(10)]   // context line
    [InlineData(11)]   // added line
    [InlineData(13)]   // last context line
    public void Aggregate_GivenFindingOnALineVisibleInTheDiff_Should_MarkItAnchored(int line)
    {
        // Arrange
        var results = new[] { Completed(findings: [Finding(line: line)]) };

        // Act
        var report = BuildSut().Aggregate(SingleFilePlan(), results);

        // Assert
        report.Findings.Should().ContainSingle().Which.IsAnchored.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]    // "whole file" sentinel
    [InlineData(9)]    // before the hunk
    [InlineData(14)]   // after the hunk
    [InlineData(500)]  // hallucinated
    public void Aggregate_GivenFindingOnALineNotInTheDiff_Should_KeepItButNotAnchorIt(int line)
    {
        // Arrange
        var results = new[] { Completed(findings: [Finding(line: line)]) };

        // Act
        var report = BuildSut().Aggregate(SingleFilePlan(), results);

        // Assert
        var finding = report.Findings.Should().ContainSingle().Subject;
        finding.IsAnchored.Should().BeFalse();
        finding.Line.Should().Be(line);
    }

    [Theory]
    [InlineData("b/src/Foo.cs")]
    [InlineData("a/src/Foo.cs")]
    [InlineData("./src/Foo.cs")]
    [InlineData("/src/Foo.cs")]
    [InlineData("src\\Foo.cs")]
    [InlineData("  src/Foo.cs  ")]
    [InlineData("SRC/FOO.CS")]
    public void Aggregate_GivenPathWrittenSlightlyDifferently_Should_MatchThePlannedFileAndUseItsExactPath(string modelPath)
    {
        // Arrange
        var results = new[] { Completed(findings: [Finding(path: modelPath)]) };

        // Act
        var report = BuildSut().Aggregate(SingleFilePlan("src/Foo.cs"), results);

        // Assert
        var finding = report.Findings.Should().ContainSingle().Subject;
        finding.Path.Should().Be("src/Foo.cs");
        finding.IsAnchored.Should().BeTrue();
    }

    [Fact]
    public void Aggregate_GivenPathOfAFileThatIsNotInThePullRequest_Should_KeepItUnanchoredWithTheOriginalPath()
    {
        // Arrange
        var results = new[] { Completed(findings: [Finding(path: "src/Other.cs")]) };

        // Act
        var report = BuildSut().Aggregate(SingleFilePlan("src/Foo.cs"), results);

        // Assert
        var finding = report.Findings.Should().ContainSingle().Subject;
        finding.Path.Should().Be("src/Other.cs");
        finding.IsAnchored.Should().BeFalse();
    }

    [Fact]
    public void Aggregate_GivenPathMatchingTwoFilesOnlyByCase_Should_NotGuessAndLeaveItUnanchored()
    {
        // Arrange
        var plan = PlanOf(new ReviewChunk(0, [Planned("src/foo.cs"), Planned("src/Foo.cs")]));
        var results = new[] { Completed(findings: [Finding(path: "SRC/FOO.CS")]) };

        // Act
        var report = BuildSut().Aggregate(plan, results);

        // Assert
        report.Findings.Should().ContainSingle().Which.IsAnchored.Should().BeFalse();
    }

    [Fact]
    public void Aggregate_GivenFindingsInDifferentFiles_Should_CheckEachAgainstItsOwnDiff()
    {
        // Arrange
        var plan = PlanOf(new ReviewChunk(0, [Planned("src/A.cs", "@@ -1,1 +100,1 @@\n+x"), Planned("src/B.cs")]));
        var results = new[]
        {
            Completed(findings: [Finding("src/A.cs", line: 100), Finding("src/A.cs", line: 11, title: "other"), Finding("src/B.cs", line: 11, title: "third")])
        };

        // Act
        var report = BuildSut().Aggregate(plan, results);

        // Assert
        report.Findings.Single(f => f.Path == "src/A.cs" && f.Line == 100).IsAnchored.Should().BeTrue();
        report.Findings.Single(f => f.Path == "src/A.cs" && f.Line == 11).IsAnchored.Should().BeFalse();
        report.Findings.Single(f => f.Path == "src/B.cs").IsAnchored.Should().BeTrue();
    }

    // ── Merging ──────────────────────────────────────────────────────────────

    [Fact]
    public void Aggregate_GivenTheSameFindingFromTwoRequests_Should_KeepOnlyOne()
    {
        // Arrange
        var plan = PlanOf(new ReviewChunk(0, [Planned("src/Foo.cs")]), new ReviewChunk(1, [Planned("src/Bar.cs")]));
        var results = new[]
        {
            Completed(findings: [Finding(title: "Null dereference")]),
            Completed(findings: [Finding(title: "  null DEREFERENCE ")])
        };

        // Act
        var report = BuildSut().Aggregate(plan, results);

        // Assert
        report.Findings.Should().ContainSingle();
    }

    [Fact]
    public void Aggregate_GivenTheSameTitleOnDifferentLines_Should_KeepBothBecauseTheyAreDifferentFindings()
    {
        // Arrange — "Missing null check" on line 11 and again on line 12 are two places to fix
        var plan = SingleFilePlan();
        var results = new[] { Completed(findings: [Finding(line: 11, title: "Missing null check"), Finding(line: 12, title: "Missing null check")]) };

        // Act
        var report = BuildSut().Aggregate(plan, results);

        // Assert
        report.Findings.Select(finding => finding.Line).Should().Equal(11, 12);
    }

    [Fact]
    public void Aggregate_GivenTheSameTitleInDifferentFiles_Should_KeepBothBecauseTheyAreDifferentFindings()
    {
        // Arrange
        var plan = PlanOf(new ReviewChunk(0, [Planned("src/A.cs"), Planned("src/B.cs")]));
        var results = new[] { Completed(findings: [Finding("src/A.cs", 11, title: "Missing null check"), Finding("src/B.cs", 11, title: "Missing null check")]) };

        // Act
        var report = BuildSut().Aggregate(plan, results);

        // Assert
        report.Findings.Select(finding => finding.Path).Should().Equal("src/A.cs", "src/B.cs");
    }

    [Fact]
    public void Aggregate_GivenTheSameFindingAsLowInOnePartAndHighInALaterOne_Should_KeepTheMoreSevereCopy()
    {
        // Arrange — the less severe copy comes first, so keeping the first one seen would keep the wrong one
        var plan = PlanOf(new ReviewChunk(0, [Planned("src/Foo.cs")]), new ReviewChunk(1, [Planned("src/Foo.cs")]));
        var results = new[]
        {
            Completed(findings: [Finding(severity: ReviewSeverity.Low, title: "Null dereference")]),
            Completed(findings: [Finding(severity: ReviewSeverity.High, title: "Null dereference")])
        };

        // Act
        var report = BuildSut().Aggregate(plan, results);

        // Assert
        report.Findings.Should().ContainSingle().Which.Severity.Should().Be(ReviewSeverity.High);
    }

    [Fact]
    public void Aggregate_GivenFindingsOfTheSameSeverityAndPathWhoseTitlesSortAgainstTheirLines_Should_OrderThemByLine()
    {
        // Arrange — had the line not been a key, the titles would put these the other way round
        var plan = SingleFilePlan();
        var results = new[] { Completed(findings: [Finding(line: 13, title: "a first by title"), Finding(line: 11, title: "b second by title")]) };

        // Act
        var report = BuildSut().Aggregate(plan, results);

        // Assert
        report.Findings.Select(finding => finding.Line).Should().Equal(11, 13);
    }

    [Fact]
    public void Aggregate_GivenFindingsOfTheSameSeverityAndLineWhoseTitlesSortAgainstTheirPaths_Should_OrderThemByPath()
    {
        // Arrange
        var plan = PlanOf(new ReviewChunk(0, [Planned("src/A.cs"), Planned("src/B.cs")]));
        var results = new[] { Completed(findings: [Finding("src/B.cs", 11, title: "a first by title"), Finding("src/A.cs", 11, title: "z last by title")]) };

        // Act
        var report = BuildSut().Aggregate(plan, results);

        // Assert
        report.Findings.Select(finding => finding.Path).Should().Equal("src/A.cs", "src/B.cs");
    }

    [Fact]
    public void Aggregate_GivenFindingsOfTheSameSeverityWhoseLinesSortAgainstTheirPaths_Should_PutTheEarlierFileFirstWhateverTheLine()
    {
        // Arrange — the path is the key after the severity, so a finding in an earlier file comes first even when its line is the later one
        var plan = PlanOf(new ReviewChunk(0, [Planned("src/A.cs"), Planned("src/B.cs")]));
        var results = new[] { Completed(findings: [Finding("src/B.cs", 11, title: "early line in the later file"), Finding("src/A.cs", 13, title: "late line in the earlier file")]) };

        // Act
        var report = BuildSut().Aggregate(plan, results);

        // Assert
        report.Findings.Select(finding => finding.Path).Should().Equal("src/A.cs", "src/B.cs");
    }

    [Fact]
    public void Aggregate_GivenFindingsOfTheSameSeverityPathAndLine_Should_OrderThemByTitleWhateverOrderTheyArrivedIn()
    {
        // Arrange — arrival order is the reverse of title order, so only a title tie-break puts them right
        var plan = SingleFilePlan();
        var results = new[] { Completed(findings: [Finding(title: "b second"), Finding(title: "a first")]) };

        // Act
        var report = BuildSut().Aggregate(plan, results);

        // Assert
        report.Findings.Select(finding => finding.Title).Should().Equal("a first", "b second");
    }

    [Fact]
    public void Aggregate_GivenFindings_Should_SortBySeverityThenPathThenLine()
    {
        // Arrange
        var plan = PlanOf(new ReviewChunk(0, [Planned("src/A.cs"), Planned("src/B.cs")]));
        var results = new[]
        {
            Completed(findings:
            [
                Finding("src/B.cs", 11, ReviewSeverity.Low, "low b"),
                Finding("src/A.cs", 13, ReviewSeverity.High, "high a13"),
                Finding("src/A.cs", 11, ReviewSeverity.High, "high a11"),
                Finding("src/B.cs", 11, ReviewSeverity.Critical, "critical b")
            ])
        };

        // Act
        var report = BuildSut().Aggregate(plan, results);

        // Assert
        report.Findings.Select(f => f.Title).Should().Equal("critical b", "high a11", "high a13", "low b");
    }

    [Fact]
    public void Aggregate_GivenMoreFindingsThanTheLimit_Should_KeepTheMostSevereAndCountTheOmittedOnes()
    {
        // Arrange
        var findings = Enumerable.Range(1, 5).Select(i => Finding(title: $"low {i}", severity: ReviewSeverity.Low))
            .Append(Finding(title: "the critical one", severity: ReviewSeverity.Critical))
            .ToList();
        var results = new[] { Completed(findings: findings) };
        var options = new ReviewOptions { MaxFindings = 3 };

        // Act
        var report = BuildSut(options).Aggregate(SingleFilePlan(), results);

        // Assert
        report.Findings.Should().HaveCount(3);
        report.Findings[0].Title.Should().Be("the critical one");
        report.OmittedFindingCount.Should().Be(3);
    }

    [Fact]
    public void Aggregate_GivenSummaries_Should_CollectTheNonBlankOnesInChunkOrder()
    {
        // Arrange
        var plan = PlanOf(
            new ReviewChunk(0, [Planned("src/A.cs")]),
            new ReviewChunk(1, [Planned("src/B.cs")]),
            new ReviewChunk(2, [Planned("src/C.cs")]));
        var results = new[] { Completed("First."), Completed("   "), Completed("Third.") };

        // Act
        var report = BuildSut().Aggregate(plan, results);

        // Assert
        report.Summaries.Should().Equal("First.", "Third.");
    }

    [Fact]
    public void Aggregate_GivenSeveralRequests_Should_SumTheUsageOfEveryRequest()
    {
        // Arrange
        var plan = PlanOf(new ReviewChunk(0, [Planned("src/A.cs")]), new ReviewChunk(1, [Planned("src/B.cs")]));
        var results = new[]
        {
            Completed(usage: new ClaudeUsage(100, 10, 5, 1)),
            Completed(usage: new ClaudeUsage(200, 20, 0, 2))
        };

        // Act
        var report = BuildSut().Aggregate(plan, results);

        // Assert
        report.Usage.Should().Be(new ClaudeUsage(300, 30, 5, 3));
    }

    // ── Incomplete requests ──────────────────────────────────────────────────

    [Theory]
    [InlineData(ChunkOutcome.OutputTruncated)]
    [InlineData(ChunkOutcome.Refused)]
    [InlineData(ChunkOutcome.InvalidOutput)]
    public void Aggregate_GivenARequestThatDidNotComplete_Should_ReportItsFilesAndIgnoreAnyFindingsItCarried(ChunkOutcome outcome)
    {
        // Arrange
        var plan = PlanOf(new ReviewChunk(0, [Planned("src/A.cs"), Planned("src/B.cs")]));
        var results = new[]
        {
            new ClaudeReviewResult(outcome, "partial summary", [Finding()], "test-model", ClaudeUsage.Empty, "cyber")
        };

        // Act
        var report = BuildSut().Aggregate(plan, results);

        // Assert
        report.Findings.Should().BeEmpty();
        report.Summaries.Should().BeEmpty();
        report.IncompleteChunks.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new IncompleteChunk(0, ["src/A.cs", "src/B.cs"], outcome, "cyber"));
    }

    [Fact]
    public void Aggregate_GivenOneGoodAndOneFailedRequest_Should_KeepTheGoodFindingsAndReportTheFailedFiles()
    {
        // Arrange
        var plan = PlanOf(new ReviewChunk(0, [Planned("src/A.cs")]), new ReviewChunk(1, [Planned("src/B.cs")]));
        var results = new[]
        {
            Completed(findings: [Finding("src/A.cs")]),
            new ClaudeReviewResult(ChunkOutcome.Refused, string.Empty, [], "test-model", ClaudeUsage.Empty)
        };

        // Act
        var report = BuildSut().Aggregate(plan, results);

        // Assert
        report.Findings.Should().ContainSingle();
        report.IncompleteChunks.Should().ContainSingle().Which.Index.Should().Be(1);
    }

    [Fact]
    public void Aggregate_GivenResultCountThatDiffersFromTheChunkCount_Should_Throw()
    {
        // Arrange
        var plan = PlanOf(new ReviewChunk(0, [Planned("src/A.cs")]), new ReviewChunk(1, [Planned("src/B.cs")]));
        var sut = BuildSut();

        // Act
        var act = () => sut.Aggregate(plan, [Completed()]);

        // Assert
        act.Should().Throw<ArgumentException>().WithMessage("*one result per chunk*");
    }
}
