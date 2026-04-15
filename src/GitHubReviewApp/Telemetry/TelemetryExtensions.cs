namespace GitHubReviewApp.Telemetry;

internal static class TelemetryExtensions
{
    private static readonly Uri OtlpEndpoint = new("https://otlp.uptrace.dev:4317");

    internal static IServiceCollection AddUptraceTelemetry(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var uptraceDsn = configuration["UptraceDsn"];

        if (string.IsNullOrWhiteSpace(uptraceDsn))
            return services;

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("GitHubReviewApp"))
            .WithTracing(tracing => tracing
                .SetSampler(new AlwaysOnSampler())
                .AddSource(ActivitySources.ReviewProcessorName)
                .AddSource(ActivitySources.ClaudeServiceName)
                .AddHttpClientInstrumentation(options =>
                {
                    options.FilterHttpRequestMessage = request =>
                        request.RequestUri?.Host != "api.anthropic.com" &&
                        request.RequestUri?.Host != "otlp.uptrace.dev";
                })
                .AddOtlpExporter(otlp => ConfigureOtlp(otlp, uptraceDsn)))
            .WithMetrics(metrics => metrics
                .AddMeter(AppMeters.MeterName)
                .AddHttpClientInstrumentation()
                .AddOtlpExporter((otlp, reader) =>
                {
                    ConfigureOtlp(otlp, uptraceDsn);
                    // Default is 60s — too long for Azure Functions which can be recycled
                    // after execution. 15s ensures at least one export per function run.
                    reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 15_000;
                }))
            .WithLogging(
                logging => logging.AddOtlpExporter(otlp => ConfigureOtlp(otlp, uptraceDsn)),
                options =>
                {
                    options.IncludeFormattedMessage = true;
                    options.IncludeScopes           = true;
                });

        return services;
    }

    private static void ConfigureOtlp(OtlpExporterOptions otlp, string uptraceDsn)
    {
        otlp.Endpoint = OtlpEndpoint;
        otlp.Protocol = OtlpExportProtocol.Grpc;
        otlp.Headers  = $"uptrace-dsn={uptraceDsn}";
    }
}
