namespace GitHubReviewApp.ReviewPreview.Tests;

public class SlackMessageFormatterTests
{
    private const string Title = "Add retries";

    private static readonly PullRequestLink _link = new("my-org", "my-service", 7);

    // The shape of a real comment (ChecklistCommentFormatter): headline, summary, agreement, checklist, counts, findings, footer, marker.
    private const string Comment = """
        PASS WITH COMMENTS (1 serious) - no security problem, but read the serious comment before approving

        Adds retries to the refund client.

        Reviewed twice; both reviews agreed.

        Checklist (MoneyMe Code Review Checklist)
        - ✅ Coding standards & design
        - ⚠️ Functionality & implementation: SERIOUS: Retries can post the same refund twice

        1 pass, 1 concern

        To fix, worst first
        - ⚠️ Functionality & implementation `src/RefundClient.cs:42`: The retry resends the POST. Fix: Send an idempotency key.

        my-org/my-service #7 · feature/retries → main · by dev1 · 3 files +40 -10 (2 reviewed) · claude-sonnet-5-5 at 0123456789
        <!-- moneyme-ai-review sha=0123456789abcdef0123456789abcdef01234567 outcome=reviewed -->
        """;

    [Fact]
    public void FormatReview_GivenATypicalComment_Should_WriteItAsSlackShowsIt()
    {
        // Arrange
        var comment = Comment.ReplaceLineEndings("\n");

        // Act
        var text = SlackMessageFormatter.FormatReview(_link, Title, comment);

        // Assert
        const string Expected = """
            :mag: *Review preview* of <https://github.com/my-org/my-service/pull/7|my-org/my-service#7>: Add retries
            _Posted here only: nothing was written to the pull request._

            *PASS WITH COMMENTS (1 serious) - no security problem, but read the serious comment before approving*

            Adds retries to the refund client.

            Reviewed twice; both reviews agreed.

            *Checklist (MoneyMe Code Review Checklist)*
            • ✅ Coding standards &amp; design
            • ⚠️ Functionality &amp; implementation: SERIOUS: Retries can post the same refund twice

            1 pass, 1 concern

            *To fix, worst first*
            • ⚠️ Functionality &amp; implementation `src/RefundClient.cs:42`: The retry resends the POST. Fix: Send an idempotency key.

            my-org/my-service #7 · feature/retries → main · by dev1 · 3 files +40 -10 (2 reviewed) · claude-sonnet-5-5 at 0123456789
            """;
        text.Should().Be(Expected.ReplaceLineEndings("\n"));
    }

    [Theory]
    [InlineData(@"Calls \*Charge\* twice", "Calls *Charge* twice")]
    [InlineData("Logs &lt;token&gt; and &#96;secret&#96;", "Logs &lt;token&gt; and 'secret'")]
    [InlineData("Uses `a<b && c>d` here", "Uses `a&lt;b &amp;&amp; c&gt;d` here")]
    [InlineData(@"Keeps `C:\path\*` as written", @"Keeps `C:\path\*` as written")]
    [InlineData("Says <!channel> and <https://evil.example|click>", "Says &lt;!channel&gt; and &lt;https://evil.example|click&gt;")]
    [InlineData("An odd ` backtick", "An odd ` backtick")]
    public void ToSlack_GivenALineOfTheComment_Should_UndoGitHubEscapesOutsideCodeAndEscapeForSlack(string line, string expected)
    {
        // Arrange — GitHub escapes, entities, inline code, and text from the pull request that would be a Slack mention or link

        // Act
        var text = SlackMessageFormatter.ToSlack(line);

        // Assert
        text.Should().Be(expected);
    }

    [Fact]
    public void FormatReview_GivenATitleThatLooksLikeASlackLink_Should_EscapeIt()
    {
        // Arrange — the title is the pull request author's
        const string title = "<!here> <https://evil.example|Fix>";

        // Act
        var text = SlackMessageFormatter.FormatReview(_link, title, "PASS");

        // Assert
        text.Should().Contain("&lt;!here&gt; &lt;https://evil.example|Fix&gt;").And.NotContain("<!here>");
    }

    [Fact]
    public void FormatReview_GivenACommentLongerThanSlackHolds_Should_CutItWithANote()
    {
        // Arrange
        var comment = "PASS\n\n" + new string('x', SlackMessageFormatter.MaxTextLength + 1000);

        // Act
        var text = SlackMessageFormatter.FormatReview(_link, Title, comment);

        // Assert
        text.Length.Should().Be(SlackMessageFormatter.MaxTextLength);
        text.Should().EndWith("longer than one Slack message can hold)");
    }

    [Theory]
    [InlineData(ReviewSkipReason.NothingToReview, "Nothing was reviewed: the pull request has no changed files.")]
    [InlineData(ReviewSkipReason.Superseded, "Nothing was reviewed: a new commit was pushed while the review ran. Run the preview again.")]
    [InlineData(ReviewSkipReason.InProgress, "Nothing was reviewed: InProgress.")]
    public void FormatSkip_GivenAReason_Should_SayWhyNothingWasReviewed(ReviewSkipReason reason, string expectedLine)
    {
        // Arrange

        // Act
        var text = SlackMessageFormatter.FormatSkip(_link, Title, reason);

        // Assert
        text.Should().StartWith(":mag: *Review preview* of <https://github.com/my-org/my-service/pull/7|my-org/my-service#7>: Add retries");
        text.Split('\n').Should().Contain(expectedLine);
    }
}
