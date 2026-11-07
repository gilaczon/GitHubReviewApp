namespace GitHubReviewApp.Services;

internal sealed class GitHubRefDto
{
    [JsonPropertyName("sha")]
    public string Sha { get; init; } = string.Empty;
}
