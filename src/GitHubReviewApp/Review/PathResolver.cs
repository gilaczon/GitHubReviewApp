namespace GitHubReviewApp.Review;

/// <summary>
/// Maps the path a model wrote in a finding to the exact path of a changed file, tolerating the usual slips:
/// diff prefixes (<c>a/</c>, <c>b/</c>), a leading <c>./</c> or <c>/</c>, backslashes and different casing.
/// It never guesses between files that differ only by case.
/// </summary>
internal sealed class PathResolver
{
    private static readonly string[] _prefixes = ["./", "/", "a/", "b/"];

    private readonly Dictionary<string, PullRequestFile> _exact = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _ignoringCase = new(StringComparer.OrdinalIgnoreCase);

    public PathResolver(IEnumerable<PullRequestFile> files)
    {
        foreach (var file in files)
        {
            _exact[file.Path] = file;

            if (!_ignoringCase.TryGetValue(file.Path, out var sameIgnoringCase))
            {
                sameIgnoringCase = [];
                _ignoringCase[file.Path] = sameIgnoringCase;
            }

            sameIgnoringCase.Add(file.Path);
        }
    }

    public string? GetPatch(string exactPath)
    {
        return _exact.TryGetValue(exactPath, out var file) ? file.Patch : null;
    }

    public string? Resolve(string modelPath)
    {
        var variants = BuildVariants(modelPath).ToList();

        foreach (var variant in variants)
        {
            if (_exact.ContainsKey(variant))
            {
                return variant;
            }
        }

        foreach (var variant in variants)
        {
            if (_ignoringCase.TryGetValue(variant, out var matches) && matches.Count == 1)
            {
                return matches[0];
            }
        }

        return null;
    }

    private static IEnumerable<string> BuildVariants(string modelPath)
    {
        var candidate = modelPath.Trim().Replace('\\', '/');
        yield return candidate;

        foreach (var prefix in _prefixes)
        {
            if (candidate.StartsWith(prefix, StringComparison.Ordinal))
            {
                yield return candidate[prefix.Length..];
            }
        }
    }
}
