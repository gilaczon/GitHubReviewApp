namespace GitHubReviewApp.Models;

/// <summary>One checklist run's request: the model to ask, the system prompt (sent cacheable) and the user message.</summary>
public sealed record ClaudeRunRequest(string Model, string SystemPrompt, string UserMessage);
