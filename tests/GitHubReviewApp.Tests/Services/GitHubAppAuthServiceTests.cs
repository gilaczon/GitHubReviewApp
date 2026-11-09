namespace GitHubReviewApp.Tests.Services;

public class GitHubAppAuthServiceTests
{
    private const long TestInstallationId = 12345678L;
    private const string TestAppId        = "123456";
    private const string TestRepository   = "my-service";

    // The clock every test starts from unless it supplies its own, so no test depends on the time of day it runs at.
    private static readonly DateTimeOffset _startOfTime = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset InAnHour => _startOfTime.AddHours(1);

    // ── GetInstallationTokenAsync — JWT generation and token exchange ─────────

    [Fact]
    public async Task GetInstallationTokenAsync_GivenNoCache_Should_SendPostToCorrectGitHubEndpoint()
    {
        // Arrange
        var handler = new TestHttpMessageHandler(req =>
            BuildInstallationTokenResponse("ghs_new_token", InAnHour));
        var sut = BuildSut(handler);

        // Act
        await sut.GetInstallationTokenAsync(TestInstallationId, TestRepository, CancellationToken.None);

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
            BuildInstallationTokenResponse(ExpectedToken, InAnHour));
        var sut = BuildSut(handler);

        // Act
        var result = await sut.GetInstallationTokenAsync(TestInstallationId, TestRepository, CancellationToken.None);

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
            return BuildInstallationTokenResponse("token", InAnHour);
        });
        var sut = BuildSut(handler);

        // Act
        await sut.GetInstallationTokenAsync(TestInstallationId, TestRepository, CancellationToken.None);

        // Assert — a valid JWT has exactly 3 Base64URL-encoded parts separated by dots
        capturedAuthHeader.Should().NotBeNull();
        var parts = capturedAuthHeader!.Split('.');
        parts.Should().HaveCount(3, because: "a RS256 JWT consists of header.payload.signature");
        parts.Should().AllSatisfy(part =>
            part.Should().MatchRegex(@"^[A-Za-z0-9\-_]+$",
                because: "each JWT part must be Base64URL-encoded (no +, /, or = padding)"));
    }

    [Fact]
    public async Task GetInstallationTokenAsync_GivenNoCache_Should_SendTheGitHubApiVersionHeader()
    {
        // Arrange
        var handler = new TestHttpMessageHandler(req =>
            BuildInstallationTokenResponse("token", InAnHour));
        var sut = BuildSut(handler);

        // Act
        await sut.GetInstallationTokenAsync(TestInstallationId, TestRepository, CancellationToken.None);

        // Assert
        handler.LastRequest!.Headers.GetValues("X-GitHub-Api-Version").Should().ContainSingle().Which.Should().Be("2022-11-28");
    }

    // ── Scoping ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetInstallationTokenAsync_GivenEveryPermissionGranted_Should_RequestPullRequestsAndChecksOnly()
    {
        // Arrange
        string? capturedBody = null;
        var handler = new TestHttpMessageHandler(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return BuildInstallationTokenResponse("token", InAnHour);
        });
        var sut = BuildSut(handler);

        // Act
        await sut.GetInstallationTokenAsync(TestInstallationId, TestRepository, CancellationToken.None);

        // Assert
        var body = JsonDocument.Parse(capturedBody!).RootElement;
        body.GetProperty("repositories").EnumerateArray().Select(e => e.GetString()).Should().Equal(TestRepository);
        var permissions = body.GetProperty("permissions");
        permissions.GetProperty("pull_requests").GetString().Should().Be("write");
        permissions.GetProperty("checks").GetString().Should().Be("write");
        permissions.EnumerateObject().Should().HaveCount(2, "nothing beyond what the review needs may be requested, and it reads no repository contents");
    }

    [Fact]
    public async Task GetInstallationTokenAsync_GivenUngrantedPermissions_Should_RetryWithPullRequestsOnlyAndReportNoChecks()
    {
        // Arrange — GitHub answers 422 when a requested permission was never granted to the installation
        var bodies = new List<string>();
        var handler = new TestHttpMessageHandler(async req =>
        {
            var body = await req.Content!.ReadAsStringAsync();
            bodies.Add(body);

            return body.Contains("\"checks\"", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
                {
                    Content = new StringContent("{\"message\":\"The permissions requested are not granted to this installation.\"}")
                }
                : BuildInstallationTokenResponse("ghs_pull_requests_only", InAnHour);
        });
        var sut = BuildSut(handler);

        // Act
        var token = await sut.GetInstallationTokenAsync(TestInstallationId, TestRepository, CancellationToken.None);

        // Assert
        token.Should().Be("ghs_pull_requests_only");
        bodies.Should().HaveCount(2);
        var retryPermissions = JsonDocument.Parse(bodies[1]).RootElement.GetProperty("permissions");
        retryPermissions.GetProperty("pull_requests").GetString().Should().Be("write");
        retryPermissions.EnumerateObject().Should().ContainSingle("the retry asks for pull requests alone");
        sut.CanWriteChecks(TestInstallationId, TestRepository).Should().BeFalse();
    }

    [Fact]
    public async Task GetInstallationTokenAsync_GivenBothAttemptsAreRejected_Should_ThrowGitHubApiExceptionWith422()
    {
        // Arrange
        var handler = new TestHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
        {
            Content = new StringContent("{\"message\":\"The permissions requested are not granted to this installation.\"}")
        });
        var sut = BuildSut(handler);

        // Act
        var act = () => sut.GetInstallationTokenAsync(TestInstallationId, TestRepository, CancellationToken.None);

        // Assert
        var exception = (await act.Should().ThrowAsync<GitHubApiException>()).Which;
        exception.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        handler.Requests.Should().HaveCount(2, "exactly one retry, never a loop");
    }

    [Theory]
    [InlineData("{\"message\":\"There is at least one repository that does not exist or is not accessible to the parent installation.\"}")]
    [InlineData("")]
    public async Task GetInstallationTokenAsync_GivenA422ThatIsNotAboutPermissions_Should_ThrowWithoutRetryingBecauseDroppingChecksCannotHelp(string body)
    {
        // Arrange — GitHub also answers 422 for a repository the installation cannot reach
        var handler = new TestHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.UnprocessableEntity) { Content = new StringContent(body) });
        var sut = BuildSut(handler);

        // Act
        var act = () => sut.GetInstallationTokenAsync(TestInstallationId, TestRepository, CancellationToken.None);

        // Assert
        (await act.Should().ThrowAsync<GitHubApiException>()).Which.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task CanWriteChecks_GivenATokenIssuedWithChecks_Should_BeTrue()
    {
        // Arrange
        var sut = BuildSut(new TestHttpMessageHandler(req => BuildInstallationTokenResponse("ghs_full", InAnHour)));
        await sut.GetInstallationTokenAsync(TestInstallationId, TestRepository, CancellationToken.None);

        // Act
        var result = sut.CanWriteChecks(TestInstallationId, TestRepository);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task CanWriteChecks_GivenATokenIssuedWithoutChecks_Should_BeFalse()
    {
        // Arrange
        var handler = new TestHttpMessageHandler(async req =>
        {
            var body = await req.Content!.ReadAsStringAsync();

            return body.Contains("\"checks\"", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
                {
                    Content = new StringContent("{\"message\":\"The permissions requested are not granted to this installation.\"}")
                }
                : BuildInstallationTokenResponse("ghs_without_checks", InAnHour);
        });
        var sut = BuildSut(handler);
        await sut.GetInstallationTokenAsync(TestInstallationId, TestRepository, CancellationToken.None);

        // Act
        var result = sut.CanWriteChecks(TestInstallationId, TestRepository);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void CanWriteChecks_GivenNoTokenWasFetchedYet_Should_BeTrueBecauseNothingSaysOtherwise()
    {
        // Arrange
        var sut = BuildSut(new TestHttpMessageHandler("{}"));

        // Act
        var result = sut.CanWriteChecks(TestInstallationId, TestRepository);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task GetInstallationTokenAsync_GivenTwoRepositoriesOfTheSameInstallation_Should_FetchATokenForEach()
    {
        // Arrange
        var bodies = new List<string>();
        var handler = new TestHttpMessageHandler(async req =>
        {
            bodies.Add(await req.Content!.ReadAsStringAsync());
            return BuildInstallationTokenResponse($"token-{bodies.Count}", InAnHour);
        });
        var sut = BuildSut(handler);

        // Act
        var first = await sut.GetInstallationTokenAsync(TestInstallationId, "repo-one", CancellationToken.None);
        var second = await sut.GetInstallationTokenAsync(TestInstallationId, "repo-two", CancellationToken.None);

        // Assert — a repository-scoped token must never be reused for another repository
        first.Should().NotBe(second);
        bodies.Should().HaveCount(2);
        bodies[0].Should().Contain("repo-one");
        bodies[1].Should().Contain("repo-two");
    }

    [Fact]
    public async Task GetInstallationTokenAsync_GivenTheSameRepositoryInADifferentCase_Should_ShareOneCachedToken()
    {
        // Arrange
        var requestCount = 0;
        var handler = new TestHttpMessageHandler(req =>
        {
            requestCount++;
            return BuildInstallationTokenResponse("ghs_token", InAnHour);
        });
        var sut = BuildSut(handler);

        // Act
        await sut.GetInstallationTokenAsync(TestInstallationId, "My-Service", CancellationToken.None);
        await sut.GetInstallationTokenAsync(TestInstallationId, "my-service", CancellationToken.None);

        // Assert
        requestCount.Should().Be(1);
    }

    // ── Caching ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetInstallationTokenAsync_GivenValidCachedToken_Should_NotHitGitHubApiSecondTime()
    {
        // Arrange
        var requestCount = 0;
        var handler = new TestHttpMessageHandler(req =>
        {
            requestCount++;
            return BuildInstallationTokenResponse("ghs_cached_token", InAnHour);
        });
        var sut = BuildSut(handler);

        // Act
        await sut.GetInstallationTokenAsync(TestInstallationId, TestRepository, CancellationToken.None);
        await sut.GetInstallationTokenAsync(TestInstallationId, TestRepository, CancellationToken.None);

        // Assert
        requestCount.Should().Be(1, because: "the second call should be served from the token cache");
    }

    [Fact]
    public async Task GetInstallationTokenAsync_GivenValidCachedToken_Should_ReturnSameToken()
    {
        // Arrange
        var handler = new TestHttpMessageHandler(req =>
            BuildInstallationTokenResponse("ghs_stable_token", InAnHour));
        var sut = BuildSut(handler);

        // Act
        var first  = await sut.GetInstallationTokenAsync(TestInstallationId, TestRepository, CancellationToken.None);
        var second = await sut.GetInstallationTokenAsync(TestInstallationId, TestRepository, CancellationToken.None);

        // Assert
        second.Should().Be(first);
    }

    [Fact]
    public async Task ForgetInstallationToken_GivenACachedToken_Should_MakeTheNextCallAskGitHubForANewOne()
    {
        // Arrange — GitHub said 401, so the cached copy is useless however long it has left
        var requestCount = 0;
        var handler = new TestHttpMessageHandler(req =>
            BuildInstallationTokenResponse($"token-{++requestCount}", InAnHour));
        var sut = BuildSut(handler);
        await sut.GetInstallationTokenAsync(TestInstallationId, TestRepository, CancellationToken.None);

        // Act
        sut.ForgetInstallationToken(TestInstallationId, TestRepository);
        var token = await sut.GetInstallationTokenAsync(TestInstallationId, TestRepository, CancellationToken.None);

        // Assert
        token.Should().Be("token-2");
        requestCount.Should().Be(2);
    }

    [Fact]
    public async Task ForgetInstallationToken_GivenTheRepositoryIsWrittenInAnotherCase_Should_StillForgetIt()
    {
        // Arrange — the cache is keyed case-insensitively, as GitHub treats repository names
        var requestCount = 0;
        var handler = new TestHttpMessageHandler(req =>
            BuildInstallationTokenResponse($"token-{++requestCount}", InAnHour));
        var sut = BuildSut(handler);
        await sut.GetInstallationTokenAsync(TestInstallationId, TestRepository, CancellationToken.None);

        // Act
        sut.ForgetInstallationToken(TestInstallationId, TestRepository.ToUpperInvariant());
        await sut.GetInstallationTokenAsync(TestInstallationId, TestRepository, CancellationToken.None);

        // Assert
        requestCount.Should().Be(2);
    }

    [Fact]
    public async Task ForgetInstallationToken_GivenAnotherRepositorysToken_Should_LeaveItCached()
    {
        // Arrange
        var requestCount = 0;
        var handler = new TestHttpMessageHandler(req =>
            BuildInstallationTokenResponse($"token-{++requestCount}", InAnHour));
        var sut = BuildSut(handler);
        await sut.GetInstallationTokenAsync(TestInstallationId, "other-service", CancellationToken.None);

        // Act
        sut.ForgetInstallationToken(TestInstallationId, TestRepository);
        await sut.GetInstallationTokenAsync(TestInstallationId, "other-service", CancellationToken.None);

        // Assert
        requestCount.Should().Be(1, "only the repository whose token GitHub refused is affected");
    }

    [Fact]
    public void ForgetInstallationToken_GivenNothingCached_Should_NotThrow()
    {
        // Arrange
        var sut = BuildSut(new TestHttpMessageHandler(req => BuildInstallationTokenResponse("token", InAnHour)));

        // Act
        var act = () => sut.ForgetInstallationToken(TestInstallationId, TestRepository);

        // Assert
        act.Should().NotThrow();
    }

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
            // The service stores ExpiresAt - 10 min, so effective validity = 50 min.
            return BuildInstallationTokenResponse(
                $"token-{callCount}",
                fakeTime.GetUtcNow().AddHours(1));
        });
        var sut = BuildSut(handler, fakeTime);

        // Act
        // First call — populates cache with token valid until fake now + ~59 min
        await sut.GetInstallationTokenAsync(TestInstallationId, TestRepository, CancellationToken.None);

        // Advance clock past the stored expiry (>60 min makes IsValidAt return false)
        fakeTime.Advance(TimeSpan.FromHours(2));

        // Second call — cache is now stale, must fetch a fresh token
        await sut.GetInstallationTokenAsync(TestInstallationId, TestRepository, CancellationToken.None);

        // Assert
        callCount.Should().Be(2, because: "the expired cached token should trigger a new fetch");
    }

    [Theory]
    [InlineData(52, 2)]   // 8 minutes left: inside the refresh margin, so a fresh token is fetched
    [InlineData(45, 1)]   // 15 minutes left: still comfortably valid, so the cache is used
    public async Task GetInstallationTokenAsync_GivenACachedTokenNearExpiry_Should_RefreshItBeforeALongReviewCouldOutliveIt(int minutesLater, int expectedRequests)
    {
        // Arrange — one review can run for several minutes and uses the same token throughout, so a token
        // that is about to expire must not be handed out
        var fakeTime = new FakeTimeProvider();
        var requestCount = 0;
        var handler = new TestHttpMessageHandler(req =>
        {
            requestCount++;
            return BuildInstallationTokenResponse($"token-{requestCount}", fakeTime.GetUtcNow().AddHours(1));
        });
        var sut = BuildSut(handler, fakeTime);
        await sut.GetInstallationTokenAsync(TestInstallationId, TestRepository, CancellationToken.None);
        fakeTime.Advance(TimeSpan.FromMinutes(minutesLater));

        // Act
        await sut.GetInstallationTokenAsync(TestInstallationId, TestRepository, CancellationToken.None);

        // Assert
        requestCount.Should().Be(expectedRequests);
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
                $"token-{requestedPaths.Count}", InAnHour);
        });
        var sut = BuildSut(handler);

        // Act
        await sut.GetInstallationTokenAsync(TestInstallationId, TestRepository, CancellationToken.None);
        await sut.GetInstallationTokenAsync(OtherInstallationId, TestRepository, CancellationToken.None);

        // Assert
        requestedPaths.Should().HaveCount(2);
        requestedPaths.Should().Contain($"/app/installations/{TestInstallationId}/access_tokens");
        requestedPaths.Should().Contain($"/app/installations/{OtherInstallationId}/access_tokens");
    }

    [Fact]
    public async Task GetInstallationTokenAsync_GivenManyExpiredTokens_Should_PruneThemSoTheCacheDoesNotGrowForever()
    {
        // Arrange
        var fakeTime = new FakeTimeProvider();
        var handler = new TestHttpMessageHandler(req =>
            BuildInstallationTokenResponse("token", fakeTime.GetUtcNow().AddHours(1)));
        var sut = BuildSut(handler, fakeTime);

        await CacheTokensForRepositoriesAsync(sut, count: 150);

        sut.CachedTokenCount.Should().Be(150, "nothing has expired yet");
        fakeTime.Advance(TimeSpan.FromHours(2));

        // Act
        await sut.GetInstallationTokenAsync(TestInstallationId, "fresh-repo", CancellationToken.None);

        // Assert
        sut.CachedTokenCount.Should().Be(1, "every expired entry is dropped when the cache is over its size threshold");
    }

    // ── Failures ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetInstallationTokenAsync_GivenGitHubApiReturnsError_Should_ThrowGitHubApiExceptionCarryingTheStatusCode()
    {
        // Arrange
        var handler = new TestHttpMessageHandler(req =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var sut = BuildSut(handler);

        // Act
        var act = () => sut.GetInstallationTokenAsync(TestInstallationId, TestRepository, CancellationToken.None);

        // Assert
        var exception = (await act.Should().ThrowAsync<GitHubApiException>()).Which;
        exception.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetInstallationTokenAsync_GivenACancelledToken_Should_ThrowOperationCanceled()
    {
        // Arrange
        var handler = new TestHttpMessageHandler(req =>
            BuildInstallationTokenResponse("token", InAnHour));
        var sut = BuildSut(handler);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // Act
        var act = () => sut.GetInstallationTokenAsync(TestInstallationId, TestRepository, cancellation.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetInstallationTokenAsync_GivenNoAppIdConfigured_Should_ThrowInvalidOperationException()
    {
        // Arrange
        var sut = BuildSut(new TestHttpMessageHandler("{}"), appId: null);

        // Act
        var act = () => sut.GetInstallationTokenAsync(TestInstallationId, TestRepository, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*GitHubAppId*");
    }

    [Fact]
    public async Task GetInstallationTokenAsync_GivenNoPrivateKeyConfigured_Should_ThrowInvalidOperationException()
    {
        // Arrange
        var sut = BuildSut(new TestHttpMessageHandler("{}"), privateKey: null);

        // Act
        var act = () => sut.GetInstallationTokenAsync(TestInstallationId, TestRepository, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*GitHubReviewAppPrivateKey*");
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
            return BuildInstallationTokenResponse("token", InAnHour);
        });
        var sut = BuildSut(handler);

        // Act
        await sut.GetInstallationTokenAsync(TestInstallationId, TestRepository, CancellationToken.None);

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
            return BuildInstallationTokenResponse("token", InAnHour);
        });
        var sut = BuildSut(handler);

        // Act
        await sut.GetInstallationTokenAsync(TestInstallationId, TestRepository, CancellationToken.None);

        // Assert — decode the JWT payload and verify the issuer claim
        capturedJwt.Should().NotBeNull();
        var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(capturedJwt!.Split('.')[1]));
        var payload = JsonDocument.Parse(payloadJson).RootElement;
        payload.GetProperty("iss").GetString().Should().Be(TestAppId);
    }

    [Fact]
    public async Task GetInstallationTokenAsync_GivenTheTestKey_Should_SignTheJwtSoThePublicKeyVerifiesIt()
    {
        // Arrange
        string? capturedJwt = null;
        var handler = new TestHttpMessageHandler(req =>
        {
            capturedJwt = req.Headers.Authorization?.Parameter;
            return BuildInstallationTokenResponse("token", InAnHour);
        });
        var sut = BuildSut(handler);

        // Act
        await sut.GetInstallationTokenAsync(TestInstallationId, TestRepository, CancellationToken.None);

        // Assert — a wrong hash or padding would still produce three well-formed parts, and GitHub would answer 401 to the first real call
        var jwt = GitHubAppJwt.Parse(capturedJwt!, RsaKeyHelper.PublicKey);
        jwt.Should().NotBeNull();
        jwt!.IsSignatureValid.Should().BeTrue("the signature must verify with RS256, PKCS#1 v1.5 and SHA-256");
    }

    [Fact]
    public async Task GetInstallationTokenAsync_GivenAFixedClock_Should_IssueTheJwtNoLaterThanNowAndExpireItWithinTenMinutes()
    {
        // Arrange — GitHub rejects an iat in the future and an exp more than ten minutes away
        var now = new DateTimeOffset(2026, 11, 4, 9, 30, 0, TimeSpan.Zero);
        string? capturedJwt = null;
        var handler = new TestHttpMessageHandler(req =>
        {
            capturedJwt = req.Headers.Authorization?.Parameter;
            return BuildInstallationTokenResponse("token", now.AddHours(1));
        });
        var sut = BuildSut(handler, new FakeTimeProvider(now));

        // Act
        await sut.GetInstallationTokenAsync(TestInstallationId, TestRepository, CancellationToken.None);

        // Assert
        var jwt = GitHubAppJwt.Parse(capturedJwt!, RsaKeyHelper.PublicKey)!;
        jwt.IssuedAt.Should().BeOnOrBefore(now);
        jwt.ExpiresAt.Should().BeAfter(now).And.BeOnOrBefore(now.AddMinutes(10));
        jwt.Problem(TestAppId, now).Should().BeNull();
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task GetInstallationTokenAsync_GivenAPkcs1KeyAsGitHubIssuesIt_Should_SignTheJwtWithIt(string lineEnding)
    {
        // Arrange — the .pem file from the app's settings page is PKCS#1 ("RSA PRIVATE KEY"), unlike the PKCS#8 key used elsewhere in these tests
        var (pem, publicKey) = RsaKeyHelper.CreatePkcs1KeyPair(lineEnding);
        string? capturedJwt = null;
        var handler = new TestHttpMessageHandler(req =>
        {
            capturedJwt = req.Headers.Authorization?.Parameter;
            return BuildInstallationTokenResponse("token", InAnHour);
        });
        var sut = BuildSut(handler, privateKey: pem);

        // Act
        await sut.GetInstallationTokenAsync(TestInstallationId, TestRepository, CancellationToken.None);

        // Assert
        pem.Should().Contain("RSA PRIVATE KEY");
        GitHubAppJwt.Parse(capturedJwt!, publicKey)!.IsSignatureValid.Should().BeTrue();
        publicKey.Dispose();
    }

    // ── GetAppLoginAsync ─────────────────────────────────────────────────────

    private static HttpResponseMessage AppResponse(string slug = "my-reviewer")
    {
        var json = JsonSerializer.Serialize(new { id = 123456, slug, name = "My Reviewer" });
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    [Fact]
    public async Task GetAppLoginAsync_GivenTheAppExists_Should_ReturnTheSlugWithTheBotSuffix()
    {
        // Arrange
        var handler = new TestHttpMessageHandler(req => AppResponse("my-reviewer"));
        var sut = BuildSut(handler);

        // Act
        var login = await sut.GetAppLoginAsync(CancellationToken.None);

        // Assert
        login.Should().Be("my-reviewer[bot]");
    }

    [Fact]
    public async Task GetAppLoginAsync_GivenNoCache_Should_AskGitHubForTheAppAuthenticatedWithAVerifiableJwt()
    {
        // Arrange
        string? capturedJwt = null;
        var handler = new TestHttpMessageHandler(req =>
        {
            capturedJwt = req.Headers.Authorization?.Parameter;
            return AppResponse();
        });
        var time = new FakeTimeProvider(_startOfTime);
        var sut = BuildSut(handler, time);

        // Act
        await sut.GetAppLoginAsync(CancellationToken.None);

        // Assert
        handler.LastRequest!.Method.Should().Be(HttpMethod.Get);
        handler.LastRequest.RequestUri!.ToString().Should().Be("https://api.github.com/app");
        handler.LastRequest.Headers.UserAgent.ToString().Should().StartWith("GitHubReviewApp/");
        handler.LastRequest.Headers.GetValues("X-GitHub-Api-Version").Should().Equal("2022-11-28");
        GitHubAppJwt.Parse(capturedJwt!, RsaKeyHelper.PublicKey)!.Problem(TestAppId, time.GetUtcNow()).Should().BeNull();
    }

    [Fact]
    public async Task GetAppLoginAsync_GivenASecondCall_Should_NotAskGitHubAgain()
    {
        // Arrange
        var handler = new TestHttpMessageHandler(req => AppResponse());
        var sut = BuildSut(handler);

        // Act
        await sut.GetAppLoginAsync(CancellationToken.None);
        await sut.GetAppLoginAsync(CancellationToken.None);

        // Assert
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task GetAppLoginAsync_GivenGitHubRefusesTheJwt_Should_ThrowGitHubApiExceptionCarryingTheStatus()
    {
        // Arrange
        var sut = BuildSut(new TestHttpMessageHandler("{\"message\":\"Bad credentials\"}", HttpStatusCode.Unauthorized));

        // Act
        var act = () => sut.GetAppLoginAsync(CancellationToken.None);

        // Assert
        (await act.Should().ThrowAsync<GitHubApiException>()).Which.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetAppLoginAsync_GivenNoSlugInTheResponse_Should_ThrowSoReviewsAreNeverMatchedToAnEmptyLogin()
    {
        // Arrange
        var sut = BuildSut(new TestHttpMessageHandler("{\"id\":123456}"));

        // Act
        var act = () => sut.GetAppLoginAsync(CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*slug*");
    }

    [Fact]
    public async Task GetAppLoginAsync_GivenAFailureFollowedBySuccess_Should_NotRememberTheFailure()
    {
        // Arrange
        var calls = 0;
        var handler = new TestHttpMessageHandler(req => ++calls == 1 ? new HttpResponseMessage(HttpStatusCode.BadGateway) : AppResponse("my-reviewer"));
        var sut = BuildSut(handler);

        // Act
        var first = () => sut.GetAppLoginAsync(CancellationToken.None);
        await first.Should().ThrowAsync<GitHubApiException>();
        var login = await sut.GetAppLoginAsync(CancellationToken.None);

        // Assert
        login.Should().Be("my-reviewer[bot]");
    }

    // ── Factory helpers ──────────────────────────────────────────────────────

    private static GitHubAppAuthService BuildSut(
        TestHttpMessageHandler handler,
        TimeProvider? timeProvider = null,
        string? appId = TestAppId,
        string? privateKey = "default")
    {
        var settings = new Dictionary<string, string?>
        {
            ["GitHubAppId"]         = appId,
            ["GitHubReviewAppPrivateKey"] = privateKey == "default" ? RsaKeyHelper.PrivateKeyPem : privateKey
        };

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        return new GitHubAppAuthService(
            new SingleClientFactory(new HttpClient(handler)),
            config,
            timeProvider ?? new FakeTimeProvider(_startOfTime),
            NullLogger<GitHubAppAuthService>.Instance);
    }

    private static async Task CacheTokensForRepositoriesAsync(GitHubAppAuthService sut, int count)
    {
        foreach (var index in Enumerable.Range(0, count))
        {
            await sut.GetInstallationTokenAsync(TestInstallationId, $"repo-{index}", CancellationToken.None);
        }
    }

    private static HttpResponseMessage BuildInstallationTokenResponse(
        string token, DateTimeOffset expiresAt)
    {
        var json = JsonSerializer.Serialize(new { token, expires_at = expiresAt });
        return new HttpResponseMessage(HttpStatusCode.Created)
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
