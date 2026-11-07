namespace GitHubReviewApp.Tests.Review;

public class ReviewResponseSchemaTests
{
    private static JsonElement Schema()
    {
        return JsonDocument.Parse(ReviewResponseSchema.Json).RootElement.Clone();
    }

    [Fact]
    public void Json_GivenTheSchema_Should_BeAnObjectSchemaRequiringSummaryFindingsAndTheOmittedCount()
    {
        // Arrange

        // Act
        var schema = Schema();

        // Assert
        schema.GetProperty("type").GetString().Should().Be("object");
        schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo("summary", "findings", "omitted_findings");
    }

    [Fact]
    public void Json_GivenTheSchema_Should_AskForTheNumberOfFindingsTheModelLeftOutAsAnInteger()
    {
        // Arrange

        // Act
        var omitted = Schema().GetProperty("properties").GetProperty("omitted_findings");

        // Assert
        omitted.GetProperty("type").GetString().Should().Be("integer");
        omitted.GetProperty("description").GetString().Should().NotBeNullOrWhiteSpace();
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

        // Assert
        objects.Should().HaveCountGreaterThan(1, "the schema has a root object and the finding object");
        violations.Should().BeEmpty();
    }

    [Fact]
    public void Json_GivenTheSchema_Should_UseNoKeywordThatStructuredOutputsDoNotSupport()
    {
        // Arrange

        // Act
        var keywords = JsonSchemaWalker.AllKeywords(Schema());

        // Assert — numeric and length constraints are rejected by the API, which would fail every review with a 400
        keywords.Should().NotContain(["minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf", "minLength", "maxLength", "minItems", "maxItems"]);
    }

    [Fact]
    public void Json_GivenTheSchema_Should_DescribeFindingsWithEveryFieldTheParserReads()
    {
        // Arrange
        var finding = Schema().GetProperty("properties").GetProperty("findings").GetProperty("items");

        // Act
        var required = finding.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();

        // Assert
        required.Should().BeEquivalentTo("path", "line", "severity", "category", "title", "body");
        finding.GetProperty("properties").GetProperty("line").GetProperty("type").GetString().Should().Be("integer");
    }

    [Fact]
    public void Json_GivenTheSeverityEnum_Should_ListExactlyTheSeverityNamesThatTheEnumDeclaresInLowerCase()
    {
        // Arrange
        var severities = Schema()
            .GetProperty("properties").GetProperty("findings").GetProperty("items")
            .GetProperty("properties").GetProperty("severity").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()).ToList();

        // Act
        var expected = Enum.GetNames<ReviewSeverity>().Select(name => name.ToLowerInvariant());

        // Assert — guards against adding an enum member without updating the schema
        severities.Should().BeEquivalentTo(expected);
    }
}
