namespace GitHubReviewApp.Services;

public interface IClaudeService
{
    /// <summary>
    /// Sends one review request and returns the parsed outcome. Failures of the call itself (rate limits,
    /// outages, bad credentials) are thrown; a reply that is incomplete, refused or unreadable is returned
    /// as a <see cref="ClaudeReviewResult"/> with the matching <see cref="ChunkOutcome"/>.
    /// </summary>
    Task<ClaudeReviewResult> ReviewAsync(ClaudeReviewRequest request, CancellationToken cancellationToken);
}
