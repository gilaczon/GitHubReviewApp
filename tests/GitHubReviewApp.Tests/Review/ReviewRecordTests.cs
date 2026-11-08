namespace GitHubReviewApp.Tests.Review;

/// <summary>
/// The review record (design 5.2): the oldest comment on a pull request that this app's bot login wrote and that ends with a readable marker. It
/// is the comment the app replaces on every push, and the pull request counts as reviewed at a commit only when its marker names that commit
/// with the outcome "reviewed". Anyone else's comment is never the record, whatever it holds.
/// </summary>
public class ReviewRecordTests
{
    private const string AppLogin = "moneyme-ai-review[bot]";
    private const string HeadSha = "0123456789abcdef0123456789abcdef01234567";
    private const string EarlierSha = "1111111111111111111111111111111111111111";

    [Fact]
    public void HasReviewed_GivenOurCommentWithAReviewedMarkerForTheHead_Should_BeTrue()
    {
        // Arrange
        IssueComment[] comments = [Comment(4, AppLogin, Marked(HeadSha, ReviewRecordOutcome.Reviewed))];

        // Act
        var hasReviewed = ReviewRecord.HasReviewed(comments, AppLogin, HeadSha);

        // Assert
        hasReviewed.Should().BeTrue();
    }

    [Fact]
    public void HasReviewed_GivenOurMarkerForTheHeadInAnotherLetterCase_Should_BeTrue()
    {
        // Arrange — a commit sha is hex, which GitHub writes in lower case and a reader may not
        IssueComment[] comments = [Comment(4, AppLogin, Marked(HeadSha.ToUpperInvariant(), ReviewRecordOutcome.Reviewed))];

        // Act
        var hasReviewed = ReviewRecord.HasReviewed(comments, AppLogin, HeadSha);

        // Assert
        hasReviewed.Should().BeTrue();
    }

    [Fact]
    public void HasReviewed_GivenOurFailedMarkerForTheHead_Should_BeFalse()
    {
        // Arrange — a "Review failed" notice is not a review, so a reopen or a new push tries again
        IssueComment[] comments = [Comment(4, AppLogin, Marked(HeadSha, ReviewRecordOutcome.Failed))];

        // Act
        var hasReviewed = ReviewRecord.HasReviewed(comments, AppLogin, HeadSha);

        // Assert
        hasReviewed.Should().BeFalse();
    }

    [Fact]
    public void HasReviewed_GivenTheMarkerInSomeoneElsesComment_Should_BeFalse()
    {
        // Arrange — anyone can write the marker into a comment of their own
        IssueComment[] comments = [Comment(4, "octocat", Marked(HeadSha, ReviewRecordOutcome.Reviewed))];

        // Act
        var hasReviewed = ReviewRecord.HasReviewed(comments, AppLogin, HeadSha);

        // Assert
        hasReviewed.Should().BeFalse();
    }

    [Fact]
    public void HasReviewed_GivenOurMarkerForAnotherCommit_Should_BeFalse()
    {
        // Arrange
        IssueComment[] comments = [Comment(4, AppLogin, Marked(EarlierSha, ReviewRecordOutcome.Reviewed))];

        // Act
        var hasReviewed = ReviewRecord.HasReviewed(comments, AppLogin, HeadSha);

        // Assert
        hasReviewed.Should().BeFalse();
    }

    [Fact]
    public void HasReviewed_GivenTheHeadNamedOnlyByANewerCommentOfOurs_Should_BeFalse()
    {
        // Arrange — the oldest marked comment is the record, the one every push replaces; a newer one is a leftover that is never updated again
        IssueComment[] comments =
        [
            Comment(9, AppLogin, Marked(HeadSha, ReviewRecordOutcome.Reviewed)),
            Comment(4, AppLogin, Marked(EarlierSha, ReviewRecordOutcome.Reviewed))
        ];

        // Act
        var hasReviewed = ReviewRecord.HasReviewed(comments, AppLogin, HeadSha);

        // Assert
        hasReviewed.Should().BeFalse();
    }

    [Fact]
    public void FindOwnComment_GivenSeveralOfOurMarkedComments_Should_ReturnTheOldest()
    {
        // Arrange — listed out of order, and the oldest holds a "Review failed" notice, which the next result replaces like any other
        var oldest = Comment(9, AppLogin, Marked(EarlierSha, ReviewRecordOutcome.Failed));
        IssueComment[] comments =
        [
            Comment(12, AppLogin, Marked(HeadSha, ReviewRecordOutcome.Reviewed)),
            oldest,
            Comment(15, AppLogin, Marked(HeadSha, ReviewRecordOutcome.Reviewed))
        ];

        // Act
        var found = ReviewRecord.FindOwnComment(comments, AppLogin);

        // Assert
        found.Should().BeSameAs(oldest);
    }

    [Fact]
    public void FindOwnComment_GivenOurCommentWithoutAMarker_Should_IgnoreIt()
    {
        // Arrange — an older comment of ours without a readable marker at its end, and another person's older comment with one
        var marked = Comment(9, AppLogin, Marked(HeadSha, ReviewRecordOutcome.Reviewed));
        IssueComment[] comments =
        [
            Comment(2, AppLogin, "A note the app posted before it kept a record."),
            Comment(3, "octocat", Marked(HeadSha, ReviewRecordOutcome.Reviewed)),
            marked
        ];

        // Act
        var found = ReviewRecord.FindOwnComment(comments, AppLogin);

        // Assert
        found.Should().BeSameAs(marked);
    }

    [Fact]
    public void FindOwnComment_GivenTheAppLoginInAnotherLetterCase_Should_StillFindIt()
    {
        // Arrange — GitHub logins ignore case
        var marked = Comment(9, "MoneyMe-AI-Review[BOT]", Marked(HeadSha, ReviewRecordOutcome.Reviewed));
        IssueComment[] comments = [marked];

        // Act
        var found = ReviewRecord.FindOwnComment(comments, AppLogin);

        // Assert
        found.Should().BeSameAs(marked);
    }

    [Fact]
    public void FindOwnComment_GivenAnEmptyAppLogin_Should_FindNothing()
    {
        // Arrange — the comment of a deleted account comes back with an empty login, and an empty app login must not take it for ours
        IssueComment[] comments = [Comment(9, string.Empty, Marked(HeadSha, ReviewRecordOutcome.Reviewed))];

        // Act
        var found = ReviewRecord.FindOwnComment(comments, string.Empty);

        // Assert
        found.Should().BeNull();
    }

    [Fact]
    public void FindOwnComment_GivenNoComments_Should_FindNothing()
    {
        // Arrange

        // Act
        var found = ReviewRecord.FindOwnComment([], AppLogin);

        // Assert
        found.Should().BeNull();
    }

    private static IssueComment Comment(long id, string authorLogin, string body)
    {
        return new IssueComment(id, authorLogin, body);
    }

    /// <summary>A comment body as the app writes it: some text, a footer, and the marker as the last line.</summary>
    private static string Marked(string headSha, ReviewRecordOutcome outcome)
    {
        return "PASS - no concerns\n\nmy-org/my-service #7 · feature/retries → main\n" + ReviewCommentMarker.For(headSha, outcome);
    }
}
