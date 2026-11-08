namespace GitHubReviewApp.Services;

/// <summary>
/// Sends review requests to Claude through the official Anthropic SDK. The model, output limit and effort come from configuration. The reply is
/// streamed (a long review on a silent connection is what request timeouts cut off), requested as schema-constrained JSON, read by content-block
/// type (current models may put a thinking block first), and checked for refusals and truncation. One time budget covers the whole call. A checklist
/// run (design 5.5) asks for the model its request names and, with <see cref="ClaudeOptions.IsRefusalFallbackEnabled"/>, goes through the beta
/// endpoint with Anthropic's server-side refusal fallback.
/// </summary>
public class ClaudeService : IClaudeService
{
    private const string ApiKeySecretName = "AnthropicApiKey";
    private const int MaxRejectionReasonChars = 300;
    private const string RefusalFallbackBeta = "server-side-fallback-2026-07-01";

    // The SDK's own limit covers only the wait for the response headers of each attempt: a streamed reply keeps arriving after them, and a stream that
    // goes quiet is not something it times out. So the budget in WithinBudgetAsync is what ends a stalled stream, and the whole call, retries
    // included. The SDK's limit is set a little longer than the budget, so a connection that never answers at all is ended by the budget, which says
    // so, and not by the SDK's own message in a race with it.
    private static readonly TimeSpan _sdkAttemptMargin = TimeSpan.FromSeconds(15);

    private static readonly IReadOnlyDictionary<string, JsonElement> _reviewSchema =
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(ReviewResponseSchema.Json)!;

    private static readonly IReadOnlyDictionary<string, JsonElement> _checklistSchema =
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(ChecklistSchema.Json)!;

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _config;
    private readonly ClaudeOptions _options;
    private readonly ILogger<ClaudeService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Lazy<AnthropicClient> _client;

    public ClaudeService(
        HttpClient httpClient,
        IConfiguration config,
        IOptions<ClaudeOptions> options,
        ILogger<ClaudeService> logger,
        TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient;
        _config = config;
        _options = options.Value;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;

        // The time limit is enforced by this class and the SDK, so the HttpClient's default 100 second limit must not cut a long review short.
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;

        // Created on first use so a missing key fails inside the review (and is reported), not at start-up.
        // PublicationOnly: a failed creation is not cached, so fixing the secret takes effect immediately.
        _client = new Lazy<AnthropicClient>(CreateClient, LazyThreadSafetyMode.PublicationOnly);
    }

    /// <summary>
    /// What a checklist run's reply says, from either endpoint: why the model stopped, the refusal category, the text of every text block in order
    /// (a mid-stream fallback leaves the answer split around its fallback block), the model that answered and every attempt the API made.
    /// </summary>
    private sealed record RunReply(string StopReason, string? RefusalCategory, string Text, string Model, IReadOnlyList<ModelAttempt> Attempts);

