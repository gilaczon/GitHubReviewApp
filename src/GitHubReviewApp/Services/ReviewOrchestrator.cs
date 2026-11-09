namespace GitHubReviewApp.Services;

/// <summary>
/// Reviews one queued pull request against MoneyMe's Code Review Checklist (design 5.1, step 3). It checks the pull request is still worth
/// reviewing and has not been reviewed at this commit, builds the review input, runs the review twice in parallel on the chosen model, merges the
/// two checklists, checks again that the commit is still the head and still unreviewed, and publishes a check run and then the comment, which is
/// the pull request's record of the review. When no run gives an answer, it publishes "Review failed" the same way, or rethrows a failure that a
/// retry may clear up (design 5.12).
/// </summary>
public sealed class ReviewOrchestrator : IReviewOrchestrator
{
    private const int RunsPerReview = 2;
    private const string FailedCheckRunTitle = "Review failed";
    private const string TransientFailureOutcome = "transient_failure";
    private const string PermanentFailureOutcome = "permanent_failure";

    private readonly IGitHubAppAuthService _auth;
    private readonly IGitHubService _github;
    private readonly IClaudeService _claude;
    private readonly ReviewInputBuilder _inputBuilder;
    private readonly ModelSelector _modelSelector;
    private readonly ReviewPublisher _publisher;
    private readonly ReviewInFlightRegistry _inFlight;
    private readonly ModelRequestLimiter _limiter;
    private readonly ModelResultCache _cache;
    private readonly IRuntimeSettings _runtime;
    private readonly ReviewOptions _options;
    private readonly ClaudeOptions _claudeOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ReviewOrchestrator> _logger;

    public ReviewOrchestrator(
        IGitHubAppAuthService auth,
        IGitHubService github,
        IClaudeService claude,
        ReviewInputBuilder inputBuilder,
        ModelSelector modelSelector,
        ReviewPublisher publisher,
        ReviewInFlightRegistry inFlight,
        ModelRequestLimiter limiter,
        ModelResultCache cache,
        IRuntimeSettings runtime,
        IOptions<ReviewOptions> options,
        IOptions<ClaudeOptions> claudeOptions,
        TimeProvider timeProvider,
        ILogger<ReviewOrchestrator> logger)
    {
        _auth = auth;
        _github = github;
        _claude = claude;
        _inputBuilder = inputBuilder;
        _modelSelector = modelSelector;
        _publisher = publisher;
        _inFlight = inFlight;
        _limiter = limiter;
        _cache = cache;
        _runtime = runtime;
        _options = options.Value;
        _claudeOptions = claudeOptions.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>How one run ended: with a result, or with the failure it threw. Exactly one of the two is set.</summary>
    private sealed record RunEnding(int Index, ClaudeRunResult? Run, Exception? Failure)
    {
        public bool IsCompleted => Run?.Outcome == RunOutcome.Completed;
    }

    /// <summary>What a finished review publishes, and what its record says about it. The merge and the outcome are null for "Review failed".</summary>
    private sealed record Publication(string Body, CheckRunRequest CheckRun, MergedReview? Merged, ReviewOutcome? Outcome);

    public async Task<ReviewRunResult> ReviewPullRequestAsync(ReviewQueueMessage message, CancellationToken cancellationToken)
    {
        // One limit for the whole review, whatever it is waiting on: GitHub, the model, or its turn for a model slot behind other reviews. Without
        // it a review that stalls runs until the Functions host gives up on the invocation, which restarts the worker and takes every other
        // review the instance is running with it, and loses the finished runs that a retry could have reused.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(_options.MaxReviewDurationSeconds), _timeProvider);
        using var limited = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);

        try
        {
            return await RunReviewAsync(message, limited.Token);
        }
        catch (Exception exception) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Our own limit ran out (the host did not cancel), however the cancellation surfaced. Like a slow model it is worth another try, and
            // a run that did finish is kept for it.
            throw new TimeoutException(
                $"The review of {message.Owner}/{message.Repo}#{message.PrNumber} did not finish within the {_options.MaxReviewDurationSeconds}-second limit.",
                exception);
        }
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

