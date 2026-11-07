namespace GitHubReviewApp.Tests.Infrastructure;

/// <summary>
/// A stand-in for both api.github.com and api.anthropic.com. Plugged in as the primary HTTP handler of the
/// real dependency-injection container, so the real services, SDK and serialization run unmodified and only
/// the network is replaced. Every request is recorded.
/// <para>
/// It is deliberately strict where the real API is: GitHub rejects a request without a User-Agent (403), an app JWT that does not verify
/// (401) and an installation token it did not issue (401), and so does this. It is stateful where the real API is: a posted review shows
/// up in the next list of reviews. A request it has no route for is kept in <see cref="UnroutedCalls"/> so a test can fail on it.
/// </para>
/// <para>
/// The JSON it returns is hand-written from GitHub's and Anthropic's documented responses. <c>RecordedGitHubResponseTests</c> feeds the
/// service responses recorded from the real GitHub API as well, so the app is shown to read the real shapes and not only these.
/// </para>
/// </summary>
internal sealed class FakeBackend : HttpMessageHandler
{
    public const string Owner = "my-org";
    public const string Repo = "my-service";
    public const int PrNumber = 7;
    public const long InstallationId = 12345L;
    public const string AppId = "123456";
    public const string AppSlug = "claude-test";
    public const string AppLogin = AppSlug + "[bot]";
    public const string HeadSha = "0123456789abcdef0123456789abcdef01234567";
    public const string BaseSha = "fedcba9876543210fedcba9876543210fedcba98";
    public const string AnthropicKey = "sk-ant-integration-key";
    public const string InstallationToken = "ghs_integration_installation_token";
    public const string GuidelinesText = "Prefer controllers over minimal APIs.";

    /// <summary>New-side lines 10 (context), 11 (added), 12 and 13 (context).</summary>
    public const string SourcePatch = "@@ -1,3 +10,4 @@\n a\n+b\n c\n d";

    // What GitHub allows in a review body or a comment.
    private const int MaxBodyChars = 65_536;

    private static readonly string[] _reviewEvents = ["APPROVE", "REQUEST_CHANGES", "COMMENT"];

    private readonly object _gate = new();
    private readonly List<RecordedCall> _calls = [];
    private readonly List<RecordedCall> _unroutedCalls = [];
    private readonly List<string> _postedReviewJson = [];
    private readonly HashSet<long> _deletedReviewIds = [];

    /// <summary>Checked first, in order. Return null to fall through to the default behavior.</summary>
    public List<Func<RecordedCall, HttpResponseMessage?>> Overrides { get; } = [];

    public string PullRequestResponse { get; set; } = PullRequestJson();

    public string FilesResponse { get; set; } = FilesJson(
    [
        new PullRequestFile("src/Foo.cs", "modified", 1, 0, SourcePatch),
        new PullRequestFile("package-lock.json", "modified", 5, 5, "@@ -1 +1 @@\n+x")
    ]);

    /// <summary>Reviews that existed before the run. Reviews the app posts during the run are listed after these.</summary>
    public string ReviewsResponse { get; set; } = "[]";

    public Func<string, HttpResponseMessage> ContentsHandler { get; set; } = path =>
        path.EndsWith("CLAUDE.md", StringComparison.Ordinal)
            ? Text(GuidelinesText)
            : new HttpResponseMessage(HttpStatusCode.NotFound);

    public Func<RecordedCall, HttpResponseMessage> AnthropicHandler { get; set; } = _ => DefaultModelReply();

    public IReadOnlyList<RecordedCall> Calls
    {
        get
        {
            lock (_gate)
            {
                return _calls.ToList();
            }
        }
    }

    /// <summary>Requests that matched no route. The real API has no such endpoint, so a test should expect none.</summary>
    public IReadOnlyList<RecordedCall> UnroutedCalls
    {
        get
        {
            lock (_gate)
            {
                return _unroutedCalls.ToList();
            }
        }
    }

    public IReadOnlyList<RecordedCall> ModelCalls => Calls.Where(call => call.IsAnthropic).ToList();

    public IReadOnlyList<RecordedCall> PostedReviews => Calls
        .Where(call => call.Method == HttpMethod.Post && call.Path.EndsWith($"/pulls/{PrNumber}/reviews", StringComparison.Ordinal))
        .ToList();

    public IReadOnlyList<RecordedCall> PostedComments => Calls
        .Where(call => call.Method == HttpMethod.Post && call.Path.EndsWith($"/issues/{PrNumber}/comments", StringComparison.Ordinal))
        .ToList();

    public static string PullRequestJson(string headSha = HeadSha, string state = "open", bool isDraft = false, int changedFiles = 2, int additions = 6, int deletions = 5)
    {
        return JsonSerializer.Serialize(new
        {
            state,
            draft = isDraft,
            title = "Add retries",
            head = new { sha = headSha },
            @base = new { sha = BaseSha },
            changed_files = changedFiles,
            additions,
            deletions
        });
    }

