namespace GitHubReviewApp.Services;

/// <summary>
/// Generates GitHub App JWTs and exchanges them for short-lived installation access tokens.
/// No external JWT library needed — GitHub App tokens use plain RS256.
/// </summary>
public class GitHubAppAuthService : IGitHubAppAuthService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GitHubAppAuthService> _logger;

    // Cache installation tokens by installation ID until 1 min before expiry
    private readonly ConcurrentDictionary<long, CachedToken> _tokenCache = new();

    // Key Vault secret name for the GitHub App RSA private key (PEM format)
    private const string PrivateKeySecretName = "GitHubAppPrivateKey";

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

    public async Task<string> GetInstallationTokenAsync(long installationId)
    {
        if (_tokenCache.TryGetValue(installationId, out var cached) && cached.IsValidAt(_timeProvider.GetUtcNow()))
            return cached.Token;

        var jwt = GenerateAppJwt();
        var token = await ExchangeForInstallationTokenAsync(installationId, jwt);

        _tokenCache[installationId] = token;
        return token.Token;
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

    private async Task<CachedToken> ExchangeForInstallationTokenAsync(long installationId, string jwt)
    {
        _logger.LogInformation("Exchanging JWT for installation {InstallationId} access token.", installationId);

        var client = _httpClientFactory.CreateClient("github");
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://api.github.com/app/installations/{installationId}/access_tokens");

        request.Headers.Add("Authorization", $"Bearer {jwt}");

        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<InstallationTokenResponse>()
            ?? throw new InvalidOperationException("Empty installation token response.");

        return new CachedToken(body.Token, body.ExpiresAt.AddMinutes(-1));
    }

    private static string Base64UrlEncode(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private record CachedToken(string Token, DateTimeOffset ExpiresAt)
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
}