    /// <summary>
    /// The failure to rethrow when no run completed, or null when both runs ended without an answer but without throwing. One that a retry may
    /// clear up comes first: a retry may well complete both runs. Then one that cannot, which the processor reports at once (design 5.12).
    /// </summary>
    private static Exception? FailureToRethrow(IReadOnlyList<RunEnding> endings)
    {
        var failures = endings.Select(ending => ending.Failure).OfType<Exception>().ToList();

        return failures.FirstOrDefault(FailureClassifier.IsTransient) ?? failures.FirstOrDefault();
    }

    /// <summary>What "Review failed" says when both runs ended without an answer: what both ended with, or that neither finished when they differ.</summary>
    private static string FailureReason(IReadOnlyList<RunEnding> endings)
    {
        var outcomes = endings.Select(ending => ending.Run!.Outcome).Distinct().ToList();

        if (outcomes.Count != 1)
        {
            return ReviewFailureReasons.NoneFinished;
        }

        return outcomes[0] switch
        {
            RunOutcome.Refused => ReviewFailureReasons.AllRefused,
            RunOutcome.OutputTruncated => ReviewFailureReasons.AllTruncated,
            RunOutcome.InvalidOutput => ReviewFailureReasons.AllInvalid,
            RunOutcome.Rejected => ReviewFailureReasons.AllRejected,
            _ => throw new UnreachableException($"A run that ended {outcomes[0]} is not a run that ended without an answer.")
        };
    }

    private static CheckRunConclusion ConclusionFor(ReviewVerdict verdict)
    {
        return verdict switch
        {
            ReviewVerdict.Pass => CheckRunConclusion.Success,
            ReviewVerdict.PassWithComments => CheckRunConclusion.Neutral,
            ReviewVerdict.Fail => CheckRunConclusion.Failure,
            _ => throw new UnreachableException($"The verdict {verdict} has no check run conclusion.")
        };
    }

    /// <summary>The model that answered a run, or the one asked for when the result names none.</summary>
    private static string AnsweringModel(ClaudeRunResult result, ModelChoice choice)
    {
        return string.IsNullOrWhiteSpace(result.Model) ? choice.Model : result.Model;
    }

    /// <summary>
    /// The answer with each finding's file matched to the changed file it names, so that the merge counts <c>src/Foo.cs</c> and <c>b/src/Foo.cs</c>
    /// as one file. A file that matches no changed file keeps the model's text.
    /// </summary>
    private static RunAnswer WithResolvedPaths(RunAnswer answer, PathResolver paths)
    {
        var findings = answer.Findings.Select(finding => finding with { File = paths.Resolve(finding.File) ?? finding.File }).ToList();

        return answer with { Findings = findings.AsReadOnly() };
    }

