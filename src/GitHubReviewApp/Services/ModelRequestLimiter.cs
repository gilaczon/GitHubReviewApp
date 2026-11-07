namespace GitHubReviewApp.Services;

/// <summary>
/// Caps how many model requests this process has in flight at once, across every review. Several queued reviews each running several parts
/// in parallel could otherwise send enough requests together to trip the provider's rate limit for every repository that shares the key.
/// </summary>
public sealed class ModelRequestLimiter
{
    private readonly SemaphoreSlim _slots;

    public ModelRequestLimiter(IOptions<ReviewOptions> options)
    {
        _slots = new SemaphoreSlim(Math.Max(1, options.Value.MaxConcurrentModelRequests));
    }

    private sealed class Releaser : IDisposable
    {
        private readonly SemaphoreSlim _slots;
        private int _isReleased;

        public Releaser(SemaphoreSlim slots)
        {
            _slots = slots;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _isReleased, 1) == 0)
            {
                _slots.Release();
            }
        }
    }

    /// <summary>Waits for a free slot. Dispose the result to give the slot back.</summary>
    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        await _slots.WaitAsync(cancellationToken);

        return new Releaser(_slots);
    }
}
