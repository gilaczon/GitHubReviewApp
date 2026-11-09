namespace GitHubReviewApp.ReviewPreview;

/// <summary>Sends one message to the Slack conversation the preview reports to.</summary>
public interface ISlackPoster
{
    Task PostAsync(string text, CancellationToken cancellationToken);
}
