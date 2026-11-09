namespace GitHubReviewApp.Models;

/// <summary>
/// What the comment's footer says about the pull request and the review: the repository and number, the branches, the author, the commit, and
/// the size of the change. <paramref name="FileCount"/> counts the changed files: the ones GitHub listed plus the ones it did not.
/// <paramref name="IncludedFileCount"/> counts the files whose diff was sent whole or cut, and <paramref name="LeftOutFileCount"/> those not
/// reviewed in full: cut, left out for their size, never read (lockfiles, binaries, minified files), sent with no diff by GitHub, or not listed by
/// GitHub at all (a pull request of more than 3,000 files). <paramref name="Models"/> holds the models that answered, in run order and each once,
/// and is empty for a review that failed.
/// </summary>
public sealed record ReviewContext(
    string Owner,
    string Repo,
    int Number,
    string HeadRef,
    string BaseRef,
    string Author,
    string HeadSha,
    int FileCount,
    int Additions,
    int Deletions,
    int IncludedFileCount,
    int LeftOutFileCount,
    IReadOnlyList<string> Models);
