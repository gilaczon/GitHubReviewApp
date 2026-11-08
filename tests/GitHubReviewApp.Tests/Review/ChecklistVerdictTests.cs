namespace GitHubReviewApp.Tests.Review;

/// <summary>
/// Deriving the verdict from the eight check ratings (design 5.7): only a Security fail fails a review, any concern or fail asks for
/// comments, a serious concern is counted but never changes the verdict, and a check that was not assessed is a concern, never a pass.
/// </summary>
public class ChecklistVerdictTests
{
    [Fact]
    public void Derive_GivenSecurityFail_Should_BeFail()
    {
        // Arrange
        var checks = Checks(new RatedCheck(CheckId.Security, CheckStatus.Fail, "Logs the access token.", false));

        // Act
        var outcome = ChecklistVerdict.Derive(checks);

        // Assert
        outcome.Should().Be(new ReviewOutcome(ReviewVerdict.Fail, 0));
    }

    [Fact]
    public void Derive_GivenAConcernAndNoSecurityFail_Should_BePassWithComments()
    {
        // Arrange
        var checks = Checks(new RatedCheck(CheckId.Testing, CheckStatus.Concern, "No test for the failure path.", false));

        // Act
        var outcome = ChecklistVerdict.Derive(checks);

        // Assert
        outcome.Should().Be(new ReviewOutcome(ReviewVerdict.PassWithComments, 0));
    }

    [Fact]
    public void Derive_GivenOnlyPassAndNa_Should_BePass()
    {
        // Arrange
        var checks = Checks(
            new RatedCheck(CheckId.Performance, CheckStatus.NotApplicable, "", false),
            new RatedCheck(CheckId.Scalability, CheckStatus.NotApplicable, "", false));

        // Act
        var outcome = ChecklistVerdict.Derive(checks);

        // Assert
        outcome.Should().Be(new ReviewOutcome(ReviewVerdict.Pass, 0));
    }

    [Fact]
    public void Derive_GivenEveryCheckNa_Should_BePass()
    {
        // Arrange — a change that touches none of the checks' areas
        var checks = Checklist.Order.Select(id => new RatedCheck(id, CheckStatus.NotApplicable, "", false)).ToList();

        // Act
        var outcome = ChecklistVerdict.Derive(checks);

        // Assert
        outcome.Should().Be(new ReviewOutcome(ReviewVerdict.Pass, 0));
    }

    [Fact]
    public void Derive_GivenTwoSeriousConcerns_Should_CountTwoSerious()
    {
        // Arrange — the third concern is not serious, so it is not counted
        var checks = Checks(
            new RatedCheck(CheckId.Functionality, CheckStatus.Concern, "Retries can post the same refund twice.", true),
            new RatedCheck(CheckId.Testing, CheckStatus.Concern, "No test for the failure path.", false),
            new RatedCheck(CheckId.ErrorHandling, CheckStatus.Concern, "A failed refund is dropped without a trace.", true));

        // Act
        var outcome = ChecklistVerdict.Derive(checks);

        // Assert
        outcome.Should().Be(new ReviewOutcome(ReviewVerdict.PassWithComments, 2));
        outcome.Headline.Should().Be("PASS WITH COMMENTS (2 serious)");
    }

    [Fact]
    public void Derive_GivenSecurityFailAndASeriousConcern_Should_BeFailWithOneSerious()
    {
        // Arrange
        var checks = Checks(
            new RatedCheck(CheckId.Functionality, CheckStatus.Concern, "Retries can post the same refund twice.", true),
            new RatedCheck(CheckId.Security, CheckStatus.Fail, "Logs the access token.", false));

        // Act
        var outcome = ChecklistVerdict.Derive(checks);

        // Assert — the serious concern is named in the headline and changes nothing else
        outcome.Should().Be(new ReviewOutcome(ReviewVerdict.Fail, 1));
        outcome.Headline.Should().Be("FAIL (1 serious)");
        outcome.Meaning.Should().Be("changes required");
    }

    [Fact]
    public void Derive_GivenFailOnACheckOtherThanSecurity_Should_NotBeFail()
    {
        // Arrange — reading turns such a fail into a concern, and the verdict holds even when one gets this far
        var checks = Checks(new RatedCheck(CheckId.Functionality, CheckStatus.Fail, "Charges the card twice on a retry.", false));

        // Act
        var outcome = ChecklistVerdict.Derive(checks);

        // Assert — only Security can fail a review
        outcome.Should().Be(new ReviewOutcome(ReviewVerdict.PassWithComments, 0));
    }

