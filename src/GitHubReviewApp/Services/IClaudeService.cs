namespace GitHubReviewApp.Services;

public interface IClaudeService
{
    /// <summary>
    /// Runs one checklist review: sends the request to the model it names and reads the answer. Failures of the call itself (rate limits,
    /// outages, bad credentials, a dropped stream) are thrown; a run that was refused, cut off or unreadable, or whose request the AI service
    /// refused as invalid, is returned as a <see cref="ClaudeRunResult"/> with the matching <see cref="RunOutcome"/>.
    /// </summary>
    Task<ClaudeRunResult> ReviewAsync(ClaudeRunRequest request, CancellationToken cancellationToken);
}
