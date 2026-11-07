namespace GitHubReviewApp.Tests.Services;

/// <summary>
/// What <see cref="ClaudeService"/> relies on the Anthropic SDK to do. The service relabels one kind of SDK exception as a failure worth retrying and
/// leaves the others alone, telling them apart by what the SDK puts in them. These fail when an SDK upgrade changes that, which is the cue to look at
/// the service again before a changed reply starts being retried (or a dropped one stops being).
/// </summary>
public class AnthropicSdkContractTests
{
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
