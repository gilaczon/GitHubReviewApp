namespace GitHubReviewApp.Tests.Infrastructure;

/// <summary>One HTTP request the fake backend received.</summary>
internal sealed record RecordedCall(
    HttpMethod Method,
    Uri Uri,
    string? Body,
    string? Authorization,
    IReadOnlyDictionary<string, string> Headers)
{
    public string Host => Uri.Host;

    public string Path => Uri.AbsolutePath;

    public bool IsAnthropic => Host == "api.anthropic.com";

    public bool IsGitHub => Host == "api.github.com";

    public JsonElement BodyJson()
    {
        return JsonDocument.Parse(Body!).RootElement.Clone();
    }

    public string? Header(string name)
    {
        return Headers.TryGetValue(name, out var value) ? value : null;
    }
}
