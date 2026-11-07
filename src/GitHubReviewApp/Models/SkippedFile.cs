namespace GitHubReviewApp.Models;

/// <summary>A changed file that was not sent to the model.</summary>
public sealed record SkippedFile(string Path, FileSkipReason Reason);