    public static string FilesJson(IEnumerable<PullRequestFile> files)
    {
        return JsonSerializer.Serialize(files.Select(file => new
        {
            filename = file.Path,
            status = file.Status,
            additions = file.Additions,
            deletions = file.Deletions,
            patch = file.Patch,
            previous_filename = file.PreviousPath
        }));
    }

    public static HttpResponseMessage DefaultModelReply()
    {
        var review = AnthropicFixtures.ReviewJson("Adds retries.", [AnthropicFixtures.Finding(path: "src/Foo.cs", line: 11)]);
        return AnthropicFixtures.Message([AnthropicFixtures.TextBlock(review)]);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    public static HttpResponseMessage Text(string text)
    {
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/plain") };
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(header => header.Key, header => string.Join(", ", header.Value), StringComparer.OrdinalIgnoreCase);
        var call = new RecordedCall(request.Method, request.RequestUri!, body, request.Headers.Authorization?.ToString(), headers);

        lock (_gate)
        {
            _calls.Add(call);
        }

        foreach (var overrideRule in Overrides.ToList())
        {
            var overridden = overrideRule(call);
            if (overridden is not null)
            {
                return overridden;
            }
        }

        return Route(call);
    }

    private static HttpResponseMessage? RejectLikeGitHub(RecordedCall call)
    {
        if (string.IsNullOrWhiteSpace(call.Header("User-Agent")))
        {
            return Json("{\"message\":\"Request forbidden by administrative rules. Please make sure your request has a User-Agent header.\"}", HttpStatusCode.Forbidden);
        }

        var isAppRequest = call.Path == "/app" || call.Path.StartsWith("/app/", StringComparison.Ordinal);

        if (isAppRequest)
        {
            var token = call.Authorization is { } header && header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..] : string.Empty;
            var jwt = GitHubAppJwt.Parse(token, RsaKeyHelper.PublicKey);
            var problem = jwt is null ? "the token is not a JWT" : jwt.Problem(AppId, DateTimeOffset.UtcNow);

            return problem is null
                ? null
                : Json($"{{\"message\":\"A JSON web token could not be decoded: {problem}\"}}", HttpStatusCode.Unauthorized);
        }

        return call.Authorization == "Bearer " + InstallationToken
            ? null
            : Json("{\"message\":\"Bad credentials\"}", HttpStatusCode.Unauthorized);
    }

