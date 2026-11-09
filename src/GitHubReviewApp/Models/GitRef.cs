namespace GitHubReviewApp.Models;

public class GitRef
{
    [JsonPropertyName("sha")]
    public string? Sha { get; init; }

    /// <summary>The branch name, such as <c>main</c>; for a pull request's base, the branch it would merge into.</summary>
    [JsonPropertyName("ref")]
    public string? Ref { get; init; }
}
