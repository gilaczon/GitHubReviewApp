namespace GitHubReviewApp.Models;

/// <summary>The markdown body and inline comments to post to GitHub.</summary>
public sealed record FormattedReview(string Body, IReadOnlyList<InlineReviewComment> Comments);
