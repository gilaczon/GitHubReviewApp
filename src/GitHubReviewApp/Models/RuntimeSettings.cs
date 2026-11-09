namespace GitHubReviewApp.Models;

/// <summary>
/// The settings an operator can change while the app runs (design 5.11): whether reviews are on, and the ids of the two models. What a review
/// uses is whatever these say when it starts; <see cref="Default"/> is what applies when they say nothing.
/// </summary>
public sealed record RuntimeSettings(bool IsReviewEnabled, string SonnetModel, string OpusModel)
{
    public static RuntimeSettings Default { get; } = new(true, "claude-sonnet-5-5", "claude-opus-5-5");
}
