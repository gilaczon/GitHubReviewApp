namespace GitHubReviewApp.Models;

/// <summary>
/// The user message of a checklist review and the changed files it describes. <paramref name="Files"/> lists every changed file in listing
/// order (highest risk first, then GitHub's order), including those the message does not list by name.
/// </summary>
public sealed record ReviewInput(string UserMessage, IReadOnlyList<ReviewInputFile> Files)
{
    /// <summary>Files whose diff was sent, whole or cut.</summary>
    public int IncludedFileCount => Files.Count(file => file.Inclusion is DiffInclusion.Included or DiffInclusion.Partial);

    /// <summary>Files whose diff was sent cut.</summary>
    public int PartialFileCount => Files.Count(file => file.Inclusion == DiffInclusion.Partial);

    /// <summary>Files whose diff did not fit the budget.</summary>
    public int OverBudgetFileCount => Files.Count(file => file.Inclusion == DiffInclusion.OverBudget);

    /// <summary>Lockfiles, binaries and minified files, which are never read.</summary>
    public int NotReadFileCount => Files.Count(file => file.Inclusion is DiffInclusion.Lockfile or DiffInclusion.BinaryOrMinified);

    /// <summary>Files GitHub sent no patch for.</summary>
    public int NoDiffFileCount => Files.Count(file => file.Inclusion == DiffInclusion.NoDiff);
}
