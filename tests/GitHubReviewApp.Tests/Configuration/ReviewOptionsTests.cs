namespace GitHubReviewApp.Tests.Configuration;

public class ReviewOptionsTests
{
    [Fact]
    public void Defaults_GivenNoConfiguration_Should_UseTheDocumentedBudgetsAndThresholds()
    {
        // Arrange

        // Act
        var options = new ReviewOptions();

        // Assert — literal values on purpose: these are the fallbacks if the "Review" section is ever missing
        options.MaxFileBytes.Should().Be(60_000);
        options.MaxReviewDurationSeconds.Should().Be(1_200);
        options.MaxDiffBytes.Should().Be(180_000);
        options.MaxListedFiles.Should().Be(1_000);
        options.MaxDescriptionChars.Should().Be(10_000);
        options.OpusLineThreshold.Should().Be(40);
        options.HighRiskRepositories.Should().BeEmpty();
    }

    [Fact]
    public void Defaults_GivenNoConfiguration_Should_NotLimitWhoCanTriggerAReviewAndShouldCapProcessWideModelRequests()
    {
        // Arrange

        // Act
        var options = new ReviewOptions();

        // Assert
        options.AllowedAuthorAssociations.Should().BeEmpty();
        options.MaxConcurrentModelRequests.Should().Be(12);
    }

    [Fact]
    public void EffectiveAllowedAuthorAssociations_GivenPaddedMixedCaseAndRepeatedEntries_Should_BeTrimmedUpperCaseAndDistinct()
    {
        // Arrange — the validator and the webhook receiver must read the setting the same way
        var options = new ReviewOptions { AllowedAuthorAssociations = [" member", "Owner ", "OWNER", "  "] };

        // Act
        var effective = options.EffectiveAllowedAuthorAssociations;

        // Assert
        effective.Should().Equal("MEMBER", "OWNER");
    }

    [Fact]
    public void EffectiveAllowedAuthorAssociations_GivenNoEntries_Should_BeEmptyWhichMeansEveryone()
    {
        // Arrange
        var options = new ReviewOptions();

        // Act
        var effective = options.EffectiveAllowedAuthorAssociations;

        // Assert
        effective.Should().BeEmpty();
    }

    [Fact]
    public void SectionName_GivenTheConfigurationLayout_Should_BeReview()
    {
        // Arrange

        // Act
        var sectionName = ReviewOptions.SectionName;

        // Assert
        sectionName.Should().Be("Review");
    }
}
