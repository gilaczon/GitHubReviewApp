// Whether App Configuration was added, for the startup diagnostics. Set while the services are configured.
var isAppConfigurationAdded = false;

var host = new HostBuilder()
    // App Configuration refreshes only when something asks it to; in an isolated worker, that is this middleware.
    .ConfigureFunctionsWorkerDefaults(worker => worker.UseMiddleware<AppConfigurationRefreshMiddleware>())
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

        // The model ids and the on/off switch can be changed at runtime. This reads the connection string, so it comes after the secrets.
        isAppConfigurationAdded = configManager!.AddRuntimeAppConfiguration(services, context.HostingEnvironment);

        // Settings, GitHub and Claude clients, and the review pipeline. Development uses a fake model.
        services.AddReviewPipeline(context.Configuration, isClaudeMocked: context.HostingEnvironment.IsDevelopment());

        // OpenTelemetry tracing + metrics + logs → Uptrace
        services.AddUptraceTelemetry(context.Configuration);
    })
    .Build();

StartupDiagnostics.LogTelemetryState(
    host.Services.GetRequiredService<ILogger<Program>>(),
    host.Services.GetRequiredService<IConfiguration>(),
    host.Services.GetRequiredService<IHostEnvironment>());

StartupDiagnostics.LogRuntimeSettingsSource(
    host.Services.GetRequiredService<ILogger<Program>>(),
    isAppConfigurationAdded);

StartupDiagnostics.LogAuthorAllowList(
    host.Services.GetRequiredService<ILogger<Program>>(),
    host.Services.GetRequiredService<IOptions<ReviewOptions>>().Value,
    host.Services.GetRequiredService<IHostEnvironment>());

host.Run();
