namespace GitHubReviewApp.Models;

/// <summary>
/// What happened when a queued pull request was processed. <paramref name="Verdict"/> is set only for a posted review, and
/// <paramref name="SkipReason"/> only for a skipped one. <paramref name="RunCount"/> is the number of model runs that completed and were merged
/// (one or two), <paramref name="IncludedFileCount"/> the files whose diff was sent whole or cut, and <paramref name="IsPartial"/> says some
/// changed file that could have been read was not read in full.
/// </summary>
public sealed record ReviewRunResult(
    ReviewRunStatus Status,
    ReviewSkipReason? SkipReason,
    ReviewVerdict? Verdict,
    int RunCount,
    int IncludedFileCount,
    bool IsPartial)
{
    public static ReviewRunResult Skipped(ReviewSkipReason reason)
    {
        return new ReviewRunResult(ReviewRunStatus.Skipped, reason, null, 0, 0, false);
    }

    public static ReviewRunResult Posted(ReviewVerdict verdict, int runCount, int includedFileCount, bool isPartial)
    {
        return new ReviewRunResult(ReviewRunStatus.Posted, null, verdict, runCount, includedFileCount, isPartial);
    }

    /// <summary>A "Review failed" result was published: no run completed, so there is no verdict and nothing was reviewed.</summary>
    public static ReviewRunResult Failed()
    {
        return new ReviewRunResult(ReviewRunStatus.Failed, null, null, 0, 0, false);
    }
}
