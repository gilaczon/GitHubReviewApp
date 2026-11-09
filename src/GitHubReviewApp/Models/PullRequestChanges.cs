namespace GitHubReviewApp.Models;

/// <summary>
/// What an <c>edited</c> pull_request event changed. GitHub sends a <c>base</c> object (the old branch name and commit) only when the pull
/// request was retargeted, alongside any <c>title</c> or <c>body</c> change; the app reads only whether that object is there.
/// </summary>
public class PullRequestChanges
{
    [JsonPropertyName("base")]
    public JsonElement? Base { get; init; }

    /// <summary>True when the edit changed the branch the pull request merges into.</summary>
    public bool HasBaseChanged => Base is { ValueKind: JsonValueKind.Object };
}
