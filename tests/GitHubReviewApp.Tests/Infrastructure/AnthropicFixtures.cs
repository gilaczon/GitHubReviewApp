namespace GitHubReviewApp.Tests.Infrastructure;

/// <summary>
/// Builds Messages API responses in the documented wire format, so the SDK parses them exactly as it would a real reply. The service
/// streams, so a reply is a server-sent-events stream: message_start, the content blocks as deltas, message_delta carrying the stop reason
/// and the final output tokens, then message_stop. These are hand-written from the API reference, not recordings of live calls.
/// </summary>
internal static class AnthropicFixtures
{
    public const string ModelId = "claude-sonnet-5-5";

    // A JSON escape is built from this, so the source holds no escape sequence for an editor or a tool to turn into the character itself.
    private const string Backslash = "\\";

    // The API writes its JSON with the characters of code and of other languages as they are: a less-than sign or an accented letter is not turned
    // into a \u escape the way the serializer's default does, so the events here are written the same way.
    private static readonly JsonSerializerOptions _wireOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // The eight check ids of the checklist schema, in the checklist's order, written out here rather than read from the code under test.
    private static readonly string[] _checkIds =
        ["standards-design", "functionality", "performance", "security", "testing", "error-handling", "scalability", "dependencies"];

    /// <summary>A checklist answer as the model writes it under the checklist schema. Without <paramref name="checks"/>, all eight checks pass.</summary>
    public static string ChecklistJson(
        string summary = "Adds retries.",
        IEnumerable<object>? checks = null,
        IEnumerable<object>? findings = null,
        string coverageNote = "")
    {
        return JsonSerializer.Serialize(
            new { summary, checks = checks ?? AllChecks(), findings = findings ?? Array.Empty<object>(), coverage_note = coverageNote },
            _wireOptions);
    }

    /// <summary>One entry of a checklist answer's <c>checks</c> list. <paramref name="isSerious"/> is written as the answer's <c>serious</c> field.</summary>
    public static object Check(string id, string status = "pass", string note = "", bool isSerious = false)
    {
        return new { id, status, note, serious = isSerious };
    }

    /// <summary>All eight checks, in the checklist's order, each rated <paramref name="status"/>.</summary>
    public static IReadOnlyList<object> AllChecks(string status = "pass")
    {
        return _checkIds.Select(id => Check(id, status)).ToList();
    }

    /// <summary>One entry of a checklist answer's <c>findings</c> list.</summary>
    public static object ChecklistFinding(
        string check = "functionality",
        string file = "src/Foo.cs",
        int line = 11,
        string issue = "Retries twice.",
        string fix = "Retry once.")
    {
        return new { check, file, line, issue, fix };
    }

    /// <summary>
    /// The block the beta API puts in the content where a model that declined hands over to the next one: no text, and no deltas when streamed. It
    /// is written by serialising the SDK's own type, so its shape is the SDK's and not a guess at it.
    /// </summary>
    public static object FallbackBlock(string fromModel, string toModel, string category = "cyber")
    {
        var block = new BetaMessages.BetaFallbackBlock
        {
            From = new BetaMessages.BetaFallbackInfo { Model = fromModel },
            To = new BetaMessages.BetaFallbackInfo { Model = toModel },
            Trigger = new BetaMessages.BetaFallbackRefusalTrigger { Category = category }
        };

        return JsonSerializer.SerializeToElement(block);
    }

    /// <summary>
    /// One entry of the beta API's <c>usage.iterations</c>: an attempt made for the request, with its own model and tokens. A model that declined is a
    /// <c>message</c> entry; the fallback model whose output is the returned message is the <c>fallback_message</c> entry.
    /// </summary>
    public static object Iteration(string type, string model, long inputTokens, long outputTokens, long cacheReadTokens = 0, long cacheCreationTokens = 0)
    {
        return new
        {
            type,
            model,
            input_tokens = inputTokens,
            output_tokens = outputTokens,
            cache_read_input_tokens = cacheReadTokens,
            cache_creation_input_tokens = cacheCreationTokens
        };
    }

