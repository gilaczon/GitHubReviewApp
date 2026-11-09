namespace GitHubReviewApp.Tests.Models;

public class RuntimeSettingsTests
{
    [Fact]
    public void Default_GivenNoOverrides_Should_EnableReviewsWithTheCurrentModels()
    {
        // Arrange

        // Act
        var settings = RuntimeSettings.Default;

        // Assert — literal ids on purpose: these are what runs when the settings say nothing
        settings.IsReviewEnabled.Should().BeTrue();
        settings.SonnetModel.Should().Be("claude-sonnet-5-5");
        settings.OpusModel.Should().Be("claude-opus-5-5");
    }
}
