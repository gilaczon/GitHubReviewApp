namespace GitHubReviewApp.Models;

/// <summary>What the app's comment on a pull request records for a commit (design 5.2): a review, or a "Review failed" notice.</summary>
public enum ReviewRecordOutcome
{
    /// <summary>The comment holds a review of the commit, which is then not reviewed again.</summary>
    Reviewed,

    /// <summary>The comment says the review failed, so a new push or a reopen tries again.</summary>
    Failed
}
