namespace GitHubReviewApp.Models;

/// <summary>The result of a review, derived from the check ratings and never stated by the model. Declared in ascending order of badness, so a larger value is worse.</summary>
public enum ReviewVerdict
{
    Pass = 0,
    PassWithComments = 1,
    Fail = 2
}
