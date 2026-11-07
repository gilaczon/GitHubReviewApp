namespace GitHubReviewApp.Tests.Telemetry;

[Collection(TelemetryCollection.Name)]
public class TelemetryExtensionsTests
{
    [Fact]
    public void ReviewDuration_GivenReviewsThatTakeSecondsToMinutes_Should_AdviseBucketsThatCoverThatRange()
    {
        // Arrange — the OpenTelemetry default buckets stop at 10 seconds, so every review would land in the overflow bucket and no percentile
        // would mean anything. The unit is milliseconds.
        var advice = AppMeters.ReviewDuration.Advice;

        // Act
        var bounds = advice?.HistogramBucketBoundaries ?? [];

        // Assert
        bounds.Should().NotBeEmpty("the histogram must say which buckets it wants");
        bounds.Should().BeInAscendingOrder();
        bounds.First().Should().BeLessThanOrEqualTo(10_000, "a review that finds nothing to do is quick");
        bounds.Should().Contain(60_000).And.Contain(600_000).And.Contain(1_200_000);
        bounds.Last().Should().BeGreaterThanOrEqualTo(1_800_000, "the Functions host gives up on an invocation after about half an hour");
    }

    private static IConfiguration ConfigurationWith(string? dsn)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["UptraceDsn"] = dsn })
            .Build();
    }

    // The sources and meters the app creates, found by reflection so a new one cannot be added and forgotten.
    private static IEnumerable<string> DeclaredActivitySourceNames()
    {
        return typeof(ActivitySources)
            .GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(field => field.FieldType == typeof(ActivitySource))
            .Select(field => ((ActivitySource)field.GetValue(null)!).Name);
    }

    private static IEnumerable<string> DeclaredMeterNames()
    {
        return typeof(AppMeters)
            .GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(field => field.FieldType == typeof(Meter))
            .Select(field => ((Meter)field.GetValue(null)!).Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddUptraceTelemetry_GivenNoDsn_Should_RegisterNothingSoNoExporterRunsAndNothingTriesToConnect(string? dsn)
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddUptraceTelemetry(ConfigurationWith(dsn));

        // Assert
        services.Should().BeEmpty();
    }

    [Fact]
    public void AddUptraceTelemetry_GivenADsn_Should_RegisterTracingMetricsAndLogging()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddUptraceTelemetry(ConfigurationWith("opaque-dsn-value"));

        // Assert
        services.Should().Contain(descriptor => descriptor.ServiceType == typeof(OpenTelemetry.Trace.TracerProvider));
        services.Should().Contain(descriptor => descriptor.ServiceType == typeof(OpenTelemetry.Metrics.MeterProvider));
        services.Should().Contain(descriptor => descriptor.ServiceType == typeof(ILoggerProvider));
    }

    [Fact]
    public void ConfigureOtlp_GivenADsn_Should_SendOverGrpcToUptraceWithTheDsnInTheHeader()
    {
        // Arrange — a wrong endpoint, protocol or header name loses every trace, metric and log without an error anywhere
        var options = new OtlpExporterOptions();

        // Act
        TelemetryExtensions.ConfigureOtlp(options, "opaque-dsn-value");

        // Assert
        options.Endpoint.Should().Be(new Uri("https://otlp.uptrace.dev:4317"));
        options.Protocol.Should().Be(OtlpExportProtocol.Grpc);
        options.Headers.Should().Be("uptrace-dsn=opaque-dsn-value");
    }

    [Fact]
    public void ConfigureMetricReader_GivenAReader_Should_ExportEveryFifteenSecondsSoAShortRunStillReportsItsMetrics()
    {
        // Arrange — the default is a minute, and a Functions instance can be recycled long before that
        var reader = new MetricReaderOptions();

        // Act
        TelemetryExtensions.ConfigureMetricReader(reader);

        // Assert
        reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds.Should().Be(15_000);
    }

    [Theory]
    [InlineData("https://api.anthropic.com/v1/messages", false)]
    [InlineData("https://otlp.uptrace.dev:4317/opentelemetry.proto.collector.trace.v1.TraceService/Export", false)]
    [InlineData("https://api.github.com/repos/my-org/my-service/pulls/7", true)]
    [InlineData("https://uptrace.dev/docs", true)]
    [InlineData("https://api.anthropic.com.example.net/v1/messages", true)]
    public void AddUptraceTelemetry_GivenADsn_Should_LeaveTheAiProviderAndTheTelemetryBackendOutOfTheHttpSpans(string url, bool isTraceExpected)
    {
        // Arrange — through the registration itself, so a filter that is written but never attached is caught as well
        var configuration = ConfigurationWith("opaque-dsn-value");
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddUptraceTelemetry(configuration);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<HttpClientTraceInstrumentationOptions>>().Get(Options.DefaultName);
        using var request = new HttpRequestMessage(HttpMethod.Post, url);

        // Act
        var isTraced = options.FilterHttpRequestMessage!(request);

        // Assert
        isTraced.Should().Be(isTraceExpected);
    }

    [Fact]
    public void AddUptraceTelemetry_GivenARequestWithNoAddress_Should_StillTraceIt()
    {
        // Arrange
        var configuration = ConfigurationWith("opaque-dsn-value");
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddUptraceTelemetry(configuration);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<HttpClientTraceInstrumentationOptions>>().Get(Options.DefaultName);
        using var request = new HttpRequestMessage();

        // Act
        var isTraced = options.FilterHttpRequestMessage!(request);

        // Assert
        isTraced.Should().BeTrue();
    }

    [Fact]
    public void AddUptraceTelemetry_GivenADsn_Should_ListenToEveryActivitySourceTheAppCreates()
    {
        // Arrange — a source that nothing listens to produces spans that nobody receives. Building the tracer provider opens no connection: that
        // happens when a span is sent, and none is
        var configuration = ConfigurationWith("opaque-dsn-value");
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddUptraceTelemetry(configuration);
        using var provider = services.BuildServiceProvider();

        // Act
        provider.GetRequiredService<OpenTelemetry.Trace.TracerProvider>();

        // Assert
        ActivitySources.ReviewProcessor.HasListeners().Should().BeTrue();
        ActivitySources.ClaudeService.HasListeners().Should().BeTrue();
    }

    [Fact]
    public void AddUptraceTelemetry_GivenADsn_Should_ExportTheFormattedMessageAndScopesOfEveryLog()
    {
        // Arrange
        var configuration = ConfigurationWith("opaque-dsn-value");
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddUptraceTelemetry(configuration);
        using var provider = services.BuildServiceProvider();

        // Act
        var options = provider.GetRequiredService<IOptions<OpenTelemetryLoggerOptions>>().Value;

        // Assert
        options.IncludeFormattedMessage.Should().BeTrue("the message template alone does not say what happened");
        options.IncludeScopes.Should().BeTrue();
    }

    [Fact]
    public void TraceSourceNames_GivenTheActivitySourcesTheAppCreates_Should_ListEveryOneOfThem()
    {
        // Arrange
        var declared = DeclaredActivitySourceNames().ToList();

        // Act
        var registered = TelemetryExtensions.TraceSourceNames;

        // Assert — a source that is not listed produces spans nobody ever receives
        declared.Should().NotBeEmpty("this test is meaningless if it found no sources");
        registered.Should().BeEquivalentTo(declared);
    }

    [Fact]
    public void MeterNames_GivenTheMetersTheAppCreates_Should_ListEveryOneOfThem()
    {
        // Arrange
        var declared = DeclaredMeterNames().ToList();

        // Act
        var registered = TelemetryExtensions.MeterNames;

        // Assert
        declared.Should().NotBeEmpty("this test is meaningless if it found no meters");
        registered.Should().BeEquivalentTo(declared);
    }
}
