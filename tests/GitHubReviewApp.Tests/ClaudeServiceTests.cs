namespace GitHubReviewApp.Tests;

public class ClaudeServiceTests
{
    private const string FakeApiKey      = "sk-ant-test-key-12345";
    private const string ExpectedModel   = "claude-sonnet-4-6";
    private const string SampleDiff      = "diff --git a/foo.cs b/foo.cs\n+Console.WriteLine(\"hello\");";
    private const string SamplePrTitle   = "Add greeting feature";

    // ── ReviewDiffAsync — request structure ──────────────────────────────────

    [Fact]
    public async Task ReviewDiffAsync_GivenValidInput_Should_IncludeApiKeyInXApiKeyHeader()
    {
        // Arrange
        string? capturedApiKey = null;
        var handler = new TestHttpMessageHandler(async req =>
        {
            req.Headers.TryGetValues("x-api-key", out var values);
            capturedApiKey = values?.FirstOrDefault();
            return BuildClaudeApiResponse("Looks good!");
        });
        var sut = BuildSut(handler, apiKey: FakeApiKey);

        // Act
        await sut.ReviewDiffAsync(SamplePrTitle, SampleDiff);

        // Assert
        capturedApiKey.Should().Be(FakeApiKey);
    }

    [Fact]
    public async Task ReviewDiffAsync_GivenValidInput_Should_SendCorrectModelNameInRequestBody()
    {
        // Arrange
        JsonElement? capturedBody = null;
        var handler = new TestHttpMessageHandler(async req =>
        {
            var json = await req.Content!.ReadAsStringAsync();
            capturedBody = JsonDocument.Parse(json).RootElement;
            return BuildClaudeApiResponse("Review text");
        });
        var sut = BuildSut(handler, apiKey: FakeApiKey);

        // Act
        await sut.ReviewDiffAsync(SamplePrTitle, SampleDiff);

        // Assert
        capturedBody!.Value.GetProperty("model").GetString().Should().Be(ExpectedModel);
    }

    [Fact]
    public async Task ReviewDiffAsync_GivenValidInput_Should_IncludePrTitleAndDiffInUserMessage()
    {
        // Arrange
        JsonElement? capturedBody = null;
        var handler = new TestHttpMessageHandler(async req =>
        {
            var json = await req.Content!.ReadAsStringAsync();
            capturedBody = JsonDocument.Parse(json).RootElement;
            return BuildClaudeApiResponse("Review text");
        });
        var sut = BuildSut(handler, apiKey: FakeApiKey);

        // Act
        await sut.ReviewDiffAsync(SamplePrTitle, SampleDiff);

        // Assert
        var userContent = capturedBody!.Value
            .GetProperty("messages")[0]
            .GetProperty("content").GetString();
        userContent.Should().Contain(SamplePrTitle);
        userContent.Should().Contain(SampleDiff);
    }

    [Fact]
    public async Task ReviewDiffAsync_GivenValidInput_Should_SetUserRoleOnMessage()
    {
        // Arrange
        JsonElement? capturedBody = null;
        var handler = new TestHttpMessageHandler(async req =>
        {
            var json = await req.Content!.ReadAsStringAsync();
            capturedBody = JsonDocument.Parse(json).RootElement;
            return BuildClaudeApiResponse("Review text");
        });
        var sut = BuildSut(handler, apiKey: FakeApiKey);

        // Act
        await sut.ReviewDiffAsync(SamplePrTitle, SampleDiff);

        // Assert
        capturedBody!.Value
            .GetProperty("messages")[0]
            .GetProperty("role").GetString()
            .Should().Be("user");
    }

    [Fact]
    public async Task ReviewDiffAsync_GivenValidInput_Should_SendAnthropicVersionHeader()
    {
        // Arrange
        string? capturedVersion = null;
        var handler = new TestHttpMessageHandler(async req =>
        {
            req.Headers.TryGetValues("anthropic-version", out var values);
            capturedVersion = values?.FirstOrDefault();
            return BuildClaudeApiResponse("OK");
        });
        var sut = BuildSut(handler, apiKey: FakeApiKey);

        // Act
        await sut.ReviewDiffAsync(SamplePrTitle, SampleDiff);

        // Assert
        capturedVersion.Should().Be("2023-06-01");
    }

    // ── ReviewDiffAsync — response handling ──────────────────────────────────

    [Fact]
    public async Task ReviewDiffAsync_GivenSuccessfulResponse_Should_ReturnTextFromFirstContentBlock()
    {
        // Arrange
        const string ExpectedReviewText = "## Review\n\nThis looks great, ship it!";
        var handler = new TestHttpMessageHandler(_ => BuildClaudeApiResponse(ExpectedReviewText));
        var sut = BuildSut(handler, apiKey: FakeApiKey);

        // Act
        var result = await sut.ReviewDiffAsync(SamplePrTitle, SampleDiff);

        // Assert
        result.Should().Be(ExpectedReviewText);
    }

    [Fact]
    public async Task ReviewDiffAsync_GivenEmptyContentArray_Should_ThrowInvalidOperationException()
    {
        // Arrange
        const string EmptyContentJson = """{"content":[]}""";
        var handler = new TestHttpMessageHandler(req =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(EmptyContentJson, Encoding.UTF8, "application/json")
            });
        var sut = BuildSut(handler, apiKey: FakeApiKey);

