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
            ["AnthropicApiKey"] = "sk-ant-test",
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
    public void MissingSecrets_GivenNeitherSecret_Should_NameBoth()
    {
        // Arrange
        var configuration = ConfigurationWith([]);

        // Act
        var missing = PreviewServices.MissingSecrets(configuration);

        // Assert
        missing.Should().Equal("AnthropicApiKey", "SlackWebhookUrl");
    }

    [Fact]
    public void MissingSecrets_GivenBothSecrets_Should_NameNone()
    {
        // Arrange
        var configuration = ConfigurationWith(BothSecrets());

        // Act
        var missing = PreviewServices.MissingSecrets(configuration);

        // Assert
        missing.Should().BeEmpty();
    }
}