    private HttpResponseMessage Route(RecordedCall call)
    {
        if (call.IsAnthropic)
        {
            return AnthropicHandler(call);
        }

        if (call.IsGitHub && RejectLikeGitHub(call) is { } rejection)
        {
            return rejection;
        }

        var path = call.Path;
        var pullRequestPath = $"/repos/{Owner}/{Repo}/pulls/{PrNumber}";

        if (call.Method == HttpMethod.Get && path == "/app")
        {
            return Json(JsonSerializer.Serialize(new { id = long.Parse(AppId, System.Globalization.CultureInfo.InvariantCulture), slug = AppSlug, name = "Claude Test" }));
        }

        if (call.Method == HttpMethod.Post && Regex.IsMatch(path, @"^/app/installations/\d+/access_tokens$"))
        {
            return Json(JsonSerializer.Serialize(new { token = InstallationToken, expires_at = DateTimeOffset.UtcNow.AddHours(1) }), HttpStatusCode.Created);
        }

        if (call.Method == HttpMethod.Get && path == pullRequestPath)
        {
            return Json(PullRequestResponse);
        }

        if (call.Method == HttpMethod.Get && path == pullRequestPath + "/files")
        {
            return Json(call.Uri.Query.EndsWith("&page=1", StringComparison.Ordinal) ? FilesResponse : "[]");
        }

        if (call.Method == HttpMethod.Get && path == pullRequestPath + "/reviews")
        {
            return Json(call.Uri.Query.EndsWith("&page=1", StringComparison.Ordinal) ? ListReviews() : "[]");
        }

        if (call.Method == HttpMethod.Post && path == pullRequestPath + "/reviews")
        {
            return RecordPostedReview(call);
        }

        if (call.Method == HttpMethod.Delete && Regex.Match(path, Regex.Escape(pullRequestPath) + @"/reviews/(\d+)$") is { Success: true } deletion)
        {
            return DeleteReview(long.Parse(deletion.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
        }

        if (call.Method == HttpMethod.Post && path == $"/repos/{Owner}/{Repo}/issues/{PrNumber}/comments")
        {
            return RecordIssueComment(call);
        }

        if (call.Method == HttpMethod.Get && path.StartsWith($"/repos/{Owner}/{Repo}/contents/", StringComparison.Ordinal))
        {
            return ContentsHandler(path);
        }

        lock (_gate)
        {
            _unroutedCalls.Add(call);
        }

        return Json("{\"message\":\"Not routed by the test backend\"}", HttpStatusCode.NotFound);
    }

    private string ListReviews()
    {
        var reviews = JsonNode.Parse(ReviewsResponse)!.AsArray();

        lock (_gate)
        {
            foreach (var posted in _postedReviewJson)
            {
                reviews.Add(JsonNode.Parse(posted));
            }

            // A review that was deleted is gone from the next list, as it is on GitHub.
            for (var index = reviews.Count - 1; index >= 0; index--)
            {
                if (reviews[index]?["id"]?.GetValue<long>() is { } id && _deletedReviewIds.Contains(id))
                {
                    reviews.RemoveAt(index);
                }
            }
        }

        return reviews.ToJsonString();
    }

    private static HttpResponseMessage Unprocessable(string reason)
    {
        return Json(JsonSerializer.Serialize(new { message = "Unprocessable Entity", errors = new[] { reason } }), HttpStatusCode.UnprocessableEntity);
    }

    private HttpResponseMessage DeleteReview(long reviewId)
    {
        var review = JsonNode.Parse(ListReviews())!.AsArray().FirstOrDefault(item => item?["id"]?.GetValue<long>() == reviewId);

        if (review is null)
        {
            return Json("{\"message\":\"Not Found\"}", HttpStatusCode.NotFound);
        }

        // Only a draft can be deleted; a review that was submitted stays.
        if (review["state"]?.GetValue<string>() != "PENDING")
        {
            return Unprocessable("Can not delete a non-pending pull request review");
        }

        lock (_gate)
        {
            _deletedReviewIds.Add(reviewId);
        }

        return Json("{}");
    }

    private static HttpResponseMessage? ValidateText(JsonElement request, string description)
    {
        return request.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String && body.GetString()!.Length > MaxBodyChars
            ? Unprocessable($"{description} is too long (maximum is {MaxBodyChars} characters)")
            : null;
    }

    private HttpResponseMessage RecordIssueComment(RecordedCall call)
    {
        var request = call.BodyJson();

        if (!request.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(body.GetString()))
        {
            return Unprocessable("Body cannot be blank");
        }

        return ValidateText(request, "Body") ?? Json("{\"id\":2}", HttpStatusCode.Created);
    }

    /// <summary>The refusals GitHub gives a review it cannot take. Null when the review would be accepted.</summary>
    private HttpResponseMessage? RejectReview(JsonElement request)
    {
        if (ValidateText(request, "Body") is { } tooLong)
        {
            return tooLong;
        }

        if (request.TryGetProperty("event", out var reviewEvent) && !_reviewEvents.Contains(reviewEvent.GetString()))
        {
            return Unprocessable("Event is not included in the list");
        }

        var head = JsonNode.Parse(PullRequestResponse)?["head"]?["sha"]?.GetValue<string>();

        if (request.TryGetProperty("commit_id", out var commit) && commit.GetString() != head)
        {
            return Unprocessable("commit_id is not part of the pull request");
        }

        return request.TryGetProperty("comments", out var comments) && comments.ValueKind == JsonValueKind.Array
            ? comments.EnumerateArray().Select(RejectInlineComment).FirstOrDefault(rejection => rejection is not null)
            : null;
    }

    private HttpResponseMessage? RejectInlineComment(JsonElement comment)
    {
        var path = comment.TryGetProperty("path", out var pathElement) ? pathElement.GetString() : null;
        var file = JsonNode.Parse(FilesResponse)!.AsArray().FirstOrDefault(item => item?["filename"]?.GetValue<string>() == path);

        if (file is null)
        {
            return Unprocessable("Path could not be resolved");
        }

        if (!comment.TryGetProperty("body", out var body) || string.IsNullOrWhiteSpace(body.GetString()))
        {
            return Unprocessable("Body cannot be blank");
        }

        if (ValidateText(comment, "Body") is { } tooLong)
        {
            return tooLong;
        }

        var side = comment.TryGetProperty("side", out var sideElement) ? sideElement.GetString() : "RIGHT";
        var line = comment.TryGetProperty("line", out var lineElement) && lineElement.TryGetInt32(out var number) ? number : 0;

        // Only the new side of a hunk is modelled, which is the side the app comments on.
        return side == "RIGHT" && IndependentDiffLines.NewSideLines(file["patch"]?.GetValue<string>()).Contains(line)
            ? null
            : Unprocessable("Pull request review thread line must be part of the diff");
    }

    private HttpResponseMessage RecordPostedReview(RecordedCall call)
    {
        var request = call.BodyJson();

        if (RejectReview(request) is { } rejection)
        {
            return rejection;
        }

        lock (_gate)
        {
            var id = 1000 + _postedReviewJson.Count;

            _postedReviewJson.Add(JsonSerializer.Serialize(new
            {
                id,
                state = "COMMENTED",
                body = request.GetProperty("body").GetString(),
                commit_id = request.GetProperty("commit_id").GetString(),
                user = new { login = AppLogin, type = "Bot" }
            }));

            return Json($"{{\"id\":{id}}}");
        }
    }
}
