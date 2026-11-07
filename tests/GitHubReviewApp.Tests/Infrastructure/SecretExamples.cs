namespace GitHubReviewApp.Tests.Infrastructure;

/// <summary>
/// A made-up credential of each kind <see cref="SecretShapes"/> knows. Each is put together from pieces when the test runs, so this file holds
/// nothing that looks like a key and trips a scanner itself, and none is a real credential: the filler is a fixed run of letters and digits.
/// </summary>
internal static class SecretExamples
{
    private const string Pattern = "aB3dE5gH7jK9mN1pQ3rS5tU7vW9xY1zA3bC5dE7fG9hJ1kL3";

    private static readonly string _alphabet = string.Concat(Enumerable.Repeat(Pattern, 3));

    public static IReadOnlyDictionary<string, SecretExample> ByKind { get; } = new Dictionary<string, SecretExample>
    {
        ["Anthropic API key"] = Whole("sk-" + "ant-" + Filler(40)),
        ["GitHub token"] = Whole("gh" + "p_" + Filler(36)),
        ["GitHub fine-grained token"] = Whole("github_" + "pat_" + Filler(30)),
        ["AWS access key id"] = Whole("AK" + "IA" + Upper(16)),
        ["Slack token"] = Whole("xo" + "xb-" + "123456789012-" + Filler(24)),
        ["Slack webhook"] = Whole("hooks." + "slack.com/services/" + "T" + Upper(8) + "/B" + Upper(8) + "/" + Filler(24)),
        ["PEM private key"] = PrivateKey(),
        ["JSON web token"] = Whole("ey" + "J" + Filler(20) + ".ey" + "J" + Filler(20) + "." + Filler(24)),
        ["password in a connection string"] = new SecretExample("Server=db;Pass" + "word=" + Filler(12) + ";", Filler(12)),
        ["bearer credential"] = new SecretExample("Authorization: Bear" + "er " + Filler(32), Filler(32)),
        ["OpenAI API key"] = Whole("s" + "k-" + Filler(40)),
        ["OpenAI project key"] = Whole("s" + "k-" + "proj-" + Filler(40)),
        ["AWS temporary access key id"] = Whole("AS" + "IA" + Upper(16)),
        ["Google API key"] = Whole("AI" + "za" + Filler(35)),
        ["Stripe secret key"] = Whole("s" + "k_" + "live_" + Filler(24)),
        ["Stripe restricted key"] = Whole("r" + "k_" + "test_" + Filler(24)),
        ["Stripe webhook secret"] = Whole("wh" + "sec_" + Filler(24)),
        ["quoted value given to a secret's name"] = new SecretExample("api" + "_key = \"" + Filler(20) + "\"", Filler(20))
    };

    /// <summary>
    /// The kinds whose alphabet includes a hyphen, each with an example that ends in one. About one key in sixty-four does, and no word boundary
    /// lies between a hyphen and the space, or the end of the text, that comes after it, which a pattern that ends in a boundary does not allow for.
    /// </summary>
    public static IReadOnlyDictionary<string, SecretExample> EndingInHyphen { get; } = new Dictionary<string, SecretExample>
    {
        ["Anthropic API key"] = Whole("sk-" + "ant-" + Filler(40) + "-"),
        ["OpenAI API key"] = Whole("s" + "k-" + Filler(40) + "-"),
        ["OpenAI project key"] = Whole("s" + "k-" + "proj-" + Filler(40) + "-"),
        ["Slack token"] = Whole("xo" + "xb-" + "123456789012-" + Filler(23) + "-"),
        ["JSON web token"] = Whole("ey" + "J" + Filler(20) + ".ey" + "J" + Filler(20) + "." + Filler(23) + "-"),
        ["Google API key"] = Whole("AI" + "za" + Filler(34) + "-")
    };

    private static string Filler(int length)
    {
        return _alphabet[..length];
    }

    private static string Upper(int length)
    {
        return _alphabet.ToUpperInvariant()[..length];
    }

    private static SecretExample Whole(string text)
    {
        return new SecretExample(text, text);
    }

    private static SecretExample PrivateKey()
    {
        var body = Filler(64);

        return new SecretExample("-----" + "BEGIN " + "RSA PRIVATE KEY-----\n" + body + "\n-----" + "END " + "RSA PRIVATE KEY-----", body);
    }
}
