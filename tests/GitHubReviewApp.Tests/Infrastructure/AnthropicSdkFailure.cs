namespace GitHubReviewApp.Tests.Infrastructure;

/// <summary>
/// Produces the exception the Anthropic SDK itself throws for a given HTTP response, by driving the SDK's own streaming call against a fake
/// handler. Tests of failure handling use it so they follow the SDK's real mapping from a status code or a broken stream to an exception type,
/// whatever the application's own service does with that exception afterwards.
/// </summary>
internal static class AnthropicSdkFailure
{
    public static async Task<Exception> CaptureAsync(HttpResponseMessage response)
    {
        var client = new Anthropic.AnthropicClient
        {
            ApiKey = "sk-ant-test",
            HttpClient = new HttpClient(new TestHttpMessageHandler(_ => response)),
            MaxRetries = 0
        };

        var parameters = new Anthropic.Models.Messages.MessageCreateParams
        {
            Model = "claude-sonnet-5-5",
            MaxTokens = 1_000,
            Messages = [new Anthropic.Models.Messages.UserMessageParam { Content = "review this" }]
        };

        try
        {
            await Anthropic.SseAggregatorExtensions.Aggregate(client.Messages.CreateStreaming(parameters));
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new InvalidOperationException("The SDK call was expected to fail.");
    }
}
