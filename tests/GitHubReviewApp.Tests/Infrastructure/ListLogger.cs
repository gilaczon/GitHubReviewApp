namespace GitHubReviewApp.Tests.Infrastructure;

/// <summary>A logger that keeps what it is given, for tests that assert on what the app says to its operators and at which level.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    private readonly List<LogEntry> _entries = [];
    private readonly List<object> _scopes = [];
    private readonly object _gate = new();

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToList();
            }
        }
    }

    /// <summary>The state of every scope that was begun, in order.</summary>
    public IReadOnlyList<object> Scopes
    {
        get
        {
            lock (_gate)
            {
                return _scopes.ToList();
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        lock (_gate)
        {
            _scopes.Add(state);
        }

        return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_gate)
        {
            _entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }
}
