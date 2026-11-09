namespace GitHubReviewApp.ReviewPreview;

/// <summary>
/// The app's GitHub client with every write taken away: reads go to GitHub, and the comment and check run the review would write are kept in
/// <see cref="PreviewCapture"/> instead. Two reads are changed so any pull request can be previewed: it is reported open and not a draft (so a
/// merged, closed or draft pull request is reviewed as it stands), and it is reported to have no comments (so the app's earlier review of the
/// same commit, if any, never stops the preview).
/// </summary>
public sealed class PreviewGitHubService : IGitHubService
{
    private const long CapturedId = 1;

    private readonly IGitHubService _github;
    private readonly PreviewCapture _capture;

    public PreviewGitHubService(IGitHubService github, PreviewCapture capture)
    {
        _github = github;
        _capture = capture;
    }

    public async Task<PullRequestInfo> GetPullRequestAsync(string owner, string repo, int prNumber, string token, CancellationToken cancellationToken)
    {
        var pullRequest = await _github.GetPullRequestAsync(owner, repo, prNumber, token, cancellationToken);

        return pullRequest with { State = "open", IsDraft = false };
    }

    public Task<IReadOnlyList<PullRequestFile>> GetPullRequestFilesAsync(string owner, string repo, int prNumber, string token, CancellationToken cancellationToken)
    {
        return _github.GetPullRequestFilesAsync(owner, repo, prNumber, token, cancellationToken);
    }

    public Task<IReadOnlyList<IssueComment>> ListIssueCommentsAsync(string owner, string repo, int prNumber, string token, CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<IssueComment>>([]);
    }

    public Task<long> CreateIssueCommentAsync(string owner, string repo, int prNumber, string body, string token, CancellationToken cancellationToken)
    {
        _capture.RecordComment(body);
        return Task.FromResult(CapturedId);
    }

    public Task UpdateIssueCommentAsync(string owner, string repo, long commentId, string body, string token, CancellationToken cancellationToken)
    {
        _capture.RecordComment(body);
        return Task.CompletedTask;
    }

    public Task<long> CreateCheckRunAsync(string owner, string repo, CheckRunRequest checkRun, string token, CancellationToken cancellationToken)
    {
        _capture.RecordCheckRun(checkRun);
        return Task.FromResult(CapturedId);
    }
}
