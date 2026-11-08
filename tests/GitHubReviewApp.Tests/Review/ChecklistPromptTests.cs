namespace GitHubReviewApp.Tests.Review;

/// <summary>
/// The system prompt is the owner's reviewer prompt from the spec, word for word, with one section added that tells the model how the pull request
/// is laid out in the user message (design 5.1). The expected text is read from the spec file, so the embedded copy cannot drift from it.
/// </summary>
public class ChecklistPromptTests
{
    private const string OpeningTag = "<reviewer_system_prompt>\n";
    private const string ClosingTag = "\n</reviewer_system_prompt>";
    private const string ChecklistAnchor = "\n\n# MoneyMe Code Review Checklist";

    private static string PromptInTheSpec()
    {
        var specPath = Path.Combine(RepositoryRoot.FullPath, "docs", "superpowers", "specs", "2026-11-07-checklist-review-spec.md");
        var spec = File.ReadAllText(specPath).Replace("\r\n", "\n");

        var start = spec.IndexOf(OpeningTag, StringComparison.Ordinal) + OpeningTag.Length;
        var end = spec.IndexOf(ClosingTag, start, StringComparison.Ordinal);

        return spec[start..end];
    }

    [Fact]
    public void SystemPrompt_GivenTheSpec_Should_BeTheReviewerSystemPromptVerbatimWithOnlyThePrInputSectionAdded()
    {
        // Arrange
        var expected = PromptInTheSpec().Replace(ChecklistAnchor, "\n\n" + ChecklistPrompt.PrInputSection + ChecklistAnchor);

        // Act
        var prompt = ChecklistPrompt.SystemPrompt;

        // Assert
        prompt.Should().Be(expected);
    }

    [Fact]
    public void SystemPrompt_GivenTheResource_Should_HaveNoCarriageReturns()
    {
        // Arrange — the embedded file may have been checked out with CRLF line endings, and the model is sent LF only
        const string carriageReturn = "\r";

        // Act
        var prompt = ChecklistPrompt.SystemPrompt;

        // Assert
        prompt.Should().NotBeEmpty();
        prompt.Should().NotContain(carriageReturn);
    }

    [Theory]
    [InlineData("<pr_metadata>")]
    [InlineData("<pr_description_untrusted>")]
    [InlineData("<changed_files>")]
    [InlineData("<diff>")]
    [InlineData("line number in the new version")]
    public void PrInputSection_GivenTheTags_Should_NameEachTagAndTheDiffLineFormat(string expectedFragment)
    {
        // Arrange — nothing to set up: the section is a constant

        // Act
        var section = ChecklistPrompt.PrInputSection;

        // Assert
        section.Should().Contain(expectedFragment);
    }
}
