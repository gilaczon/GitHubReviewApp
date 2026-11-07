namespace GitHubReviewApp.Services;

/// <summary>
/// Reviews one queued pull request end to end. It checks the pull request is still worth reviewing, plans
/// which files go to the model, runs the requests (in parallel when the change is large), merges the results,
/// re-checks that the commit is still the head, and posts one review tied to that commit.
/// </summary>
public sealed class ReviewOrchestrator : IReviewOrchestrator
{
    private readonly IGitHubAppAuthService _auth;
    private readonly IGitHubService _github;
    private readonly IClaudeService _claude;
    private readonly ReviewPlanner _planner;
    private readonly ReviewAggregator _aggregator;
    private readonly ReviewFormatter _formatter;
    private readonly ReviewInFlightRegistry _inFlight;
    private readonly ModelRequestLimiter _limiter;
    private readonly ChunkResultCache _cache;
    private readonly ReviewOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ReviewOrchestrator> _logger;

    public ReviewOrchestrator(
        IGitHubAppAuthService auth,
        IGitHubService github,
        IClaudeService claude,
        ReviewPlanner planner,
        ReviewAggregator aggregator,
        ReviewFormatter formatter,
        ReviewInFlightRegistry inFlight,
        ModelRequestLimiter limiter,
        ChunkResultCache cache,
        IOptions<ReviewOptions> options,
        TimeProvider timeProvider,
        ILogger<ReviewOrchestrator> logger)
    {
        _auth = auth;
        _github = github;
        _claude = claude;
        _planner = planner;
        _aggregator = aggregator;
        _formatter = formatter;
        _inFlight = inFlight;
        _limiter = limiter;
        _cache = cache;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    private sealed record ChunkRun(ClaudeReviewResult[] Results, ReviewSkipReason? StopReason);

    /// <summary>The first failure among parts that are running side by side, kept so the parts still running are not cancelled because of it.</summary>
    private sealed class FirstFailure
    {
        private Exception? _exception;

        public bool HasFailed => Volatile.Read(ref _exception) is not null;

        public void Record(Exception exception)
        {
            Interlocked.CompareExchange(ref _exception, exception, null);
        }

        public void ThrowIfAny()
        {
            if (Volatile.Read(ref _exception) is { } exception)
            {
                ExceptionDispatchInfo.Capture(exception).Throw();
            }
        }
    }

    private sealed class StopSignal
    {
        private int _reason = -1;

        public ReviewSkipReason? Reason => Volatile.Read(ref _reason) is var value and >= 0 ? (ReviewSkipReason)value : null;

        public bool IsRaised => Volatile.Read(ref _reason) >= 0;

        public void Raise(ReviewSkipReason reason)
        {
            Interlocked.CompareExchange(ref _reason, (int)reason, -1);
        }
    }

    public async Task<ReviewRunResult> ReviewPullRequestAsync(ReviewQueueMessage message, CancellationToken cancellationToken)
    {
        // One limit for the whole review, whatever it is waiting on: GitHub, the model, or its turn for a model slot behind other reviews. Without
        // it a review that stalls runs until the Functions host gives up on the invocation, which restarts the worker and takes every other
        // review the instance is running with it, and loses the finished parts that a retry could have reused.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(_options.MaxReviewDurationSeconds), _timeProvider);
        using var limited = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);

        try
        {
            return await RunReviewAsync(message, limited.Token);
        }
        catch (Exception exception) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Our own limit ran out (the host did not cancel), however the cancellation surfaced. Like a slow model it is worth another try, and
            // the parts that did finish are kept for it.
            throw new TimeoutException(
                $"The review of {message.Owner}/{message.Repo}#{message.PrNumber} did not finish within the {_options.MaxReviewDurationSeconds}-second limit.",
                exception);
        }
    }

    private async Task<ReviewRunResult> RunReviewAsync(ReviewQueueMessage message, CancellationToken cancellationToken)
    {
        var token = await _auth.GetInstallationTokenAsync(message.InstallationId, message.Repo, cancellationToken);
        var appLogin = await _auth.GetAppLoginAsync(cancellationToken);

        var pullRequest = await _github.GetPullRequestAsync(message.Owner, message.Repo, message.PrNumber, token, cancellationToken);

        if (GetSkipReason(pullRequest, message.HeadSha) is { } skipReason)
        {
            return Skip(message, skipReason);
        }

        var reviews = await _github.ListReviewsAsync(message.Owner, message.Repo, message.PrNumber, token, cancellationToken);

        // Only this app's own review of this very commit counts: anyone can post a review quoting the marker,
        // and a forged marker must not be able to suppress the review of a commit.
        if (ReviewHistory.HasCompletedReview(reviews, pullRequest.HeadSha, appLogin))
        {
            return Skip(message, ReviewSkipReason.AlreadyReviewed);
        }

        // Another message for this commit may already be running in this process (a reopen, a draft toggle, a redelivery). That run posts
        // the review, and this one would only pay for the same work and post it twice.
        using var lease = _inFlight.TryBegin(message.Owner, message.Repo, message.PrNumber, pullRequest.HeadSha);

        if (lease is null)
        {
            return Skip(message, ReviewSkipReason.InProgress);
        }

        var files = await _github.GetPullRequestFilesAsync(message.Owner, message.Repo, message.PrNumber, token, cancellationToken);
        var plan = _planner.CreatePlan(files);
        TagPlan(plan);

        // Silence is right only when there is no new code at all. Anything else the reviewer could not read still gets a coverage
        // report, because a pull request that gets no response looks exactly like an app that is down.
        if (plan.SkippedFiles.All(skipped => skipped.Reason == FileSkipReason.Deleted) && !plan.HasReviewableContent)
        {
            RecordPlan(pullRequest, plan);
            return Skip(message, ReviewSkipReason.NothingToReview);
        }

        var guidelines = await ReadGuidelinesAsync(message, pullRequest, plan, token, cancellationToken);
        var run = await ReviewChunksAsync(message, plan, pullRequest, guidelines, cancellationToken);

        if (run.StopReason is { } stopReason)
        {
            _logger.LogInformation(
                "Stopped reviewing {Owner}/{Repo}#{PrNumber} before every part was reviewed: {Reason}.",
                message.Owner, message.Repo, message.PrNumber, stopReason);
            return Skip(message, stopReason);
        }

        ThrowWhenEveryPartWasRejected(message, run.Results);

        var report = _aggregator.Aggregate(plan, run.Results) with { HasUnreadableGuidelines = guidelines.IsUnreadable };

        // The model can take minutes and the token was fetched before it started. Ask again: a token still in the cache comes back as it was,
        // and one that would have expired during the run is replaced.
        token = await _auth.GetInstallationTokenAsync(message.InstallationId, message.Repo, cancellationToken);

        // If the branch moved on meanwhile, this review describes a stale commit and the newer commit has its own queued review.
        var latest = await _github.GetPullRequestAsync(message.Owner, message.Repo, message.PrNumber, token, cancellationToken);

        if (GetSkipReason(latest, message.HeadSha) is { } staleReason)
        {
            _logger.LogInformation(
                "Discarding a finished review of {Owner}/{Repo}#{PrNumber}: {Reason}.",
                message.Owner, message.Repo, message.PrNumber, staleReason);
            return Skip(message, staleReason);
        }

        // Another message for this commit (a reopen, a redelivery, a second instance) may have posted its review while the
        // model was working, so read the reviews again rather than trusting the list from before.
        var currentReviews = await _github.ListReviewsAsync(message.Owner, message.Repo, message.PrNumber, token, cancellationToken);

        if (ReviewHistory.HasCompletedReview(currentReviews, pullRequest.HeadSha, appLogin))
        {
            _logger.LogInformation(
                "Not posting a second review of {Owner}/{Repo}#{PrNumber}: another run already reviewed this commit.",
                message.Owner, message.Repo, message.PrNumber);
            return Skip(message, ReviewSkipReason.AlreadyReviewed);
        }

        await DeletePendingReviewsAsync(message, token, ReviewHistory.OwnPendingReviews(currentReviews, appLogin), cancellationToken);

        // No model ran when nothing could be read, and the review says so rather than naming one.
        var model = plan.HasReviewableContent
            ? run.Results.Select(result => result.Model).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? "claude"
            : null;
        await PostReviewAsync(message, token, pullRequest, plan, report, model, cancellationToken);

        return RecordOutcome(pullRequest, plan, report);
    }

    private static ReviewSkipReason? GetSkipReason(PullRequestInfo pullRequest, string expectedHeadSha)
    {
        if (!pullRequest.IsOpen)
        {
            return ReviewSkipReason.NotOpen;
        }

        if (pullRequest.IsDraft)
        {
            return ReviewSkipReason.Draft;
        }

        if (!string.Equals(pullRequest.HeadSha, expectedHeadSha, StringComparison.Ordinal))
        {
            return ReviewSkipReason.Superseded;
        }

        return null;
    }

    private ReviewRunResult Skip(ReviewQueueMessage message, ReviewSkipReason reason)
    {
        _logger.LogInformation(
            "Skipping review of {Owner}/{Repo}#{PrNumber}: {Reason}.",
            message.Owner, message.Repo, message.PrNumber, reason);

        AppMeters.ReviewsSkipped.Add(1, new KeyValuePair<string, object?>("reason", reason.ToString()));

        return ReviewRunResult.Skipped(reason);
    }

    private async Task<RepositoryGuidelines> ReadGuidelinesAsync(
        ReviewQueueMessage message,
        PullRequestInfo pullRequest,
        ReviewPlan plan,
        string token,
        CancellationToken cancellationToken)
    {
        if (!plan.HasReviewableContent)
        {
            return RepositoryGuidelines.None;
        }

        if (!_auth.CanReadContents(message.InstallationId, message.Repo))
        {
            _logger.LogInformation(
                "The installation has no access to the contents of {Owner}/{Repo}, so its review guidelines are not read.",
                message.Owner, message.Repo);
            return RepositoryGuidelines.Unreadable;
        }

        // Read from the base commit, never the pull request: its author must not be able to rewrite the rules.
        return await _github.GetRepositoryGuidelinesAsync(message.Owner, message.Repo, pullRequest.BaseSha, token, cancellationToken);
    }

    private async Task<ChunkRun> ReviewChunksAsync(
        ReviewQueueMessage message,
        ReviewPlan plan,
        PullRequestInfo pullRequest,
        RepositoryGuidelines guidelines,
        CancellationToken cancellationToken)
    {
        var results = new ClaudeReviewResult[plan.Chunks.Count];

        if (plan.Chunks.Count == 0)
        {
            return new ChunkRun(results, null);
        }

        var systemPrompt = ReviewPromptBuilder.BuildSystemPrompt(guidelines.Text);
        var stop = new StopSignal();
        var transientFailure = new FirstFailure();

        // Cancelled when the commit turns out to have been replaced, so that the parts still being written stop being paid for.
        using var replaced = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Is this part still worth a request? Asked of each part twice: when it is picked up, from what is already known, and again once it has its
        // slot, because the wait for one can take minutes when reviews share the instance and what was true when the part was queued may not be true
        // when its turn comes. Only this second time is the pull request looked at, so that a part is not sent on the strength of an old answer.
        async Task<bool> IsStillWorthStartingAsync(CancellationToken slotToken)
        {
            if (stop.IsRaised || transientFailure.HasFailed)
            {
                return false;
            }

            if (plan.Chunks.Count > 1 && await FindNewerCommitAsync(message, slotToken) is { } moved)
            {
                stop.Raise(moved);
                await replaced.CancelAsync();
                return false;
            }

            return true;
        }

        try
        {
            await Parallel.ForEachAsync(
                Enumerable.Range(0, plan.Chunks.Count),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(1, _options.MaxParallelChunks),
                    CancellationToken = replaced.Token
                },
                async (index, chunkToken) =>
                {
                    // A part that has not started is not worth paying for once the commit has been replaced, and not worth starting once another part
                    // has failed in a way a retry can clear up: its request would probably meet the same rate limit or outage, and the retry runs it.
                    if (stop.IsRaised || transientFailure.HasFailed)
                    {
                        return;
                    }

                    try
                    {
                        var userMessage = ReviewPromptBuilder.BuildUserMessage(pullRequest, plan.Chunks[index], plan.Chunks.Count);

                        if (await ReviewChunkAsync(systemPrompt, userMessage, IsStillWorthStartingAsync, chunkToken) is { } result)
                        {
                            results[index] = result;
                        }
                    }
                    catch (OperationCanceledException) when (stop.IsRaised && !cancellationToken.IsCancellationRequested)
                    {
                        // This part was cancelled because the commit was replaced. Nothing went wrong with it.
                    }
                    catch (Exception exception) when (
                        !chunkToken.IsCancellationRequested && FailureClassifier.IsTransient(exception))
                    {
                        // A failure that can clear up does not cancel the parts still running: they are paid for, and what they finish is kept for the
                        // retry. One that cannot clear up is not caught, so it cancels them and is what surfaces.
                        transientFailure.Record(exception);
                    }
                });
        }
        catch (OperationCanceledException) when (stop.IsRaised && !cancellationToken.IsCancellationRequested)
        {
            // The loop itself was cancelled because the commit was replaced.
        }

        // A replaced commit outranks a failure that a retry would clear up: the retry would only find the newer commit and skip, after the wait.
        if (stop.Reason is { } reason)
        {
            return new ChunkRun(results, reason);
        }

        transientFailure.ThrowIfAny();

        return new ChunkRun(results, null);
    }

    private static void ThrowWhenEveryPartWasRejected(ReviewQueueMessage message, ClaudeReviewResult[] results)
    {
        if (results.Length == 0 || results.Any(result => result.Outcome != ChunkOutcome.Rejected))
        {
            return;
        }

        // Nothing was read and the same requests would be refused again, so this is a failed review, not a partial one. A coverage report saying so
        // for every pull request would hide a wrong model name or an empty account behind what looks like normal output. Some parts refused is
        // different: a part too large for the service says nothing about the others, and that review is still posted.
        throw new AiServiceRejectedRequestException(
            $"The AI service rejected all {results.Length} request(s) for {message.Owner}/{message.Repo}#{message.PrNumber}: {results[0].Detail}");
    }

    /// <summary>
    /// A look before a part's request is sent, to skip work that would be thrown away. It asks for the token itself, because a part can start many
    /// minutes after the review began, and a cached token is only retired ten minutes before it expires. It never fails the part: the check before
    /// posting is the one that matters, and it runs again. That includes a client that gave up waiting (a cancelled task that nobody cancelled):
    /// only our own cancellation ends it.
    /// </summary>
    private async Task<ReviewSkipReason?> FindNewerCommitAsync(ReviewQueueMessage message, CancellationToken cancellationToken)
    {
        try
        {
            var token = await _auth.GetInstallationTokenAsync(message.InstallationId, message.Repo, cancellationToken);
            var latest = await _github.GetPullRequestAsync(message.Owner, message.Repo, message.PrNumber, token, cancellationToken);

            return GetSkipReason(latest, message.HeadSha);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            if (exception is GitHubApiException { StatusCode: HttpStatusCode.Unauthorized })
            {
                // The cached token is no longer accepted. Dropped here, the check before posting asks for a new one instead of failing the same way.
                _auth.ForgetInstallationToken(message.InstallationId, message.Repo);
            }

            _logger.LogWarning(
                exception,
                "Could not check whether the commit of {Owner}/{Repo}#{PrNumber} was replaced before reviewing a part; reviewing it anyway.",
                message.Owner, message.Repo, message.PrNumber);

            return null;
        }
    }

    /// <returns>The result, or null when the part was not started because <paramref name="isStillWorthStarting"/> said no once the slot was granted.</returns>
    private async Task<ClaudeReviewResult?> ReviewChunkAsync(
        string systemPrompt,
        string userMessage,
        Func<CancellationToken, Task<bool>> isStillWorthStarting,
        CancellationToken cancellationToken)
    {
        // A part that already finished (in an earlier attempt that failed on a different part) is not paid for twice.
        var key = ChunkResultCache.KeyFor(systemPrompt, userMessage);

        if (_cache.Find(key) is { } known)
        {
            _logger.LogInformation("Reusing the finished result of a part that was already reviewed.");
            return known;
        }

        using (await _limiter.AcquireAsync(cancellationToken))
        {
            // A slot can be granted at the very moment the wait is cancelled. A request that is already cancelled is not worth starting.
            cancellationToken.ThrowIfCancellationRequested();

            if (!await isStillWorthStarting(cancellationToken))
            {
                return null;
            }

            ClaudeReviewResult result;

            try
            {
                result = await _claude.ReviewAsync(new ClaudeReviewRequest(systemPrompt, userMessage), cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A request that threw is counted too, or the counter would show fewer requests than were made and none of them failing. One that was
                // cancelled is not: the model did nothing wrong.
                AppMeters.ChunkRequests.Add(1, new KeyValuePair<string, object?>("outcome", "Failed"));
                throw;
            }

            AppMeters.ChunkRequests.Add(1, new KeyValuePair<string, object?>("outcome", result.Outcome.ToString()));
            _cache.Add(key, result);

            return result;
        }
    }

    private async Task DeletePendingReviewsAsync(
        ReviewQueueMessage message,
        string token,
        IReadOnlyList<PullRequestReviewSummary> ownPendingReviews,
        CancellationToken cancellationToken)
    {
        // GitHub allows one pending review per reviewer per pull request. A draft left behind by a run that
        // crashed would make the new review fail, so clear it first.
        foreach (var pending in ownPendingReviews)
        {
            try
            {
                _logger.LogInformation(
                    "Deleting pending review {ReviewId} before posting a new review for {Owner}/{Repo}#{PrNumber}.",
                    pending.Id, message.Owner, message.Repo, message.PrNumber);

                await _github.DeleteReviewAsync(message.Owner, message.Repo, message.PrNumber, pending.Id, token, cancellationToken);
            }
            catch (HttpRequestException exception) when (!FailureClassifier.IsTransient(exception))
            {
                _logger.LogWarning(exception, "Could not delete pending review {ReviewId}; trying to post anyway.", pending.Id);
            }
        }
    }

    private async Task PostReviewAsync(
        ReviewQueueMessage message,
        string token,
        PullRequestInfo pullRequest,
        ReviewPlan plan,
        ReviewReport report,
        string? model,
        CancellationToken cancellationToken)
    {
        var withInline = _formatter.Format(report, plan, pullRequest, model, hasInlineComments: true);

        try
        {
            await _github.PostReviewAsync(
                message.Owner, message.Repo, message.PrNumber,
                new ReviewSubmission(pullRequest.HeadSha, withInline.Body, withInline.Comments),
                token, cancellationToken);
        }
        catch (GitHubApiException exception) when (
            exception.StatusCode == HttpStatusCode.UnprocessableEntity && withInline.Comments.Count > 0)
        {
            // GitHub rejects the whole review if any inline comment points at a line it cannot place.
            // Findings are valuable, so post them all in the body rather than lose the review.
            _logger.LogWarning(
                exception,
                "GitHub rejected the review of {Owner}/{Repo}#{PrNumber} with HTTP 422 ({Reason}); posting the findings in the review body instead.",
                message.Owner, message.Repo, message.PrNumber, exception.Message);

            var bodyOnly = _formatter.Format(report, plan, pullRequest, model, hasInlineComments: false);

            await _github.PostReviewAsync(
                message.Owner, message.Repo, message.PrNumber,
                new ReviewSubmission(pullRequest.HeadSha, bodyOnly.Body, []),
                token, cancellationToken);
        }
    }

    private static void TagPlan(ReviewPlan plan)
    {
        Activity.Current?.SetTag("review.files_changed", plan.TotalFileCount);
        Activity.Current?.SetTag("review.files_reviewed", plan.ReviewedFileCount);
        Activity.Current?.SetTag("review.files_skipped", plan.SkippedFiles.Count);
        Activity.Current?.SetTag("review.chunks", plan.Chunks.Count);
    }

    /// <summary>
    /// Counted when the outcome is final (a review is posted, or there was nothing to review), not when the plan is made: a plan that is discarded
    /// because the commit was replaced is counted by the review of the new commit, and one that is retried would be counted again by every attempt.
    /// </summary>
    private static void RecordPlan(PullRequestInfo pullRequest, ReviewPlan plan)
    {
        AppMeters.PullRequestChangedLines.Record(pullRequest.ChangedLineCount);

        foreach (var group in plan.SkippedFiles.GroupBy(skipped => skipped.Reason))
        {
            AppMeters.FilesSkipped.Add(group.Count(), new KeyValuePair<string, object?>("reason", group.Key.ToString()));
        }
    }

    private static ReviewRunResult RecordOutcome(PullRequestInfo pullRequest, ReviewPlan plan, ReviewReport report)
    {
        RecordPlan(pullRequest, plan);

        foreach (var group in report.Findings.GroupBy(finding => finding.Severity))
        {
            AppMeters.FindingsReported.Add(group.Count(), new KeyValuePair<string, object?>("severity", group.Key.ToString()));
        }

        var notReviewedCount = report.IncompleteChunks.Sum(chunk => chunk.FilePaths.Count);
        var filesReviewed = plan.ReviewedFileCount - notReviewedCount;

        // Partial means something that could have been read was not. A lockfile or a binary is never read, so skipping one does not count.
        var isPartial = plan.PartialFileCount > 0
            || report.IncompleteChunks.Count > 0
            || !plan.HasReviewableContent
            || plan.SkippedFiles.Any(skipped => skipped.Reason is FileSkipReason.OverBudget or FileSkipReason.NoDiffAvailable);

        // Counted when the review is posted, not when it is planned: a plan that is discarded or retried reviewed nothing.
        AppMeters.FilesReviewed.Add(filesReviewed);

        if (isPartial)
        {
            AppMeters.ReviewsPartial.Add(1);
        }

        Activity.Current?.SetTag("review.findings", report.Findings.Count);
        Activity.Current?.SetTag("review.partial", isPartial);

        return ReviewRunResult.Posted(filesReviewed, report.Findings.Count, isPartial);
    }
}
