namespace GitHubReviewApp.Services;

internal sealed class GitHubIssueCommentDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("body")]
    public string? Body { get; init; }

    [JsonPropertyName("user")]
    public GitHubUserDto? User { get; init; }
}
