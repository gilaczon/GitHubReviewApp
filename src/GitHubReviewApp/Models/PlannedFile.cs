namespace GitHubReviewApp.Models;

/// <summary>A file selected for review, with the text the model will see.</summary>
public sealed record PlannedFile(PullRequestFile File, string RenderedDiff, bool IsPartial)
{
    /// <summary>
    /// The size of <see cref="RenderedDiff"/> in UTF-8 bytes, which is what every size budget measures. A token is at least one byte, so bytes
    /// bound the tokens however unusual the text is, which a count of characters does not: one emoji or one rare character can be several tokens.
    /// </summary>
    public int RenderedBytes => Encoding.UTF8.GetByteCount(RenderedDiff);
}
