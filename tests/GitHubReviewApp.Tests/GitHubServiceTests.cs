// =============================================================
// SUT: GitHubService (Services/GitHubService.cs)
// Covered members: GetPullRequestDiffAsync, PostReviewAsync
//
// Uncovered lines: none — all reachable branches are covered.
// =============================================================

namespace GitHubReviewApp.Tests;

public class GitHubServiceTests
{
    // ── GetPullRequestDiffAsync ──────────────────────────────────────────────

    [Fact]
    public async Task GetPullRequestDiffAsync_GivenValidRequest_Should_SetAcceptHeaderToGitHubDiffMediaType()
    {
        // Arrange
        var handler = new TestHttpMessageHandler(req =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("diff content", Encoding.UTF8, "text/plain")
            });
        var sut = BuildSut(handler);

        // Act
        await sut.GetPullRequestDiffAsync("owner", "repo", 1, "test-token");

        // Assert
        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Headers.Accept
            .Should().Contain(h => h.MediaType == "application/vnd.github.v3.diff");
    }

    [Fact]
    public async Task GetPullRequestDiffAsync_GivenValidRequest_Should_SetAuthorizationBearerHeader()
    {
        // Arrange
        const string Token = "ghp_test_token_abc123";
        var handler = new TestHttpMessageHandler(req =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("diff", Encoding.UTF8, "text/plain")
            });
        var sut = BuildSut(handler);

        // Act
        await sut.GetPullRequestDiffAsync("owner", "repo", 42, Token);

        // Assert
        handler.LastRequest!.Headers.Authorization.Should().NotBeNull();
        handler.LastRequest.Headers.Authorization!.Scheme.Should().Be("Bearer");
        handler.LastRequest.Headers.Authorization.Parameter.Should().Be(Token);
    }

    [Fact]
    public async Task GetPullRequestDiffAsync_GivenValidRequest_Should_RequestCorrectPullRequestUrl()
    {
        // Arrange
        var handler = new TestHttpMessageHandler(req =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("diff", Encoding.UTF8, "text/plain")
            });
        var sut = BuildSut(handler);

        // Act
        await sut.GetPullRequestDiffAsync("myorg", "myrepo", 99, "token");

        // Assert
        handler.LastRequest!.RequestUri!.PathAndQuery.Should().Be("/repos/myorg/myrepo/pulls/99");
    }

    [Fact]
    public async Task GetPullRequestDiffAsync_GivenDiffWithinLimit_Should_ReturnFullDiff()
    {
        // Arrange
        var shortDiff = new string('x', 1_000);   // well under the 80,000 byte limit
        var handler = new TestHttpMessageHandler(req =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(shortDiff, Encoding.UTF8, "text/plain")
            });
        var sut = BuildSut(handler);

        // Act
        var result = await sut.GetPullRequestDiffAsync("owner", "repo", 1, "token");

        // Assert
        result.Should().Be(shortDiff);
        result.Should().NotContain("[diff truncated");
    }

    [Fact]
    public async Task GetPullRequestDiffAsync_GivenDiffExceedsMaxBytes_Should_TruncateDiffAndAppendNotice()
    {
        // Arrange — diff is MaxDiffBytes + 500 characters
        const int MaxDiffBytes = 80_000;
        var oversizedDiff = new string('a', MaxDiffBytes + 500);
        var handler = new TestHttpMessageHandler(req =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(oversizedDiff, Encoding.UTF8, "text/plain")
            });
        var sut = BuildSut(handler);

        // Act
        var result = await sut.GetPullRequestDiffAsync("owner", "repo", 1, "token");

        // Assert
        const string TruncationNotice = "\n\n[diff truncated — too large for review]";
        result.Should().HaveLength(MaxDiffBytes + TruncationNotice.Length);
        result.Should().EndWith(TruncationNotice);
        result[..MaxDiffBytes].Should().Be(new string('a', MaxDiffBytes));
    }

    [Fact]
    public async Task GetPullRequestDiffAsync_GivenDiffExactlyAtLimit_Should_NotTruncate()
    {
        // Arrange — exactly at the boundary (not over)
        const int MaxDiffBytes = 80_000;
        var exactDiff = new string('b', MaxDiffBytes);
        var handler = new TestHttpMessageHandler(req =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(exactDiff, Encoding.UTF8, "text/plain")
            });
        var sut = BuildSut(handler);

        // Act
        var result = await sut.GetPullRequestDiffAsync("owner", "repo", 1, "token");

        // Assert
        result.Should().Be(exactDiff);
        result.Should().NotContain("[diff truncated");
    }

    [Fact]
    public async Task GetPullRequestDiffAsync_GivenNonSuccessStatusCode_Should_ThrowHttpRequestException()
    {
        // Arrange
        var handler = new TestHttpMessageHandler(req =>
            new HttpResponseMessage(HttpStatusCode.NotFound));
        var sut = BuildSut(handler);

        // Act
        var act = () => sut.GetPullRequestDiffAsync("owner", "repo", 1, "token");

        // Assert
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    // ── PostReviewAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task PostReviewAsync_GivenValidRequest_Should_PostToCorrectUrl()
    {
        // Arrange
        var handler = new TestHttpMessageHandler(req =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
        var sut = BuildSut(handler);

        // Act
        await sut.PostReviewAsync("acme-corp", "my-service", 7, "Great PR!", "token");

        // Assert
        handler.LastRequest!.RequestUri!.PathAndQuery
            .Should().Be("/repos/acme-corp/my-service/pulls/7/reviews");
        handler.LastRequest.Method.Should().Be(HttpMethod.Post);
    }

    [Fact]
    public async Task PostReviewAsync_GivenValidRequest_Should_SetAuthorizationBearerHeader()
    {
        // Arrange
        const string Token = "ghs_review_token_xyz";
        var handler = new TestHttpMessageHandler(req =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
        var sut = BuildSut(handler);

        // Act
        await sut.PostReviewAsync("owner", "repo", 1, "review body", Token);

        // Assert
        handler.LastRequest!.Headers.Authorization!.Scheme.Should().Be("Bearer");
        handler.LastRequest.Headers.Authorization.Parameter.Should().Be(Token);
    }

    [Fact]
    public async Task PostReviewAsync_GivenReviewBody_Should_SendBodyTextInRequestPayload()
    {
        // Arrange
        const string ReviewBody = "## Code Review\n\nLooks good overall!";
        string? capturedBody = null;

        var handler = new TestHttpMessageHandler(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        });

        var sut = BuildSut(handler);

        // Act
        await sut.PostReviewAsync("owner", "repo", 1, ReviewBody, "token");

        // Assert
        capturedBody.Should().NotBeNull();
        var parsed = JsonDocument.Parse(capturedBody!).RootElement;
        parsed.GetProperty("body").GetString().Should().Be(ReviewBody);
    }

    [Fact]
    public async Task PostReviewAsync_GivenNonSuccessStatusCode_Should_ThrowHttpRequestException()
    {
        // Arrange
        var handler = new TestHttpMessageHandler(req =>
            new HttpResponseMessage(HttpStatusCode.UnprocessableEntity));
        var sut = BuildSut(handler);

        // Act
        var act = () => sut.PostReviewAsync("owner", "repo", 1, "body", "token");

        // Assert
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    // ── Factory helper ───────────────────────────────────────────────────────

    private static GitHubService BuildSut(TestHttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler);
        return new GitHubService(httpClient, NullLogger<GitHubService>.Instance);
    }
}
