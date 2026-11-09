namespace GitHubReviewApp.Models;

/// <summary>Token usage reported by the API for one or more requests.</summary>
public sealed record ClaudeUsage(
    long InputTokens,
    long OutputTokens,
    long CacheReadInputTokens,
    long CacheCreationInputTokens)
{
    public static ClaudeUsage Empty { get; } = new(0, 0, 0, 0);

    public ClaudeUsage Plus(ClaudeUsage other)
    {
        return new ClaudeUsage(
            InputTokens + other.InputTokens,
            OutputTokens + other.OutputTokens,
            CacheReadInputTokens + other.CacheReadInputTokens,
            CacheCreationInputTokens + other.CacheCreationInputTokens);
    }
}
