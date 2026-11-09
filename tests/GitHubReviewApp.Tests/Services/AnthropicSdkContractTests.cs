namespace GitHubReviewApp.Tests.Services;

/// <summary>
/// What <see cref="ClaudeService"/> relies on the Anthropic SDK to do. The service relabels one kind of SDK exception as a failure worth retrying and
/// leaves the others alone, telling them apart by what the SDK puts in them. These fail when an SDK upgrade changes that, which is the cue to look at
/// the service again before a changed reply starts being retried (or a dropped one stops being). With the refusal fallback on, the service also reads
/// what the SDK assembles from a beta stream: the answer's text around a fallback block, and the attempts in <c>usage.iterations</c>.
/// </summary>
public class AnthropicSdkContractTests
{
    private static async Task<BetaMessages.BetaMessage> AggregateBetaAsync(HttpResponseMessage response)
    {
        var client = new Anthropic.AnthropicClient
        {
            ApiKey = "sk-ant-test",
            HttpClient = new HttpClient(new TestHttpMessageHandler(_ => response)),
            MaxRetries = 0
        };

        var parameters = new BetaMessages.MessageCreateParams
        {
            Model = "claude-sonnet-5-5",
            MaxTokens = 1_000,
            Messages = [new BetaMessages.BetaUserMessageParam { Content = "review this" }]
        };

        return await Anthropic.SseAggregatorExtensions.Aggregate(client.Beta.Messages.CreateStreaming(parameters));
    }

    [Fact]
    public async Task Aggregate_GivenABetaStreamWithAFallbackBlockBetweenTwoTextBlocks_Should_KeepBothTextBlocksInOrder()
    {
        // Arrange — the requested model declined part way through its answer and the fallback model went on from there
        var response = AnthropicFixtures.Message(
            [
                AnthropicFixtures.TextBlock("{\"summary\":\"Adds"),
                AnthropicFixtures.FallbackBlock("claude-sonnet-5-5", "claude-sonnet-5"),
                AnthropicFixtures.TextBlock(" retries.\"}")
            ]);

        // Act
        var message = await AggregateBetaAsync(response);

        // Assert
        message.Content.Select(block => block.Type.Raw()).Should().Equal("text", "fallback", "text");
        message.Content.Select(block => block.Value).OfType<BetaMessages.BetaTextBlock>().Select(block => block.Text)
            .Should().Equal("{\"summary\":\"Adds", " retries.\"}");
        var fallback = message.Content[1].Value.Should().BeOfType<BetaMessages.BetaFallbackBlock>().Subject;
        fallback.From.Model.Raw().Should().Be("claude-sonnet-5-5");
        fallback.To.Model.Raw().Should().Be("claude-sonnet-5");
    }

    [Fact]
    public async Task Aggregate_GivenABetaStreamWithAFallbackBlockMidStream_Should_NameTheModelTheBlockHandsOverTo()
    {
        // Arrange — the stream opened on the requested model, which declined part way through; ClaudeService reports message.Model as the model
        // that answered, so it relies on the aggregate moving the model on at the fallback block
        var response = AnthropicFixtures.Message(
            [
                AnthropicFixtures.TextBlock("{\"summary\":\"Adds"),
                AnthropicFixtures.FallbackBlock("claude-sonnet-5-5", "claude-sonnet-5"),
                AnthropicFixtures.TextBlock(" retries.\"}")
            ],
            model: "claude-sonnet-5-5");

        // Act
        var message = await AggregateBetaAsync(response);

        // Assert
        message.Model.Raw().Should().Be("claude-sonnet-5");
    }

