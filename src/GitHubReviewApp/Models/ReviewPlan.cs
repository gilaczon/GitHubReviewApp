namespace GitHubReviewApp.Models;

/// <summary>How a pull request will be reviewed: which files go to the model, in which requests, and what is left out.</summary>
public sealed record ReviewPlan(
    IReadOnlyList<ReviewChunk> Chunks,
    IReadOnlyList<SkippedFile> SkippedFiles,
    int TotalFileCount)
{
    public int ReviewedFileCount => Chunks.Sum(chunk => chunk.Files.Count);

    public int PartialFileCount => Chunks.Sum(chunk => chunk.Files.Count(file => file.IsPartial));

    public bool HasReviewableContent => ReviewedFileCount > 0;
}
