namespace GitHubReviewApp.Tests.Services;

public class CostCalculatorTests
{
    private const string SonnetModel = "claude-sonnet-5-5";
    private const string OpusModel = "claude-opus-5-5";

    // Written out here and not read from appsettings.json: these tests are about the sum, and the shipped table has its own test.
    private static readonly Dictionary<string, ModelPrice> _prices = new(StringComparer.OrdinalIgnoreCase)
    {
        [SonnetModel] = new ModelPrice
        {
            InputPerMillionTokens = 2.00m,
            OutputPerMillionTokens = 10.00m,
            CacheWritePerMillionTokens = 2.50m,
            CacheReadPerMillionTokens = 0.20m
        },
        [OpusModel] = new ModelPrice
        {
            InputPerMillionTokens = 4.00m,
            OutputPerMillionTokens = 20.00m,
            CacheWritePerMillionTokens = 5.00m,
            CacheReadPerMillionTokens = 0.20m
        }
    };

    [Fact]
    public void CostOf_GivenOneSonnetAttempt_Should_PriceEveryTokenKind()
    {
        // Arrange — every kind of token has its own price, and the cache write and cache read prices differ, so a swap of the two shows
        var attempts = new[] { new ModelAttempt(SonnetModel, new ClaudeUsage(InputTokens: 10_000, OutputTokens: 2_000, CacheReadInputTokens: 5_000, CacheCreationInputTokens: 1_000)) };

        // Act
        var cost = CostCalculator.CostOf(attempts, _prices);

        // Assert — 10,000 x 2.00 + 2,000 x 10.00 + 5,000 x 0.20 + 1,000 x 2.50 = 43,500, per million tokens
        cost.Should().Be(0.0435m);
    }

    [Fact]
    public void CostOf_GivenAFallbackAttemptOnAnotherModel_Should_SumBothAtTheirOwnPrices()
    {
        // Arrange — the first model refused, so the request moved to the other one: two attempts, each billed at its own model's rates
        var attempts = new[]
        {
            new ModelAttempt(SonnetModel, new ClaudeUsage(InputTokens: 100_000, OutputTokens: 10_000, CacheReadInputTokens: 0, CacheCreationInputTokens: 0)),
            new ModelAttempt(OpusModel, new ClaudeUsage(InputTokens: 50_000, OutputTokens: 5_000, CacheReadInputTokens: 20_000, CacheCreationInputTokens: 4_000))
        };

        // Act
        var cost = CostCalculator.CostOf(attempts, _prices);

        // Assert — Sonnet: 200,000 + 100,000 = 300,000. Opus: 200,000 + 100,000 + 4,000 + 20,000 = 324,000. At Sonnet's rates the second would cost 164,000.
        cost.Should().Be(0.624m);
    }

    [Fact]
    public void CostOf_GivenAnUnpricedModel_Should_ReturnNull()
    {
        // Arrange — a partial sum would read as the real cost, so one unknown price makes the whole cost unknown
        var usage = new ClaudeUsage(InputTokens: 1_000, OutputTokens: 1_000, CacheReadInputTokens: 0, CacheCreationInputTokens: 0);
        var attempts = new[] { new ModelAttempt(SonnetModel, usage), new ModelAttempt("claude-not-in-the-table", usage) };

        // Act
        var cost = CostCalculator.CostOf(attempts, _prices);

        // Assert
        cost.Should().BeNull();
    }

    [Fact]
    public void CostOf_GivenNoPriceTableAtAll_Should_ReturnNull()
    {
        // Arrange
        var attempts = new[] { new ModelAttempt(SonnetModel, ClaudeUsage.Empty) };

        // Act
        var cost = CostCalculator.CostOf(attempts, new Dictionary<string, ModelPrice>());

        // Assert — an attempt that used no tokens still names a model the table does not know
        cost.Should().BeNull();
    }

    [Fact]
    public void CostOf_GivenNoAttempts_Should_ReturnZero()
    {
        // Arrange — a review that never reached the model cost nothing, which is a cost and not an unknown one

        // Act
        var cost = CostCalculator.CostOf([], new Dictionary<string, ModelPrice>());

        // Assert
        cost.Should().Be(0m);
    }

    [Fact]
    public void CostOf_GivenAModelIdInAnotherLetterCase_Should_StillPriceIt()
    {
        // Arrange — a table that compares exactly, as a plain dictionary does: the lookup must not depend on how the caller built it
        var prices = new Dictionary<string, ModelPrice> { [SonnetModel] = _prices[SonnetModel] };
        var attempts = new[] { new ModelAttempt("Claude-Sonnet-5-5", new ClaudeUsage(InputTokens: 10_000, OutputTokens: 0, CacheReadInputTokens: 0, CacheCreationInputTokens: 0)) };

        // Act
        var cost = CostCalculator.CostOf(attempts, prices);

        // Assert
        cost.Should().Be(0.02m);
    }
}
