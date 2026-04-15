namespace GitHubReviewApp.Services;

public interface IClaudeService
{
    Task<string> ReviewDiffAsync(string prTitle, string diff);
}
