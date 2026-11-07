namespace GitHubReviewApp.Models;

/// <summary>A review request that did not produce a usable review, and the files it covered.</summary>
public sealed record IncompleteChunk(
    int Index,
    IReadOnlyList<string> FilePaths,
    ChunkOutcome Outcome,
    string? Detail);
