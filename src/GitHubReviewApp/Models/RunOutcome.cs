namespace GitHubReviewApp.Models;

/// <summary>
/// How one checklist run ended (design 5.5). A call that fails outright is not an outcome: it is thrown, and <see cref="FailureClassifier"/>
/// says whether a retry can help. Written as its name into the runs metric and the review record, so keep the names stable.
/// </summary>
public enum RunOutcome
{
    /// <summary>The run returned an answer that was read.</summary>
    Completed = 0,

    /// <summary>The answer was cut off by the output limit or the model's context window.</summary>
    OutputTruncated = 1,

    /// <summary>The model declined to answer, and so did every model the request fell back to.</summary>
    Refused = 2,

    /// <summary>The answer could not be read as a checklist answer, or it rated none of the eight checks.</summary>
    InvalidOutput = 3,

    /// <summary>The AI service refused the request itself (400, 413 or 422), and would refuse it again.</summary>
    Rejected = 4
}
