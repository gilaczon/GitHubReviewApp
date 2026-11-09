namespace GitHubReviewApp.Review;

/// <summary>
/// Picks the model for a review (design 5.4). Opus when the change is over <see cref="ReviewOptions.OpusLineThreshold"/> lines and it touches a
/// high-risk area; Sonnet otherwise. A change touches a high-risk area when a changed file is in the auth, payments, cards, bank login,
/// personal data or pipelines tier (tests, docs, generated and unreadable files never count), when the repository's name carries one of the
/// same risk keywords, or when the repository is listed in <see cref="ReviewOptions.HighRiskRepositories"/>.
/// </summary>
public sealed class ModelSelector
{
    private const string HighRiskReason = "high_risk";
    private const string LowRiskReason = "low_risk";
    private const string SmallChangeReason = "small_change";

    private readonly ReviewOptions _options;

    public ModelSelector(IOptions<ReviewOptions> options)
    {
        _options = options.Value;
    }

    public ModelChoice Choose(string owner, string repo, PullRequestInfo pullRequest, ReviewInput input, RuntimeSettings runtime)
    {
        var isLarge = pullRequest.ChangedLineCount > _options.OpusLineThreshold;
        var isRisky = IsRisky(owner, repo, input);

        if (isLarge && isRisky)
        {
            return new ModelChoice(runtime.OpusModel, IsOpus: true, HighRiskReason);
        }

        return new ModelChoice(runtime.SonnetModel, IsOpus: false, isLarge ? LowRiskReason : SmallChangeReason);
    }

    // The tiers are declared highest risk first, and Pipelines is the last of the high-risk ones.
    private bool IsRisky(string owner, string repo, ReviewInput input)
    {
        return input.Files.Any(file => file.Tier <= RiskTier.Pipelines)
            || RiskKeywords.MatchHighRiskTier(repo) is not null
            || _options.HighRiskRepositories.Contains($"{owner}/{repo}", StringComparer.OrdinalIgnoreCase);
    }
}
