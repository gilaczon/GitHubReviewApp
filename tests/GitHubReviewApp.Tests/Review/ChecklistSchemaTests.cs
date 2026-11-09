namespace GitHubReviewApp.Tests.Review;

/// <summary>
/// The schema of a checklist answer: what the API is asked to enforce, that it uses no keyword the API refuses, and that every limit
/// the API cannot enforce is at least stated to the model.
/// </summary>
public class ChecklistSchemaTests
{
    [Fact]
    public void Json_GivenTheSchema_Should_UseNoKeywordThatStructuredOutputsDoNotSupport()
    {
        // Arrange
        var schema = Schema();

        // Act
        var keywords = JsonSchemaWalker.AllKeywords(schema);

        // Assert — numeric, length and array-size constraints are refused by the API with a 400, which would fail every review
        keywords.Should().NotContain(["minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf", "minLength", "maxLength", "minItems", "maxItems"]);
    }

    [Fact]
    public void Json_GivenTheSchema_Should_ForbidAdditionalPropertiesAndRequireEveryPropertyOnEveryObject()
    {
        // Arrange — structured outputs need this on every object schema, however deeply nested, and a property that is not required may be left out
        var objects = JsonSchemaWalker.ObjectSchemas(Schema());

        // Act
        var violations = objects
            .Where(entry =>
                !(entry.Schema.TryGetProperty("additionalProperties", out var extras) && extras.ValueKind == JsonValueKind.False)
                || !entry.Schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Order()
                    .SequenceEqual(entry.Schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order()))
            .Select(entry => entry.Path)
            .ToList();

        // Assert — the answer, a check and a finding
        objects.Select(entry => entry.Path).Should().Equal("$", "$.properties.checks.items", "$.properties.findings.items");
        violations.Should().BeEmpty();
    }

    [Fact]
    public void Json_GivenTheSchema_Should_NameEveryFieldTheReaderReads()
    {
        // Arrange
        var schema = Schema();

        // Act
        var answerFields = PropertyNames(schema);
        var checkFields = PropertyNames(Items(schema, "checks"));
        var findingFields = PropertyNames(Items(schema, "findings"));

        // Assert — a field named differently here would be read as missing from every answer
        answerFields.Should().Equal("summary", "checks", "findings", "coverage_note");
        checkFields.Should().Equal("id", "status", "note", "serious");
        findingFields.Should().Equal("check", "file", "line", "issue", "fix");
    }

    [Fact]
    public void Json_GivenTheCheckIdEnums_Should_ListExactlyTheChecklistWireIdsInOrder()
    {
        // Arrange
        var schema = Schema();
        var expected = Checklist.Order.Select(Checklist.WireId).ToList();

        // Act
        var checkIds = EnumValues(Field(schema, "checks", "id"));
        var findingChecks = EnumValues(Field(schema, "findings", "check"));

        // Assert — the reader drops an id that names no check, so the model must be offered exactly the eight
        checkIds.Should().Equal(expected);
        findingChecks.Should().Equal(expected);
    }

    [Fact]
    public void Json_GivenTheStatusEnum_Should_ListPassConcernFailAndNa()
    {
        // Arrange
        var schema = Schema();

        // Act
        var statuses = EnumValues(Field(schema, "checks", "status"));

        // Assert
        statuses.Should().Equal("pass", "concern", "fail", "na");
    }

    [Fact]
    public void Json_GivenTheSchema_Should_TypeLineAsIntegerAndSeriousAsBoolean()
    {
        // Arrange
        var schema = Schema();

        // Act
        var lineType = Field(schema, "findings", "line").GetProperty("type").GetString();
        var seriousType = Field(schema, "checks", "serious").GetProperty("type").GetString();

        // Assert — the reader takes only a whole number as a line, and only the boolean true as serious
        lineType.Should().Be("integer");
        seriousType.Should().Be("boolean");
    }

    [Fact]
    public void Json_GivenTheLimitsTheApiCannotEnforce_Should_StateThemInTheDescriptions()
    {
        // Arrange
        var schema = Schema();

        // Act
        var summary = Description(schema.GetProperty("properties").GetProperty("summary"));
        var checks = Description(schema.GetProperty("properties").GetProperty("checks"));
        var note = Description(Field(schema, "checks", "note"));
        var findings = Description(schema.GetProperty("properties").GetProperty("findings"));
        var file = Description(Field(schema, "findings", "file"));
        var issue = Description(Field(schema, "findings", "issue"));
        var fix = Description(Field(schema, "findings", "fix"));
        var coverageNote = Description(schema.GetProperty("properties").GetProperty("coverage_note"));

        // Assert — the reader enforces each limit; the description is the only place the model learns it
        summary.Should().Contain(Number(ChecklistAnswerReader.MaxSummaryChars));
        checks.Should().Contain("eight");
        note.Should().Contain(Number(ChecklistAnswerReader.MaxNoteChars));
        findings.Should().Contain("eight");
        file.Should().Contain(Number(ChecklistAnswerReader.MaxFileChars));
        issue.Should().Contain(Number(ChecklistAnswerReader.MaxIssueChars));
        fix.Should().Contain(Number(ChecklistAnswerReader.MaxFixChars));
        coverageNote.Should().Contain(Number(ChecklistAnswerReader.MaxCoverageNoteChars));
    }

    private static JsonElement Schema()
    {
        return JsonDocument.Parse(ChecklistSchema.Json).RootElement.Clone();
    }

    /// <summary>The schema of one entry of the array property <paramref name="arrayName"/>.</summary>
    private static JsonElement Items(JsonElement schema, string arrayName)
    {
        return schema.GetProperty("properties").GetProperty(arrayName).GetProperty("items");
    }

    /// <summary>The schema of the field <paramref name="fieldName"/> in an entry of the array property <paramref name="arrayName"/>.</summary>
    private static JsonElement Field(JsonElement schema, string arrayName, string fieldName)
    {
        return Items(schema, arrayName).GetProperty("properties").GetProperty(fieldName);
    }

    private static List<string> PropertyNames(JsonElement objectSchema)
    {
        return objectSchema.GetProperty("properties").EnumerateObject().Select(property => property.Name).ToList();
    }

    private static List<string?> EnumValues(JsonElement fieldSchema)
    {
        return fieldSchema.GetProperty("enum").EnumerateArray().Select(value => value.GetString()).ToList();
    }

    private static string? Description(JsonElement fieldSchema)
    {
        return fieldSchema.GetProperty("description").GetString();
    }

    private static string Number(int value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }
}
