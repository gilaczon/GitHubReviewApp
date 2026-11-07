namespace GitHubReviewApp.Services;

/// <summary>
/// Remembers which commits this process is reviewing right now. A reopen, a draft toggle or a redelivery queues another message for a
/// commit that is already being reviewed, and without this each would pay for the same review and post a duplicate. It covers one process
/// only; the check of the review list just before posting is what catches a second instance.
/// </summary>
public sealed class ReviewInFlightRegistry
{
    private readonly ConcurrentDictionary<string, byte> _active = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Lease : IDisposable
    {
        private readonly ConcurrentDictionary<string, byte> _active;
        private readonly string _key;
        private int _isReleased;

        public Lease(ConcurrentDictionary<string, byte> active, string key)
        {
            _active = active;
            _key = key;
        }

        public void Dispose()
        {
            // Once only: a second disposal after another run has claimed the same commit would otherwise release that run's claim.
            if (Interlocked.Exchange(ref _isReleased, 1) == 0)
            {
                _active.TryRemove(_key, out _);
            }
        }
    }

    /// <summary>Claims the commit, or returns null when this process is already reviewing it. Dispose the lease when the run ends.</summary>
    public IDisposable? TryBegin(string owner, string repo, int prNumber, string headSha)
    {
        var key = $"{owner}/{repo}#{prNumber}@{headSha}";

        return _active.TryAdd(key, 0) ? new Lease(_active, key) : null;
    }
}
