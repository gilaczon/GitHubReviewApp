namespace GitHubReviewApp.Models;

/// <summary>How one check was rated. Declared in ascending order of strictness, so the stricter of two ratings is the larger value.</summary>
public enum CheckStatus
{
    NotApplicable = 0,
    Pass = 1,
    Concern = 2,
    Fail = 3
}
