namespace GitHubReviewApp.Models;

/// <summary>How one Claude request ended. Used as a metric tag, so keep the names stable.</summary>
public enum ChunkOutcome
{
    /// <summary>The model returned a complete, readable review.</summary>
    Completed = 0,

    /// <summary>The output hit the token limit, so the review is incomplete.</summary>
    OutputTruncated = 1,

    /// <summary>The model declined to review (safety classifier).</summary>
    Refused = 2,

    /// <summary>The response could not be read as a review.</summary>
    InvalidOutput = 3,

    /// <summary>
    /// The AI service refused the request itself (too large, or malformed) and would refuse it again. Only this part of the review is lost,
    /// because the other parts are unaffected.
    /// </summary>
    Rejected = 4
}
