namespace GitHubReviewApp.Functions;

/// <summary>
/// Names shared by the function that writes review requests (the webhook) and the functions that read them.
/// These are the deployed queue names: renaming one orphans any messages already in the queue.
/// </summary>
internal static class ReviewQueue
{
    public const string Name = "ai-review-queue";

    /// <summary>The Functions host moves a message here after it has failed its maximum number of attempts.</summary>
    public const string PoisonName = "ai-review-queue-poison";

    public const string Connection = "ReviewQueueConnection";
}
