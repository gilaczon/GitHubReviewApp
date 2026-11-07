namespace GitHubReviewApp.Tests.Configuration;

public class ClaudeOptionsTests
{
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
    public void SectionName_GivenTheConfigurationLayout_Should_BeClaude()
    {
        // Arrange

        // Act
        var sectionName = ClaudeOptions.SectionName;

        // Assert
        sectionName.Should().Be("Claude");
    }
}
