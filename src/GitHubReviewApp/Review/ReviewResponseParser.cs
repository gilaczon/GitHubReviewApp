namespace GitHubReviewApp.Review;

/// <summary>
/// Reads the model's reply. The reply should be JSON matching <see cref="ReviewResponseSchema"/>. This tolerates a markdown fence
/// around it, odd line values and unknown severities, but not a reply that is not a review: prose, or JSON with no findings list, is an
/// error rather than a review with no findings, because the difference is whether the pull request got read at all.
/// </summary>
public static class ReviewResponseParser
{
    /// <summary>
    /// The model's own count of findings it left out, which nothing can check. A real reply reports a handful, so a larger number is read as this
    /// one: it keeps a wild value from being printed in a review, and from wrapping around when the counts of several parts are added.
    /// </summary>
    public const int MaxOmittedFindings = 1000;

    private const string DefaultCategory = "other";

    public static ParsedReview Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Failure("The model returned an empty response.");
        }

        var content = StripCodeFence(text.Trim());

        if (content.StartsWith('[') )
        {
            return Failure("The model returned a JSON array instead of an object.");
        }

        if (!content.StartsWith('{'))
        {
            // The text itself is left out of the message: it came from a model that read the pull request author's code.
            return Failure("The model's reply was not the requested JSON review.");
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            return ReadReview(document.RootElement);
        }
        catch (JsonException exception)
        {
            return Failure($"The model's response was not valid JSON: {exception.Message}");
        }
    }

    private static ParsedReview ReadReview(JsonElement root)
    {
        if (!root.TryGetProperty("findings", out var findingsElement) || findingsElement.ValueKind != JsonValueKind.Array)
        {
            return Failure("The model's review has no findings list.");
        }

        var findings = new List<ReviewFinding>();
        var droppedCount = 0;

        foreach (var item in findingsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                droppedCount++;
                continue;
            }

            var title = ReadString(item, "title");
            var body = ReadString(item, "body");

            if (title.Length == 0 && body.Length == 0)
            {
                droppedCount++;
                continue;
            }

            var category = ReadString(item, "category").ToLowerInvariant();

            findings.Add(new ReviewFinding(
                ReadString(item, "path"),
                ReadLine(item),
                ParseSeverity(ReadString(item, "severity")),
                category.Length == 0 ? DefaultCategory : category,
                title,
                body));
        }

        // An empty result here would read as "no issues" although the model did list something.
        if (findings.Count == 0 && droppedCount > 0)
        {
            return Failure("The model listed findings that could not be read.");
        }

        return new ParsedReview(true, ReadString(root, "summary"), findings, null, ReadCount(root, "omitted_findings"), droppedCount);
    }

    private static int ReadCount(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var count)
            ? Math.Clamp(count, 0, MaxOmittedFindings)
            : 0;
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString()?.Trim() ?? string.Empty;
        }

        return string.Empty;
    }

    private static int ReadLine(JsonElement element)
    {
        if (!element.TryGetProperty("line", out var value))
        {
            return 0;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                if (value.TryGetInt32(out var whole))
                {
                    return Math.Max(whole, 0);
                }

                if (value.TryGetDouble(out var fractional) && fractional >= 1 && fractional <= int.MaxValue && fractional == Math.Floor(fractional))
                {
                    return (int)fractional;
                }

                return 0;

            case JsonValueKind.String:
                return int.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

            default:
                return 0;
        }
    }

    private static ReviewSeverity ParseSeverity(string text)
    {
        return text.ToLowerInvariant() switch
        {
            "critical" => ReviewSeverity.Critical,
            "high" => ReviewSeverity.High,
            "medium" => ReviewSeverity.Medium,
            "low" => ReviewSeverity.Low,
            _ => ReviewSeverity.Medium
        };
    }

    private static string StripCodeFence(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal))
        {
            return text;
        }

        var firstLineEnd = text.IndexOf('\n');
        if (firstLineEnd < 0)
        {
            return text;
        }

        // A reply that quotes code fences of its own is wrapped in a longer one, and a fence closes with a run at least as long as the one that opened it.
        var fence = new string('`', text.TakeWhile(character => character == '`').Count());
        var inner = text[(firstLineEnd + 1)..];
        var closingFence = inner.LastIndexOf(fence, StringComparison.Ordinal);

        // What was found may be the tail of a longer run, and the whole run is the fence.
        while (closingFence > 0 && inner[closingFence - 1] == '`')
        {
            closingFence--;
        }

        return (closingFence >= 0 ? inner[..closingFence] : inner).Trim();
    }

    private static ParsedReview Failure(string error)
    {
        return new ParsedReview(false, string.Empty, [], error);
    }
}
