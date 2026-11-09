namespace GitHubReviewApp.ReviewPreview.Tests;

public class SlackWebhookPosterTests
{
    // Not a real webhook: the handler below answers every request.
    private const string WebhookUrl = "https://hooks.slack.example/services/T000/B000/not-a-real-secret";
    private const string Text = "*PASS*";

    private static (SlackWebhookPoster Sut, RecordingHandler Handler) Build(string? webhookUrl, HttpStatusCode status = HttpStatusCode.OK, string reply = "ok")
    {
        var handler = new RecordingHandler(status, reply);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [SlackWebhookPoster.WebhookUrlSettingName] = webhookUrl })
            .Build();

        return (new SlackWebhookPoster(new HttpClient(handler), configuration), handler);
    }

    [Fact]
    public async Task PostAsync_GivenAWebhook_Should_PostTheTextAsJsonToIt()
    {
        // Arrange
        var (sut, handler) = Build(WebhookUrl);

        // Act
        await sut.PostAsync(Text, CancellationToken.None);

        // Assert
        handler.RequestUri.Should().Be(new Uri(WebhookUrl));
        handler.Method.Should().Be(HttpMethod.Post);
        JsonDocument.Parse(handler.Body!).RootElement.GetProperty("text").GetString().Should().Be(Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://hooks.slack.example/services/T000/B000/plain-http")]
    [InlineData("not a url")]
    public async Task PostAsync_GivenNoUsableWebhook_Should_SayWhichSettingWithoutQuotingIt(string? webhookUrl)
    {
        // Arrange
        var (sut, handler) = Build(webhookUrl);

        // Act
        var act = () => sut.PostAsync(Text, CancellationToken.None);

        // Assert
        var failure = await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*SlackWebhookUrl*https*");
        failure.Which.Message.Should().NotContain("hooks.slack.example");
        handler.RequestUri.Should().BeNull();
    }

    [Fact]
    public async Task PostAsync_GivenSlackRefuses_Should_ThrowWithTheStatusAndSlacksCodeButNotTheUrl()
    {
        // Arrange
        var (sut, _) = Build(WebhookUrl, HttpStatusCode.BadRequest, "invalid_payload");

        // Act
        var act = () => sut.PostAsync(Text, CancellationToken.None);

        // Assert
        var failure = await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*HTTP 400 invalid_payload*");
        failure.Which.Message.Should().NotContain("not-a-real-secret");
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _reply;

        public RecordingHandler(HttpStatusCode status, string reply)
        {
            _status = status;
            _reply = reply;
        }

        public Uri? RequestUri { get; private set; }

        public HttpMethod? Method { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Method = request.Method;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(_status) { Content = new StringContent(_reply) };
        }
    }
}
