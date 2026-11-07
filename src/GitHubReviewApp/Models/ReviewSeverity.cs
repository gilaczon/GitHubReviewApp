namespace GitHubReviewApp.Models;

/// <summary>Severity of a finding. Declared in ascending order so comparisons work.</summary>
public enum ReviewSeverity
{
    Low = 0,
    Medium = 1,
    High = 2,
    Critical = 3
}
