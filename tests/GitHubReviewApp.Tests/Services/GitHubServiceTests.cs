namespace GitHubReviewApp.Tests.Services;

public class GitHubServiceTests
{
    private const string Token = "ghs_installation_token";

    private static GitHubService BuildReviewSut(HttpMessageHandler handler, ReviewOptions? options = null, ILogger<GitHubService>? logger = null)
    {
        return new GitHubService(
            new HttpClient(handler),
            logger ?? NullLogger<GitHubService>.Instance,
            Options.Create(options ?? new ReviewOptions()));
    }

    /// <summary>One full page of reviews, which is how GitHub answers when there may be more to read.</summary>
    private static string FullReviewPage()
    {
        return JsonSerializer.Serialize(
            Enumerable.Range(1, 100).Select(i => new { id = i, state = "COMMENTED", body = "b", commit_id = "c", user = new { login = "octocat" } }));
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

    // ── ListReviewsAsync / DeleteReviewAsync ─────────────────────────────────

    [Fact]
    public async Task ListReviewsAsync_GivenReviews_Should_MapIdStateBodyCommitAndAuthorLogin()
    {
        // Arrange
        const string Json = """
            [
              { "id": 11, "state": "COMMENTED", "body": "hello", "commit_id": "abc", "user": { "login": "my-reviewer[bot]", "type": "Bot" } },
              { "id": 12, "state": "PENDING", "body": null, "commit_id": null, "user": null }
            ]
            """;
        var handler = new TestHttpMessageHandler(Json);
        var sut = BuildReviewSut(handler);

        // Act
        var reviews = await sut.ListReviewsAsync("my-org", "my-repo", 9, Token, CancellationToken.None);

        // Assert
        reviews.Should().Equal(
            new PullRequestReviewSummary(11, "COMMENTED", "hello", "abc", "my-reviewer[bot]"),
            new PullRequestReviewSummary(12, "PENDING", string.Empty, string.Empty, string.Empty));
        handler.LastRequest!.RequestUri!.PathAndQuery.Should().Be("/repos/my-org/my-repo/pulls/9/reviews?per_page=100&page=1");
    }

    [Fact]
    public async Task ListReviewsAsync_GivenAFullPage_Should_ReadTheNextPageToo()
    {
        // Arrange
        string Page(int count, int startId) => JsonSerializer.Serialize(
            Enumerable.Range(startId, count).Select(i => new { id = i, state = "COMMENTED", body = "b", commit_id = "c", user = new { login = "octocat" } }));

        var handler = new TestHttpMessageHandler(req =>
            req.RequestUri!.Query.EndsWith("&page=1", StringComparison.Ordinal) ? JsonResponse(Page(100, 1)) : JsonResponse(Page(2, 101)));
        var sut = BuildReviewSut(handler);

        // Act
        var reviews = await sut.ListReviewsAsync("o", "r", 1, Token, CancellationToken.None);

        // Assert
        reviews.Should().HaveCount(102);
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task ListReviewsAsync_GivenAnEmptyPageAfterAFullOne_Should_StopWithoutWarning()
    {
        // Arrange — exactly one full page of reviews: the second request is how the end is known
        var logger = new ListLogger<GitHubService>();
        var handler = new TestHttpMessageHandler(req =>
            req.RequestUri!.Query.EndsWith("&page=1", StringComparison.Ordinal) ? JsonResponse(FullReviewPage()) : JsonResponse("[]"));
        var sut = BuildReviewSut(handler, logger: logger);

        // Act
        var reviews = await sut.ListReviewsAsync("o", "r", 1, Token, CancellationToken.None);

        // Assert
        reviews.Should().HaveCount(100);
        handler.Requests.Should().HaveCount(2);
        logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task ListReviewsAsync_GivenEveryPageIsFull_Should_StopAtFiftyPagesAndWarnThatTheNewestWereNotRead()
    {
        // Arrange — the newest reviews are on the last page, which is the one never reached, so a review this app already posted could go unseen
        var logger = new ListLogger<GitHubService>();
        var handler = new TestHttpMessageHandler(_ => JsonResponse(FullReviewPage()));
        var sut = BuildReviewSut(handler, logger: logger);

        // Act
        var reviews = await sut.ListReviewsAsync("my-org", "my-repo", 7, Token, CancellationToken.None);

        // Assert
        handler.Requests.Should().HaveCount(50);
        handler.LastRequest!.RequestUri!.Query.Should().EndWith("&page=50");
        reviews.Should().HaveCount(5_000);
        var warning = logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Warning).Which;
        warning.Message.Should().Contain("my-org/my-repo#7").And.Contain("5000").And.Contain("may be missed");
    }

    [Fact]
    public async Task DeleteReviewAsync_GivenReviewId_Should_SendDeleteToTheReviewUrl()
    {
        // Arrange
        var handler = new TestHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK));
        var sut = BuildReviewSut(handler);

        // Act
        await sut.DeleteReviewAsync("my-org", "my-repo", 9, 555, Token, CancellationToken.None);

        // Assert
        handler.LastRequest!.Method.Should().Be(HttpMethod.Delete);
        handler.LastRequest.RequestUri!.PathAndQuery.Should().Be("/repos/my-org/my-repo/pulls/9/reviews/555");
    }

