namespace GitHubReviewApp.Models;

public class RepositoryOwner
{
    [JsonPropertyName("login")]
    public string? Login { get; init; }
}
