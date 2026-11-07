namespace GitHubReviewApp.Tests.Review;

/// <summary>
/// The sanitizer decides which lines are code, and a renderer decides again. At the top level they agree; inside a list item or a block quote
/// they can differ, and wherever they differ text the sanitizer thinks is code is posted raw. These tests judge sanitized text in every
/// setting the formatter puts it in, with an independent renderer.
/// </summary>
public class MarkdownContainerTests
{
    private const string Payload = "@victim <img src=\"https://attacker.example/x.png\"> [l](https://evil.example)";

    /// <summary>Inputs that read as a fence to the sanitizer and as something else to a renderer, each found by a reviewer reading the CommonMark rules.</summary>
    public static TheoryData<string> KnownContainerBypasses => new()
    {
        "- x\n  ```\n" + Payload,
        "```\n\t```\n" + Payload,
        "- ~~~\n  ```\n  ~~~\n  " + Payload,
        "> ```\n" + Payload,
        "1. x\n   ~~~\n" + Payload,
        "* x\n\t```\n" + Payload,
        "- a\n\n      ```\n" + Payload,
        "> - x\n>   ```\n" + Payload,
        "+ x\n  ~~~~\n" + Payload,
        "10) x\n    ```\n" + Payload
    };

    [Theory]
    [MemberData(nameof(KnownContainerBypasses))]
    public void LiveSettings_GivenAKnownContainerBypassThatIsNotSanitized_Should_FindASettingWhereItGoesLiveSoTheOracleCanFail(string input)
    {
        // Arrange

        // Act
        var settings = MarkdownContexts.LiveSettings(input);

        // Assert
        settings.Should().NotBeEmpty("the input must go live somewhere, or it proves nothing about the sanitizer");
    }

    [Theory]
    [MemberData(nameof(KnownContainerBypasses))]
    public void Neutralize_GivenAKnownContainerBypass_Should_LeaveNothingLiveInAnySetting(string input)
    {
        // Arrange

        // Act
        var result = MarkdownSanitizer.Neutralize(input);

        // Assert
        MarkdownContexts.LiveSettings(result).Should().BeEmpty($"it was sanitized to {JsonSerializer.Serialize(result)}");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Neutralize_GivenGeneratedDocumentsOfListsQuotesAndFences_Should_LeaveNothingLiveInAnySetting(int seed)
    {
        // Arrange
        var documents = StructuredMarkdownGenerator.Generate(seed, count: 4_000);

        // Act
        var violations = documents
            .Select(document => (Document: document, Sanitized: MarkdownSanitizer.Neutralize(document)))
            .Select(item => (item.Document, item.Sanitized, Settings: MarkdownContexts.LiveSettings(item.Sanitized)))
            .Where(item => item.Settings.Count > 0)
            .Select(item => $"{JsonSerializer.Serialize(item.Document)} => {JsonSerializer.Serialize(item.Sanitized)} is live as {string.Join(" and ", item.Settings)}")
            .Take(8)
            .ToList();

        // Assert
        violations.Should().BeEmpty();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Neutralize_GivenGeneratedDocumentsOfListsQuotesAndFences_Should_RenderNoListOrQuoteElement(int seed)
    {
        // Arrange
        string[] containerElements = ["ul", "ol", "li", "blockquote"];
        var documents = StructuredMarkdownGenerator.Generate(seed, count: 4_000);

        // Act
        var offenders = documents
            .Select(document => (Document: document, Sanitized: MarkdownSanitizer.Neutralize(document)))
            .Where(item => containerElements.Any(name => MarkdownOracle.CountElements(item.Sanitized, name) > 0))
            .Select(item => $"{JsonSerializer.Serialize(item.Document)} => {JsonSerializer.Serialize(item.Sanitized)}")
            .Take(8)
            .ToList();

        // Assert — the sanitizer reads text line by line, which is exact only while no container is left for a renderer to read differently
        offenders.Should().BeEmpty();
    }

    [Fact]
    public void Neutralize_GivenTheGeneratorsDocumentsLeftUnsanitized_Should_FindManyThatGoLive()
    {
        // Arrange
        var documents = StructuredMarkdownGenerator.Generate(seed: 1, count: 4_000);

        // Act
        var liveCount = documents.Count(document => MarkdownContexts.LiveSettings(document).Count > 0);

        // Assert — the property test above means something only if the documents it feeds the sanitizer are dangerous to begin with
        liveCount.Should().BeGreaterThan(1_000);
    }
}
