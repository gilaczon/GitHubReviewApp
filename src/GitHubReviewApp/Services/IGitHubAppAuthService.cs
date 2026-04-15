namespace GitHubReviewApp.Services;

public interface IGitHubAppAuthService
{
    Task<string> GetInstallationTokenAsync(long installationId);
}
