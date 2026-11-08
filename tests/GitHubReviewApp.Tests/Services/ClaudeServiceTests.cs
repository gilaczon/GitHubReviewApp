namespace GitHubReviewApp.Tests.Services;

[Collection(TelemetryCollection.Name)]
public class ClaudeServiceTests
{
    private const string FakeApiKey = "sk-ant-test-key-12345";

    // A call that hangs must fail the test, not hang the run.
    private static readonly TimeSpan _hangGuard = TimeSpan.FromSeconds(20);

    private static readonly ClaudeReviewRequest _sampleRequest = new("SYSTEM RULES", "USER PULL REQUEST");

    private static readonly ClaudeRunRequest _runRequest = new("claude-sonnet-5-5", "CHECKLIST RULES", "PULL REQUEST INPUT");

    private sealed record CapturedRequest(JsonElement Body, string RawBody, HttpRequestMessage Message);

    // Retries are off so error-path tests do not wait for the SDK's backoff. The refusal fallback is on, as it ships.
    private static ClaudeOptions TestOptions(
        string model = "claude-sonnet-5-5",
        int maxOutputTokens = 16_000,
        string? effort = "high",
        int requestTimeoutSeconds = 30,
        int maxRetries = 0,
        bool isRefusalFallbackEnabled = true)
    {
        return new ClaudeOptions
        {
            Model = model,
            MaxOutputTokens = maxOutputTokens,
            Effort = effort,
            RequestTimeoutSeconds = requestTimeoutSeconds,
            MaxRetries = maxRetries,
            IsRefusalFallbackEnabled = isRefusalFallbackEnabled
        };
    }