    [Fact]
    public async Task Aggregate_GivenABetaStreamWithUsageIterations_Should_ExposeOneIterationPerAttemptWithItsModel()
    {
        // Arrange — the requested model declined after reading the prompt, and the fallback model answered
        var response = AnthropicFixtures.Message(
            [AnthropicFixtures.FallbackBlock("claude-sonnet-5-5", "claude-sonnet-5"), AnthropicFixtures.TextBlock(AnthropicFixtures.ChecklistJson())],
            model: "claude-sonnet-5",
            iterations:
            [
                AnthropicFixtures.Iteration("message", "claude-sonnet-5-5", inputTokens: 1_000, outputTokens: 20, cacheReadTokens: 300, cacheCreationTokens: 50),
                AnthropicFixtures.Iteration("fallback_message", "claude-sonnet-5", inputTokens: 1_100, outputTokens: 400, cacheReadTokens: 0, cacheCreationTokens: 900)
            ]);

        // Act
        var message = await AggregateBetaAsync(response);

        // Assert
        message.Usage.Iterations.Should().NotBeNull();
        message.Usage.Iterations!
            .Select(iteration => (iteration.Type.Raw(), iteration.Model?.Raw(), iteration.InputTokens, iteration.OutputTokens, iteration.CacheReadInputTokens, iteration.CacheCreationInputTokens))
            .Should().Equal(
                ("message", "claude-sonnet-5-5", 1_000L, 20L, 300L, 50L),
                ("fallback_message", "claude-sonnet-5", 1_100L, 400L, 0L, 900L));
    }

    [Fact]
    public async Task Aggregate_GivenAStreamThatEndsBeforeTheModelFinishes_Should_ThrowInvalidDataWithNoInnerExceptionAndTheEndMessageText()
    {
        // Arrange
        var response = AnthropicFixtures.StreamThatEndsBeforeTheModelFinishes();

        // Act
        var failure = await AnthropicSdkFailure.CaptureAsync(response);

        // Assert
        failure.Should().BeOfType<AnthropicInvalidDataException>();
        failure.InnerException.Should().BeNull("the service reads a missing inner exception as 'the reply was cut off', not 'the reply was malformed'");
        failure.Message.Should().Contain("End message");
    }

    [Fact]
    public async Task Aggregate_GivenABetaStreamThatEndsBeforeTheModelFinishes_Should_ThrowInvalidDataWithNoInnerExceptionAndTheEndMessageText()
    {
        // Arrange — the refusal fallback is on as the app ships, so a run reads the beta stream, and its cut-off must look like the standard one's
        var response = AnthropicFixtures.StreamThatEndsBeforeTheModelFinishes();

        // Act
        var act = () => AggregateBetaAsync(response);

        // Assert
        var failure = (await act.Should().ThrowAsync<AnthropicInvalidDataException>()).Which;
        failure.InnerException.Should().BeNull("the service reads a missing inner exception as 'the reply was cut off', not 'the reply was malformed'");
        failure.Message.Should().Contain("End message");
    }

    [Fact]
    public async Task Aggregate_GivenAnEventThatIsNotJson_Should_ThrowInvalidDataWithAJsonExceptionInside()
    {
        // Arrange
        var response = AnthropicFixtures.StreamWithAnEventThatIsNotJson();

        // Act
        var failure = await AnthropicSdkFailure.CaptureAsync(response);

        // Assert
        failure.Should().BeOfType<AnthropicInvalidDataException>();
        failure.InnerException.Should().BeOfType<JsonException>();
    }

    [Fact]
    public async Task Aggregate_GivenTextThatIsNotValidUnicode_Should_ThrowInvalidDataWithAJsonExceptionInside()
    {
        // Arrange — half of a surrogate pair in the text. The SDK refuses the event, so ChecklistAnswerReader's "not valid Unicode" reason cannot
        // be reached through the service: the call fails as one with an unreadable event does, and is not retried
        var response = AnthropicFixtures.StreamWithTextThatIsNotValidUnicode();

        // Act
        var act = () => AggregateBetaAsync(response);

        // Assert
        (await act.Should().ThrowAsync<AnthropicInvalidDataException>()).Which.InnerException.Should().BeOfType<JsonException>();
    }

    [Fact]
    public async Task Aggregate_GivenTheMonthlySpendLimit_Should_ThrowARateLimitExceptionThatCarriesTheResponseBody()
    {
        // Arrange — the classifier tells a spend cap from a rate limit by the error code in the body, so the SDK must keep the body
        var response = AnthropicFixtures.SpendLimitReached();

        // Act
        var failure = await AnthropicSdkFailure.CaptureAsync(response);

        // Assert
        failure.Should().BeOfType<AnthropicRateLimitException>().Which.ResponseBody.Should().Contain("enforced_spend_limit_reached");
    }
}
