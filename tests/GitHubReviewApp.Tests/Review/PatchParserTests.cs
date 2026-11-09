namespace GitHubReviewApp.Tests.Review;

public class PatchParserTests
{
    private static string Patch(params string[] lines)
    {
        return string.Join("\n", lines);
    }

    [Fact]
    public void Parse_GivenSingleHunk_Should_NumberContextLinesOnBothSides()
    {
        // Arrange
        var patch = Patch(
            "@@ -10,3 +20,3 @@ class Foo",
            " first",
            "-old",
            "+new",
            " last");

        // Act
        var lines = PatchParser.Parse(patch);

        // Assert
        lines.Should().Equal(
            new PatchLine(PatchLineKind.HunkHeader, null, null, "@@ -10,3 +20,3 @@ class Foo"),
            new PatchLine(PatchLineKind.Context, 10, 20, "first"),
            new PatchLine(PatchLineKind.Removed, 11, null, "old"),
            new PatchLine(PatchLineKind.Added, null, 21, "new"),
            new PatchLine(PatchLineKind.Context, 12, 22, "last"));
    }

    [Fact]
    public void Parse_GivenOnlyAddedLines_Should_NumberOnlyTheNewSide()
    {
        // Arrange
        var patch = Patch("@@ -0,0 +1,2 @@", "+alpha", "+beta");

        // Act
        var lines = PatchParser.Parse(patch);

        // Assert
        lines.Skip(1).Should().Equal(
            new PatchLine(PatchLineKind.Added, null, 1, "alpha"),
            new PatchLine(PatchLineKind.Added, null, 2, "beta"));
    }

    [Fact]
    public void Parse_GivenOnlyRemovedLines_Should_NumberOnlyTheOldSide()
    {
        // Arrange
        var patch = Patch("@@ -5,2 +4,0 @@", "-alpha", "-beta");

        // Act
        var lines = PatchParser.Parse(patch);

        // Assert
        lines.Skip(1).Should().Equal(
            new PatchLine(PatchLineKind.Removed, 5, null, "alpha"),
            new PatchLine(PatchLineKind.Removed, 6, null, "beta"));
    }

    [Fact]
    public void Parse_GivenMultipleHunks_Should_RestartNumberingAtEachHeader()
    {
        // Arrange
        var patch = Patch(
            "@@ -1,1 +1,1 @@",
            " one",
            "@@ -50,1 +60,1 @@",
            " two");

        // Act
        var lines = PatchParser.Parse(patch);

        // Assert
        lines.Where(line => line.Kind == PatchLineKind.Context).Should().Equal(
            new PatchLine(PatchLineKind.Context, 1, 1, "one"),
            new PatchLine(PatchLineKind.Context, 50, 60, "two"));
    }

    [Fact]
    public void Parse_GivenHunkHeaderWithoutCounts_Should_ParseStartLines()
    {
        // Arrange
        var patch = Patch("@@ -3 +7 @@", "-before", "+after");

        // Act
        var lines = PatchParser.Parse(patch);

        // Assert
        lines.Skip(1).Should().Equal(
            new PatchLine(PatchLineKind.Removed, 3, null, "before"),
            new PatchLine(PatchLineKind.Added, null, 7, "after"));
    }

    [Fact]
    public void Parse_GivenNoNewlineMarker_Should_EmitMarkerWithoutAdvancingLineNumbers()
    {
        // Arrange
        var patch = Patch(
            "@@ -1,1 +1,2 @@",
            "-last",
            "\\ No newline at end of file",
            "+last",
            "+next");

        // Act
        var lines = PatchParser.Parse(patch);

        // Assert
        lines.Should().Contain(new PatchLine(PatchLineKind.NoNewlineMarker, null, null, " No newline at end of file"));
        lines.Last().Should().Be(new PatchLine(PatchLineKind.Added, null, 2, "next"));
    }

    [Fact]
    public void Parse_GivenTrailingNewline_Should_NotInventAnExtraContextLine()
    {
        // Arrange
        var patch = Patch("@@ -1,1 +1,1 @@", "+only") + "\n";

        // Act
        var lines = PatchParser.Parse(patch);

        // Assert
        lines.Should().HaveCount(2);
    }

    [Fact]
    public void Parse_GivenBlankLineInsideHunk_Should_TreatItAsEmptyContext()
    {
        // Arrange — some tools strip the single trailing space of an empty context line
        var patch = Patch("@@ -1,3 +1,3 @@", " a", "", " c");

        // Act
        var lines = PatchParser.Parse(patch);

        // Assert
        lines[2].Should().Be(new PatchLine(PatchLineKind.Context, 2, 2, string.Empty));
        lines[3].Should().Be(new PatchLine(PatchLineKind.Context, 3, 3, "c"));
    }

