namespace GitHubReviewApp.Tests.Integration;

/// <summary>
/// The fake GitHub is what the integration tests judge the app by, so it is itself checked: it must refuse what GitHub refuses, or a test of the
/// app would pass for the wrong reason. Each case is a refusal that GitHub documents or that was seen from the real API.
/// </summary>
public class FakeBackendTests
{
    private static readonly string _reviewsPath = $"/repos/{FakeBackend.Owner}/{FakeBackend.Repo}/pulls/{FakeBackend.PrNumber}/reviews";

    private static HttpClient ClientFor(FakeBackend backend)
    {
        var client = new HttpClient(backend) { BaseAddress = new Uri("https://api.github.com") };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("fake-backend-tests");
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", FakeBackend.InstallationToken);

        return client;
    }

    private static StringContent Review(object[]? comments = null, string commitId = FakeBackend.HeadSha, string? body = "A review.", string reviewEvent = "COMMENT")
    {
        var json = JsonSerializer.Serialize(new { commit_id = commitId, body, @event = reviewEvent, comments = comments ?? Array.Empty<object>() });

        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    private static object Comment(string path = "src/Foo.cs", int line = 11, string side = "RIGHT", string body = "A comment.")
    {
        return new { path, line, side, body };
    }

    [Fact]
    public async Task PostReview_GivenAValidReviewWithAnInlineCommentOnAnAddedLine_Should_BeAccepted()
    {
        // Arrange
        var backend = new FakeBackend();
        var client = ClientFor(backend);

        // Act
        var response = await client.PostAsync(_reviewsPath, Review([Comment(line: 11)]));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(12)]
    [InlineData(13)]
    public async Task PostReview_GivenAnInlineCommentOnAnUnchangedLineInsideTheHunk_Should_BeAccepted(int line)
    {
        // Arrange — GitHub lets a comment sit on the context lines of a hunk as well as on the added ones
        var client = ClientFor(new FakeBackend());

        // Act
        var response = await client.PostAsync(_reviewsPath, Review([Comment(line: line)]));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(14)]
    [InlineData(0)]
    [InlineData(500)]
    public async Task PostReview_GivenAnInlineCommentOnALineOutsideTheDiff_Should_RefuseTheWholeReviewWith422(int line)
    {
        // Arrange — the hunk header says the new side runs from line 10 to line 13
        var client = ClientFor(new FakeBackend());

        // Act
        var response = await client.PostAsync(_reviewsPath, Review([Comment(line: line)]));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync()).Should().Contain("must be part of the diff");
    }

    [Fact]
    public async Task PostReview_GivenAnInlineCommentOnAFileThatIsNotInThePullRequest_Should_RefuseWith422()
    {
        // Arrange
        var client = ClientFor(new FakeBackend());

        // Act
        var response = await client.PostAsync(_reviewsPath, Review([Comment(path: "src/Missing.cs")]));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Path could not be resolved");
    }

    [Fact]
    public async Task PostReview_GivenAnInlineCommentOnTheLeftSide_Should_RefuseBecauseOnlyTheNewSideIsModelled()
    {
        // Arrange
        var client = ClientFor(new FakeBackend());

        // Act
        var response = await client.PostAsync(_reviewsPath, Review([Comment(side: "LEFT")]));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task PostReview_GivenAnInlineCommentWithNoBody_Should_RefuseWith422()
    {
        // Arrange
        var client = ClientFor(new FakeBackend());

        // Act
        var response = await client.PostAsync(_reviewsPath, Review([Comment(body: "  ")]));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task PostReview_GivenACommitThatIsNotTheHeadOfThePullRequest_Should_RefuseWith422()
    {
        // Arrange
        var client = ClientFor(new FakeBackend());

        // Act
        var response = await client.PostAsync(_reviewsPath, Review(commitId: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Theory]
    [InlineData(65_536, HttpStatusCode.OK)]
    [InlineData(65_537, HttpStatusCode.UnprocessableEntity)]
    public async Task PostReview_GivenABodyAtAndOverTheLimit_Should_AcceptOnlyWhatGitHubAccepts(int length, HttpStatusCode expected)
    {
        // Arrange
        var client = ClientFor(new FakeBackend());

        // Act
        var response = await client.PostAsync(_reviewsPath, Review(body: new string('x', length)));

        // Assert
        response.StatusCode.Should().Be(expected);
    }

    [Fact]
    public async Task PostReview_GivenAnEventGitHubDoesNotKnow_Should_RefuseWith422()
    {
        // Arrange
        var client = ClientFor(new FakeBackend());

        // Act
        var response = await client.PostAsync(_reviewsPath, Review(reviewEvent: "SHIP_IT"));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task DeleteReview_GivenAReviewThatWasSubmitted_Should_RefuseWith422BecauseOnlyADraftCanBeDeleted()
    {
        // Arrange
        var backend = new FakeBackend { ReviewsResponse = """[{"id":41,"state":"COMMENTED","body":"b","commit_id":"c","user":{"login":"someone","type":"User"}}]""" };
        var client = ClientFor(backend);

        // Act
        var response = await client.DeleteAsync($"{_reviewsPath}/41");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync()).Should().Contain("non-pending");
    }

    [Fact]
    public async Task DeleteReview_GivenAReviewThatDoesNotExist_Should_Answer404()
    {
        // Arrange
        var client = ClientFor(new FakeBackend());

        // Act
        var response = await client.DeleteAsync($"{_reviewsPath}/999");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteReview_GivenADraft_Should_RemoveItFromTheNextListOfReviews()
    {
        // Arrange
        var backend = new FakeBackend { ReviewsResponse = """[{"id":41,"state":"PENDING","body":"","commit_id":"","user":{"login":"claude-test[bot]","type":"Bot"}}]""" };
        var client = ClientFor(backend);

        // Act
        var deletion = await client.DeleteAsync($"{_reviewsPath}/41");
        var list = await client.GetStringAsync($"{_reviewsPath}?per_page=100&page=1");

        // Assert
        deletion.StatusCode.Should().Be(HttpStatusCode.OK);
        list.Should().NotContain("\"id\":41");
    }

    [Theory]
    [InlineData("{\"body\":\"  \"}", HttpStatusCode.UnprocessableEntity)]
    [InlineData("{\"body\":\"A notice.\"}", HttpStatusCode.Created)]
    public async Task PostIssueComment_GivenABlankOrARealBody_Should_RefuseOrAcceptAsGitHubDoes(string json, HttpStatusCode expected)
    {
        // Arrange
        var client = ClientFor(new FakeBackend());

        // Act
        var response = await client.PostAsync(
            $"/repos/{FakeBackend.Owner}/{FakeBackend.Repo}/issues/{FakeBackend.PrNumber}/comments",
            new StringContent(json, Encoding.UTF8, "application/json"));

        // Assert
        response.StatusCode.Should().Be(expected);
    }
}
