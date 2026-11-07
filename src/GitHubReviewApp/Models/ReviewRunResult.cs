namespace GitHubReviewApp.Models;

/// <summary>What happened when a queued pull request was processed.</summary>
public sealed record ReviewRunResult(
    ReviewRunStatus Status,
    ReviewSkipReason? SkipReason,
    int FilesReviewed,
    int FindingCount,
    bool IsPartial)
{
    public static ReviewRunResult Skipped(ReviewSkipReason reason)
    {
        return new ReviewRunResult(ReviewRunStatus.Skipped, reason, 0, 0, false);
    }

    public static ReviewRunResult Posted(int filesReviewed, int findingCount, bool isPartial)
    {
        return new ReviewRunResult(ReviewRunStatus.Posted, null, filesReviewed, findingCount, isPartial);
    }
}
