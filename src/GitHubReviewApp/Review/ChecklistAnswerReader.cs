namespace GitHubReviewApp.Review;

/// <summary>
/// Reads one run's answer, JSON matching <see cref="ChecklistSchema"/>, by the rules of design 5.6.3. The schema cannot hold a model to all of
/// them (the API refuses length and count limits, and a value can still arrive of the wrong kind), so they are enforced here: an entry whose
/// id names no check is dropped and the first entry for a check wins; a status that is not exactly one of the four is a concern, never a
/// pass; only Security can fail; only the boolean <c>true</c> marks a concern serious; and every text is made one line, loses what looks like
/// a credential (<see cref="SecretRedactor"/>) and is then cut to its limit, counted in UTF-16 characters. An answer that rates none of the
/// eight checks is a failure, not a review in which nothing was assessed.
/// </summary>
public static class ChecklistAnswerReader
{
    /// <summary>The longest note kept for a check.</summary>
    public const int MaxNoteChars = 100;

    /// <summary>The longest summary kept.</summary>
    public const int MaxSummaryChars = 240;

    /// <summary>The longest coverage note kept.</summary>
    public const int MaxCoverageNoteChars = 160;

    /// <summary>The most findings kept: the first ones in the answer whose check is one of the eight.</summary>
    public const int MaxFindings = 8;

    /// <summary>The longest file path kept in a finding.</summary>
    public const int MaxFileChars = 250;

    /// <summary>The longest issue kept in a finding.</summary>
    public const int MaxIssueChars = 160;

    /// <summary>The longest fix kept in a finding.</summary>
    public const int MaxFixChars = 120;

    private const string Ellipsis = "…";

    /// <summary>
    /// Reads the text of one answer. It never throws: text that is not a usable answer is a failure saying why, in fixed words that quote
    /// nothing of the answer, which is model text written after reading the author's code.
    /// </summary>
    public static ChecklistReadResult Read(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return ChecklistReadResult.Failure("The answer was empty.");
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            // The exception's message is left out: it can quote the answer.
            return ChecklistReadResult.Failure("The answer was not valid JSON.");
        }
        catch (ArgumentException)
        {
            // What the parser throws for text that is not valid UTF-16, such as half of a surrogate pair.
            return ChecklistReadResult.Failure("The answer held text that is not valid Unicode.");
        }

