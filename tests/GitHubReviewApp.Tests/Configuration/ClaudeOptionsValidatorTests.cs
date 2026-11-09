namespace GitHubReviewApp.Tests.Configuration;

public class ClaudeOptionsValidatorTests
{
    private static readonly ClaudeOptionsValidator _sut = new();

    public static IEnumerable<object[]> SettingsThatCannotWork()
    {
        yield return [new ClaudeOptions { MaxOutputTokens = 0 }, "Claude:MaxOutputTokens"];
        yield return [new ClaudeOptions { MaxOutputTokens = -5 }, "Claude:MaxOutputTokens"];
        yield return [new ClaudeOptions { RequestTimeoutSeconds = 0 }, "Claude:RequestTimeoutSeconds"];
        yield return [new ClaudeOptions { RequestTimeoutSeconds = -1 }, "Claude:RequestTimeoutSeconds"];
        yield return [new ClaudeOptions { MaxRetries = -1 }, "Claude:MaxRetries"];
        yield return [new ClaudeOptions { Effort = "turbo" }, "Claude:Effort"];

        // Too large to work, and not reported until the first review: a timeout over about 49 days cannot even be started, and a request that may take
        // longer than an hour is past any Functions timeout
        yield return [new ClaudeOptions { RequestTimeoutSeconds = 3_601 }, "Claude:RequestTimeoutSeconds"];
        yield return [new ClaudeOptions { RequestTimeoutSeconds = int.MaxValue }, "Claude:RequestTimeoutSeconds"];
        yield return [new ClaudeOptions { MaxRetries = 11 }, "Claude:MaxRetries"];
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(3_600, 10)]
    [InlineData(600, 1)]
    public void Validate_GivenTheSmallestAndLargestSettingsThatWork_Should_Succeed(int requestTimeoutSeconds, int maxRetries)
    {
        // Arrange
        var options = new ClaudeOptions { RequestTimeoutSeconds = requestTimeoutSeconds, MaxRetries = maxRetries };

        // Act
        var result = _sut.Validate(Options.DefaultName, options);

        // Assert
        result.Succeeded.Should().BeTrue();
    }

    public static IEnumerable<object[]> NegativePrices()
    {
        yield return [new ModelPrice { InputPerMillionTokens = -0.01m }, "InputPerMillionTokens"];
        yield return [new ModelPrice { OutputPerMillionTokens = -0.01m }, "OutputPerMillionTokens"];
        yield return [new ModelPrice { CacheWritePerMillionTokens = -0.01m }, "CacheWritePerMillionTokens"];
        yield return [new ModelPrice { CacheReadPerMillionTokens = -0.01m }, "CacheReadPerMillionTokens"];
    }

    [Theory]
    [MemberData(nameof(NegativePrices))]
    public void Validate_GivenANegativePrice_Should_Fail(ModelPrice price, string expectedPrice)
    {
        // Arrange — a negative price would take money off the cost of every review that used the model
        var options = CreateOptionsWithPricing("claude-sonnet-5-5", price);

        // Act
        var result = _sut.Validate(Options.DefaultName, options);

        // Assert
        result.Failed.Should().BeTrue();
        result.Failures.Should().ContainSingle().Which.Should().Contain("Claude:Pricing").And.Contain("claude-sonnet-5-5").And.Contain(expectedPrice);
    }

    [Fact]
    public void Validate_GivenAPriceOfZero_Should_Succeed()
    {
        // Arrange — "at least 0": a model that costs nothing, or a token kind it does not bill, is a price
        var options = CreateOptionsWithPricing("claude-free", new ModelPrice());

        // Act
        var result = _sut.Validate(Options.DefaultName, options);

        // Assert
        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_GivenAPriceWithNoModelName_Should_Fail(string model)
    {
        // Arrange — the table is looked up by model id, so a price with a blank id would never be used
        var options = CreateOptionsWithPricing(model, new ModelPrice { InputPerMillionTokens = 2.00m });

        // Act
        var result = _sut.Validate(Options.DefaultName, options);

        // Assert
        result.Failed.Should().BeTrue();
        result.Failures.Should().ContainSingle().Which.Should().Contain("Claude:Pricing").And.Contain("model");
    }

    [Fact]
    public void Validate_GivenSeveralBadPrices_Should_ReportEveryOneSoTheyCanBeFixedInOneGo()
    {
        // Arrange
        var options = new ClaudeOptions
        {
            Pricing = new Dictionary<string, ModelPrice>
            {
                ["claude-sonnet-5-5"] = new ModelPrice { InputPerMillionTokens = -1m, OutputPerMillionTokens = -2m },
                ["claude-opus-5-5"] = new ModelPrice { CacheReadPerMillionTokens = -3m }
            }
        };

        // Act
        var result = _sut.Validate(Options.DefaultName, options);

        // Assert
        result.Failures.Should().HaveCount(3);
    }

    [Fact]
    public void Validate_GivenTheDefaultOptions_Should_Succeed()
    {
        // Arrange

        // Act
        var result = _sut.Validate(Options.DefaultName, new ClaudeOptions());

        // Assert
        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("HIGH")]
    [InlineData(" xhigh ")]
    [InlineData("max")]
    public void Validate_GivenAnEffortTheServiceUnderstands_Should_Succeed(string? effort)
    {
        // Arrange

        // Act
        var result = _sut.Validate(Options.DefaultName, new ClaudeOptions { Effort = effort });

        // Assert
        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(SettingsThatCannotWork))]
    public void Validate_GivenASettingThatCannotWork_Should_FailNamingTheSetting(ClaudeOptions options, string expectedSetting)
    {
        // Arrange

        // Act
        var result = _sut.Validate(Options.DefaultName, options);

        // Assert
        result.Failed.Should().BeTrue();
        result.Failures.Should().ContainSingle().Which.Should().Contain(expectedSetting);
    }

    [Fact]
    public void Validate_GivenSeveralBadSettings_Should_ReportEveryOneSoTheyCanBeFixedInOneGo()
    {
        // Arrange
        var options = new ClaudeOptions { MaxOutputTokens = 0, RequestTimeoutSeconds = 0, MaxRetries = -1 };

        // Act
        var result = _sut.Validate(Options.DefaultName, options);

        // Assert
        result.Failures.Should().HaveCount(3);
    }

    [Fact]
    public void KnownEfforts_GivenTheServicesOwnParsing_Should_BeEveryValueItAccepts()
    {
        // Arrange

        // Act
        var efforts = ClaudeOptions.KnownEfforts;

        // Assert — a theory in ClaudeServiceTests sends each of these; the two lists must not drift apart
        efforts.Should().BeEquivalentTo("low", "medium", "high", "xhigh", "max");
    }

    private static ClaudeOptions CreateOptionsWithPricing(string model, ModelPrice price)
    {
        return new ClaudeOptions { Pricing = new Dictionary<string, ModelPrice> { [model] = price } };
    }
}
