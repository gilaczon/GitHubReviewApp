namespace GitHubReviewApp.Configuration;

/// <summary>
/// Rejects Claude settings that could never work, when the host starts. Without it a zero timeout or a misspelt effort level would surface
/// as a failed review of the first pull request, long after the deployment that caused it.
/// </summary>
public sealed class ClaudeOptionsValidator : IValidateOptions<ClaudeOptions>
{
    private const int MaxRequestTimeoutSeconds = 3_600;
    private const int MaxRetriesCap = 10;

    public ValidateOptionsResult Validate(string? name, ClaudeOptions options)
    {
        var failures = new List<string>();

        if (options.MaxOutputTokens < 1)
        {
            failures.Add($"Claude:MaxOutputTokens must be at least 1, but it is {options.MaxOutputTokens}.");
        }

        if (options.RequestTimeoutSeconds < 1)
        {
            failures.Add($"Claude:RequestTimeoutSeconds must be at least 1, but it is {options.RequestTimeoutSeconds}.");
        }

        // A timeout over about 49 days cannot even be started, which would fail every review with an error that startup validation had passed, and
        // a request that may take longer than an hour is past any Functions timeout.
        if (options.RequestTimeoutSeconds > MaxRequestTimeoutSeconds)
        {
            failures.Add($"Claude:RequestTimeoutSeconds must be at most {MaxRequestTimeoutSeconds}, but it is {options.RequestTimeoutSeconds}.");
        }

        if (options.MaxRetries < 0)
        {
            failures.Add($"Claude:MaxRetries must not be negative, but it is {options.MaxRetries}.");
        }

        if (options.MaxRetries > MaxRetriesCap)
        {
            failures.Add($"Claude:MaxRetries must be at most {MaxRetriesCap}, but it is {options.MaxRetries}. Each retry spends part of the request's time budget.");
        }

        if (!string.IsNullOrWhiteSpace(options.Effort)
            && !ClaudeOptions.KnownEfforts.Contains(options.Effort.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            failures.Add(
                $"Claude:Effort '{options.Effort}' is not one of {string.Join(", ", ClaudeOptions.KnownEfforts)}. Leave it empty to leave the setting out.");
        }

        foreach (var (model, price) in options.Pricing)
        {
            if (string.IsNullOrWhiteSpace(model))
            {
                failures.Add("Claude:Pricing has a price with no model name. Prices are looked up by model id, so it would never be used.");
            }

            RequirePriceNotNegative(failures, model, nameof(ModelPrice.InputPerMillionTokens), price.InputPerMillionTokens);
            RequirePriceNotNegative(failures, model, nameof(ModelPrice.OutputPerMillionTokens), price.OutputPerMillionTokens);
            RequirePriceNotNegative(failures, model, nameof(ModelPrice.CacheWritePerMillionTokens), price.CacheWritePerMillionTokens);
            RequirePriceNotNegative(failures, model, nameof(ModelPrice.CacheReadPerMillionTokens), price.CacheReadPerMillionTokens);
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void RequirePriceNotNegative(List<string> failures, string model, string priceName, decimal value)
    {
        if (value < 0)
        {
            failures.Add($"Claude:Pricing:{model}:{priceName} must be at least 0, but it is {value}. A negative price would take money off the cost of every review.");
        }
    }
}
