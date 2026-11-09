namespace GitHubReviewApp.Models;

/// <summary>Why a whole review run was skipped. Used as a metric tag, so keep the names stable.</summary>
public enum ReviewSkipReason
{
    NotOpen = 0,
    Draft = 1,

    /// <summary>The pull request head moved on; the newer commit has its own queued review.</summary>
    Superseded = 2,

    /// <summary>A review for this exact commit already exists (webhook redelivery or retry).</summary>
    AlreadyReviewed = 3,

    /// <summary>GitHub listed no changed files, so there is nothing to review.</summary>
    NothingToReview = 4,

    /// <summary>This process is already reviewing this exact commit; that run posts the review.</summary>
    InProgress = 5,

    /// <summary>Reviews are switched off in the runtime settings (design 5.11), so nothing was reviewed.</summary>
    SwitchedOff = 6
}
