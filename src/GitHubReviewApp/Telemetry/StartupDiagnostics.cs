namespace GitHubReviewApp.Telemetry;

/// <summary>What the app tells its operators when it starts.</summary>
internal static class StartupDiagnostics
{
    /// <summary>
    /// Says who may trigger a review. No allow list means every author, which is right for a private repository but lets anyone spend model tokens
    /// on a public one, so outside Development it is a warning: the app works either way, and nothing else would draw attention to it.
    /// </summary>
    internal static void LogAuthorAllowList(ILogger logger, ReviewOptions options, IHostEnvironment environment)
    {
        var associations = options.EffectiveAllowedAuthorAssociations;

        if (associations.Count > 0)
        {
            logger.LogInformation(
                "Only pull requests whose author's association with the repository is one of {Associations} are reviewed.",
                string.Join(", ", associations.Order(StringComparer.Ordinal)));
            return;
        }

        if (environment.IsDevelopment())
        {
            logger.LogInformation("No Review:AllowedAuthorAssociations is set, so every author can trigger a review, which is normal in Development.");
            return;
        }

        logger.LogWarning(
            "No Review:AllowedAuthorAssociations is set in the {EnvironmentName} environment, so every author can trigger a review. " +
            "On a public repository that lets anyone spend model tokens: set it to the associations you trust, for example OWNER, MEMBER and COLLABORATOR.",
            environment.EnvironmentName);
    }

    /// <summary>
    /// Says whether telemetry is on. A missing Uptrace DSN outside Development is a warning, because the app still works and so nobody would
    /// notice that it has gone silent until they needed its traces. The DSN itself is a credential and is never written.
    /// </summary>
    internal static void LogTelemetryState(ILogger logger, IConfiguration configuration, IHostEnvironment environment)
    {
        if (TelemetryExtensions.IsUptraceConfigured(configuration))
        {
            logger.LogInformation("Uptrace telemetry is on: traces, metrics and logs are exported.");
            return;
        }

        if (environment.IsDevelopment())
        {
            logger.LogInformation("Uptrace telemetry is off because no UptraceDsn is configured, which is normal in Development.");
            return;
        }

        logger.LogWarning(
            "No UptraceDsn is configured in the {EnvironmentName} environment, so this app exports no traces, metrics or logs. " +
            "Set the UptraceDsn secret in Key Vault.",
            environment.EnvironmentName);
    }
}
