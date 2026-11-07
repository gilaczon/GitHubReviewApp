namespace GitHubReviewApp.Services;

/// <summary>
/// Keeps the finished results of model requests for a while, keyed by the exact prompt. When one part of a multi-part review fails, the queue
/// retries the whole message; without this, every part that had already succeeded would be paid for again. Only a completed result is kept,
/// and the key covers the system prompt (so the repository's guidelines) and the rendered diff, so a changed input never finds a stale answer.
/// </summary>
public sealed class ChunkResultCache
{
    private const int MaxEntries = 64;

    private static readonly TimeSpan _lifetime = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, (ClaudeReviewResult Review, DateTimeOffset StoredAt)> _entries = new();
    private readonly TimeProvider _timeProvider;

    public ChunkResultCache(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public static string KeyFor(string systemPrompt, string userMessage)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(systemPrompt + "\0" + userMessage)));
    }

    /// <summary>The completed result stored for this prompt, or null when there is none or it has expired.</summary>
    public ClaudeReviewResult? Find(string key)
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

        return entry.Review;
    }

    /// <summary>Stores a completed result. A truncated, refused or unreadable one is not kept: a retry may do better.</summary>
    public void Add(string key, ClaudeReviewResult result)
    {
        if (result.Outcome != ChunkOutcome.Completed)
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
