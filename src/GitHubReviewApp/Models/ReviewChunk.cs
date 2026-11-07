namespace GitHubReviewApp.Models;

/// <summary>A group of files reviewed in one Claude request. <see cref="Index"/> is zero-based.</summary>
public sealed record ReviewChunk(int Index, IReadOnlyList<PlannedFile> Files)
{
    /// <summary>The size of the diff text in this request, in UTF-8 bytes.</summary>
    public int TotalBytes => Files.Sum(file => file.RenderedBytes);
}
