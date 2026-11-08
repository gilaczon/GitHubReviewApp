namespace GitHubReviewApp.Models;

/// <summary>What one model run answered, after reading. <paramref name="Rated"/> holds only the checks the run rated.</summary>
public sealed record RunAnswer(
    IReadOnlyDictionary<CheckId, RatedCheck> Rated,
    IReadOnlyList<ChecklistFinding> Findings,
    string Summary,
    string CoverageNote);
