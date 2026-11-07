namespace GitHubReviewApp.Models;

/// <summary>Everything needed to post one review: tied to a commit, with optional inline comments.</summary>
public sealed record ReviewSubmission(
    string CommitId,
    string Body,
    IReadOnlyList<InlineReviewComment> Comments);
