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
    /// message is retried. A review runs two requests and can wait for a free model slot first, so <c>Review:MaxReviewDurationSeconds</c> is what has
    /// to stay below the Functions host timeout, and this must not exceed it.
    /// </summary>
    public int RequestTimeoutSeconds { get; init; } = 600;

    /// <summary>Retries performed by the SDK for 408/409/429/5xx and connection errors. Each one spends part of the time budget above.</summary>
    public int MaxRetries { get; init; } = 1;

    /// <summary>
    /// Whether a checklist run uses Anthropic's server-side refusal fallback (design 5.5): the call goes to the beta Messages endpoint with
    /// <c>fallbacks: "default"</c>, so a request the asked model declines is answered by the model Anthropic picks for that refusal, in the same
    /// call. Off, the run uses the standard endpoint and a refusal ends it.
    /// </summary>
    public bool IsRefusalFallbackEnabled { get; init; } = true;

    /// <summary>
    /// What each model charges, by model id (design 5.10), used to put a cost on every review. Ids are compared without regard to letter case. A model
    /// that is not listed has no cost, and the review record says so instead of guessing, so a model the app can fall back to belongs here too.
    /// </summary>
    public Dictionary<string, ModelPrice> Pricing { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
