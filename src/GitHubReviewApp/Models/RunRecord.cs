namespace GitHubReviewApp.Models;

/// <summary>
/// One model run of a finished review, as the review record logs it (design 5.10). <paramref name="Outcome"/> is the <see cref="RunOutcome"/>
/// name, or <c>transient_failure</c> or <c>permanent_failure</c> for a run that threw. <paramref name="Model"/> is the model that answered, or
/// the one asked for when none did. <paramref name="OwnVerdict"/> is set for a completed run only, and <paramref name="Statuses"/> lists its
/// eight checks as <c>standards-design=pass functionality=concern* ...</c>, a <c>*</c> marking a serious concern; it is empty for any other run.
/// <paramref name="Cost"/> is in US dollars, and null when it is not known.
/// </summary>
public sealed record RunRecord(int Index, string Outcome, string Model, ReviewVerdict? OwnVerdict, string Statuses, ClaudeUsage Usage, decimal? Cost);
