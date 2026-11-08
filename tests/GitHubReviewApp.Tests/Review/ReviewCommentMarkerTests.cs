namespace GitHubReviewApp.Tests.Review;

/// <summary>
/// The hidden marker that ends the app's comment on a pull request (design 5.8): it names the commit the comment is about and whether it holds a
/// review or a "Review failed" notice, and it counts only as the comment's last line, with a full 40-character commit sha.
/// </summary>
public class ReviewCommentMarkerTests
{
    private const string HeadSha = "0123456789abcdef0123456789abcdef01234567";

    [Theory]
    [InlineData(ReviewRecordOutcome.Reviewed, "<!-- moneyme-ai-review sha=0123456789abcdef0123456789abcdef01234567 outcome=reviewed -->")]
    [InlineData(ReviewRecordOutcome.Failed, "<!-- moneyme-ai-review sha=0123456789abcdef0123456789abcdef01234567 outcome=failed -->")]
    public void For_GivenAShaAndOutcome_Should_WriteTheMarker(ReviewRecordOutcome outcome, string expectedMarker)
    {
        // Arrange

        // Act
        var marker = ReviewCommentMarker.For(HeadSha, outcome);

        // Assert
        marker.Should().Be(expectedMarker);
    }

    [Theory]
    [InlineData("<!-- moneyme-ai-review sha=0123456789abcdef0123456789abcdef01234567 outcome=reviewed -->", ReviewRecordOutcome.Reviewed)]
    [InlineData("<!-- moneyme-ai-review sha=0123456789abcdef0123456789abcdef01234567 outcome=failed -->", ReviewRecordOutcome.Failed)]
    public void TryRead_GivenTheMarkerAsTheLastLine_Should_ReadShaAndOutcome(string marker, ReviewRecordOutcome expectedOutcome)
    {
        // Arrange — line breaks as GitHub returns a comment that was edited in the browser, and a blank line after the marker
        var body = "PASS - no concerns\r\n\r\nmy-org/my-service #7 · feature/retries → main\r\n" + marker + "\r\n\r\n";

        // Act
        var isRead = ReviewCommentMarker.TryRead(body, out var headSha, out var outcome);

        // Assert
        isRead.Should().BeTrue();
        headSha.Should().Be(HeadSha);
        outcome.Should().Be(expectedOutcome);
    }

    [Fact]
    public void TryRead_GivenTheMarkerQuotedEarlierInTheBody_Should_ReturnFalse()
    {
        // Arrange — the marker inside a file name the comment lists, which is not where the app writes it
        var marker = ReviewCommentMarker.For(HeadSha, ReviewRecordOutcome.Reviewed);
        var body = $"To fix, worst first\n- Testing `src/{marker}.cs`: No test.\n\nmy-org/my-service #7 · feature/retries → main";

        // Act
        var isRead = ReviewCommentMarker.TryRead(body, out var headSha, out _);

        // Assert
        isRead.Should().BeFalse();
        headSha.Should().BeEmpty();
    }

    [Theory]
    [InlineData("<!-- moneyme-ai-review sha=0123456789abcdef0123456789abcdef0123456 outcome=reviewed -->")]
    [InlineData("<!-- moneyme-ai-review sha=0123456789abcdef0123456789abcdef012345678 outcome=reviewed -->")]
    [InlineData("<!-- moneyme-ai-review sha=0123456789abcdef0123456789abcdef0123456g outcome=reviewed -->")]
    [InlineData("<!-- moneyme-ai-review sha=0123456789abcdef0123456789abcdef01234567 outcome=passed -->")]
    [InlineData("<!-- moneyme-ai-review sha=0123456789abcdef0123456789abcdef01234567 outcome=Reviewed -->")]
    [InlineData("<!-- moneyme-ai-review sha=0123456789abcdef0123456789abcdef01234567 -->")]
    [InlineData("footer <!-- moneyme-ai-review sha=0123456789abcdef0123456789abcdef01234567 outcome=reviewed -->")]
    [InlineData("<!-- ai-review:sha=0123456789abcdef0123456789abcdef01234567 -->")]
    public void TryRead_GivenAMalformedShaOrUnknownOutcome_Should_ReturnFalse(string lastLine)
    {
        // Arrange — a sha one character short, one long and one not hex; an outcome the app never writes; no outcome; text before the
        // marker on its line; and the marker of the reviews the app posted before the checklist
        var body = "PASS - no concerns\n\nmy-org/my-service #7 · feature/retries → main\n" + lastLine;

        // Act
        var isRead = ReviewCommentMarker.TryRead(body, out _, out _);

        // Assert
        isRead.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \n\t\r\n")]
    public void TryRead_GivenNoBody_Should_ReturnFalse(string? body)
    {
        // Arrange — a comment GitHub returned without a body, or with nothing but blank lines

        // Act
        var isRead = ReviewCommentMarker.TryRead(body, out var headSha, out _);

        // Assert
        isRead.Should().BeFalse();
        headSha.Should().BeEmpty();
    }
}
