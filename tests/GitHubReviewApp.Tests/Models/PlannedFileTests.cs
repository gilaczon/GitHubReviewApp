namespace GitHubReviewApp.Tests.Models;

public class PlannedFileTests
{
    private static PlannedFile Planned(string renderedDiff)
    {
        return new PlannedFile(new PullRequestFile("a.cs", "modified", 1, 0, "@@ -0,0 +1 @@\n+x"), renderedDiff, IsPartial: false);
    }

    [Fact]
    public void RenderedBytes_GivenAsciiText_Should_EqualItsLength()
    {
        // Arrange
        var planned = Planned("<file path=\"a.cs\">\n     1 |+x\n</file>");

        // Act
        var bytes = planned.RenderedBytes;

        // Assert
        bytes.Should().Be(planned.RenderedDiff.Length);
    }

    [Theory]
    [InlineData("日本語", 9)]       // three characters, three bytes each
    [InlineData("é", 2)]                   // one accented letter, two bytes
    [InlineData("😀", 4)]             // one emoji: two UTF-16 characters, four bytes
    [InlineData("", 0)]
    public void RenderedBytes_GivenNonAsciiText_Should_CountUtf8BytesNotCharacters(string text, int expectedBytes)
    {
        // Arrange
        var planned = Planned(text);

        // Act
        var bytes = planned.RenderedBytes;

        // Assert
        bytes.Should().Be(expectedBytes);
    }

    [Fact]
    public void TotalBytes_GivenAChunkOfSeveralFiles_Should_AddUpTheirUtf8Sizes()
    {
        // Arrange
        var chunk = new ReviewChunk(0, [Planned("abc"), Planned("日本語")]);

        // Act
        var total = chunk.TotalBytes;

        // Assert
        total.Should().Be(3 + 9);
    }
}
