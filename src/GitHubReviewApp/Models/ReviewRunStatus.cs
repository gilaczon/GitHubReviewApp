namespace GitHubReviewApp.Models;

public enum ReviewRunStatus
{
    /// <summary>A review with a verdict was published.</summary>
    Posted = 0,

    Skipped = 1,

    /// <summary>Neither model run gave an answer, and the "Review failed" result was published instead of a verdict (design 5.12).</summary>
    Failed = 2
}
