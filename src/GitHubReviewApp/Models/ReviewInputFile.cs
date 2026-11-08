namespace GitHubReviewApp.Models;

/// <summary>One changed file of the review input: its risk tier, and what happened to its diff.</summary>
public sealed record ReviewInputFile(PullRequestFile File, RiskTier Tier, DiffInclusion Inclusion);