    public static string ReviewJson(string summary = "Adds retry handling.", IEnumerable<object>? findings = null, int omittedFindings = 0)
    {
        return JsonSerializer.Serialize(new { summary, findings = findings ?? Array.Empty<object>(), omitted_findings = omittedFindings }, _wireOptions);
    }

    public static object Finding(
        string path = "src/Foo.cs",
        int line = 11,
        string severity = "high",
        string category = "bug",
        string title = "Null dereference",
        string body = "The value can be null when the cache is cold.")
    {
        return new { path, line, severity, category, title, body };
    }

    public static object TextBlock(string text)
    {
        return new { type = "text", text };
    }

    /// <summary>A thinking block as returned when thinking is on and display is omitted (empty text).</summary>
    public static object ThinkingBlock()
    {
        return new { type = "thinking", thinking = string.Empty, signature = "c2lnbmF0dXJl" };
    }

    /// <summary>A thinking block the API redacted: it carries opaque data and no readable text.</summary>
    public static object RedactedThinkingBlock()
    {
        return new { type = "redacted_thinking", data = "cmVkYWN0ZWQ=" };
    }

    /// <summary>
    /// A reply as a server-sent-events stream, in the order the API sends it: the start, each block's start followed by a ping, the blocks' deltas and
    /// stops, then the final delta with the stop reason, then the stop.
    /// </summary>
    /// <param name="isCacheUsageOmitted">Leave the prompt-cache counters out of the usage objects, as a reply with no caching does.</param>
    /// <param name="isUsageRestatedInFinalEvent">
    /// Send provisional counts in the first event and the final ones in the last, as the API documents its usage figures to be cumulative: the start
    /// says one input token and no cache use, and the final delta restates all four counters.
    /// </param>
    /// <param name="bytesPerRead">When set, the stream hands out that many bytes per read, so a character of several bytes can arrive in pieces.</param>
    /// <param name="iterations">
    /// The beta API's per-attempt usage (see <see cref="Iteration"/>), sent in the final delta's usage, where the API reports them once every attempt
    /// has run.
    /// </param>
    public static HttpResponseMessage Message(
        IEnumerable<object>? content = null,
        string stopReason = "end_turn",
        object? stopDetails = null,
        long inputTokens = 1_200,
        long outputTokens = 300,
        long cacheReadTokens = 0,
        long cacheCreationTokens = 0,
        string model = ModelId,
        bool isCacheUsageOmitted = false,
        bool isUsageRestatedInFinalEvent = false,
        int? bytesPerRead = null,
        IEnumerable<object>? iterations = null)
    {
        var startUsage = isUsageRestatedInFinalEvent
            ? Usage(1, 1, 0, 0, isCacheUsageOmitted: false)
            : Usage(inputTokens, 1, cacheReadTokens, cacheCreationTokens, isCacheUsageOmitted);

        var events = new List<string>
        {
            Event("message_start", new
            {
                type = "message_start",
                message = new
                {
                    id = "msg_test_01",
                    type = "message",
                    role = "assistant",
                    model,
                    content = Array.Empty<object>(),
                    stop_reason = (string?)null,
                    stop_sequence = (string?)null,
                    usage = startUsage
                }
            })
        };

        var index = 0;

        foreach (var block in content ?? [TextBlock(ReviewJson())])
        {
            var blockEvents = BlockEvents(index++, JsonSerializer.SerializeToElement(block)).ToList();

            events.Add(blockEvents[0]);
            events.Add(Event("ping", new { type = "ping" }));
            events.AddRange(blockEvents.Skip(1));
        }

        var delta = new Dictionary<string, object?> { ["stop_reason"] = stopReason, ["stop_sequence"] = null };

        if (stopDetails is not null)
        {
            delta["stop_details"] = stopDetails;
        }

        object finalUsage = isUsageRestatedInFinalEvent
            ? Usage(inputTokens, outputTokens, cacheReadTokens, cacheCreationTokens, isCacheUsageOmitted)
            : new { output_tokens = outputTokens };

        if (iterations is not null)
        {
            finalUsage = WithIterations(finalUsage, iterations);
        }

        events.Add(Event("message_delta", new { type = "message_delta", delta, usage = finalUsage }));
        events.Add(Event("message_stop", new { type = "message_stop" }));

        return Sse(string.Concat(events), bytesPerRead);
    }

