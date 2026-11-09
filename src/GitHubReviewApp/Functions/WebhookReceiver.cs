namespace GitHubReviewApp.Functions;

public class WebhookReceiver
{
    /// <summary>
    /// The largest body read from a caller that has not yet been authenticated. A pull_request payload is tens of kilobytes, and even a
    /// description of the longest length GitHub allows (65,536 characters of up to four bytes each) is about a quarter of a megabyte, so a megabyte
    /// leaves four times that. GitHub's own ceiling of 25 MiB would let one anonymous request hold about twice that in memory while it is read.
    /// </summary>
    internal const int MaxBodyBytes = 1024 * 1024;

    /// <summary>The size of one read from the body, which is also the most that a read can go past <see cref="MaxBodyBytes"/> before it stops.</summary>
    internal const int ReadBufferBytes = 81_920;

    private const string WebhookSecretName = "GitHubWebhookSecret";

    // Actions after which the pull request's reviewable content may have changed. A draft that is marked
    // ready, and a pull request that is reopened, have nothing new to push, so they need their own entries.
    private static readonly HashSet<string> _reviewableActions = new(StringComparer.Ordinal)
    {
        "opened", "synchronize", "reopened", "ready_for_review"
    };

    private static readonly byte[] _utf8ByteOrderMark = [0xEF, 0xBB, 0xBF];

    private readonly IConfiguration _config;
    private readonly ReviewOptions _options;
    private readonly IRuntimeSettings _runtime;
    private readonly ILogger<WebhookReceiver> _logger;

    public WebhookReceiver(IConfiguration config, IOptions<ReviewOptions> options, IRuntimeSettings runtime, ILogger<WebhookReceiver> logger)
    {
        _config = config;
        _options = options.Value;
        _runtime = runtime;
        _logger = logger;
    }

