namespace GitHubReviewApp.Tests.Services;

public class ReviewPublisherTests
{
    private const string Token = "ghs_token";
    private const string HeadSha = "0123456789abcdef0123456789abcdef01234567";
    private const string EarlierSha = "1111111111111111111111111111111111111111";
    private const string AppLogin = "my-reviewer[bot]";
    private const string NewBody = "PASS - new result";

    private static readonly ReviewQueueMessage _message = new("my-org", "my-service", 7, "title", "basesha", HeadSha, 12345L);
    private static readonly CheckRunRequest _checkRun = new(HeadSha, CheckRunConclusion.Success, "PASS", "summary", "7");

    private static (ReviewPublisher Sut, Mock<IGitHubService> GitHub, List<string> Calls) Build()
    {
        var calls = new List<string>();
        var github = new Mock<IGitHubService>();

        github.Setup(g => g.CreateCheckRunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CheckRunRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("check run"))
            .ReturnsAsync(900L);
        github.Setup(g => g.CreateIssueCommentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("create comment"))
            .ReturnsAsync(901L);
        github.Setup(g => g.UpdateIssueCommentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("update comment"))
            .Returns(Task.CompletedTask);

        return (new ReviewPublisher(github.Object, NullLogger<ReviewPublisher>.Instance), github, calls);
    }

    private static IssueComment Comment(long id, string author, string sha)
    {
        return new IssueComment(id, author, $"result\n\n{ReviewCommentMarker.For(sha, ReviewRecordOutcome.Reviewed)}");
    }

    [Fact]
    public async Task PublishAsync_GivenNoCommentOfOurs_Should_CreateTheCheckRunThenTheComment()
    {
        // Arrange
        var (sut, github, calls) = Build();

        // Act
        await sut.PublishAsync(_message, Token, AppLogin, [], NewBody, _checkRun, canWriteChecks: true, CancellationToken.None);

        // Assert
        calls.Should().Equal("check run", "create comment");
        github.Verify(g => g.CreateCheckRunAsync("my-org", "my-service", _checkRun, Token, It.IsAny<CancellationToken>()), Times.Once());
        github.Verify(g => g.CreateIssueCommentAsync("my-org", "my-service", 7, NewBody, Token, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task PublishAsync_GivenOurMarkedComments_Should_UpdateTheOldest()
    {
        // Arrange
        var (sut, github, calls) = Build();
        IssueComment[] comments = [Comment(30, AppLogin, HeadSha), Comment(12, AppLogin, EarlierSha), Comment(20, AppLogin, HeadSha)];

        // Act
        await sut.PublishAsync(_message, Token, AppLogin, comments, NewBody, _checkRun, canWriteChecks: true, CancellationToken.None);

        // Assert
        calls.Should().Equal("check run", "update comment");
        github.Verify(g => g.UpdateIssueCommentAsync("my-org", "my-service", 12L, NewBody, Token, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task PublishAsync_GivenSomeoneElsesCommentWithOurMarker_Should_LeaveItAndCreateOurs()
    {
        // Arrange
        var (sut, github, calls) = Build();
        IssueComment[] comments = [Comment(12, "octocat", HeadSha)];

        // Act
        await sut.PublishAsync(_message, Token, AppLogin, comments, NewBody, _checkRun, canWriteChecks: true, CancellationToken.None);

        // Assert
        calls.Should().Equal("check run", "create comment");
        github.Verify(
            g => g.UpdateIssueCommentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never());
    }

    [Fact]
    public async Task PublishAsync_GivenNoChecksPermission_Should_PostOnlyTheComment()
    {
        // Arrange
        var (sut, github, calls) = Build();

        // Act
        await sut.PublishAsync(_message, Token, AppLogin, [], NewBody, _checkRun, canWriteChecks: false, CancellationToken.None);

        // Assert
        calls.Should().Equal("create comment");
        github.Verify(
            g => g.CreateCheckRunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CheckRunRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never());
    }

    [Fact]
    public async Task PublishAsync_GivenTheCheckRunFailsForAReasonWorthRetrying_Should_ThrowBeforeTouchingTheComment()
    {
        // Arrange
        var (sut, github, calls) = Build();
        github.Setup(g => g.CreateCheckRunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CheckRunRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("unavailable", HttpStatusCode.ServiceUnavailable, false));
        IssueComment[] comments = [Comment(12, AppLogin, EarlierSha)];

        // Act
        var act = () => sut.PublishAsync(_message, Token, AppLogin, comments, NewBody, _checkRun, canWriteChecks: true, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<GitHubApiException>();
        calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task PublishAsync_GivenGitHubRefusesTheCheckRunForGood_Should_StillWriteTheComment(HttpStatusCode refusal)
    {
        // Arrange — a refusal a retry would not change, such as Checks revoked between the token and the request
        var (sut, github, calls) = Build();
        github.Setup(g => g.CreateCheckRunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CheckRunRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("refused", refusal, false));
        IssueComment[] comments = [Comment(12, AppLogin, EarlierSha)];

        // Act
        await sut.PublishAsync(_message, Token, AppLogin, comments, NewBody, _checkRun, canWriteChecks: true, CancellationToken.None);

        // Assert
        calls.Should().Equal("update comment");
        github.Verify(
            g => g.UpdateIssueCommentAsync(_message.Owner, _message.Repo, 12, NewBody, Token, It.IsAny<CancellationToken>()),
            Times.Once());
    }
}
