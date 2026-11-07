namespace GitHubReviewApp.Models;

/// <summary>A fully built prompt for one review request. Prompt construction lives in <c>ReviewPromptBuilder</c>.</summary>
public sealed record ClaudeReviewRequest(string SystemPrompt, string UserMessage);