        using (document)
        {
            return ReadAnswer(document.RootElement);
        }
    }

    private static ChecklistReadResult ReadAnswer(JsonElement answer)
    {
        if (answer.ValueKind != JsonValueKind.Object)
        {
            return ChecklistReadResult.Failure("The answer was not a JSON object.");
        }

        var rated = ReadChecks(answer);

        // The spec's schema asked for all eight, which the API cannot enforce. An answer that assessed nothing must not give a verdict.
        if (rated.Count == 0)
        {
            return ChecklistReadResult.Failure("The answer rated none of the eight checks.");
        }

        return ChecklistReadResult.Success(new RunAnswer(
            rated.AsReadOnly(),
            ReadFindings(answer),
            RedactedLine(ReadText(answer, "summary"), MaxSummaryChars),
            RedactedLine(ReadText(answer, "coverage_note"), MaxCoverageNoteChars)));
    }

    private static Dictionary<CheckId, RatedCheck> ReadChecks(JsonElement answer)
    {
        var rated = new Dictionary<CheckId, RatedCheck>();

        foreach (var entry in ObjectsIn(answer, "checks"))
        {
            // An entry whose id names no check is dropped, and so is every entry for a check after its first.
            if (Checklist.TryParse(ReadText(entry, "id"), out var id) && !rated.ContainsKey(id))
            {
                rated.Add(id, ReadCheck(id, entry));
            }
        }

        return rated;
    }

    private static RatedCheck ReadCheck(CheckId id, JsonElement entry)
    {
        var status = ReadStatus(ReadText(entry, "status"));

        // Only Security can fail a review: a fail on any other check is a concern, and keeps its note.
        if (status == CheckStatus.Fail && id != CheckId.Security)
        {
            status = CheckStatus.Concern;
        }

        var note = status is CheckStatus.Concern or CheckStatus.Fail ? RedactedLine(ReadText(entry, "note"), MaxNoteChars) : string.Empty;

        if (status == CheckStatus.Concern && note.Length == 0)
        {
            note = Checklist.NotAssessedNote;
        }

        // Only the boolean true counts; "true", 1 and null do not.
        var isSerious = status == CheckStatus.Concern
            && TryGetProperty(entry, "serious", out var serious)
            && serious.ValueKind == JsonValueKind.True;

        return new RatedCheck(id, status, note, isSerious);
    }

    private static CheckStatus ReadStatus(string status)
    {
        // Matched exactly, so "PASS" and "na " are not statuses. Whatever is not one of the four is a concern, never a pass.
        return status switch
        {
            "pass" => CheckStatus.Pass,
            "concern" => CheckStatus.Concern,
            "fail" => CheckStatus.Fail,
            "na" => CheckStatus.NotApplicable,
            _ => CheckStatus.Concern
        };
    }

    private static IReadOnlyList<ChecklistFinding> ReadFindings(JsonElement answer)
    {
        // A finding whose check is not one of the eight is dropped before the limit applies, so it takes no place from one that is kept.
        return ObjectsIn(answer, "findings")
            .Select(ReadFinding)
            .OfType<ChecklistFinding>()
            .Take(MaxFindings)
            .ToList()
            .AsReadOnly();
    }

    private static ChecklistFinding? ReadFinding(JsonElement entry)
    {
        if (!Checklist.TryParse(ReadText(entry, "check"), out var check))
        {
            return null;
        }

        return new ChecklistFinding(
            check,
            RedactedLine(ReadText(entry, "file"), MaxFileChars),
            ReadLine(entry),
            RedactedLine(ReadText(entry, "issue"), MaxIssueChars),
            RedactedLine(ReadText(entry, "fix"), MaxFixChars));
    }

    private static int ReadLine(JsonElement entry)
    {
        // Only a whole number that fits is a line. "42", 4.5 and null are 0, and so is any number below 1: the comment shows 0 as no line,
        // and the merge, which compares lines, must not take -1 and 0 for two different ones.
        return TryGetProperty(entry, "line", out var line) && line.ValueKind == JsonValueKind.Number && line.TryGetInt32(out var number) && number > 0
            ? number
            : 0;
    }

    private static IEnumerable<JsonElement> ObjectsIn(JsonElement owner, string propertyName)
    {
        // A list of the wrong kind reads as an empty list, and an entry that is not an object is dropped.
        return TryGetProperty(owner, propertyName, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(entry => entry.ValueKind == JsonValueKind.Object)
            : [];
    }

    private static bool TryGetProperty(JsonElement owner, string propertyName, out JsonElement value)
    {
        // Not JsonElement.TryGetProperty: that unescapes the names it compares and can throw when it meets one that is the JSON escape of
        // half a surrogate pair, even though the property asked for is in the object too. A name that cannot be read is skipped here as if
        // it were absent, so the other properties are still found. Like TryGetProperty, the last of two properties with the same name wins.
        var isFound = false;

        value = default;

        foreach (var property in owner.EnumerateObject())
        {
            if (IsNamed(property, propertyName))
            {
                value = property.Value;
                isFound = true;
            }
        }

        return isFound;
    }

    private static bool IsNamed(JsonProperty property, string propertyName)
    {
        try
        {
            return property.NameEquals(propertyName);
        }
        catch (InvalidOperationException)
        {
            // Thrown when the name is the JSON escape of half a surrogate pair. Such a name holds no valid text, so it is no one's name.
            return false;
        }
    }

    private static string ReadText(JsonElement owner, string propertyName)
    {
        // A value of the wrong kind reads as empty text.
        if (!TryGetProperty(owner, propertyName, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        try
        {
            return value.GetString() ?? string.Empty;
        }
        catch (InvalidOperationException)
        {
            // Thrown when a JSON escape in the string stands for half of a surrogate pair. Such a value holds no valid text, so it reads as empty too.
            return string.Empty;
        }
    }

    private static string RedactedLine(string text, int limit)
    {
        // With no separator, Split splits at every whitespace character (char.IsWhiteSpace), line breaks included. Dropping the empty
        // parts collapses each run of whitespace into one space and trims both ends.
        var oneLine = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        // Every text read here can be posted, so credentials come out of it now, before the cut. The redactor knows a key by its whole shape
        // (a GitHub token needs 30 characters after its prefix), so a key that the cut had shortened would no longer match, and what the cut
        // left of it would be posted. What the redactor writes in a key's place holds no line break and no run of spaces, so the text stays
        // one line.
        var line = SecretRedactor.Redact(oneLine);

        if (line.Length <= limit)
        {
            return line;
        }

        var keptLength = limit - 1;

        // Never keep the first half of a surrogate pair whose second half is cut off.
        if (char.IsHighSurrogate(line[keptLength - 1]))
        {
            keptLength--;
        }

        return line[..keptLength] + Ellipsis;
    }
}
