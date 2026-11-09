namespace GitHubReviewApp.Services;

public sealed class ReviewFailureNotifier : IReviewFailureNotifier
{
    private const string CheckRunTitle = "Review failed";

    private readonly IGitHubAppAuthService _auth;
    private readonly IGitHubService _github;
    private readonly ReviewPublisher _publisher;
    private readonly ILogger<ReviewFailureNotifier> _logger;

    public ReviewFailureNotifier(
        IGitHubAppAuthService auth,
        IGitHubService github,
        ReviewPublisher publisher,
        ILogger<ReviewFailureNotifier> logger)
    {
        _auth = auth;
        _github = github;
        _publisher = publisher;
        _logger = logger;
    }

    public async Task NotifyFailureAsync(ReviewQueueMessage message, string reason, CancellationToken cancellationToken)
    {
        var token = await _auth.GetInstallationTokenAsync(message.InstallationId, message.Repo, cancellationToken);
        var pullRequest = await _github.GetPullRequestAsync(message.Owner, message.Repo, message.PrNumber, token, cancellationToken);

        var isStale = !pullRequest.IsOpen
            || pullRequest.IsDraft
            || !string.Equals(pullRequest.HeadSha, message.HeadSha, StringComparison.Ordinal);

        if (isStale)
        {
            _logger.LogInformation(
                "Not posting a failure notice for {Owner}/{Repo}#{PrNumber}: it is closed, a draft, or has a newer commit.",
                message.Owner, message.Repo, message.PrNumber);
            return;
        }

        // Another queued message for the same commit may have completed while this one was failing. Only this app's own comment counts:
        // anyone can paste the marker text into a comment of their own.
        var appLogin = await _auth.GetAppLoginAsync(cancellationToken);
        var comments = await _github.ListIssueCommentsAsync(message.Owner, message.Repo, message.PrNumber, token, cancellationToken);

        if (ReviewRecord.HasReviewed(comments, appLogin, message.HeadSha))
        {
            _logger.LogInformation(
                "Not posting a failure notice for {Owner}/{Repo}#{PrNumber}: this commit has been reviewed since.",
                message.Owner, message.Repo, message.PrNumber);
            return;
        }

        // The notice has no review behind it, so it counts no files and names no model.
        var context = new ReviewContext(
            message.Owner,
            message.Repo,
            message.PrNumber,
            pullRequest.HeadRef,
            pullRequest.BaseRef,
            pullRequest.AuthorLogin,
            message.HeadSha,
            pullRequest.ChangedFileCount,
            pullRequest.Additions,
            pullRequest.Deletions,
            IncludedFileCount: 0,
            LeftOutFileCount: 0,
            Models: []);

        var body = ChecklistCommentFormatter.FormatFailure(context, reason);
        var checkRun = new CheckRunRequest(
            message.HeadSha,
            CheckRunConclusion.Neutral,
            CheckRunTitle,
            ChecklistCommentFormatter.CheckRunSummary(body),
            message.PrNumber.ToString(CultureInfo.InvariantCulture));

        var canWriteChecks = _auth.CanWriteChecks(message.InstallationId, message.Repo);

        await _publisher.PublishAsync(message, token, appLogin, comments, body, checkRun, canWriteChecks, cancellationToken);

        _logger.LogInformation(
            "Published a failure notice on {Owner}/{Repo}#{PrNumber} for commit {HeadSha}.",
            message.Owner, message.Repo, message.PrNumber, message.HeadSha);
    }
}