    [Fact]
    public void Parse_GivenACrlfPatchWhoseEmptyContextLineLostItsMarkerSpace_Should_NotLoseALineNumber()
    {
        // Arrange — in a file with CRLF endings the empty line is a bare carriage return once the marker space is stripped. Dropping it
        // would shift every later line number in the hunk by one, so comments would land on the wrong line.
        var patch = "@@ -1,3 +1,3 @@\n a\r\n\r\n-b\r\n+c\r";

        // Act
        var lines = PatchParser.Parse(patch);

        // Assert
        lines.Should().Equal(
            new PatchLine(PatchLineKind.HunkHeader, null, null, "@@ -1,3 +1,3 @@"),
            new PatchLine(PatchLineKind.Context, 1, 1, "a"),
            new PatchLine(PatchLineKind.Context, 2, 2, string.Empty),
            new PatchLine(PatchLineKind.Removed, 3, null, "b"),
            new PatchLine(PatchLineKind.Added, null, 3, "c"));
    }

    [Fact]
    public void Parse_GivenCrlfLineEndings_Should_KeepTheCarriageReturnOutOfTheText()
    {
        // Arrange
        var patch = "@@ -1 +1 @@\r\n-old\r\n+new\r\n context\r";

        // Act
        var lines = PatchParser.Parse(patch);

        // Assert
        lines.Select(line => line.Text).Should().Equal("@@ -1 +1 @@", "old", "new", "context");
    }

    [Fact]
    public void Parse_GivenAContentLineThatEndsWithTwoCarriageReturns_Should_OnlyDropTheLineEndingOne()
    {
        // Arrange — one carriage return is the CRLF line ending; a second belongs to the file's own content
        var patch = "@@ -0,0 +1 @@\n+text\r\r";

        // Act
        var lines = PatchParser.Parse(patch);

        // Assert
        lines[1].Text.Should().Be("text\r");
    }

    [Fact]
    public void Parse_GivenHunkHeaderWithCarriageReturn_Should_StillParseTheHeader()
    {
        // Arrange
        var patch = "@@ -1,1 +4,1 @@\r\n+value";

        // Act
        var lines = PatchParser.Parse(patch);

        // Assert
        lines[0].Should().Be(new PatchLine(PatchLineKind.HunkHeader, null, null, "@@ -1,1 +4,1 @@"));
        lines[1].Should().Be(new PatchLine(PatchLineKind.Added, null, 4, "value"));
    }

    [Fact]
    public void Parse_GivenContentThatLooksLikeFileHeaders_Should_TreatThemAsRegularLines()
    {
        // Arrange — a removed line "-- a/x" and an added line "++ b/x" are ordinary content inside a hunk
        var patch = Patch("@@ -1,1 +1,1 @@", "--- a/x", "+++ b/x");

        // Act
        var lines = PatchParser.Parse(patch);

        // Assert
        lines[1].Should().Be(new PatchLine(PatchLineKind.Removed, 1, null, "-- a/x"));
        lines[2].Should().Be(new PatchLine(PatchLineKind.Added, null, 1, "++ b/x"));
    }

    [Fact]
    public void Parse_GivenTextBeforeFirstHunk_Should_IgnoreIt()
    {
        // Arrange
        var patch = Patch("diff --git a/x b/x", "index 1..2 100644", "@@ -1,1 +1,1 @@", "+value");

        // Act
        var lines = PatchParser.Parse(patch);

        // Assert
        lines.Should().HaveCount(2);
        lines[0].Kind.Should().Be(PatchLineKind.HunkHeader);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void Parse_GivenNullOrBlankPatch_Should_ReturnNoLines(string? patch)
    {
        // Arrange

        // Act
        var lines = PatchParser.Parse(patch);

        // Assert
        lines.Should().BeEmpty();
    }

    [Fact]
    public void Parse_GivenAMalformedHunkHeaderAfterAValidHunk_Should_NotAttributeTheLinesAfterItToThatHunk()
    {
        // Arrange — a damaged header is not the start of a hunk, and the lines under it belong to nothing that can be numbered
        var patch = Patch("@@ -1,1 +1,1 @@", " context", "@@ nonsense @@", "+ignored", "@@ -5,1 +5,1 @@", "+kept");

        // Act
        var lines = PatchParser.Parse(patch);

        // Assert
        lines.Select(line => line.Text).Should().Equal("@@ -1,1 +1,1 @@", "context", "@@ -5,1 +5,1 @@", "kept");
        lines[^1].Should().Be(new PatchLine(PatchLineKind.Added, null, 5, "kept"));
    }

    [Fact]
    public void Parse_GivenMalformedHunkHeader_Should_IgnoreLinesUntilAValidHeader()
    {
        // Arrange
        var patch = Patch("@@ nonsense @@", "+ignored", "@@ -1,1 +1,1 @@", "+kept");

        // Act
        var lines = PatchParser.Parse(patch);

        // Assert
        lines.Should().HaveCount(2);
        lines[1].Should().Be(new PatchLine(PatchLineKind.Added, null, 1, "kept"));
    }
}
