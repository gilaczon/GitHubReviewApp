namespace GitHubReviewApp.Tests;

public class GitHubAppAuthServiceTests
{
    private const long TestInstallationId = 12345678L;
    private const string TestAppId        = "123456";

    // ── GetInstallationTokenAsync — JWT generation and token exchange ─────────

    [Fact]
    public async Task GetInstallationTokenAsync_GivenNoCache_Should_SendPostToCorrectGitHubEndpoint()
    {
        // Arrange
        var handler = new TestHttpMessageHandler(req =>
            BuildInstallationTokenResponse("ghs_new_token", DateTimeOffset.UtcNow.AddHours(1)));
        var sut = BuildSut(handler);

        // Act
        await sut.GetInstallationTokenAsync(TestInstallationId);

        // Assert
        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.RequestUri!.PathAndQuery
            .Should().Be($"/app/installations/{TestInstallationId}/access_tokens");
    }

    [Fact]
    public async Task GetInstallationTokenAsync_GivenNoCache_Should_ReturnTokenFromGitHubResponse()
    {
        // Arrange
        const string ExpectedToken = "ghs_returned_access_token";
        var handler = new TestHttpMessageHandler(req =>
            BuildInstallationTokenResponse(ExpectedToken, DateTimeOffset.UtcNow.AddHours(1)));
        var sut = BuildSut(handler);

        // Act
        var result = await sut.GetInstallationTokenAsync(TestInstallationId);

        // Assert
        result.Should().Be(ExpectedToken);
    }

    [Fact]
    public async Task GetInstallationTokenAsync_GivenNoCache_Should_SendAuthorizationBearerJwtHeader()
    {
        // Arrange
        string? capturedAuthHeader = null;
        var handler = new TestHttpMessageHandler(req =>
        {
            capturedAuthHeader = req.Headers.Authorization?.Parameter;
            return BuildInstallationTokenResponse("token", DateTimeOffset.UtcNow.AddHours(1));
        });
        var sut = BuildSut(handler);

        // Act
        await sut.GetInstallationTokenAsync(TestInstallationId);

        // Assert — a valid JWT has exactly 3 Base64URL-encoded parts separated by dots
        capturedAuthHeader.Should().NotBeNull();
        var parts = capturedAuthHeader!.Split('.');
        parts.Should().HaveCount(3, because: "a RS256 JWT consists of header.payload.signature");
        parts.Should().AllSatisfy(part =>
            part.Should().MatchRegex(@"^[A-Za-z0-9\-_]+$",
                because: "each JWT part must be Base64URL-encoded (no +, /, or = padding)"));
    }

    [Fact]
    public async Task GetInstallationTokenAsync_GivenValidCachedToken_Should_NotHitGitHubApiSecondTime()
    {
        // Arrange
        var requestCount = 0;
        var handler = new TestHttpMessageHandler(req =>
        {
            requestCount++;
            return BuildInstallationTokenResponse("ghs_cached_token", DateTimeOffset.UtcNow.AddHours(1));
        });
        var sut = BuildSut(handler);

        // Act
        await sut.GetInstallationTokenAsync(TestInstallationId);
        await sut.GetInstallationTokenAsync(TestInstallationId);

        // Assert
        requestCount.Should().Be(1, because: "the second call should be served from the token cache");
    }

    [Fact]
    public async Task GetInstallationTokenAsync_GivenValidCachedToken_Should_ReturnSameToken()
    {
        // Arrange
        var handler = new TestHttpMessageHandler(req =>
            BuildInstallationTokenResponse("ghs_stable_token", DateTimeOffset.UtcNow.AddHours(1)));
        var sut = BuildSut(handler);

        // Act
        var first  = await sut.GetInstallationTokenAsync(TestInstallationId);
        var second = await sut.GetInstallationTokenAsync(TestInstallationId);

        // Assert
        second.Should().Be(first);
    }

    // ── Token expiry (TimeProvider injection) ────────────────────────────────

    [Fact]
    public async Task GetInstallationTokenAsync_GivenExpiredCachedToken_Should_FetchNewToken()
    {
        // Arrange — FakeTimeProvider lets us control the clock without real sleeps
        var fakeTime = new FakeTimeProvider();
        var callCount = 0;

        var handler = new TestHttpMessageHandler(req =>
        {
            callCount++;
            // Token expires 1 hour from the fake clock's current time.
            // The service stores ExpiresAt - 1 min, so effective validity = 59 min.
            return BuildInstallationTokenResponse(
                $"token-{callCount}",
                fakeTime.GetUtcNow().AddHours(1));
        });
        var sut = BuildSut(handler, fakeTime);

        // First call — populates cache with token valid until fake now + ~59 min
        await sut.GetInstallationTokenAsync(TestInstallationId);

        // Advance clock past the stored expiry (>60 min makes IsValidAt return false)
        fakeTime.Advance(TimeSpan.FromHours(2));

        // Second call — cache is now stale, must fetch a fresh token
        await sut.GetInstallationTokenAsync(TestInstallationId);

        // Assert
        callCount.Should().Be(2, because: "the expired cached token should trigger a new fetch");
    }

