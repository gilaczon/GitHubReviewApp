namespace GitHubReviewApp.Models;

/// <summary>What happened to a changed file's diff in the review input.</summary>
public enum DiffInclusion
{
    /// <summary>Sent whole.</summary>
    Included = 0,

    /// <summary>Sent cut at <c>Review:MaxFileBytes</c>, at a line boundary.</summary>
    Partial = 1,

    /// <summary>Left out because it did not fit what was left of <c>Review:MaxDiffBytes</c>.</summary>
    OverBudget = 2,

    /// <summary>A dependency lockfile, which is listed and never read.</summary>
    Lockfile = 3,

    /// <summary>A binary, or a minified or bundled file, which is listed and never read.</summary>
    BinaryOrMinified = 4,

    /// <summary>GitHub sent no patch for it: a pure rename, a binary it recognised, or a change too large to diff.</summary>
    NoDiff = 5,

    /// <summary>
    /// Left out because a cut at <c>Review:MaxFileBytes</c> would keep under 2,000 bytes of it: a line near its top is longer than what is
    /// left of that limit, as in a bundled script or a one-line JSON file.
    /// </summary>
    LongLine = 6
}
