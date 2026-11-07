namespace GitHubReviewApp.Services;

internal sealed class GitHubReviewDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("state")]
    public string State { get; init; } = string.Empty;

    [JsonPropertyName("body")]
    public string? Body { get; init; }

    [JsonPropertyName("commit_id")]
    public string? CommitId { get; init; }

    [JsonPropertyName("user")]
    public GitHubUserDto? User { get; init; }
}
