namespace GitHubReviewApp.Services;

internal sealed class GitHubUserDto
{
    [JsonPropertyName("login")]
    public string? Login { get; init; }
}
