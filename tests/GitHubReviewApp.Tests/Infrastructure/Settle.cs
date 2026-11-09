namespace GitHubReviewApp.Tests.Infrastructure;

/// <summary>
/// For a test that has to show something did NOT happen, such as a cancellation that a failure used to cause. It lets work that is already queued on
/// the thread pool run, a fixed number of turns, instead of waiting for a delay to pass. It cannot make a missing cancellation certain, so a test that
/// uses it can only pass wrongly (when a regression is slower than the turns), never fail wrongly, because there is nothing it waits for.
/// </summary>
internal static class Settle
{
    private const int Turns = 200;

    public static async Task QueuedWorkAsync()
    {
        for (var turn = 0; turn < Turns; turn++)
        {
            await Task.Yield();
        }
    }
}
