namespace GitHubReviewApp.Tests.Services;

public class FailureClassifierTests
{
    // Real SDK exceptions, produced by driving the SDK itself against a fake handler, so the test follows the SDK's own mapping from an HTTP
    // status or a broken stream to an exception type, whatever the application's service does with it afterwards (it turns 400, 413 and 422
    // into a rejected result instead of throwing, so it cannot be the source of those exceptions).
    private static Task<Exception> SdkFailureAsync(HttpResponseMessage response)
    {
        return AnthropicSdkFailure.CaptureAsync(response);
    }

    private static GitHubApiException GitHubFailure(HttpStatusCode status, bool isRateLimited = false)
    {
        return new GitHubApiException("failure", status, isRateLimited);
    }

    // ── Anthropic ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(429, "rate_limit_error")]
    [InlineData(500, "api_error")]
    [InlineData(502, "api_error")]
    [InlineData(503, "api_error")]
    [InlineData(529, "overloaded_error")]
    [InlineData(408, "timeout_error")]
    [InlineData(409, "conflict_error")]
    public async Task IsTransient_GivenAnAnthropicOutageOrRateLimit_Should_BeTrue(int status, string errorType)
    {
        // Arrange
        var exception = await SdkFailureAsync(AnthropicFixtures.Error((HttpStatusCode)status, errorType, "temporary"));

        // Act
        var result = FailureClassifier.IsTransient(exception);

        // Assert
        result.Should().BeTrue($"HTTP {status} ({exception.GetType().Name}) clears up on its own");
    }

    [Theory]
    [InlineData(400, "invalid_request_error")]
    [InlineData(401, "authentication_error")]
    [InlineData(403, "permission_error")]
    [InlineData(404, "not_found_error")]
    [InlineData(422, "invalid_request_error")]
    public async Task IsTransient_GivenAnAnthropicClientError_Should_BeFalse(int status, string errorType)
    {
        // Arrange
        var exception = await SdkFailureAsync(AnthropicFixtures.Error((HttpStatusCode)status, errorType, "permanent"));

        // Act
        var result = FailureClassifier.IsTransient(exception);

        // Assert
        result.Should().BeFalse($"HTTP {status} ({exception.GetType().Name}) would fail the same way on a retry");
    }

    [Theory]
    [InlineData("overloaded_error")]
    [InlineData("api_error")]
    public async Task IsTransient_GivenAStreamThatBreaksPartWayThroughWithAnErrorEvent_Should_BeTrue(string errorType)
    {
        // Arrange — the reply began with a 200, so the failure arrives as an event inside the stream, not as a status code
        var exception = await SdkFailureAsync(AnthropicFixtures.StreamWithAnErrorEvent(errorType, "temporary"));

        // Act
        var result = FailureClassifier.IsTransient(exception);

        // Assert
        result.Should().BeTrue($"{exception.GetType().Name} from a dropped stream clears up on a retry");
    }

