namespace GitHubReviewApp.ReviewPreview;

/// <summary>
/// Builds the preview: the app's own review pipeline and settings (its appsettings.json, so the limits and model choice are production's), with
/// the GitHub sign-in, the GitHub writes and the publishing target replaced.
/// </summary>
public static class PreviewServices
{
    /// <summary>The secrets a preview needs, by setting name. Neither is ever printed.</summary>
    public static IReadOnlyList<string> RequiredSecretNames { get; } = ["AnthropicApiKey", SlackWebhookPoster.WebhookUrlSettingName];

    // The app's limit for a GitHub call (ServiceCollectionExtensions).
    private static readonly TimeSpan _gitHubRequestTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan _slackRequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The app's appsettings.json, then user secrets, then environment variables (each overriding the one before).</summary>
    public static IConfiguration BuildConfiguration()
    {
        return new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false)
            .AddUserSecrets(typeof(PreviewServices).Assembly, optional: true)
            .AddEnvironmentVariables()
            .Build();
    }

    /// <summary>The names in <see cref="RequiredSecretNames"/> that have no value.</summary>
    public static IReadOnlyList<string> MissingSecrets(IConfiguration configuration)
    {
        return RequiredSecretNames.Where(name => string.IsNullOrWhiteSpace(configuration[name])).ToList();
    }

    public static ServiceProvider Build(IConfiguration configuration, IGitHubTokenSource tokenSource)
    {
        var services = new ServiceCollection();

        services.AddLogging(logging => logging
            .AddSimpleConsole(console => console.SingleLine = true)
            .SetMinimumLevel(LogLevel.Information)
            .AddFilter("System.Net.Http", LogLevel.Warning)
            .AddFilter("Microsoft", LogLevel.Warning));
        services.AddSingleton(configuration);
        services.AddReviewPipeline(configuration, isClaudeMocked: false);

        // Registered after the app's own, so these are the ones resolved.
        services.AddSingleton(tokenSource);
        services.AddSingleton<IGitHubAppAuthService, PreviewAuthService>();
        services.AddSingleton<PreviewCapture>();
        services.AddHttpClient<GitHubService>(client => client.Timeout = _gitHubRequestTimeout);
        services.AddTransient<IGitHubService>(provider =>
            new PreviewGitHubService(provider.GetRequiredService<GitHubService>(), provider.GetRequiredService<PreviewCapture>()));
        services.AddHttpClient<ISlackPoster, SlackWebhookPoster>(client => client.Timeout = _slackRequestTimeout);
        services.AddTransient<PreviewRunner>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}
