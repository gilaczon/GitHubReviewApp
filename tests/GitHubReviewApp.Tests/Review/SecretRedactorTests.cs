namespace GitHubReviewApp.Tests.Review;

public class SecretRedactorTests
{
    private const string Value = "abcdEFGH12345678";

    public static IEnumerable<object[]> Kinds()
    {
        return SecretExamples.ByKind.Keys.Select(kind => new object[] { kind });
    }

    [Fact]
    public void Examples_GivenTheIndependentListOfSecretShapes_Should_CoverEveryKindItKnows()
    {
        // Arrange
        var kindsWithAnExample = SecretExamples.ByKind.Keys;

        // Act
        var kindsTheListKnows = SecretShapes.AllKinds;

        // Assert — a shape added to the list must come with an example, or the redactor is never checked against it
        kindsWithAnExample.Should().BeEquivalentTo(kindsTheListKnows);
    }

    [Fact]
    public void Shapes_GivenTheRedactorsOwnList_Should_EachBeMatchedByAnExampleSoNoShapeIsAddedOrKeptUntested()
    {
        // Arrange — the check above compares the examples with the test's own list. This one starts from what the redactor really looks for.
        var examples = SecretExamples.ByKind.Values.Select(example => example.Text).ToList();

        // Act
        var shapesWithoutAnExample = SecretRedactor.Shapes
            .Where(shape => !examples.Any(example => shape.Pattern.IsMatch(example)))
            .Select(shape => shape.Pattern.ToString())
            .ToList();

        // Assert
        SecretRedactor.Shapes.Should().NotBeEmpty();
        shapesWithoutAnExample.Should().BeEmpty("a shape that no example reaches could be broken or deleted without a test noticing");
    }

