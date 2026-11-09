namespace GitHubReviewApp.ReviewPreview;

/// <summary>
/// A pull request named by its GitHub link (<c>https://github.com/{owner}/{repo}/pull/{number}</c>, with any tab, query or anchor after it)
/// or in the short form <c>{owner}/{repo}#{number}</c>.
/// </summary>
public sealed record PullRequestLink(string Owner, string Repo, int Number)
{
    private static readonly Regex _link = new(
        @"^https?://(?:www\.)?github\.com/(?<owner>[A-Za-z0-9-]+)/(?<repo>[A-Za-z0-9._-]+)/pull/(?<number>\d{1,9})(?:[/?#].*)?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex _shortForm = new(
        @"^(?<owner>[A-Za-z0-9-]+)/(?<repo>[A-Za-z0-9._-]+)#(?<number>\d{1,9})$",
        RegexOptions.CultureInvariant);

    /// <summary>The pull request's page on GitHub.</summary>
    public string Url => $"https://github.com/{Owner}/{Repo}/pull/{Number}";

    /// <summary>The pull request as GitHub writes it, for example <c>my-org/my-service#7</c>.</summary>
    public string Name => $"{Owner}/{Repo}#{Number}";

    /// <summary>Reads a pull request link; false for anything else, including a link to an issue or a commit.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out PullRequestLink? link)
    {
        link = null;
        var trimmed = text?.Trim() ?? string.Empty;
        var match = _link.Match(trimmed);

        if (!match.Success)
        {
            match = _shortForm.Match(trimmed);
        }

        if (!match.Success || !int.TryParse(match.Groups["number"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
        {
            return false;
        }

        link = new PullRequestLink(match.Groups["owner"].Value, match.Groups["repo"].Value, number);
        return true;
    }
}
