namespace GitHubReviewApp.Tests.Infrastructure;

/// <summary>
/// Concrete HttpResponseData for unit tests, so a function's status code, headers and body can be asserted
/// without an Azure Functions runtime.
/// </summary>
internal sealed class FakeHttpResponseData : HttpResponseData
{
    public FakeHttpResponseData(FunctionContext context)
        : base(context)
    {
        Headers = new HttpHeadersCollection();
        Body = new MemoryStream();
        StatusCode = HttpStatusCode.OK;
    }

    public override HttpStatusCode StatusCode { get; set; }

    public override HttpHeadersCollection Headers { get; set; }

    public override Stream Body { get; set; }

    public override HttpCookies Cookies => new Mock<HttpCookies>().Object;

    /// <summary>Everything written to the response body, as text.</summary>
    public string ReadBodyText()
    {
        Body.Position = 0;
        return new StreamReader(Body, Encoding.UTF8, leaveOpen: true).ReadToEnd();
    }
}
