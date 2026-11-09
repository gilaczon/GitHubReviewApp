namespace GitHubReviewApp.ReviewPreview;

/// <summary>
/// Posts to a Slack incoming webhook. The webhook's URL decides the conversation, so the URL is the only setting; it is a secret (anyone with
/// it can post there), read from configuration at the time of posting and never written to a log or an error message.
/// </summary>
public sealed class SlackWebhookPoster : ISlackPoster
{
    public const string WebhookUrlSettingName = "SlackWebhookUrl";

    private const int MaxReplyChars = 200;

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;

    public SlackWebhookPoster(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
    }

    public async Task PostAsync(string text, CancellationToken cancellationToken)
    {
        var webhookUrl = _configuration[WebhookUrlSettingName];

        if (!Uri.TryCreate(webhookUrl, UriKind.Absolute, out var webhook) || webhook.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                $"{WebhookUrlSettingName} is missing or is not an https URL. Set it to the Slack incoming webhook's URL, in user secrets or an environment variable.");
        }

        using var response = await _httpClient.PostAsJsonAsync(webhook, new { text }, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // Slack answers a refusal with a short code such as "invalid_payload" or "no_service".
            var reply = await response.Content.ReadAsStringAsync(cancellationToken);
            var shortReply = reply.Length <= MaxReplyChars ? reply : reply[..MaxReplyChars];

            throw new InvalidOperationException($"Slack refused the message: HTTP {(int)response.StatusCode} {shortReply}");
        }
    }
}
