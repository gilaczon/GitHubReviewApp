namespace GitHubReviewApp.Services;

internal sealed class GitHubCheckRunDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }
}
