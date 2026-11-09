namespace GitHubReviewApp.Tests.Infrastructure;

/// <summary>Where the repository is on disk, found by walking up from the test output folder to the solution file.</summary>
internal static class RepositoryRoot
{
    public static string FullPath { get; } = Find();

    private static string Find()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GitHubReviewApp.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not find GitHubReviewApp.slnx above the test output folder.");
    }
}
