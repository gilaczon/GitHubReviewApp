namespace GitHubReviewApp.Tests.Infrastructure;

/// <summary>
/// Generates a real RSA-2048 key pair for use in GitHubAppAuthService JWT tests.
/// The key is generated once per test run and cached as a static field.
/// </summary>
internal static class RsaKeyHelper
{
    private static readonly Lazy<(string Pem, RSA PublicKey)> _lazyKey = new(GenerateKey);

    /// <summary>RSA private key in PKCS#8 PEM format (as returned by Key Vault).</summary>
    public static string PrivateKeyPem => _lazyKey.Value.Pem;

    /// <summary>Corresponding RSA public key for verifying JWT signatures in tests.</summary>
    public static RSA PublicKey => _lazyKey.Value.PublicKey;

    private static (string Pem, RSA PublicKey) GenerateKey()
    {
        using var rsa = RSA.Create(2048);

        // Export PKCS#8 private key — matches ImportFromPem used in production
        var privateKeyBytes = rsa.ExportPkcs8PrivateKey();
        var sb = new StringBuilder();
        sb.AppendLine("-----BEGIN PRIVATE KEY-----");
        sb.AppendLine(Convert.ToBase64String(privateKeyBytes, Base64FormattingOptions.InsertLineBreaks));
        sb.AppendLine("-----END PRIVATE KEY-----");

        // Detach the public key into a separate instance so the private key RSA can be disposed
        var publicKeyOnly = RSA.Create();
        publicKeyOnly.ImportRSAPublicKey(rsa.ExportRSAPublicKey(), out _);

        return (sb.ToString(), publicKeyOnly);
    }
}