    private async Task<ReviewRunResult> RunReviewAsync(ReviewQueueMessage message, CancellationToken cancellationToken)
    {
        var startedAt = _timeProvider.GetTimestamp();
        var token = await _auth.GetInstallationTokenAsync(message.InstallationId, message.Repo, cancellationToken);
        var appLogin = await _auth.GetAppLoginAsync(cancellationToken);
        var pullRequest = await _github.GetPullRequestAsync(message.Owner, message.Repo, message.PrNumber, token, cancellationToken);

        if (GetSkipReason(pullRequest, message.HeadSha) is { } skipReason)
        {
            return Skip(message, skipReason);
        }

        // The record is this pull request's own comment (design 5.2): another pull request on the same commit has its own diff and its own review.
        // Only a comment by this app counts, since anyone can paste the marker into a comment of their own.
        var comments = await _github.ListIssueCommentsAsync(message.Owner, message.Repo, message.PrNumber, token, cancellationToken);

        if (ReviewRecord.HasReviewed(comments, appLogin, pullRequest.HeadSha))
        {
            return Skip(message, ReviewSkipReason.AlreadyReviewed);
        }

        // Another message for this commit may already be running in this process (a reopen, a draft toggle, a redelivery). That run publishes the
        // review, and this one would only pay for the same work.
        using var lease = _inFlight.TryBegin(message.Owner, message.Repo, message.PrNumber, pullRequest.HeadSha);

        if (lease is null)
        {
            return Skip(message, ReviewSkipReason.InProgress);
        }

        var files = await _github.GetPullRequestFilesAsync(message.Owner, message.Repo, message.PrNumber, token, cancellationToken);

        if (files.Count == 0)
        {
            return Skip(message, ReviewSkipReason.NothingToReview);
        }

        var input = _inputBuilder.Build(message.Owner, message.Repo, pullRequest, files);

        // The runtime settings in force as the review starts: a change an operator makes applies from the next review, never to half of this one.
        var choice = _modelSelector.Choose(message.Owner, message.Repo, pullRequest, input, _runtime.Current);
        var request = new ClaudeRunRequest(choice.Model, ChecklistPrompt.SystemPrompt, input.UserMessage);

        var endings = await Task.WhenAll(Enumerable.Range(0, RunsPerReview).Select(index => EndRunAsync(message, request, index, cancellationToken)));

        if (!endings.Any(ending => ending.IsCompleted) && FailureToRethrow(endings) is { } failure)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        LogFailedRuns(message, endings);

        // GitHub lists at most 3,000 of a pull request's files. The ones it did not list were not reviewed either.
        var unlistedCount = Math.Max(0, pullRequest.ChangedFileCount - files.Count);
        var paths = new PathResolver(files);
        var context = BuildContext(message, pullRequest, input, unlistedCount, endings, choice);
        var publication = endings.Any(ending => ending.IsCompleted)
            ? BuildReview(message, context, endings, paths)
            : BuildFailure(message, context, FailureReason(endings));

        // The model can take minutes and the token was fetched before it started. Ask again: a token still in the cache comes back as it was, and
        // one that would have expired during the runs is replaced. It also says whether the installation granted Checks, which is asked next.
        token = await _auth.GetInstallationTokenAsync(message.InstallationId, message.Repo, cancellationToken);

        // If the pull request was closed, turned into a draft or moved on meanwhile, this review describes a commit nobody is waiting on, and a
        // newer commit has its own queued review.
        var latest = await _github.GetPullRequestAsync(message.Owner, message.Repo, message.PrNumber, token, cancellationToken);

        if (GetSkipReason(latest, message.HeadSha) is { } staleReason)
        {
            _logger.LogInformation(
                "Discarding a finished review of {Owner}/{Repo}#{PrNumber}: {Reason}.",
                message.Owner, message.Repo, message.PrNumber, staleReason);
            return Skip(message, staleReason);
        }

        // Another instance may have published its review of this commit while the model was working, so the comments are read again.
        var currentComments = await _github.ListIssueCommentsAsync(message.Owner, message.Repo, message.PrNumber, token, cancellationToken);

        if (ReviewRecord.HasReviewed(currentComments, appLogin, pullRequest.HeadSha))
        {
            _logger.LogInformation(
                "Not publishing a second review of {Owner}/{Repo}#{PrNumber}: another run already reviewed this commit.",
                message.Owner, message.Repo, message.PrNumber);
            return Skip(message, ReviewSkipReason.AlreadyReviewed);
        }

        var canWriteChecks = _auth.CanWriteChecks(message.InstallationId, message.Repo);

        await _publisher.PublishAsync(
            message, token, appLogin, currentComments, publication.Body, publication.CheckRun, canWriteChecks, cancellationToken);

        RecordFinishedReview(message, pullRequest, input, choice, endings, publication, startedAt);

        return publication.Outcome is { } outcome
            ? ReviewRunResult.Posted(outcome.Verdict, publication.Merged!.RunCount, input.IncludedFileCount, IsPartial(input))
            : ReviewRunResult.Failed();
    }

    private ReviewRunResult Skip(ReviewQueueMessage message, ReviewSkipReason reason)
    {
        _logger.LogInformation(
            "Skipping review of {Owner}/{Repo}#{PrNumber}: {Reason}.",
            message.Owner, message.Repo, message.PrNumber, reason);

        AppMeters.ReviewsSkipped.Add(1, new KeyValuePair<string, object?>("reason", reason.ToString()));

        return ReviewRunResult.Skipped(reason);
    }

    /// <summary>
    /// Runs one of the review's runs and keeps what it threw, so that a failure does not cancel the other run, which is paid for and whose answer
    /// may still give a verdict. A cancellation of the review itself, by the host or the review's own limit, is not a failure of the run and is
    /// rethrown.
    /// </summary>
    private async Task<RunEnding> EndRunAsync(ReviewQueueMessage message, ClaudeRunRequest request, int runIndex, CancellationToken cancellationToken)
    {
        try
        {
            return new RunEnding(runIndex, await RunAsync(message, request, runIndex, cancellationToken), null);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new RunEnding(runIndex, null, exception);
        }
    }