        // Act
        var act = () => sut.ReviewDiffAsync(SamplePrTitle, SampleDiff);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*No text content*");
    }

    [Fact]
    public async Task ReviewDiffAsync_GivenNonSuccessStatusCode_Should_ThrowHttpRequestException()
    {
        // Arrange
        var handler = new TestHttpMessageHandler(req =>
            new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var sut = BuildSut(handler, apiKey: FakeApiKey);

        // Act
        var act = () => sut.ReviewDiffAsync(SamplePrTitle, SampleDiff);

        // Assert
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    // ── Activity / Gen AI span attributes ───────────────────────────────────

    [Fact]
    public async Task ReviewDiffAsync_GivenResponseWithUsage_Should_ReturnTextFromContent()
    {
        // Arrange
        const string ExpectedText = "Looks good!";
        var handler = new TestHttpMessageHandler(
            _ => BuildClaudeApiResponseWithUsage(ExpectedText, inputTokens: 100, outputTokens: 200));
        var sut = BuildSut(handler, apiKey: FakeApiKey);

        // Act
        var result = await sut.ReviewDiffAsync(SamplePrTitle, SampleDiff);

        // Assert
        result.Should().Be(ExpectedText);
    }

    [Fact]
    public async Task ReviewDiffAsync_GivenSuccessfulResponse_Should_EmitActivityWithGenAiAttributes()
    {
        // Arrange
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo  = source => source.Name == ActivitySources.ClaudeServiceName,
            Sample          = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add
        };
        ActivitySource.AddActivityListener(listener);

        var handler = new TestHttpMessageHandler(
            _ => BuildClaudeApiResponseWithUsage("Review text", inputTokens: 50, outputTokens: 75));
        var sut = BuildSut(handler, apiKey: FakeApiKey);

        // Act
        await sut.ReviewDiffAsync(SamplePrTitle, SampleDiff);

        // Assert
        var span = activities.Should().ContainSingle().Subject;
        span.GetTagItem("gen_ai.system").Should().Be("anthropic");
        span.GetTagItem("gen_ai.operation.name").Should().Be("chat");
        span.GetTagItem("gen_ai.request.model").Should().Be(ExpectedModel);
        span.GetTagItem("gen_ai.request.max_tokens").Should().Be(4096);
        span.GetTagItem("gen_ai.usage.input_tokens").Should().Be(50);
        span.GetTagItem("gen_ai.usage.output_tokens").Should().Be(75);
    }

    [Fact]
    public async Task ReviewDiffAsync_GivenResponseWithoutUsage_Should_EmitActivityWithoutUsageTags()
    {
        // Arrange
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo  = source => source.Name == ActivitySources.ClaudeServiceName,
            Sample          = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add
        };
        ActivitySource.AddActivityListener(listener);

        var handler = new TestHttpMessageHandler(_ => BuildClaudeApiResponse("Review text"));
        var sut = BuildSut(handler, apiKey: FakeApiKey);

        // Act
        await sut.ReviewDiffAsync(SamplePrTitle, SampleDiff);

        // Assert
        var span = activities.Should().ContainSingle().Subject;
        span.GetTagItem("gen_ai.usage.input_tokens").Should().BeNull();
        span.GetTagItem("gen_ai.usage.output_tokens").Should().BeNull();
    }

    // ── Token usage metrics ──────────────────────────────────────────────────

    [Fact]
    public async Task ReviewDiffAsync_GivenResponseWithUsage_Should_RecordInputAndOutputTokenMetrics()
    {
        // Arrange
        var measurements = new List<(string Name, long Value)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == AppMeters.MeterName)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
            measurements.Add((instrument.Name, measurement)));
        listener.Start();

        var handler = new TestHttpMessageHandler(
            _ => BuildClaudeApiResponseWithUsage("Review text", inputTokens: 50, outputTokens: 75));
        var sut = BuildSut(handler, apiKey: FakeApiKey);

        // Act
        await sut.ReviewDiffAsync(SamplePrTitle, SampleDiff);

        // Assert
        measurements.Should().Contain(m => m.Name == "gen_ai.usage.input_tokens"  && m.Value == 50);
        measurements.Should().Contain(m => m.Name == "gen_ai.usage.output_tokens" && m.Value == 75);
    }

    [Fact]
    public async Task ReviewDiffAsync_GivenResponseWithoutUsage_Should_NotRecordTokenMetrics()
    {
        // Arrange
        var measurements = new List<(string Name, long Value)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == AppMeters.MeterName)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
            measurements.Add((instrument.Name, measurement)));
        listener.Start();

        var handler = new TestHttpMessageHandler(_ => BuildClaudeApiResponse("Review text"));
        var sut = BuildSut(handler, apiKey: FakeApiKey);

        // Act
        await sut.ReviewDiffAsync(SamplePrTitle, SampleDiff);

        // Assert
        measurements.Should().NotContain(m => m.Name == "gen_ai.usage.input_tokens");
        measurements.Should().NotContain(m => m.Name == "gen_ai.usage.output_tokens");
    }

    // ── Factory helpers ──────────────────────────────────────────────────────

    private static ClaudeService BuildSut(TestHttpMessageHandler handler, string apiKey)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AnthropicApiKey"] = apiKey })
            .Build();

        return new ClaudeService(
            new HttpClient(handler),
            config,
            NullLogger<ClaudeService>.Instance);
    }

    private static HttpResponseMessage BuildClaudeApiResponse(string reviewText)
    {
        var json = JsonSerializer.Serialize(new
        {
            content = new[] { new { text = reviewText } }
        });

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static HttpResponseMessage BuildClaudeApiResponseWithUsage(
        string reviewText, int inputTokens, int outputTokens)
    {
        var json = JsonSerializer.Serialize(new
        {
            content = new[] { new { text = reviewText } },
            usage   = new { input_tokens = inputTokens, output_tokens = outputTokens }
        });

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }
}
