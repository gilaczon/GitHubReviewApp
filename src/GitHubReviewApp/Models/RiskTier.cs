namespace GitHubReviewApp.Models;

/// <summary>
/// How much is at stake in a changed file (design 5.3). Declared in listing order: the review lists and sends files in this order, highest risk
/// first, so when the diff budget runs out it is the lower tiers that are left out.
/// </summary>
public enum RiskTier
{
    Auth = 0,
    Payments = 1,
    Cards = 2,
    BankLogin = 3,
    PersonalData = 4,

    /// <summary>Build and release pipelines, container files and infrastructure as code.</summary>
    Pipelines = 5,

    Config = 6,

    /// <summary>Everything that matches no other tier.</summary>
    Source = 7,

    Tests = 8,
    Docs = 9,

    /// <summary>Generated or vendored code: listed after everything the team wrote, but still sent when the budget allows.</summary>
    GeneratedOrVendored = 10,

    /// <summary>Dependency lockfiles, binaries and minified or bundled files. Listed, never sent.</summary>
    NotReadable = 11
}
