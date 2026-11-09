namespace GitHubReviewApp.Models;

/// <summary>The rating of one check: its status, a one-line note (kept only for a concern or a fail), and whether the concern is serious.</summary>
public sealed record RatedCheck(CheckId Id, CheckStatus Status, string Note, bool IsSerious);
