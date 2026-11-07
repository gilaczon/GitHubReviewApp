namespace GitHubReviewApp.Tests.Services;

/// <summary>
/// Feeds responses GitHub really produced (Fixtures/GitHub/README.md says where from and what was trimmed) through the real service, diff parser,
/// planner and review history. The hand-written tests cover shapes GitHub can return; these prove the app reads one real pull request correctly.
/// </summary>
public class RecordedGitHubResponseTests
{
    private const string Token = "ghs_recorded_test_token";
    private const string AppLogin = "my-reviewer[bot]";

    private static GitHubService BuildSut(string responseJson)
    {
        return new GitHubService(
            new HttpClient(new TestHttpMessageHandler(responseJson)),
            NullLogger<GitHubService>.Instance,
            Options.Create(new ReviewOptions()));
    }

    private static Task<PullRequestInfo> ReadPullRequestAsync()
    {
        return BuildSut(RecordedGitHub.PullRequest)
            .GetPullRequestAsync(RecordedGitHub.Owner, RecordedGitHub.Repo, RecordedGitHub.PrNumber, Token, CancellationToken.None);
    }

    private static Task<IReadOnlyList<PullRequestFile>> ReadFilesAsync()
    {
        return BuildSut(RecordedGitHub.PullRequestFiles)
            .GetPullRequestFilesAsync(RecordedGitHub.Owner, RecordedGitHub.Repo, RecordedGitHub.PrNumber, Token, CancellationToken.None);
    }

    private static Task<IReadOnlyList<PullRequestReviewSummary>> ReadReviewsAsync()
    {
        return BuildSut(RecordedGitHub.PullRequestReviews)
            .ListReviewsAsync(RecordedGitHub.Owner, RecordedGitHub.Repo, RecordedGitHub.PrNumber, Token, CancellationToken.None);
    }

    // ── The pull request ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetPullRequestAsync_GivenTheRecordedPullRequest_Should_ReadTheValuesGitHubReported()
    {
        // Arrange

        // Act
        var result = await ReadPullRequestAsync();

        // Assert
        result.Should().Be(new PullRequestInfo(
            "closed", false, RecordedGitHub.Title, RecordedGitHub.HeadSha, RecordedGitHub.BaseSha, ChangedFileCount: 9, Additions: 78, Deletions: 80));
        result.IsOpen.Should().BeFalse("it was merged when it was recorded");
    }

    // ── The files ────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetPullRequestFilesAsync_GivenTheRecordedFiles_Should_ReadEveryFileWithItsPatch()
    {
        // Arrange

        // Act
        var files = await ReadFilesAsync();

        // Assert
        files.Should().HaveCount(9);
        files.Should().OnlyContain(file => file.Status == "modified" && !string.IsNullOrWhiteSpace(file.Patch));
        files.Select(file => file.Path).Should().Contain("extensions/copilot/pnpm-lock.yaml");
    }

    [Fact]
    public async Task GetPullRequestFilesAsync_GivenTheRecordedFiles_Should_AddUpToTheTotalsTheRecordedPullRequestReported()
    {
        // Arrange
        var pullRequest = await ReadPullRequestAsync();

        // Act
        var files = await ReadFilesAsync();

        // Assert — two separate real responses that must agree with each other
        files.Count.Should().Be(pullRequest.ChangedFileCount);
        files.Sum(file => file.Additions).Should().Be(pullRequest.Additions);
        files.Sum(file => file.Deletions).Should().Be(pullRequest.Deletions);
    }

    [Fact]
    public async Task Parse_GivenTheRecordedPatches_Should_CountTheSameAddedAndRemovedLinesGitHubReportedForEachFile()
    {
        // Arrange
        var files = await ReadFilesAsync();

        // Act
        var disagreements = files
            .Select(file => (file.Path, file.Additions, file.Deletions, Parsed: PatchParser.Parse(file.Patch)))
            .Where(item => item.Parsed.Count(line => line.Kind == PatchLineKind.Added) != item.Additions
                || item.Parsed.Count(line => line.Kind == PatchLineKind.Removed) != item.Deletions)
            .Select(item => item.Path)
            .ToList();

        // Assert
        disagreements.Should().BeEmpty("a parser that miscounts real patches would anchor comments to the wrong lines");
    }

    [Fact]
    public async Task FromPatch_GivenTheRecordedPatches_Should_IndexEveryAddedAndContextLineOnTheNewSide()
    {
        // Arrange
        var files = await ReadFilesAsync();

        // Act
        var wrongIndexes = files
            .Select(file => (file.Path, Parsed: PatchParser.Parse(file.Patch), Index: DiffLineIndex.FromPatch(file.Patch)))
            .Where(item => item.Index.Count != item.Parsed.Count(line => line.NewLine is not null)
                || item.Parsed.Where(line => line.NewLine is not null).Any(line => !item.Index.Contains(line.NewLine!.Value)))
            .Select(item => item.Path)
            .ToList();

        // Assert
        wrongIndexes.Should().BeEmpty("GitHub rejects the whole review when one inline comment names a line outside the diff");
    }

    [Fact]
    public async Task FromPatch_GivenTheRecordedPatches_Should_IndexExactlyTheLinesTheHunkHeadersAccountFor()
    {
        // Arrange — the check above compares the index with the parser it is built on, so the two could be wrong together. This one counts from the
        // numbers in the hunk headers alone, with code that shares nothing with the parser.
        var files = await ReadFilesAsync();

        // Act
        var disagreements = files
            .Select(file => (file.Path, Index: DiffLineIndex.FromPatch(file.Patch), Expected: IndependentDiffLines.NewSideLines(file.Patch)))
            .Where(item => item.Index.Count != item.Expected.Count || item.Expected.Any(line => !item.Index.Contains(line)))
            .Select(item => item.Path)
            .ToList();

        // Assert
        files.Select(file => IndependentDiffLines.NewSideLines(file.Patch).Count).Should().OnlyContain(count => count > 0, "a comparison with nothing proves nothing");
        disagreements.Should().BeEmpty();
    }

