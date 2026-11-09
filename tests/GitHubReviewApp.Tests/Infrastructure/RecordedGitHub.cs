namespace GitHubReviewApp.Tests.Infrastructure;

/// <summary>
/// Real GitHub API responses for one public pull request, recorded once and kept as embedded resources. Fixtures/GitHub/README.md says where they
/// came from and what was trimmed. Everything the app reads is as GitHub returned it.
/// </summary>
internal static class RecordedGitHub
{
    public const string Owner = "microsoft";
    public const string Repo = "vscode";
    public const int PrNumber = 346019;
    public const string HeadSha = "b434f850ab7e8a4fbe02fdb4925d5f86b8486d1d";
    public const string BaseSha = "abb54349981451cd344fc1dd7a4299f7bcf0152e";
    public const string Title = "Copilot - bump node types so that they are consistent with the rest of the repository";

    public static string PullRequest => Read("pull-request.json");

    public static string PullRequestFiles => Read("pull-request-files.json");

    /// <summary>
    /// The recorded pull request with only its <c>state</c> changed to open. It was merged when it was recorded, and the pipeline reviews open pull
    /// requests only; every other value is as recorded, so <c>merged</c>, <c>merged_at</c> and <c>closed_at</c> still describe a merged pull request.
    /// The app reads none of those three, which is why the one change is enough.
    /// </summary>
    public static string PullRequestAsOpen()
    {
        const string closed = "\"state\": \"closed\"";
        var json = PullRequest;

        if (!json.Contains(closed, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The recorded pull request no longer has the state this helper changes.");
        }

        return json.Replace(closed, "\"state\": \"open\"", StringComparison.Ordinal);
    }

    /// <summary>The recorded files as the app reads them, so a test can compare what the app made of them with what GitHub said.</summary>
    public static IReadOnlyList<PullRequestFile> Files()
    {
        using var document = JsonDocument.Parse(PullRequestFiles);

        return document.RootElement.EnumerateArray()
            .Select(file => new PullRequestFile(
                file.GetProperty("filename").GetString()!,
                file.GetProperty("status").GetString()!,
                file.GetProperty("additions").GetInt32(),
                file.GetProperty("deletions").GetInt32(),
                file.TryGetProperty("patch", out var patch) ? patch.GetString() : null))
            .ToList();
    }

    private static string Read(string fileName)
    {
        var assembly = typeof(RecordedGitHub).Assembly;
        var resourceName = assembly.GetManifestResourceNames().Single(name => name.EndsWith("." + fileName, StringComparison.Ordinal));

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' could not be opened.");
        using var reader = new StreamReader(stream, Encoding.UTF8);

        return reader.ReadToEnd();
    }
}
