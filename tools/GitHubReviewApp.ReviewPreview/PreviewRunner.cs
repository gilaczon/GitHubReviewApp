namespace GitHubReviewApp.ReviewPreview;

/// <summary>
/// Runs one preview: reads the pull request, hands it to the app's own review exactly as the queue would, and posts what the review would
/// have written to the pull request to Slack instead.
/// </summary>
public sealed class PreviewRunner
{
    // The preview has no GitHub App installation; the id only keys the token cache of PreviewAuthService, which ignores it.
    private const long PreviewInstallationId = 0;

    private readonly IGitHubAppAuthService _auth;
    private readonly IGitHubService _github;
    private readonly IReviewOrchestrator _orchestrator;
    private readonly PreviewCapture _capture;
    private readonly ISlackPoster _slack;
    private readonly ILogger<PreviewRunner> _logger;

    public PreviewRunner(
        IGitHubAppAuthService auth,
        IGitHubService github,
        IReviewOrchestrator orchestrator,
        PreviewCapture capture,
        ISlackPoster slack,
        ILogger<PreviewRunner> logger)
    {
        _auth = auth;
        _github = github;
        _orchestrator = orchestrator;
        _capture = capture;
        _slack = slack;
        _logger = logger;
    }

    public async Task<ReviewRunResult> RunAsync(PullRequestLink link, CancellationToken cancellationToken)
    {
        var token = await _auth.GetInstallationTokenAsync(PreviewInstallationId, link.Repo, cancellationToken);
        var pullRequest = await _github.GetPullRequestAsync(link.Owner, link.Repo, link.Number, token, cancellationToken);
        var message = new ReviewQueueMessage(
            link.Owner, link.Repo, link.Number, pullRequest.Title, pullRequest.BaseSha, pullRequest.HeadSha, PreviewInstallationId);

        _logger.LogInformation(
            "Reviewing {PullRequest} at {HeadSha} ({ChangedFileCount} files, +{Additions} -{Deletions}). This takes a few minutes.",
            link.Name, pullRequest.HeadSha, pullRequest.ChangedFileCount, pullRequest.Additions, pullRequest.Deletions);

        var result = await _orchestrator.ReviewPullRequestAsync(message, cancellationToken);
        var text = result.Status == ReviewRunStatus.Skipped
            ? SlackMessageFormatter.FormatSkip(link, pullRequest.Title, result.SkipReason ?? ReviewSkipReason.NothingToReview)
            : SlackMessageFormatter.FormatReview(link, pullRequest.Title, _capture.CommentBody
                ?? throw new InvalidOperationException("The review finished without the comment it would have posted."));

        await _slack.PostAsync(text, cancellationToken);

        _logger.LogInformation("Posted the preview of {PullRequest} to Slack: {Status} {Verdict}.", link.Name, result.Status, result.Verdict);
        return result;
    }
}