    [Fact]
    public async Task GetInstallationTokenAsync_GivenTwoDifferentInstallations_Should_FetchTokenForEach()
    {
        // Arrange
        const long OtherInstallationId = 99999999L;
        var requestedPaths = new List<string>();

        var handler = new TestHttpMessageHandler(req =>
        {
            requestedPaths.Add(req.RequestUri!.PathAndQuery);
            return BuildInstallationTokenResponse(
                $"token-{requestedPaths.Count}", DateTimeOffset.UtcNow.AddHours(1));
        });
        var sut = BuildSut(handler);

        // Act
        await sut.GetInstallationTokenAsync(TestInstallationId);
        await sut.GetInstallationTokenAsync(OtherInstallationId);

        // Assert
        requestedPaths.Should().HaveCount(2);
        requestedPaths.Should().Contain($"/app/installations/{TestInstallationId}/access_tokens");
        requestedPaths.Should().Contain($"/app/installations/{OtherInstallationId}/access_tokens");
    }

    [Fact]
    public async Task GetInstallationTokenAsync_GivenGitHubApiReturnsError_Should_ThrowHttpRequestException()
    {
        // Arrange
        var handler = new TestHttpMessageHandler(req =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var sut = BuildSut(handler);

        // Act
        var act = () => sut.GetInstallationTokenAsync(TestInstallationId);

        // Assert
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    // ── JWT structure verification ───────────────────────────────────────────

    [Fact]
    public async Task GetInstallationTokenAsync_GivenValidRsaKey_Should_GenerateJwtWithRs256Header()
    {
        // Arrange
        string? capturedJwt = null;
        var handler = new TestHttpMessageHandler(req =>
        {
            capturedJwt = req.Headers.Authorization?.Parameter;
            return BuildInstallationTokenResponse("token", DateTimeOffset.UtcNow.AddHours(1));
        });
        var sut = BuildSut(handler);

        // Act
        await sut.GetInstallationTokenAsync(TestInstallationId);

        // Assert — decode the JWT header and verify RS256 algorithm
        capturedJwt.Should().NotBeNull();
        var headerJson = Encoding.UTF8.GetString(Base64UrlDecode(capturedJwt!.Split('.')[0]));
        var header = JsonDocument.Parse(headerJson).RootElement;
        header.GetProperty("alg").GetString().Should().Be("RS256");
        header.GetProperty("typ").GetString().Should().Be("JWT");
    }

    [Fact]
    public async Task GetInstallationTokenAsync_GivenValidRsaKey_Should_EmbedAppIdAsIssuerInJwtPayload()
    {
        // Arrange
        string? capturedJwt = null;
        var handler = new TestHttpMessageHandler(req =>
        {
            capturedJwt = req.Headers.Authorization?.Parameter;
            return BuildInstallationTokenResponse("token", DateTimeOffset.UtcNow.AddHours(1));
        });
        var sut = BuildSut(handler);

        // Act
        await sut.GetInstallationTokenAsync(TestInstallationId);

        // Assert — decode the JWT payload and verify the issuer claim
        capturedJwt.Should().NotBeNull();
        var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(capturedJwt!.Split('.')[1]));
        var payload = JsonDocument.Parse(payloadJson).RootElement;
        payload.GetProperty("iss").GetString().Should().Be(TestAppId);
    }

    // ── Factory helpers ──────────────────────────────────────────────────────

    private static GitHubAppAuthService BuildSut(
        TestHttpMessageHandler handler,
        TimeProvider? timeProvider = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GitHubAppId"]            = TestAppId,
                ["GitHubAppPrivateKey"] = RsaKeyHelper.PrivateKeyPem
            })
            .Build();

        return new GitHubAppAuthService(
            new SingleClientFactory(new HttpClient(handler)),
            config,
            timeProvider ?? TimeProvider.System,
            NullLogger<GitHubAppAuthService>.Instance);
    }

    private static HttpResponseMessage BuildInstallationTokenResponse(
        string token, DateTimeOffset expiresAt)
    {
        var json = JsonSerializer.Serialize(new { token, expires_at = expiresAt });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static byte[] Base64UrlDecode(string base64Url)
    {
        var padded = base64Url.Replace('-', '+').Replace('_', '/');
        var padding = (4 - padded.Length % 4) % 4;
        padded += new string('=', padding);
        return Convert.FromBase64String(padded);
    }
}

// ── SingleClientFactory ──────────────────────────────────────────────────────

internal sealed class SingleClientFactory : IHttpClientFactory
{
    private readonly HttpClient _client;
    public SingleClientFactory(HttpClient client) => _client = client;
    public HttpClient CreateClient(string name) => _client;
}
