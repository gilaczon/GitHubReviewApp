namespace GitHubReviewApp.Review;

/// <summary>
/// Removes what looks like a credential from text the model wrote, before it is posted. The model is told never to repeat a secret it finds in a
/// diff, but the diff is written by the pull request author, and a review is a comment that stays in the page history after it is edited, so the
/// rule is also enforced here, from the shapes of the common kinds of key. It cannot find a secret that has no recognisable shape. What it does is
/// narrower and still worth having: a key that a provider issues, or a private key, is removed when it is written as it was issued. A key written to
/// get past it (a backslash before each hyphen, emphasis marks inside it) is not found. The author of the pull request can already read whatever the
/// model read, so this is there to stop an accident, such as a key committed by mistake and then repeated, and not someone who steers the model.
/// </summary>
public static class SecretRedactor
{
    public const string Replacement = "[redacted]";

    private const string PrivateKeyReplacement = "[redacted private key]";

    // Nothing here backtracks badly, but this text comes from a model reading hostile input, so no match may run for long. Declared before the
    // shapes, which use it when they are built.
    private static readonly TimeSpan _matchTimeout = TimeSpan.FromSeconds(1);

    // Applied in order. A shape that has a context (a setting's name, the word Bearer) keeps it in group 1 and 2, so the review can still say what
    // kind of thing was found and where.
    private static readonly (Regex Pattern, string Replacement)[] _shapes =
    [
        // A private key, through its end line. One that the model cut off part way is removed to the end of the text. The words of a header are bounded
        // (the longest real one, "ENCRYPTED PRIVATE KEY" or "PGP PRIVATE KEY BLOCK", is far inside 40 characters): without a bound, a run of "PRIVATE
        // KEY " repeated and never closed makes the match take time in proportion to the square of its length.
        Shape(@"-----BEGIN [A-Z0-9 ]{0,40}PRIVATE KEY[A-Z ]{0,40}-----[\s\S]*?(?:-----END [A-Z0-9 ]{0,40}PRIVATE KEY[A-Z ]{0,40}-----|\z)", PrivateKeyReplacement),

        // Keys that a provider issues, which have a prefix of their own.
        Shape(@"sk-ant-[A-Za-z0-9_\-]{20,}", Replacement),
        Shape(@"\bsk-(?:proj-)?[A-Za-z0-9_\-]{32,}", Replacement),
        Shape(@"\bgh[pousr]_[A-Za-z0-9]{30,}", Replacement),
        Shape(@"\bgithub_pat_[A-Za-z0-9_]{20,}", Replacement),
        Shape(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b", Replacement),
        Shape(@"\bxox[baprs]-[A-Za-z0-9\-]{10,}", Replacement),
        Shape(@"hooks\.slack\.com/services/T[A-Z0-9]+/B[A-Z0-9]+/[A-Za-z0-9]+", Replacement),
        Shape(@"\bAIza[0-9A-Za-z_\-]{35}(?![0-9A-Za-z_\-])", Replacement),
        Shape(@"\b[sr]k_(?:live|test)_[0-9A-Za-z]{16,}", Replacement),
        Shape(@"\bwhsec_[A-Za-z0-9]{16,}", Replacement),
        Shape(@"\beyJ[A-Za-z0-9_\-]{10,}\.eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}", Replacement),

        // A bearer credential, which keeps the word Bearer.
        Shape(@"(\bbearer\s+)[A-Za-z0-9._~+/\-]{20,}=*", "$1" + Replacement, RegexOptions.IgnoreCase),

        // A password in a connection string, which keeps the setting's name. A connection string has no space around the equals sign, which is
        // what tells it from code that assigns one variable to another (password = GetPassword()).
        // claude-allow-secret: the next line is a pattern that recognises a password setting; this file holds no credential.
        Shape(@"\b(password|pwd|accountkey|sharedaccesskey)(=)[^;\s""']{6,}", "$1$2" + Replacement, RegexOptions.IgnoreCase),

        // A quoted value of some length given to a variable whose name says it holds a secret. A value that is not quoted is a reference to one
        // (a call that fetches the password), which is not itself a secret.
        Shape(
            """(\b(?:password|passwd|secret|client_?secret|api_?key|access_?token|auth_?token|token)["']?\s*[:=]\s*)(["'])[^"'\s]{8,}\2""",
            "$1$2" + Replacement + "$2",
            RegexOptions.IgnoreCase)
    ];

    /// <summary>The shapes the redactor looks for, so that a test can check that each has an example and a time limit.</summary>
    internal static IReadOnlyList<(Regex Pattern, string Replacement)> Shapes => _shapes;

    /// <summary>
    /// The text with every credential in it replaced. When a pattern runs out of time the whole text is replaced: a text that cannot be checked
    /// is not posted.
    /// </summary>
    public static string Redact(string? text)
    {
        return Redact(text, _shapes);
    }

    // The shapes are a parameter so that a test can supply one that runs out of time.
    internal static string Redact(string? text, IReadOnlyList<(Regex Pattern, string Replacement)> shapes)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        try
        {
            return shapes.Aggregate(text, (current, shape) => shape.Pattern.Replace(current, shape.Replacement));
        }
        catch (RegexMatchTimeoutException)
        {
            return Replacement;
        }
    }

    private static (Regex Pattern, string Replacement) Shape(string pattern, string replacement, RegexOptions extraOptions = RegexOptions.None)
    {
        return (new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant | extraOptions, _matchTimeout), replacement);
    }
}
