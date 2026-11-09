namespace GitHubReviewApp.Functions;

public class ReviewProcessor
{
    private readonly IReviewOrchestrator _orchestrator;
    private readonly IReviewFailureNotifier _notifier;
    private readonly IGitHubAppAuthService _auth;
    private readonly IRuntimeSettings _runtime;
    private readonly ILogger<ReviewProcessor> _logger;
    private readonly TimeProvider _timeProvider;

    public ReviewProcessor(
        IReviewOrchestrator orchestrator,
        IReviewFailureNotifier notifier,
        IGitHubAppAuthService auth,
        IRuntimeSettings runtime,
        ILogger<ReviewProcessor> logger,
        TimeProvider? timeProvider = null)
    {
        _orchestrator = orchestrator;
        _notifier = notifier;
        _auth = auth;
        _runtime = runtime;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Failures that can clear up on their own (rate limits, outages, timeouts) are rethrown so the queue
    /// retries the message; once its attempts run out the host moves it to the poison queue, where
    /// <see cref="ReviewPoisonHandler"/> tells the author. Failures that would repeat identically are not
    /// retried: the author is told immediately.
    /// </summary>
    [Function(nameof(ReviewProcessor))]
    public async Task RunAsync(
        [QueueTrigger(ReviewQueue.Name, Connection = ReviewQueue.Connection)] ReviewQueueMessage message,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Processing review for {Owner}/{Repo}#{PrNumber} at commit {HeadSha}.",
            message.Owner, message.Repo, message.PrNumber, message.HeadSha);

        // Azure Functions injects an invocation span as Activity.Current before the function
        // body runs. That span lives in the Functions host and is never exported to Uptrace,
        // making our spans orphans that never appear in the Traces view. Clear it so our span
        // becomes a proper root with its own trace ID.
        Activity.Current = null;
        using var activity = ActivitySources.ReviewProcessor.StartActivity(
            "ReviewProcessor.RunAsync", ActivityKind.Consumer);

        activity?.SetTag("pr.owner", message.Owner);
        activity?.SetTag("pr.repo", message.Repo);
        activity?.SetTag("pr.number", message.PrNumber);
        activity?.SetTag("pr.head_sha", message.HeadSha);

        var outcome = "failed";
        var startedAt = _timeProvider.GetTimestamp();
        try
        {
            // Checked again here: the switch may have been turned off while the message waited in the queue.
            if (!_runtime.Current.IsReviewEnabled)
            {
                _logger.LogInformation(
                    "Skipping review of {Owner}/{Repo}#{PrNumber}: reviews are switched off.",
                    message.Owner, message.Repo, message.PrNumber);

                // Counted here: the orchestrator, which counts every other skip, never sees this one.
                AppMeters.ReviewsSkipped.Add(1, new KeyValuePair<string, object?>("reason", ReviewSkipReason.SwitchedOff.ToString()));
                activity?.SetTag("review.skipped_reason", ReviewSkipReason.SwitchedOff.ToString());
                activity?.SetStatus(ActivityStatusCode.Ok);
                outcome = "skipped";
                return;
            }

            var result = await _orchestrator.ReviewPullRequestAsync(message, cancellationToken);

            if (result.Status == ReviewRunStatus.Posted)
            {
                _logger.LogInformation(
                    "Review posted for {Owner}/{Repo}#{PrNumber}: {Verdict} from {RunCount} runs, {IncludedFileCount} files, partial: {IsPartial}.",
                    message.Owner, message.Repo, message.PrNumber, result.Verdict, result.RunCount, result.IncludedFileCount, result.IsPartial);

                // Counts and fixed names only: the span carries nothing the model or the author wrote.
                activity?.SetTag("review.result", ReviewRecordLog.ResultText(result.Verdict));
                activity?.SetTag("review.runs", result.RunCount);
                activity?.SetTag("review.files_included", result.IncludedFileCount);
                activity?.SetTag("review.partial", result.IsPartial);
                CountProcessed(result);
                outcome = "posted";
            }
            else if (result.Status == ReviewRunStatus.Failed)
            {
                // Neither run gave an answer for a reason a retry would not change, and the author has been told so. The app worked as designed,
                // so this is no failure of the processor: it is counted with the reviews, as one whose result is review_failed.
                _logger.LogInformation(
                    "Review failed published for {Owner}/{Repo}#{PrNumber}: neither model run gave an answer.",
                    message.Owner, message.Repo, message.PrNumber);

                activity?.SetTag("review.result", ReviewRecordLog.ResultText(result.Verdict));
                activity?.SetTag("review.runs", 0);
                CountProcessed(result);
                outcome = "review_failed";
            }
            else
            {
                activity?.SetTag("review.skipped_reason", result.SkipReason?.ToString());
                outcome = "skipped";
            }

            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested)
        {
            // The host is shutting down. Whatever surfaced (a cancellation, or an ObjectDisposedException from a client torn down under the
            // call) says nothing about the review: the message returns to the queue and is retried.
            _logger.LogInformation(
                exception,
                "Review of {Owner}/{Repo}#{PrNumber} was interrupted by host shutdown; it will be retried.",
                message.Owner, message.Repo, message.PrNumber);

            activity?.SetTag("review.interrupted", true);
            AppMeters.ReviewsInterrupted.Add(1);
            outcome = "interrupted";
            throw;
        }
        catch (Exception exception) when (FailureClassifier.IsTransient(exception))
        {
            _logger.LogWarning(
                exception,
                "Transient failure reviewing {Owner}/{Repo}#{PrNumber}; the queue will retry.",
                message.Owner, message.Repo, message.PrNumber);

            RecordFailure(activity, exception, "transient");
            ForgetTokenGitHubRefused(message, exception);
            outcome = "retry";
            throw;
        }
        catch (Exception exception)
        {
            var isSpendLimit = FailureClassifier.IsAiSpendLimitReached(exception);

            if (isSpendLimit)
            {
                // Every review fails this way until someone acts, which nothing else that is not retried implies, so it has its own message and kind.
                _logger.LogError(
                    exception,
                    "The AI provider's monthly spend limit is reached, so reviews fail until it is raised or the month ends. {Owner}/{Repo}#{PrNumber} was not reviewed and will not be retried.",
                    message.Owner, message.Repo, message.PrNumber);
            }
            else
            {
                _logger.LogError(
                    exception,
                    "Failed to review {Owner}/{Repo}#{PrNumber}; this will not be retried.",
                    message.Owner, message.Repo, message.PrNumber);
            }

            RecordFailure(activity, exception, isSpendLimit ? "ai_spend_limit" : "permanent");
            await TryNotifyAsync(message, cancellationToken);
        }
        finally
        {
            AppMeters.ReviewDuration.Record(_timeProvider.GetElapsedTime(startedAt).TotalMilliseconds, new KeyValuePair<string, object?>("outcome", outcome));
        }
    }

