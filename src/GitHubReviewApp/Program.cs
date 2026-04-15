var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureAppConfiguration((context, configBuilder) => {
        var configManager = new ConfigurationManager();
        configManager.AddJsonFile($"appsettings.json", optional: false);
        configManager.AddJsonFile($"appsettings.{context.HostingEnvironment.EnvironmentName}.json", optional: true, reloadOnChange: false);
        configBuilder.AddConfiguration(configManager);

        // we don't have access to the service collection here, so we'll add the configuration manager to the context properties
        context.Properties.Add("AppConfigurationManager", configManager);
    })
    .ConfigureServices((context, services) =>
    {
        // now we have access to the service collection, so we can use the configuration manager to add secrets
        ConfigurationManager? configManager = context.Properties["AppConfigurationManager"] as ConfigurationManager;
        configManager!.AddSecretsToConfiguration(services, context.HostingEnvironment.EnvironmentName);

        // TimeProvider — injectable seam that enables testable time-dependent logic
        services.AddSingleton(TimeProvider.System);

        services.AddSingleton<IGitHubAppAuthService, GitHubAppAuthService>();

        // Typed HTTP clients registered against interfaces
        services.AddHttpClient<IGitHubService, GitHubService>();
        if (context.HostingEnvironment.IsDevelopment())
            services.AddSingleton<IClaudeService, MockClaudeService>();
        else
            services.AddHttpClient<IClaudeService, ClaudeService>();

        // OpenTelemetry tracing + metrics → Uptrace
        services.AddUptraceTelemetry(context.Configuration);
    })
    // ConfigureLogging runs after ConfigureServices (registration order), so Key Vault secrets
    // are already loaded into context.Configuration by the time this callback executes.
    .ConfigureLogging((context, logging) =>
    {
        var uptraceDsn = context.Configuration["UptraceDsn"];
        logging.AddOpenTelemetry(options =>
        {
            options.IncludeFormattedMessage = true;
            options.IncludeScopes = true;
            if (!string.IsNullOrWhiteSpace(uptraceDsn))
            {
                options.AddOtlpExporter(otlp =>
                {
                    otlp.Endpoint = new Uri("https://otlp.uptrace.dev");
                    otlp.Protocol = OtlpExportProtocol.HttpProtobuf;
                    otlp.Headers  = $"uptrace-dsn={uptraceDsn}";
                });
            }
        });
    })
    .Build();

// Diagnostic: confirm whether Uptrace DSN is configured before starting
var startupLogger = host.Services.GetRequiredService<ILogger<Program>>();
var uptraceDsnCheck = host.Services.GetRequiredService<IConfiguration>()["UptraceDsn"];
startupLogger.LogInformation(
    "Uptrace DSN configured: {IsConfigured}",
    !string.IsNullOrWhiteSpace(uptraceDsnCheck));

host.Run();
