namespace GitHubReviewApp.Models;

public class Repository
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("owner")]
    public RepositoryOwner? Owner { get; init; }
}
