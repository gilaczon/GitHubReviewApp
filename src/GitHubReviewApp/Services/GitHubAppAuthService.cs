namespace GitHubReviewApp.Services;

/// <summary>
/// Generates GitHub App JWTs and exchanges them for short-lived installation access tokens.
/// No external JWT library needed — GitHub App tokens use plain RS256.
/// </summary>
public class GitHubAppAuthService : IGitHubAppAuthService
{
    private const string ApiBaseUrl = "https://api.github.com";

    // Key Vault secret name for the GitHub App RSA private key (PEM format)
    private const string PrivateKeySecretName = "GitHubAppPrivateKey";

    // Above this many cached tokens, expired ones are dropped so the cache cannot grow without bound.
    private const int MaxCachedTokens = 128;

    // A review can run for several minutes and uses one token for its whole run, so a token is retired
    // well before it expires rather than handed out when it has seconds left.
    private static readonly TimeSpan _tokenRefreshMargin = TimeSpan.FromMinutes(10);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GitHubAppAuthService> _logger;

    // Cache installation tokens by installation and repository until the refresh margin before expiry
    private readonly ConcurrentDictionary<(long InstallationId, string Repository), CachedToken> _tokenCache = new();

    // The app's own login never changes while the process runs.
    private string? _appLogin;

    public GitHubAppAuthService(
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        TimeProvider timeProvider,
        ILogger<GitHubAppAuthService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    private record CachedToken(string Token, DateTimeOffset ExpiresAt, bool HasContentsAccess)
    {
        public bool IsValidAt(DateTimeOffset now) => now < ExpiresAt;
    }

    private class InstallationTokenResponse
    {
        [JsonPropertyName("token")]
        public string Token { get; init; } = string.Empty;

        [JsonPropertyName("expires_at")]
        public DateTimeOffset ExpiresAt { get; init; }
    }

    private class AppResponse
    {
        [JsonPropertyName("slug")]
        public string? Slug { get; init; }
    }

    internal int CachedTokenCount => _tokenCache.Count;

    public async Task<string> GetInstallationTokenAsync(long installationId, string repository, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);

        var cacheKey = (installationId, repository.ToLowerInvariant());

        if (_tokenCache.TryGetValue(cacheKey, out var cached) && cached.IsValidAt(_timeProvider.GetUtcNow()))
        {
            return cached.Token;
        }

        var jwt = GenerateAppJwt();
        var token = await ExchangeForInstallationTokenAsync(installationId, repository, jwt, cancellationToken);

        _tokenCache[cacheKey] = token;

        if (_tokenCache.Count > MaxCachedTokens)
        {
            PruneExpiredTokens();
        }

        return token.Token;
    }

