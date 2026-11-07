namespace GitHubReviewApp.Models;

/// <summary>
/// The model's reply read as a review. <see cref="IsStructured"/> is false when the reply cannot be used as a review at all (not the requested JSON,
/// no findings list, or findings that could not be read), and <see cref="Error"/> then says why. A reply that is not a review is never read as
/// "no findings": that would turn a broken reply into a clean bill of health.
/// </summary>
/// <param name="OmittedFindingCount">How many further findings the model says it found but left out because of the per-request limit.</param>
/// <param name="DroppedEntryCount">Entries in the findings list that were not readable findings and were left out.</param>
public sealed record ParsedReview(
    bool IsStructured,
    string Summary,
    IReadOnlyList<ReviewFinding> Findings,
    string? Error,
    int OmittedFindingCount = 0,
    int DroppedEntryCount = 0);