    /// <summary>
    /// GitHub answers 401 to a token it no longer accepts. The cached copy would be handed out again until its own expiry time, so the retry, and
    /// the next review of the repository, would fail the same way. It is dropped so that the next request asks for a new one. A missing permission
    /// (403), a missing resource (404), a rate limit or an outage says nothing against the token, so those leave it alone.
    /// </summary>
    private void ForgetTokenGitHubRefused(ReviewQueueMessage message, Exception exception)
    {
        if (exception is GitHubApiException { StatusCode: HttpStatusCode.Unauthorized })
        {
            _auth.ForgetInstallationToken(message.InstallationId, message.Repo);
        }
    }

    private static void CountProcessed(ReviewRunResult result)
    {
        AppMeters.ReviewsProcessed.Add(
            1,
            new KeyValuePair<string, object?>("result", ReviewRecordLog.ResultText(result.Verdict)),
            new KeyValuePair<string, object?>("runs", result.RunCount.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, object?>("coverage", DescribeCoverage(result)));
    }

    private static string DescribeCoverage(ReviewRunResult result)
    {
        if (result.IncludedFileCount == 0)
        {
            return "none";
        }

        return result.IsPartial ? "partial" : "full";
    }

    private static void RecordFailure(Activity? activity, Exception exception, string kind)
    {
        activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
        activity?.AddException(exception);
        AppMeters.ReviewsFailed.Add(1, new KeyValuePair<string, object?>("kind", kind));
    }

    /// <summary>
    /// Tells the author a review failed for good. A notice that fails for a reason that can clear up (GitHub unavailable, rate limited, the
    /// host shutting down) is rethrown so the queue retries the message and the author is not left untold. One that GitHub refuses for
    /// good cannot be fixed by retrying, so it is counted and logged for the operator instead.
    /// </summary>
    private async Task TryNotifyAsync(ReviewQueueMessage message, CancellationToken cancellationToken)
    {
        try
        {
            await _notifier.NotifyFailureAsync(message, ReviewFailureReasons.PermanentError, cancellationToken);
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested || FailureClassifier.IsTransient(exception))
        {
            _logger.LogWarning(
                exception,
                "Could not post the failure notice for {Owner}/{Repo}#{PrNumber} yet; the queue will retry the message.",
                message.Owner, message.Repo, message.PrNumber);

            // Counted, though the message comes back: a long GitHub outage must not leave the notice counter at zero. A host shutdown is no failure
            // of the notice, so it is not counted.
            if (!cancellationToken.IsCancellationRequested)
            {
                AppMeters.FailureNoticesFailed.Add(1, new KeyValuePair<string, object?>("outcome", "retry"));
            }

            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "The failure notice for {Owner}/{Repo}#{PrNumber} was refused and will not be retried; the author has not been told.",
                message.Owner, message.Repo, message.PrNumber);

            AppMeters.FailureNoticesFailed.Add(1, new KeyValuePair<string, object?>("outcome", "refused"));
        }
    }
}
