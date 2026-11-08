namespace GitHubReviewApp.Models;

/// <summary>
/// How one checklist run ended. <paramref name="Answer"/> is set only when <paramref name="Outcome"/> is <see cref="RunOutcome.Completed"/>.
/// <paramref name="Model"/> is the model that produced the answer, which after a refusal fallback is not the one asked for, and is empty when no
/// model ran. <paramref name="Attempts"/> holds every attempt the API made, declined ones included, each with its own model and tokens.
/// <paramref name="Detail"/> says why a run did not complete, in fixed words or a refusal category, never in model text.
/// </summary>
public sealed record ClaudeRunResult(
    RunOutcome Outcome,
    RunAnswer? Answer,
    string Model,
    IReadOnlyList<ModelAttempt> Attempts,
    string? Detail = null)
{
    /// <summary>The tokens of every attempt added up.</summary>
    public ClaudeUsage TotalUsage => Attempts.Aggregate(ClaudeUsage.Empty, (total, attempt) => total.Plus(attempt.Usage));
}
