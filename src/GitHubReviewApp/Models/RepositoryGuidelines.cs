namespace GitHubReviewApp.Models;

/// <summary>
/// The result of looking for a repository's review guidelines. <see cref="IsUnreadable"/> is true when the guidelines may exist but the app
/// could not read them (no access to repository contents), which the review says so its readers know the repository's rules were not applied.
/// </summary>
public sealed record RepositoryGuidelines(string? Text, bool IsUnreadable)
{
    public static RepositoryGuidelines None { get; } = new(null, false);

    public static RepositoryGuidelines Unreadable { get; } = new(null, true);
}
