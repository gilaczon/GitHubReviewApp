namespace GitHubReviewApp.Tests.Services;

/// <summary>
/// Feeds responses GitHub really produced (Fixtures/GitHub/README.md says where from and what was trimmed) through the real service and diff
/// parser. The hand-written tests cover shapes GitHub can return; these prove the app reads one real pull request correctly.
/// </summary>
public class RecordedGitHubResponseTests
{
    private const string Token = "ghs_recorded_test_token";

    private static GitHubService BuildSut(string responseJson)
    {
        return new GitHubService(
            new HttpClient(new TestHttpMessageHandler(responseJson)),
            NullLogger<GitHubService>.Instance);
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

    // ── The pull request ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetPullRequestAsync_GivenTheRecordedPullRequest_Should_ReadTheValuesGitHubReported()
    {
        // Arrange

        // Act
        var result = await ReadPullRequestAsync();

        // Assert
        result.Should().Be(new PullRequestInfo(
            "closed", false, RecordedGitHub.Title, RecordedGitHub.HeadSha, RecordedGitHub.BaseSha, ChangedFileCount: 9, Additions: 78, Deletions: 80,
            BaseRef: "main", HeadRef: "feature-branch"));
        result.IsOpen.Should().BeFalse("it was merged when it was recorded");
    }

    [Fact]
    public async Task GetPullRequestAsync_GivenTheRecordedResponse_Should_ReadTheAuthorAndBranches()
    {
        // Arrange

        // Act
        var result = await ReadPullRequestAsync();

        // Assert — the recording keeps the branch names but not the description or the author (Fixtures/GitHub/README.md), so those read as empty
        result.BaseRef.Should().Be("main");
        result.HeadRef.Should().Be("feature-branch");
        result.AuthorLogin.Should().BeEmpty();
        result.Body.Should().BeEmpty();
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
    public async Task Parse_GivenTheRecordedPackageJsonPatch_Should_PlaceTheChangedLineAtLine6827WhereTheRealFileHasIt()
    {
        // Arrange — 6827 is not computed by anything in this repository. It is the line that `"@types/node": "24.x",` has in
        // extensions/copilot/package.json at the recorded head commit, counted in the file itself (read with `gh api` on 2026-11-05), so it holds
        // whatever the parser does.
        var packageJson = (await ReadFilesAsync()).Single(file => file.Path == "extensions/copilot/package.json");

        // Act
        var added = PatchParser.Parse(packageJson.Patch).Single(line => line.Kind == PatchLineKind.Added);

        // Assert
        added.Text.Should().Contain("\"@types/node\": \"24.x\"");
        added.NewLine.Should().Be(6827);
    }
}
