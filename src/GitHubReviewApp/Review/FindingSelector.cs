namespace GitHubReviewApp.Review;

/// <summary>
/// Picks the findings the comment shows (design 5.7): only those whose check ended a fail or a concern, worst first (the fail's, then those of
/// serious concerns, then the rest, each group in the merged order), and at most <see cref="MaxShown"/>.
/// </summary>
public static class FindingSelector
{
    /// <summary>The most findings the comment shows.</summary>
    public const int MaxShown = 6;

    /// <summary>The findings to show, worst first.</summary>
    public static IReadOnlyList<ChecklistFinding> SelectShown(MergedReview merged)
    {
        var groups = merged.Checks.ToDictionary(check => check.Id, GroupOf);

        // OrderBy is a stable sort, so each group keeps the merged order. The cut comes after it, so it never drops a worse finding for a
        // milder one.
        return merged.Findings
            .Select(finding => (Finding: finding, Group: groups.GetValueOrDefault(finding.Check)))
            .Where(item => item.Group is not null)
            .OrderBy(item => item.Group)
            .Select(item => item.Finding)
            .Take(MaxShown)
            .ToList()
            .AsReadOnly();
    }

    private static int? GroupOf(RatedCheck check)
    {
        // Where a check's findings come in the list, or null for a check that ended a pass or n/a, which needs no fix.
        return check.Status switch
        {
            CheckStatus.Fail => 0,
            CheckStatus.Concern when check.IsSerious => 1,
            CheckStatus.Concern => 2,
            _ => null
        };
    }
}
