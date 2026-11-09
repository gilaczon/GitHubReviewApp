namespace GitHubReviewApp.Telemetry;

/// <summary>
/// Writes the review record (design 5.10): one Information line per finished review, posted or failed, that an operator can search by pull
/// request or commit. It holds ids, names, counts, statuses and costs only. No diff, no title or description, and nothing the model wrote (summary,
/// notes, findings, coverage note) goes into it, so it can be kept and shipped like any other log line.
/// </summary>
internal static class ReviewRecordLog
{
    private const string ResultReviewFailed = "review_failed";
    private const string NoValue = "none";
    private const string UnknownCost = "unknown";

    /// <summary>
    /// Logs the record of one finished review. <paramref name="merged"/> and <paramref name="outcome"/> are null for a review that failed.
    /// <paramref name="unlistedFileCount"/> is the number of changed files GitHub did not list (a pull request of more than 3,000 files): they
    /// are counted as left out, as the comment counts them.
    /// </summary>
    public static void Write(
        ILogger logger,
        ReviewQueueMessage message,
        ModelChoice choice,
        IReadOnlyList<RunRecord> runs,
        MergedReview? merged,
        ReviewOutcome? outcome,
        ReviewInput input,
        int unlistedFileCount,
        decimal? totalCost,
        TimeSpan duration)
    {
        // The same count as the comment's: every listed file not included, plus the files GitHub did not list at all.
        var leftOutCount = input.Files.Count - input.IncludedFileCount + unlistedFileCount;

        logger.LogInformation(
            "Review record for {Owner}/{Repo}#{PrNumber} at {HeadSha}: model {Model} ({Tier}, {ChoiceReason}); runs {Runs}; "
            + "result {Result}, {SeriousCount} serious, runs agreed {IsUnanimous}; files {IncludedFileCount} included, {PartialFileCount} partial, "
            + "{LeftOutFileCount} left out, {UnlistedFileCount} not listed by GitHub; cost {TotalCost} USD; took {DurationMilliseconds} ms.",
            message.Owner,
            message.Repo,
            message.PrNumber,
            message.HeadSha,
            choice.Model,
            TierText(choice),
            choice.Reason,
            string.Join(" | ", runs.Select(FormatRun)),
            ResultText(outcome?.Verdict),
            outcome?.SeriousCount ?? 0,
            merged?.IsUnanimous ?? false,
            input.IncludedFileCount,
            input.PartialFileCount,
            leftOutCount,
            unlistedFileCount,
            CostText(totalCost),
            (long)duration.TotalMilliseconds);
    }

    /// <summary>The name of a review's result, as the record and the reviews counter's <c>result</c> tag write it; null is a review that failed.</summary>
    public static string ResultText(ReviewVerdict? verdict)
    {
        return verdict switch
        {
            null => ResultReviewFailed,
            ReviewVerdict.Pass => "pass",
            ReviewVerdict.PassWithComments => "pass_with_comments",
            ReviewVerdict.Fail => "fail",
            _ => throw new UnreachableException($"The verdict {verdict} has no name in the review record.")
        };
    }

    /// <summary>The model tier, as the record and the model-choice and cost metrics' <c>tier</c> tag write it.</summary>
    public static string TierText(ModelChoice choice)
    {
        return choice.IsOpus ? "opus" : "sonnet";
    }

    /// <summary>A check status as the model's answer writes it, which is also how the record and the check-status metric write it.</summary>
    public static string StatusText(CheckStatus status)
    {
        return status switch
        {
            CheckStatus.Pass => "pass",
            CheckStatus.Concern => "concern",
            CheckStatus.Fail => "fail",
            CheckStatus.NotApplicable => "na",
            _ => throw new UnreachableException($"The check status {status} has no name in the review record.")
        };
    }

    /// <summary>
    /// The eight checks of one run's answer in checklist order, for example <c>standards-design=pass functionality=concern* ...</c>: a <c>*</c>
    /// marks a serious concern, and a check the run left out is <c>missing</c>.
    /// </summary>
    public static string Statuses(RunAnswer answer)
    {
        return string.Join(' ', Checklist.Order.Select(id => $"{Checklist.WireId(id)}={CheckText(answer.Rated.GetValueOrDefault(id))}"));
    }

    private static string CheckText(RatedCheck? check)
    {
        if (check is null)
        {
            return "missing";
        }

        var isSeriousConcern = check.Status == CheckStatus.Concern && check.IsSerious;

        return StatusText(check.Status) + (isSeriousConcern ? "*" : string.Empty);
    }

    private static string FormatRun(RunRecord run)
    {
        var verdict = run.OwnVerdict is { } ownVerdict ? ResultText(ownVerdict) : NoValue;

        return FormattableString.Invariant($"run {run.Index} {run.Outcome} {run.Model} {verdict} [{run.Statuses}] ")
            + FormattableString.Invariant($"tokens in {run.Usage.InputTokens} out {run.Usage.OutputTokens} ")
            + FormattableString.Invariant($"cache_read {run.Usage.CacheReadInputTokens} cache_write {run.Usage.CacheCreationInputTokens} cost {CostText(run.Cost)}");
    }

    private static string CostText(decimal? cost)
    {
        return cost?.ToString(CultureInfo.InvariantCulture) ?? UnknownCost;
    }
}
