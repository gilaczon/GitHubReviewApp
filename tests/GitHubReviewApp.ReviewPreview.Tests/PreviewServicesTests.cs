namespace GitHubReviewApp.ReviewPreview.Tests;

public class PreviewServicesTests
{
    private static IConfiguration ConfigurationWith(Dictionary<string, string?> secrets)
    {
        return new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false)
            .AddInMemoryCollection(secrets)
            .Build();
    }

    private static Dictionary<string, string?> BothSecrets()
    {
        return new Dictionary<string, string?>
        {
            ["GitHubReviewAppAnthropicApiKey"] = "sk-ant-test",
            [SlackWebhookPoster.WebhookUrlSettingName] = "https://hooks.slack.example/services/T000/B000/not-a-real-secret"
        };
    }

    [Fact]
    public void Build_GivenTheAppsSettings_Should_ResolveTheRunnerWithThePreviewStandIns()
    {
        // Arrange
        var configuration = ConfigurationWith(BothSecrets());

        // Act
        using var provider = PreviewServices.Build(configuration, Mock.Of<IGitHubTokenSource>());

        // Assert
        provider.GetRequiredService<PreviewRunner>().Should().NotBeNull();
        provider.GetRequiredService<IGitHubService>().Should().BeOfType<PreviewGitHubService>();
        provider.GetRequiredService<IGitHubAppAuthService>().Should().BeOfType<PreviewAuthService>();
        provider.GetRequiredService<ISlackPoster>().Should().BeOfType<SlackWebhookPoster>();
    }

    [Fact]
    public void Build_GivenOneRun_Should_ShareOneCaptureBetweenTheGitHubStandInAndTheRunner()
    {
        // Arrange — the comment the review writes through IGitHubService must be the one the runner reads
        using var provider = PreviewServices.Build(ConfigurationWith(BothSecrets()), Mock.Of<IGitHubTokenSource>());

        // Act
        var first = provider.GetRequiredService<PreviewCapture>();
        var second = provider.GetRequiredService<PreviewCapture>();

        // Assert
        first.Should().BeSameAs(second);
    }

    [Fact]
    public void SecretProblems_GivenNeitherSecret_Should_SayBothAreNotSet()
    {
        // Arrange
        var configuration = ConfigurationWith([]);

        // Act
        var problems = PreviewServices.SecretProblems(configuration);

        // Assert
        problems.Should().Equal("GitHubReviewAppAnthropicApiKey is not set.", "SlackWebhookUrl is not set.");
    }

    [Fact]
    public void SecretProblems_GivenBothSecrets_Should_FindNone()
    {
        // Arrange
        var configuration = ConfigurationWith(BothSecrets());

        // Act
        var problems = PreviewServices.SecretProblems(configuration);

        // Assert
        problems.Should().BeEmpty();
    }

    [Theory]
    [InlineData("\u0016")]
    [InlineData("anthropic-key-without-the-prefix")]
    [InlineData("sk-ant-test with-a-space")]
    [InlineData("sk-ant-test\u0016")]
    public void SecretProblems_GivenAnApiKeyThatIsPlainlyWrong_Should_SayItIsNotAnAnthropicKeyWithoutQuotingIt(string apiKey)
    {
        // Arrange — "\u0016" is the Ctrl+V a failed paste leaves behind
        var configuration = ConfigurationWith(new Dictionary<string, string?>(BothSecrets()) { ["GitHubReviewAppAnthropicApiKey"] = apiKey });

        // Act
        var problems = PreviewServices.SecretProblems(configuration);

        // Assert
        problems.Should().ContainSingle().Which.Should().Match("GitHubReviewAppAnthropicApiKey is not an Anthropic API key*sk-ant-*");
        problems[0].Should().NotContain(apiKey);
    }

    [Theory]
    [InlineData("\u0016")]
    [InlineData("hooks.slack.example/services/T000/B000/no-scheme")]
    [InlineData("http://hooks.slack.example/services/T000/B000/plain-http")]
    [InlineData("https://hooks.slack.example/services/T000/B000/ends-with-a-space ")]
    public void SecretProblems_GivenAWebhookUrlThatIsPlainlyWrong_Should_SayItIsNotAnHttpsUrlWithoutQuotingIt(string webhookUrl)
    {
        // Arrange
        var configuration = ConfigurationWith(
            new Dictionary<string, string?>(BothSecrets()) { [SlackWebhookPoster.WebhookUrlSettingName] = webhookUrl });

        // Act
        var problems = PreviewServices.SecretProblems(configuration);

        // Assert
        problems.Should().ContainSingle().Which.Should().Match("SlackWebhookUrl is not an https URL*");
        problems[0].Should().NotContain("hooks.slack.example");
    }
}