    public async Task<ClaudeReviewResult> ReviewAsync(ClaudeReviewRequest request, CancellationToken cancellationToken)
    {
        using var activity = ActivitySources.ClaudeService.StartActivity("chat", ActivityKind.Client);
        activity?.SetTag("gen_ai.system", "anthropic");
        activity?.SetTag("gen_ai.operation.name", "chat");
        activity?.SetTag("gen_ai.request.model", _options.Model);
        activity?.SetTag("gen_ai.request.max_tokens", _options.MaxOutputTokens);

        try
        {
            var parameters = BuildParameters(_options.Model, _reviewSchema, request.SystemPrompt, request.UserMessage);

            _logger.LogInformation("Calling Claude ({Model}) for review.", _options.Model);
            var message = await WithinBudgetAsync(
                token => _client.Value.Messages.CreateStreaming(parameters, token).Aggregate(), cancellationToken);

            return ToResult(message, activity);
        }
        catch (AnthropicApiException exception) when (IsRejectedAsInvalid(exception.StatusCode))
        {
            return ToRejectedResult(exception, activity);
        }
        catch (Exception exception)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            activity?.AddException(exception);
            throw;
        }
    }

    public async Task<ClaudeRunResult> ReviewAsync(ClaudeRunRequest request, CancellationToken cancellationToken)
    {
        using var activity = ActivitySources.ClaudeService.StartActivity("chat", ActivityKind.Client);
        activity?.SetTag("gen_ai.system", "anthropic");
        activity?.SetTag("gen_ai.operation.name", "chat");
        activity?.SetTag("gen_ai.request.model", request.Model);
        activity?.SetTag("gen_ai.request.max_tokens", _options.MaxOutputTokens);

        try
        {
            _logger.LogInformation(
                "Calling Claude ({Model}) for a checklist run, refusal fallback {RefusalFallback}.",
                request.Model, _options.IsRefusalFallbackEnabled ? "on" : "off");

            var reply = _options.IsRefusalFallbackEnabled
                ? ToRunReply(await WithinBudgetAsync(
                    token => _client.Value.Beta.Messages.CreateStreaming(BuildFallbackParameters(request), token).Aggregate(), cancellationToken))
                : ToRunReply(await WithinBudgetAsync(
                    token => _client.Value.Messages.CreateStreaming(
                        BuildParameters(request.Model, _checklistSchema, request.SystemPrompt, request.UserMessage), token).Aggregate(),
                    cancellationToken));

            return ToRunResult(reply, activity);
        }
        catch (AnthropicApiException exception) when (IsRejectedAsInvalid(exception.StatusCode))
        {
            return ToRejectedRunResult(request, exception, activity);
        }
        catch (Exception exception)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            activity?.AddException(exception);
            throw;
        }
    }

    private async Task<T> WithinBudgetAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        // One budget for the whole call: connecting, the SDK's retries and every event of the reply. The SDK's own limit covers a single attempt, and a
        // stream that goes quiet part way through would otherwise hold the review until the host gave up on it.
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds), _timeProvider);
        using var limited = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);

        try
        {
            return await call(limited.Token);
        }
        catch (Exception exception) when (budget.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Our own limit ran out (the caller did not cancel), however the SDK chose to report it. A cancellation from the caller is left alone:
            // it means the host is shutting down, which is not the model being slow.
            throw new TimeoutException(
                $"Claude did not finish the review request within the {_options.RequestTimeoutSeconds}-second limit.", exception);
        }
        catch (AnthropicInvalidDataException exception) when (IsStreamCutOff(exception))
        {
            // The request was fine and the reply was lost in transit, so it is worth retrying; as the SDK's own exception type it would read as a
            // fault in the data, which no retry would clear.
            throw new IOException("The reply from Claude ended before the model finished.", exception);
        }
    }

    /// <summary>
    /// The SDK throws this type for a stream that ends before its last event: with no inner exception, and naming the End message it did not get. It
    /// throws the same type for an event it cannot read, with the parse error inside, and that one is a reply in a format this code does not
    /// understand, which a retry would meet again. The two are told apart by what the SDK puts in them; <c>AnthropicSdkContractTests</c> pins it.
    /// </summary>
    private static bool IsStreamCutOff(AnthropicInvalidDataException exception)
    {
        return exception.InnerException is null && exception.Message.Contains("End message", StringComparison.Ordinal);
    }

    private AnthropicClient CreateClient()
    {
        var apiKey = _config[ApiKeySecretName];

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException($"Secret '{ApiKeySecretName}' is not configured.");
        }

        return new AnthropicClient
        {
            ApiKey = apiKey,
            HttpClient = _httpClient,
            MaxRetries = _options.MaxRetries,
            Timeout = TimeSpan.FromSeconds(_options.RequestTimeoutSeconds) + _sdkAttemptMargin
        };
    }

    private AnthropicMessages.MessageCreateParams BuildParameters(
        string model, IReadOnlyDictionary<string, JsonElement> schema, string systemPrompt, string userMessage)
    {
        var outputConfig = new AnthropicMessages.OutputConfig
        {
            Format = new AnthropicMessages.JsonOutputFormat { Schema = schema }
        };

        // Only send effort when configured: older models reject the parameter, and the SDK would
        // otherwise write an explicit null.
        if (ParseEffort(_options.Effort) is { } effort)
        {
            outputConfig = outputConfig with { Effort = effort };
        }

        // No thinking, temperature or tool_choice settings: current models think adaptively by default,
        // and reject non-default sampling, disabled thinking and forced tool use.
        return new AnthropicMessages.MessageCreateParams
        {
            Model = model,
            MaxTokens = _options.MaxOutputTokens,
            System = new List<AnthropicMessages.TextBlockParam>
            {
                // Marked cacheable so repeated reviews with the same rules and guidelines read them from cache. No TTL: the default five-minute
                // lifetime is the one whose write price Claude:Pricing holds.
                new() { Text = systemPrompt, CacheControl = new AnthropicMessages.CacheControlEphemeral() }
            },
            Messages = [new AnthropicMessages.UserMessageParam { Content = userMessage }],
            OutputConfig = outputConfig
        };
    }

    /// <summary>
    /// The beta request for a checklist run with the refusal fallback: the same request as the standard one, plus the fallback beta and
    /// <c>fallbacks: "default"</c>, which lets Anthropic pick the model that answers for each refusal category.
    /// </summary>
    private BetaMessages.MessageCreateParams BuildFallbackParameters(ClaudeRunRequest request)
    {
        var outputConfig = new BetaMessages.BetaOutputConfig
        {
            Format = new BetaMessages.BetaJsonOutputFormat { Schema = _checklistSchema }
        };

        if (ParseBetaEffort(_options.Effort) is { } effort)
        {
            outputConfig = outputConfig with { Effort = effort };
        }

        return new BetaMessages.MessageCreateParams
        {
            Betas = [RefusalFallbackBeta],
            Fallbacks = new BetaMessages.Default(),
            Model = request.Model,
            MaxTokens = _options.MaxOutputTokens,
            System = new List<BetaMessages.BetaTextBlockParam>
            {
                // No TTL, as in the standard request: the default five-minute lifetime is the one Claude:Pricing prices cache writes for.
                new() { Text = request.SystemPrompt, CacheControl = new BetaMessages.BetaCacheControlEphemeral() }
            },
            Messages = [new BetaMessages.BetaUserMessageParam { Content = request.UserMessage }],
            OutputConfig = outputConfig
        };
    }

    private static AnthropicMessages.Effort? ParseEffort(string? effort)
    {
        if (string.IsNullOrWhiteSpace(effort))
        {
            return null;
        }

        return effort.Trim().ToLowerInvariant() switch
        {
            "low" => AnthropicMessages.Effort.Low,
            "medium" => AnthropicMessages.Effort.Medium,
            "high" => AnthropicMessages.Effort.High,
            "xhigh" => AnthropicMessages.Effort.Xhigh,
            "max" => AnthropicMessages.Effort.Max,
            _ => throw new InvalidOperationException(
                $"Unknown Claude effort level '{effort}'. Use low, medium, high, xhigh or max, or leave it empty.")
        };
    }

    private static BetaMessages.Effort? ParseBetaEffort(string? effort)
    {
        // The beta endpoint has its own effort type with the same five levels, so the standard parse decides and this only translates.
        return ParseEffort(effort) switch
        {
            null => null,
            AnthropicMessages.Effort.Low => BetaMessages.Effort.Low,
            AnthropicMessages.Effort.Medium => BetaMessages.Effort.Medium,
            AnthropicMessages.Effort.High => BetaMessages.Effort.High,
            AnthropicMessages.Effort.Xhigh => BetaMessages.Effort.Xhigh,
            _ => BetaMessages.Effort.Max
        };
    }

    /// <summary>
    /// 400, 413 and 422 mean the request itself was refused (too large, or malformed) and would be refused again, so retrying is pointless. 401, 403
    /// and 404 are not here: a bad key or model name fails every part of every review, which must surface as a failure, not as lost parts.
    /// </summary>
    private static bool IsRejectedAsInvalid(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnprocessableEntity;
    }

    private ClaudeReviewResult ToRejectedResult(AnthropicApiException exception, Activity? activity)
    {
        var detail = DescribeRejection(exception);

        _logger.LogWarning("The AI service rejected a review request and would reject it again ({Detail}); this part of the review is lost.", detail);

        activity?.SetTag("http.response.status_code", (int)exception.StatusCode);
        activity?.SetStatus(ActivityStatusCode.Error, detail);

        // No model name: nothing ran, so the review footer must not credit one.
        return new ClaudeReviewResult(ChunkOutcome.Rejected, string.Empty, [], string.Empty, ClaudeUsage.Empty, detail);
    }

    private static string DescribeRejection(AnthropicApiException exception)
    {
        var status = (int)exception.StatusCode;
        var reason = ReadApiErrorMessage(exception.ResponseBody);

        return reason is null ? $"HTTP {status}" : $"HTTP {status}: {reason}";
    }

    private static string? ReadApiErrorMessage(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.Object
                || !error.TryGetProperty("message", out var messageElement)
                || messageElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var reason = messageElement.GetString()?.ReplaceLineEndings(" ").Trim();

            if (string.IsNullOrEmpty(reason))
            {
                return null;
            }

            return reason.Length > MaxRejectionReasonChars ? reason[..MaxRejectionReasonChars] + "…" : reason;
        }
        catch (JsonException)
        {
            // A body that is not JSON, such as a proxy's error page, has nothing worth quoting.
            return null;
        }
    }

    private ClaudeReviewResult ToResult(AnthropicMessages.Message message, Activity? activity)
    {
        var stopReason = message.StopReason?.Raw();

        // A reply that never reports why it stopped was cut off in transit. That is a failure to retry, not a review to read, and no tokens are
        // counted for it because the figures it carries are not final.
        if (stopReason is null)
        {
            throw new IOException("The reply from Claude ended before the model finished.");
        }

        var usage = new ClaudeUsage(
            message.Usage.InputTokens,
            message.Usage.OutputTokens,
            message.Usage.CacheReadInputTokens ?? 0,
            message.Usage.CacheCreationInputTokens ?? 0);

        var model = message.Model.Raw();

        RecordUsage(activity, usage, model, stopReason);

        _logger.LogInformation(
            "Claude finished a review request: stop reason {StopReason}, {InputTokens} input and {OutputTokens} output tokens.",
            stopReason, usage.InputTokens, usage.OutputTokens);

        if (stopReason == "refusal")
        {
            var category = message.StopDetails?.Category?.Raw();
            _logger.LogWarning("Claude declined to review this request (category: {Category}).", category ?? "unknown");
            activity?.SetStatus(ActivityStatusCode.Error, "refused");

            return new ClaudeReviewResult(ChunkOutcome.Refused, string.Empty, [], model, usage, category);
        }

        // Read by block type: with thinking on, the first block can be a thinking block with empty text, and a redacted one has no text at all.
        var text = string.Concat(message.Content.Select(
            block => block.TryPickText(out AnthropicMessages.TextBlock? textBlock) ? textBlock.Text : string.Empty));

        if (stopReason is "max_tokens" or "model_context_window_exceeded")
        {
            var isContextWindow = stopReason == "model_context_window_exceeded";

            // Two different causes with two different remedies, so two different messages: raising the output limit does nothing for a request
            // that did not fit the model's context window.
            if (isContextWindow)
            {
                _logger.LogWarning("The request did not fit in the model's context window, so Claude's reply was cut short; the review is incomplete.");
            }
            else
            {
                _logger.LogWarning(
                    "Claude's reply hit the output limit of {MaxTokens} tokens; the review is incomplete.", _options.MaxOutputTokens);
            }

            var detail = isContextWindow ? "context window exceeded" : "output limit reached";
            activity?.SetStatus(ActivityStatusCode.Error, detail);

            return new ClaudeReviewResult(ChunkOutcome.OutputTruncated, string.Empty, [], model, usage, detail);
        }

        // Any other stop reason, one this code has never seen included, is judged by what the reply holds: a complete review is a review.
        var parsed = ReviewResponseParser.Parse(text);

        if (parsed.Error is not null)
        {
            _logger.LogWarning("Claude's reply could not be read as a review: {Error}", parsed.Error);
            activity?.SetStatus(ActivityStatusCode.Error, parsed.Error);

            return new ClaudeReviewResult(ChunkOutcome.InvalidOutput, string.Empty, [], model, usage, parsed.Error);
        }

        if (parsed.DroppedEntryCount > 0)
        {
            _logger.LogWarning("{DroppedCount} malformed finding(s) in Claude's reply were ignored.", parsed.DroppedEntryCount);
        }

        return new ClaudeReviewResult(
            ChunkOutcome.Completed,
            parsed.Summary,
            parsed.Findings,
            model,
            usage,
            OmittedFindingCount: parsed.OmittedFindingCount,
            DroppedFindingCount: parsed.DroppedEntryCount);
    }

    private static RunReply ToRunReply(AnthropicMessages.Message message)
    {
        var model = message.Model.Raw();
        var usage = new ClaudeUsage(
            message.Usage.InputTokens,
            message.Usage.OutputTokens,
            message.Usage.CacheReadInputTokens ?? 0,
            message.Usage.CacheCreationInputTokens ?? 0);

        return new RunReply(
            StopReasonOf(message.StopReason?.Raw()),
            message.StopDetails?.Category?.Raw(),
            string.Concat(message.Content.Select(block => block.TryPickText(out AnthropicMessages.TextBlock? textBlock) ? textBlock.Text : string.Empty)),
            model,
            [new ModelAttempt(model, usage)]);
    }

    private static RunReply ToRunReply(BetaMessages.BetaMessage message)
    {
        var stopReason = StopReasonOf(message.StopReason?.Raw());
        // The model that produced the answer. The SDK's aggregate takes it from the stream's first event and, after a fallback block, from the
        // model that block hands over to (AnthropicSdkContractTests pins it), so a mid-stream fallback names the model that finished the answer.
        var model = message.Model.Raw();
        var attempts = new List<ModelAttempt>();

        // Each attempt the API made, a declined one ("message") or the fallback model's ("fallback_message"), with its own model and tokens. The
        // top-level usage covers only the attempt that produced the message. Other kinds of iteration (compaction, an advisor) are not part of a run.
        foreach (var iteration in message.Usage.Iterations ?? [])
        {
            if (iteration.TryPickBetaMessageIterationUsage(out var declined))
            {
                attempts.Add(new ModelAttempt(
                    declined.Model?.Raw() ?? model,
                    new ClaudeUsage(declined.InputTokens, declined.OutputTokens, declined.CacheReadInputTokens, declined.CacheCreationInputTokens)));
            }
            else if (iteration.TryPickBetaFallbackMessageIterationUsage(out var served))
            {
                attempts.Add(new ModelAttempt(
                    served.Model.Raw(),
                    new ClaudeUsage(served.InputTokens, served.OutputTokens, served.CacheReadInputTokens, served.CacheCreationInputTokens)));
            }
        }

        if (attempts.Count == 0)
        {
            attempts.Add(new ModelAttempt(model, new ClaudeUsage(
                message.Usage.InputTokens,
                message.Usage.OutputTokens,
                message.Usage.CacheReadInputTokens ?? 0,
                message.Usage.CacheCreationInputTokens ?? 0)));
        }

        // The fallback block marks where one model handed over to the next and has no text, so only text blocks are read.
        return new RunReply(
            stopReason,
            message.StopDetails?.Category?.Raw(),
            string.Concat(message.Content.Select(block => block.TryPickText(out BetaMessages.BetaTextBlock? textBlock) ? textBlock.Text : string.Empty)),
            model,
            attempts);
    }

    private static string StopReasonOf(string? stopReason)
    {
        // A reply that never reports why it stopped was cut off in transit. That is a failure to retry, not an answer to read, and no tokens are
        // counted for it because the figures it carries are not final.
        return stopReason ?? throw new IOException("The reply from Claude ended before the model finished.");
    }

    private ClaudeRunResult ToRunResult(RunReply reply, Activity? activity)
    {
        var totalUsage = reply.Attempts.Aggregate(ClaudeUsage.Empty, (total, attempt) => total.Plus(attempt.Usage));

        RecordUsage(activity, totalUsage, reply.Model, reply.StopReason);

        _logger.LogInformation(
            "Claude finished a checklist run: model {Model}, stop reason {StopReason}, {AttemptCount} attempt(s), {InputTokens} input and {OutputTokens} output tokens.",
            reply.Model, reply.StopReason, reply.Attempts.Count, totalUsage.InputTokens, totalUsage.OutputTokens);

        var result = Judge(reply);

        if (result.Outcome != RunOutcome.Completed)
        {
            activity?.SetStatus(ActivityStatusCode.Error, result.Detail ?? result.Outcome.ToString());
        }

        CountRun(result.Outcome, reply.Model);

        return result;
    }

    private ClaudeRunResult Judge(RunReply reply)
    {
        if (reply.StopReason == "refusal")
        {
            // With the fallback on, a final refusal means every model in the chain declined.
            _logger.LogWarning("Claude declined the checklist run (category: {Category}).", reply.RefusalCategory ?? "unknown");

            return new ClaudeRunResult(RunOutcome.Refused, null, reply.Model, reply.Attempts, reply.RefusalCategory);
        }

        if (reply.StopReason is "max_tokens" or "model_context_window_exceeded")
        {
            var isContextWindow = reply.StopReason == "model_context_window_exceeded";

            if (isContextWindow)
            {
                _logger.LogWarning("The checklist run did not fit in the model's context window, so its answer was cut short.");
            }
            else
            {
                _logger.LogWarning("The checklist run's answer hit the output limit of {MaxTokens} tokens.", _options.MaxOutputTokens);
            }

            var detail = isContextWindow ? "context window exceeded" : "output limit reached";

            return new ClaudeRunResult(RunOutcome.OutputTruncated, null, reply.Model, reply.Attempts, detail);
        }

        // Any other stop reason, one this code has never seen included, is judged by what the answer holds.
        var read = ChecklistAnswerReader.Read(reply.Text);

        if (read.Error is not null)
        {
            // The reader's reasons are fixed words that quote nothing of the answer, so they can be logged.
            _logger.LogWarning("Claude's checklist answer could not be used: {Error}", read.Error);

            return new ClaudeRunResult(RunOutcome.InvalidOutput, null, reply.Model, reply.Attempts, read.Error);
        }

        return new ClaudeRunResult(RunOutcome.Completed, read.Answer, reply.Model, reply.Attempts);
    }

    private ClaudeRunResult ToRejectedRunResult(ClaudeRunRequest request, AnthropicApiException exception, Activity? activity)
    {
        var detail = DescribeRejection(exception);

        _logger.LogWarning("The AI service rejected a checklist run's request and would reject it again ({Detail}).", detail);

        activity?.SetTag("http.response.status_code", (int)exception.StatusCode);
        activity?.SetStatus(ActivityStatusCode.Error, detail);

        // No model answered, so the count names the one that was asked for, and the result names none.
        CountRun(RunOutcome.Rejected, request.Model);

        return new ClaudeRunResult(RunOutcome.Rejected, null, string.Empty, [], detail);
    }

    private static void CountRun(RunOutcome outcome, string model)
    {
        AppMeters.ReviewRuns.Add(
            1,
            new KeyValuePair<string, object?>("outcome", outcome.ToString()),
            new KeyValuePair<string, object?>("model", model));
    }

    private static void RecordUsage(Activity? activity, ClaudeUsage usage, string model, string stopReason)
    {
        activity?.SetTag("gen_ai.response.model", model);
        activity?.SetTag("gen_ai.usage.input_tokens", usage.InputTokens);
        activity?.SetTag("gen_ai.usage.output_tokens", usage.OutputTokens);
        activity?.SetTag("gen_ai.usage.cache_read_input_tokens", usage.CacheReadInputTokens);
        activity?.SetTag("gen_ai.usage.cache_creation_input_tokens", usage.CacheCreationInputTokens);
        activity?.SetTag("gen_ai.response.finish_reasons", new[] { stopReason });

        // Tagged with the stop reason so spend on replies that were cut off can be told from spend on finished ones.
        var stopReasonTag = new KeyValuePair<string, object?>("stop_reason", stopReason);

        AppMeters.InputTokens.Add(usage.InputTokens, stopReasonTag);
        AppMeters.OutputTokens.Add(usage.OutputTokens, stopReasonTag);
        AppMeters.CacheReadInputTokens.Add(usage.CacheReadInputTokens, stopReasonTag);
        AppMeters.CacheCreationInputTokens.Add(usage.CacheCreationInputTokens, stopReasonTag);
    }
}
