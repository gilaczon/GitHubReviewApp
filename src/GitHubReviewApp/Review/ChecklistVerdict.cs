namespace GitHubReviewApp.Review;

/// <summary>
/// Works out a review's verdict from its check ratings (design 5.7); the model never states one. FAIL when the Security check is a fail,
/// PASS WITH COMMENTS when any check is a concern or a fail, and PASS when every check is a pass or n/a. A check that was not assessed is
/// a concern, so it can never give a PASS.
/// </summary>
public static class ChecklistVerdict
{
    /// <summary>
    /// The eight checks in checklist order, each with its rating in <paramref name="rated"/>. A check that is not there was not assessed: it
    /// is a concern with <see cref="Checklist.NotAssessedNote"/>, not serious, and never a pass.
    /// </summary>
    public static IReadOnlyList<RatedCheck> Complete(IReadOnlyDictionary<CheckId, RatedCheck> rated)
    {
        return Checklist.Order
            .Select(id => rated.GetValueOrDefault(id) ?? new RatedCheck(id, CheckStatus.Concern, Checklist.NotAssessedNote, false))
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// The verdict of the eight checks, as <see cref="Complete"/> or the merge gives them, with the number of serious concerns. The count is
    /// named in the headline and never changes the verdict. A list that is not the eight checks, each once, could give a PASS in which a check
    /// was not assessed, so it is refused; the order of the list is not checked.
    /// </summary>
    /// <exception cref="ArgumentException">The list does not hold each of the eight checks exactly once.</exception>
    public static ReviewOutcome Derive(IReadOnlyList<RatedCheck> checks)
    {
        if (Checklist.Order.Any(id => checks.Count(check => check.Id == id) != 1))
        {
            throw new ArgumentException("A verdict needs each of the eight checks rated exactly once.", nameof(checks));
        }

        // Serious is a flag on a concern, so a flag on any other status is not counted.
        var seriousCount = checks.Count(check => check.Status == CheckStatus.Concern && check.IsSerious);

        // Only Security can fail a review. A fail on another check (which reading has already made a concern) asks for comments.
        if (checks.Any(check => check.Id == CheckId.Security && check.Status == CheckStatus.Fail))
        {
            return new ReviewOutcome(ReviewVerdict.Fail, seriousCount);
        }

        if (checks.Any(check => check.Status is CheckStatus.Concern or CheckStatus.Fail))
        {
            return new ReviewOutcome(ReviewVerdict.PassWithComments, seriousCount);
        }

        return new ReviewOutcome(ReviewVerdict.Pass, seriousCount);
    }

    /// <summary>A run's own verdict, by which the stricter of two runs is picked: its ratings, with every check it left out a concern.</summary>
    public static ReviewVerdict OwnVerdict(RunAnswer answer)
    {
        return Derive(Complete(answer.Rated)).Verdict;
    }
}