    [Fact]
    public void Shapes_GivenTheRedactorsOwnList_Should_EachHaveAFiniteTimeLimitBecauseTheTextComesFromAModelReadingHostileInput()
    {
        // Arrange

        // Act
        var withoutALimit = SecretRedactor.Shapes
            .Where(shape => shape.Pattern.MatchTimeout == Regex.InfiniteMatchTimeout || shape.Pattern.MatchTimeout > TimeSpan.FromSeconds(2))
            .Select(shape => shape.Pattern.ToString())
            .ToList();

        // Assert
        withoutALimit.Should().BeEmpty("no match may run for long: past its limit the whole text is replaced, so a limit that is too long would hold a review for ever");
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Examples_GivenAKindOfSecret_Should_LookLikeThatKindToTheIndependentList(string kind)
    {
        // Arrange
        var example = SecretExamples.ByKind[kind];

        // Act
        var kinds = SecretShapes.KindsIn(example.Text);

        // Assert
        kinds.Should().Contain(kind);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Redact_GivenAKindOfSecretInTheMiddleOfAFinding_Should_RemoveItAndKeepTheTextAroundIt(string kind)
    {
        // Arrange
        var example = SecretExamples.ByKind[kind];
        var text = $"The key {example.Text} is committed here, and it grants access.";

        // Act
        var redacted = SecretRedactor.Redact(text);

        // Assert
        redacted.Should().NotContain(example.Secret);
        redacted.Should().StartWith("The key ").And.EndWith(" is committed here, and it grants access.");
        redacted.Should().Contain("[redacted");

        // The independent list cannot tell a placeholder from a value (it reads "Name=[redacted]" as a password), so the placeholders come out first
        var withoutPlaceholders = redacted.Replace("[redacted private key]", string.Empty).Replace(SecretRedactor.Replacement, string.Empty);
        SecretShapes.KindsIn(withoutPlaceholders).Should().BeEmpty("the independent list of secret shapes must find nothing left");
    }

    public static IEnumerable<object[]> KindsThatCanEndInAHyphenAndWhatMayFollow()
    {
        return SecretExamples.EndingInHyphen.Keys.SelectMany(
            kind => new[] { " is committed here.", string.Empty },
            (kind, followedBy) => new object[] { kind, followedBy });
    }

    [Theory]
    [MemberData(nameof(KindsThatCanEndInAHyphenAndWhatMayFollow))]
    public void Redact_GivenAKeyWhoseLastCharacterIsAHyphen_Should_RemoveAllOfItWhateverFollows(string kind, string followedBy)
    {
        // Arrange — the hyphen is part of the key's alphabet, so about one key in sixty-four ends with it, and no word boundary lies after a hyphen
        var example = SecretExamples.EndingInHyphen[kind];

        // Act
        var redacted = SecretRedactor.Redact("The key " + example.Text + followedBy);

        // Assert
        redacted.Should().Be("The key " + SecretRedactor.Replacement + followedBy);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Redact_GivenTextThatWasAlreadyRedacted_Should_ChangeNothing(string kind)
    {
        // Arrange
        var once = SecretRedactor.Redact($"before {SecretExamples.ByKind[kind].Text} after");

        // Act
        var twice = SecretRedactor.Redact(once);

        // Assert
        twice.Should().Be(once);
    }

    [Fact]
    public void Redact_GivenAShortAnthropicKey_Should_StillRemoveItBecauseItsPrefixIsUnmistakable()
    {
        // Arrange — below the length the general "sk-" shape asks for, so only the shape for this provider's own prefix can catch it
        var key = "sk-" + "ant-" + SecretExamples.ByKind["GitHub token"].Text[^24..];
        var text = $"The key {key} is committed.";

        // Act
        var redacted = SecretRedactor.Redact(text);

        // Assert
        redacted.Should().Be("The key [redacted] is committed.");
    }

    [Fact]
    public void Redact_GivenSeveralSecretsInOneText_Should_RemoveEachOfThem()
    {
        // Arrange
        var first = SecretExamples.ByKind["GitHub token"];
        var second = SecretExamples.ByKind["Anthropic API key"];
        var third = SecretExamples.ByKind["AWS access key id"];

        // Act
        var redacted = SecretRedactor.Redact($"{first.Text}, then {second.Text} and also {third.Text}.");

        // Assert
        redacted.Should().NotContain(first.Secret).And.NotContain(second.Secret).And.NotContain(third.Secret);
        redacted.Should().Contain(", then ").And.Contain(" and also ");
    }

    [Fact]
    public void Redact_GivenAPrivateKeyBlock_Should_RemoveEveryLineOfItAndKeepWhatFollows()
    {
        // Arrange
        var key = SecretExamples.ByKind["PEM private key"];
        var text = $"Found:\n{key.Text}\nand then more text on the next line.";

        // Act
        var redacted = SecretRedactor.Redact(text);

        // Assert
        redacted.Should().Be("Found:\n[redacted private key]\nand then more text on the next line.");
    }

    [Fact]
    public void Redact_GivenAPrivateKeyThatIsCutOffBeforeItsEnd_Should_RemoveEverythingAfterItsHeader()
    {
        // Arrange — the model may stop part way through a key, and the part it wrote is still a key
        var header = "-----" + "BEGIN " + "PRIVATE KEY-----";
        var text = $"Leaked:\n{header}\n{SecretExamples.ByKind["PEM private key"].Secret}\nand a last line";

        // Act
        var redacted = SecretRedactor.Redact(text);

        // Assert
        redacted.Should().Be("Leaked:\n[redacted private key]");
    }

    [Theory]
    [InlineData("pass" + "word = \"" + Value + "\"", "pass" + "word = \"[redacted]\"")]
    [InlineData("\"api" + "Key\": \"" + Value + "\"", "\"api" + "Key\": \"[redacted]\"")]
    [InlineData("const to" + "ken = '" + Value + "';", "const to" + "ken = '[redacted]';")]
    [InlineData("client_" + "secret: \"" + Value + "\"", "client_" + "secret: \"[redacted]\"")]
    public void Redact_GivenAQuotedValueAssignedToASecretNamedVariable_Should_RemoveTheValueAndKeepTheName(string input, string expected)
    {
        // Arrange

        // Act
        var redacted = SecretRedactor.Redact(input);

        // Assert
        redacted.Should().Be(expected);
    }

    [Theory]
    [InlineData("pass" + "word = GetPass" + "word();")]
    [InlineData("var to" + "ken = request.Token;")]
    [InlineData("pass" + "word = \"\"")]
    [InlineData("\"pass" + "word\": \"short\"")]
    [InlineData("to" + "ken = \"a b c d e f g h\"")]
    [InlineData("the pass" + "word must be at least twelve characters long")]
    [InlineData("Use sk-learn for machine learning, or sk-image if you must.")]
    [InlineData("ghp_short and AKIA are only prefixes")]
    [InlineData("The hash is e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855 for the empty input.")]
    [InlineData("The request id is 72d3162e-cc78-11e3-81ab-4c9367dc0958.")]
    [InlineData("Send Bearer <token> in the Authorization header.")]
    [InlineData("```cs\nvar client = new HttpClient();\nclient.DefaultRequestHeaders.Add(\"X-Trace\", traceId);\n```")]
    public void Redact_GivenTextThatOnlyLooksLikeItMentionsASecret_Should_LeaveItUnchanged(string input)
    {
        // Arrange

        // Act
        var redacted = SecretRedactor.Redact(input);

        // Assert
        redacted.Should().Be(input);
    }

    [Fact]
    public void Redact_GivenAPatternThatRunsOutOfTime_Should_ReplaceTheWholeTextRatherThanPostItUnchecked()
    {
        // Arrange — a text that cannot be checked is not posted. This pattern backtracks without end on a run of letters that does not finish properly.
        var slow = new Regex("(a+)+$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(1));
        var text = new string('a', 60) + "!";

        // Act
        var redacted = SecretRedactor.Redact(text, [(slow, "x")]);

        // Assert
        redacted.Should().Be(SecretRedactor.Replacement);
    }

    [Fact]
    public void Redact_GivenAHugeRunThatOnlyLooksLikeTheStartOfAPrivateKey_Should_FinishAndLeaveItAloneInsteadOfTimingOut()
    {
        // Arrange — the model's text comes from a model reading hostile input. A header written as "BEGIN" and then "PRIVATE KEY " over and over, with no
        // closing dashes, used to backtrack in proportion to the square of its length: past the one second limit at 120,000 characters, where the
        // fail-closed answer replaced the whole text.
        var text = "-----" + "BEGIN " + string.Concat(Enumerable.Repeat("PRIVATE KEY ", 10_000));

        // Act
        var redacted = SecretRedactor.Redact(text);

        // Assert
        redacted.Should().Be(text);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("RSA ", "")]
    [InlineData("ENCRYPTED ", "")]
    [InlineData("OPENSSH ", "")]
    [InlineData("PGP ", " BLOCK")]
    public void Redact_GivenAPrivateKeyWithTheHeadersRealKeysHave_Should_StillRemoveIt(string kind, string suffix)
    {
        // Arrange — the bound that keeps the pattern fast must leave every real header alone. The markers are put together here so that this file
        // holds nothing that looks like a key.
        var key = "-----" + "BEGIN " + kind + "PRIVATE KEY" + suffix + "-----\nabc\n-----" + "END " + kind + "PRIVATE KEY" + suffix + "-----";

        // Act
        var redacted = SecretRedactor.Redact("The file holds " + key + " and more.");

        // Assert
        redacted.Should().Be("The file holds [redacted private key] and more.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Redact_GivenNothing_Should_ReturnAnEmptyString(string? input)
    {
        // Arrange

        // Act
        var redacted = SecretRedactor.Redact(input);

        // Assert
        redacted.Should().BeEmpty();
    }
}
