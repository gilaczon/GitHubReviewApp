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

    /// <summary>
    /// How long adding the source may wait for the store at startup. The provider's own default is 100 seconds, which would hold every cold start
    /// and scale-out during a store outage, against GitHub's 10 second webhook timeout. After this the host starts on the appsettings values and the
    /// refresh picks the store up when it is reachable again.
    /// </summary>
    public static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan _productionRefreshInterval = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan _otherRefreshInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Adds App Configuration to the configuration when the connection string secret is set and well formed, and says what it did. It must run
    /// after the secrets are loaded. The source is optional and its startup wait is bounded by <see cref="StartupTimeout"/>, so a store that cannot
    /// be reached at startup does not stop the host and the appsettings values apply. A malformed secret would make the package throw while the host
    /// is built, so it is checked first and, when bad, nothing is added at all: with no source there is no refresher either, and the middleware
    /// then has none to ask.
    /// </summary>
    public static AppConfigurationOutcome AddRuntimeAppConfiguration(this ConfigurationManager configuration, IServiceCollection services, IHostEnvironment environment)
    {
        var connectionString = configuration[ConnectionStringSecretName];

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return AppConfigurationOutcome.NotConfigured;
        }

        if (!IsWellFormedConnectionString(connectionString))
        {
            return AppConfigurationOutcome.Invalid;
        }

        configuration.AddAzureAppConfiguration(
            options =>
            {
                options.Connect(connectionString)
                    .ConfigureStartupOptions(startup => startup.Timeout = StartupTimeout)
                    .Select(RuntimeKeyFilter)
                    .TrimKeyPrefix(KeyPrefix)
                    .ConfigureRefresh(refresh => refresh
                        .Register(SentinelKey, refreshAll: true)
                        .SetRefreshInterval(RefreshInterval(environment)));
            },
            optional: true);

        // The refreshers that the Functions middleware asks to refresh.
        services.AddAzureAppConfiguration();

        return AppConfigurationOutcome.Added;
    }

    public static TimeSpan RefreshInterval(IHostEnvironment environment)
    {
        return environment.IsProduction() ? _productionRefreshInterval : _otherRefreshInterval;
    }

    /// <summary>
    /// The shape the package accepts: <c>Endpoint=</c> an absolute http or https URI, <c>Id=</c> something, and <c>Secret=</c> base64. Nothing is
    /// ever written from the string, because it holds the store's secret.
    /// </summary>
    internal static bool IsWellFormedConnectionString(string connectionString)
    {
        string? endpoint = null;
        string? id = null;
        string? secret = null;

        foreach (var segment in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = segment.IndexOf('=');

            if (separatorIndex <= 0)
            {
                return false;
            }

            var name = segment[..separatorIndex].Trim();
            var value = segment[(separatorIndex + 1)..].Trim();

            if (name.Equals("Endpoint", StringComparison.OrdinalIgnoreCase))
            {
                endpoint = value;
            }
            else if (name.Equals("Id", StringComparison.OrdinalIgnoreCase))
            {
                id = value;
            }
            else if (name.Equals("Secret", StringComparison.OrdinalIgnoreCase))
            {
                secret = value;
            }
        }

        return IsHttpUri(endpoint)
            && !string.IsNullOrWhiteSpace(id)
            && !string.IsNullOrEmpty(secret)
            && Convert.TryFromBase64String(secret, new byte[secret.Length], out _);
    }

    private static bool IsHttpUri(string? text)
    {
        return Uri.TryCreate(text, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
    }
}
