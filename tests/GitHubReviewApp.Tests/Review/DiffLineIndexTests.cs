namespace GitHubReviewApp.Tests.Review;

public class DiffLineIndexTests
{
    private const string TwoHunkPatch =
        "@@ -1,3 +1,4 @@\n context\n-removed\n+added one\n+added two\n context two\n" +
        "@@ -20,1 +30,2 @@\n context three\n+added three";

    [Theory]
    [InlineData(1)]   // context line
    [InlineData(2)]   // added line
    [InlineData(3)]   // added line
    [InlineData(4)]   // context line
    [InlineData(30)]  // context line in the second hunk
    [InlineData(31)]  // added line in the second hunk
    public void Contains_GivenLineVisibleOnTheNewSide_Should_BeTrue(int line)
    {
        // Arrange
        var sut = DiffLineIndex.FromPatch(TwoHunkPatch);

        // Act
        var isVisible = sut.Contains(line);

        // Assert
        isVisible.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]    // "no line" sentinel
    [InlineData(-1)]
    [InlineData(5)]    // gap between hunks
    [InlineData(29)]   // just before the second hunk
    [InlineData(32)]   // just after the second hunk
    public void Contains_GivenLineOutsideEveryHunk_Should_BeFalse(int line)
    {
        // Arrange
        var sut = DiffLineIndex.FromPatch(TwoHunkPatch);

        // Act
        var isVisible = sut.Contains(line);

        // Assert
        isVisible.Should().BeFalse();
    }

    [Fact]
    public void Count_GivenTwoHunks_Should_CountOnlyAddedAndContextLines()
    {
        // Arrange

        // Act
        var sut = DiffLineIndex.FromPatch(TwoHunkPatch);

        // Assert — removed lines have no new-side number
        sut.Count.Should().Be(6);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Contains_GivenNullOrEmptyPatch_Should_BeFalseForAnyLine(string? patch)
    {
        // Arrange
        var sut = DiffLineIndex.FromPatch(patch);

        // Act
        var isVisible = sut.Contains(1);

        // Assert
        isVisible.Should().BeFalse();
    }
}
