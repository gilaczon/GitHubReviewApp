namespace GitHubReviewApp.Services;

public class GitHubService : IGitHubService
{
    private const string ApiVersion = "2022-11-28";
    private const string JsonMediaType = "application/vnd.github+json";
    private const string RawMediaType = "application/vnd.github.raw+json";

    // GitHub returns at most 3,000 files for a pull request: 30 pages of 100.
    private const int ItemsPerPage = 100;
    private const int MaxFilePages = 30;

    // Comments are listed oldest first, so this app's newest review comment is on the last page. A pull request with this many comments is not normal.
    private const int MaxCommentPages = 30;

    // Reviews are listed oldest first, so this app's newest review is on the last page. A pull request with this many reviews is not normal.
    private const int MaxReviewPages = 50;

    private readonly HttpClient _httpClient;
    private readonly ILogger<GitHubService> _logger;
    private readonly ReviewOptions _options;

    public GitHubService(HttpClient httpClient, ILogger<GitHubService> logger, IOptions<ReviewOptions> options)
    {
        _httpClient = httpClient;
        _httpClient.BaseAddress = new Uri("https://api.github.com");
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GitHubReviewApp", "1.0"));
        _httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", ApiVersion);
        _logger = logger;
        _options = options.Value;
    }

    public async Task<PullRequestInfo> GetPullRequestAsync(
        string owner, string repo, int prNumber, string token, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, PullRequestUrl(owner, repo, prNumber), token);
        using var response = await _httpClient.SendAsync(request, cancellationToken);

        await EnsureSuccessAsync(response, "read the pull request", cancellationToken);

        var dto = await response.Content.ReadFromJsonAsync<GitHubPullRequestDto>(cancellationToken)
            ?? throw new InvalidOperationException("GitHub returned an empty pull request response.");

