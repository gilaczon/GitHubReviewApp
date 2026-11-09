namespace GitHubReviewApp.Tests.Infrastructure;

/// <summary>One line a <see cref="ListLogger{T}"/> was asked to write.</summary>
internal sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);
