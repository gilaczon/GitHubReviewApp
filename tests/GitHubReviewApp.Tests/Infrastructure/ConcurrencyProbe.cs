namespace GitHubReviewApp.Tests.Infrastructure;

/// <summary>
/// Proves how many calls really ran at the same time. Every caller reports in and then waits until a set number of callers have arrived, so the
/// calls only finish if they overlap; the most that were ever in flight together is what a test then asserts on.
/// </summary>
internal sealed class ConcurrencyProbe
{
    private static readonly TimeSpan _arrivalTimeout = TimeSpan.FromSeconds(10);

    private readonly TaskCompletionSource _enoughArrived = new();
    private readonly int _arrivalsNeeded;
    private int _inFlight;
    private int _maxInFlight;
    private int _arrived;

    public ConcurrencyProbe(int arrivalsNeeded)
    {
        _arrivalsNeeded = arrivalsNeeded;
    }

    public int MaxInFlight => Volatile.Read(ref _maxInFlight);

    /// <summary>Reports one call in flight, waits until enough calls have arrived, then reports it finished.</summary>
    public async Task EnterAndWaitForPeersAsync(CancellationToken cancellationToken)
    {
        RaiseMaxTo(Interlocked.Increment(ref _inFlight));

        if (Interlocked.Increment(ref _arrived) == _arrivalsNeeded)
        {
            _enoughArrived.TrySetResult();
        }

        await _enoughArrived.Task.WaitAsync(_arrivalTimeout, cancellationToken);
        Interlocked.Decrement(ref _inFlight);
    }

    private void RaiseMaxTo(int value)
    {
        int snapshot;

        do
        {
            snapshot = Volatile.Read(ref _maxInFlight);
        }
        while (value > snapshot && Interlocked.CompareExchange(ref _maxInFlight, value, snapshot) != snapshot);
    }
}
