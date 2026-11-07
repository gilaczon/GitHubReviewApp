namespace GitHubReviewApp.Services;

/// <summary>
/// A failed GitHub API call. Carries the status code and whether GitHub was rate limiting us, which is what
/// decides if the work should be retried (rate limits and 5xx) or abandoned (permissions, missing resources).
/// </summary>
public sealed class GitHubApiException : HttpRequestException
{
    private const int MaxBodyChars = 500;

    public GitHubApiException(string message, HttpStatusCode statusCode, bool isRateLimited)
        : base(message, null, statusCode)
    {
        IsRateLimited = isRateLimited;
    }

    public bool IsRateLimited { get; }

    public static async Task<GitHubApiException> FromResponseAsync(
        string operation,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await ReadBodySnippetAsync(response, cancellationToken);
        var message = $"GitHub could not {operation}: {(int)response.StatusCode} {response.ReasonPhrase}.";

        if (body.Length > 0)
        {
            message += " " + body;
        }

        return new GitHubApiException(message, response.StatusCode, IsRateLimitResponse(response) || IsRateLimitMessage(response.StatusCode, body));
    }

    private static bool IsRateLimitResponse(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return true;
        }

        // GitHub signals primary and secondary rate limits with 403 plus Retry-After or an empty quota.
        if (response.StatusCode != HttpStatusCode.Forbidden)
        {
            return false;
        }

        if (response.Headers.RetryAfter is not null)
        {
            return true;
        }

        return response.Headers.TryGetValues("X-RateLimit-Remaining", out var values)
            && values.Any(value => value.Trim() == "0");
    }

    /// <summary>
    /// A 403 is a permission problem or a rate limit, and GitHub does not always say which in a header: a secondary limit can arrive without
    /// Retry-After, and a quota header can read non-zero. Only 403 is ambiguous this way, so only 403 is judged by what the message says.
    /// </summary>
    private static bool IsRateLimitMessage(HttpStatusCode statusCode, string body)
    {
        return statusCode == HttpStatusCode.Forbidden
            && (body.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
                || body.Contains("abuse detection", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<string> ReadBodySnippetAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            body = body.ReplaceLineEndings(" ").Trim();

            return body.Length > MaxBodyChars ? body[..MaxBodyChars] + "…" : body;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The status code is the important part; a body we cannot read must not hide it.
            return string.Empty;
        }
    }
}
