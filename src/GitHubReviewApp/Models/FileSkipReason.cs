namespace GitHubReviewApp.Models;

/// <summary>Why a changed file was left out of the review.</summary>
public enum FileSkipReason
{
    /// <summary>
    /// A dependency lockfile. The reviewer does not read it, yet a changed package source or hash is a supply chain risk,
    /// so the review says so plainly instead of folding it into a list of generated files.
    /// </summary>
    DependencyLockfile = 0,

    /// <summary>A binary asset, or a minified or bundled file that neither a person nor the model can review as text.</summary>
    BinaryOrMinified = 1,

    /// <summary>GitHub returned no textual diff (a pure rename, or a change too large to diff).</summary>
    NoDiffAvailable = 2,

    /// <summary>The file was deleted, so there is no new code to review.</summary>
    Deleted = 3,

    /// <summary>The pull request is larger than the configured review budget.</summary>
    OverBudget = 4
}