    /// <summary>A stream that starts well and then reports an error event, as the API does when it becomes overloaded part way through a reply.</summary>
    public static HttpResponseMessage StreamWithAnErrorEvent(string errorType = "overloaded_error", string message = "Overloaded")
    {
        var events = new List<string> { MessageStartEvent() };

        // The block's start and its first delta, then the stream breaks before the block is closed.
        events.AddRange(BlockEvents(0, JsonSerializer.SerializeToElement(TextBlock("{\"summary\":\"cut"))).Take(2));
        events.Add(Event("error", new { type = "error", error = new { type = errorType, message } }));

        return Sse(string.Concat(events));
    }

    /// <summary>A stream whose connection simply closes before the model finished: no message_delta, so no stop reason, and no message_stop.</summary>
    public static HttpResponseMessage StreamThatEndsBeforeTheModelFinishes()
    {
        var events = new List<string> { MessageStartEvent() };
        events.AddRange(BlockEvents(0, JsonSerializer.SerializeToElement(TextBlock("{\"summary\":\"cut"))).Take(2));

        return Sse(string.Concat(events));
    }

    /// <summary>
    /// A stream that starts well and then carries an event whose data is not valid JSON, as a reply from an API whose format has changed would. The
    /// request was fine and a retry would get the same reply, unlike a stream that merely ends early.
    /// </summary>
    public static HttpResponseMessage StreamWithAnEventThatIsNotJson()
    {
        var events = new List<string> { MessageStartEvent(), "event: content_block_start\ndata: {this is not json\n\n" };

        return Sse(string.Concat(events));
    }