    [Fact]
    public void Derive_GivenASeriousFlagOnACheckThatIsNotAConcern_Should_NotCountIt()
    {
        // Arrange — serious is a flag on a concern; set on a pass, it must not give "PASS (1 serious)"
        var checks = Checks(new RatedCheck(CheckId.Testing, CheckStatus.Pass, "", true));

        // Act
        var outcome = ChecklistVerdict.Derive(checks);

        // Assert
        outcome.Should().Be(new ReviewOutcome(ReviewVerdict.Pass, 0));
    }

    [Fact]
    public void Complete_GivenAMissingSecurity_Should_AddAConcernThatIsNotAssessed()
    {
        // Arrange — the run rated every check a pass except Security, which it left out
        var rated = Checklist.Order
            .Where(id => id != CheckId.Security)
            .ToDictionary(id => id, id => new RatedCheck(id, CheckStatus.Pass, "", false));

        // Act
        var checks = ChecklistVerdict.Complete(rated);
        var outcome = ChecklistVerdict.Derive(checks);

        // Assert — the spec's note, and never a pass: a review that did not assess Security asks for a look
        checks.Single(check => check.Id == CheckId.Security).Should().Be(NotAssessed(CheckId.Security));
        outcome.Verdict.Should().Be(ReviewVerdict.PassWithComments);
    }

    [Fact]
    public void Complete_GivenSomeRatings_Should_ReturnEightChecksInTheSpecOrder()
    {
        // Arrange — three ratings, added out of the spec's order
        var rated = new Dictionary<CheckId, RatedCheck>
        {
            [CheckId.Dependencies] = new(CheckId.Dependencies, CheckStatus.Pass, "", false),
            [CheckId.Security] = new(CheckId.Security, CheckStatus.Fail, "Logs the access token.", false),
            [CheckId.StandardsDesign] = new(CheckId.StandardsDesign, CheckStatus.Concern, "Copies the retry loop into two clients.", false)
        };

        // Act
        var checks = ChecklistVerdict.Complete(rated);

        // Assert — each rated check keeps its rating, and the five left out are not assessed
        checks.Should().Equal(
            new RatedCheck(CheckId.StandardsDesign, CheckStatus.Concern, "Copies the retry loop into two clients.", false),
            NotAssessed(CheckId.Functionality),
            NotAssessed(CheckId.Performance),
            new RatedCheck(CheckId.Security, CheckStatus.Fail, "Logs the access token.", false),
            NotAssessed(CheckId.Testing),
            NotAssessed(CheckId.ErrorHandling),
            NotAssessed(CheckId.Scalability),
            new RatedCheck(CheckId.Dependencies, CheckStatus.Pass, "", false));
    }

    [Fact]
    public void OwnVerdict_GivenAnAnswerWithOnlyOnePassRated_Should_BePassWithComments()
    {
        // Arrange — the run rated Security a pass and left the other seven checks out
        var answer = new RunAnswer(
            new Dictionary<CheckId, RatedCheck> { [CheckId.Security] = new(CheckId.Security, CheckStatus.Pass, "", false) },
            [],
            "Adds retries to the refund client.",
            "");

        // Act
        var verdict = ChecklistVerdict.OwnVerdict(answer);

        // Assert — the seven it left out are concerns, so its own result is not a pass
        verdict.Should().Be(ReviewVerdict.PassWithComments);
    }

    /// <summary>The eight checks in checklist order, each rated a pass, except where a rating is given.</summary>
    private static List<RatedCheck> Checks(params RatedCheck[] ratings)
    {
        return Checklist.Order
            .Select(id => ratings.FirstOrDefault(rating => rating.Id == id) ?? new RatedCheck(id, CheckStatus.Pass, "", false))
            .ToList();
    }

    /// <summary>The rating of a check that was not assessed, with the note the spec gives it.</summary>
    private static RatedCheck NotAssessed(CheckId id)
    {
        return new RatedCheck(id, CheckStatus.Concern, "Not assessed by the reviewer.", false);
    }
}
