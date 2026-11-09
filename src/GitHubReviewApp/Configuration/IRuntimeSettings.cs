namespace GitHubReviewApp.Configuration;

/// <summary>Where the settings that can change while the app runs are read from (design 5.11).</summary>
public interface IRuntimeSettings
{
    /// <summary>The settings in force right now. Always a valid snapshot, and never changed once handed out: a reload replaces it with another.</summary>
    RuntimeSettings Current { get; }
}
