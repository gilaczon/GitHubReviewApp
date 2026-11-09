namespace GitHubReviewApp.Services;

/// <summary>
/// Keeps the finished model runs of recent reviews for a while, so that a review the queue retries (because the other run failed, GitHub failed
/// before the comment was written, or the review ran out of time) does not pay again for a run that already completed on this instance. Only a
/// completed run is kept. The key covers the model, both prompts and the run's index: the two runs of a review send the same request, and without
/// the index the second would be handed the first one's answer.
/// </summary>
public sealed class ModelResultCache
{
    private const int MaxEntries = 64;

    private static readonly TimeSpan _lifetime = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, (ClaudeRunResult Run, DateTimeOffset StoredAt)> _entries = new();
    private readonly TimeProvider _timeProvider;

    public ModelResultCache(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <summary>The key of one run of a request: its index among the review's runs, the model, the system prompt and the user message.</summary>
    public static string KeyFor(ClaudeRunRequest request, int runIndex)
    {
        var text = string.Join('\0', runIndex.ToString(CultureInfo.InvariantCulture), request.Model, request.SystemPrompt, request.UserMessage);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>The completed run stored under this key, or null when there is none or it has expired.</summary>
    public ClaudeRunResult? Find(string key)
    {
        if (!_entries.TryGetValue(key, out var entry))
        {
            return null;
        }

        if (_timeProvider.GetUtcNow() - entry.StoredAt > _lifetime)
        {
            _entries.TryRemove(key, out _);
            return null;
        }

        return entry.Run;
    }

    /// <summary>Stores a completed run. One that was truncated, refused, unreadable or rejected is not kept: a retry may do better.</summary>
    public void Add(string key, ClaudeRunResult result)
    {
        if (result.Outcome != RunOutcome.Completed)
        {
            return;
        }

        _entries[key] = (result, _timeProvider.GetUtcNow());

        if (_entries.Count > MaxEntries)
        {
            Prune();
        }
    }

    private void Prune()
    {
        // Drop the oldest entries until the cache is back under its limit.
        foreach (var stale in _entries.OrderBy(entry => entry.Value.StoredAt).Take(_entries.Count - MaxEntries))
        {
            _entries.TryRemove(stale.Key, out _);
        }
    }
}
