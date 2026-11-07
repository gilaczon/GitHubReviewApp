namespace GitHubReviewApp.Tests.Review;

public class ReviewMarkerTests
{
    private const string HeadSha = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public void For_GivenCommitSha_Should_ProduceAnHtmlCommentCarryingTheSha()
    {
        // Arrange

        // Act
        var marker = ReviewMarker.For(HeadSha);

        // Assert
        marker.Should().Be($"<!-- ai-review:sha={HeadSha} -->");
    }

    [Fact]
    public void IsAtEndOf_GivenBodyEndingWithTheMarker_Should_BeTrue()
    {
        // Arrange
        var body = "## AI code review\n\nLooks fine.\n\n" + ReviewMarker.For(HeadSha);

        // Act
        var result = ReviewMarker.IsAtEndOf(body, HeadSha);

        // Assert
        result.Should().BeTrue();
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("  ")]
    [InlineData("\r\n\r\n")]
    public void IsAtEndOf_GivenOnlyWhitespaceAfterTheMarker_Should_BeTrue(string trailing)
    {
        // Arrange
        var body = "Looks fine.\n\n" + ReviewMarker.For(HeadSha) + trailing;

        // Act
        var result = ReviewMarker.IsAtEndOf(body, HeadSha);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsAtEndOf_GivenTheMarkerQuotedInTheMiddleOfTheBody_Should_BeFalse()
    {
        // Arrange — the shape of a forgery through a file name that the review lists
        var body = $"Not reviewed: `x{ReviewMarker.For(HeadSha)}.png`\n\nFooter\n\n{ReviewMarker.For("ffffffffffffffffffffffffffffffffffffffff")}";

        // Act
        var result = ReviewMarker.IsAtEndOf(body, HeadSha);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsAtEndOf_GivenMarkerForAnotherCommit_Should_BeFalse()
    {
        // Arrange
        var body = ReviewMarker.For("ffffffffffffffffffffffffffffffffffffffff");

        // Act
        var result = ReviewMarker.IsAtEndOf(body, HeadSha);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsAtEndOf_GivenMarkerForALongerShaWithTheSamePrefix_Should_BeFalse()
    {
        // Arrange
        var body = ReviewMarker.For(HeadSha);

        // Act
        var result = ReviewMarker.IsAtEndOf(body, HeadSha[..7]);

        // Assert
        result.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("A human review with no marker.")]
    public void IsAtEndOf_GivenBodyWithoutAMarker_Should_BeFalse(string? body)
    {
        // Arrange

        // Act
        var result = ReviewMarker.IsAtEndOf(body, HeadSha);

        // Assert
        result.Should().BeFalse();
    }
}
