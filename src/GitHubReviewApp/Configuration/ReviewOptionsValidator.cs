namespace GitHubReviewApp.Configuration;

/// <summary>
/// Rejects review settings that could never work, when the host starts. A zero budget would skip every file, and a misspelt author
/// association would match no pull request, so every review would be skipped and nothing would say why.
/// </summary>
public sealed class ReviewOptionsValidator : IValidateOptions<ReviewOptions>
{
    private const int MaxValueCharsShown = 100;

    // The planner skips a file that would be cut to less than this, so a smaller budget would review nothing and say nothing.
    private const int MinimumUsefulBytes = 2_000;

    // A cut slice under 2,000 bytes is left out as a line too long to show, which is true only when the file limit leaves room for many
    // lines. Just above 2,000, almost every file that needs a cut would be left out for that reason, though its lines are short.
    private const int MinimumFileBytes = 10_000;

    // The guidelines buffer is allocated at this size for every pull request.
    private const int MaxGuidelinesCharsCap = 200_000;

    // A day. Far beyond any Functions timeout, and well inside what a timer can count.
    private const int MaxReviewDurationSecondsCap = 86_400;

    private readonly IConfiguration? _configuration;
    private readonly IOptions<ClaudeOptions>? _claudeOptions;

    /// <param name="configuration">
    /// Needed to see a list written as one value, which the binder drops without a word. Without it that one check is skipped.
    /// </param>
    /// <param name="claudeOptions">
    /// Needed to check that a review may last as long as one model request. Without it that one check is skipped.
    /// </param>
    public ReviewOptionsValidator(IConfiguration? configuration = null, IOptions<ClaudeOptions>? claudeOptions = null)
    {
        _configuration = configuration;
        _claudeOptions = claudeOptions;
    }

