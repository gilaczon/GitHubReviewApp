var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureAppConfiguration((context, configBuilder) => {
        var configManager = new ConfigurationManager();
        configBuilder.AddConfiguration(configManager);

        // we don't have access to the service collection here, so we'll add the configuration manager to the context properties
        context.Properties.Add("AppConfigurationManager", configManager);
    })
    .ConfigureLogging(logging => logging.AddOpenTelemetry(options =>
    {
        options.IncludeFormattedMessage = true;
        options.IncludeScopes = true;
    }))
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

        // OpenTelemetry → Uptrace
        services.AddUptraceTelemetry(context.Configuration);
    })
    .Build();

host.Run();