    /// <summary>
    /// A stream whose text holds half of a surrogate pair, written as the JSON escape for it. The API sends valid UTF-8, so no real reply is like
    /// this, and the SDK refuses to read the event.
    /// </summary>
    public static HttpResponseMessage StreamWithTextThatIsNotValidUnicode()
    {
        var halfOfASurrogatePair = Backslash + "ud800";
        var events = new List<string>
        {
            MessageStartEvent(),
            Event("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "text", text = string.Empty } }),
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Adds retries" + halfOfASurrogatePair + "\"}}\n\n",
            Event("content_block_stop", new { type = "content_block_stop", index = 0 }),
            Event("message_delta", new { type = "message_delta", delta = new { stop_reason = "end_turn", stop_sequence = (string?)null }, usage = new { output_tokens = 5 } }),
            Event("message_stop", new { type = "message_stop" })
        };

        return Sse(string.Concat(events));
    }

    /// <summary>A stream that reaches message_stop without the message_delta that carries the stop reason and the final token counts.</summary>
    public static HttpResponseMessage StreamWithoutAStopReason()
    {
        var events = new List<string> { MessageStartEvent() };
        events.AddRange(BlockEvents(0, JsonSerializer.SerializeToElement(TextBlock(ReviewJson("Looks complete.")))));
        events.Add(Event("message_stop", new { type = "message_stop" }));

        return Sse(string.Concat(events));
    }

    /// <summary>A response whose body delivers the start of a stream and then stalls until the caller gives up. <paramref name="stalled"/> is completed once it has.</summary>
    public static HttpResponseMessage StreamThatStallsAfterItsFirstEvent(TaskCompletionSource? stalled = null)
    {
        var content = new StreamContent(new StallingStream(Encoding.UTF8.GetBytes(MessageStartEvent()), stalled));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    /// <summary>
    /// The 429 the API sends once the organization has used its monthly spend cap, in the shape its documentation gives: the type of a rate limit,
    /// an error code in the details, and no retry-after header. Nothing a retry does changes it until access resumes.
    /// </summary>
    public static HttpResponseMessage SpendLimitReached()
    {
        var json = JsonSerializer.Serialize(new
        {
            type = "error",
            error = new
            {
                type = "rate_limit_error",
                message = "You have reached your API usage limits: your organization has crossed its monthly API usage threshold. You will regain access on 2026-12-01 at 00:00 UTC.",
                details = new { error_code = "enforced_spend_limit_reached" }
            },
            request_id = "req_test_01"
        });

        return new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    public static HttpResponseMessage Error(HttpStatusCode status, string errorType, string message)
    {
        var json = JsonSerializer.Serialize(new { type = "error", error = new { type = errorType, message } });

        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    /// <summary>An error response whose body is exactly <paramref name="body"/>: a proxy's HTML page, an empty body, or JSON in a shape the API does not use.</summary>
    public static HttpResponseMessage ErrorWithBody(HttpStatusCode status, string body, string mediaType = "application/json")
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, mediaType)
        };
    }

    private static string MessageStartEvent()
    {
        return Event("message_start", new
        {
            type = "message_start",
            message = new
            {
                id = "msg_test_01",
                type = "message",
                role = "assistant",
                model = ModelId,
                content = Array.Empty<object>(),
                stop_reason = (string?)null,
                stop_sequence = (string?)null,
                usage = Usage(100, 1, 0, 0, isCacheUsageOmitted: false)
            }
        });
    }

    private static JsonObject WithIterations(object usage, IEnumerable<object> iterations)
    {
        var withIterations = JsonSerializer.SerializeToNode(usage, _wireOptions)!.AsObject();
        withIterations["iterations"] = JsonSerializer.SerializeToNode(iterations.ToList(), _wireOptions);

        return withIterations;
    }

    private static object Usage(long inputTokens, long outputTokens, long cacheReadTokens, long cacheCreationTokens, bool isCacheUsageOmitted)
    {
        return isCacheUsageOmitted
            ? new { input_tokens = inputTokens, output_tokens = outputTokens }
            : new { input_tokens = inputTokens, output_tokens = outputTokens, cache_creation_input_tokens = cacheCreationTokens, cache_read_input_tokens = cacheReadTokens };
    }

    private static IEnumerable<string> BlockEvents(int index, JsonElement block)
    {
        var type = block.GetProperty("type").GetString();

        switch (type)
        {
            case "text":
                yield return Event("content_block_start", new { type = "content_block_start", index, content_block = new { type = "text", text = string.Empty } });

                foreach (var piece in Pieces(block.GetProperty("text").GetString() ?? string.Empty))
                {
                    yield return Event("content_block_delta", new { type = "content_block_delta", index, delta = new { type = "text_delta", text = piece } });
                }

                break;

            case "thinking":
                yield return Event("content_block_start", new { type = "content_block_start", index, content_block = new { type = "thinking", thinking = string.Empty, signature = string.Empty } });

                if (!string.IsNullOrEmpty(block.GetProperty("thinking").GetString()))
                {
                    yield return Event("content_block_delta", new { type = "content_block_delta", index, delta = new { type = "thinking_delta", thinking = block.GetProperty("thinking").GetString() } });
                }

                yield return Event("content_block_delta", new { type = "content_block_delta", index, delta = new { type = "signature_delta", signature = block.GetProperty("signature").GetString() } });
                break;

            default:
                // A block the service does not read, such as redacted_thinking, arrives whole in its start event.
                yield return Event("content_block_start", new { type = "content_block_start", index, content_block = block });
                break;
        }

        yield return Event("content_block_stop", new { type = "content_block_stop", index });
    }

    private static IEnumerable<string> Pieces(string text)
    {
        if (text.Length < 2)
        {
            return [text];
        }

        // Two deltas, so the service has to join them. The cut is moved off the middle of a surrogate pair.
        var cut = text.Length / 2;

        if (char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return [text[..cut], text[cut..]];
    }

    private static string Event(string name, object data)
    {
        return $"event: {name}\ndata: {JsonSerializer.Serialize(data, _wireOptions)}\n\n";
    }

    private static HttpResponseMessage Sse(string body, int? bytesPerRead = null)
    {
        if (bytesPerRead is { } trickle)
        {
            var content = new StreamContent(new TricklingStream(Encoding.UTF8.GetBytes(body), trickle));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
        };
    }
}
