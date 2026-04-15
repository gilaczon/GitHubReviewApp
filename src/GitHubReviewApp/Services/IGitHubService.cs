namespace GitHubReviewApp.Services;

public interface IGitHubService
{
    Task<string> GetPullRequestDiffAsync(string owner, string repo, int prNumber, string token);
    Task PostReviewAsync(string owner, string repo, int prNumber, string body, string token);
}
