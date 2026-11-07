namespace GitHubReviewApp.Services;

public sealed class ReviewFailureNotifier : IReviewFailureNotifier
{
    private const int ShortShaLength = 7;

    private readonly IGitHubAppAuthService _auth;
    private readonly IGitHubService _github;
    private readonly ILogger<ReviewFailureNotifier> _logger;

    public ReviewFailureNotifier(
        IGitHubAppAuthService auth,
        IGitHubService github,
        ILogger<ReviewFailureNotifier> logger)
    {
        _auth = auth;
        _github = github;
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

        // Another queued message for the same commit may have completed while this one was failing. Only this app's
        // own review of this commit counts: anyone can paste the marker text into a review of their own.
        var appLogin = await _auth.GetAppLoginAsync(cancellationToken);
        var reviews = await _github.ListReviewsAsync(message.Owner, message.Repo, message.PrNumber, token, cancellationToken);

        if (ReviewHistory.HasCompletedReview(reviews, message.HeadSha, appLogin))
        {
            _logger.LogInformation(
                "Not posting a failure notice for {Owner}/{Repo}#{PrNumber}: this commit has been reviewed since.",
                message.Owner, message.Repo, message.PrNumber);
            return;
        }

        var shortSha = message.HeadSha.Length > ShortShaLength ? message.HeadSha[..ShortShaLength] : message.HeadSha;

        var body = $"**AI code review did not complete** for commit `{shortSha}`.\n\n"
            + reason
            + "\n\nPush a new commit, or close and re-open this pull request, to try again.";

        await _github.PostCommentAsync(message.Owner, message.Repo, message.PrNumber, body, token, cancellationToken);

        _logger.LogInformation(
            "Posted a failure notice on {Owner}/{Repo}#{PrNumber} for commit {HeadSha}.",
            message.Owner, message.Repo, message.PrNumber, message.HeadSha);
    }
}
