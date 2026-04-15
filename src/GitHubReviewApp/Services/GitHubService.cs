namespace GitHubReviewApp.Services;

public class GitHubService : IGitHubService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<GitHubService> _logger;

    // GitHub limits diffs to 300 files / ~20 MB — we cap further to stay within Claude context
    private const int MaxDiffBytes = 80_000;

    public GitHubService(HttpClient httpClient, ILogger<GitHubService> logger)
    {
        _httpClient = httpClient;
        _httpClient.BaseAddress = new Uri("https://api.github.com");
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GitHubReviewApp", "1.0"));
        _logger = logger;
    }

    public async Task<string> GetPullRequestDiffAsync(
        string owner, string repo, int prNumber, string token)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/repos/{owner}/{repo}/pulls/{prNumber}");

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3.diff"));

        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var diff = await response.Content.ReadAsStringAsync();

        if (diff.Length > MaxDiffBytes)
        {
            _logger.LogWarning(
                "Diff for {Owner}/{Repo}#{PrNumber} is {Size} bytes — truncating to {Max}.",
                owner, repo, prNumber, diff.Length, MaxDiffBytes);
            diff = diff[..MaxDiffBytes] + "\n\n[diff truncated — too large for review]";
        }

        return diff;
    }

    public async Task PostReviewAsync(
        string owner, string repo, int prNumber, string body, string token)
    {
        await DeletePendingReviewAsync(owner, repo, prNumber, token);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/repos/{owner}/{repo}/pulls/{prNumber}/reviews");

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));

        var payload = JsonSerializer.Serialize(new
        {
            body,
            @event = "COMMENT"   // COMMENT = non-blocking; use REQUEST_CHANGES if you want blocking
        });
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException(
                $"GitHub POST reviews failed {(int)response.StatusCode} ({response.ReasonPhrase}): {errorBody}");
        }

        _logger.LogInformation("Posted review to {Owner}/{Repo}#{PrNumber}.", owner, repo, prNumber);
    }

    // GitHub allows only one pending (draft) review per reviewer per PR.
    // Any earlier run that crashed before submitting leaves a dangling draft that blocks future reviews.
    private async Task DeletePendingReviewAsync(string owner, string repo, int prNumber, string token)
    {
        using var listRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/repos/{owner}/{repo}/pulls/{prNumber}/reviews");

        listRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        listRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));

        var listResponse = await _httpClient.SendAsync(listRequest);
        if (!listResponse.IsSuccessStatusCode) return;

        var reviews = await listResponse.Content.ReadFromJsonAsync<PullRequestReview[]>();
        var pending = reviews?.FirstOrDefault(r => r.State == "PENDING");
        if (pending is null) return;

        _logger.LogInformation(
            "Deleting pending review {ReviewId} before posting new review for {Owner}/{Repo}#{PrNumber}.",
            pending.Id, owner, repo, prNumber);

        using var deleteRequest = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/repos/{owner}/{repo}/pulls/{prNumber}/reviews/{pending.Id}");

        deleteRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        deleteRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));

        await _httpClient.SendAsync(deleteRequest);
    }

    private sealed class PullRequestReview
    {
        [JsonPropertyName("id")]
        public long Id { get; init; }

        [JsonPropertyName("state")]
        public string State { get; init; } = string.Empty;
    }
}
