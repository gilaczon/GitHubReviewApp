namespace GitHubReviewApp.Tests.Review;

/// <summary>
/// Which findings the comment shows (design 5.7): only those whose check ended a fail or a concern, the fail's first, then those of serious
/// concerns, then the rest, each group in the merged order, and at most six.
/// </summary>
public class FindingSelectorTests
{
    [Fact]
    public void SelectShown_GivenFindingsOfChecksThatEndedPassOrNa_Should_LeaveThemOut()
    {
        // Arrange — the runs found something under Security and Performance, but those checks ended a pass and n/a
        var underPass = new ChecklistFinding(CheckId.Security, "src/RefundClient.cs", 30, "Logs the refund id.", "Drop the log line.");
        var underNa = new ChecklistFinding(CheckId.Performance, "src/RefundClient.cs", 12, "Allocates a buffer per retry.", "Reuse one buffer.");
        var underConcern = new ChecklistFinding(CheckId.Testing, "tests/RefundClientTests.cs", 0, "No test for a failed refund.", "Add one.");
        var merged = Merged(
            Checks(
                new RatedCheck(CheckId.Performance, CheckStatus.NotApplicable, "", false),
                new RatedCheck(CheckId.Testing, CheckStatus.Concern, "No test for the failure path.", false)),
            underPass,
            underNa,
            underConcern);

        // Act
        var shown = FindingSelector.SelectShown(merged);

        // Assert
        shown.Should().Equal(underConcern);
    }

    [Fact]
    public void SelectShown_GivenFailSeriousAndOtherConcerns_Should_ShowFailThenSeriousThenTheRestKeepingMergedOrder()
    {
        // Arrange — Security failed, Functionality is a serious concern and Testing a plain one; the merged findings mix the three
        var untestedRetry = new ChecklistFinding(CheckId.Testing, "tests/RetryTests.cs", 0, "No test for the retry.", "Add one.");
        var doubleRefund = new ChecklistFinding(CheckId.Functionality, "src/RefundClient.cs", 42, "The retry posts the refund twice.", "Send an idempotency key.");
        var loggedToken = new ChecklistFinding(CheckId.Security, "src/RefundClient.cs", 30, "Logs the access token.", "Log the token's id instead.");
        var untestedRefund = new ChecklistFinding(CheckId.Testing, "tests/RefundClientTests.cs", 0, "No test for a failed refund.", "Add one.");
        var loggedSecret = new ChecklistFinding(CheckId.Security, "src/AuthClient.cs", 8, "Logs the client secret.", "Remove the log line.");
        var droppedPayment = new ChecklistFinding(CheckId.Functionality, "src/PaymentClient.cs", 9, "A failed payment is dropped.", "Retry it or report it.");
        var merged = Merged(
            Checks(
                new RatedCheck(CheckId.Functionality, CheckStatus.Concern, "Retries can post the same refund twice.", true),
                new RatedCheck(CheckId.Security, CheckStatus.Fail, "Logs secrets.", false),
                new RatedCheck(CheckId.Testing, CheckStatus.Concern, "No test for the failure path.", false)),
            untestedRetry,
            doubleRefund,
            loggedToken,
            untestedRefund,
            loggedSecret,
            droppedPayment);

        // Act
        var shown = FindingSelector.SelectShown(merged);

        // Assert — inside each group the merged order holds, although the lines and paths run the other way
        shown.Should().Equal(loggedToken, loggedSecret, doubleRefund, droppedPayment, untestedRetry, untestedRefund);
    }

    [Fact]
    public void SelectShown_GivenMoreThanSixFindings_Should_ShowTheFirstSix()
    {
        // Arrange — seven findings under a concern, then one under the failed Security check
        var testingFindings = Enumerable.Range(1, 7)
            .Select(line => new ChecklistFinding(CheckId.Testing, "tests/RefundClientTests.cs", line, "No test for this branch.", "Add one."))
            .ToList();
        var loggedToken = new ChecklistFinding(CheckId.Security, "src/RefundClient.cs", 30, "Logs the access token.", "Log the token's id instead.");
        var merged = Merged(
            Checks(
                new RatedCheck(CheckId.Security, CheckStatus.Fail, "Logs the access token.", false),
                new RatedCheck(CheckId.Testing, CheckStatus.Concern, "No test for the failure path.", false)),
            [.. testingFindings, loggedToken]);

        // Act
        var shown = FindingSelector.SelectShown(merged);

        // Assert — the cut comes after the ordering, so the fail listed last is shown and the last two Testing findings are not
        shown.Select(finding => finding.Line).Should().Equal(30, 1, 2, 3, 4, 5);
    }

    /// <summary>The eight merged checks in checklist order, each a pass, except where a rating is given.</summary>
    private static List<RatedCheck> Checks(params RatedCheck[] ratings)
    {
        return Checklist.Order
            .Select(id => ratings.FirstOrDefault(rating => rating.Id == id) ?? new RatedCheck(id, CheckStatus.Pass, "", false))
            .ToList();
    }

    /// <summary>A merged review with the checks and findings given. Nothing else in it has a say in what is shown.</summary>
    private static MergedReview Merged(IReadOnlyList<RatedCheck> checks, params ChecklistFinding[] findings)
    {
        return new MergedReview(checks, findings, "Adds retries to the refund client.", "", 2, true, ReviewVerdict.Fail, ReviewVerdict.Fail);
    }
}
