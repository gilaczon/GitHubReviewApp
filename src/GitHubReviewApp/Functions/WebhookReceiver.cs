namespace GitHubReviewApp.Functions;

public class WebhookReceiver
{
    private readonly IConfiguration _config;
    private readonly ILogger<WebhookReceiver> _logger;

    private const string WebhookSecretName = "GitHubWebhookSecret";

    public WebhookReceiver(IConfiguration config, ILogger<WebhookReceiver> logger)
    {
        _config = config;
        _logger = logger;
    }

    [Function(nameof(WebhookReceiver))]
    [QueueOutput("ai-review-queue", Connection = "ReviewQueueConnection")]
    public async Task<ReviewQueueMessage?> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "webhook/github")] HttpRequestData req)
    {
        try
        {
            // 1. Only handle pull_request events
            if (!req.Headers.TryGetValues("X-GitHub-Event", out var events) ||
                events.FirstOrDefault() != "pull_request")
            {
                _logger.LogInformation("Ignored non-pull_request event.");
                return null;
            }

            // 2. Read raw body (must happen before any stream position changes)
            var body = await new StreamReader(req.Body).ReadToEndAsync();

            // 3. Validate HMAC-SHA256 signature
            if (!ValidateSignature(req, body))
            {
                _logger.LogWarning("Webhook signature validation failed.");
                return null;
            }

            // 4. Deserialize payload
            var payload = JsonSerializer.Deserialize<PullRequestWebhookPayload>(body);
            if (payload is null)
            {
                _logger.LogError("Failed to deserialize webhook payload.");
                return null;
            }

            // 5. Filter to actionable events; skip drafts
            if (payload.Action is not ("opened" or "synchronize") || payload.PullRequest.Draft)
            {
                _logger.LogInformation("Skipping PR action={Action} draft={Draft}.",
                    payload.Action, payload.PullRequest.Draft);
                return null;
            }

            _logger.LogInformation(
                "Queuing review for {Owner}/{Repo}#{PrNumber}.",
                payload.Repository.Owner.Login,
                payload.Repository.Name,
                payload.PullRequest.Number);

            // 6. Return message to queue — function framework writes it automatically
            return new ReviewQueueMessage(
                Owner: payload.Repository.Owner.Login,
                Repo: payload.Repository.Name,
                PrNumber: payload.PullRequest.Number,
                PrTitle: payload.PullRequest.Title,
                BaseSha: payload.PullRequest.Base.Sha,
                HeadSha: payload.PullRequest.Head.Sha,
                InstallationId: payload.Installation.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception processing GitHub webhook.");
            return null;
        }
    }

    private bool ValidateSignature(HttpRequestData req, string body)
    {
        if (!req.Headers.TryGetValues("X-Hub-Signature-256", out var sigs))
        {
            _logger.LogWarning("Missing X-Hub-Signature-256 header.");
            return false;
        }

        var receivedSig = sigs.FirstOrDefault() ?? string.Empty;
        if (!receivedSig.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
            return false;

        var webhookSecret = _config[WebhookSecretName]
            ?? throw new InvalidOperationException($"Secret '{WebhookSecretName}' is not configured.");
        var keyBytes = Encoding.UTF8.GetBytes(webhookSecret);
        var bodyBytes = Encoding.UTF8.GetBytes(body);

        var computed = HMACSHA256.HashData(keyBytes, bodyBytes);
        var expectedSig = "sha256=" + Convert.ToHexString(computed).ToLowerInvariant();

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expectedSig),
            Encoding.ASCII.GetBytes(receivedSig));
    }
}
