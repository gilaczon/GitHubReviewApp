namespace GitHubReviewApp.Tests.Infrastructure;

/// <summary>
/// Finds what looks like a credential in a JSON settings file: a setting whose name says it holds one, or a value shaped like a known kind of
/// key. Secrets live in Key Vault; settings files hold what is safe to read aloud. Findings name the setting and the kind, never the value, so a
/// failing test does not print the thing it found.
/// </summary>
internal static class SecretShapes
{
    // Settings named for what they hold. The pattern ends at the name's end, so MaxOutputTokens (a count) is not mistaken for a token.
    private static readonly Regex _secretName = new(
        "(Secret|Password|Passwd|ApiKey|PrivateKey|ConnectionString|Dsn|Credential|Token)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Each shape is written as a pattern, never as an example, so this file holds nothing that looks like a key and trips a scanner itself.
    private static readonly (string Kind, Regex Pattern)[] _valueShapes =
    [
        ("Anthropic API key", new Regex("sk-ant-[A-Za-z0-9_-]{20,}", RegexOptions.CultureInvariant)),
        ("GitHub token", new Regex("gh[pousr]_[A-Za-z0-9]{30,}", RegexOptions.CultureInvariant)),
        ("GitHub fine-grained token", new Regex("github_pat_[A-Za-z0-9_]{20,}", RegexOptions.CultureInvariant)),
        ("AWS access key id", new Regex("AKIA[0-9A-Z]{16}", RegexOptions.CultureInvariant)),
        ("Slack token", new Regex("xox[baprs]-[A-Za-z0-9-]{10,}", RegexOptions.CultureInvariant)),
        ("Slack webhook", new Regex("hooks\\.slack\\.com/services/T[A-Z0-9]+/B[A-Z0-9]+/[A-Za-z0-9]+", RegexOptions.CultureInvariant)),
        ("PEM private key", new Regex("-{5}BEGIN [A-Z ]*PRIVATE KEY-{5}", RegexOptions.CultureInvariant)),
        ("JSON web token", new Regex("eyJ[A-Za-z0-9_-]{10,}\\.eyJ[A-Za-z0-9_-]{10,}\\.[A-Za-z0-9_-]{10,}", RegexOptions.CultureInvariant)),
        ("password in a connection string", new Regex("(?i)(password|pwd|accountkey|sharedaccesskey)\\s*=\\s*[^;\\s]{4,}", RegexOptions.CultureInvariant)),
        ("bearer credential", new Regex("(?i)bearer\\s+[A-Za-z0-9._~+/-]{20,}", RegexOptions.CultureInvariant)),
        ("OpenAI API key", new Regex("sk-[A-Za-z0-9]{40,}", RegexOptions.CultureInvariant)),
        ("OpenAI project key", new Regex("sk-proj-[A-Za-z0-9_-]{40,}", RegexOptions.CultureInvariant)),
        ("AWS temporary access key id", new Regex("ASIA[0-9A-Z]{16}", RegexOptions.CultureInvariant)),
        ("Google API key", new Regex("AIza[0-9A-Za-z_-]{35}", RegexOptions.CultureInvariant)),
        ("Stripe secret key", new Regex("sk_(?:live|test)_[0-9A-Za-z]{16,}", RegexOptions.CultureInvariant)),
        ("Stripe restricted key", new Regex("rk_(?:live|test)_[0-9A-Za-z]{16,}", RegexOptions.CultureInvariant)),
        ("Stripe webhook secret", new Regex("whsec_[A-Za-z0-9]{16,}", RegexOptions.CultureInvariant)),
        ("quoted value given to a secret's name", new Regex("(?i)(password|passwd|secret|client_?secret|api_?key|access_?token|auth_?token|token)[\"']?\\s*[:=]\\s*[\"'][^\"'\\s]{8,}[\"']", RegexOptions.CultureInvariant))
    ];

    /// <summary>Every kind of credential this list knows by its shape.</summary>
    public static IReadOnlyList<string> AllKinds => _valueShapes.Select(shape => shape.Kind).ToList();

    /// <summary>The kinds of credential the text contains, by shape. It names the kinds and never the values.</summary>
    public static IReadOnlyList<string> KindsIn(string text)
    {
        return _valueShapes.Where(shape => shape.Pattern.IsMatch(text)).Select(shape => shape.Kind).ToList();
    }

    /// <summary>What looks like a credential in the JSON text, as "setting: why". Empty values are placeholders and are not findings.</summary>
    public static IReadOnlyList<string> Find(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        return Flatten(document.RootElement, string.Empty)
            .Where(setting => !string.IsNullOrWhiteSpace(setting.Value))
            .SelectMany(setting => Describe(setting.Path, setting.Value))
            .ToList();
    }

    private static IEnumerable<string> Describe(string path, string value)
    {
        var name = path[(path.LastIndexOf(':') + 1)..];

        if (_secretName.IsMatch(name))
        {
            yield return $"{path}: the name says it holds a credential, which belongs in Key Vault";
        }

        foreach (var (kind, pattern) in _valueShapes.Where(shape => shape.Pattern.IsMatch(value)))
        {
            yield return $"{path}: the value looks like a {kind}";
        }
    }

    private static IEnumerable<(string Path, string Value)> Flatten(JsonElement element, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                return element.EnumerateObject().SelectMany(property => Flatten(property.Value, path.Length == 0 ? property.Name : $"{path}:{property.Name}"));

            case JsonValueKind.Array:
                return element.EnumerateArray().SelectMany((item, index) => Flatten(item, $"{path}:{index}"));

            case JsonValueKind.String:
                return [(path, element.GetString() ?? string.Empty)];

            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                return [(path, element.GetRawText())];

            default:
                return [];
        }
    }
}
