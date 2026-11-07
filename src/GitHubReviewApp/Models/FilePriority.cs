namespace GitHubReviewApp.Models;

/// <summary>
/// How early a file is reviewed when the pull request is too big to review whole.
/// Lower values are reviewed first.
/// </summary>
public enum FilePriority
{
    /// <summary>Application code, SQL, build pipelines, Dockerfiles and infrastructure as code.</summary>
    Source = 0,

    /// <summary>Configuration and project files.</summary>
    Configuration = 1,

    /// <summary>Test code and fixtures.</summary>
    Tests = 2,

    /// <summary>Documentation, styles and anything unrecognised.</summary>
    Other = 3,

    /// <summary>
    /// Generated or vendored code. It is reviewed after everything else and only if the budget allows, never dropped on the strength of its
    /// name or a comment alone: it still compiles and runs, and its author controls both.
    /// </summary>
    GeneratedOrVendored = 4
}
