namespace GitHubReviewApp.Models;

public record ReviewQueueMessage(
    string Owner,
    string Repo,
    int PrNumber,
    string PrTitle,
    string BaseSha,
    string HeadSha,
    long InstallationId
);
