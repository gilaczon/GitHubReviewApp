namespace GitHubReviewApp.Models;

/// <summary>
/// The model a review runs on, and why (design 5.4). <paramref name="Reason"/> is <c>high_risk</c> for Opus, and for Sonnet either <c>low_risk</c>
/// (a large change that touches nothing risky) or <c>small_change</c>; it is also the value of the model-choice metric's tag.
/// </summary>
public sealed record ModelChoice(string Model, bool IsOpus, string Reason);