        return new PullRequestInfo(
            dto.State,
            dto.IsDraft,
            dto.Title ?? string.Empty,
            dto.Head?.Sha ?? string.Empty,
            dto.Base?.Sha ?? string.Empty,
            dto.ChangedFiles,
            dto.Additions,
            dto.Deletions,
            dto.Body ?? string.Empty,
            dto.User?.Login ?? string.Empty,
            dto.Base?.Ref ?? string.Empty,
            dto.Head?.Ref ?? string.Empty);
    }

    public async Task<IReadOnlyList<PullRequestFile>> GetPullRequestFilesAsync(
        string owner, string repo, int prNumber, string token, CancellationToken cancellationToken)
    {
        var files = new List<PullRequestFile>();
        var baseUrl = PullRequestUrl(owner, repo, prNumber) + "/files";

        for (var page = 1; page <= MaxFilePages; page++)
        {
            var items = await GetPageAsync<GitHubFileDto>(
                $"{baseUrl}?per_page={ItemsPerPage}&page={page}", token, "list the pull request files", cancellationToken);

            files.AddRange(items.Select(dto => new PullRequestFile(
                dto.Filename, dto.Status, dto.Additions, dto.Deletions, dto.Patch, dto.PreviousFilename)));

            if (items.Count < ItemsPerPage)
            {
                break;
            }
        }

        return files;
    }

    public async Task<IReadOnlyList<PullRequestReviewSummary>> ListReviewsAsync(
        string owner, string repo, int prNumber, string token, CancellationToken cancellationToken)
    {
        var reviews = new List<PullRequestReviewSummary>();
        var baseUrl = PullRequestUrl(owner, repo, prNumber) + "/reviews";
        var isComplete = false;

        for (var page = 1; page <= MaxReviewPages && !isComplete; page++)
        {
            var items = await GetPageAsync<GitHubReviewDto>(
                $"{baseUrl}?per_page={ItemsPerPage}&page={page}", token, "list the pull request reviews", cancellationToken);

            reviews.AddRange(items.Select(dto => new PullRequestReviewSummary(
                dto.Id, dto.State, dto.Body ?? string.Empty, dto.CommitId ?? string.Empty, dto.User?.Login ?? string.Empty)));

            isComplete = items.Count < ItemsPerPage;
        }

        if (!isComplete)
        {
            // The newest reviews are the ones left unread, so a review this app already posted may not be seen and could be posted again.
            _logger.LogWarning(
                "{Owner}/{Repo}#{PrNumber} has more than {ReviewCount} reviews. Only the first {ReviewCount} were read, so an existing review of the current commit may be missed.",
                owner, repo, prNumber, MaxReviewPages * ItemsPerPage, MaxReviewPages * ItemsPerPage);
        }

        return reviews;
    }

    public async Task DeleteReviewAsync(
        string owner, string repo, int prNumber, long reviewId, string token, CancellationToken cancellationToken)
    {
        var url = $"{PullRequestUrl(owner, repo, prNumber)}/reviews/{reviewId}";

        using var request = CreateRequest(HttpMethod.Delete, url, token);
        using var response = await _httpClient.SendAsync(request, cancellationToken);

        // Already gone is the outcome we wanted.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        await EnsureSuccessAsync(response, "delete the pending review", cancellationToken);
    }

    public async Task PostReviewAsync(
        string owner, string repo, int prNumber, ReviewSubmission submission, string token, CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["commit_id"] = submission.CommitId,
            ["body"] = submission.Body,
            ["event"] = "COMMENT"   // COMMENT = non-blocking; use REQUEST_CHANGES if you want blocking
        };

        if (submission.Comments.Count > 0)
        {
            payload["comments"] = submission.Comments
                .Select(comment => new { path = comment.Path, line = comment.Line, side = "RIGHT", body = comment.Body })
                .ToList();
        }

        using var request = CreateRequest(HttpMethod.Post, PullRequestUrl(owner, repo, prNumber) + "/reviews", token);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "post the review", cancellationToken);

        _logger.LogInformation(
            "Posted review for commit {CommitId} with {CommentCount} inline comments to {Owner}/{Repo}#{PrNumber}.",
            submission.CommitId, submission.Comments.Count, owner, repo, prNumber);
    }

    public async Task PostCommentAsync(
        string owner, string repo, int prNumber, string body, string token, CancellationToken cancellationToken)
    {
        var url = $"/repos/{Escape(owner)}/{Escape(repo)}/issues/{prNumber}/comments";

        using var request = CreateRequest(HttpMethod.Post, url, token);
        request.Content = new StringContent(JsonSerializer.Serialize(new { body }), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "post the comment", cancellationToken);
    }

    public async Task<IReadOnlyList<IssueComment>> ListIssueCommentsAsync(
        string owner, string repo, int prNumber, string token, CancellationToken cancellationToken)
    {
        var comments = new List<IssueComment>();
        var baseUrl = IssueCommentsUrl(owner, repo, prNumber);
        var isComplete = false;

        for (var page = 1; page <= MaxCommentPages && !isComplete; page++)
        {
            var items = await GetPageAsync<GitHubIssueCommentDto>(
                $"{baseUrl}?per_page={ItemsPerPage}&page={page}", token, "list the pull request comments", cancellationToken);

            comments.AddRange(items.Select(dto => new IssueComment(dto.Id, dto.User?.Login ?? string.Empty, dto.Body ?? string.Empty)));

            isComplete = items.Count < ItemsPerPage;
        }

        if (!isComplete)
        {
            // The newest comments are the ones left unread, so a review this app already posted may not be seen and could be posted again.
            _logger.LogWarning(
                "{Owner}/{Repo}#{PrNumber} has more than {CommentCount} comments. Only the first {CommentCount} were read, so an existing review of the current commit may be missed.",
                owner, repo, prNumber, MaxCommentPages * ItemsPerPage, MaxCommentPages * ItemsPerPage);
        }

        return comments;
    }

    public async Task<long> CreateIssueCommentAsync(
        string owner, string repo, int prNumber, string body, string token, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, IssueCommentsUrl(owner, repo, prNumber), token);
        request.Content = JsonContent(new { body });

        return await SendForIdAsync<GitHubIssueCommentDto>(request, "create the comment", dto => dto.Id, cancellationToken);
    }

    public async Task UpdateIssueCommentAsync(
        string owner, string repo, long commentId, string body, string token, CancellationToken cancellationToken)
    {
        var url = $"/repos/{Escape(owner)}/{Escape(repo)}/issues/comments/{commentId}";

        using var request = CreateRequest(HttpMethod.Patch, url, token);
        request.Content = JsonContent(new { body });

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "update the comment", cancellationToken);
    }

    public async Task<long> CreateCheckRunAsync(
        string owner, string repo, CheckRunRequest checkRun, string token, CancellationToken cancellationToken)
    {
        var payload = new
        {
            name = CheckRunRequest.Name,
            head_sha = checkRun.HeadSha,
            status = "completed",
            conclusion = ConclusionText(checkRun.Conclusion),
            external_id = checkRun.ExternalId,
            output = new { title = checkRun.Title, summary = checkRun.Summary }
        };

        using var request = CreateRequest(HttpMethod.Post, $"/repos/{Escape(owner)}/{Escape(repo)}/check-runs", token);
        request.Content = JsonContent(payload);

        return await SendForIdAsync<GitHubCheckRunDto>(request, "create the check run", dto => dto.Id, cancellationToken);
    }

    public async Task<RepositoryGuidelines> GetRepositoryGuidelinesAsync(
        string owner, string repo, string gitRef, string token, CancellationToken cancellationToken)
    {
        foreach (var path in _options.EffectiveGuidelinePaths)
        {
            var url = $"/repos/{Escape(owner)}/{Escape(repo)}/contents/{EscapePath(path)}?ref={Escape(gitRef)}";

            using var request = CreateRequest(HttpMethod.Get, url, token, RawMediaType);

            // A network failure or a timeout is thrown, not swallowed: nothing has been paid for yet, so retrying is free, whereas carrying on
            // would post a review that quietly ignored the repository's rules.
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                var failure = await GitHubApiException.FromResponseAsync("read the review guidelines", response, cancellationToken);

                if (FailureClassifier.IsTransient(failure))
                {
                    throw failure;
                }

                _logger.LogWarning(
                    failure,
                    "Could not read review guidelines {Path} from {Owner}/{Repo}. Continuing without them.",
                    path, owner, repo);
                return RepositoryGuidelines.Unreadable;
            }

            var (text, isTruncated) = await ReadCappedAsync(response.Content, _options.MaxGuidelinesChars, cancellationToken);

            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            return new RepositoryGuidelines(isTruncated ? text.TrimEnd() + "\n\n[guidelines truncated]" : text.Trim(), IsUnreadable: false);
        }

        return RepositoryGuidelines.None;
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method, string url, string token, string accept = JsonMediaType)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        return request;
    }

    private static string PullRequestUrl(string owner, string repo, int prNumber)
    {
        return $"/repos/{Escape(owner)}/{Escape(repo)}/pulls/{prNumber}";
    }

    private static string IssueCommentsUrl(string owner, string repo, int prNumber)
    {
        return $"/repos/{Escape(owner)}/{Escape(repo)}/issues/{prNumber}/comments";
    }

    private static string ConclusionText(CheckRunConclusion conclusion)
    {
        return conclusion switch
        {
            CheckRunConclusion.Success => "success",
            CheckRunConclusion.Neutral => "neutral",
            CheckRunConclusion.Failure => "failure",
            _ => throw new ArgumentOutOfRangeException(nameof(conclusion), conclusion, "Unknown check run conclusion.")
        };
    }

    private static StringContent JsonContent(object payload)
    {
        return new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
    }

    private static string Escape(string value)
    {
        return Uri.EscapeDataString(value);
    }

    private static string EscapePath(string path)
    {
        return string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
    }

    private async Task<List<T>> GetPageAsync<T>(
        string url, string token, string operation, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, url, token);
        using var response = await _httpClient.SendAsync(request, cancellationToken);

        await EnsureSuccessAsync(response, operation, cancellationToken);

        return await response.Content.ReadFromJsonAsync<List<T>>(cancellationToken) ?? [];
    }

    private async Task<long> SendForIdAsync<T>(
        HttpRequestMessage request, string operation, Func<T, long> readId, CancellationToken cancellationToken)
        where T : class
    {
        using var response = await _httpClient.SendAsync(request, cancellationToken);

        await EnsureSuccessAsync(response, operation, cancellationToken);

        var dto = await response.Content.ReadFromJsonAsync<T>(cancellationToken)
            ?? throw new InvalidOperationException($"GitHub returned an empty response when asked to {operation}.");

        return readId(dto);
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw await GitHubApiException.FromResponseAsync(operation, response, cancellationToken);
        }
    }

    private static async Task<(string Text, bool IsTruncated)> ReadCappedAsync(
        HttpContent content, int maxChars, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        // Read one character more than allowed to learn whether the file was longer than the cap.
        var buffer = new char[maxChars + 1];
        var read = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);

        return (new string(buffer, 0, Math.Min(read, maxChars)), read > maxChars);
    }
}
