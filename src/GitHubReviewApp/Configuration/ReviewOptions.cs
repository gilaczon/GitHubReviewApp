namespace GitHubReviewApp.Configuration;

/// <summary>
/// Budgets and thresholds for reviewing pull requests of different sizes.
/// Bound from the "Review" configuration section. The diff budgets are in UTF-8 bytes, not characters: a token
/// is at least one byte, so bytes bound the tokens however unusual the text is (code runs at about 3 to 4 bytes per
/// token), while a count of characters does not, because one emoji or rare character can be several tokens.
/// </summary>
public sealed class ReviewOptions
{
    public const string SectionName = "Review";

    private static readonly string[] _defaultGuidelinePaths = [".github/ai-review.md", "CLAUDE.md"];

    /// <summary>The values of a pull request's <c>author_association</c> that GitHub documents. Anything else in the allow list matches nobody.</summary>
    public static IReadOnlyList<string> KnownAuthorAssociations { get; } =
        ["COLLABORATOR", "CONTRIBUTOR", "FIRST_TIMER", "FIRST_TIME_CONTRIBUTOR", "MANNEQUIN", "MEMBER", "NONE", "OWNER"];

    /// <summary>Largest amount of rendered diff sent in one Claude request, in UTF-8 bytes.</summary>
    public int MaxChunkBytes { get; init; } = 300_000;

    /// <summary>Largest amount of rendered diff reviewed for one pull request across all requests, in UTF-8 bytes.</summary>
    public int MaxTotalBytes { get; init; } = 1_200_000;

    /// <summary>A single file larger than this many UTF-8 bytes is cut at a line boundary and reported as partially reviewed.</summary>
    public int MaxFileBytes { get; init; } = 60_000;

    /// <summary>
    /// Largest amount of rendered diff a checklist review sends, in UTF-8 bytes. Files are taken highest risk first; one that does not fit
    /// what is left is listed as left out, and the walk goes on to the next.
    /// </summary>
    public int MaxDiffBytes { get; init; } = 180_000;

    /// <summary>Most changed files a checklist review lists by name. The rest are counted in one line.</summary>
    public int MaxListedFiles { get; init; } = 1_000;

    /// <summary>A pull request description longer than this many characters is cut, and the prompt says it was.</summary>
    public int MaxDescriptionChars { get; init; } = 10_000;

    /// <summary>Most files reviewed for one pull request.</summary>
    public int MaxFiles { get; init; } = 150;

    /// <summary>How many Claude requests run at once for a multi-part review.</summary>
    public int MaxParallelChunks { get; init; } = 3;

    /// <summary>How many Claude requests this process has in flight at once, across every review it is running.</summary>
    public int MaxConcurrentModelRequests { get; init; } = 6;

    /// <summary>
    /// The longest one review may take from start to finish, GitHub calls, model requests and the wait for a free model slot included. When it runs out the
    /// work is cancelled and the message is retried, with the parts that did finish kept. It exists so that a review is ended by the app and not by the
    /// Functions host: when the host's own timeout fires, it restarts the worker, which takes every other review the instance is running with it. The host's
    /// timeout must therefore be longer than this, and this at least as long as <c>Claude:RequestTimeoutSeconds</c>. A review of the largest size is
    /// at most two waves of three parts (<see cref="MaxParallelChunks"/>), so twice that request time is the longest it needs.
    /// </summary>
    public int MaxReviewDurationSeconds { get; init; } = 1_200;

    /// <summary>Most findings posted as inline comments; the rest are listed in the review body.</summary>
    public int MaxInlineComments { get; init; } = 25;

    /// <summary>Most findings kept in one review; lower-severity findings beyond this are counted, not listed.</summary>
    public int MaxFindings { get; init; } = 60;

    /// <summary>A pull request touching more files than this gets a suggestion to split it.</summary>
    public int LargePullRequestFileThreshold { get; init; } = 50;

    /// <summary>A pull request changing more lines than this gets a suggestion to split it.</summary>
    public int LargePullRequestLineThreshold { get; init; } = 2000;

    /// <summary>
    /// When not empty, only pull requests whose author has one of these relationships to the repository (OWNER, MEMBER, COLLABORATOR...) are
    /// reviewed automatically. Empty means everyone, which is right for a private repository but lets anyone spend model tokens on a public one.
    /// </summary>
    public string[] AllowedAuthorAssociations { get; init; } = [];

    /// <summary>
    /// The allow list as it is compared: trimmed, upper case, without blanks or repeats. The validator and the webhook receiver both read it
    /// this way, so an entry the validator accepts is an entry the receiver matches.
    /// </summary>
    public IReadOnlyList<string> EffectiveAllowedAuthorAssociations =>
        AllowedAuthorAssociations
            .Where(association => !string.IsNullOrWhiteSpace(association))
            .Select(association => association.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Repository files, in order of preference, holding review guidelines. Read from the base branch only. Null means the defaults;
    /// set it to replace them. It has no default value of its own because the configuration binder adds configured elements to an array that
    /// already has one, which would leave the defaults in front of an operator's own file.
    /// </summary>
    public string[]? GuidelinePaths { get; init; }

    /// <summary>The guideline files to look for: the configured ones, or the defaults when none are configured.</summary>
    public IReadOnlyList<string> EffectiveGuidelinePaths => GuidelinePaths ?? _defaultGuidelinePaths;

    /// <summary>Guidelines longer than this are cut.</summary>
    public int MaxGuidelinesChars { get; init; } = 20_000;
}
