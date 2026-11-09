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

    /// <summary>The values of a pull request's <c>author_association</c> that GitHub documents. Anything else in the allow list matches nobody.</summary>
    public static IReadOnlyList<string> KnownAuthorAssociations { get; } =
        ["COLLABORATOR", "CONTRIBUTOR", "FIRST_TIMER", "FIRST_TIME_CONTRIBUTOR", "MANNEQUIN", "MEMBER", "NONE", "OWNER"];

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

    /// <summary>
    /// A review runs on Opus only when the change is over this many lines, additions plus deletions, and it touches a high-risk area (design 5.4).
    /// A change of exactly this many lines is not over it.
    /// </summary>
    public int OpusLineThreshold { get; init; } = 40;

    /// <summary>
    /// Repositories, each written <c>owner/repo</c> and compared without regard to letter case, that always count as a high-risk area, whatever
    /// their name or files say. It is for a repository whose name does not say what it holds.
    /// </summary>
    public string[] HighRiskRepositories { get; init; } = [];

    /// <summary>
    /// How many Claude requests this process has in flight at once, across every review it is running. Each review runs two at once, so 12 lets
    /// the six reviews an instance takes from the queue run without waiting for one another.
    /// </summary>
    public int MaxConcurrentModelRequests { get; init; } = 12;

    /// <summary>
    /// The longest one review may take from start to finish, GitHub calls, model requests and the wait for a free model slot included. When it runs out the
    /// work is cancelled and the message is retried, with a run that did finish kept. It exists so that a review is ended by the app and not by the
    /// Functions host: when the host's own timeout fires, it restarts the worker, which takes every other review the instance is running with it. The host's
    /// timeout must therefore be longer than this, and this at least as long as <c>Claude:RequestTimeoutSeconds</c>. The two runs of a review are
    /// sent at once, so twice that request time leaves the other half for a wait behind other reviews for a model slot.
    /// </summary>
    public int MaxReviewDurationSeconds { get; init; } = 1_200;

    /// <summary>
    /// When not empty, only pull requests whose author has one of these relationships to the repository (OWNER, MEMBER, COLLABORATOR...) are
    /// reviewed automatically. Empty means everyone, which is right for a private repository but lets anyone spend model tokens on a public one.
    /// </summary>
    public string[] AllowedAuthorAssociations { get; init; } = [];

    /// <summary>
    /// When not empty, only pull requests that would merge into one of these branches are reviewed automatically; one into any other branch is
    /// ignored by the webhook. Compared without regard to letter case. Empty means every branch.
    /// </summary>
    public string[] TargetBranches { get; init; } = [];

    /// <summary>The target branches as they are compared: trimmed, without blanks or repeats.</summary>
    public IReadOnlyList<string> EffectiveTargetBranches =>
        TargetBranches
            .Where(branch => !string.IsNullOrWhiteSpace(branch))
            .Select(branch => branch.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

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
}
