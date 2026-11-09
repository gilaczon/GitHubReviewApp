namespace GitHubReviewApp.Tests.Infrastructure;

/// <summary>
/// A GitHub App JWT as GitHub reads it: RS256 over "header.payload", an issuer that is the app id, and an expiry that is in the
/// future but no more than ten minutes away. The test key's public half does the verifying, so a JWT only passes if the app really signed it.
/// </summary>
internal sealed record GitHubAppJwt(string Algorithm, string Issuer, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt, bool IsSignatureValid)
{
    private static readonly TimeSpan _clockSkew = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _maxLifetime = TimeSpan.FromMinutes(10);

    /// <summary>Returns null when the text is not a three-part JWT with the claims GitHub needs.</summary>
    public static GitHubAppJwt? Parse(string token, RSA publicKey)
    {
        var parts = token.Split('.');

        if (parts.Length != 3)
        {
            return null;
        }

        try
        {
            using var header = JsonDocument.Parse(Base64UrlDecode(parts[0]));
            using var payload = JsonDocument.Parse(Base64UrlDecode(parts[1]));

            var isSignatureValid = publicKey.VerifyData(
                Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"),
                Base64UrlDecode(parts[2]),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            return new GitHubAppJwt(
                header.RootElement.GetProperty("alg").GetString()!,
                payload.RootElement.GetProperty("iss").GetString()!,
                DateTimeOffset.FromUnixTimeSeconds(payload.RootElement.GetProperty("iat").GetInt64()),
                DateTimeOffset.FromUnixTimeSeconds(payload.RootElement.GetProperty("exp").GetInt64()),
                isSignatureValid);
        }
        catch (Exception exception) when (exception is FormatException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>A JWT for the given app, signed the way GitHub expects, that is valid now and for five minutes. For tests that call the app endpoints directly.</summary>
    public static string Sign(string appId, string privateKeyPem)
    {
        var issuedAt = DateTimeOffset.UtcNow;
        var header = Base64UrlEncode(Encoding.UTF8.GetBytes("""{"alg":"RS256","typ":"JWT"}"""));
        var payload = Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            iss = appId,
            iat = issuedAt.AddSeconds(-30).ToUnixTimeSeconds(),
            exp = issuedAt.AddMinutes(5).ToUnixTimeSeconds()
        })));

        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        var signature = Base64UrlEncode(rsa.SignData(Encoding.ASCII.GetBytes($"{header}.{payload}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

        return $"{header}.{payload}.{signature}";
    }

    /// <summary>The reason GitHub would reject this JWT at <paramref name="now"/>, or null when it would accept it.</summary>
    public string? Problem(string expectedAppId, DateTimeOffset now)
    {
        if (Algorithm != "RS256")
        {
            return $"the algorithm is {Algorithm}, not RS256";
        }

        if (!IsSignatureValid)
        {
            return "the signature does not verify against the app's key";
        }

        if (Issuer != expectedAppId)
        {
            return "the issuer is not the app id";
        }

        if (IssuedAt > now + _clockSkew)
        {
            return "iat is in the future";
        }

        if (ExpiresAt <= now)
        {
            return "exp is in the past";
        }

        return ExpiresAt > now + _maxLifetime ? "exp is more than ten minutes away" : null;
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] Base64UrlDecode(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');

        return Convert.FromBase64String(padded);
    }
}