    private async Task<ClaudeRunResult> RunAsync(ReviewQueueMessage message, ClaudeRunRequest request, int runIndex, CancellationToken cancellationToken)
    {
        // A run that completed in an earlier attempt of this review (one that failed later, or on the other run) is not paid for twice.
        var key = ModelResultCache.KeyFor(request, runIndex);

        if (_cache.Find(key) is { } known)
        {
            _logger.LogInformation(
                "Reusing run {RunIndex} of {Owner}/{Repo}#{PrNumber} from an earlier attempt.",
                runIndex, message.Owner, message.Repo, message.PrNumber);
            return known;
        }

        // Each run waits for its own slot, so the limiter counts both runs of every review the process is running.
        using (await _limiter.AcquireAsync(cancellationToken))
        {
            // A slot can be granted at the very moment the wait is cancelled. A request that is already cancelled is not worth starting.
            cancellationToken.ThrowIfCancellationRequested();

            var result = await _claude.ReviewAsync(request, cancellationToken);
            _cache.Add(key, result);

            return result;
        }
    }

    private void LogFailedRuns(ReviewQueueMessage message, IReadOnlyList<RunEnding> endings)
    {
        // Only reached when the other run completed, or when neither threw: the failure does not stop the review, so it is logged here.
        foreach (var ending in endings.Where(ending => ending.Failure is not null))
        {
            _logger.LogWarning(
                ending.Failure,
                "Run {RunIndex} of the review of {Owner}/{Repo}#{PrNumber} failed; publishing the verdict of the run that completed.",
                ending.Index, message.Owner, message.Repo, message.PrNumber);
        }
    }

    private static ReviewContext BuildContext(
        ReviewQueueMessage message,
        PullRequestInfo pullRequest,
        ReviewInput input,
        int unlistedCount,
        IReadOnlyList<RunEnding> endings,
        ModelChoice choice)
    {
        // Every file not reviewed in full: cut, left out for its size, never read, sent no diff by GitHub, or not listed by GitHub at all.
        var leftOutCount = input.PartialFileCount + input.OverBudgetFileCount + input.NotReadFileCount + input.NoDiffFileCount + unlistedCount;

        // The models that answered, run A's first, each once: two only when a refusal fallback answered one run with another model.
        var models = endings
            .Where(ending => ending.IsCompleted)
            .Select(ending => AnsweringModel(ending.Run!, choice))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new ReviewContext(
            message.Owner,
            message.Repo,
            message.PrNumber,
            pullRequest.HeadRef,
            pullRequest.BaseRef,
            pullRequest.AuthorLogin,
            pullRequest.HeadSha,
            input.Files.Count + unlistedCount,
            pullRequest.Additions,
            pullRequest.Deletions,
            input.IncludedFileCount,
            leftOutCount,
            models.AsReadOnly());
    }

    private static bool IsPartial(ReviewInput input)
    {
        // Partial means a listed file that could have been read was not read in full. A lockfile or a binary is never read, so it does not
        // count; nor does a file GitHub did not list, which the comment still counts as not reviewed.
        return input.PartialFileCount + input.OverBudgetFileCount + input.NoDiffFileCount > 0;
    }

    private static Publication BuildReview(ReviewQueueMessage message, ReviewContext context, IReadOnlyList<RunEnding> endings, PathResolver paths)
    {
        var answers = endings
            .Where(ending => ending.IsCompleted)
            .Select(ending => WithResolvedPaths(ending.Run!.Answer!, paths))
            .ToList();

        var merged = ChecklistMerger.Merge(answers);
        var outcome = ChecklistVerdict.Derive(merged.Checks);
        var body = ChecklistCommentFormatter.FormatReview(context, merged, outcome, FindingSelector.SelectShown(merged), paths);
        var checkRun = new CheckRunRequest(
            context.HeadSha,
            ConclusionFor(outcome.Verdict),
            outcome.Headline,
            ChecklistCommentFormatter.CheckRunSummary(body),
            message.PrNumber.ToString(CultureInfo.InvariantCulture));

        return new Publication(body, checkRun, merged, outcome);
    }

