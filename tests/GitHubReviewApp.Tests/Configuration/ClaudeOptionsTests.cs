namespace GitHubReviewApp.Tests.Configuration;

public class ClaudeOptionsTests
{
    // Design 5.10, USD per million tokens: model, input, output, cache write, cache read. Written out here on purpose, so that a price changed in
    // appsettings.json without the design (or the other way round) fails this test.
    public static IEnumerable<object[]> ShippedPrices()
    {
        yield return ["claude-sonnet-5-5", 2.00m, 10.00m, 2.50m, 0.20m];
        yield return ["claude-opus-5-5", 4.00m, 20.00m, 5.00m, 0.20m];
        yield return ["claude-sonnet-5", 2.00m, 10.00m, 2.50m, 0.20m];
        yield return ["claude-opus-5", 5.00m, 25.00m, 6.25m, 0.50m];
        yield return ["claude-opus-4-8", 5.00m, 25.00m, 6.25m, 0.50m];
    }

    [Fact]
    public void Defaults_GivenNoConfiguration_Should_UseTheCurrentSonnetModelAndRoomForThinkingPlusTheReview()
    {
        // Arrange

        // Act
        var options = new ClaudeOptions();

        // Assert — literal values on purpose: these are the fallbacks if the "Claude" section is ever missing
        options.Model.Should().Be("claude-sonnet-5-5");
        options.MaxOutputTokens.Should().Be(32_000, "thinking tokens count towards the limit, so it must cover the reasoning as well as the review");
        options.Effort.Should().Be("high");
        options.RequestTimeoutSeconds.Should().Be(600, "a streamed reply with thinking can run for minutes");
        options.MaxRetries.Should().Be(1, "every retry waits out the whole request again");
    }

    [Fact]
    public void Pricing_GivenNoConfiguration_Should_BeEmpty()
    {
        // Arrange

        // Act
        var options = new ClaudeOptions();

        // Assert
        options.Pricing.Should().BeEmpty("a price table that is not configured must not be made up");
    }

    [Fact]
    public void Pricing_GivenAModelAddedInOneLetterCase_Should_FindItInAnother()
    {
        // Arrange
        var options = new ClaudeOptions();

        // Act
        options.Pricing["Claude-Sonnet-5-5"] = new ModelPrice { InputPerMillionTokens = 2.00m };

        // Assert
        options.Pricing.Should().ContainKey("claude-sonnet-5-5");
    }

    [Theory]
    [MemberData(nameof(ShippedPrices))]
    public void Pricing_GivenTheShippedAppSettings_Should_PriceTheReviewModelsAndTheirFallbacks(
        string model,
        decimal inputPerMillionTokens,
        decimal outputPerMillionTokens,
        decimal cacheWritePerMillionTokens,
        decimal cacheReadPerMillionTokens)
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false)
            .Build();

        // Act
        var options = configuration.GetSection(ClaudeOptions.SectionName).Get<ClaudeOptions>()!;

        // Assert
        options.Pricing.Should().ContainKey(model).WhoseValue.Should().BeEquivalentTo(new ModelPrice
        {
            InputPerMillionTokens = inputPerMillionTokens,
            OutputPerMillionTokens = outputPerMillionTokens,
            CacheWritePerMillionTokens = cacheWritePerMillionTokens,
            CacheReadPerMillionTokens = cacheReadPerMillionTokens
        });
    }

    [Fact]
    public void SectionName_GivenTheConfigurationLayout_Should_BeClaude()
    {
        // Arrange

        // Act
        var sectionName = ClaudeOptions.SectionName;

        // Assert
        sectionName.Should().Be("Claude");
    }
}
