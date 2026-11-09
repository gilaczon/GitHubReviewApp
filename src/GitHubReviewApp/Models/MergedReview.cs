namespace GitHubReviewApp.Models;

/// <summary>
/// What the completed runs of one review give together (design 5.7). <paramref name="Checks"/> holds the eight checks in
/// <see cref="Checklist.Order"/>, each with the stricter of the runs' ratings, and <paramref name="Findings"/> both runs' findings once
/// each, the stricter run's first. The summary and the coverage note are the stricter run's. <paramref name="IsUnanimous"/> is true only
/// when two runs rated every check with the same status. <paramref name="StricterRunVerdict"/> and <paramref name="OtherRunVerdict"/> are
/// each run's own verdict; with one run there is no other, and it is null.
/// </summary>
public sealed record MergedReview(
    IReadOnlyList<RatedCheck> Checks,
    IReadOnlyList<ChecklistFinding> Findings,
    string Summary,
    string CoverageNote,
    int RunCount,
    bool IsUnanimous,
    ReviewVerdict StricterRunVerdict,
    ReviewVerdict? OtherRunVerdict);