    private static Publication BuildFailure(ReviewQueueMessage message, ReviewContext context, string reason)
    {
        // The reason is the app's own words: a rejected run's detail is the AI service's text, which stays in the logs.
        var body = ChecklistCommentFormatter.FormatFailure(context, reason);
        var checkRun = new CheckRunRequest(
            context.HeadSha,
            CheckRunConclusion.Neutral,
            FailedCheckRunTitle,
            ChecklistCommentFormatter.CheckRunSummary(body),
            message.PrNumber.ToString(CultureInfo.InvariantCulture));

        return new Publication(body, checkRun, null, null);
    }

    /// <summary>
    /// The metrics and the review record of a published review. Counted when the result is published, not before: a review that is discarded
    /// because the commit moved on is counted by the newer commit's review, and one that is retried would otherwise be counted by every attempt.
    /// </summary>
    private void RecordFinishedReview(
        ReviewQueueMessage message,
        PullRequestInfo pullRequest,
        ReviewInput input,
        ModelChoice choice,
        IReadOnlyList<RunEnding> endings,
        Publication publication,
        long startedAt)
    {
        var runs = endings.Select(ending => ToRunRecord(ending, choice)).ToList();

        // A run whose cost is not known leaves the total unknown: a sum without it would read as the real cost.
        decimal? totalCost = runs.All(run => run.Cost is not null) ? runs.Sum(run => run.Cost!.Value) : null;
        var tier = new KeyValuePair<string, object?>("tier", ReviewRecordLog.TierText(choice));

        AppMeters.ModelChoices.Add(1, tier, new KeyValuePair<string, object?>("reason", choice.Reason));

        if (totalCost is { } cost)
        {
            AppMeters.ReviewCost.Record((double)cost, tier);
        }

        if (publication.Merged is { } merged && publication.Outcome is { } outcome)
        {
            RecordVerdict(merged, outcome);
            AppMeters.FilesReviewed.Add(input.IncludedFileCount);
            AppMeters.PullRequestChangedLines.Record(pullRequest.ChangedLineCount);

            if (IsPartial(input))
            {
                AppMeters.ReviewsPartial.Add(1);
            }
        }

        ReviewRecordLog.Write(
            _logger, message, choice, runs, publication.Merged, publication.Outcome, input, totalCost, _timeProvider.GetElapsedTime(startedAt));
    }

    private static void RecordVerdict(MergedReview merged, ReviewOutcome outcome)
    {
        foreach (var check in merged.Checks)
        {
            AppMeters.CheckStatuses.Add(
                1,
                new KeyValuePair<string, object?>("check", Checklist.WireId(check.Id)),
                new KeyValuePair<string, object?>("status", ReviewRecordLog.StatusText(check.Status)));
        }

        AppMeters.SeriousFlags.Add(outcome.SeriousCount);

        if (merged.RunCount == RunsPerReview && !merged.IsUnanimous)
        {
            AppMeters.ReviewDisagreements.Add(1, new KeyValuePair<string, object?>("kind", "checks"));
        }

        if (merged.OtherRunVerdict is { } otherVerdict && otherVerdict != merged.StricterRunVerdict)
        {
            AppMeters.ReviewDisagreements.Add(1, new KeyValuePair<string, object?>("kind", "verdict"));
        }
    }

    private RunRecord ToRunRecord(RunEnding ending, ModelChoice choice)
    {
        if (ending.Run is not { } result)
        {
            var failureOutcome = FailureClassifier.IsTransient(ending.Failure!) ? TransientFailureOutcome : PermanentFailureOutcome;

            return new RunRecord(ending.Index, failureOutcome, choice.Model, null, string.Empty, ClaudeUsage.Empty, null);
        }

        var cost = CostCalculator.CostOf(result.Attempts, _claudeOptions.Pricing);

        if (cost is null)
        {
            var unpriced = result.Attempts
                .Select(attempt => attempt.Model)
                .Where(model => !_claudeOptions.Pricing.Keys.Any(priced => string.Equals(priced, model, StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            _logger.LogWarning(
                "Claude:Pricing has no price for {Models}, so the cost of run {RunIndex} is not known.",
                string.Join(", ", unpriced), ending.Index);
        }

        var answer = ending.IsCompleted ? result.Answer : null;

        return new RunRecord(
            ending.Index,
            result.Outcome.ToString(),
            AnsweringModel(result, choice),
            answer is null ? null : ChecklistVerdict.OwnVerdict(answer),
            answer is null ? string.Empty : ReviewRecordLog.Statuses(answer),
            result.TotalUsage,
            cost);
    }
}