    [Fact]
    public async Task DeleteReviewAsync_GivenTheReviewIsAlreadyGone_Should_NotThrow()
    {
        // Arrange
        var sut = BuildReviewSut(new TestHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.NotFound)));

        // Act
        var act = () => sut.DeleteReviewAsync("o", "r", 1, 2, Token, CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DeleteReviewAsync_GivenServerError_Should_ThrowGitHubApiException()
    {
        // Arrange
        var sut = BuildReviewSut(new TestHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        // Act
        var act = () => sut.DeleteReviewAsync("o", "r", 1, 2, Token, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<GitHubApiException>();
    }

    // ── PostReviewAsync (submission) ─────────────────────────────────────────

    private static async Task<(JsonElement Body, HttpRequestMessage Request)> PostAndCaptureAsync(ReviewSubmission submission)
    {
        string? capturedBody = null;
        var handler = new TestHttpMessageHandler(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return JsonResponse("{}", HttpStatusCode.OK);
        });
        var sut = BuildReviewSut(handler);

        await sut.PostReviewAsync("my-org", "my-repo", 9, submission, Token, CancellationToken.None);

        return (JsonDocument.Parse(capturedBody!).RootElement.Clone(), handler.LastRequest!);
    }

    [Fact]
    public async Task PostReviewAsync_GivenSubmission_Should_PostToTheReviewsEndpointWithBearerToken()
    {
        // Arrange
        var submission = new ReviewSubmission("sha1", "body text", []);

        // Act
        var (_, request) = await PostAndCaptureAsync(submission);

        // Assert
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri!.PathAndQuery.Should().Be("/repos/my-org/my-repo/pulls/9/reviews");
        request.Headers.Authorization!.Parameter.Should().Be(Token);
    }

    [Fact]
    public async Task PostReviewAsync_GivenSubmission_Should_TieTheReviewToTheCommitAndPostItAsANonBlockingComment()
    {
        // Arrange
        var submission = new ReviewSubmission("abc123", "the body", []);

        // Act
        var (body, _) = await PostAndCaptureAsync(submission);

        // Assert
        body.GetProperty("commit_id").GetString().Should().Be("abc123");
        body.GetProperty("body").GetString().Should().Be("the body");
        body.GetProperty("event").GetString().Should().Be("COMMENT");
    }

    [Fact]
    public async Task PostReviewAsync_GivenNoInlineComments_Should_OmitTheCommentsProperty()
    {
        // Arrange
        var submission = new ReviewSubmission("sha", "body", []);

        // Act
        var (body, _) = await PostAndCaptureAsync(submission);

        // Assert
        body.TryGetProperty("comments", out _).Should().BeFalse();
    }

    [Fact]
    public async Task PostReviewAsync_GivenInlineComments_Should_SendEachOnTheRightSideOfTheDiff()
    {
        // Arrange
        var submission = new ReviewSubmission("sha", "body",
        [
            new InlineReviewComment("src/Foo.cs", 42, "first"),
            new InlineReviewComment("src/Bar.cs", 7, "second")
        ]);

        // Act
        var (body, _) = await PostAndCaptureAsync(submission);

        // Assert
        var comments = body.GetProperty("comments").EnumerateArray().ToList();
        comments.Should().HaveCount(2);
        comments[0].GetProperty("path").GetString().Should().Be("src/Foo.cs");
        comments[0].GetProperty("line").GetInt32().Should().Be(42);
        comments[0].GetProperty("side").GetString().Should().Be("RIGHT");
        comments[0].GetProperty("body").GetString().Should().Be("first");
        comments[1].GetProperty("path").GetString().Should().Be("src/Bar.cs");
    }

    [Fact]
    public async Task PostReviewAsync_GivenValidationFailure_Should_ThrowGitHubApiExceptionWith422AndGitHubsExplanation()
    {
        // Arrange
        var sut = BuildReviewSut(new TestHttpMessageHandler(
            "{\"message\":\"Unprocessable Entity\",\"errors\":[\"Line must be part of the diff\"]}",
            HttpStatusCode.UnprocessableEntity));

        // Act
        var act = () => sut.PostReviewAsync("o", "r", 1, new ReviewSubmission("sha", "b", []), Token, CancellationToken.None);

        // Assert
        var exception = (await act.Should().ThrowAsync<GitHubApiException>()).Which;
        exception.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        exception.Message.Should().Contain("Line must be part of the diff");
    }

    // ── PostCommentAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task PostCommentAsync_GivenBody_Should_PostToTheIssueCommentsEndpoint()
    {
        // Arrange
        string? capturedBody = null;
        var handler = new TestHttpMessageHandler(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return JsonResponse("{}", HttpStatusCode.Created);
        });
        var sut = BuildReviewSut(handler);

        // Act
        await sut.PostCommentAsync("my-org", "my-repo", 9, "the notice", Token, CancellationToken.None);

        // Assert
        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.RequestUri!.PathAndQuery.Should().Be("/repos/my-org/my-repo/issues/9/comments");
        JsonDocument.Parse(capturedBody!).RootElement.GetProperty("body").GetString().Should().Be("the notice");
    }

    [Fact]
    public async Task PostCommentAsync_GivenForbidden_Should_ThrowGitHubApiException()
    {
        // Arrange
        var sut = BuildReviewSut(new TestHttpMessageHandler("{}", HttpStatusCode.Forbidden));

        // Act
        var act = () => sut.PostCommentAsync("o", "r", 1, "x", Token, CancellationToken.None);

        // Assert
        (await act.Should().ThrowAsync<GitHubApiException>()).Which.StatusCode.Should().Be(HttpStatusCode.Forbidden);
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

    // ── GetRepositoryGuidelinesAsync ─────────────────────────────────────────

    private static HttpResponseMessage RawResponse(string text)
    {
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/plain") };
    }

    [Fact]
    public async Task GetRepositoryGuidelinesAsync_GivenTheFirstConfiguredFileExists_Should_ReturnItsTextFromTheGivenCommit()
    {
        // Arrange
        var handler = new TestHttpMessageHandler(req => RawResponse("  Be strict about null checks.  \n"));
        var sut = BuildReviewSut(handler);

        // Act
        var guidelines = await sut.GetRepositoryGuidelinesAsync("my-org", "my-repo", "basesha", Token, CancellationToken.None);

        // Assert
        guidelines.Should().Be(new RepositoryGuidelines("Be strict about null checks.", IsUnreadable: false));
        handler.Requests.Should().ContainSingle();
        handler.LastRequest!.RequestUri!.PathAndQuery.Should().Be("/repos/my-org/my-repo/contents/.github/ai-review.md?ref=basesha");
        handler.LastRequest.Headers.Accept.Should().Contain(h => h.MediaType == "application/vnd.github.raw+json");
    }

    [Fact]
    public async Task GetRepositoryGuidelinesAsync_GivenTheFirstFileIsMissing_Should_FallBackToTheNextConfiguredFile()
    {
        // Arrange
        var handler = new TestHttpMessageHandler(req =>
            req.RequestUri!.AbsolutePath.EndsWith("/ai-review.md", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : RawResponse("From CLAUDE.md"));
        var sut = BuildReviewSut(handler);

        // Act
        var guidelines = await sut.GetRepositoryGuidelinesAsync("o", "r", "sha", Token, CancellationToken.None);

        // Assert
        guidelines.Text.Should().Be("From CLAUDE.md");
        handler.Requests.Select(r => r.RequestUri!.AbsolutePath).Should().Equal(
            "/repos/o/r/contents/.github/ai-review.md",
            "/repos/o/r/contents/CLAUDE.md");
    }

    [Fact]
    public async Task GetRepositoryGuidelinesAsync_GivenNoConfiguredFileExists_Should_ReturnNoneWhichIsNotAFailure()
    {
        // Arrange
        var handler = new TestHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.NotFound));
        var sut = BuildReviewSut(handler);

        // Act
        var guidelines = await sut.GetRepositoryGuidelinesAsync("o", "r", "sha", Token, CancellationToken.None);

        // Assert
        guidelines.Should().Be(RepositoryGuidelines.None);
        handler.Requests.Should().HaveCount(2, "both configured paths were tried");
    }

    [Fact]
    public async Task GetRepositoryGuidelinesAsync_GivenAnEmptyFile_Should_TryTheNextOne()
    {
        // Arrange
        var handler = new TestHttpMessageHandler(req =>
            req.RequestUri!.AbsolutePath.EndsWith("/ai-review.md", StringComparison.Ordinal) ? RawResponse("  \n ") : RawResponse("Real guidelines"));
        var sut = BuildReviewSut(handler);

        // Act
        var guidelines = await sut.GetRepositoryGuidelinesAsync("o", "r", "sha", Token, CancellationToken.None);

        // Assert
        guidelines.Text.Should().Be("Real guidelines");
    }

    [Fact]
    public async Task GetRepositoryGuidelinesAsync_GivenThePermissionIsRefused_Should_ReportTheGuidelinesAsUnreadableAfterOneRequest()
    {
        // Arrange — the app may not have been granted contents access; that must never fail a review, but the review must say the rules were not applied
        var handler = new TestHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var sut = BuildReviewSut(handler);

        // Act
        var guidelines = await sut.GetRepositoryGuidelinesAsync("o", "r", "sha", Token, CancellationToken.None);

        // Assert
        guidelines.Should().Be(RepositoryGuidelines.Unreadable);
        handler.Requests.Should().ContainSingle("no point trying other paths without permission");
    }

    [Fact]
    public async Task GetRepositoryGuidelinesAsync_GivenTheTokenIsNoLongerAccepted_Should_ThrowSoTheMessageIsRetriedWithANewOneInsteadOfReportingTheRulesUnreadable()
    {
        // Arrange — a 401 is a token that has expired or been revoked, not a permission the app lacks: a review that went on without the rules
        // would claim they could not be read, when a new token would read them
        var handler = new TestHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var sut = BuildReviewSut(handler);

        // Act
        var act = () => sut.GetRepositoryGuidelinesAsync("o", "r", "sha", Token, CancellationToken.None);

        // Assert
        (await act.Should().ThrowAsync<GitHubApiException>()).Which.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task GetRepositoryGuidelinesAsync_GivenGitHubIsUnavailable_Should_ThrowSoTheMessageIsRetriedBeforeAnyModelCall(HttpStatusCode status)
    {
        // Arrange — carrying on would post a review that quietly ignored the repository's rules
        var sut = BuildReviewSut(new TestHttpMessageHandler(req => new HttpResponseMessage(status)));

        // Act
        var act = () => sut.GetRepositoryGuidelinesAsync("o", "r", "sha", Token, CancellationToken.None);

        // Assert
        (await act.Should().ThrowAsync<GitHubApiException>()).Which.StatusCode.Should().Be(status);
    }

    [Fact]
    public async Task GetRepositoryGuidelinesAsync_GivenARateLimit_Should_ThrowARateLimitedExceptionSoTheMessageIsRetried()
    {
        // Arrange
        var handler = new TestHttpMessageHandler(req =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return response;
        });
        var sut = BuildReviewSut(handler);

        // Act
        var act = () => sut.GetRepositoryGuidelinesAsync("o", "r", "sha", Token, CancellationToken.None);

        // Assert
        (await act.Should().ThrowAsync<GitHubApiException>()).Which.IsRateLimited.Should().BeTrue();
    }

    [Fact]
    public async Task GetRepositoryGuidelinesAsync_GivenANetworkFailure_Should_Throw()
    {
        // Arrange
        var failingHandler = new TestHttpMessageHandler(
            (Func<HttpRequestMessage, HttpResponseMessage>)(req => throw new HttpRequestException("connection reset")));
        var sut = BuildReviewSut(failingHandler);

        // Act
        var act = () => sut.GetRepositoryGuidelinesAsync("o", "r", "sha", Token, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task GetRepositoryGuidelinesAsync_GivenTheRequestTimesOut_Should_ThrowSoTheMessageIsRetried()
    {
        // Arrange — an HttpClient timeout surfaces as a TaskCanceledException while the caller's own token is not cancelled
        var failingHandler = new TestHttpMessageHandler(
            (Func<HttpRequestMessage, HttpResponseMessage>)(req => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.")));
        var sut = BuildReviewSut(failingHandler);

        // Act
        var act = () => sut.GetRepositoryGuidelinesAsync("o", "r", "sha", Token, CancellationToken.None);

        // Assert
        var thrown = (await act.Should().ThrowAsync<TaskCanceledException>()).Which;
        FailureClassifier.IsTransient(thrown).Should().BeTrue();
    }

    [Fact]
    public async Task GetRepositoryGuidelinesAsync_GivenACancelledToken_Should_StillThrowSoShutdownIsNotSwallowed()
    {
        // Arrange
        var sut = BuildReviewSut(new TestHttpMessageHandler(req => RawResponse("x")));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // Act
        var act = () => sut.GetRepositoryGuidelinesAsync("o", "r", "sha", Token, cancellation.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetRepositoryGuidelinesAsync_GivenTextLongerThanTheLimit_Should_CutItAndSaySo()
    {
        // Arrange
        var handler = new TestHttpMessageHandler(req => RawResponse(new string('g', 5_000)));
        var sut = BuildReviewSut(handler, new ReviewOptions { MaxGuidelinesChars = 1_000 });

        // Act
        var guidelines = await sut.GetRepositoryGuidelinesAsync("o", "r", "sha", Token, CancellationToken.None);

        // Assert
        guidelines.Text.Should().StartWith(new string('g', 1_000)).And.Contain("[guidelines truncated]");
        guidelines.Text!.Length.Should().BeLessThan(1_100);
    }

    [Fact]
    public async Task GetRepositoryGuidelinesAsync_GivenTextExactlyAtTheLimit_Should_ReturnAllOfItWithoutSayingItWasCut()
    {
        // Arrange — the limit is the longest text that is kept whole
        var handler = new TestHttpMessageHandler(req => RawResponse(new string('g', 1_000)));
        var sut = BuildReviewSut(handler, new ReviewOptions { MaxGuidelinesChars = 1_000 });

        // Act
        var guidelines = await sut.GetRepositoryGuidelinesAsync("o", "r", "sha", Token, CancellationToken.None);

        // Assert
        guidelines.Text.Should().Be(new string('g', 1_000));
    }

    [Fact]
    public async Task GetRepositoryGuidelinesAsync_GivenTextOneCharacterOverTheLimit_Should_DropThatCharacterAndSaySo()
    {
        // Arrange
        var handler = new TestHttpMessageHandler(req => RawResponse(new string('g', 1_001)));
        var sut = BuildReviewSut(handler, new ReviewOptions { MaxGuidelinesChars = 1_000 });

        // Act
        var guidelines = await sut.GetRepositoryGuidelinesAsync("o", "r", "sha", Token, CancellationToken.None);

        // Assert
        guidelines.Text.Should().Be(new string('g', 1_000) + "\n\n[guidelines truncated]");
    }

    [Fact]
    public async Task GetRepositoryGuidelinesAsync_GivenAFileFarLargerThanTheLimit_Should_StopReadingNearTheLimitInsteadOfBufferingTheWholeFile()
    {
        // Arrange — the file is the repository's own, so its size is not the app's to trust; the cap must apply while reading and not after
        var file = new EndlessZeroStream(100L * 1024 * 1024);
        var handler = new TestHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(file) });
        var sut = BuildReviewSut(handler, new ReviewOptions { MaxGuidelinesChars = 1_000 });

        // Act
        var guidelines = await sut.GetRepositoryGuidelinesAsync("o", "r", "sha", Token, CancellationToken.None);

        // Assert
        guidelines.Text.Should().EndWith("[guidelines truncated]");
        file.BytesRead.Should().BeLessThan(1_048_576);
    }

    [Fact]
    public async Task GetRepositoryGuidelinesAsync_GivenNoConfiguredPaths_Should_ReturnNoneWithoutCallingGitHub()
    {
        // Arrange
        var handler = new TestHttpMessageHandler(req => RawResponse("x"));
        var sut = BuildReviewSut(handler, new ReviewOptions { GuidelinePaths = [] });

        // Act
        var guidelines = await sut.GetRepositoryGuidelinesAsync("o", "r", "sha", Token, CancellationToken.None);

        // Assert
        guidelines.Should().Be(RepositoryGuidelines.None);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRepositoryGuidelinesAsync_GivenPathAndRefWithSpecialCharacters_Should_EscapeThemInTheUrl()
    {
        // Arrange
        var handler = new TestHttpMessageHandler(req => RawResponse("x"));
        var sut = BuildReviewSut(handler, new ReviewOptions { GuidelinePaths = ["docs/my guide#1.md"] });

        // Act
        await sut.GetRepositoryGuidelinesAsync("o", "r", "feature/a b", Token, CancellationToken.None);

        // Assert
        handler.LastRequest!.RequestUri!.AbsoluteUri.Should().EndWith("/contents/docs/my%20guide%231.md?ref=feature%2Fa%20b");
    }
}
