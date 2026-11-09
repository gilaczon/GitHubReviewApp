namespace GitHubReviewApp.Configuration;

/// <summary>
/// Connects the runtime settings (design 5.11) to Azure App Configuration. The store is reached with its read-only connection string, which comes
/// from Key Vault through the same configuration as every other secret.
/// </summary>
public static class AppConfigurationSetup
{
    public const string ConnectionStringSecretName = "AppConfigurationConnectionString";

    /// <summary>Every key of this app in the store starts with this; it is removed, so the store's key <c>GitHubReviewApp:Runtime:SonnetModel</c> reads as <c>Runtime:SonnetModel</c>.</summary>
    public const string KeyPrefix = "GitHubReviewApp:";

    /// <summary>The only keys read from the store, so it can never reach the settings that are validated at startup.</summary>
    public const string RuntimeKeyFilter = "GitHubReviewApp:Runtime:*";

    /// <summary>Changing this key's value makes every selected key be read again.</summary>
    public const string SentinelKey = "GitHubReviewApp:Sentinel";

    private static readonly TimeSpan _productionRefreshInterval = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan _otherRefreshInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Adds App Configuration to the configuration when the connection string secret is set, and says whether it did. It must run after the secrets
    /// are loaded. The source is optional, so a store that cannot be reached at startup is a warning and the appsettings values apply.
    /// </summary>
    public static bool AddRuntimeAppConfiguration(this ConfigurationManager configuration, IServiceCollection services, IHostEnvironment environment)
    {
        var connectionString = configuration[ConnectionStringSecretName];

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return false;
        }

        configuration.AddAzureAppConfiguration(
            options =>
            {
                options.Connect(connectionString)
                    .Select(RuntimeKeyFilter)
                    .TrimKeyPrefix(KeyPrefix)
                    .ConfigureRefresh(refresh => refresh
                        .Register(SentinelKey, refreshAll: true)
                        .SetRefreshInterval(RefreshInterval(environment)));
            },
            optional: true);

        // The refreshers that the Functions middleware asks to refresh.
        services.AddAzureAppConfiguration();

        return true;
    }

    public static TimeSpan RefreshInterval(IHostEnvironment environment)
    {
        return environment.IsProduction() ? _productionRefreshInterval : _otherRefreshInterval;
    }
}
