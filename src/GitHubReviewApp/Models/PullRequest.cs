namespace GitHubReviewApp.Models;

public class PullRequest
{
    [JsonPropertyName("number")]
    public int Number { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("base")]
    public GitRef? Base { get; init; }

    [JsonPropertyName("head")]
    public GitRef? Head { get; init; }

    [JsonPropertyName("draft")]
    public bool IsDraft { get; init; }

    /// <summary>How the author relates to the repository: OWNER, MEMBER, COLLABORATOR, CONTRIBUTOR, FIRST_TIME_CONTRIBUTOR, NONE and so on.</summary>
    [JsonPropertyName("author_association")]
    public string? AuthorAssociation { get; init; }
}
