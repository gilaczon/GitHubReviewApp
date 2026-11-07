namespace GitHubReviewApp.Models;

/// <summary>The outcome of one Claude review request, already parsed into findings.</summary>
/// <param name="OmittedFindingCount">How many further findings the model says it found but did not list.</param>
/// <param name="DroppedFindingCount">How many entries in the model's findings list were not readable findings and were left out.</param>
public sealed record ClaudeReviewResult(
    ChunkOutcome Outcome,
    string Summary,
    IReadOnlyList<ReviewFinding> Findings,
    string Model,
    ClaudeUsage Usage,
    string? Detail = null,
    int OmittedFindingCount = 0,
    int DroppedFindingCount = 0);
