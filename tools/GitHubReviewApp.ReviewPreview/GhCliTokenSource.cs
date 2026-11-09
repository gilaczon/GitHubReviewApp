namespace GitHubReviewApp.ReviewPreview;

/// <summary>
/// The token of the GitHub CLI's signed-in account (<c>gh auth token</c>), so the preview reads any pull request that account can read and
/// stores nothing. The token is held in memory only and never written out.
/// </summary>
public sealed class GhCliTokenSource : IGitHubTokenSource
{
    private const string SignInHint = "Sign in with `gh auth login` (and authorize SSO for the organisation if it asks), then try again.";

    private readonly string _fileName;
    private readonly string _arguments;

    public GhCliTokenSource()
        : this("gh", "auth token")
    {
    }

    // The command is a parameter so tests can run one that exists everywhere, or one that does not exist.
    internal GhCliTokenSource(string fileName, string arguments)
    {
        _fileName = fileName;
        _arguments = arguments;
    }

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(_fileName, _arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        Process? process;

        try
        {
            process = Process.Start(startInfo);
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException($"The GitHub CLI (gh) could not be started. Install it, then: {SignInHint}", exception);
        }

        using (process)
        {
            if (process is null)
            {
                throw new InvalidOperationException($"The GitHub CLI (gh) could not be started. {SignInHint}");
            }

            // Both streams are read at once: reading one to its end first can block on the other filling up.
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errors = process.StandardError.ReadToEndAsync(cancellationToken);
            await Task.WhenAll(output, errors);
            await process.WaitForExitAsync(cancellationToken);

            var token = output.Result.Trim();

            if (process.ExitCode != 0 || token.Length == 0)
            {
                throw new InvalidOperationException($"The GitHub CLI gave no token. {SignInHint}");
            }

            return token;
        }
    }
}
