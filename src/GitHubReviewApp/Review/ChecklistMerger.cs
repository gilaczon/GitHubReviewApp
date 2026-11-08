namespace GitHubReviewApp.Review;

/// <summary>
/// Merges the completed runs of one review into what the comment shows (design 5.7). Each check takes the stricter of the runs' ratings, and
/// a check no run rated was not assessed. The findings come once each, the stricter run's first, and the summary and coverage note are the
/// stricter run's: the run whose own verdict is worse, or run A, the one started first, on a tie.
/// </summary>
public static class ChecklistMerger
{
    /// <summary>Merges the answers of one or two runs, given in run order.</summary>
    /// <exception cref="ArgumentException">There is no answer, or there are more than two.</exception>
    public static MergedReview Merge(IReadOnlyList<RunAnswer> answers)
    {
        if (answers.Count is not (1 or 2))
        {
            throw new ArgumentException($"A review merges the answers of one or two runs, not {answers.Count}.", nameof(answers));
        }

        var ownVerdicts = answers.Select(ChecklistVerdict.OwnVerdict).ToList();

        // Run B is the stricter run only when its own verdict is worse: a tie goes to run A, the one started first.
        var stricter = answers.Count == 2 && ownVerdicts[1] > ownVerdicts[0] ? 1 : 0;
        var stricterRun = answers[stricter];
        var otherRun = answers.Count == 2 ? answers[1 - stricter] : null;

        return new MergedReview(
            MergeChecks(answers),
            MergeFindings(stricterRun, otherRun),
            stricterRun.Summary,
            stricterRun.CoverageNote,
            answers.Count,
            IsUnanimous(answers),
            ownVerdicts[stricter],
            otherRun is null ? null : ownVerdicts[1 - stricter]);
    }

    private static IReadOnlyList<RatedCheck> MergeChecks(IReadOnlyList<RunAnswer> answers)
    {
        var rated = new Dictionary<CheckId, RatedCheck>();

        foreach (var id in Checklist.Order)
        {
            // The ratings of the runs that rated the check, in run order.
            var ratings = answers.Select(answer => answer.Rated.GetValueOrDefault(id)).OfType<RatedCheck>().ToList();

            if (ratings.Count > 0)
            {
                rated.Add(id, Stricter(id, ratings));
            }
        }

        // A check no run rated is left for Complete, which makes it a concern that was not assessed, as a run's own verdict does.
        return ChecklistVerdict.Complete(rated);
    }

    private static RatedCheck Stricter(CheckId id, List<RatedCheck> ratings)
    {
        var status = ratings.Max(rating => rating.Status);
        var holders = ratings.Where(rating => rating.Status == status).ToList();

        // A serious run's real note first, then the first real note given, run A's before run B's. The placeholder the reader gives a concern
        // with no note stands for "no note", so it is kept only when no run that holds the status wrote a real one: any note left after the
        // two real-note steps is that placeholder, and with no note at all the merged note is empty.
        var note = holders.FirstOrDefault(rating => rating.IsSerious && IsRealNote(rating.Note))?.Note
            ?? holders.FirstOrDefault(rating => IsRealNote(rating.Note))?.Note
            ?? holders.FirstOrDefault(rating => rating.Note.Length > 0)?.Note
            ?? string.Empty;

        // Only a run that holds the winning status can make it serious, and only a concern is ever serious.
        var isSerious = status == CheckStatus.Concern && holders.Any(rating => rating.IsSerious);

        return new RatedCheck(id, status, note, isSerious);
    }

    private static bool IsRealNote(string note)
    {
        return note.Length > 0 && !string.Equals(note, Checklist.NotAssessedNote, StringComparison.Ordinal);
    }

    private static IReadOnlyList<ChecklistFinding> MergeFindings(RunAnswer stricterRun, RunAnswer? otherRun)
    {
        var kept = new List<ChecklistFinding>();

        foreach (var finding in stricterRun.Findings.Concat(otherRun?.Findings ?? []))
        {
            // The first copy is kept, so the stricter run's wins over the other's.
            if (!kept.Any(earlier => IsRepeat(earlier, finding)))
            {
                kept.Add(finding);
            }
        }

        return kept.AsReadOnly();
    }

    private static bool IsRepeat(ChecklistFinding earlier, ChecklistFinding finding)
    {
        return earlier.Check == finding.Check
            && earlier.Line == finding.Line
            && string.Equals(ComparablePath(earlier.File), ComparablePath(finding.File), StringComparison.OrdinalIgnoreCase);
    }

    private static string ComparablePath(string file)
    {
        // A model may write the same file with '\' or '/' between folders, and with or without a leading "./". IsRepeat ignores letter case.
        var path = file.Replace('\\', '/');

        return path.StartsWith("./", StringComparison.Ordinal) ? path[2..] : path;
    }

    private static bool IsUnanimous(IReadOnlyList<RunAnswer> answers)
    {
        // Both runs rated every check, each with the same status. A check either run left out is not agreement, and notes and serious flags
        // are not compared.
        return answers.Count == 2
            && Checklist.Order.All(id =>
                answers[0].Rated.TryGetValue(id, out var first)
                && answers[1].Rated.TryGetValue(id, out var second)
                && first.Status == second.Status);
    }
}
