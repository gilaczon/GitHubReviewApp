namespace GitHubReviewApp.Models;

/// <summary>The merged result of every review request for one pull request, ready to be formatted.</summary>
/// <param name="DroppedFindingCount">How many entries in the model's findings lists could not be read as findings and are not shown.</param>
public sealed record ReviewReport(
    IReadOnlyList<string> Summaries,
    IReadOnlyList<ReviewFinding> Findings,
    int OmittedFindingCount,
    IReadOnlyList<IncompleteChunk> IncompleteChunks,
    ClaudeUsage Usage,
    bool HasUnreadableGuidelines = false,
    int DroppedFindingCount = 0);
