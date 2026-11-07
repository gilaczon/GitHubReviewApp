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
        options.MaxChunkBytes.Should().Be(300_000);
        options.MaxTotalBytes.Should().Be(1_200_000);
        options.MaxFileBytes.Should().Be(60_000);
        options.MaxFiles.Should().Be(150);
        options.MaxParallelChunks.Should().Be(3);
        options.MaxInlineComments.Should().Be(25);
        options.MaxFindings.Should().Be(60);
        options.LargePullRequestFileThreshold.Should().Be(50);
        options.LargePullRequestLineThreshold.Should().Be(2_000);
        options.MaxGuidelinesChars.Should().Be(20_000);
        options.MaxReviewDurationSeconds.Should().Be(1_200);
    }

    [Fact]
    public void EffectiveGuidelinePaths_GivenNoConfiguration_Should_PreferTheDedicatedFileOverClaudeMd()
    {
        // Arrange

        // Act
        var options = new ReviewOptions();

        // Assert
        options.EffectiveGuidelinePaths.Should().Equal(".github/ai-review.md", "CLAUDE.md");
    }

    [Fact]
    public void EffectiveGuidelinePaths_GivenPathsAreSet_Should_ReplaceTheDefaults()
    {
        // Arrange

        // Act
        var options = new ReviewOptions { GuidelinePaths = ["docs/rules.md"] };

        // Assert
        options.EffectiveGuidelinePaths.Should().Equal("docs/rules.md");
    }

    [Fact]
    public void EffectiveGuidelinePaths_GivenAnEmptyListInCode_Should_MeanNoGuidelinesAtAll()
    {
        // Arrange

        // Act
        var options = new ReviewOptions { GuidelinePaths = [] };

        // Assert
        options.EffectiveGuidelinePaths.Should().BeEmpty();
    }

    [Fact]
    public void Defaults_GivenNoConfiguration_Should_NotLimitWhoCanTriggerAReviewAndShouldCapProcessWideModelRequests()
    {
        // Arrange

        // Act
        var options = new ReviewOptions();

        // Assert
        options.AllowedAuthorAssociations.Should().BeEmpty();
        options.MaxConcurrentModelRequests.Should().Be(6);
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
