namespace GitHubReviewApp.Services;

internal sealed class GitHubFileDto
{
    [JsonPropertyName("filename")]
    public string Filename { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("additions")]
    public int Additions { get; init; }

    [JsonPropertyName("deletions")]
    public int Deletions { get; init; }

    /// <summary>Absent for binary files and for diffs too large for GitHub to return.</summary>
    [JsonPropertyName("patch")]
    public string? Patch { get; init; }

    [JsonPropertyName("previous_filename")]
    public string? PreviousFilename { get; init; }
}
