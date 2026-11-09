namespace GitHubReviewApp.Configuration;

/// <summary>What <see cref="AppConfigurationSetup.AddRuntimeAppConfiguration"/> did with the App Configuration connection string secret.</summary>
public enum AppConfigurationOutcome
{
    /// <summary>The secret is blank, so App Configuration is not used and the appsettings values apply.</summary>
    NotConfigured = 0,

    /// <summary>The source was added. The store's values apply once it has been read, and until then the appsettings values do.</summary>
    Added = 1,

    /// <summary>The secret is set but is not a well-formed connection string. Nothing was added, the appsettings values apply, and the host still starts.</summary>
    Invalid = 2
}
