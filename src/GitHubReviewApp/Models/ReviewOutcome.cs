namespace GitHubReviewApp.Models;

/// <summary>A verdict with the number of serious comments, and the words the comment and the check run show for it.</summary>
public sealed record ReviewOutcome(ReviewVerdict Verdict, int SeriousCount)
{
    /// <summary>The verdict's name, for example "PASS WITH COMMENTS".</summary>
    public string VerdictText => Verdict switch
    {
        ReviewVerdict.Pass => "PASS",
        ReviewVerdict.PassWithComments => "PASS WITH COMMENTS",
        _ => "FAIL"
    };

    /// <summary>The verdict's name, followed by the serious count when it is above zero, for example "PASS WITH COMMENTS (1 serious)".</summary>
    public string Headline => SeriousCount > 0 ? $"{VerdictText} ({SeriousCount} serious)" : VerdictText;

    /// <summary>What the verdict means for the person about to approve.</summary>
    public string Meaning => Verdict switch
    {
        ReviewVerdict.Pass => "no concerns",
        ReviewVerdict.PassWithComments when SeriousCount > 0 => "no security problem, but read the serious comment before approving",
        ReviewVerdict.PassWithComments => "fine to approve; the author should look at the comments",
        _ => "changes required"
    };
}
