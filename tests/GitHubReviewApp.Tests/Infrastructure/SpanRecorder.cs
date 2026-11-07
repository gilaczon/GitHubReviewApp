namespace GitHubReviewApp.Tests.Infrastructure;

/// <summary>Collects the finished spans of one activity source while it is alive.</summary>
internal sealed class SpanRecorder : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly List<Activity> _spans = [];
    private readonly object _gate = new();

    public SpanRecorder(string sourceName)
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                lock (_gate)
                {
                    _spans.Add(activity);
                }
            }
        };

        ActivitySource.AddActivityListener(_listener);
    }

    public IReadOnlyList<Activity> Spans
    {
        get
        {
            lock (_gate)
            {
                return _spans.ToList();
            }
        }
    }

    public void Dispose()
    {
        _listener.Dispose();
    }
}
