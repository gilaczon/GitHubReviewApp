namespace GitHubReviewApp.Tests.Infrastructure;

internal sealed record RecordedMeasurement(string Name, double Value, IReadOnlyDictionary<string, object?> Tags);
