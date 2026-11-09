namespace GitHubReviewApp.Tests.Services;

[Collection(TelemetryCollection.Name)]
public class ClaudeServiceTests
{
    private const string FakeApiKey = "sk-ant-test-key-12345";

    // A call that hangs must fail the test, not hang the run.
    private static readonly TimeSpan _hangGuard = TimeSpan.FromSeconds(20);

    private static readonly ClaudeRunRequest _runRequest = new("claude-sonnet-5-5", "CHECKLIST RULES", "PULL REQUEST INPUT");

    private sealed record CapturedRequest(JsonElement Body, string RawBody, HttpRequestMessage Message);

    // Retries are off so error-path tests do not wait for the SDK's backoff. The refusal fallback is on, as it ships.
    private static ClaudeOptions TestOptions(
        int maxOutputTokens = 16_000,
        string? effort = "high",
        int requestTimeoutSeconds = 30,
        int maxRetries = 0,
        bool isRefusalFallbackEnabled = true)
    {
        return new ClaudeOptions
        {
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
            .AddInMemoryCollection(new Dictionary<string, string?> { ["GitHubReviewAppAnthropicApiKey"] = apiKey })
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

    [Theory]
    [InlineData("low", "low", true)]
    [InlineData("medium", "medium", true)]
    [InlineData("HIGH", "high", true)]
    [InlineData("xhigh", "xhigh", true)]
    [InlineData("max", "max", true)]
    [InlineData("low", "low", false)]
    [InlineData("medium", "medium", false)]
    [InlineData("HIGH", "high", false)]
    [InlineData("xhigh", "xhigh", false)]
    [InlineData("max", "max", false)]
    public async Task ReviewAsync_GivenAnEffortLevel_Should_SendItExplicitly(string configured, string expectedOnTheWire, bool isRefusalFallbackEnabled)
    {
        // Arrange — with the refusal fallback on this is the beta request, whose effort type is its own; off, it is the standard request
        var options = TestOptions(effort: configured, isRefusalFallbackEnabled: isRefusalFallbackEnabled);

        // Act
        var captured = await CaptureRequestAsync(options);

        // Assert
        captured.Body.GetProperty("output_config").GetProperty("effort").GetString().Should().Be(expectedOnTheWire);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public async Task ReviewAsync_GivenNoEffortConfigured_Should_OmitTheParameterForModelsThatRejectIt(string? configured, bool isRefusalFallbackEnabled)
    {
        // Arrange — on the beta request and on the standard one alike
        var options = TestOptions(effort: configured, isRefusalFallbackEnabled: isRefusalFallbackEnabled);

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
        var act = () => sut.ReviewAsync(_runRequest, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*turbo*");
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

    // ── Answer ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReviewAsync_GivenAThinkingBlockBeforeTheAnswer_Should_ReadTheTextBlockAndNotPostAnEmptyReview()
    {
        // Arrange — current models think by default; with display omitted the first block has empty text
        var content = new[] { AnthropicFixtures.ThinkingBlock(), AnthropicFixtures.TextBlock(AnthropicFixtures.ChecklistJson("After thinking.")) };
        var sut = BuildSut(Respond(AnthropicFixtures.Message(content)));

        // Act
        var result = await sut.ReviewAsync(_runRequest, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(RunOutcome.Completed);
        result.Answer!.Summary.Should().Be("After thinking.");
    }

    [Fact]
    public async Task ReviewAsync_GivenARedactedThinkingBlockBeforeTheAnswer_Should_SkipItAndReadTheTextBlock()
    {
        // Arrange — when the API redacts the model's reasoning it sends opaque data and no text
        var content = new[] { AnthropicFixtures.RedactedThinkingBlock(), AnthropicFixtures.TextBlock(AnthropicFixtures.ChecklistJson("After redaction.")) };
        var sut = BuildSut(Respond(AnthropicFixtures.Message(content)));

        // Act
        var result = await sut.ReviewAsync(_runRequest, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(RunOutcome.Completed);
        result.Answer!.Summary.Should().Be("After redaction.");
    }

    [Fact]
    public async Task ReviewAsync_GivenOnlyARedactedThinkingBlockAndNoAnswer_Should_ReturnInvalidOutputNotAnEmptyReview()
    {
        // Arrange
        var sut = BuildSut(Respond(AnthropicFixtures.Message([AnthropicFixtures.RedactedThinkingBlock()])));

        // Act
        var result = await sut.ReviewAsync(_runRequest, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(RunOutcome.InvalidOutput);
        result.Answer.Should().BeNull();
    }

    [Fact]
    public async Task ReviewAsync_GivenNoContentAtAll_Should_ReturnInvalidOutput()
    {
        // Arrange
        var sut = BuildSut(Respond(AnthropicFixtures.Message([])));

        // Act
        var result = await sut.ReviewAsync(_runRequest, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(RunOutcome.InvalidOutput);
        result.Detail.Should().Be("The answer was empty.");
    }

    [Fact]
    public async Task ReviewAsync_GivenTheAnswerSplitAcrossTextBlocks_Should_JoinThemBeforeReadingIt()
    {
        // Arrange
        var json = AnthropicFixtures.ChecklistJson("Joined summary.");
        var half = json.Length / 2;
        var content = new[] { AnthropicFixtures.TextBlock(json[..half]), AnthropicFixtures.TextBlock(json[half..]) };
        var sut = BuildSut(Respond(AnthropicFixtures.Message(content)));

        // Act
        var result = await sut.ReviewAsync(_runRequest, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(RunOutcome.Completed);
        result.Answer!.Summary.Should().Be("Joined summary.");
    }

    [Fact]
    public async Task ReviewAsync_GivenNonAsciiText_Should_ReturnItIntactAcrossTheStreamedPieces()
    {
        // Arrange — the fixture streams every text block as two deltas, so this also proves they are joined without damage
        const string summary = "Añade reintentos ✓ 日本語 😀 end";
        var sut = BuildSut(Respond(AnthropicFixtures.Message([AnthropicFixtures.TextBlock(AnthropicFixtures.ChecklistJson(summary))])));

        // Act
        var result = await sut.ReviewAsync(_runRequest, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(RunOutcome.Completed);
        result.Answer!.Summary.Should().Be(summary);
    }

    [Fact]
    public async Task ReviewAsync_GivenTextInOtherLanguagesAndCharactersJsonWritersEscape_Should_ReadItWithoutDamageEvenWhenItArrivesOneByteAtATime()
    {
        // Arrange — characters of two, three and four bytes, and the ones another writer would turn into escapes, with every multi-byte one split across reads
        const string summary = "Fügt <Wiederholung> & »Zeitlimit« hinzu: 日本語のコメント 😀";
        const string issue = "Das Ergebnis kann leer sein, wenn der Cache kalt ist: naïve café ✓";
        var answer = AnthropicFixtures.ChecklistJson(summary, findings: [AnthropicFixtures.ChecklistFinding(issue: issue, fix: summary)]);
        var sut = BuildSut(Respond(AnthropicFixtures.Message([AnthropicFixtures.TextBlock(answer)], bytesPerRead: 1)));

        // Act
        var result = await sut.ReviewAsync(_runRequest, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(RunOutcome.Completed);
        result.Answer!.Summary.Should().Be(summary);
        var finding = result.Answer.Findings.Should().ContainSingle().Subject;
        finding.Issue.Should().Be(issue);
        finding.Fix.Should().Be(summary);
    }

    [Fact]
    public async Task ReviewAsync_GivenAStopReasonThisCodeHasNeverSeen_Should_ReadTheCompleteAnswerAnyway()
    {
        // Arrange — the API adds stop reasons over time; one this code does not know must not turn a good answer into a failure
        var answer = AnthropicFixtures.ChecklistJson("Complete despite the odd reason.");
        var sut = BuildSut(Respond(AnthropicFixtures.Message([AnthropicFixtures.TextBlock(answer)], stopReason: "a_future_stop_reason")));

        // Act
        var result = await sut.ReviewAsync(_runRequest, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(RunOutcome.Completed);
        result.Answer!.Summary.Should().Be("Complete despite the odd reason.");
    }

    [Fact]
    public async Task ReviewAsync_GivenARefusalWithoutDetails_Should_StillReturnRefused()
    {
        // Arrange
        var sut = BuildSut(Respond(AnthropicFixtures.Message([], stopReason: "refusal")));

        // Act
        var result = await sut.ReviewAsync(_runRequest, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(RunOutcome.Refused);
        result.Detail.Should().BeNull();
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
        await sut.ReviewAsync(_runRequest, CancellationToken.None);

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
        await sut.ReviewAsync(_runRequest, CancellationToken.None);

        // Assert
        logger.Entries.Should().Contain(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("context window", StringComparison.Ordinal));
        logger.Entries.Should().NotContain(entry => entry.Message.Contains("output limit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReviewAsync_GivenUsageWithoutCacheFields_Should_CountTheCacheTokensAsZero()
    {
        // Arrange — a reply with no prompt caching in play carries no cache counters at all
        var sut = BuildSut(Respond(AnthropicFixtures.Message(inputTokens: 1_000, outputTokens: 250, isCacheUsageOmitted: true)));

        // Act
        var result = await sut.ReviewAsync(_runRequest, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(RunOutcome.Completed);
        result.TotalUsage.Should().Be(new ClaudeUsage(1_000, 250, 0, 0));
    }

    [Fact]
    public async Task ReviewAsync_GivenUsageFiguresThatTheFinalEventRestates_Should_ReportTheFinalOnesNotTheProvisionalOnes()
    {
        // Arrange — the API documents its counts as cumulative, so the first event can say less than the last: here one input token and no caching
        var sut = BuildSut(Respond(AnthropicFixtures.Message(
            inputTokens: 1_200, outputTokens: 300, cacheReadTokens: 800, cacheCreationTokens: 50, isUsageRestatedInFinalEvent: true)));

        // Act
        var result = await sut.ReviewAsync(_runRequest, CancellationToken.None);

        // Assert
        result.TotalUsage.Should().Be(new ClaudeUsage(1_200, 300, 800, 50));
    }

    // ── Requests the service refuses ─────────────────────────────────────────

    [Fact]
    public async Task ReviewAsync_GivenAnExplanationOverThreeHundredCharacters_Should_CutItThereAndMarkTheCut()
    {
        // Arrange — the detail goes to the logs and a span, so a service that sends a page of text must not fill them
        var explanation = new string('x', 300) + "TAIL";
        var sut = BuildSut(Respond(AnthropicFixtures.Error(HttpStatusCode.BadRequest, "invalid_request_error", explanation)));

        // Act
        var result = await sut.ReviewAsync(_runRequest, CancellationToken.None);

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
        var result = await sut.ReviewAsync(_runRequest, CancellationToken.None);

        // Assert
        result.Detail.Should().Be("HTTP 400: " + explanation);
    }

    [Fact]
    public async Task ReviewAsync_GivenAnExplanationSpreadOverSeveralLines_Should_ReadAsOneLineWithoutTheEdgeWhitespace()
    {
        // Arrange — a line break would split the one-line log entry
        var sut = BuildSut(Respond(AnthropicFixtures.Error(HttpStatusCode.BadRequest, "invalid_request_error", "\r\nfirst line\r\nsecond line\nthird line\n")));

        // Act
        var result = await sut.ReviewAsync(_runRequest, CancellationToken.None);

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
        var result = await sut.ReviewAsync(_runRequest, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(RunOutcome.Rejected);
        result.Detail.Should().Be("HTTP 400");
    }

    [Theory]
    [InlineData(401, "authentication_error")]
    [InlineData(403, "permission_error")]
    [InlineData(404, "not_found_error")]
    public async Task ReviewAsync_GivenACredentialOrModelProblem_Should_StillThrowBecauseNoRunOfAnyReviewCanSucceed(int status, string errorType)
    {
        // Arrange
        var sut = BuildSut(Respond(AnthropicFixtures.Error((HttpStatusCode)status, errorType, "Check the key or the model name.")));

        // Act
        var act = () => sut.ReviewAsync(_runRequest, CancellationToken.None);

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
        var act = () => sut.ReviewAsync(_runRequest, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*GitHubReviewAppAnthropicApiKey*");
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
        var act = () => sut.ReviewAsync(_runRequest, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*GitHubReviewAppAnthropicApiKey*");
    }

    [Fact]
    public async Task ReviewAsync_GivenTheApiKeyAppearsAfterAFailedAttempt_Should_UseItOnTheNextCallWithoutARestart()
    {
        // Arrange — a failed creation of the client is not remembered, so fixing the secret takes effect at once
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["GitHubReviewAppAnthropicApiKey"] = null }).Build();
        var sut = new ClaudeService(
            new HttpClient(Respond(AnthropicFixtures.Message())), configuration, Options.Create(TestOptions()), NullLogger<ClaudeService>.Instance);
        var firstAttempt = () => sut.ReviewAsync(_runRequest, CancellationToken.None);
        await firstAttempt.Should().ThrowAsync<InvalidOperationException>();

        // Act
        configuration["GitHubReviewAppAnthropicApiKey"] = FakeApiKey;
        var result = await sut.ReviewAsync(_runRequest, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(RunOutcome.Completed);
    }

    [Fact]
    public async Task ReviewAsync_GivenRateLimiting_Should_ThrowTheSdksRateLimitException()
    {
        // Arrange
        var sut = BuildSut(Respond(AnthropicFixtures.Error(HttpStatusCode.TooManyRequests, "rate_limit_error", "Slow down.")));

        // Act
        var act = () => sut.ReviewAsync(_runRequest, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<AnthropicRateLimitException>();
    }

    [Fact]
    public async Task ReviewAsync_GivenAnOverloadedApi_Should_ThrowAServerErrorException()
    {
        // Arrange
        var sut = BuildSut(Respond(AnthropicFixtures.Error((HttpStatusCode)529, "overloaded_error", "Overloaded")));

        // Act
        var act = () => sut.ReviewAsync(_runRequest, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<Anthropic5xxException>();
    }

    [Fact]
    public async Task ReviewAsync_GivenTheStreamEndsBeforeTheModelFinishes_Should_FailWithAnErrorThatIsWorthRetryingInsteadOfReadingAHalfReply()
    {
        // Arrange — the connection closed early, so no stop reason ever arrived; the fallback is on, so this is the beta stream
        var sut = BuildSut(Respond(AnthropicFixtures.StreamThatEndsBeforeTheModelFinishes()));

        // Act
        var act = () => sut.ReviewAsync(_runRequest, CancellationToken.None);

        // Assert
        var failure = (await act.Should().ThrowAsync<IOException>()).Which;
        FailureClassifier.IsTransient(failure).Should().BeTrue("a reply cut off in transit is worth asking for again");
    }

    [Fact]
    public async Task ReviewAsync_GivenACancelledToken_Should_ThrowOperationCanceled()
    {
        // Arrange
        var sut = BuildSut(Respond(AnthropicFixtures.Message()));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // Act
        var act = () => sut.ReviewAsync(_runRequest, cancellation.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ReviewAsync_GivenAnEventTheSdkCannotRead_Should_NotRelabelItAsATransientFailure()
    {
        // Arrange — a reply in a format this code does not understand is the same on every attempt, unlike a stream that merely ended early
        var sut = BuildSut(Respond(AnthropicFixtures.StreamWithAnEventThatIsNotJson()));

        // Act
        var act = () => sut.ReviewAsync(_runRequest, CancellationToken.None);

        // Assert
        var failure = (await act.Should().ThrowAsync<AnthropicInvalidDataException>()).Which;
        FailureClassifier.IsTransient(failure).Should().BeFalse();
    }

    // ── Time limit ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ReviewAsync_GivenTheReplyStallsButTheBudgetHasNotRunOut_Should_KeepWaiting()
    {
        // Arrange — the limit must be the configured one, not something shorter. The stalled call is cancelled at the end so it does not run on
        // into the rest of the suite until the SDK's real-clock timeout faults it.
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var time = new FakeTimeProvider();
        var sut = BuildSut(Respond(AnthropicFixtures.StreamThatStallsAfterItsFirstEvent(stalled)), TestOptions(requestTimeoutSeconds: 30), timeProvider: time);
        using var cancellation = new CancellationTokenSource();

        // Act
        var run = sut.ReviewAsync(_runRequest, cancellation.Token);
        await stalled.Task.WaitAsync(_hangGuard);
        time.Advance(TimeSpan.FromSeconds(29));
        await Settle.QueuedWorkAsync();
        var isCompletedWithOneSecondLeft = run.IsCompleted;
        await cancellation.CancelAsync();
        var act = () => run.WaitAsync(_hangGuard);

        // Assert
        isCompletedWithOneSecondLeft.Should().BeFalse("the budget has one second left");
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ReviewAsync_GivenTheCallerCancelsWhileTheReplyIsStalled_Should_ThrowCancellationNotATimeout()
    {
        // Arrange — a host shutdown must read as a shutdown, so the message is retried rather than reported as a model that was too slow
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sut = BuildSut(Respond(AnthropicFixtures.StreamThatStallsAfterItsFirstEvent(stalled)), timeProvider: new FakeTimeProvider());
        using var cancellation = new CancellationTokenSource();

        // Act
        var run = sut.ReviewAsync(_runRequest, cancellation.Token);
        await stalled.Task.WaitAsync(_hangGuard);
        await cancellation.CancelAsync();
        var act = () => run.WaitAsync(_hangGuard);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ── Retries ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReviewAsync_GivenOneRetryConfigured_Should_SendTheRequestAgainAfterAnOverloadAndReturnTheAnswer()
    {
        // Arrange
        var handler = RespondInOrder(
            AnthropicFixtures.Error((HttpStatusCode)529, "overloaded_error", "Overloaded"),
            AnthropicFixtures.Message([AnthropicFixtures.TextBlock(AnthropicFixtures.ChecklistJson("Second try."))]));
        var sut = BuildSut(handler, TestOptions(maxRetries: 1));

        // Act
        var result = await sut.ReviewAsync(_runRequest, CancellationToken.None);

        // Assert
        handler.Requests.Should().HaveCount(2);
        result.Outcome.Should().Be(RunOutcome.Completed);
        result.Answer!.Summary.Should().Be("Second try.");
    }

    [Fact]
    public async Task ReviewAsync_GivenNoRetriesConfigured_Should_SendTheRequestOnlyOnceBecauseTheSdkDefaultWouldRetryTwice()
    {
        // Arrange
        var handler = Respond(AnthropicFixtures.Error((HttpStatusCode)529, "overloaded_error", "Overloaded"));
        var sut = BuildSut(handler, TestOptions(maxRetries: 0));

        // Act
        var act = () => sut.ReviewAsync(_runRequest, CancellationToken.None);

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
        await sut.ReviewAsync(_runRequest, CancellationToken.None);

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
        var act = () => sut.ReviewAsync(_runRequest, CancellationToken.None);
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
        var run = sut.ReviewAsync(_runRequest, CancellationToken.None);
        await stalled.Task.WaitAsync(_hangGuard);
        time.Advance(TimeSpan.FromSeconds(31));
        var act = () => run.WaitAsync(_hangGuard);
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
        var result = await sut.ReviewAsync(_runRequest, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(RunOutcome.Rejected);
        var span = spans.Spans.Should().ContainSingle().Subject;
        span.Status.Should().Be(ActivityStatusCode.Error);
        span.GetTagItem("http.response.status_code").Should().Be(400);
    }

    [Theory]
    [InlineData("end_turn")]
    [InlineData("max_tokens")]
    [InlineData("a_future_stop_reason")]
    public async Task ReviewAsync_GivenAnyStopReason_Should_TagTheTokenMetricsWithItSoTruncationAndSpendCanBeSeenTogether(string stopReason)
    {
        // Arrange
        using var metrics = new MetricsRecorder();
        var sut = BuildSut(Respond(AnthropicFixtures.Message([AnthropicFixtures.TextBlock(AnthropicFixtures.ChecklistJson())], stopReason: stopReason, outputTokens: 75)));

        // Act
        await sut.ReviewAsync(_runRequest, CancellationToken.None);

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
        var act = () => sut.ReviewAsync(_runRequest, CancellationToken.None);
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
        await sut.ReviewAsync(_runRequest, CancellationToken.None);

        // Assert
        metrics.Measurements.Should().NotContain(measurement => measurement.Name.StartsWith("gen_ai.usage.", StringComparison.Ordinal));
    }

    // ── Outcomes and the refusal fallback ────────────────────────────────────

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
        // Arrange — a run asks for the model its request names
        var options = TestOptions(maxOutputTokens: 32_000, effort: "high", isRefusalFallbackEnabled: isRefusalFallbackEnabled);

        // Act
        var captured = await CaptureRequestAsync(options);

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
        var captured = await CaptureRequestAsync(options);

        // Assert
        // The SDK sends the beta request to the same path as the standard one, with no query: the beta is named in the header alone.
        var requestUri = captured.Message.RequestUri!;
        requestUri.AbsolutePath.Should().Be("/v1/messages");
        requestUri.Query.Should().BeEmpty();
        BetasSent(captured.Message).Should().Contain("server-side-fallback-2026-07-01");
        captured.Body.GetProperty("fallbacks").GetString().Should().Be("default");
    }

    [Fact]
    public async Task ReviewAsync_GivenTheFallbackDisabled_Should_CallTheStandardEndpointWithoutFallbacks()
    {
        // Arrange
        var options = TestOptions(isRefusalFallbackEnabled: false);

        // Act
        var captured = await CaptureRequestAsync(options);

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
        // Arrange — the requested model comes from the run's request, the answering one from the reply
        using var spans = new SpanRecorder(ActivitySources.ClaudeServiceName);
        var sut = BuildSut(Respond(ReplyAnsweredAfterAFallback()), TestOptions(maxOutputTokens: 32_000));

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
