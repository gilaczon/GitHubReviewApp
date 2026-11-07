namespace GitHubReviewApp.Tests.Services;

public class GitHubApiExceptionTests
{
    private static HttpResponseMessage Response(HttpStatusCode status, string body = "")
    {
        return new HttpResponseMessage(status)
        {
            ReasonPhrase = status.ToString(),
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    [Fact]
    public async Task FromResponseAsync_GivenNotFound_Should_CarryTheStatusCodeAndNameTheOperation()
    {
        // Arrange
        using var response = Response(HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}");

        // Act
        var exception = await GitHubApiException.FromResponseAsync("get pull request", response, CancellationToken.None);

        // Assert
        exception.StatusCode.Should().Be(HttpStatusCode.NotFound);
        exception.IsRateLimited.Should().BeFalse();
        exception.Message.Should().Contain("get pull request").And.Contain("404").And.Contain("Not Found");
    }

    [Fact]
    public async Task FromResponseAsync_GivenTooManyRequests_Should_BeRateLimited()
    {
        // Arrange
        using var response = Response(HttpStatusCode.TooManyRequests);

        // Act
        var exception = await GitHubApiException.FromResponseAsync("list files", response, CancellationToken.None);

        // Assert
        exception.IsRateLimited.Should().BeTrue();
    }

    [Fact]
    public async Task FromResponseAsync_GivenForbiddenWithRetryAfter_Should_BeRateLimited()
    {
        // Arrange — GitHub's secondary rate limit answers 403 with Retry-After
        using var response = Response(HttpStatusCode.Forbidden);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));

        // Act
        var exception = await GitHubApiException.FromResponseAsync("post review", response, CancellationToken.None);

        // Assert
        exception.IsRateLimited.Should().BeTrue();
    }

    [Fact]
    public async Task FromResponseAsync_GivenForbiddenWithNoRateLimitRemaining_Should_BeRateLimited()
    {
        // Arrange
        using var response = Response(HttpStatusCode.Forbidden);
        response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");

        // Act
        var exception = await GitHubApiException.FromResponseAsync("post review", response, CancellationToken.None);

        // Assert
        exception.IsRateLimited.Should().BeTrue();
    }

    [Theory]
    [InlineData("{\"message\":\"You have exceeded a secondary rate limit. Please wait a few minutes before you try again.\",\"documentation_url\":\"https://docs.github.com/rest/overview/rate-limits-for-the-rest-api#about-secondary-rate-limits\"}")]
    [InlineData("{\"message\":\"API rate limit exceeded for installation ID 12345678.\",\"documentation_url\":\"https://docs.github.com/rest/overview/rate-limits-for-the-rest-api\"}")]
    [InlineData("{\"message\":\"You have triggered an abuse detection mechanism. Please wait a few minutes before you try again.\"}")]
    public async Task FromResponseAsync_GivenForbiddenWhoseBodyReportsARateLimitButNoHeadersSayIt_Should_BeRateLimited(string body)
    {
        // Arrange — GitHub does not always send Retry-After with a secondary limit, and the quota header can read non-zero; the message is all there is
        using var response = Response(HttpStatusCode.Forbidden, body);

        // Act
        var exception = await GitHubApiException.FromResponseAsync("post review", response, CancellationToken.None);

        // Assert
        exception.IsRateLimited.Should().BeTrue();
    }

    [Fact]
    public async Task FromResponseAsync_GivenAnUnprocessableEntityWhoseBodyMentionsARateLimit_Should_NotBeRateLimitedBecauseOnlyForbiddenIsAmbiguous()
    {
        // Arrange — a rejected request stays rejected, whatever words the message uses
        using var response = Response(HttpStatusCode.UnprocessableEntity, "{\"message\":\"Validation Failed\",\"errors\":[\"the rate limit field is invalid\"]}");

        // Act
        var exception = await GitHubApiException.FromResponseAsync("post review", response, CancellationToken.None);

        // Assert
        exception.IsRateLimited.Should().BeFalse();
    }

    [Fact]
    public async Task FromResponseAsync_GivenPlainForbidden_Should_NotBeRateLimited()
    {
        // Arrange — a genuine permission problem must not be retried
        using var response = Response(HttpStatusCode.Forbidden, "{\"message\":\"Resource not accessible by integration\"}");
        response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "4999");

        // Act
        var exception = await GitHubApiException.FromResponseAsync("post review", response, CancellationToken.None);

        // Assert
        exception.IsRateLimited.Should().BeFalse();
        exception.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task FromResponseAsync_GivenVeryLongBody_Should_TruncateItInTheMessage()
    {
        // Arrange
        using var response = Response(HttpStatusCode.BadGateway, new string('z', 5_000));

        // Act
        var exception = await GitHubApiException.FromResponseAsync("get pull request", response, CancellationToken.None);

        // Assert
        exception.Message.Length.Should().BeLessThan(800);
    }

    [Fact]
    public async Task FromResponseAsync_GivenAnyResponse_Should_BeAnHttpRequestExceptionSoGenericHandlersStillWork()
    {
        // Arrange
        using var response = Response(HttpStatusCode.InternalServerError);

        // Act
        HttpRequestException exception = await GitHubApiException.FromResponseAsync("x", response, CancellationToken.None);

        // Assert
        exception.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }
}
