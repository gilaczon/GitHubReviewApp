namespace GitHubReviewApp.Tests.Infrastructure;

/// <summary>An HTTP client factory that hands out one client whatever name is asked for, so a test can put its own handler behind the code under test.</summary>
internal sealed class SingleClientFactory : IHttpClientFactory
{
    private readonly HttpClient _client;

    public SingleClientFactory(HttpClient client)
    {
        _client = client;
    }

    public HttpClient CreateClient(string name)
    {
        return _client;
    }
}