    public ValidateOptionsResult Validate(string? name, ReviewOptions options)
    {
        var failures = new List<string>();

        RejectListWrittenAsOneValue(failures, nameof(ReviewOptions.AllowedAuthorAssociations));
        RejectListWrittenAsOneValue(failures, nameof(ReviewOptions.GuidelinePaths));
        RejectListWrittenAsOneValue(failures, nameof(ReviewOptions.HighRiskRepositories));

        RequireAtLeast(failures, "MaxChunkBytes", options.MaxChunkBytes, MinimumUsefulBytes);
        RequireAtLeast(failures, "MaxFileBytes", options.MaxFileBytes, MinimumFileBytes);
        RequireAtLeast(failures, "MaxTotalBytes", options.MaxTotalBytes, Math.Max(options.MaxChunkBytes, 1));
        RequireAtLeast(failures, "MaxFiles", options.MaxFiles, 1);
        RequireAtLeast(failures, "MaxParallelChunks", options.MaxParallelChunks, 1);
        RequireAtLeast(failures, "MaxConcurrentModelRequests", options.MaxConcurrentModelRequests, 1);
        RequireAtLeast(failures, "MaxInlineComments", options.MaxInlineComments, 0);
        RequireAtLeast(failures, "MaxFindings", options.MaxFindings, 1);
        RequireAtLeast(failures, "LargePullRequestFileThreshold", options.LargePullRequestFileThreshold, 1);
        RequireAtLeast(failures, "LargePullRequestLineThreshold", options.LargePullRequestLineThreshold, 1);
        RequireAtLeast(failures, "MaxGuidelinesChars", options.MaxGuidelinesChars, 1);
        RequireAtMost(failures, "MaxGuidelinesChars", options.MaxGuidelinesChars, MaxGuidelinesCharsCap);
        RequireAtLeast(failures, "MaxReviewDurationSeconds", options.MaxReviewDurationSeconds, 1);
        RequireAtMost(failures, "MaxReviewDurationSeconds", options.MaxReviewDurationSeconds, MaxReviewDurationSecondsCap);
        RequireAtLeast(failures, "MaxDiffBytes", options.MaxDiffBytes, 1);
        RequireAtLeast(failures, "MaxListedFiles", options.MaxListedFiles, 1);
        RequireAtLeast(failures, "MaxDescriptionChars", options.MaxDescriptionChars, 1);
        RequireAtLeast(failures, "OpusLineThreshold", options.OpusLineThreshold, 0);

        if (options.MaxFileBytes > options.MaxDiffBytes)
        {
            failures.Add(
                $"Review:MaxFileBytes is {options.MaxFileBytes}, which is more than the {options.MaxDiffBytes} bytes of Review:MaxDiffBytes. A file cut " +
                "to that size could be larger than the whole diff, and would then be left out where a smaller cut would have fitted.");
        }

        if (_claudeOptions?.Value is { } claude && options.MaxReviewDurationSeconds < claude.RequestTimeoutSeconds)
        {
            failures.Add(
                $"Review:MaxReviewDurationSeconds is {options.MaxReviewDurationSeconds}, which is shorter than the {claude.RequestTimeoutSeconds} seconds that " +
                "Claude:RequestTimeoutSeconds allows one model request. A request that is allowed that long could never finish inside such a review.");
        }

        foreach (var association in options.AllowedAuthorAssociations)
        {
            if (string.IsNullOrWhiteSpace(association)
                || !ReviewOptions.KnownAuthorAssociations.Contains(association.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                failures.Add(
                    $"Review:AllowedAuthorAssociations has '{association}', which is not an author association GitHub uses " +
                    $"({string.Join(", ", ReviewOptions.KnownAuthorAssociations)}). No pull request would ever match it.");
            }
        }

        foreach (var repository in options.HighRiskRepositories)
        {
            if (!IsOwnerAndRepository(repository))
            {
                failures.Add(
                    $"Review:HighRiskRepositories has '{repository}', which is not written owner/repo, with no spaces. " +
                    "It would match no repository, so a repository that needs the stronger model would be reviewed by the other one.");
            }
        }

        foreach (var path in options.GuidelinePaths ?? [])
        {
            if (!IsRepositoryRelativePath(path))
            {
                failures.Add(
                    $"Review:GuidelinePaths entry '{path}' must be a path inside the repository, written with forward slashes, " +
                    "not rooted and without '..'.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>
    /// "Review__AllowedAuthorAssociations=OWNER,MEMBER" is one value, and the binder never reads a list from one value. Alone, the value is refused by
    /// the app's strict binding, and a binder that is not strict leaves the list at its default, which for the author allow list is empty: an empty
    /// list lets every author trigger a review, a typo that opens the gate. Next to entries from elsewhere, such as the repository appsettings.json
    /// seeds in Review:HighRiskRepositories, even the strict binding drops the value without a word and keeps only those entries. So any value that
    /// is not blank is refused here, whether or not the list has entries.
    /// </summary>
    private void RejectListWrittenAsOneValue(List<string> failures, string setting)
    {
        var section = _configuration?.GetSection($"{ReviewOptions.SectionName}:{setting}");

        if (section?.Value is not { } value || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var shown = value.Length > MaxValueCharsShown ? value[..MaxValueCharsShown] + "…" : value;
        var outcome = section.GetChildren().Any()
            ? "so the setting would keep only the entries it is given one by one"
            : "so the setting would stay at its default";

        failures.Add(
            $"Review:{setting} is a list, but it is set to the single value '{shown}', which the configuration binder ignores, {outcome}. " +
            $"Write one entry per item: a JSON array, or Review__{setting}__0, Review__{setting}__1 and so on in the app settings.");
    }

    private static void RequireAtLeast(List<string> failures, string setting, int value, int minimum)
    {
        if (value < minimum)
        {
            failures.Add($"Review:{setting} must be at least {minimum}, but it is {value}.");
        }
    }

    private static void RequireAtMost(List<string> failures, string setting, int value, int maximum)
    {
        if (value > maximum)
        {
            failures.Add($"Review:{setting} must be at most {maximum}, but it is {value}.");
        }
    }

    private static bool IsOwnerAndRepository(string? entry)
    {
        if (string.IsNullOrWhiteSpace(entry) || entry.Any(char.IsWhiteSpace))
        {
            return false;
        }

        var parts = entry.Split('/');

        return parts.Length == 2 && parts.All(part => part.Length > 0);
    }

    private static bool IsRepositoryRelativePath(string? path)
    {
        return !string.IsNullOrWhiteSpace(path)
            && !path.StartsWith('/')
            && !path.Contains('\\')
            && !path.Split('/').Contains("..");
    }
}
