namespace GitHubReviewApp.Models;

/// <summary>
/// What reading one run's answer gave: the answer, or why there was none. Exactly one of the two is set. <see cref="Error"/> is fixed
/// text that quotes nothing of the answer, so it can be logged.
/// </summary>
public sealed record ChecklistReadResult(RunAnswer? Answer, string? Error)
{
    /// <summary>An answer that was read.</summary>
    public static ChecklistReadResult Success(RunAnswer answer)
    {
        return new ChecklistReadResult(answer, null);
    }

    /// <summary>An answer that could not be used, and why.</summary>
    public static ChecklistReadResult Failure(string error)
    {
        return new ChecklistReadResult(null, error);
    }
}
