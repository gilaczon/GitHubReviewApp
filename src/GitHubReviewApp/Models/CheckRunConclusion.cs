namespace GitHubReviewApp.Models;

/// <summary>How a completed check run ends on the pull request: green, grey or red.</summary>
public enum CheckRunConclusion
{
    Success,
    Neutral,
    Failure
}
