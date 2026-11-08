namespace GitHubReviewApp.Tests.Integration;

/// <summary>
/// The fake GitHub is what the integration tests judge the app by, so it is itself checked: it must refuse what GitHub refuses, or a test of the
/// app would pass for the wrong reason. Each case is a refusal that GitHub documents or that was seen from the real API.
/// </summary>
public class FakeBackendTests
{
    private const string ChecksPermissions = """{"pull_requests":"write","checks":"write"}""";
    private const string PullRequestsPermission = """{"pull_requests":"write"}""";

    private static readonly string _commentsPath = $"/repos/{FakeBackend.Owner}/{FakeBackend.Repo}/issues/{FakeBackend.PrNumber}/comments";
    private static readonly string _checkRunsPath = $"/repos/{FakeBackend.Owner}/{FakeBackend.Repo}/check-runs";
    private static readonly string _tokenPath = $"/app/installations/{FakeBackend.InstallationId}/access_tokens";
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

    private static StringContent JsonBody(object body)
    {
        return new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
    }

    /// <summary>A request for an installation token, signed with the test key as GitHub requires of an app.</summary>
    private static HttpRequestMessage TokenRequest(string permissionsJson)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.github.com" + _tokenPath)
        {
            Content = new StringContent("{\"permissions\":" + permissionsJson + "}", Encoding.UTF8, "application/json")
        };
        request.Headers.UserAgent.ParseAdd("fake-backend-tests");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", GitHubAppJwt.Sign(FakeBackend.AppId, RsaKeyHelper.PrivateKeyPem));

        return request;
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

    [Fact]
    public async Task ListIssueComments_GivenEarlierAndPostedComments_Should_ListTheEarlierOnesFirstWithTheAppAsAuthorOfThePostedOne()
    {
        // Arrange
        var backend = new FakeBackend();
        backend.InitialComments.Add(new IssueComment(11, "octocat", "An earlier comment."));
        backend.InitialComments.Add(new IssueComment(12, string.Empty, "A comment from a deleted account."));
        var client = ClientFor(backend);
        await client.PostAsync(_commentsPath, JsonBody(new { body = "The review." }));

        // Act
        var json = await client.GetStringAsync($"{_commentsPath}?per_page=100&page=1");

        // Assert
        var comments = JsonNode.Parse(json)!.AsArray();
        comments.Select(comment => comment!["body"]!.GetValue<string>()).Should().Equal(
            "An earlier comment.", "A comment from a deleted account.", "The review.");
        comments[0]!["user"]!["login"]!.GetValue<string>().Should().Be("octocat");
        comments[1]!["user"].Should().BeNull();
        comments[2]!["user"]!["login"]!.GetValue<string>().Should().Be(FakeBackend.AppLogin);
    }

    [Fact]
    public async Task ListIssueComments_GivenMoreThanOnePageOfComments_Should_HandOutOneHundredPerPage()
    {
        // Arrange
        var backend = new FakeBackend();
        backend.InitialComments.AddRange(Enumerable.Range(1, 101).Select(id => new IssueComment(id, "octocat", $"Comment {id}")));
        var client = ClientFor(backend);

        // Act
        var firstPage = JsonNode.Parse(await client.GetStringAsync($"{_commentsPath}?per_page=100&page=1"))!.AsArray();
        var secondPage = JsonNode.Parse(await client.GetStringAsync($"{_commentsPath}?per_page=100&page=2"))!.AsArray();

        // Assert
        firstPage.Should().HaveCount(100);
        secondPage.Should().ContainSingle().Which!["id"]!.GetValue<long>().Should().Be(101);
    }

    [Fact]
    public async Task PostIssueComment_GivenTwoComments_Should_GiveEachItsOwnId()
    {
        // Arrange
        var client = ClientFor(new FakeBackend());

        // Act
        var first = await client.PostAsync(_commentsPath, JsonBody(new { body = "One." }));
        var second = await client.PostAsync(_commentsPath, JsonBody(new { body = "Two." }));

        // Assert
        var firstId = JsonNode.Parse(await first.Content.ReadAsStringAsync())!["id"]!.GetValue<long>();
        var secondId = JsonNode.Parse(await second.Content.ReadAsStringAsync())!["id"]!.GetValue<long>();
        secondId.Should().NotBe(firstId);
    }

    [Fact]
    public async Task PatchIssueComment_GivenAKnownComment_Should_ChangeItsBodyInTheNextListAndRecordTheUpdate()
    {
        // Arrange
        var backend = new FakeBackend();
        backend.InitialComments.Add(new IssueComment(41, FakeBackend.AppLogin, "The old review."));
        var client = ClientFor(backend);

        // Act
        var response = await client.PatchAsync(
            $"/repos/{FakeBackend.Owner}/{FakeBackend.Repo}/issues/comments/41", JsonBody(new { body = "The new review." }));
        var list = JsonNode.Parse(await client.GetStringAsync($"{_commentsPath}?per_page=100&page=1"))!.AsArray();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        list.Should().ContainSingle().Which!["body"]!.GetValue<string>().Should().Be("The new review.");
        backend.UpdatedComments.Should().ContainSingle().Which.BodyJson().GetProperty("body").GetString().Should().Be("The new review.");
    }

    [Fact]
    public async Task PatchIssueComment_GivenACommentThatDoesNotExist_Should_RefuseWith404()
    {
        // Arrange
        var client = ClientFor(new FakeBackend());

        // Act
        var response = await client.PatchAsync(
            $"/repos/{FakeBackend.Owner}/{FakeBackend.Repo}/issues/comments/404", JsonBody(new { body = "Nobody's comment." }));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreateCheckRun_GivenARunForTheHeadCommit_Should_RecordItAndReturnAnId()
    {
        // Arrange
        var backend = new FakeBackend();
        var client = ClientFor(backend);

        // Act
        var response = await client.PostAsync(_checkRunsPath, JsonBody(new { name = "MoneyMe AI review", head_sha = FakeBackend.HeadSha }));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!["id"]!.GetValue<long>().Should().BePositive();
        backend.CheckRuns.Should().ContainSingle().Which.BodyJson().GetProperty("name").GetString().Should().Be("MoneyMe AI review");
    }

    [Fact]
    public async Task RequestToken_GivenTheChecksPermissionIsNotGranted_Should_RefuseARequestForItWith422()
    {
        // Arrange
        var backend = new FakeBackend { IsChecksPermissionGranted = false };
        using var client = new HttpClient(backend);
        using var request = TokenRequest(ChecksPermissions);

        // Act
        var response = await client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync()).Should().Contain("The permissions requested are not granted to this installation.");
    }

    [Theory]
    [InlineData(true, ChecksPermissions)]
    [InlineData(true, PullRequestsPermission)]
    [InlineData(false, PullRequestsPermission)]
    public async Task RequestToken_GivenTheChecksPermissionIsGrantedOrNotAskedFor_Should_IssueTheToken(bool isChecksPermissionGranted, string permissionsJson)
    {
        // Arrange
        var backend = new FakeBackend { IsChecksPermissionGranted = isChecksPermissionGranted };
        using var client = new HttpClient(backend);
        using var request = TokenRequest(permissionsJson);

        // Act
        var response = await client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }
}
