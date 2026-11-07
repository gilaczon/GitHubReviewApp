namespace GitHubReviewApp.Review;

/// <summary>
/// The JSON schema passed to the API as the structured-output format, so the reply is always parseable.
/// Structured outputs require <c>additionalProperties: false</c> on every object. The severity list must
/// match <see cref="ReviewSeverity"/>; a unit test enforces that.
/// </summary>
public static class ReviewResponseSchema
{
    public const string Json = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["summary", "findings", "omitted_findings"],
          "properties": {
            "summary": {
              "type": "string",
              "description": "One to three sentences describing the change and the overall assessment."
            },
            "omitted_findings": {
              "type": "integer",
              "description": "How many further problems you found but left out of the list because of the limit on the number of findings. 0 if none."
            },
            "findings": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["path", "line", "severity", "category", "title", "body"],
                "properties": {
                  "path": {
                    "type": "string",
                    "description": "Path of the file exactly as shown in the file element."
                  },
                  "line": {
                    "type": "integer",
                    "description": "Line number in the new version of the file, as shown in the diff prefix. 0 if the finding concerns the whole file or a removed line."
                  },
                  "severity": {
                    "type": "string",
                    "enum": ["low", "medium", "high", "critical"]
                  },
                  "category": {
                    "type": "string",
                    "enum": ["bug", "security", "performance", "error-handling", "concurrency", "tests", "maintainability", "other"]
                  },
                  "title": {
                    "type": "string",
                    "description": "A short headline for the problem."
                  },
                  "body": {
                    "type": "string",
                    "description": "The concrete failure scenario and what to change. Markdown allowed."
                  }
                }
              }
            }
          }
        }
        """;
}
