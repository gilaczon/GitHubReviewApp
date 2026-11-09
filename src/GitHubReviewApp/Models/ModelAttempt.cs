namespace GitHubReviewApp.Models;

/// <summary>
/// One attempt the API made for a run, and the tokens it used. A run whose request moved to another model after a refusal has one attempt per
/// model, so each is priced at its own model's rates.
/// </summary>
public sealed record ModelAttempt(string Model, ClaudeUsage Usage);
