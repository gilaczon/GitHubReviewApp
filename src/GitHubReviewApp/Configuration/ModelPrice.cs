namespace GitHubReviewApp.Configuration;

/// <summary>
/// What one model charges, in US dollars per million tokens, for each kind of token the API reports (design 5.10). One entry of
/// <c>Claude:Pricing</c>, keyed by model id. The cache write price is the five minute one, the only cache lifetime the service asks for.
/// </summary>
public sealed class ModelPrice
{
    /// <summary>Input tokens that were neither written to the prompt cache nor served from it.</summary>
    public decimal InputPerMillionTokens { get; init; }

    /// <summary>Tokens the model wrote, thinking included.</summary>
    public decimal OutputPerMillionTokens { get; init; }

    /// <summary>Input tokens written to the prompt cache.</summary>
    public decimal CacheWritePerMillionTokens { get; init; }

    /// <summary>Input tokens served from the prompt cache.</summary>
    public decimal CacheReadPerMillionTokens { get; init; }
}
