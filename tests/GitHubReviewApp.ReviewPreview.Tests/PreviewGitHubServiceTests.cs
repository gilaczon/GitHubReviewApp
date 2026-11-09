namespace GitHubReviewApp.ReviewPreview.Tests;

public class PreviewGitHubServiceTests
{
    private const string Owner = "my-org";
    private const string Repo = "my-service";
    private const int PrNumber = 7;
    private const string Token = "gho_token";
    private const string HeadSha = "0123456789abcdef0123456789abcdef01234567";

    private readonly Mock<IGitHubService> _github = new(MockBehavior.Strict);
    private readonly PreviewCapture _capture = new();

    private PreviewGitHubService CreateSut()
    {
        return new PreviewGitHubService(_github.Object, _capture);
    }

    [Theory]
    [InlineData("closed", false)]
    [InlineData("open", true)]
    [InlineData("closed", true)]
    public async Task GetPullRequestAsync_GivenAMergedClosedOrDraftPullRequest_Should_ReportItOpenAndReady(string state, bool isDraft)
    {
        // Arrange
        var pullRequest = new PullRequestInfo(state, isDraft, "Add retries", HeadSha, "basesha", 3, 40, 10);
        _github.Setup(g => g.GetPullRequestAsync(Owner, Repo, PrNumber, Token, It.IsAny<CancellationToken>())).ReturnsAsync(pullRequest);

        // Act
        var result = await CreateSut().GetPullRequestAsync(Owner, Repo, PrNumber, Token, CancellationToken.None);

        // Assert
        result.Should().Be(pullRequest with { State = "open", IsDraft = false });
    }

    [Fact]
    public async Task ListIssueCommentsAsync_GivenAnyPullRequest_Should_ReportNoCommentsWithoutAskingGitHub()
    {
        // Arrange — a strict mock: any call to GitHub would throw

        // Act
        var comments = await CreateSut().ListIssueCommentsAsync(Owner, Repo, PrNumber, Token, CancellationToken.None);

        // Assert
        comments.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateIssueCommentAsync_GivenTheReviewComment_Should_CaptureItWithoutWritingToGitHub()
    {
        // Arrange
        const string body = "PASS - nothing to fix";

        // Act
        await CreateSut().CreateIssueCommentAsync(Owner, Repo, PrNumber, body, Token, CancellationToken.None);

        // Assert
        _capture.CommentBody.Should().Be(body);
    }

    [Fact]
    public async Task UpdateIssueCommentAsync_GivenTheReviewComment_Should_CaptureItWithoutWritingToGitHub()
    {
        // Arrange
        const string body = "FAIL - fix the security problem";

        // Act
        await CreateSut().UpdateIssueCommentAsync(Owner, Repo, 12, body, Token, CancellationToken.None);

        // Assert
        _capture.CommentBody.Should().Be(body);
    }

    [Fact]
    public async Task CreateCheckRunAsync_GivenTheCheckRun_Should_CaptureItWithoutWritingToGitHub()
    {
        // Arrange
        var checkRun = new CheckRunRequest(HeadSha, CheckRunConclusion.Success, "PASS", "summary", "7");

        // Act
        await CreateSut().CreateCheckRunAsync(Owner, Repo, checkRun, Token, CancellationToken.None);

        // Assert
        _capture.CheckRun.Should().Be(checkRun);
    }

    [Fact]
    public async Task GetPullRequestFilesAsync_GivenAPullRequest_Should_ReadThemFromGitHub()
    {
        // Arrange
        IReadOnlyList<PullRequestFile> files = [];
        _github.Setup(g => g.GetPullRequestFilesAsync(Owner, Repo, PrNumber, Token, It.IsAny<CancellationToken>())).ReturnsAsync(files);

        // Act
        var result = await CreateSut().GetPullRequestFilesAsync(Owner, Repo, PrNumber, Token, CancellationToken.None);

        // Assert
        result.Should().BeSameAs(files);
    }
}
