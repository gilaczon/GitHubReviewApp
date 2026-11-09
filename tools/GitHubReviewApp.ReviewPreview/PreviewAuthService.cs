namespace GitHubReviewApp.ReviewPreview;

/// <summary>
/// Stands in for the GitHub App's sign-in: the token is the developer's own (see <see cref="IGitHubTokenSource"/>), and the app login is one
/// that no real account has, so no comment on the pull request is ever taken for the app's own and every preview reviews afresh.
/// </summary>
public sealed class PreviewAuthService : IGitHubAppAuthService
{
    /// <summary>A login GitHub cannot issue (no account may contain "[bot]" except an app's), so it matches no comment.</summary>
    public const string PreviewLogin = "review-preview[bot]";

    private readonly IGitHubTokenSource _tokenSource;
    private string? _token;

    public PreviewAuthService(IGitHubTokenSource tokenSource)
    {
        _tokenSource = tokenSource;
    }

    public async Task<string> GetInstallationTokenAsync(long installationId, string repository, CancellationToken cancellationToken)
    {
        return _token ??= await _tokenSource.GetTokenAsync(cancellationToken);
    }

    /// <summary>Always true, so the review builds its check run as well; the preview captures it instead of creating it.</summary>
    public bool CanWriteChecks(long installationId, string repository)
    {
        return true;
    }

    public void ForgetInstallationToken(long installationId, string repository)
    {
        _token = null;
    }

    public Task<string> GetAppLoginAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(PreviewLogin);
    }
}
