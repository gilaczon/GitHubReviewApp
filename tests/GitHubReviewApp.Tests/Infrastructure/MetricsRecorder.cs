namespace GitHubReviewApp.Tests.Infrastructure;

/// <summary>
/// Records every measurement the app's meter publishes while it is alive. Use inside a test class that is in
/// the Telemetry collection, because the meter is process-wide.
/// </summary>
internal sealed class MetricsRecorder : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly List<RecordedMeasurement> _measurements = [];
    private readonly object _gate = new();

    public MetricsRecorder()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == AppMeters.MeterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };

        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument.Name, value, tags));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Add(instrument.Name, value, tags));
        _listener.Start();
    }

    public IReadOnlyList<RecordedMeasurement> Measurements
    {
        get
        {
            lock (_gate)
            {
                return _measurements.ToList();
            }
        }
    }

    /// <summary>Total of every measurement with this name whose tags include all the given key/value pairs.</summary>
    public double Sum(string name, params (string Key, string Value)[] tags)
    {
        return Matching(name, tags).Sum(measurement => measurement.Value);
    }

    public bool Has(string name, params (string Key, string Value)[] tags)
    {
        return Matching(name, tags).Any();
    }

    public void Dispose()
    {
        _listener.Dispose();
    }

    private IEnumerable<RecordedMeasurement> Matching(string name, (string Key, string Value)[] tags)
    {
        return Measurements.Where(measurement =>
            measurement.Name == name
            && tags.All(tag => measurement.Tags.TryGetValue(tag.Key, out var value) && (value as string) == tag.Value));
    }

    private void Add(string name, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var tagMap = tags.ToArray().ToDictionary(pair => pair.Key, pair => pair.Value);

        lock (_gate)
        {
            _measurements.Add(new RecordedMeasurement(name, value, tagMap));
        }
    }
}