    [Fact]
    public async Task IsTransient_GivenTheMonthlySpendLimitIsReached_Should_BeFalseBecauseNoRetryCanSucceedUntilAccessResumes()
    {
        // Arrange — reported with the status and the type of a rate limit, but the queue's retries would fail as the SDK's own do, until the cap is raised
        var exception = await SdkFailureAsync(AnthropicFixtures.SpendLimitReached());

        // Act
        var result = FailureClassifier.IsTransient(exception);

        // Assert
        exception.Should().BeOfType<AnthropicRateLimitException>("the spend limit looks like a rate limit to the SDK");
        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsAiSpendLimitReached_GivenTheMonthlySpendLimit_Should_BeTrue()
    {
        // Arrange
        var exception = await SdkFailureAsync(AnthropicFixtures.SpendLimitReached());

        // Act
        var result = FailureClassifier.IsAiSpendLimitReached(exception);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsAiSpendLimitReached_GivenAnOrdinaryRateLimit_Should_BeFalseAndStillBeWorthRetrying()
    {
        // Arrange
        var exception = await SdkFailureAsync(AnthropicFixtures.Error(HttpStatusCode.TooManyRequests, "rate_limit_error", "Number of request tokens has exceeded your per-minute rate limit."));

        // Act
        var isSpendLimit = FailureClassifier.IsAiSpendLimitReached(exception);
        var isTransient = FailureClassifier.IsTransient(exception);

        // Assert
        isSpendLimit.Should().BeFalse();
        isTransient.Should().BeTrue();
    }

    [Fact]
    public void IsAiSpendLimitReached_GivenAnyOtherFailure_Should_BeFalse()
    {
        // Arrange
        var exception = new TimeoutException("timed out");

        // Act
        var result = FailureClassifier.IsAiSpendLimitReached(exception);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsTransient_GivenANetworkFailureReachingAnthropic_Should_BeTrue()
    {
        // Arrange
        var failingHandler = new TestHttpMessageHandler(
            (Func<HttpRequestMessage, HttpResponseMessage>)(_ => throw new HttpRequestException("connection reset")));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["GitHubReviewAppAnthropicApiKey"] = "sk-ant-test" })
            .Build();
        var sut = new ClaudeService(new HttpClient(failingHandler), config, Options.Create(new ClaudeOptions { MaxRetries = 0 }), NullLogger<ClaudeService>.Instance);
        var act = () => sut.ReviewAsync(new ClaudeRunRequest("claude-sonnet-5-5", "s", "u"), CancellationToken.None);
        var exception = (await act.Should().ThrowAsync<Exception>()).Which;

        // Act
        var result = FailureClassifier.IsTransient(exception);

        // Assert
        result.Should().BeTrue($"a dropped connection ({exception.GetType().Name}) is worth retrying");
    }

    // ── GitHub ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public void IsTransient_GivenATransientGitHubStatus_Should_BeTrue(HttpStatusCode status)
    {
        // Arrange

        // Act
        var result = FailureClassifier.IsTransient(GitHubFailure(status));

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsTransient_GivenAGitHubRateLimitReportedAs403_Should_BeTrue()
    {
        // Arrange

        // Act
        var result = FailureClassifier.IsTransient(GitHubFailure(HttpStatusCode.Forbidden, isRateLimited: true));

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsTransient_GivenGitHubRefusesTheTokenWith401_Should_BeTrueBecauseTheRetryAsksForANewOne()
    {
        // Arrange — an installation token can expire or be revoked while a long review runs, and the next attempt mints a fresh one
        var failure = GitHubFailure(HttpStatusCode.Unauthorized);

        // Act
        var result = FailureClassifier.IsTransient(failure);

        // Assert
        result.Should().BeTrue();
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.BadRequest)]
    public void IsTransient_GivenAGitHubPermissionOrValidationFailure_Should_BeFalse(HttpStatusCode status)
    {
        // Arrange

        // Act
        var result = FailureClassifier.IsTransient(GitHubFailure(status));

        // Assert
        result.Should().BeFalse();
    }

    // ── Generic ──────────────────────────────────────────────────────────────

    [Fact]
    public void IsTransient_GivenAnHttpRequestExceptionWithoutAStatus_Should_BeTrueBecauseItIsANetworkFailure()
    {
        // Arrange

        // Act
        var result = FailureClassifier.IsTransient(new HttpRequestException("connection refused"));

        // Assert
        result.Should().BeTrue();
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    public void IsTransient_GivenAnHttpRequestExceptionWithAStatus_Should_FollowTheStatus(HttpStatusCode status, bool isExpected)
    {
        // Arrange

        // Act
        var result = FailureClassifier.IsTransient(new HttpRequestException("failed", null, status));

        // Assert
        result.Should().Be(isExpected);
    }

    [Fact]
    public void IsTransient_GivenATimeout_Should_BeTrue()
    {
        // Arrange

        // Act
        var results = new[]
        {
            FailureClassifier.IsTransient(new TaskCanceledException("The request timed out.")),
            FailureClassifier.IsTransient(new TimeoutException()),
            FailureClassifier.IsTransient(new IOException("pipe closed"))
        };

        // Assert
        results.Should().OnlyContain(isTransient => isTransient);
    }

    [Theory]
    [MemberData(nameof(PermanentExceptions))]
    public void IsTransient_GivenAProgrammingOrConfigurationError_Should_BeFalse(Exception exception)
    {
        // Arrange

        // Act
        var result = FailureClassifier.IsTransient(exception);

        // Assert
        result.Should().BeFalse($"{exception.GetType().Name} will not fix itself");
    }

    public static IEnumerable<object[]> PermanentExceptions()
    {
        yield return [new InvalidOperationException("Secret 'GitHubReviewAppAnthropicApiKey' is not configured.")];
        yield return [new ArgumentException("bad argument")];
        yield return [new JsonException("bad json")];
        yield return [new NotSupportedException("nope")];
        yield return [new NullReferenceException()];
    }

    [Fact]
    public void IsTransient_GivenAnAggregateOfOnlyTransientFailures_Should_BeTrue()
    {
        // Arrange
        var aggregate = new AggregateException(GitHubFailure(HttpStatusCode.BadGateway), new TimeoutException());

        // Act
        var result = FailureClassifier.IsTransient(aggregate);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsTransient_GivenAnAggregateContainingAPermanentFailure_Should_BeFalse()
    {
        // Arrange — one permanent failure means a retry cannot succeed
        var aggregate = new AggregateException(GitHubFailure(HttpStatusCode.BadGateway), new InvalidOperationException("config"));

        // Act
        var result = FailureClassifier.IsTransient(aggregate);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsTransient_GivenAnEmptyAggregate_Should_BeFalse()
    {
        // Arrange

        // Act
        var result = FailureClassifier.IsTransient(new AggregateException());

        // Assert
        result.Should().BeFalse();
    }
}
