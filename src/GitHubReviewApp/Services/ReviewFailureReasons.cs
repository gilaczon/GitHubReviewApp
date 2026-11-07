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
    // reply look the same from here as an outage does, so a notice that blamed one would sometimes be wrong.
    public const string RetriesExhausted =
        "The automated review could not be completed after several attempts. " +
        "This is usually temporary, so pushing a new commit will start a new review. " +
        "If it keeps happening, please tell whoever runs this review app.";
}
