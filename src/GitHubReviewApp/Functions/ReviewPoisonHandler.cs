namespace GitHubReviewApp.Functions;

public class ReviewPoisonHandler
{
    private readonly IReviewFailureNotifier _notifier;
    private readonly ILogger<ReviewPoisonHandler> _logger;

    public ReviewPoisonHandler(IReviewFailureNotifier notifier, ILogger<ReviewPoisonHandler> logger)
    {
        _notifier = notifier;
        _logger = logger;
    }

    /// <summary>
    /// The Functions host moves a review request here after it has failed every attempt. Telling the author is the last thing the app can do for
    /// it. A notice that fails for a reason that can clear up (GitHub unavailable, rate limited, the host shutting down) is rethrown, so this
    /// message is retried too, and only then abandoned. One that GitHub refuses for good is counted and logged instead: reading the message again
    /// would only meet the same refusal.
    /// </summary>
    [Function(nameof(ReviewPoisonHandler))]
    public async Task RunAsync(
        [QueueTrigger(ReviewQueue.PoisonName, Connection = ReviewQueue.Connection)] ReviewQueueMessage message,
        CancellationToken cancellationToken)
    {
        // As in ReviewProcessor: the Functions host's own invocation span is never exported, so this one starts a trace of its own.
        Activity.Current = null;
        using var activity = ActivitySources.ReviewProcessor.StartActivity("ReviewPoisonHandler.RunAsync", ActivityKind.Consumer);

        activity?.SetTag("pr.owner", message.Owner);
        activity?.SetTag("pr.repo", message.Repo);
        activity?.SetTag("pr.number", message.PrNumber);
        activity?.SetTag("pr.head_sha", message.HeadSha);
        activity?.SetTag("review.exhausted", true);

        _logger.LogError(
            "Review of {Owner}/{Repo}#{PrNumber} (commit {HeadSha}) gave up after repeated failures.",
            message.Owner, message.Repo, message.PrNumber, message.HeadSha);

        AppMeters.ReviewsFailed.Add(1, new KeyValuePair<string, object?>("kind", "exhausted"));

        try
        {
            await _notifier.NotifyFailureAsync(message, ReviewFailureReasons.RetriesExhausted, cancellationToken);
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested || FailureClassifier.IsTransient(exception))
        {
            // Counted, though the message comes back: when GitHub stays down the last delivery ends in the queue that nobody reads, and without
            // this the notice counter would show nothing while authors go untold. A host shutdown is no failure of the notice, so it is not counted.
            if (!cancellationToken.IsCancellationRequested)
            {
                AppMeters.FailureNoticesFailed.Add(1, new KeyValuePair<string, object?>("outcome", "retry"));
            }

            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            activity?.AddException(exception);
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "The failure notice for {Owner}/{Repo}#{PrNumber} was refused and will not be retried; the author has not been told.",
                message.Owner, message.Repo, message.PrNumber);

            AppMeters.FailureNoticesFailed.Add(1, new KeyValuePair<string, object?>("outcome", "refused"));
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            activity?.AddException(exception);
        }
    }
}
