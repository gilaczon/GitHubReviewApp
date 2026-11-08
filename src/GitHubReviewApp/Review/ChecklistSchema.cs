namespace GitHubReviewApp.Review;

/// <summary>
/// The JSON schema of a checklist answer, passed to the API as the structured-output format. It is the spec's schema without the keywords
/// structured outputs refuse with a 400 (<c>maxLength</c>, <c>minItems</c>, <c>maxItems</c>): each of those limits is stated in the field's
/// description instead, and <see cref="ChecklistAnswerReader"/> enforces it while reading.
/// </summary>
public static class ChecklistSchema
{
    public const string Json = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["summary", "checks", "findings", "coverage_note"],
          "properties": {
            "summary": { "type": "string", "description": "One or two plain sentences, at most 240 characters." },
            "checks": {
              "type": "array",
              "description": "Exactly eight entries, one for each check id.",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["id", "status", "note", "serious"],
                "properties": {
                  "id": { "type": "string", "enum": ["standards-design", "functionality", "performance", "security", "testing", "error-handling", "scalability", "dependencies"] },
                  "status": { "type": "string", "enum": ["pass", "concern", "fail", "na"] },
                  "note": { "type": "string", "description": "At most 100 characters. Empty for pass and na." },
                  "serious": { "type": "boolean" }
                }
              }
            },
            "findings": {
              "type": "array",
              "description": "At most eight entries, worst first.",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["check", "file", "line", "issue", "fix"],
                "properties": {
                  "check": { "type": "string", "enum": ["standards-design", "functionality", "performance", "security", "testing", "error-handling", "scalability", "dependencies"] },
                  "file": { "type": "string", "description": "At most 250 characters." },
                  "line": { "type": "integer" },
                  "issue": { "type": "string", "description": "One sentence, at most 160 characters." },
                  "fix": { "type": "string", "description": "One sentence, at most 120 characters." }
                }
              }
            },
            "coverage_note": { "type": "string", "description": "At most 160 characters. Empty when the whole diff was included." }
          }
        }
        """;
}
