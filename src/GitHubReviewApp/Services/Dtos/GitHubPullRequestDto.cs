namespace GitHubReviewApp.Services;

internal sealed class GitHubPullRequestDto
{
    [JsonPropertyName("state")]
    public string State { get; init; } = string.Empty;

    [JsonPropertyName("draft")]
    public bool IsDraft { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("body")]
    public string? Body { get; init; }

    [JsonPropertyName("user")]
    public GitHubUserDto? User { get; init; }

    [JsonPropertyName("head")]
    public GitHubRefDto? Head { get; init; }

    [JsonPropertyName("base")]
    public GitHubRefDto? Base { get; init; }

    [JsonPropertyName("changed_files")]
    public int ChangedFiles { get; init; }

    [JsonPropertyName("additions")]
    public int Additions { get; init; }

    [JsonPropertyName("deletions")]
    public int Deletions { get; init; }
}
