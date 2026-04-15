namespace GitHubReviewApp.Telemetry;

internal static class TelemetryExtensions
{
    internal static IServiceCollection AddUptraceTelemetry(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var uptraceDsn = configuration["UptraceDsn"];

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("GitHubReviewApp"))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(ActivitySources.ReviewProcessorName)
                    .AddSource(ActivitySources.ClaudeServiceName)
                    .AddHttpClientInstrumentation(options =>
                    {
                        // Suppress auto-instrumented spans for Claude HTTP calls —
                        // the ClaudeService span carries the full Gen AI semantic detail.
                        // GitHub API calls still emit their own HttpClient spans.
                        options.FilterHttpRequestMessage = request =>
                            request.RequestUri?.Host != "api.anthropic.com";
                    });

                if (!string.IsNullOrWhiteSpace(uptraceDsn))
                {
                    tracing.AddOtlpExporter(otlp =>
                    {
                        otlp.Endpoint = new Uri("https://otlp.uptrace.dev");
                        otlp.Protocol = OtlpExportProtocol.HttpProtobuf;
                        otlp.Headers  = $"uptrace-dsn={uptraceDsn}";
                    });
                }
            })
            .WithLogging(logging =>
            {
                if (!string.IsNullOrWhiteSpace(uptraceDsn))
                {
                    logging.AddOtlpExporter(otlp =>
                    {
                        otlp.Endpoint = new Uri("https://otlp.uptrace.dev");
                        otlp.Protocol = OtlpExportProtocol.HttpProtobuf;
                        otlp.Headers  = $"uptrace-dsn={uptraceDsn}";
                    });
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(AppMeters.MeterName)
                    .AddHttpClientInstrumentation();

                if (!string.IsNullOrWhiteSpace(uptraceDsn))
                {
                    metrics.AddOtlpExporter(otlp =>
                    {
                        otlp.Endpoint = new Uri("https://otlp.uptrace.dev");
                        otlp.Protocol = OtlpExportProtocol.HttpProtobuf;
                        otlp.Headers  = $"uptrace-dsn={uptraceDsn}";
                    });
                }
            });

        return services;
    }
}
