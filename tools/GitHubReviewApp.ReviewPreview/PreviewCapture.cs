namespace GitHubReviewApp.ReviewPreview;

/// <summary>What the review would have written to the pull request: the comment body and the check run. One per preview run.</summary>
public sealed class PreviewCapture
{
    public string? CommentBody { get; private set; }

    public CheckRunRequest? CheckRun { get; private set; }

    public void RecordComment(string body)
    {
        CommentBody = body;
    }

    public void RecordCheckRun(CheckRunRequest checkRun)
    {
        CheckRun = checkRun;
    }
}
