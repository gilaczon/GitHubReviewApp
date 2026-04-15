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

    internal static readonly Counter<long> ReviewsProcessed =
        _meter.CreateCounter<long>(
            "github_review_app.reviews.processed",
            unit: "reviews",
            description: "Number of reviews successfully posted");

    internal static readonly Counter<long> ReviewsFailed =
        _meter.CreateCounter<long>(
            "github_review_app.reviews.failed",
            unit: "reviews",
            description: "Number of review processing failures");

    internal static readonly Histogram<double> ReviewDuration =
        _meter.CreateHistogram<double>(
            "github_review_app.review.duration",
            unit: "ms",
            description: "End-to-end review processing duration in milliseconds");
}
