namespace GitHubReviewApp.Models;

/// <summary>
/// A completed check run to create on a commit. <paramref name="ExternalId"/> is GitHub's field for the caller's own reference to the run, so
/// the review that made it can be found again. <paramref name="Title"/> and <paramref name="Summary"/> are the run's output.
/// </summary>
public sealed record CheckRunRequest(string HeadSha, CheckRunConclusion Conclusion, string Title, string Summary, string ExternalId)
{
    /// <summary>The name the check run shows on the pull request.</summary>
    public const string Name = "MoneyMe AI review";
}
