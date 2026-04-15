namespace GitHubReviewApp.Models;

public class PullRequestWebhookPayload
{
    [JsonPropertyName("action")]
    public string Action { get; init; } = string.Empty;

    [JsonPropertyName("pull_request")]
    public PullRequest PullRequest { get; init; } = new();

    [JsonPropertyName("repository")]
    public Repository Repository { get; init; } = new();

    [JsonPropertyName("installation")]
    public Installation Installation { get; init; } = new();
}

public class PullRequest
{
    [JsonPropertyName("number")]
    public int Number { get; init; }

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("base")]
    public GitRef Base { get; init; } = new();

    [JsonPropertyName("head")]
    public GitRef Head { get; init; } = new();

    [JsonPropertyName("draft")]
    public bool Draft { get; init; }
}

public class GitRef
{
    [JsonPropertyName("sha")]
    public string Sha { get; init; } = string.Empty;
}

public class Repository
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("owner")]
    public RepositoryOwner Owner { get; init; } = new();
}

public class RepositoryOwner
{
    [JsonPropertyName("login")]
    public string Login { get; init; } = string.Empty;
}

public class Installation
{
    [JsonPropertyName("id")]
    public long Id { get; init; }
}
