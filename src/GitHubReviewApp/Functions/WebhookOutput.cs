namespace GitHubReviewApp.Functions;

/// <summary>
/// What the webhook function produces: an HTTP response for GitHub, and (only when a review should run)
/// a message for the review queue. A null <see cref="QueueMessage"/> writes nothing to the queue.
/// </summary>
public class WebhookOutput
{
    [QueueOutput(ReviewQueue.Name, Connection = ReviewQueue.Connection)]
    public ReviewQueueMessage? QueueMessage { get; set; }

    [HttpResult]
    public HttpResponseData HttpResponse { get; set; } = default!;
}
