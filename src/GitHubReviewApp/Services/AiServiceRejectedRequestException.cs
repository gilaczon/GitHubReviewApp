namespace GitHubReviewApp.Services;

/// <summary>
/// The AI service refused every part of a review as invalid (too large, or malformed) and would refuse it again, so retrying cannot help.
/// When only some parts are refused the review is still posted and names what was not read; this is for the case where nothing could be reviewed.
/// </summary>
public sealed class AiServiceRejectedRequestException : Exception
{
    public AiServiceRejectedRequestException(string message)
        : base(message)
    {
    }
}
