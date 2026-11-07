namespace GitHubReviewApp.Configuration;

/// <summary>
/// Settings for the Claude API calls. Bound from the "Claude" configuration section.
/// All values are non-secret; the API key itself comes from Key Vault.
/// </summary>
public sealed class ClaudeOptions
{
    public const string SectionName = "Claude";

    /// <summary>The effort levels the service understands, in any letter case. Anything else is a configuration mistake.</summary>
    public static IReadOnlyList<string> KnownEfforts { get; } = ["low", "medium", "high", "xhigh", "max"];

    /// <summary>Model ID sent to the API, e.g. "claude-sonnet-5-5". Change it here, not in code.</summary>
    public string Model { get; init; } = "claude-sonnet-5-5";

    /// <summary>
    /// Output token cap per request. Thinking tokens count towards this limit on current models,
    /// so it must cover the reasoning as well as the review text.
    /// </summary>
    public int MaxOutputTokens { get; init; } = 32000;

    /// <summary>
    /// Effort level: low, medium, high, xhigh or max. Leave empty to omit the parameter,
    /// which is required for older models that do not support it.
    /// </summary>
    public string? Effort { get; init; } = "high";

    /// <summary>
    /// The whole time one request may take, retries and the streamed reply included. A request that is still running at this point is abandoned and the
    /// message is retried. A review can run several requests one after another, so <c>Review:MaxReviewDurationSeconds</c> is what has to stay below the
    /// Functions host timeout, and this must not exceed it.
    /// </summary>
    public int RequestTimeoutSeconds { get; init; } = 600;

    /// <summary>Retries performed by the SDK for 408/409/429/5xx and connection errors. Each one spends part of the time budget above.</summary>
    public int MaxRetries { get; init; } = 1;
}
