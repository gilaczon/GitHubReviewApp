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
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/repos/{owner}/{repo}/pulls/{prNumber}/reviews");

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));

        var payload = JsonSerializer.Serialize(new
        {
            body,
            event_type = "COMMENT"   // COMMENT = non-blocking; use REQUEST_CHANGES if you want blocking
        });
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        _logger.LogInformation("Posted review to {Owner}/{Repo}#{PrNumber}.", owner, repo, prNumber);
    }
}
