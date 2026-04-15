namespace GitHubReviewApp.Tests.Infrastructure;

/// <summary>
/// A delegating handler that intercepts outgoing HttpClient requests and returns a
/// pre-configured response. Supports both sync and async response factories.
/// Captures all sent requests for assertion purposes.
/// </summary>
internal sealed class TestHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

    /// <summary>The most recent request sent through this handler.</summary>
    public HttpRequestMessage? LastRequest { get; private set; }

    /// <summary>All requests sent through this handler in chronological order.</summary>
    public List<HttpRequestMessage> Requests { get; } = new();

    /// <summary>Creates a handler with a synchronous response factory.</summary>
    public TestHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : this(req => Task.FromResult(handler(req)))
    {
    }

    /// <summary>Creates a handler with an asynchronous response factory.</summary>
    public TestHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
    {
        _handler = handler;
    }

    /// <summary>
    /// Convenience constructor: always returns the given status code with the
    /// provided JSON body and Content-Type: application/json.
    /// </summary>
    public TestHttpMessageHandler(string responseJson, HttpStatusCode statusCode = HttpStatusCode.OK)
        : this(_ => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
        }))
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        Requests.Add(request);
        return await _handler(request);
    }
}