    private static IConfiguration BuildConfiguration(string? apiKey)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AnthropicApiKey"] = apiKey })
            .Build();
    }

    private static ClaudeService BuildSut(
        TestHttpMessageHandler handler,
        ClaudeOptions? options = null,
        string? apiKey = FakeApiKey,
        ILogger<ClaudeService>? logger = null,
        TimeProvider? timeProvider = null)
    {
        return new ClaudeService(
            new HttpClient(handler),
            BuildConfiguration(apiKey),
            Options.Create(options ?? TestOptions()),
            logger ?? NullLogger<ClaudeService>.Instance,
            timeProvider);
    }

    private static TestHttpMessageHandler Respond(HttpResponseMessage response)
    {
        return new TestHttpMessageHandler(_ => response);
    }

    private static TestHttpMessageHandler RespondInOrder(params HttpResponseMessage[] responses)
    {
        var queue = new Queue<HttpResponseMessage>(responses);

        return new TestHttpMessageHandler(_ => queue.Dequeue());
    }

    private static async Task<CapturedRequest> CaptureRequestAsync(ClaudeOptions? options = null)
    {
        var rawBody = string.Empty;
        var handler = new TestHttpMessageHandler(async request =>
        {
            rawBody = await request.Content!.ReadAsStringAsync();
            return AnthropicFixtures.Message();
        });

        await BuildSut(handler, options).ReviewAsync(_sampleRequest, CancellationToken.None);

        using var document = JsonDocument.Parse(rawBody);

        return new CapturedRequest(document.RootElement.Clone(), rawBody, handler.LastRequest!);
    }

    private static async Task<CapturedRequest> CaptureRunRequestAsync(ClaudeOptions options)
    {
        var rawBody = string.Empty;
        var handler = new TestHttpMessageHandler(async request =>
        {
            rawBody = await request.Content!.ReadAsStringAsync();
            return AnthropicFixtures.Message([AnthropicFixtures.TextBlock(AnthropicFixtures.ChecklistJson())]);
        });

        await BuildSut(handler, options).ReviewAsync(_runRequest, CancellationToken.None);

        using var document = JsonDocument.Parse(rawBody);

        return new CapturedRequest(document.RootElement.Clone(), rawBody, handler.LastRequest!);
    }

    private static string UserText(JsonElement body)
    {
        var content = body.GetProperty("messages")[0].GetProperty("content");

        return content.ValueKind == JsonValueKind.String
            ? content.GetString()!
            : string.Concat(content.EnumerateArray().Select(block => block.GetProperty("text").GetString()));
    }

    // Every beta the request names, however the SDK spreads them over anthropic-beta headers and comma-separated values.
    private static IReadOnlyList<string> BetasSent(HttpRequestMessage message)
    {
        return message.Headers
            .Where(header => string.Equals(header.Key, "anthropic-beta", StringComparison.OrdinalIgnoreCase))
            .SelectMany(header => header.Value)
            .SelectMany(value => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .ToList();
    }

    // Sonnet 5.5 declined before writing anything and Sonnet 5 answered. The top-level usage covers only the attempt that produced the message;
    // the iterations cover both.
    private static HttpResponseMessage ReplyAnsweredAfterAFallback()
    {
        return AnthropicFixtures.Message(
            [AnthropicFixtures.FallbackBlock("claude-sonnet-5-5", "claude-sonnet-5"), AnthropicFixtures.TextBlock(AnthropicFixtures.ChecklistJson())],
            inputTokens: 1_100,
            outputTokens: 400,
            cacheReadTokens: 0,
            cacheCreationTokens: 900,
            model: "claude-sonnet-5",
            iterations:
            [
                AnthropicFixtures.Iteration("message", "claude-sonnet-5-5", inputTokens: 1_000, outputTokens: 20, cacheReadTokens: 300, cacheCreationTokens: 50),
                AnthropicFixtures.Iteration("fallback_message", "claude-sonnet-5", inputTokens: 1_100, outputTokens: 400, cacheReadTokens: 0, cacheCreationTokens: 900)
            ]);
    }

    // ── Construction ─────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_GivenAnHttpClientWithTheDefaultTimeout_Should_RemoveTheLimitSoALongReplyIsNotCutAtOneHundredSeconds()
    {
        // Arrange
        using var httpClient = new HttpClient(Respond(AnthropicFixtures.Message()));
        httpClient.Timeout.Should().Be(TimeSpan.FromSeconds(100), "the framework default is what this test must see being replaced");

        // Act
        _ = new ClaudeService(httpClient, BuildConfiguration(FakeApiKey), Options.Create(TestOptions()), NullLogger<ClaudeService>.Instance);

        // Assert
        httpClient.Timeout.Should().Be(Timeout.InfiniteTimeSpan);
    }

    // ── Request ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReviewAsync_GivenValidRequest_Should_PostToTheMessagesEndpoint()
    {
        // Arrange

        // Act
        var captured = await CaptureRequestAsync();

        // Assert
        captured.Message.Method.Should().Be(HttpMethod.Post);
        captured.Message.RequestUri!.AbsolutePath.Should().Be("/v1/messages");
        captured.Message.Headers.Contains("anthropic-version").Should().BeTrue();
    }

    [Fact]
    public async Task ReviewAsync_GivenValidRequest_Should_SendTheApiKeyInExactlyOneCredentialHeaderAndNowhereElse()
    {
        // Arrange

        // Act
        var captured = await CaptureRequestAsync();

        // Assert — which header carries the key is the SDK's business; that it is one header, and not the URL or the body, is ours
        var headersCarryingTheKey = captured.Message.Headers
            .Where(header => header.Value.Any(value => value.Contains(FakeApiKey, StringComparison.Ordinal)))
            .Select(header => header.Key)
            .ToList();

        headersCarryingTheKey.Should().ContainSingle().Which.Should().BeOneOf("Authorization", "x-api-key");
        captured.Message.RequestUri!.ToString().Should().NotContain(FakeApiKey);
        captured.RawBody.Should().NotContain(FakeApiKey);
    }

    [Fact]
    public async Task ReviewAsync_GivenAnyRequest_Should_AskForAStreamedReplySoALongReviewDoesNotSitOnASilentConnection()
    {
        // Arrange

        // Act
        var captured = await CaptureRequestAsync();

        // Assert
        captured.Body.GetProperty("stream").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ReviewAsync_GivenDefaultOptions_Should_SendTheCurrentSonnetModelAndTheConfiguredOutputLimit()
    {
        // Arrange

        // Act
        var captured = await CaptureRequestAsync();

        // Assert
        captured.Body.GetProperty("model").GetString().Should().Be("claude-sonnet-5-5");
        captured.Body.GetProperty("max_tokens").GetInt32().Should().Be(16_000);
    }

    [Fact]
    public async Task ReviewAsync_GivenAnotherModelInOptions_Should_SendThatModelWithoutACodeChange()
    {
        // Arrange
        var options = TestOptions(model: "claude-opus-5-5", maxOutputTokens: 32_000);

        // Act
        var captured = await CaptureRequestAsync(options);

        // Assert
        captured.Body.GetProperty("model").GetString().Should().Be("claude-opus-5-5");
        captured.Body.GetProperty("max_tokens").GetInt32().Should().Be(32_000);
    }

    [Theory]
    [InlineData("low", "low")]
    [InlineData("medium", "medium")]
    [InlineData("HIGH", "high")]
    [InlineData("xhigh", "xhigh")]
    [InlineData("max", "max")]
    public async Task ReviewAsync_GivenAnEffortLevel_Should_SendItExplicitly(string configured, string expectedOnTheWire)
    {
        // Arrange
        var options = TestOptions(effort: configured);

        // Act
        var captured = await CaptureRequestAsync(options);

        // Assert
        captured.Body.GetProperty("output_config").GetProperty("effort").GetString().Should().Be(expectedOnTheWire);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ReviewAsync_GivenNoEffortConfigured_Should_OmitTheParameterForModelsThatRejectIt(string? configured)
    {
        // Arrange
        var options = TestOptions(effort: configured);

        // Act
        var captured = await CaptureRequestAsync(options);

        // Assert
        captured.Body.GetProperty("output_config").TryGetProperty("effort", out _).Should().BeFalse();
    }

    [Fact]
    public async Task ReviewAsync_GivenAnUnknownEffortLevel_Should_ThrowSoAMisconfigurationIsNotSilentlyIgnored()
    {
        // Arrange
        var sut = BuildSut(Respond(AnthropicFixtures.Message()), TestOptions(effort: "turbo"));

        // Act
        var act = () => sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*turbo*");
    }

    [Fact]
    public async Task ReviewAsync_GivenAnyRequest_Should_RequestStructuredOutputWithTheReviewSchema()
    {
        // Arrange

        // Act
        var captured = await CaptureRequestAsync();

        // Assert
        var format = captured.Body.GetProperty("output_config").GetProperty("format");
        format.GetProperty("type").GetString().Should().Be("json_schema");
        format.GetProperty("schema").GetProperty("required").EnumerateArray().Select(element => element.GetString())
            .Should().BeEquivalentTo("summary", "findings", "omitted_findings");
    }

    [Fact]
    public async Task ReviewAsync_GivenAPrompt_Should_SendTheSystemPromptAsACacheableBlockAndThePullRequestAsTheUserMessage()
    {
        // Arrange

        // Act
        var captured = await CaptureRequestAsync();

        // Assert
        var system = captured.Body.GetProperty("system").EnumerateArray().Single();
        system.GetProperty("text").GetString().Should().Be("SYSTEM RULES");
        system.GetProperty("cache_control").GetProperty("type").GetString().Should().Be("ephemeral");

        captured.Body.GetProperty("messages").GetArrayLength().Should().Be(1);
        captured.Body.GetProperty("messages")[0].GetProperty("role").GetString().Should().Be("user");
        UserText(captured.Body).Should().Be("USER PULL REQUEST");
    }

    [Theory]
    [InlineData("temperature")]
    [InlineData("top_p")]
    [InlineData("top_k")]
    [InlineData("tool_choice")]
    [InlineData("thinking")]
    public async Task ReviewAsync_GivenAnyRequest_Should_NotSendParametersThatCurrentModelsRejectOrThatChangeThinking(string property)
    {
        // Arrange

        // Act
        var captured = await CaptureRequestAsync();

        // Assert — non-default sampling, forced tool use and disabled thinking all return 400 on Sonnet 5.5
        captured.Body.TryGetProperty(property, out _).Should().BeFalse();
    }

    // ── Response ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReviewAsync_GivenAStructuredReply_Should_ReturnACompletedResultWithSummaryAndFindings()
    {
        // Arrange
        var reply = AnthropicFixtures.ReviewJson(
            "Adds retries.",
            [AnthropicFixtures.Finding(line: 42, severity: "critical", category: "security", title: "SQL injection", body: "User input is concatenated.")]);
        var sut = BuildSut(Respond(AnthropicFixtures.Message([AnthropicFixtures.TextBlock(reply)])));

        // Act
        var result = await sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(ChunkOutcome.Completed);
        result.Summary.Should().Be("Adds retries.");
        result.Model.Should().Be(AnthropicFixtures.ModelId);
        result.Findings.Should().ContainSingle().Which.Should().Be(
            new ReviewFinding("src/Foo.cs", 42, ReviewSeverity.Critical, "security", "SQL injection", "User input is concatenated."));
    }

    [Fact]
    public async Task ReviewAsync_GivenAReplyThatOmitsFindings_Should_ReturnHowManyWereLeftOut()
    {
        // Arrange
        var reply = AnthropicFixtures.ReviewJson("Many problems.", [AnthropicFixtures.Finding()], omittedFindings: 7);
        var sut = BuildSut(Respond(AnthropicFixtures.Message([AnthropicFixtures.TextBlock(reply)])));

        // Act
        var result = await sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(ChunkOutcome.Completed);
        result.OmittedFindingCount.Should().Be(7);
    }

    [Fact]
    public async Task ReviewAsync_GivenAThinkingBlockBeforeTheAnswer_Should_ReadTheTextBlockAndNotPostAnEmptyReview()
    {
        // Arrange — current models think by default; with display omitted the first block has empty text
        var content = new[] { AnthropicFixtures.ThinkingBlock(), AnthropicFixtures.TextBlock(AnthropicFixtures.ReviewJson("After thinking.")) };
        var sut = BuildSut(Respond(AnthropicFixtures.Message(content)));

        // Act
        var result = await sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(ChunkOutcome.Completed);
        result.Summary.Should().Be("After thinking.");
    }

    [Fact]
    public async Task ReviewAsync_GivenARedactedThinkingBlockBeforeTheAnswer_Should_SkipItAndReadTheTextBlock()
    {
        // Arrange — when the API redacts the model's reasoning it sends opaque data and no text
        var content = new[] { AnthropicFixtures.RedactedThinkingBlock(), AnthropicFixtures.TextBlock(AnthropicFixtures.ReviewJson("After redaction.")) };
        var sut = BuildSut(Respond(AnthropicFixtures.Message(content)));

        // Act
        var result = await sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(ChunkOutcome.Completed);
        result.Summary.Should().Be("After redaction.");
    }

    [Fact]
    public async Task ReviewAsync_GivenOnlyARedactedThinkingBlockAndNoAnswer_Should_ReturnInvalidOutputNotAnEmptyReview()
    {
        // Arrange
        var sut = BuildSut(Respond(AnthropicFixtures.Message([AnthropicFixtures.RedactedThinkingBlock()])));

        // Act
        var result = await sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(ChunkOutcome.InvalidOutput);
    }

    [Fact]
    public async Task ReviewAsync_GivenTheAnswerSplitAcrossTextBlocks_Should_JoinThemBeforeParsing()
    {
        // Arrange
        var json = AnthropicFixtures.ReviewJson("Joined summary.");
        var half = json.Length / 2;
        var content = new[] { AnthropicFixtures.TextBlock(json[..half]), AnthropicFixtures.TextBlock(json[half..]) };
        var sut = BuildSut(Respond(AnthropicFixtures.Message(content)));

        // Act
        var result = await sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(ChunkOutcome.Completed);
        result.Summary.Should().Be("Joined summary.");
    }

    [Fact]
    public async Task ReviewAsync_GivenNonAsciiText_Should_ReturnItIntactAcrossTheStreamedPieces()
    {
        // Arrange — the fixture streams every text block as two deltas, so this also proves they are joined without damage
        const string summary = "Añade reintentos ✓ 日本語 😀 end";
        var reply = JsonSerializer.Serialize(new { summary, findings = Array.Empty<object>(), omitted_findings = 0 }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var sut = BuildSut(Respond(AnthropicFixtures.Message([AnthropicFixtures.TextBlock(reply)])));

        // Act
        var result = await sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(ChunkOutcome.Completed);
        result.Summary.Should().Be(summary);
    }

    [Theory]
    [InlineData("max_tokens", "output limit reached")]
    [InlineData("model_context_window_exceeded", "context window")]
    public async Task ReviewAsync_GivenTheReplyWasCutShort_Should_ReturnOutputTruncatedInsteadOfAHalfReview(string stopReason, string expectedDetailFragment)
    {
        // Arrange — the text is cut mid-JSON, as it would be when the output limit or the context window runs out
        var truncated = AnthropicFixtures.ReviewJson("Cut", [AnthropicFixtures.Finding()])[..60];
        var sut = BuildSut(Respond(AnthropicFixtures.Message([AnthropicFixtures.TextBlock(truncated)], stopReason: stopReason)));

        // Act
        var result = await sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(ChunkOutcome.OutputTruncated);
        result.Detail.Should().Contain(expectedDetailFragment);
        result.Findings.Should().BeEmpty();
    }

    [Fact]
    public async Task ReviewAsync_GivenFindingsThatCannotBeRead_Should_ReportHowManyWereLeftOutSoTheReviewCanSayItToo()
    {
        // Arrange — one entry is not a finding at all, one is a real finding
        const string Reply = """{"summary":"s","findings":["a string",{"path":"b.cs","line":2,"severity":"low","category":"bug","title":"real","body":""}],"omitted_findings":0}""";
        var sut = BuildSut(Respond(AnthropicFixtures.Message([AnthropicFixtures.TextBlock(Reply)])));

        // Act
        var result = await sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(ChunkOutcome.Completed);
        result.Findings.Should().ContainSingle();
        result.DroppedFindingCount.Should().Be(1);
    }

    [Fact]
    public async Task ReviewAsync_GivenFindingsThatCannotBeRead_Should_SayHowManyWereIgnoredInTheLog()
    {
        // Arrange
        const string Reply = """{"summary":"s","findings":["a string",{"path":"b.cs","line":2,"severity":"low","category":"bug","title":"real","body":""}],"omitted_findings":0}""";
        var logger = new ListLogger<ClaudeService>();
        var sut = BuildSut(Respond(AnthropicFixtures.Message([AnthropicFixtures.TextBlock(Reply)])), logger: logger);

        // Act
        await sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        logger.Entries.Should().Contain(entry => entry.Level == LogLevel.Warning && entry.Message.StartsWith("1 malformed finding", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReviewAsync_GivenAReplyFromADifferentModelThanConfigured_Should_NameTheModelThatAnswered()
    {
        // Arrange — the review's footer credits the model that did the work, which is the one the reply names, whatever the configuration says
        var sut = BuildSut(Respond(AnthropicFixtures.Message(model: "claude-other-model")), TestOptions(model: "claude-sonnet-5-5"));

        // Act
        var result = await sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        result.Model.Should().Be("claude-other-model");
    }

    [Fact]
    public async Task ReviewAsync_GivenTheReplyHitTheOutputLimit_Should_SayTheOutputLimitInTheLog()
    {
        // Arrange
        var logger = new ListLogger<ClaudeService>();
        var sut = BuildSut(
            Respond(AnthropicFixtures.Message([AnthropicFixtures.TextBlock("{\"summ")], stopReason: "max_tokens")),
            TestOptions(maxOutputTokens: 16_000),
            logger: logger);

        // Act
        await sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        logger.Entries.Should().Contain(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("output limit of 16000 tokens", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReviewAsync_GivenTheRequestDidNotFitTheContextWindow_Should_SayTheContextWindowInTheLogAndNotTheOutputLimit()
    {
        // Arrange — the output limit has nothing to do with it, and an operator reading "output limit" would raise the wrong setting
        var logger = new ListLogger<ClaudeService>();
        var sut = BuildSut(
            Respond(AnthropicFixtures.Message([AnthropicFixtures.TextBlock("{\"summ")], stopReason: "model_context_window_exceeded")),
            logger: logger);

        // Act
        await sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        logger.Entries.Should().Contain(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("context window", StringComparison.Ordinal));
        logger.Entries.Should().NotContain(entry => entry.Message.Contains("output limit", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("refusal")]
    [InlineData("max_tokens")]
    [InlineData("model_context_window_exceeded")]
    public async Task ReviewAsync_GivenAReplyThatCannotBeUsedAsAReview_Should_MarkTheSpanAsAnErrorSoItIsNotGreenInTheTraces(string stopReason)
    {
        // Arrange — a part of the review was lost, which is what an operator looks for in the traces
        using var spans = new SpanRecorder(ActivitySources.ClaudeServiceName);
        var sut = BuildSut(Respond(AnthropicFixtures.Message([AnthropicFixtures.TextBlock("{\"summ")], stopReason: stopReason)));

        // Act
        await sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        spans.Spans.Should().ContainSingle().Which.Status.Should().Be(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task ReviewAsync_GivenAReplyThatIsNotAReview_Should_MarkTheSpanAsAnError()
    {
        // Arrange
        using var spans = new SpanRecorder(ActivitySources.ClaudeServiceName);
        var sut = BuildSut(Respond(AnthropicFixtures.Message([AnthropicFixtures.TextBlock("I could not find a problem.")], stopReason: "end_turn")));

        // Act
        var result = await sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(ChunkOutcome.InvalidOutput);
        spans.Spans.Should().ContainSingle().Which.Status.Should().Be(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task ReviewAsync_GivenACompleteReview_Should_NotMarkTheSpanAsAnError()
    {
        // Arrange
        using var spans = new SpanRecorder(ActivitySources.ClaudeServiceName);
        var sut = BuildSut(Respond(AnthropicFixtures.Message([AnthropicFixtures.TextBlock(AnthropicFixtures.ReviewJson())], stopReason: "end_turn")));

        // Act
        await sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        spans.Spans.Should().ContainSingle().Which.Status.Should().NotBe(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task ReviewAsync_GivenARefusal_Should_ReturnRefusedWithTheCategory()
    {
        // Arrange
        var details = new { type = "refusal", category = "cyber", explanation = "Could enable cyber harm." };
        var sut = BuildSut(Respond(AnthropicFixtures.Message([], stopReason: "refusal", stopDetails: details)));

        // Act
        var result = await sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(ChunkOutcome.Refused);
        result.Detail.Should().Be("cyber");
        result.Findings.Should().BeEmpty();
    }

    [Fact]
    public async Task ReviewAsync_GivenARefusalWithoutDetails_Should_StillReturnRefused()
    {
        // Arrange
        var sut = BuildSut(Respond(AnthropicFixtures.Message([], stopReason: "refusal")));

        // Act
        var result = await sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(ChunkOutcome.Refused);
        result.Detail.Should().BeNull();
    }

    [Fact]
    public async Task ReviewAsync_GivenAStopReasonThisCodeHasNeverSeen_Should_ReadTheCompleteReviewAnyway()
    {
        // Arrange — the API adds stop reasons over time; one this code does not know must not turn a good review into a failure
        var reply = AnthropicFixtures.ReviewJson("Complete despite the odd reason.");
        var sut = BuildSut(Respond(AnthropicFixtures.Message([AnthropicFixtures.TextBlock(reply)], stopReason: "a_future_stop_reason")));

        // Act
        var result = await sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(ChunkOutcome.Completed);
        result.Summary.Should().Be("Complete despite the odd reason.");
    }

    [Fact]
    public async Task ReviewAsync_GivenUnreadableJson_Should_ReturnInvalidOutputWithTheReason()
    {
        // Arrange
        var sut = BuildSut(Respond(AnthropicFixtures.Message([AnthropicFixtures.TextBlock("{ \"summary\": ")])));

        // Act
        var result = await sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(ChunkOutcome.InvalidOutput);
        result.Detail.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ReviewAsync_GivenNoContentAtAll_Should_ReturnInvalidOutput()
    {
        // Arrange
        var sut = BuildSut(Respond(AnthropicFixtures.Message([])));

        // Act
        var result = await sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(ChunkOutcome.InvalidOutput);
    }

    [Fact]
    public async Task ReviewAsync_GivenAProseReply_Should_ReturnInvalidOutputSoItIsNeverPostedAsACleanReview()
    {
        // Arrange
        var sut = BuildSut(Respond(AnthropicFixtures.Message([AnthropicFixtures.TextBlock("Looks good to me overall.")])));

        // Act
        var result = await sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(ChunkOutcome.InvalidOutput);
        result.Findings.Should().BeEmpty();
        result.Summary.Should().BeEmpty();
    }

    [Fact]
    public async Task ReviewAsync_GivenUsageWithCacheTokens_Should_MapTokenUsageIncludingPromptCacheTokens()
    {
        // Arrange
        var sut = BuildSut(Respond(AnthropicFixtures.Message(inputTokens: 1_000, outputTokens: 250, cacheReadTokens: 800, cacheCreationTokens: 50)));

        // Act
        var result = await sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        result.Usage.Should().Be(new ClaudeUsage(1_000, 250, 800, 50));
    }

    [Fact]
    public async Task ReviewAsync_GivenUsageWithoutCacheFields_Should_CountTheCacheTokensAsZero()
    {
        // Arrange — a reply with no prompt caching in play carries no cache counters at all
        var sut = BuildSut(Respond(AnthropicFixtures.Message(inputTokens: 1_000, outputTokens: 250, isCacheUsageOmitted: true)));

        // Act
        var result = await sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(ChunkOutcome.Completed);
        result.Usage.Should().Be(new ClaudeUsage(1_000, 250, 0, 0));
    }

    [Fact]
    public async Task ReviewAsync_GivenUsageFiguresThatTheFinalEventRestates_Should_ReportTheFinalOnesNotTheProvisionalOnes()
    {
        // Arrange — the API documents its counts as cumulative, so the first event can say less than the last: here one input token and no caching
        var sut = BuildSut(Respond(AnthropicFixtures.Message(
            inputTokens: 1_200, outputTokens: 300, cacheReadTokens: 800, cacheCreationTokens: 50, isUsageRestatedInFinalEvent: true)));

        // Act
        var result = await sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        result.Usage.Should().Be(new ClaudeUsage(1_200, 300, 800, 50));
    }

    [Fact]
    public async Task ReviewAsync_GivenTextInOtherLanguagesAndCharactersJsonWritersEscape_Should_ReadItWithoutDamageEvenWhenItArrivesOneByteAtATime()
    {
        // Arrange — characters of two, three and four bytes, and the ones another writer would turn into escapes, with every multi-byte one split across reads
        const string title = "Fügt <Wiederholung> & »Zeitlimit« hinzu: 日本語のコメント 😀";
        const string body = "Das Ergebnis kann leer sein, wenn der Cache kalt ist: naïve café ✓";
        var reply = AnthropicFixtures.ReviewJson(summary: title, findings: [AnthropicFixtures.Finding(title: title, body: body)]);
        var sut = BuildSut(Respond(AnthropicFixtures.Message([AnthropicFixtures.TextBlock(reply)], bytesPerRead: 1)));

        // Act
        var result = await sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(ChunkOutcome.Completed);
        result.Summary.Should().Be(title);
        var finding = result.Findings.Should().ContainSingle().Subject;
        finding.Title.Should().Be(title);
        finding.Body.Should().Be(body);
    }

    // ── Requests the service refuses ─────────────────────────────────────────

    [Theory]
    [InlineData(400, "invalid_request_error")]
    [InlineData(413, "request_too_large")]
    [InlineData(422, "invalid_request_error")]
    public async Task ReviewAsync_GivenTheServiceRejectsTheRequestAsInvalid_Should_ReturnRejectedInsteadOfFailingTheWholeReview(int status, string errorType)
    {
        // Arrange — the same request would be refused on every attempt, and the other parts of a large review are unaffected
        var sut = BuildSut(Respond(AnthropicFixtures.Error((HttpStatusCode)status, errorType, "prompt is too long")));

        // Act
        var result = await sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(ChunkOutcome.Rejected);
        result.Detail.Should().Contain(status.ToString());
        result.Findings.Should().BeEmpty();
        result.Usage.Should().Be(ClaudeUsage.Empty);
    }

    [Fact]
    public async Task ReviewAsync_GivenTheServiceRejectsTheRequest_Should_NameNoModelBecauseNothingRan()
    {
        // Arrange — the footer of a review credits the model named here, so a request that never ran must not name one
        var sut = BuildSut(Respond(AnthropicFixtures.Error(HttpStatusCode.BadRequest, "invalid_request_error", "prompt is too long")));

        // Act
        var result = await sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        result.Model.Should().BeEmpty();
    }

    [Fact]
    public async Task ReviewAsync_GivenARejectionThatExplainsItself_Should_QuoteTheExplanationInTheDetail()
    {
        // Arrange
        var sut = BuildSut(Respond(AnthropicFixtures.Error(HttpStatusCode.BadRequest, "invalid_request_error", "prompt is too long: 212345 tokens > 200000 maximum")));

        // Act
        var result = await sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        result.Detail.Should().Be("HTTP 400: prompt is too long: 212345 tokens > 200000 maximum");
    }

    [Fact]
    public async Task ReviewAsync_GivenAnExplanationOverThreeHundredCharacters_Should_CutItThereAndMarkTheCut()
    {
        // Arrange — the detail goes to the logs, a span and the pull request, so a service that sends a page of text must not fill them
        var explanation = new string('x', 300) + "TAIL";
        var sut = BuildSut(Respond(AnthropicFixtures.Error(HttpStatusCode.BadRequest, "invalid_request_error", explanation)));

        // Act
        var result = await sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        result.Detail.Should().Be("HTTP 400: " + new string('x', 300) + "…");
    }

    [Fact]
    public async Task ReviewAsync_GivenAnExplanationOfExactlyThreeHundredCharacters_Should_KeepItWhole()
    {
        // Arrange
        var explanation = new string('x', 300);
        var sut = BuildSut(Respond(AnthropicFixtures.Error(HttpStatusCode.BadRequest, "invalid_request_error", explanation)));

        // Act
        var result = await sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        result.Detail.Should().Be("HTTP 400: " + explanation);
    }

    [Fact]
    public async Task ReviewAsync_GivenAnExplanationSpreadOverSeveralLines_Should_ReadAsOneLineWithoutTheEdgeWhitespace()
    {
        // Arrange — a line break would split the one-line log entry and the sentence in the pull request note
        var sut = BuildSut(Respond(AnthropicFixtures.Error(HttpStatusCode.BadRequest, "invalid_request_error", "\r\nfirst line\r\nsecond line\nthird line\n")));

        // Act
        var result = await sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        result.Detail.Should().Be("HTTP 400: first line second line third line");
    }

    [Theory]
    [InlineData("<html><body><h1>400 Bad Request</h1></body></html>", "text/html")]
    [InlineData("", "application/json")]
    [InlineData("   ", "application/json")]
    [InlineData("{}", "application/json")]
    [InlineData("[1,2]", "application/json")]
    [InlineData("{\"error\":\"oops\"}", "application/json")]
    [InlineData("{\"error\":{}}", "application/json")]
    [InlineData("{\"error\":{\"message\":42}}", "application/json")]
    [InlineData("{\"error\":{\"message\":\"   \"}}", "application/json")]
    public async Task ReviewAsync_GivenARejectionWithNothingWorthQuoting_Should_GiveTheStatusAlone(string body, string mediaType)
    {
        // Arrange — a proxy's page, an empty body or JSON in some other shape: the status is all that can be said, and reading it must not fail
        var sut = BuildSut(Respond(AnthropicFixtures.ErrorWithBody(HttpStatusCode.BadRequest, body, mediaType)));

        // Act
        var result = await sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(ChunkOutcome.Rejected);
        result.Detail.Should().Be("HTTP 400");
    }

    [Theory]
    [InlineData(401, "authentication_error")]
    [InlineData(403, "permission_error")]
    [InlineData(404, "not_found_error")]
    public async Task ReviewAsync_GivenACredentialOrModelProblem_Should_StillThrowBecauseNoPartOfAnyReviewCanSucceed(int status, string errorType)
    {
        // Arrange
        var sut = BuildSut(Respond(AnthropicFixtures.Error((HttpStatusCode)status, errorType, "Check the key or the model name.")));

        // Act
        var act = () => sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        (await act.Should().ThrowAsync<AnthropicApiException>()).Which.StatusCode.Should().Be((HttpStatusCode)status);
    }

    // ── Failures ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReviewAsync_GivenNoApiKeyConfigured_Should_ThrowInvalidOperationExceptionNamingTheSecret()
    {
        // Arrange
        var sut = BuildSut(Respond(AnthropicFixtures.Message()), apiKey: null);

        // Act
        var act = () => sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*AnthropicApiKey*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task ReviewAsync_GivenAnApiKeyThatIsBlank_Should_ThrowInvalidOperationExceptionNamingTheSecretLikeAMissingOne(string apiKey)
    {
        // Arrange — a secret store that hands back spaces is as good as none, and the SDK would send it and fail with a 401 that names nothing
        var sut = BuildSut(Respond(AnthropicFixtures.Message()), apiKey: apiKey);

        // Act
        var act = () => sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*AnthropicApiKey*");
    }

    [Fact]
    public async Task ReviewAsync_GivenTheApiKeyAppearsAfterAFailedAttempt_Should_UseItOnTheNextCallWithoutARestart()
    {
        // Arrange — a failed creation of the client is not remembered, so fixing the secret takes effect at once
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["AnthropicApiKey"] = null }).Build();
        var sut = new ClaudeService(
            new HttpClient(Respond(AnthropicFixtures.Message())), configuration, Options.Create(TestOptions()), NullLogger<ClaudeService>.Instance);
        var firstAttempt = () => sut.ReviewAsync(_sampleRequest, CancellationToken.None);
        await firstAttempt.Should().ThrowAsync<InvalidOperationException>();

        // Act
        configuration["AnthropicApiKey"] = FakeApiKey;
        var result = await sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(ChunkOutcome.Completed);
    }

    [Fact]
    public async Task ReviewAsync_GivenRateLimiting_Should_ThrowTheSdksRateLimitException()
    {
        // Arrange
        var sut = BuildSut(Respond(AnthropicFixtures.Error(HttpStatusCode.TooManyRequests, "rate_limit_error", "Slow down.")));

        // Act
        var act = () => sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<AnthropicRateLimitException>();
    }

    [Fact]
    public async Task ReviewAsync_GivenAnOverloadedApi_Should_ThrowAServerErrorException()
    {
        // Arrange
        var sut = BuildSut(Respond(AnthropicFixtures.Error((HttpStatusCode)529, "overloaded_error", "Overloaded")));

        // Act
        var act = () => sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<Anthropic5xxException>();
    }

    [Fact]
    public async Task ReviewAsync_GivenTheStreamBreaksWithAnErrorEventPartWayThrough_Should_FailWithAnErrorThatIsWorthRetrying()
    {
        // Arrange — the API reports an overload inside a stream that already started with a 200
        var sut = BuildSut(Respond(AnthropicFixtures.StreamWithAnErrorEvent()));

        // Act
        var act = () => sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        var failure = (await act.Should().ThrowAsync<Exception>()).Which;
        FailureClassifier.IsTransient(failure).Should().BeTrue($"{failure.GetType().Name} is a dropped stream, not a fault in the request");
    }

    [Fact]
    public async Task ReviewAsync_GivenTheStreamEndsBeforeTheModelFinishes_Should_FailWithAnErrorThatIsWorthRetryingInsteadOfReadingAHalfReply()
    {
        // Arrange — the connection closed early, so no stop reason ever arrived
        var sut = BuildSut(Respond(AnthropicFixtures.StreamThatEndsBeforeTheModelFinishes()));

        // Act
        var act = () => sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        var failure = (await act.Should().ThrowAsync<Exception>()).Which;
        FailureClassifier.IsTransient(failure).Should().BeTrue($"{failure.GetType().Name} is a reply cut off in transit");
    }

    [Fact]
    public async Task ReviewAsync_GivenTheStreamStopsWithoutEverReportingAStopReason_Should_FailWithAnErrorThatIsWorthRetryingEvenIfTheTextLooksComplete()
    {
        // Arrange — text that parses as a review is not proof the reply was whole when the model never said why it stopped
        var sut = BuildSut(Respond(AnthropicFixtures.StreamWithoutAStopReason()));

        // Act
        var act = () => sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        var failure = (await act.Should().ThrowAsync<Exception>()).Which;
        FailureClassifier.IsTransient(failure).Should().BeTrue($"{failure.GetType().Name} is a reply that did not finish properly");
    }

    [Fact]
    public async Task ReviewAsync_GivenACancelledToken_Should_ThrowOperationCanceled()
    {
        // Arrange
        var sut = BuildSut(Respond(AnthropicFixtures.Message()));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // Act
        var act = () => sut.ReviewAsync(_sampleRequest, cancellation.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ── Time limit ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ReviewAsync_GivenTheReplyStallsAfterItStarts_Should_ThrowATimeoutWhenTheBudgetRunsOut()
    {
        // Arrange — a stream that goes quiet after its first event is the failure an HTTP timeout on the headers alone would never see
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var time = new FakeTimeProvider();
        var sut = BuildSut(Respond(AnthropicFixtures.StreamThatStallsAfterItsFirstEvent(stalled)), TestOptions(requestTimeoutSeconds: 30), timeProvider: time);

        // Act
        var review = sut.ReviewAsync(_sampleRequest, CancellationToken.None);
        await stalled.Task.WaitAsync(_hangGuard);
        time.Advance(TimeSpan.FromSeconds(31));
        var act = () => review.WaitAsync(_hangGuard);

        // Assert — the message tells our own limit from the guard's generic one
        (await act.Should().ThrowAsync<TimeoutException>()).Which.Message.Should().Contain("within the 30-second limit");
    }

    [Fact]
    public async Task ReviewAsync_GivenTheReplyStallsButTheBudgetHasNotRunOut_Should_KeepWaiting()
    {
        // Arrange — the limit must be the configured one, not something shorter
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var time = new FakeTimeProvider();
        var sut = BuildSut(Respond(AnthropicFixtures.StreamThatStallsAfterItsFirstEvent(stalled)), TestOptions(requestTimeoutSeconds: 30), timeProvider: time);

        // Act
        var review = sut.ReviewAsync(_sampleRequest, CancellationToken.None);
        await stalled.Task.WaitAsync(_hangGuard);
        time.Advance(TimeSpan.FromSeconds(29));
        await Settle.QueuedWorkAsync();

        // Assert
        review.IsCompleted.Should().BeFalse("the budget has one second left");
    }

    [Fact]
    public async Task ReviewAsync_GivenTheCallerCancelsWhileTheReplyIsStalled_Should_ThrowCancellationNotATimeout()
    {
        // Arrange — a host shutdown must read as a shutdown, so the message is retried rather than reported as a model that was too slow
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sut = BuildSut(Respond(AnthropicFixtures.StreamThatStallsAfterItsFirstEvent(stalled)), timeProvider: new FakeTimeProvider());
        using var cancellation = new CancellationTokenSource();

        // Act
        var review = sut.ReviewAsync(_sampleRequest, cancellation.Token);
        await stalled.Task.WaitAsync(_hangGuard);
        await cancellation.CancelAsync();
        var act = () => review.WaitAsync(_hangGuard);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ReviewAsync_GivenAnEventTheSdkCannotRead_Should_NotRelabelItAsATransientFailure()
    {
        // Arrange — a reply in a format this code does not understand is the same on every attempt, unlike a stream that merely ended early
        var sut = BuildSut(Respond(AnthropicFixtures.StreamWithAnEventThatIsNotJson()));

        // Act
        var act = () => sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        var failure = (await act.Should().ThrowAsync<AnthropicInvalidDataException>()).Which;
        FailureClassifier.IsTransient(failure).Should().BeFalse();
    }

    // ── Retries ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReviewAsync_GivenOneRetryConfigured_Should_SendTheRequestAgainAfterAnOverloadAndReturnTheReview()
    {
        // Arrange
        var handler = RespondInOrder(
            AnthropicFixtures.Error((HttpStatusCode)529, "overloaded_error", "Overloaded"),
            AnthropicFixtures.Message([AnthropicFixtures.TextBlock(AnthropicFixtures.ReviewJson("Second try."))]));
        var sut = BuildSut(handler, TestOptions(maxRetries: 1));

        // Act
        var result = await sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        handler.Requests.Should().HaveCount(2);
        result.Outcome.Should().Be(ChunkOutcome.Completed);
        result.Summary.Should().Be("Second try.");
    }

    [Fact]
    public async Task ReviewAsync_GivenNoRetriesConfigured_Should_SendTheRequestOnlyOnceBecauseTheSdkDefaultWouldRetryTwice()
    {
        // Arrange
        var handler = Respond(AnthropicFixtures.Error((HttpStatusCode)529, "overloaded_error", "Overloaded"));
        var sut = BuildSut(handler, TestOptions(maxRetries: 0));

        // Act
        var act = () => sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<Anthropic5xxException>();
        handler.Requests.Should().HaveCount(1);
    }

    // ── Telemetry ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReviewAsync_GivenSuccessfulResponse_Should_EmitASpanWithGenAiAttributes()
    {
        // Arrange
        using var spans = new SpanRecorder(ActivitySources.ClaudeServiceName);
        var sut = BuildSut(Respond(AnthropicFixtures.Message(inputTokens: 50, outputTokens: 75, stopReason: "end_turn")));

        // Act
        await sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        var span = spans.Spans.Should().ContainSingle().Subject;
        span.GetTagItem("gen_ai.system").Should().Be("anthropic");
        span.GetTagItem("gen_ai.operation.name").Should().Be("chat");
        span.GetTagItem("gen_ai.request.model").Should().Be("claude-sonnet-5-5");
        span.GetTagItem("gen_ai.request.max_tokens").Should().Be(16_000);
        span.GetTagItem("gen_ai.response.model").Should().Be(AnthropicFixtures.ModelId);
        span.GetTagItem("gen_ai.usage.input_tokens").Should().Be(50L);
        span.GetTagItem("gen_ai.usage.output_tokens").Should().Be(75L);
        span.GetTagItem("gen_ai.response.finish_reasons").Should().BeEquivalentTo(new[] { "end_turn" });
    }

    [Fact]
    public async Task ReviewAsync_GivenAFailedCall_Should_MarkTheSpanAsAnError()
    {
        // Arrange
        using var spans = new SpanRecorder(ActivitySources.ClaudeServiceName);
        var sut = BuildSut(Respond(AnthropicFixtures.Error(HttpStatusCode.TooManyRequests, "rate_limit_error", "Slow down.")));

        // Act
        var act = () => sut.ReviewAsync(_sampleRequest, CancellationToken.None);
        await act.Should().ThrowAsync<AnthropicRateLimitException>();

        // Assert
        spans.Spans.Should().ContainSingle().Which.Status.Should().Be(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task ReviewAsync_GivenATimeout_Should_MarkTheSpanAsAnErrorAndRecordTheTimeout()
    {
        // Arrange
        using var spans = new SpanRecorder(ActivitySources.ClaudeServiceName);
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var time = new FakeTimeProvider();
        var sut = BuildSut(Respond(AnthropicFixtures.StreamThatStallsAfterItsFirstEvent(stalled)), TestOptions(requestTimeoutSeconds: 30), timeProvider: time);

        // Act
        var review = sut.ReviewAsync(_sampleRequest, CancellationToken.None);
        await stalled.Task.WaitAsync(_hangGuard);
        time.Advance(TimeSpan.FromSeconds(31));
        var act = () => review.WaitAsync(_hangGuard);
        await act.Should().ThrowAsync<TimeoutException>();

        // Assert
        var span = spans.Spans.Should().ContainSingle().Subject;
        span.Status.Should().Be(ActivityStatusCode.Error);
        span.Events.Should().Contain(spanEvent => spanEvent.Name == "exception");
    }

    [Fact]
    public async Task ReviewAsync_GivenARejectedRequest_Should_MarkTheSpanAsAnErrorWithTheStatusCode()
    {
        // Arrange
        using var spans = new SpanRecorder(ActivitySources.ClaudeServiceName);
        var sut = BuildSut(Respond(AnthropicFixtures.Error(HttpStatusCode.BadRequest, "invalid_request_error", "prompt is too long")));

        // Act
        await sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        var span = spans.Spans.Should().ContainSingle().Subject;
        span.Status.Should().Be(ActivityStatusCode.Error);
        span.GetTagItem("http.response.status_code").Should().Be(400);
    }

    [Fact]
    public async Task ReviewAsync_GivenResponseWithUsage_Should_RecordInputOutputAndCacheTokenMetrics()
    {
        // Arrange
        using var metrics = new MetricsRecorder();
        var sut = BuildSut(Respond(AnthropicFixtures.Message(inputTokens: 50, outputTokens: 75, cacheReadTokens: 30, cacheCreationTokens: 20)));

        // Act
        await sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        metrics.Sum("gen_ai.usage.input_tokens").Should().Be(50);
        metrics.Sum("gen_ai.usage.output_tokens").Should().Be(75);
        metrics.Sum("gen_ai.usage.cache_read_input_tokens").Should().Be(30);
        metrics.Sum("gen_ai.usage.cache_creation_input_tokens").Should().Be(20);
    }

    [Theory]
    [InlineData("end_turn")]
    [InlineData("max_tokens")]
    [InlineData("a_future_stop_reason")]
    public async Task ReviewAsync_GivenAnyStopReason_Should_TagTheTokenMetricsWithItSoTruncationAndSpendCanBeSeenTogether(string stopReason)
    {
        // Arrange
        using var metrics = new MetricsRecorder();
        var sut = BuildSut(Respond(AnthropicFixtures.Message([AnthropicFixtures.TextBlock(AnthropicFixtures.ReviewJson())], stopReason: stopReason, outputTokens: 75)));

        // Act
        await sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        metrics.Sum("gen_ai.usage.output_tokens", ("stop_reason", stopReason)).Should().Be(75);
    }

    [Fact]
    public async Task ReviewAsync_GivenAFailedCall_Should_NotRecordTokenMetrics()
    {
        // Arrange
        using var metrics = new MetricsRecorder();
        var sut = BuildSut(Respond(AnthropicFixtures.Error(HttpStatusCode.Unauthorized, "authentication_error", "Bad key.")));

        // Act
        var act = () => sut.ReviewAsync(_sampleRequest, CancellationToken.None);
        await act.Should().ThrowAsync<AnthropicApiException>();

        // Assert
        metrics.Measurements.Should().NotContain(measurement => measurement.Name.StartsWith("gen_ai.usage.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReviewAsync_GivenARejectedRequest_Should_NotRecordTokenMetricsBecauseNothingWasSpent()
    {
        // Arrange
        using var metrics = new MetricsRecorder();
        var sut = BuildSut(Respond(AnthropicFixtures.Error(HttpStatusCode.BadRequest, "invalid_request_error", "prompt is too long")));

        // Act
        await sut.ReviewAsync(_sampleRequest, CancellationToken.None);

        // Assert
        metrics.Measurements.Should().NotContain(measurement => measurement.Name.StartsWith("gen_ai.usage.", StringComparison.Ordinal));
    }

    // ── Checklist runs ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReviewAsync_GivenACompletedChecklist_Should_ReturnTheReadAnswerTheModelAndOneAttempt(bool isRefusalFallbackEnabled)
    {
        // Arrange — the same answer read back from either endpoint
        var answer = AnthropicFixtures.ChecklistJson(
            "Adds retries.",
            [.. AnthropicFixtures.AllChecks().Take(3), AnthropicFixtures.Check("security", "concern", "Logs the token.", isSerious: true), .. AnthropicFixtures.AllChecks().Skip(4)],
            [AnthropicFixtures.ChecklistFinding("security", "src/Retry.cs", 42, "Logs the token.", "Log the token's id instead.")]);
        var reply = AnthropicFixtures.Message([AnthropicFixtures.TextBlock(answer)], inputTokens: 1_000, outputTokens: 250, cacheReadTokens: 800, cacheCreationTokens: 50);
        var sut = BuildSut(Respond(reply), TestOptions(isRefusalFallbackEnabled: isRefusalFallbackEnabled));

        // Act
        var result = await sut.ReviewAsync(_runRequest, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(RunOutcome.Completed);
        result.Detail.Should().BeNull();
        result.Model.Should().Be(AnthropicFixtures.ModelId);
        result.Attempts.Should().Equal(new ModelAttempt(AnthropicFixtures.ModelId, new ClaudeUsage(1_000, 250, 800, 50)));
        result.Answer.Should().NotBeNull();
        result.Answer!.Summary.Should().Be("Adds retries.");
        result.Answer.Rated.Should().HaveCount(8);
        result.Answer.Rated[CheckId.Security].Should().Be(new RatedCheck(CheckId.Security, CheckStatus.Concern, "Logs the token.", IsSerious: true));
        result.Answer.Findings.Should().Equal(new ChecklistFinding(CheckId.Security, "src/Retry.cs", 42, "Logs the token.", "Log the token's id instead."));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReviewAsync_GivenARunRequest_Should_SendTheRequestedModelTheSchemaEffortAndACacheableSystemPrompt(bool isRefusalFallbackEnabled)
    {
        // Arrange — the configured model belongs to the old review; a run asks for the model its request names
        var options = TestOptions(model: "claude-opus-5-5", maxOutputTokens: 32_000, effort: "high", isRefusalFallbackEnabled: isRefusalFallbackEnabled);

        // Act
        var captured = await CaptureRunRequestAsync(options);

        // Assert
        captured.Body.GetProperty("model").GetString().Should().Be("claude-sonnet-5-5");
        captured.Body.GetProperty("max_tokens").GetInt32().Should().Be(32_000);
        var outputConfig = captured.Body.GetProperty("output_config");
        outputConfig.GetProperty("format").GetProperty("type").GetString().Should().Be("json_schema");
        JsonNode.DeepEquals(JsonNode.Parse(outputConfig.GetProperty("format").GetProperty("schema").GetRawText()), JsonNode.Parse(ChecklistSchema.Json))
            .Should().BeTrue("the schema sent is the checklist schema, whole and unchanged");
        outputConfig.GetProperty("effort").GetString().Should().Be("high");
        var system = captured.Body.GetProperty("system").EnumerateArray().Should().ContainSingle().Subject;
        system.GetProperty("text").GetString().Should().Be("CHECKLIST RULES");
        system.GetProperty("cache_control").GetProperty("type").GetString().Should().Be("ephemeral");
        captured.RawBody.Should().NotContain("\"ttl\"", "the default five-minute cache is the one Claude:Pricing prices cache writes for");
        UserText(captured.Body).Should().Be("PULL REQUEST INPUT");
        captured.Body.TryGetProperty("tools", out _).Should().BeFalse();
        captured.Body.TryGetProperty("thinking", out _).Should().BeFalse("current models think adaptively when it is left out, and refuse it turned off");
    }

    [Fact]
    public async Task ReviewAsync_GivenTheFallbackEnabled_Should_CallTheBetaEndpointWithDefaultFallbacks()
    {
        // Arrange
        var options = TestOptions(isRefusalFallbackEnabled: true);

        // Act
        var captured = await CaptureRunRequestAsync(options);

        // Assert
        BetasSent(captured.Message).Should().Contain("server-side-fallback-2026-07-01");
        captured.Body.GetProperty("fallbacks").GetString().Should().Be("default");
    }

    [Fact]
    public async Task ReviewAsync_GivenTheFallbackDisabled_Should_CallTheStandardEndpointWithoutFallbacks()
    {
        // Arrange
        var options = TestOptions(isRefusalFallbackEnabled: false);

        // Act
        var captured = await CaptureRunRequestAsync(options);

        // Assert
        captured.Message.RequestUri!.AbsolutePath.Should().Be("/v1/messages");
        captured.Message.RequestUri.Query.Should().NotContain("beta");
        BetasSent(captured.Message).Should().BeEmpty();
        captured.Body.TryGetProperty("fallbacks", out _).Should().BeFalse();
    }

    [Fact]
    public async Task ReviewAsync_GivenAFallbackBeforeAnyOutput_Should_ReportTheModelThatAnswered()
    {
        // Arrange — the requested model declined before writing anything, so the stream opens with the fallback model's name
        var reply = AnthropicFixtures.Message(
            [AnthropicFixtures.FallbackBlock("claude-sonnet-5-5", "claude-sonnet-5"), AnthropicFixtures.TextBlock(AnthropicFixtures.ChecklistJson())],
            model: "claude-sonnet-5");
        var sut = BuildSut(Respond(reply));

        // Act
        var result = await sut.ReviewAsync(_runRequest, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(RunOutcome.Completed);
        result.Model.Should().Be("claude-sonnet-5");
    }

    [Fact]
    public async Task ReviewAsync_GivenAFallbackMidStream_Should_JoinTheTextOnBothSidesOfTheFallbackBlock()
    {
        // Arrange — the requested model declined part way through the answer, and the fallback model wrote the rest after the block
        var answer = AnthropicFixtures.ChecklistJson("Finished by the fallback model.");
        var cut = answer.Length / 2;
        var reply = AnthropicFixtures.Message(
            [AnthropicFixtures.TextBlock(answer[..cut]), AnthropicFixtures.FallbackBlock("claude-sonnet-5-5", "claude-sonnet-5"), AnthropicFixtures.TextBlock(answer[cut..])]);
        var sut = BuildSut(Respond(reply));

        // Act
        var result = await sut.ReviewAsync(_runRequest, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(RunOutcome.Completed);
        result.Answer!.Summary.Should().Be("Finished by the fallback model.");
        result.Answer.Rated.Should().HaveCount(8);
    }

    [Fact]
    public async Task ReviewAsync_GivenAFallbackMidStream_Should_ReportTheFallbackModelThatServedTheAnswer()
    {
        // Arrange — the stream opened before the requested model declined, so its first event still names that model; the fallback block names
        // the model that served the rest of the answer
        var answer = AnthropicFixtures.ChecklistJson();
        var cut = answer.Length / 2;
        var reply = AnthropicFixtures.Message(
            [AnthropicFixtures.TextBlock(answer[..cut]), AnthropicFixtures.FallbackBlock("claude-sonnet-5-5", "claude-sonnet-5"), AnthropicFixtures.TextBlock(answer[cut..])],
            model: "claude-sonnet-5-5",
            iterations:
            [
                AnthropicFixtures.Iteration("message", "claude-sonnet-5-5", inputTokens: 1_000, outputTokens: 150),
                AnthropicFixtures.Iteration("fallback_message", "claude-sonnet-5", inputTokens: 1_150, outputTokens: 300)
            ]);
        var sut = BuildSut(Respond(reply));

        // Act
        var result = await sut.ReviewAsync(_runRequest, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(RunOutcome.Completed);
        result.Model.Should().Be("claude-sonnet-5");
    }

    [Fact]
    public async Task ReviewAsync_GivenUsageIterations_Should_ReturnOneAttemptPerIterationWithItsModel()
    {
        // Arrange
        var sut = BuildSut(Respond(ReplyAnsweredAfterAFallback()));

        // Act
        var result = await sut.ReviewAsync(_runRequest, CancellationToken.None);

        // Assert
        result.Attempts.Should().Equal(
            new ModelAttempt("claude-sonnet-5-5", new ClaudeUsage(1_000, 20, 300, 50)),
            new ModelAttempt("claude-sonnet-5", new ClaudeUsage(1_100, 400, 0, 900)));
        result.TotalUsage.Should().Be(new ClaudeUsage(2_100, 420, 300, 950));
    }

    [Fact]
    public async Task ReviewAsync_GivenTheWholeChainRefused_Should_ReturnRefusedWithTheCategory()
    {
        // Arrange — the requested model declined, and so did the model it fell back to
        var details = new { type = "refusal", category = "cyber", explanation = "Could enable cyber harm." };
        var reply = AnthropicFixtures.Message(
            [AnthropicFixtures.FallbackBlock("claude-sonnet-5-5", "claude-sonnet-5")],
            stopReason: "refusal",
            stopDetails: details,
            model: "claude-sonnet-5",
            iterations:
            [
                AnthropicFixtures.Iteration("message", "claude-sonnet-5-5", inputTokens: 1_000, outputTokens: 0),
                AnthropicFixtures.Iteration("message", "claude-sonnet-5", inputTokens: 1_000, outputTokens: 0)
            ]);
        var sut = BuildSut(Respond(reply));

        // Act
        var result = await sut.ReviewAsync(_runRequest, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(RunOutcome.Refused);
        result.Detail.Should().Be("cyber");
        result.Answer.Should().BeNull();
        result.Attempts.Should().HaveCount(2, "both declined attempts are part of what the run cost");
    }

    [Fact]
    public async Task ReviewAsync_GivenARefusalWithTheFallbackDisabled_Should_ReturnRefusedWithTheCategory()
    {
        // Arrange
        var details = new { type = "refusal", category = "bio", explanation = "Could enable biological harm." };
        var reply = AnthropicFixtures.Message([], stopReason: "refusal", stopDetails: details);
        var sut = BuildSut(Respond(reply), TestOptions(isRefusalFallbackEnabled: false));

        // Act
        var result = await sut.ReviewAsync(_runRequest, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(RunOutcome.Refused);
        result.Detail.Should().Be("bio");
        result.Answer.Should().BeNull();
    }

    [Theory]
    [InlineData("max_tokens", "output limit reached")]
    [InlineData("model_context_window_exceeded", "context window exceeded")]
    public async Task ReviewAsync_GivenTheOutputLimitOrContextWindow_Should_ReturnOutputTruncated(string stopReason, string expectedDetail)
    {
        // Arrange — the answer is cut mid-JSON, as it is when the output limit or the context window runs out
        var truncated = AnthropicFixtures.ChecklistJson()[..60];
        var sut = BuildSut(Respond(AnthropicFixtures.Message([AnthropicFixtures.TextBlock(truncated)], stopReason: stopReason)));

        // Act
        var result = await sut.ReviewAsync(_runRequest, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(RunOutcome.OutputTruncated);
        result.Detail.Should().Be(expectedDetail);
        result.Answer.Should().BeNull();
    }

    [Fact]
    public async Task ReviewAsync_GivenAnAnswerThatRatesNoCheck_Should_ReturnInvalidOutput()
    {
        // Arrange — JSON of the right shape whose checks list is empty: a run that assessed nothing must not give a verdict
        var answer = AnthropicFixtures.ChecklistJson(checks: []);
        var sut = BuildSut(Respond(AnthropicFixtures.Message([AnthropicFixtures.TextBlock(answer)])));

        // Act
        var result = await sut.ReviewAsync(_runRequest, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(RunOutcome.InvalidOutput);
        result.Detail.Should().Be("The answer rated none of the eight checks.");
        result.Answer.Should().BeNull();
    }

    [Theory]
    [InlineData("", "The answer was empty.")]
    [InlineData("   ", "The answer was empty.")]
    [InlineData("{ \"summary\": ", "The answer was not valid JSON.")]
    [InlineData("Looks good to me.", "The answer was not valid JSON.")]
    [InlineData("[\"pass\", \"pass\"]", "The answer was not a JSON object.")]
    [InlineData("{\"summary\":\"s\",\"checks\":[{\"id\":\"not-a-check\",\"status\":\"pass\",\"note\":\"\",\"serious\":false}],\"findings\":[],\"coverage_note\":\"\"}", "The answer rated none of the eight checks.")]
    public async Task ReviewAsync_GivenAnAnswerTheReaderCannotUse_Should_ReturnInvalidOutputWithTheReadersReason(string answer, string expectedDetail)
    {
        // Arrange — the reader's fifth reason, text that is not valid Unicode, cannot come this far: the SDK refuses such a stream before there is
        // an answer to read (AnthropicSdkContractTests)
        var sut = BuildSut(Respond(AnthropicFixtures.Message([AnthropicFixtures.TextBlock(answer)])));

        // Act
        var result = await sut.ReviewAsync(_runRequest, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(RunOutcome.InvalidOutput);
        result.Detail.Should().Be(expectedDetail);
        result.Answer.Should().BeNull();
    }

    [Theory]
    [InlineData(400)]
    [InlineData(413)]
    [InlineData(422)]
    public async Task ReviewAsync_GivenTheRequestIsRefusedAsInvalid_Should_ReturnRejected(int status)
    {
        // Arrange — the same request would be refused on every attempt, so this ends the run instead of failing it for a retry
        var sut = BuildSut(Respond(AnthropicFixtures.Error((HttpStatusCode)status, "invalid_request_error", "prompt is too long")));

        // Act
        var result = await sut.ReviewAsync(_runRequest, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(RunOutcome.Rejected);
        result.Detail.Should().Be($"HTTP {status}: prompt is too long");
        result.Model.Should().BeEmpty("no model ran, so none answered");
        result.Attempts.Should().BeEmpty("nothing ran, so nothing was spent");
        result.Answer.Should().BeNull();
    }

    [Fact]
    public async Task ReviewAsync_GivenAnOverloadedStream_Should_Throw()
    {
        // Arrange — the API reports an overload inside a stream that already started with a 200
        var sut = BuildSut(Respond(AnthropicFixtures.StreamWithAnErrorEvent()));

        // Act
        var act = () => sut.ReviewAsync(_runRequest, CancellationToken.None);

        // Assert
        var failure = (await act.Should().ThrowAsync<Exception>()).Which;
        FailureClassifier.IsTransient(failure).Should().BeTrue($"{failure.GetType().Name} is a dropped stream, which the queue retries");
    }

    [Fact]
    public async Task ReviewAsync_GivenARunWhoseStreamNeverReportsAStopReason_Should_FailWithAnErrorThatIsWorthRetrying()
    {
        // Arrange — text that reads as an answer is no proof the reply was whole when the model never said why it stopped
        var sut = BuildSut(Respond(AnthropicFixtures.StreamWithoutAStopReason()));

        // Act
        var act = () => sut.ReviewAsync(_runRequest, CancellationToken.None);

        // Assert
        var failure = (await act.Should().ThrowAsync<Exception>()).Which;
        FailureClassifier.IsTransient(failure).Should().BeTrue($"{failure.GetType().Name} is a reply that did not finish properly");
    }

    [Fact]
    public async Task ReviewAsync_GivenARunWhoseReplyStallsAfterItStarts_Should_ThrowATimeoutWhenTheBudgetRunsOut()
    {
        // Arrange
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var time = new FakeTimeProvider();
        var sut = BuildSut(Respond(AnthropicFixtures.StreamThatStallsAfterItsFirstEvent(stalled)), TestOptions(requestTimeoutSeconds: 30), timeProvider: time);

        // Act
        var run = sut.ReviewAsync(_runRequest, CancellationToken.None);
        await stalled.Task.WaitAsync(_hangGuard);
        time.Advance(TimeSpan.FromSeconds(31));
        var act = () => run.WaitAsync(_hangGuard);

        // Assert
        (await act.Should().ThrowAsync<TimeoutException>()).Which.Message.Should().Contain("within the 30-second limit");
    }

    [Fact]
    public async Task ReviewAsync_GivenAFinishedRun_Should_CountItByOutcomeAndModel()
    {
        // Arrange — answered by the fallback model, which is the model the run is counted for
        using var metrics = new MetricsRecorder();
        var sut = BuildSut(Respond(ReplyAnsweredAfterAFallback()));

        // Act
        await sut.ReviewAsync(_runRequest, CancellationToken.None);

        // Assert
        metrics.Sum("github_review_app.review.runs").Should().Be(1);
        metrics.Sum("github_review_app.review.runs", ("outcome", "Completed"), ("model", "claude-sonnet-5")).Should().Be(1);
    }

    [Fact]
    public async Task ReviewAsync_GivenARejectedRun_Should_CountItUnderTheModelItAskedFor()
    {
        // Arrange — no model answered a request the service refused, so the count names the model that was asked
        using var metrics = new MetricsRecorder();
        var sut = BuildSut(Respond(AnthropicFixtures.Error(HttpStatusCode.BadRequest, "invalid_request_error", "prompt is too long")));

        // Act
        await sut.ReviewAsync(_runRequest, CancellationToken.None);

        // Assert
        metrics.Sum("github_review_app.review.runs", ("outcome", "Rejected"), ("model", "claude-sonnet-5-5")).Should().Be(1);
    }

    [Fact]
    public async Task ReviewAsync_GivenUsageIterations_Should_RecordTheTokensOfEveryAttemptInTheTokenMetrics()
    {
        // Arrange
        using var metrics = new MetricsRecorder();
        var sut = BuildSut(Respond(ReplyAnsweredAfterAFallback()));

        // Act
        await sut.ReviewAsync(_runRequest, CancellationToken.None);

        // Assert
        metrics.Sum("gen_ai.usage.input_tokens").Should().Be(2_100);
        metrics.Sum("gen_ai.usage.output_tokens").Should().Be(420);
        metrics.Sum("gen_ai.usage.cache_read_input_tokens").Should().Be(300);
        metrics.Sum("gen_ai.usage.cache_creation_input_tokens").Should().Be(950);
    }

    [Fact]
    public async Task ReviewAsync_GivenARunAnsweredByAFallbackModel_Should_TagTheSpanWithTheRequestedAndTheAnsweringModel()
    {
        // Arrange — the configured model belongs to the old review, so the requested model can only come from the run's request
        using var spans = new SpanRecorder(ActivitySources.ClaudeServiceName);
        var sut = BuildSut(Respond(ReplyAnsweredAfterAFallback()), TestOptions(model: "claude-opus-5-5", maxOutputTokens: 32_000));

        // Act
        await sut.ReviewAsync(_runRequest, CancellationToken.None);

        // Assert
        var span = spans.Spans.Should().ContainSingle().Subject;
        span.GetTagItem("gen_ai.request.model").Should().Be("claude-sonnet-5-5");
        span.GetTagItem("gen_ai.response.model").Should().Be("claude-sonnet-5");
        span.GetTagItem("gen_ai.request.max_tokens").Should().Be(32_000);
        span.GetTagItem("gen_ai.usage.input_tokens").Should().Be(2_100L);
        span.Status.Should().NotBe(ActivityStatusCode.Error);
    }

    [Theory]
    [InlineData("refusal")]
    [InlineData("max_tokens")]
    [InlineData("model_context_window_exceeded")]
    [InlineData("end_turn")]
    public async Task ReviewAsync_GivenARunThatDidNotComplete_Should_MarkTheSpanAsAnError(string stopReason)
    {
        // Arrange — with end_turn the text is not an answer, so the run ends with invalid output
        using var spans = new SpanRecorder(ActivitySources.ClaudeServiceName);
        var sut = BuildSut(Respond(AnthropicFixtures.Message([AnthropicFixtures.TextBlock("{\"summ")], stopReason: stopReason)));

        // Act
        await sut.ReviewAsync(_runRequest, CancellationToken.None);

        // Assert
        spans.Spans.Should().ContainSingle().Which.Status.Should().Be(ActivityStatusCode.Error);
    }
}
