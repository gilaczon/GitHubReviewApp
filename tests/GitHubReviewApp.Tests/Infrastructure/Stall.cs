namespace GitHubReviewApp.Tests.Infrastructure;

/// <summary>Fakes for a call that is still in progress.</summary>
internal static class Stall
{
    /// <summary>
    /// Waits until the token is cancelled and then throws, like a request to a server that has stopped answering. It never completes on its
    /// own, so a test that uses it must cancel, or fail by a guard of its own, rather than wait for a delay to run out.
    /// </summary>
    public static async Task UntilCancelledAsync(CancellationToken cancellationToken)
    {
        await new TaskCompletionSource().Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// As <see cref="UntilCancelledAsync(CancellationToken)"/>, and it also says so when it was the cancellation that ended the wait. The signal comes
    /// from the wait itself, not from a callback registered on the token: those run in reverse order of registration, so a callback registered before
    /// the wait can be removed by the code the wait resumes before it has run.
    /// </summary>
    public static async Task UntilCancelledAsync(CancellationToken cancellationToken, TaskCompletionSource wasCancelled)
    {
        await UntilAsync(new TaskCompletionSource().Task, cancellationToken, wasCancelled);
    }

    /// <summary>Waits until the task completes, or until the token is cancelled, in which case it says so and then throws.</summary>
    public static async Task UntilAsync(Task released, CancellationToken cancellationToken, TaskCompletionSource wasCancelled)
    {
        try
        {
            await released.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            wasCancelled.TrySetResult();
            throw;
        }
    }
}