    public bool CanReadContents(long installationId, string repository)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);

        // With no token cached there is nothing to say the permission is missing.
        return !_tokenCache.TryGetValue((installationId, repository.ToLowerInvariant()), out var cached) || cached.HasContentsAccess;
    }

    public void ForgetInstallationToken(long installationId, string repository)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);

        _tokenCache.TryRemove((installationId, repository.ToLowerInvariant()), out _);
    }

    public async Task<string> GetAppLoginAsync(CancellationToken cancellationToken)
    {
        if (_appLogin is { } known)
        {
            return known;
        }

        using var request = CreateAppRequest(HttpMethod.Get, $"{ApiBaseUrl}/app", GenerateAppJwt());
        using var response = await _httpClientFactory.CreateClient().SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw await GitHubApiException.FromResponseAsync("read the GitHub App", response, cancellationToken);
        }

        var app = await response.Content.ReadFromJsonAsync<AppResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Empty GitHub App response.");

        if (string.IsNullOrWhiteSpace(app.Slug))
        {
            throw new InvalidOperationException("GitHub did not return the app's slug, so its own reviews cannot be recognised.");
        }

        _appLogin = app.Slug + "[bot]";

        return _appLogin;
    }

    private static HttpRequestMessage CreateAppRequest(HttpMethod method, string url, string jwt)
    {
        var request = new HttpRequestMessage(method, url);

        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("GitHubReviewApp", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        return request;
    }

    private static string Base64UrlEncode(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private string GenerateAppJwt()
    {
        var appId = _config["GitHubAppId"]
            ?? throw new InvalidOperationException("GitHubAppId is not configured.");
        var pem = _config[PrivateKeySecretName]
            ?? throw new InvalidOperationException($"Secret '{PrivateKeySecretName}' is not configured.");

        var now = _timeProvider.GetUtcNow();
        var iat = now.AddSeconds(-60).ToUnixTimeSeconds();   // 60s clock skew buffer
        var exp = now.AddMinutes(9).ToUnixTimeSeconds();     // max 10 min

        var header = Base64UrlEncode("""{"alg":"RS256","typ":"JWT"}""");
        var payload = Base64UrlEncode($$$"""{"iat":{{{iat}}},"exp":{{{exp}}},"iss":"{{{appId}}}"}""");
        var message = $"{header}.{payload}";

        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);

        var signature = rsa.SignData(
            Encoding.ASCII.GetBytes(message),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        return $"{message}.{Convert.ToBase64String(signature).Replace('+', '-').Replace('/', '_').TrimEnd('=')}";
    }

    private async Task<CachedToken> ExchangeForInstallationTokenAsync(
        long installationId,
        string repository,
        string jwt,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Exchanging JWT for an access token for installation {InstallationId}, repository {Repository}.",
            installationId, repository);

        var response = await SendTokenRequestAsync(installationId, repository, jwt, hasContentsPermission: true, cancellationToken);
        var hasContentsAccess = true;

        // GitHub answers 422 when a requested permission was never granted to the installation, and also when the repository is not available
        // to it. Only the first is worth retrying without contents access: the review itself needs pull request access only.
        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            var rejection = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!rejection.Contains("permission", StringComparison.OrdinalIgnoreCase))
            {
                using (response)
                {
                    throw await GitHubApiException.FromResponseAsync("create an installation access token", response, cancellationToken);
                }
            }

            response.Dispose();

            _logger.LogWarning(
                "Installation {InstallationId} has not been granted the contents permission ({Reason}). " +
                "Requesting a token without it; repository review guidelines will not be read.",
                installationId, rejection.ReplaceLineEndings(" ").Trim());

            response = await SendTokenRequestAsync(installationId, repository, jwt, hasContentsPermission: false, cancellationToken);
            hasContentsAccess = false;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw await GitHubApiException.FromResponseAsync("create an installation access token", response, cancellationToken);
            }

            var body = await response.Content.ReadFromJsonAsync<InstallationTokenResponse>(cancellationToken)
                ?? throw new InvalidOperationException("Empty installation token response.");

            return new CachedToken(body.Token, body.ExpiresAt - _tokenRefreshMargin, hasContentsAccess);
        }
    }

    private async Task<HttpResponseMessage> SendTokenRequestAsync(
        long installationId,
        string repository,
        string jwt,
        bool hasContentsPermission,
        CancellationToken cancellationToken)
    {
        var permissions = new Dictionary<string, string> { ["pull_requests"] = "write" };

        if (hasContentsPermission)
        {
            permissions["contents"] = "read";
        }

        var requestBody = JsonSerializer.Serialize(new { repositories = new[] { repository }, permissions });

        using var request = CreateAppRequest(HttpMethod.Post, $"{ApiBaseUrl}/app/installations/{installationId}/access_tokens", jwt);
        request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

        return await _httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
    }

    private void PruneExpiredTokens()
    {
        var now = _timeProvider.GetUtcNow();

        foreach (var entry in _tokenCache)
        {
            if (!entry.Value.IsValidAt(now))
            {
                _tokenCache.TryRemove(entry.Key, out _);
            }
        }
    }
}
