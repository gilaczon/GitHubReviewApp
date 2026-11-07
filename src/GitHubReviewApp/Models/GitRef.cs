namespace GitHubReviewApp.Models;

public class GitRef
{
    [JsonPropertyName("sha")]
    public string? Sha { get; init; }
}
