namespace GitHubReviewApp.Tests.Configuration;

/// <summary>
/// Secrets live in Key Vault; the settings files in the repository hold what is safe to read aloud. These tests scan the shipped files for
/// anything credential-shaped, and prove the scanner can find each kind it looks for.
/// </summary>
public class AppSettingsSecretTests
{
    // Built at run time from pieces, so this file holds nothing that looks like a key.
    public static IEnumerable<object[]> KeyShapedValues()
    {
        yield return ["Anthropic API key", "sk-ant-" + new string('a', 30)];
        yield return ["GitHub token", "ghp_" + new string('b', 36)];
        yield return ["GitHub fine-grained token", "github_pat_" + new string('c', 30)];
        yield return ["AWS access key id", "AKIA" + new string('A', 16)];
        yield return ["Slack token", "xoxb-" + new string('1', 12)];
        yield return ["Slack webhook", "https://hooks.slack.com/services/T" + "ABC123" + "/B" + "ABC123" + "/" + "abc123def456"];
        yield return ["PEM private key", new string('-', 5) + "BEGIN RSA PRIVATE KEY" + new string('-', 5)];
        yield return ["JSON web token", "eyJ" + new string('a', 12) + ".eyJ" + new string('b', 12) + "." + new string('c', 12)];
        yield return ["password in a connection string", "Server=db;" + "Password=" + "hunter22" + ";"];
        yield return ["bearer credential", "Bearer " + new string('d', 24)];
    }

    private static List<string> ShippedSettingsFiles()
    {
        // local.settings.json is each developer's own file and is not committed, so it is not the repository's to judge.
        return Directory.EnumerateFiles(Path.Combine(RepositoryRoot.FullPath, "src", "GitHubReviewApp"), "*.json", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetFileName(path) != "local.settings.json")
            .ToList();
    }

    [Fact]
    public void Find_GivenTheShippedSettingsFiles_Should_FindNoCredential()
    {
        // Arrange
        var files = ShippedSettingsFiles();

        // Act
        var findings = files
            .SelectMany(path => SecretShapes.Find(File.ReadAllText(path)).Select(finding => $"{Path.GetFileName(path)}: {finding}"))
            .ToList();

        // Assert
        files.Select(Path.GetFileName).Should().Contain("appsettings.json", "this test is meaningless if it found no settings");
        findings.Should().BeEquivalentTo(Array.Empty<string>(), "secrets belong in Key Vault, never in a settings file");
    }

    [Theory]
    [MemberData(nameof(KeyShapedValues))]
    public void Find_GivenAValueShapedLikeAKnownKey_Should_FlagItByKind(string kind, string value)
    {
        // Arrange
        var json = JsonSerializer.Serialize(new { Setting = value });

        // Act
        var findings = SecretShapes.Find(json);

        // Assert
        findings.Should().ContainSingle().Which.Should().Be($"Setting: the value looks like a {kind}");
    }

    [Fact]
    public void Find_GivenASettingNamedForASecret_Should_FlagItByNameWhateverItsValueLooksLike()
    {
        // Arrange
        const string json = "{\"Section\":{\"GitHubWebhookSecret\":\"plain-text\",\"Nested\":{\"AnthropicApiKey\":\"x\"}}}";

        // Act
        var findings = SecretShapes.Find(json);

        // Assert
        findings.Should().HaveCount(2);
        findings.Should().Contain(finding => finding.StartsWith("Section:GitHubWebhookSecret:", StringComparison.Ordinal));
        findings.Should().Contain(finding => finding.StartsWith("Section:Nested:AnthropicApiKey:", StringComparison.Ordinal));
    }

    [Fact]
    public void Find_GivenOrdinarySettings_Should_FindNothingIncludingACountThatEndsInTokens()
    {
        // Arrange
        const string json =
            "{\"Claude\":{\"Model\":\"claude-sonnet-5-5\",\"MaxOutputTokens\":32000,\"Effort\":\"high\"},\"GitHubAppId\":\"3384929\"," +
            "\"Review\":{\"MaxFiles\":150,\"GuidelinePaths\":[\".github/ai-review.md\",\"CLAUDE.md\"]}}";

        // Act
        var findings = SecretShapes.Find(json);

        // Assert
        findings.Should().BeEmpty();
    }

    [Fact]
    public void Find_GivenASecretNamedSettingWithNoValue_Should_NotFlagThePlaceholder()
    {
        // Arrange — an empty entry is how a file says "supply this from Key Vault"
        const string json = "{\"UptraceDsn\":\"\",\"GitHubWebhookSecret\":\"   \"}";

        // Act
        var findings = SecretShapes.Find(json);

        // Assert
        findings.Should().BeEmpty();
    }

    [Fact]
    public void Find_GivenACredential_Should_NameTheSettingAndNeverQuoteTheValue()
    {
        // Arrange
        var value = "sk-ant-" + new string('z', 30);
        var json = JsonSerializer.Serialize(new { Setting = value });

        // Act
        var findings = SecretShapes.Find(json);

        // Assert — a failing test must not print the key it found
        findings.Should().NotBeEmpty();
        findings.Should().OnlyContain(finding => !finding.Contains(value, StringComparison.Ordinal));
    }
}
