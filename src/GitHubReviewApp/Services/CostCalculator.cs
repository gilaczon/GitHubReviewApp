namespace GitHubReviewApp.Services;

/// <summary>Prices a run from the tokens its attempts used, with the static table in <c>Claude:Pricing</c> (design 5.10).</summary>
public static class CostCalculator
{
    private const decimal TokensPerMillion = 1_000_000m;

    /// <summary>
    /// The cost in US dollars of every attempt added up, each at its own model's prices: input, output, cache write and cache read tokens times their
    /// price per million. It is zero for no attempts, and <see langword="null"/> when any attempt's model is not in the table, because a sum that
    /// leaves one attempt out would read as the real cost. Model ids are compared without regard to letter case, whatever the table itself does.
    /// </summary>
    public static decimal? CostOf(IReadOnlyList<ModelAttempt> attempts, IReadOnlyDictionary<string, ModelPrice> prices)
    {
        var total = 0m;

        foreach (var attempt in attempts)
        {
            var price = prices
                .Where(entry => string.Equals(entry.Key, attempt.Model, StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.Value)
                .FirstOrDefault();

            if (price is null)
            {
                return null;
            }

            total += (attempt.Usage.InputTokens * price.InputPerMillionTokens
                + attempt.Usage.OutputTokens * price.OutputPerMillionTokens
                + attempt.Usage.CacheCreationInputTokens * price.CacheWritePerMillionTokens
                + attempt.Usage.CacheReadInputTokens * price.CacheReadPerMillionTokens) / TokensPerMillion;
        }

        return total;
    }
}
