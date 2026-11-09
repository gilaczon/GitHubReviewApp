namespace GitHubReviewApp.Services;

public interface IGitHubAppAuthService
{
    /// <summary>
    /// Returns an installation access token limited to one repository. Scoping the token to the repository
    /// being reviewed means a leaked token cannot touch the installation's other repositories.
    /// </summary>
    Task<string> GetInstallationTokenAsync(long installationId, string repository, CancellationToken cancellationToken);

    /// <summary>
    /// False when the token cached for this repository was issued without the contents permission, because the installation was never granted
    /// it. GitHub answers "not found" to a private repository's file in that case, so a missing guidelines file cannot be told from a hidden one.
    /// </summary>
    bool CanReadContents(long installationId, string repository);

    /// <summary>
    /// False when the token cached for this repository was issued without the checks permission, because the installation was never granted it.
    /// A check run is then not attempted: GitHub would refuse it, and the comment alone still carries the result. True when no token is cached.
    /// </summary>
    bool CanWriteChecks(long installationId, string repository);

    /// <summary>
    /// Drops the cached token for this repository. GitHub answers 401 to a token it no longer accepts (revoked, or expired early), and the cached
    /// copy would be handed out again until its own expiry time, failing every review of the repository the same way.
    /// </summary>
    void ForgetInstallationToken(long installationId, string repository);

    /// <summary>
    /// The login GitHub shows on this app's reviews and comments, "{slug}[bot]". It tells this app's own reviews apart from those
    /// of every other account, bots included. It is looked up once and kept for the life of the process.
    /// </summary>
    Task<string> GetAppLoginAsync(CancellationToken cancellationToken);
}
