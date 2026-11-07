namespace GitHubReviewApp.Tests.Infrastructure;

/// <summary>
/// Telemetry tests listen to process-wide activity sources and meters. Test classes that record or assert
/// on telemetry join this collection so they never run at the same time and cannot see each other's data.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TelemetryCollection
{
    public const string Name = "Telemetry";
}
