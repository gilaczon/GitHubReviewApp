namespace GitHubReviewApp.Services;

/// <summary>
/// What a pull request author is told when a review fails. Deliberately generic: exception messages can
/// carry internal detail and are kept in the logs, never in a pull request comment. The author is usually not
/// the person who runs the app, so the text does not send them to its logs.
/// </summary>
public static class ReviewFailureReasons
{
    public const string PermanentError =
        "The automated review stopped because of an error that retrying would not fix. " +
        "If this keeps happening, please tell whoever runs this review app.";

    // Names no cause on purpose. All the app knows is that every attempt failed; a timeout under its own limits, a spending cap or a changed
    // reply look the same from here as an outage does, so a notice that blamed one would sometimes be wrong. How to try again is not said here:
    // the notice ChecklistCommentFormatter.FormatFailure builds around every reason says it once.
    public const string RetriesExhausted =
        "The automated review could not be completed after several attempts. " +
        "This is usually temporary. " +
        "If it keeps happening, please tell whoever runs this review app.";

    // What a review says when both model runs ended without an answer (design 5.12). The first four name what both runs ended with; the last
    // covers runs that ended differently.
    public const string AllRefused = "Both reviews were declined by the AI service's safety checks.";

    public const string AllTruncated = "Both reviews ran out of output space before finishing.";

    public const string AllInvalid = "Neither review returned a readable answer.";

    public const string AllRejected = "The AI service refused the review request.";

    public const string NoneFinished = "Neither review finished.";
}
