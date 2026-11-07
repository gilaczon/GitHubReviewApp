namespace GitHubReviewApp.Telemetry;

internal static class TelemetryExtensions
{
    private const string DsnSettingName = "UptraceDsn";

    private static readonly Uri _otlpEndpoint = new("https://otlp.uptrace.dev:4317");

    /// <summary>Every activity source the app creates. A source left off this list produces spans that nobody ever receives.</summary>
    internal static IReadOnlyList<string> TraceSourceNames { get; } = [ActivitySources.ReviewProcessorName, ActivitySources.ClaudeServiceName];

    /// <summary>Every meter the app creates. A meter left off this list produces measurements that nobody ever receives.</summary>
    internal static IReadOnlyList<string> MeterNames { get; } = [AppMeters.MeterName];

    internal static bool IsUptraceConfigured(IConfiguration configuration)
    {
        return !string.IsNullOrWhiteSpace(configuration[DsnSettingName]);
    }

    internal static IServiceCollection AddUptraceTelemetry(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        if (!IsUptraceConfigured(configuration))
        {
            return services;
        }

        var uptraceDsn = configuration[DsnSettingName]!;

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("GitHubReviewApp"))
            .WithTracing(tracing => tracing
                .SetSampler(new AlwaysOnSampler())
                .AddSource(TraceSourceNames.ToArray())
                .AddHttpClientInstrumentation(options =>
                {
                    options.FilterHttpRequestMessage = request =>
                        request.RequestUri?.Host != "api.anthropic.com" &&
                        request.RequestUri?.Host != "otlp.uptrace.dev";
                })
                .AddOtlpExporter(otlp => ConfigureOtlp(otlp, uptraceDsn)))
            .WithMetrics(metrics => metrics
                .AddMeter(MeterNames.ToArray())
                .AddHttpClientInstrumentation()
                .AddOtlpExporter((otlp, reader) =>
                {
                    ConfigureOtlp(otlp, uptraceDsn);
                    ConfigureMetricReader(reader);
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

    internal static void ConfigureOtlp(OtlpExporterOptions otlp, string uptraceDsn)
    {
        otlp.Endpoint = _otlpEndpoint;
        otlp.Protocol = OtlpExportProtocol.Grpc;
        otlp.Headers  = $"uptrace-dsn={uptraceDsn}";
    }

    internal static void ConfigureMetricReader(MetricReaderOptions reader)
    {
        // Default is 60s — too long for Azure Functions which can be recycled
        // after execution. 15s ensures at least one export per function run.
        reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 15_000;
    }
}
