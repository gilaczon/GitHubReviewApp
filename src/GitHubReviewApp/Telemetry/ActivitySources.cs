namespace GitHubReviewApp.Telemetry;

internal static class ActivitySources
{
    internal const string ReviewProcessorName = "GitHubReviewApp.ReviewProcessor";
    internal const string ClaudeServiceName   = "GitHubReviewApp.ClaudeService";

    internal static readonly ActivitySource ReviewProcessor = new(ReviewProcessorName);
    internal static readonly ActivitySource ClaudeService   = new(ClaudeServiceName);
}
