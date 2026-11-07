namespace GitHubReviewApp.Services;

/// <summary>
/// Separates failures worth retrying (rate limits, outages, timeouts, dropped connections) from failures that
/// would fail identically on every attempt (bad credentials, missing permissions, rejected requests,
/// configuration and programming errors). The queue retries only the first kind.
/// </summary>
public static class FailureClassifier
{
    private const string SpendLimitErrorCode = "enforced_spend_limit_reached";

    public static bool IsTransient(Exception exception)
    {
        return exception switch
        {
            // A 401 is GitHub no longer accepting an installation token, which can expire or be revoked while a long review runs. The retry mints
            // a new one (the processor drops the old one first), so it is worth trying again. A wrong key for the app itself fails every time too,
            // but only costs the few attempts the queue allows.
            GitHubApiException gitHub => gitHub.IsRateLimited || gitHub.StatusCode == HttpStatusCode.Unauthorized || IsTransientStatus((int)gitHub.StatusCode!.Value),
            AnthropicRateLimitException rateLimit => !IsAiSpendLimitReached(rateLimit),
            Anthropic5xxException => true,
            AnthropicIOException => true,

            // An error event inside a stream that began with a 200 (typically "overloaded") or a stream that breaks: the request was fine, so retry it.
            AnthropicSseException => true,
            AnthropicApiException api => IsTransientStatus((int)api.StatusCode),
            AnthropicException => false,
            HttpRequestException http => http.StatusCode is null || IsTransientStatus((int)http.StatusCode.Value),
            AggregateException aggregate => aggregate.InnerExceptions.Count > 0 && aggregate.InnerExceptions.All(IsTransient),
            TimeoutException => true,
            IOException => true,
            OperationCanceledException => true,
            _ => false
        };
    }

    /// <summary>
    /// The provider reports its monthly spend cap with the status and the type of a rate limit and no Retry-After header, so the error code in the
    /// response is the only way to tell the two apart. No retry, the SDK's own or the queue's, succeeds until the cap is raised or the month ends.
    /// </summary>
    public static bool IsAiSpendLimitReached(Exception exception)
    {
        return exception is AnthropicRateLimitException { ResponseBody: { } body }
            && body.Contains(SpendLimitErrorCode, StringComparison.Ordinal);
    }

    private static bool IsTransientStatus(int statusCode)
    {
        const int requestTimeout = 408;
        const int conflict = 409;
        const int tooManyRequests = 429;

        return statusCode is requestTimeout or conflict or tooManyRequests || statusCode >= 500;
    }
}
