namespace GitHubReviewApp.Functions;

public class ReviewProcessor
{
    private readonly IGitHubAppAuthService _auth;
    private readonly IGitHubService _github;
    private readonly IClaudeService _claude;
    private readonly ILogger<ReviewProcessor> _logger;

    public ReviewProcessor(
        IGitHubAppAuthService auth,
        IGitHubService github,
        IClaudeService claude,
        ILogger<ReviewProcessor> logger)
    {
        _auth = auth;
        _github = github;
        _claude = claude;
        _logger = logger;
    }

    [Function(nameof(ReviewProcessor))]
    public async Task RunAsync(
        [QueueTrigger("ai-review-queue", Connection = "ReviewQueueConnection")] ReviewQueueMessage message)
    {
        _logger.LogInformation(
            "Processing review for {Owner}/{Repo}#{PrNumber}.",
            message.Owner, message.Repo, message.PrNumber);

        // Azure Functions injects an invocation span as Activity.Current before the function
        // body runs. That span lives in the Functions host and is never exported to Uptrace,
        // making our spans orphans that never appear in the Traces view. Clear it so our span
        // becomes a proper root with its own trace ID.
        Activity.Current = null;
        using var activity = ActivitySources.ReviewProcessor.StartActivity(
            "ReviewProcessor.RunAsync", ActivityKind.Consumer);

        activity?.SetTag("pr.owner",  message.Owner);
        activity?.SetTag("pr.repo",   message.Repo);
        activity?.SetTag("pr.number", message.PrNumber);
        activity?.SetTag("pr.title",  message.PrTitle);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var token = await _auth.GetInstallationTokenAsync(message.InstallationId);
            var diff = await _github.GetPullRequestDiffAsync(message.Owner, message.Repo, message.PrNumber, token);

            if (string.IsNullOrWhiteSpace(diff))
            {
                _logger.LogWarning("Empty diff for {Owner}/{Repo}#{PrNumber} — skipping.", message.Owner, message.Repo, message.PrNumber);
                activity?.SetTag("pr.skipped_reason", "empty_diff");
                return;
            }

            var review = await _claude.ReviewDiffAsync(message.PrTitle, diff);
            await _github.PostReviewAsync(message.Owner, message.Repo, message.PrNumber, review, token);

            _logger.LogInformation(
                "Review posted for {Owner}/{Repo}#{PrNumber}.",
                message.Owner, message.Repo, message.PrNumber);

            activity?.SetStatus(ActivityStatusCode.Ok);
            AppMeters.ReviewsProcessed.Add(1);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to process review for {Owner}/{Repo}#{PrNumber}.",
                message.Owner, message.Repo, message.PrNumber);

            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
            AppMeters.ReviewsFailed.Add(1);
        }
        finally
        {
            AppMeters.ReviewDuration.Record(stopwatch.Elapsed.TotalMilliseconds);
        }
    }
}
