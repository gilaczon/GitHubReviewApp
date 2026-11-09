namespace GitHubReviewApp.Tests.Infrastructure;

/// <summary>
/// A test that calls the real Anthropic API, so it costs money and needs a key. It is skipped unless BOTH variables are set in the environment of
/// the machine running the tests. A key alone is not enough: many developers have one set for other tools, and merely running the suite must
/// never spend their money.
/// </summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public const string OptInVariable = "LIVE_MODEL_TESTS";
    public const string KeyVariable = "ANTHROPIC_API_KEY";

    public LiveFactAttribute()
    {
        var isOptedIn = string.Equals(Environment.GetEnvironmentVariable(OptInVariable), "1", StringComparison.Ordinal);
        var hasKey = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(KeyVariable));

        if (!isOptedIn || !hasKey)
        {
            Skip = $"Live test: set {OptInVariable}=1 and {KeyVariable} to call the real Anthropic API. It costs a few cents.";
        }
    }
}