    [Fact]
    public async Task FromPatch_GivenTheRecordedPackageJsonPatch_Should_PlaceTheChangedLineAtLine6827WhereTheRealFileHasIt()
    {
        // Arrange — 6827 is not computed by anything in this repository. It is the line that `"@types/node": "24.x",` has in
        // extensions/copilot/package.json at the recorded head commit, counted in the file itself (read with `gh api` on 2026-11-05), so it holds
        // whatever the parser and the index do.
        var packageJson = (await ReadFilesAsync()).Single(file => file.Path == "extensions/copilot/package.json");

        // Act
        var added = PatchParser.Parse(packageJson.Patch).Single(line => line.Kind == PatchLineKind.Added);
        var index = DiffLineIndex.FromPatch(packageJson.Patch);

        // Assert
        added.Text.Should().Contain("\"@types/node\": \"24.x\"");
        added.NewLine.Should().Be(6827);
        Enumerable.Range(6824, 7).Should().OnlyContain(line => index.Contains(line), "three lines of context on each side of the change");
        index.Count.Should().Be(7);
        index.Contains(6823).Should().BeFalse();
        index.Contains(6831).Should().BeFalse();
    }

    [Theory]
    [InlineData(6827, true)]
    [InlineData(6824, true)]
    [InlineData(6830, true)]
    [InlineData(6823, false)]
    [InlineData(6831, false)]
    [InlineData(0, false)]
    public async Task Aggregate_GivenAFindingOnTheRecordedPackageJson_Should_AnchorItOnlyOnALineTheRealDiffShows(int line, bool isAnchored)
    {
        // Arrange — lines 6824 to 6830 are what GitHub shows of this file, so a comment anywhere else would make it reject the whole review
        var options = Options.Create(new ReviewOptions());
        var plan = new ReviewPlanner(options).CreatePlan(await ReadFilesAsync());
        var finding = new ReviewFinding("extensions/copilot/package.json", line, ReviewSeverity.Low, "maintainability", "Pinned version", "The version is not a range.");
        var results = new[] { new ClaudeReviewResult(ChunkOutcome.Completed, "ok", [finding], "claude-test", ClaudeUsage.Empty) };

        // Act
        var report = new ReviewAggregator(options).Aggregate(plan, results);

        // Assert
        report.Findings.Should().ContainSingle().Which.IsAnchored.Should().Be(isAnchored);
    }

    [Fact]
    public async Task CreatePlan_GivenTheRecordedFiles_Should_LeaveOutOnlyTheLockfileAndReviewTheOtherEight()
    {
        // Arrange
        var files = await ReadFilesAsync();
        var planner = new ReviewPlanner(Options.Create(new ReviewOptions()));

        // Act
        var plan = planner.CreatePlan(files);

        // Assert
        plan.SkippedFiles.Should().ContainSingle().Which.Should().Be(new SkippedFile("extensions/copilot/pnpm-lock.yaml", FileSkipReason.DependencyLockfile));
        plan.ReviewedFileCount.Should().Be(8);
        plan.TotalFileCount.Should().Be(9);
        plan.Chunks.Should().ContainSingle("a change this size fits one request");
    }

    // ── The reviews ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ListReviewsAsync_GivenTheRecordedReviews_Should_ReadTheBotsLoginExactlyAsGitHubWritesIt()
    {
        // Arrange

        // Act
        var reviews = await ReadReviewsAsync();

        // Assert
        reviews.Should().HaveCount(2);

        var botReview = reviews[0];
        botReview.Id.Should().Be(5766893190);
        botReview.State.Should().Be("COMMENTED");
        botReview.CommitId.Should().Be(RecordedGitHub.HeadSha);
        botReview.AuthorLogin.Should().Be(RecordedGitHub.CopilotReviewerLogin);
        botReview.Body.Should().StartWith("<!-- ccr-overview-v2 -->");

        var approval = reviews[1];
        approval.State.Should().Be("APPROVED");
        approval.Body.Should().BeEmpty();
        approval.AuthorLogin.Should().Be("octocat");
    }

    [Fact]
    public async Task HasCompletedReview_GivenTheRecordedReviewsOfTheSameCommit_Should_NotTreatCopilotsReviewAsOurOwn()
    {
        // Arrange — a real bot reviewed this very commit and began its body with a hidden marker of its own
        var reviews = await ReadReviewsAsync();
        var ourReview = new PullRequestReviewSummary(
            1, "COMMENTED", "Our review.\n\n" + ReviewMarker.For(RecordedGitHub.HeadSha), RecordedGitHub.HeadSha, AppLogin);

        // Act
        var withoutOurs = ReviewHistory.HasCompletedReview(reviews, RecordedGitHub.HeadSha, AppLogin);
        var withOurs = ReviewHistory.HasCompletedReview(reviews.Append(ourReview), RecordedGitHub.HeadSha, AppLogin);

        // Assert — the negative needs its positive control, or a check that always answers no would pass
        withoutOurs.Should().BeFalse();
        withOurs.Should().BeTrue();
    }

    [Fact]
    public async Task OwnPendingReviews_GivenTheRecordedReviews_Should_FindNoneOfOursToDelete()
    {
        // Arrange
        var reviews = await ReadReviewsAsync();

        // Act
        var pending = ReviewHistory.OwnPendingReviews(reviews, AppLogin);

        // Assert
        pending.Should().BeEmpty();
    }
}
