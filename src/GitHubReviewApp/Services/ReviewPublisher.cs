namespace GitHubReviewApp.Services;

/// <summary>
/// Publishes a finished result on a pull request (design 5.1, 5.9): the check run first, then the comment, which is written last because it is
/// the app's record of the commit (design 5.2). A check run that fails stops the publish before the comment is touched, so the queue's retry
/// finds no record and publishes again. A retry after a comment failure may add a second, identical check run, which is harmless.
/// </summary>
public sealed class ReviewPublisher
{
    private readonly IGitHubService _github;
    private readonly ILogger<ReviewPublisher> _logger;

    public ReviewPublisher(IGitHubService github, ILogger<ReviewPublisher> logger)
    {
        _github = github;
        _logger = logger;
    }

    /// <summary>
    /// Creates the check run when <paramref name="canWriteChecks"/> (the installation may not have granted the Checks permission yet), then
    /// replaces the body of the app's own comment among <paramref name="comments"/>, or posts a new one when it has none.
    /// </summary>
    public async Task PublishAsync(
        ReviewQueueMessage message,
        string token,
        string appLogin,
        IReadOnlyList<IssueComment> comments,
        string body,
        CheckRunRequest checkRun,
        bool canWriteChecks,
        CancellationToken cancellationToken)
    {
        if (canWriteChecks)
        {
            await _github.CreateCheckRunAsync(message.Owner, message.Repo, checkRun, token, cancellationToken);
        }
        else
        {
            _logger.LogWarning(
                "Not creating a check run for {Owner}/{Repo}#{PrNumber}: the installation has not granted the Checks permission, "
                + "so its token was issued with pull request access only.",
                message.Owner, message.Repo, message.PrNumber);
        }

        var own = ReviewRecord.FindOwnComment(comments, appLogin);

        if (own is null)
        {
            await _github.CreateIssueCommentAsync(message.Owner, message.Repo, message.PrNumber, body, token, cancellationToken);
        }
        else
        {
            await _github.UpdateIssueCommentAsync(message.Owner, message.Repo, own.Id, body, token, cancellationToken);
        }

        _logger.LogInformation(
            "Published the result for {Owner}/{Repo}#{PrNumber} at {HeadSha}: check run {HasCheckRun}, comment {CommentAction}.",
            message.Owner, message.Repo, message.PrNumber, checkRun.HeadSha, canWriteChecks, own is null ? "created" : "updated");
    }
}
