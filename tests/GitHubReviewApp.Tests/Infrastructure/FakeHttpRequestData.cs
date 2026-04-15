namespace GitHubReviewApp.Tests.Infrastructure;

/// <summary>
/// Minimal concrete implementation of the abstract HttpRequestData for use in unit tests.
/// Allows setting Body, Headers, and Method without a real Azure Functions runtime.
/// </summary>
internal sealed class FakeHttpRequestData : HttpRequestData
{
    private readonly HttpHeadersCollection _headers = new();

    public FakeHttpRequestData(
        FunctionContext context,
        Stream body,
        string method = "POST",
        Uri? url = null)
        : base(context)
    {
        Body = body;
        Method = method;
        Url = url ?? new Uri("https://localhost/api/webhook/github");
    }

    public override Stream Body { get; }
    public override HttpHeadersCollection Headers => _headers;
    public override IReadOnlyCollection<IHttpCookie> Cookies => Array.Empty<IHttpCookie>();
    public override Uri Url { get; }
    public override IEnumerable<ClaimsIdentity> Identities => Enumerable.Empty<ClaimsIdentity>();
    public override string Method { get; }

    public override HttpResponseData CreateResponse()
    {
        var responseMock = new Mock<HttpResponseData>(FunctionContext);
        responseMock.Setup(x => x.Headers).Returns(new HttpHeadersCollection());
        return responseMock.Object;
    }

    /// <summary>Adds a header value to the fake request.</summary>
    public FakeHttpRequestData WithHeader(string name, string value)
    {
        _headers.Add(name, value);
        return this;
    }

    /// <summary>
    /// Convenience factory: creates a POST request from a UTF-8 JSON body string.
    /// </summary>
    public static FakeHttpRequestData Create(
        string body,
        FunctionContext? context = null,
        params (string Name, string Value)[] headers)
    {
        context ??= CreateFunctionContext();
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(body));
        var request = new FakeHttpRequestData(context, stream);
        foreach (var (name, value) in headers)
            request._headers.Add(name, value);
        return request;
    }

    /// <summary>Creates a minimal stub FunctionContext sufficient for HttpRequestData instantiation.</summary>
    public static FunctionContext CreateFunctionContext()
    {
        var ctxMock = new Mock<FunctionContext>();
        var servicesMock = new Mock<IServiceProvider>();
        ctxMock.Setup(x => x.InstanceServices).Returns(servicesMock.Object);
        return ctxMock.Object;
    }
}
