namespace GitHubReviewApp.Tests.Services;

public class GitHubServiceTests
{
    private const string Token = "ghs_installation_token";

    private static GitHubService BuildReviewSut(HttpMessageHandler handler, ILogger<GitHubService>? logger = null)
    {
        return new GitHubService(new HttpClient(handler), logger ?? NullLogger<GitHubService>.Instance);
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static string FilesJson(int count, int startAt = 1)
    {
        var files = Enumerable.Range(startAt, count)
            .Select(i => new { filename = $"src/File{i:D4}.cs", status = "modified", additions = 1, deletions = 0, patch = "@@ -1 +1 @@\n+x" });

        return JsonSerializer.Serialize(files);
    }

    // ── GetPullRequestAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task GetPullRequestAsync_GivenValidRequest_Should_CallThePullRequestEndpointWithAuthAndVersionHeaders()
    {
        // Arrange
        var handler = new TestHttpMessageHandler(req => JsonResponse("{}"));
        var sut = BuildReviewSut(handler);

        // Act
        await sut.GetPullRequestAsync("my-org", "my-repo", 42, Token, CancellationToken.None);

        // Assert
        var request = handler.LastRequest!;
        request.Method.Should().Be(HttpMethod.Get);
        request.RequestUri!.PathAndQuery.Should().Be("/repos/my-org/my-repo/pulls/42");
        request.Headers.Authorization!.Scheme.Should().Be("Bearer");
        request.Headers.Authorization.Parameter.Should().Be(Token);
        request.Headers.Accept.Should().Contain(h => h.MediaType == "application/vnd.github+json");
        request.Headers.GetValues("X-GitHub-Api-Version").Should().ContainSingle().Which.Should().Be("2022-11-28");
    }

    [Fact]
    public async Task GetPullRequestAsync_GivenApiResponse_Should_MapStateCommitsAndSizeCounters()
    {
        // Arrange
        const string Json = """
            {
              "state": "open", "draft": true, "title": "Add retries",
              "head": { "sha": "headsha1" }, "base": { "sha": "basesha1" },
              "changed_files": 31, "additions": 420, "deletions": 90
            }
            """;
        var sut = BuildReviewSut(new TestHttpMessageHandler(Json));

        // Act
        var result = await sut.GetPullRequestAsync("o", "r", 1, Token, CancellationToken.None);

        // Assert
        result.Should().Be(new PullRequestInfo("open", true, "Add retries", "headsha1", "basesha1", 31, 420, 90));
    }

    [Fact]
    public async Task GetPullRequestAsync_GivenAPullRequest_Should_ReadTheBodyAuthorAndBranchNames()
    {
        // Arrange
        const string Json = """
            {
              "state": "open", "draft": false, "title": "Add retries", "body": "Retries failed calls.",
              "user": { "login": "octocat" },
              "head": { "sha": "headsha1", "ref": "feature/retries" }, "base": { "sha": "basesha1", "ref": "main" },
              "changed_files": 2, "additions": 6, "deletions": 5
            }
            """;
        var sut = BuildReviewSut(new TestHttpMessageHandler(Json));

        // Act
        var result = await sut.GetPullRequestAsync("o", "r", 1, Token, CancellationToken.None);

        // Assert
        result.Should().Be(new PullRequestInfo(
            "open", false, "Add retries", "headsha1", "basesha1", 2, 6, 5,
            Body: "Retries failed calls.", AuthorLogin: "octocat", BaseRef: "main", HeadRef: "feature/retries"));
    }

    [Fact]
    public async Task GetPullRequestAsync_GivenNullTextAndNoUser_Should_TurnEachIntoAnEmptyString()
    {
        // Arrange — GitHub sends a null body for a pull request without a description and a null user for a deleted account
        const string Json = """
            {
              "state": "open", "draft": false, "title": null, "body": null, "user": null,
              "head": { "sha": null, "ref": null }, "base": { "sha": null, "ref": null },
              "changed_files": 0, "additions": 0, "deletions": 0
            }
            """;
        var sut = BuildReviewSut(new TestHttpMessageHandler(Json));

        // Act
        var result = await sut.GetPullRequestAsync("o", "r", 1, Token, CancellationToken.None);

        // Assert
        result.Should().Be(new PullRequestInfo("open", false, string.Empty, string.Empty, string.Empty, 0, 0, 0));
    }

    [Fact]
    public async Task GetPullRequestAsync_GivenAUserWithoutALogin_Should_ReadAnEmptyAuthorLogin()
    {
        // Arrange
        const string Json = """{ "state": "open", "user": { "login": null }, "head": { "sha": "h" }, "base": { "sha": "b" } }""";
        var sut = BuildReviewSut(new TestHttpMessageHandler(Json));

        // Act
        var result = await sut.GetPullRequestAsync("o", "r", 1, Token, CancellationToken.None);

        // Assert
        result.AuthorLogin.Should().BeEmpty();
    }

    [Fact]
    public async Task GetPullRequestAsync_GivenNotFound_Should_ThrowGitHubApiExceptionWith404()
    {
        // Arrange
        var sut = BuildReviewSut(new TestHttpMessageHandler("{\"message\":\"Not Found\"}", HttpStatusCode.NotFound));

        // Act
        var act = () => sut.GetPullRequestAsync("o", "r", 1, Token, CancellationToken.None);

        // Assert
        var exception = (await act.Should().ThrowAsync<GitHubApiException>()).Which;
        exception.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── GetPullRequestFilesAsync ─────────────────────────────────────────────

    [Fact]
    public async Task GetPullRequestFilesAsync_GivenOnePage_Should_MapEveryFileField()
    {
        // Arrange
        const string Json = """
            [
              { "filename": "src/A.cs", "status": "modified", "additions": 3, "deletions": 1, "patch": "@@ -1 +1 @@\n+x" },
              { "filename": "src/New.cs", "status": "renamed", "additions": 0, "deletions": 0, "previous_filename": "src/Old.cs" },
              { "filename": "logo.png", "status": "added", "additions": 0, "deletions": 0 }
            ]
            """;
        var sut = BuildReviewSut(new TestHttpMessageHandler(Json));

        // Act
        var files = await sut.GetPullRequestFilesAsync("o", "r", 7, Token, CancellationToken.None);

        // Assert
        files.Should().Equal(
            new PullRequestFile("src/A.cs", "modified", 3, 1, "@@ -1 +1 @@\n+x"),
            new PullRequestFile("src/New.cs", "renamed", 0, 0, null, "src/Old.cs"),
            new PullRequestFile("logo.png", "added", 0, 0, null));
    }

    [Fact]
    public async Task GetPullRequestFilesAsync_GivenAnyPullRequest_Should_RequestOneHundredFilesPerPageStartingAtPageOne()
    {
        // Arrange
        var handler = new TestHttpMessageHandler(req => JsonResponse("[]"));
        var sut = BuildReviewSut(handler);

        // Act
        await sut.GetPullRequestFilesAsync("my-org", "my-repo", 5, Token, CancellationToken.None);

        // Assert
        handler.Requests.Should().ContainSingle();
        handler.LastRequest!.RequestUri!.PathAndQuery.Should().Be("/repos/my-org/my-repo/pulls/5/files?per_page=100&page=1");
    }

    [Fact]
    public async Task GetPullRequestFilesAsync_GivenAFullFirstPage_Should_FetchTheNextPageAndCombineThem()
    {
        // Arrange
        var handler = new TestHttpMessageHandler(req =>
            req.RequestUri!.Query.EndsWith("&page=1", StringComparison.Ordinal)
                ? JsonResponse(FilesJson(100))
                : JsonResponse(FilesJson(3, startAt: 101)));
        var sut = BuildReviewSut(handler);

        // Act
        var files = await sut.GetPullRequestFilesAsync("o", "r", 1, Token, CancellationToken.None);

        // Assert
        files.Should().HaveCount(103);
        files[100].Path.Should().Be("src/File0101.cs");
        handler.Requests.Select(r => r.RequestUri!.Query).Should().Equal("?per_page=100&page=1", "?per_page=100&page=2");
    }

    [Fact]
    public async Task GetPullRequestFilesAsync_GivenAShortPage_Should_StopWithoutAskingForAnother()
    {
        // Arrange
        var handler = new TestHttpMessageHandler(req => JsonResponse(FilesJson(99)));
        var sut = BuildReviewSut(handler);

        // Act
        var files = await sut.GetPullRequestFilesAsync("o", "r", 1, Token, CancellationToken.None);

        // Assert
        files.Should().HaveCount(99);
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task GetPullRequestFilesAsync_GivenEndlessFullPages_Should_StopAtGitHubsThreeThousandFileCeiling()
    {
        // Arrange — GitHub never returns more than 30 pages of 100 files
        var handler = new TestHttpMessageHandler(req => JsonResponse(FilesJson(100)));
        var sut = BuildReviewSut(handler);

        // Act
        var files = await sut.GetPullRequestFilesAsync("o", "r", 1, Token, CancellationToken.None);

        // Assert
        handler.Requests.Should().HaveCount(30);
        files.Should().HaveCount(3_000);
    }

    [Fact]
    public async Task GetPullRequestFilesAsync_GivenServerError_Should_ThrowGitHubApiExceptionWithTheStatus()
    {
        // Arrange
        var sut = BuildReviewSut(new TestHttpMessageHandler("oops", HttpStatusCode.BadGateway));

        // Act
        var act = () => sut.GetPullRequestFilesAsync("o", "r", 1, Token, CancellationToken.None);

        // Assert
        (await act.Should().ThrowAsync<GitHubApiException>()).Which.StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }

    // ── ListIssueCommentsAsync ───────────────────────────────────────────────

    private static string CommentPage(int count, int startId)
    {
        return JsonSerializer.Serialize(
            Enumerable.Range(startId, count).Select(i => new { id = i, body = $"comment {i}", user = new { login = "octocat" } }));
    }

    [Fact]
    public async Task ListIssueCommentsAsync_GivenTwoPages_Should_ReturnEveryCommentWithItsAuthorAndBody()
    {
        // Arrange
        var handler = new TestHttpMessageHandler(req =>
            req.RequestUri!.Query.EndsWith("&page=1", StringComparison.Ordinal) ? JsonResponse(CommentPage(100, 1)) : JsonResponse(CommentPage(2, 101)));
        var sut = BuildReviewSut(handler);

        // Act
        var comments = await sut.ListIssueCommentsAsync("my-org", "my-service", 7, Token, CancellationToken.None);

        // Assert
        comments.Should().HaveCount(102);
        comments[0].Should().Be(new IssueComment(1, "octocat", "comment 1"));
        comments[101].Should().Be(new IssueComment(102, "octocat", "comment 102"));
        handler.Requests.Select(request => request.RequestUri!.PathAndQuery).Should().Equal(
            "/repos/my-org/my-service/issues/7/comments?per_page=100&page=1",
            "/repos/my-org/my-service/issues/7/comments?per_page=100&page=2");
        handler.Requests.Should().OnlyContain(request => request.Method == HttpMethod.Get);
    }

    [Fact]
    public async Task ListIssueCommentsAsync_GivenACommentFromADeletedUser_Should_KeepItWithAnEmptyLogin()
    {
        // Arrange
        const string Json = """
            [
              { "id": 11, "body": "from a person", "user": { "login": "octocat" } },
              { "id": 12, "body": "from nobody", "user": null },
              { "id": 13, "body": null, "user": { "login": null } }
            ]
            """;
        var sut = BuildReviewSut(new TestHttpMessageHandler(Json));

        // Act
        var comments = await sut.ListIssueCommentsAsync("o", "r", 1, Token, CancellationToken.None);

        // Assert
        comments.Should().Equal(
            new IssueComment(11, "octocat", "from a person"),
            new IssueComment(12, string.Empty, "from nobody"),
            new IssueComment(13, string.Empty, string.Empty));
    }

    [Fact]
    public async Task ListIssueCommentsAsync_GivenAnEmptyPageAfterAFullOne_Should_StopWithoutWarning()
    {
        // Arrange
        var logger = new ListLogger<GitHubService>();
        var handler = new TestHttpMessageHandler(req =>
            req.RequestUri!.Query.EndsWith("&page=1", StringComparison.Ordinal) ? JsonResponse(CommentPage(100, 1)) : JsonResponse("[]"));
        var sut = BuildReviewSut(handler, logger: logger);

        // Act
        var comments = await sut.ListIssueCommentsAsync("o", "r", 1, Token, CancellationToken.None);

        // Assert
        comments.Should().HaveCount(100);
        handler.Requests.Should().HaveCount(2);
        logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task ListIssueCommentsAsync_GivenEveryPageIsFull_Should_StopAtThirtyPagesAndWarnThatTheNewestWereNotRead()
    {
        // Arrange — comments are listed oldest first, so the unread ones are the newest and an earlier review could go unseen
        var logger = new ListLogger<GitHubService>();
        var handler = new TestHttpMessageHandler(_ => JsonResponse(CommentPage(100, 1)));
        var sut = BuildReviewSut(handler, logger: logger);

        // Act
        var comments = await sut.ListIssueCommentsAsync("my-org", "my-repo", 7, Token, CancellationToken.None);

        // Assert
        handler.Requests.Should().HaveCount(30);
        handler.LastRequest!.RequestUri!.Query.Should().EndWith("&page=30");
        comments.Should().HaveCount(3_000);
        var warning = logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Warning).Which;
        warning.Message.Should().Contain("my-org/my-repo#7").And.Contain("3000").And.Contain("may be missed");
    }

    [Fact]
    public async Task ListIssueCommentsAsync_GivenGitHubRefuses_Should_ThrowGitHubApiException()
    {
        // Arrange
        var sut = BuildReviewSut(new TestHttpMessageHandler("{}", HttpStatusCode.Forbidden));

        // Act
        var act = () => sut.ListIssueCommentsAsync("o", "r", 1, Token, CancellationToken.None);

        // Assert
        (await act.Should().ThrowAsync<GitHubApiException>()).Which.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── CreateIssueCommentAsync / UpdateIssueCommentAsync ────────────────────

    [Fact]
    public async Task CreateIssueCommentAsync_GivenABody_Should_PostItAndReturnTheNewId()
    {
        // Arrange
        string? capturedBody = null;
        var handler = new TestHttpMessageHandler(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return JsonResponse("{\"id\": 4821}", HttpStatusCode.Created);
        });
        var sut = BuildReviewSut(handler);

        // Act
        var id = await sut.CreateIssueCommentAsync("my-org", "my-service", 7, "the review", Token, CancellationToken.None);

        // Assert
        id.Should().Be(4821);
        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.RequestUri!.PathAndQuery.Should().Be("/repos/my-org/my-service/issues/7/comments");
        JsonDocument.Parse(capturedBody!).RootElement.GetProperty("body").GetString().Should().Be("the review");
    }

    [Fact]
    public async Task CreateIssueCommentAsync_GivenGitHubRefuses_Should_ThrowGitHubApiException()
    {
        // Arrange
        var sut = BuildReviewSut(new TestHttpMessageHandler("{\"message\":\"Body cannot be blank\"}", HttpStatusCode.UnprocessableEntity));

        // Act
        var act = () => sut.CreateIssueCommentAsync("o", "r", 1, " ", Token, CancellationToken.None);

        // Assert
        (await act.Should().ThrowAsync<GitHubApiException>()).Which.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task UpdateIssueCommentAsync_GivenACommentId_Should_PatchItsBody()
    {
        // Arrange
        string? capturedBody = null;
        var handler = new TestHttpMessageHandler(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return JsonResponse("{\"id\": 99}");
        });
        var sut = BuildReviewSut(handler);

        // Act
        await sut.UpdateIssueCommentAsync("my-org", "my-service", 99, "the new review", Token, CancellationToken.None);

        // Assert
        handler.LastRequest!.Method.Should().Be(HttpMethod.Patch);
        handler.LastRequest.RequestUri!.PathAndQuery.Should().Be("/repos/my-org/my-service/issues/comments/99");
        handler.LastRequest.Headers.Authorization!.Parameter.Should().Be(Token);
        JsonDocument.Parse(capturedBody!).RootElement.GetProperty("body").GetString().Should().Be("the new review");
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task UpdateIssueCommentAsync_GivenGitHubRefuses_Should_ThrowGitHubApiException(HttpStatusCode status)
    {
        // Arrange
        var sut = BuildReviewSut(new TestHttpMessageHandler("{}", status));

        // Act
        var act = () => sut.UpdateIssueCommentAsync("o", "r", 99, "x", Token, CancellationToken.None);

        // Assert
        (await act.Should().ThrowAsync<GitHubApiException>()).Which.StatusCode.Should().Be(status);
    }

    // ── CreateCheckRunAsync ──────────────────────────────────────────────────

    [Theory]
    [InlineData(CheckRunConclusion.Success, "success")]
    [InlineData(CheckRunConclusion.Neutral, "neutral")]
    [InlineData(CheckRunConclusion.Failure, "failure")]
    public async Task CreateCheckRunAsync_GivenAConclusion_Should_PostACompletedCheckRunNamedMoneyMeAiReview(CheckRunConclusion conclusion, string expectedConclusion)
    {
        // Arrange
        const string headSha = "0123456789abcdef0123456789abcdef01234567";
        string? capturedBody = null;
        var handler = new TestHttpMessageHandler(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return JsonResponse("{\"id\": 7001}", HttpStatusCode.Created);
        });
        var sut = BuildReviewSut(handler);
        var checkRun = new CheckRunRequest(headSha, conclusion, "Ready to merge", "All eight checks passed.", "review-key");

        // Act
        var id = await sut.CreateCheckRunAsync("my-org", "my-service", checkRun, Token, CancellationToken.None);

        // Assert
        id.Should().Be(7001);
        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.RequestUri!.PathAndQuery.Should().Be("/repos/my-org/my-service/check-runs");
        var body = JsonDocument.Parse(capturedBody!).RootElement;
        body.GetProperty("name").GetString().Should().Be("MoneyMe AI review");
        body.GetProperty("head_sha").GetString().Should().Be(headSha);
        body.GetProperty("status").GetString().Should().Be("completed");
        body.GetProperty("conclusion").GetString().Should().Be(expectedConclusion);
        body.GetProperty("external_id").GetString().Should().Be("review-key");
        body.GetProperty("output").GetProperty("title").GetString().Should().Be("Ready to merge");
        body.GetProperty("output").GetProperty("summary").GetString().Should().Be("All eight checks passed.");
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task CreateCheckRunAsync_GivenGitHubRefuses_Should_ThrowGitHubApiException(HttpStatusCode status)
    {
        // Arrange
        var sut = BuildReviewSut(new TestHttpMessageHandler("{\"message\":\"Resource not accessible by integration\"}", status));
        var checkRun = new CheckRunRequest("sha", CheckRunConclusion.Success, "title", "summary", "key");

        // Act
        var act = () => sut.CreateCheckRunAsync("o", "r", checkRun, Token, CancellationToken.None);

        // Assert
        (await act.Should().ThrowAsync<GitHubApiException>()).Which.StatusCode.Should().Be(status);
    }
}
