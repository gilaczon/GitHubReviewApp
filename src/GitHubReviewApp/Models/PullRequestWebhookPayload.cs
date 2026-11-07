namespace GitHubReviewApp.Models;

/// <summary>
/// The parts of a GitHub pull_request webhook this app reads. Every object is nullable on purpose: System.Text.Json assigns a JSON null over a
/// property initializer, so "= new()" would not stop a payload with an explicit null from crashing the receiver.
/// </summary>
public class PullRequestWebhookPayload
{
    [JsonPropertyName("action")]
    public string? Action { get; init; }

    [JsonPropertyName("pull_request")]
    public PullRequest? PullRequest { get; init; }

    [JsonPropertyName("repository")]
    public Repository? Repository { get; init; }

    [JsonPropertyName("installation")]
    public Installation? Installation { get; init; }
}