    /// <summary>
    /// Answers GitHub with a status that tells the truth: 401 for a bad signature, 413 for a body over GitHub's maximum, 400 for a payload
    /// that cannot be used, 200 for events that need no review and 202 when a review was queued. A misconfigured secret throws, so the
    /// delivery shows as failed in GitHub instead of quietly "succeeding".
    /// </summary>
    [Function(nameof(WebhookReceiver))]
    public async Task<WebhookOutput> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "webhook/github")] HttpRequestData req)
    {
        // A missing secret fails every request, before any body is read, so a misconfigured deployment can never look healthy.
        var webhookSecret = GetWebhookSecret();

        // Lets a failed or ignored delivery in GitHub's "Recent Deliveries" be matched to these log lines.
        using var scope = _logger.BeginScope(new Dictionary<string, object> { ["DeliveryId"] = DescribeDelivery(req) });

        // 1. Read the raw body, bounded: the signature covers these exact bytes, and the caller is not yet authenticated.
        if (DeclaredLength(req) > MaxBodyBytes)
        {
            _logger.LogWarning("Webhook body declares a length over {MaxBytes} bytes.", MaxBodyBytes);
            return Respond(req, HttpStatusCode.RequestEntityTooLarge, "Payload too large.");
        }

        var body = await ReadBoundedAsync(req.Body);

        if (body is null)
        {
            _logger.LogWarning("Webhook body is larger than {MaxBytes} bytes.", MaxBodyBytes);
            return Respond(req, HttpStatusCode.RequestEntityTooLarge, "Payload too large.");
        }

        // 2. Validate the HMAC-SHA256 signature before looking at anything else.
        if (!IsSignatureValid(req, body, webhookSecret))
        {
            _logger.LogWarning("Webhook signature validation failed.");
            return Respond(req, HttpStatusCode.Unauthorized, "Invalid signature.");
        }

        // 3. Only pull_request events are of interest.
        if (!req.Headers.TryGetValues("X-GitHub-Event", out var events))
        {
            _logger.LogWarning("Webhook is missing the X-GitHub-Event header.");
            return Respond(req, HttpStatusCode.BadRequest, "Missing X-GitHub-Event header.");
        }

        if (events.FirstOrDefault() != "pull_request")
        {
            _logger.LogInformation("Ignored non-pull_request event.");
            return Respond(req, HttpStatusCode.OK, "Ignored: not a pull_request event.");
        }

        // 4. Read the payload from the same bytes that were signed. The parser does not skip a byte-order mark, but the signature covers it.
        PullRequestWebhookPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<PullRequestWebhookPayload>(body.AsSpan().StartsWith(_utf8ByteOrderMark) ? body.AsSpan(_utf8ByteOrderMark.Length) : body);
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "Webhook payload is not valid JSON.");
            return Respond(req, HttpStatusCode.BadRequest, "Invalid JSON payload.");
        }

        if (payload is null)
        {
            _logger.LogWarning("Webhook payload was empty.");
            return Respond(req, HttpStatusCode.BadRequest, "Empty payload.");
        }

        // 5. Filter to actions that need a review; skip drafts.
        if (payload.Action is null || !_reviewableActions.Contains(payload.Action))
        {
            _logger.LogInformation("Skipping PR action={Action}.", payload.Action);
            return Respond(req, HttpStatusCode.OK, $"Ignored: action '{payload.Action}' needs no review.");
        }

        if (payload.PullRequest?.IsDraft == true)
        {
            _logger.LogInformation("Skipping draft PR action={Action}.", payload.Action);
            return Respond(req, HttpStatusCode.OK, "Ignored: draft pull request.");
        }

        // The operator's switch (design 5.11). It is read after the signature and the filters, so a forged request still gets 401 and the
        // answers that mean "needs no review" stay as they are; what is left is a review that would have been queued.
        if (!_runtime.Current.IsReviewEnabled)
        {
            _logger.LogInformation("Ignored a review request: reviews are switched off.");
            return Respond(req, HttpStatusCode.OK, "Ignored: reviews are switched off.");
        }

        // 6. A message the processor cannot act on must not be queued.
        var message = ToQueueMessage(payload);

        if (message is null)
        {
            _logger.LogWarning("Webhook payload for action={Action} is missing required fields.", payload.Action);
            return Respond(req, HttpStatusCode.BadRequest, "Payload is missing required fields.");
        }

        if (!IsAuthorAllowed(payload.PullRequest?.AuthorAssociation))
        {
            _logger.LogInformation(
                "Skipping {Owner}/{Repo}#{PrNumber}: the author's association is not in the allow-list.",
                message.Owner, message.Repo, message.PrNumber);
            return Respond(req, HttpStatusCode.OK, "Ignored: the pull request author is not allowed to trigger a review.");
        }

        _logger.LogInformation(
            "Queuing review for {Owner}/{Repo}#{PrNumber} (action={Action}).",
            message.Owner, message.Repo, message.PrNumber, payload.Action);

        return Respond(req, HttpStatusCode.Accepted, "Review queued.", message);
    }

    private static string? Header(HttpRequestData req, string name)
    {
        return req.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
    }

    /// <summary>
    /// Anyone can send this header and it lands in every log line of the request, before the signature has been checked, so only what a delivery
    /// id looks like (a GUID) is let through.
    /// </summary>
    private static string DescribeDelivery(HttpRequestData req)
    {
        var header = Header(req, "X-GitHub-Delivery");

        if (header is null)
        {
            return "unknown";
        }

        return Guid.TryParseExact(header, "D", out var deliveryId) ? deliveryId.ToString("D") : "invalid";
    }

    private static long? DeclaredLength(HttpRequestData req)
    {
        return long.TryParse(Header(req, "Content-Length"), NumberStyles.None, CultureInfo.InvariantCulture, out var length) ? length : null;
    }

    private static async Task<byte[]?> ReadBoundedAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[ReadBufferBytes];
        int read;

        while ((read = await stream.ReadAsync(chunk)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static ReviewQueueMessage? ToQueueMessage(PullRequestWebhookPayload payload)
    {
        if (payload.PullRequest is not { } pullRequest
            || payload.Repository is not { } repository
            || payload.Installation is not { } installation
            || pullRequest.Head?.Sha is not { } headSha
            || repository.Owner?.Login is not { } owner
            || repository.Name is not { } repo)
        {
            return null;
        }

        var hasEverythingAReviewNeeds = !string.IsNullOrWhiteSpace(owner)
            && !string.IsNullOrWhiteSpace(repo)
            && !string.IsNullOrWhiteSpace(headSha)
            && pullRequest.Number > 0
            && installation.Id > 0;

        // The processor reads the title and the base commit from the API, so a payload without them is still usable.
        return hasEverythingAReviewNeeds
            ? new ReviewQueueMessage(owner, repo, pullRequest.Number, pullRequest.Title ?? string.Empty, pullRequest.Base?.Sha ?? string.Empty, headSha, installation.Id)
            : null;
    }

    private bool IsAuthorAllowed(string? authorAssociation)
    {
        var allowed = _options.EffectiveAllowedAuthorAssociations;

        if (allowed.Count == 0)
        {
            return true;
        }

        // With an allow-list set, a pull request that does not say who its author is cannot be shown to be allowed.
        return authorAssociation is not null && allowed.Contains(authorAssociation.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    private static WebhookOutput Respond(
        HttpRequestData req,
        HttpStatusCode statusCode,
        string message,
        ReviewQueueMessage? queueMessage = null)
    {
        var response = req.CreateResponse(statusCode);
        response.WriteString(message);

        return new WebhookOutput { QueueMessage = queueMessage, HttpResponse = response };
    }

    private string GetWebhookSecret()
    {
        // An empty secret would let anyone compute a valid signature, so it counts as missing.
        var webhookSecret = _config[WebhookSecretName];

        if (string.IsNullOrWhiteSpace(webhookSecret))
        {
            throw new InvalidOperationException($"Secret '{WebhookSecretName}' is not configured.");
        }

        return webhookSecret;
    }

    private bool IsSignatureValid(HttpRequestData req, byte[] body, string webhookSecret)
    {
        if (!req.Headers.TryGetValues("X-Hub-Signature-256", out var sigs))
        {
            _logger.LogWarning("Missing X-Hub-Signature-256 header.");
            return false;
        }

        var receivedSig = sigs.FirstOrDefault() ?? string.Empty;
        if (!receivedSig.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Over the raw bytes, never over text decoded from them: decoding and re-encoding can change the bytes.
        var computed = HMACSHA256.HashData(Encoding.UTF8.GetBytes(webhookSecret), body);
        var expectedSig = "sha256=" + Convert.ToHexString(computed).ToLowerInvariant();

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expectedSig),
            Encoding.ASCII.GetBytes(receivedSig));
    }
}
