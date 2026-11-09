namespace GitHubReviewApp.Telemetry;

internal static class AppMeters
{
    internal const string MeterName = "GitHubReviewApp";

    private static readonly Meter _meter = new(MeterName);

    internal static readonly Counter<long> InputTokens =
        _meter.CreateCounter<long>(
            "gen_ai.usage.input_tokens",
            unit: "tokens",
            description: "Total input tokens consumed by Claude API calls");

    internal static readonly Counter<long> OutputTokens =
        _meter.CreateCounter<long>(
            "gen_ai.usage.output_tokens",
            unit: "tokens",
            description: "Total output tokens consumed by Claude API calls");

    internal static readonly Counter<long> CacheReadInputTokens =
        _meter.CreateCounter<long>(
            "gen_ai.usage.cache_read_input_tokens",
            unit: "tokens",
            description: "Input tokens served from the prompt cache");

    internal static readonly Counter<long> CacheCreationInputTokens =
        _meter.CreateCounter<long>(
            "gen_ai.usage.cache_creation_input_tokens",
            unit: "tokens",
            description: "Input tokens written to the prompt cache");

    /// <summary>
    /// Reviews whose result was published. Tagged <c>result</c>: pass, pass_with_comments, fail, or review_failed (no run gave an answer, and
    /// "Review failed" was published); <c>runs</c>: the model runs that completed, 1 or 2, and 0 for review_failed; and <c>coverage</c>: full,
    /// partial (something readable was not read) or none (no diff was sent, which an operator should treat as a failure rather than a success).
    /// </summary>
    internal static readonly Counter<long> ReviewsProcessed =
        _meter.CreateCounter<long>(
            "github_review_app.reviews.processed",
            unit: "reviews",
            description: "Number of reviews whose result was published");

    /// <summary>Failed attempts. Tagged <c>kind</c> = transient (will be retried), permanent (gave up) or exhausted (the retries ran out).</summary>
    internal static readonly Counter<long> ReviewsFailed =
        _meter.CreateCounter<long>(
            "github_review_app.reviews.failed",
            unit: "reviews",
            description: "Number of review processing failures");

    internal static readonly Counter<long> ReviewsInterrupted =
        _meter.CreateCounter<long>(
            "github_review_app.reviews.interrupted",
            unit: "reviews",
            description: "Attempts cut short because the host was shutting down; the message returns to the queue");

    /// <summary>
    /// Attempts to tell the author that a review failed which themselves failed. Tagged <c>outcome</c>: refused (GitHub will not accept it, so the author
    /// is not told) or retry (a failure that can clear up; the message comes back, so one author can be counted again, and if the outage lasts the
    /// message ends in a queue nobody reads).
    /// </summary>
    internal static readonly Counter<long> FailureNoticesFailed =
        _meter.CreateCounter<long>(
            "github_review_app.failure_notices.failed",
            unit: "notices",
            description: "Attempts to post a failure notice to the pull request author that failed");

    /// <summary>
    /// Checklist runs that finished, tagged <c>outcome</c> with the <see cref="Models.RunOutcome"/> name and <c>model</c> with the model that
    /// answered (the one asked for when none did). A run that failed outright is thrown, not counted here.
    /// </summary>
    internal static readonly Counter<long> ReviewRuns =
        _meter.CreateCounter<long>(
            "github_review_app.review.runs",
            unit: "runs",
            description: "Checklist runs, by how they ended and the model that answered");

    /// <summary>
    /// Published reviews whose two runs did not agree. Tagged <c>kind</c>: checks (at least one check rated differently, or rated by one run only)
    /// or verdict (the runs' own verdicts differ). A review whose verdicts differ is counted under both.
    /// </summary>
    internal static readonly Counter<long> ReviewDisagreements =
        _meter.CreateCounter<long>(
            "github_review_app.review.disagreements",
            unit: "reviews",
            description: "Published reviews whose two runs disagreed, by what they disagreed on");

    /// <summary>The eight checks of every published review, after merging. Tagged <c>check</c> with the check's id and <c>status</c> (pass, concern, fail, na).</summary>
    internal static readonly Counter<long> CheckStatuses =
        _meter.CreateCounter<long>(
            "github_review_app.review.check_status",
            unit: "checks",
            description: "Checks of published reviews, by check and merged status");

    /// <summary>Concerns flagged serious in published reviews, after merging.</summary>
    internal static readonly Counter<long> SeriousFlags =
        _meter.CreateCounter<long>(
            "github_review_app.review.serious",
            unit: "checks",
            description: "Serious concerns in published reviews");

    /// <summary>The model chosen for each finished review. Tagged <c>tier</c> (sonnet or opus) and <c>reason</c> (high_risk, low_risk or small_change).</summary>
    internal static readonly Counter<long> ModelChoices =
        _meter.CreateCounter<long>(
            "github_review_app.review.model_choice",
            unit: "reviews",
            description: "Model chosen for finished reviews, by tier and reason");

    /// <summary>Tagged <c>reason</c> with the <see cref="Models.ReviewSkipReason"/> name.</summary>
    internal static readonly Counter<long> ReviewsSkipped =
        _meter.CreateCounter<long>(
            "github_review_app.reviews.skipped",
            unit: "reviews",
            description: "Number of queued reviews that were skipped without calling the model");

    internal static readonly Counter<long> ReviewsPartial =
        _meter.CreateCounter<long>(
            "github_review_app.reviews.partial",
            unit: "reviews",
            description: "Reviews posted without covering every changed file");

    internal static readonly Counter<long> FilesReviewed =
        _meter.CreateCounter<long>(
            "github_review_app.files.reviewed",
            unit: "files",
            description: "Changed files sent to the model");

    // A review takes from a few seconds (nothing to read) to about twenty minutes (the limit of one review), and the OpenTelemetry default buckets
    // stop at ten seconds, so without these every review of any size would land in the overflow bucket and no percentile would mean anything.
    private static readonly double[] _reviewDurationBucketsMilliseconds =
        [5_000, 15_000, 30_000, 60_000, 120_000, 300_000, 600_000, 900_000, 1_200_000, 1_800_000];

    /// <summary>
    /// What one finished review cost in US dollars, both runs together, each attempt at its own model's price. Tagged <c>tier</c> (sonnet or opus). A
    /// review with a run whose cost is not known (a model missing from <c>Claude:Pricing</c>, or a run that failed outright) is not recorded.
    /// </summary>
    internal static readonly Histogram<double> ReviewCost =
        _meter.CreateHistogram<double>(
            "github_review_app.review.cost",
            unit: "USD",
            description: "Cost of finished reviews in US dollars");

    /// <summary>Tagged <c>outcome</c>: posted, review_failed, skipped, retry (a transient failure), failed or interrupted.</summary>
    internal static readonly Histogram<double> ReviewDuration =
        _meter.CreateHistogram<double>(
            "github_review_app.review.duration",
            unit: "ms",
            description: "End-to-end review processing duration in milliseconds",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = _reviewDurationBucketsMilliseconds });

    internal static readonly Histogram<long> PullRequestChangedLines =
        _meter.CreateHistogram<long>(
            "github_review_app.pull_request.changed_lines",
            unit: "lines",
            description: "Lines changed (added plus deleted) in reviewed pull requests");
}
