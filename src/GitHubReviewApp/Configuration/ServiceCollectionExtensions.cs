namespace GitHubReviewApp.Configuration;

internal static class ServiceCollectionExtensions
{
    // GitHub calls are small; a minute is generous. Claude has its own, longer, SDK-managed timeout.
    private static readonly TimeSpan _gitHubRequestTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Registers the review pipeline: settings, GitHub and Claude clients, and the services that plan,
    /// run and report a review. <paramref name="isClaudeMocked"/> swaps in a fake model for local runs.
    /// </summary>
    internal static IServiceCollection AddReviewPipeline(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isClaudeMocked)
    {
        // TimeProvider — injectable seam that enables testable time-dependent logic
        services.AddSingleton(TimeProvider.System);

        // Non-secret settings from appsettings.*.json (secrets come from Key Vault). Validated when the host starts, so a setting that can
        // never work stops the deployment instead of failing the first pull request. A key the options class does not have is refused too: the
        // binder would skip it and the code default would apply without a word, and for the author allow-list the default is "everyone", so a
        // missing letter in its name would open the gate.
        services.AddOptions<ClaudeOptions>().Bind(configuration.GetSection(ClaudeOptions.SectionName), RefuseUnknownKeys).ValidateOnStart();
        services.AddOptions<ReviewOptions>().Bind(configuration.GetSection(ReviewOptions.SectionName), RefuseUnknownKeys).ValidateOnStart();
        services.AddSingleton<IValidateOptions<ClaudeOptions>, ClaudeOptionsValidator>();
        services.AddSingleton<IValidateOptions<ReviewOptions>, ReviewOptionsValidator>();

        // The settings an operator can change while the app runs. One holder, so that every review reads the snapshot that a reload publishes.
        services.AddSingleton<IRuntimeSettings, RuntimeSettingsMonitor>();

        services.AddSingleton<IGitHubAppAuthService, GitHubAppAuthService>();

        // Typed HTTP clients registered against interfaces
        services.AddHttpClient<IGitHubService, GitHubService>(client => client.Timeout = _gitHubRequestTimeout);

        if (isClaudeMocked)
        {
            services.AddSingleton<IClaudeService, MockClaudeService>();
        }
        else
        {
            services.AddHttpClient<IClaudeService, ClaudeService>();
        }

        // Stateless review logic.
        services.AddSingleton<ReviewPlanner>();
        services.AddSingleton<ReviewAggregator>();
        services.AddSingleton<ReviewFormatter>();

        // Shared by every review this process runs, so each is a singleton: they are how concurrent reviews coordinate.
        services.AddSingleton<ReviewInFlightRegistry>();
        services.AddSingleton<ModelRequestLimiter>();
        services.AddSingleton<ChunkResultCache>();

        // Transient, because they depend on typed HTTP clients: a singleton would pin one client (and its
        // connection pool) for the life of the process and never notice DNS changes.
        services.AddTransient<IReviewOrchestrator, ReviewOrchestrator>();
        services.AddTransient<ReviewPublisher>();
        services.AddTransient<IReviewFailureNotifier, ReviewFailureNotifier>();

        return services;
    }

    private static void RefuseUnknownKeys(BinderOptions binderOptions)
    {
        binderOptions.ErrorOnUnknownConfiguration = true;
    }
}
