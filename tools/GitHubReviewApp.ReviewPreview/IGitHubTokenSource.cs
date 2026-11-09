namespace GitHubReviewApp.ReviewPreview;

/// <summary>Where the preview gets the token it reads pull requests with.</summary>
public interface IGitHubTokenSource
{
    Task<string> GetTokenAsync(CancellationToken cancellationToken);
}
