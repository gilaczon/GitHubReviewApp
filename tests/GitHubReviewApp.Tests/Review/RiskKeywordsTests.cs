namespace GitHubReviewApp.Tests.Review;

/// <summary>
/// The words of a path or repository name (design 5.3): split at every character that is not a letter or digit, at camelCase and acronym
/// boundaries and where letters and digits meet, lower-cased; and the first high-risk tier, in enum order, that has one of those words, or one of
/// them without a plural "s", as a keyword. Whole words only.
/// </summary>
public class RiskKeywordsTests
{
    [Theory]
    [InlineData("src/OpenBanking/BankStatementParser_v2.cs", "src open banking bank statement parser v 2 cs")]
    [InlineData("PayIDAlias.cs", "pay id alias cs")]
    [InlineData("ParseJWT", "parse jwt")]
    [InlineData("XMLHttpRequest", "xml http request")]
    [InlineData("Moneyme.Global.Library-VueApp", "moneyme global library vue app")]
    [InlineData("src\\Api\\my file.cs", "src api my file cs")]
    public void Tokenize_GivenAPathWithSeparatorsAndCamelCase_Should_SplitIntoLowerCaseWords(string text, string expectedWords)
    {
        // Arrange
        var expected = expectedWords.Split(' ');

        // Act
        var tokens = RiskKeywords.Tokenize(text);

        // Assert
        tokens.Should().Equal(expected);
    }

    [Theory]
    [InlineData("OAuth2Client", "o auth 2 client")]
    [InlineData("V2PaymentsController", "v 2 payments controller")]
    [InlineData("oauth2-proxy", "oauth 2 proxy")]
    [InlineData("JWTsValidator", "jwts validator")]
    [InlineData("JWTs", "jwts")]
    [InlineData("JWTs2", "jwts 2")]
    [InlineData("APIService", "api service")]
    public void Tokenize_GivenDigitsOrAPluralAcronym_Should_MakeEachDigitRunAWordAndKeepThePluralS(string text, string expectedWords)
    {
        // Arrange — the "s" stays with an acronym when it ends the word or another word or a number follows it; "APIService" has no plural
        var expected = expectedWords.Split(' ');

        // Act
        var tokens = RiskKeywords.Tokenize(text);

        // Assert
        tokens.Should().Equal(expected);
    }

    [Theory]
    [InlineData("src/Auth/JwtValidator.cs", RiskTier.Auth)]
    [InlineData("src/Payments/RefundService.cs", RiskTier.Payments)]
    [InlineData("src/Cards/CardTokenizer.cs", RiskTier.Cards)]
    [InlineData("src/OpenBanking/ConsentController.cs", RiskTier.BankLogin)]
    [InlineData("src/Customers/AddressValidator.cs", RiskTier.PersonalData)]
    public void MatchHighRiskTier_GivenAPathInEachTier_Should_ReturnThatTier(string path, RiskTier expectedTier)
    {
        // Arrange

        // Act
        var tier = RiskKeywords.MatchHighRiskTier(path);

        // Assert
        tier.Should().Be(expectedTier);
    }

    [Fact]
    public void MatchHighRiskTier_GivenAPathMatchingTwoTiers_Should_ReturnTheHigherRiskOne()
    {
        // Arrange — "payments" is a payments word and "login" an auth word; auth comes first
        const string path = "src/Payments/LoginAudit.cs";

        // Act
        var tier = RiskKeywords.MatchHighRiskTier(path);

        // Assert
        tier.Should().Be(RiskTier.Auth);
    }

    [Theory]
    [InlineData("src/OAuth2Client.cs", RiskTier.Auth)]
    [InlineData("src/Auth0Service.cs", RiskTier.Auth)]
    [InlineData("src/Pbkdf2PasswordHasher.cs", RiskTier.Auth)]
    [InlineData("src/Aes256CardEncryptor.cs", RiskTier.Cards)]
    [InlineData("src/V2PaymentsController.cs", RiskTier.Payments)]
    [InlineData("src/JWTsValidator.cs", RiskTier.Auth)]
    [InlineData("src/Audit/Logins.cs", RiskTier.Auth)]
    public void MatchHighRiskTier_GivenAKeywordBesideDigitsOrWithAPluralS_Should_ReturnItsTier(string path, RiskTier expectedTier)
    {
        // Arrange — each path holds a keyword only once its digits are split off ("Auth0") or a plural "s" is dropped ("jwts", "logins")

        // Act
        var tier = RiskKeywords.MatchHighRiskTier(path);

        // Assert
        tier.Should().Be(expectedTier);
    }

    [Theory]
    [InlineData("src/Utf8JsonReader.cs")]
    [InlineData("src/Base64Encoder.cs")]
    public void MatchHighRiskTier_GivenWordsBesideDigitsThatAreNoKeywords_Should_ReturnNull(string path)
    {
        // Arrange — split off its digits, each word is still none of the keywords

        // Act
        var tier = RiskKeywords.MatchHighRiskTier(path);

        // Assert
        tier.Should().BeNull();
    }

    [Theory]
    [InlineData("src/Extensions/ServiceCollectionExtensions.cs")]
    [InlineData("src/Startup/IServiceCollectionExtensions.cs")]
    [InlineData("src/ViewModels/ObservableCollectionHelper.cs")]
    [InlineData("src/Hosting/service_collections.py")]
    public void MatchHighRiskTier_GivenAFrameworkTypeEndingInAKeyword_Should_ReturnNull(string path)
    {
        // Arrange — "collection" after "service" or "observable" is the .NET type, not debt collection

        // Act
        var tier = RiskKeywords.MatchHighRiskTier(path);

        // Assert
        tier.Should().BeNull();
    }

    [Theory]
    [InlineData("src/Collections/DebtCollectionService.cs")]
    [InlineData("src/CollectionAgency/Client.cs")]
    [InlineData("src/Services/CollectionsService.cs")]
    public void MatchHighRiskTier_GivenDebtCollectionCode_Should_ReturnPayments(string path)
    {
        // Arrange — "collection" on its own or after another word is still the payments keyword

        // Act
        var tier = RiskKeywords.MatchHighRiskTier(path);

        // Assert
        tier.Should().Be(RiskTier.Payments);
    }

    [Theory]
    [InlineData("src/Spanner.cs")]
    [InlineData("src/Tokenizer.cs")]
    [InlineData("src/Utils/StringHelpers.cs")]
    public void MatchHighRiskTier_GivenAWordThatOnlyContainsAKeyword_Should_ReturnNull(string path)
    {
        // Arrange — "Spanner" holds "pan" and "Tokenizer" holds "token", but neither is the word itself

        // Act
        var tier = RiskKeywords.MatchHighRiskTier(path);

        // Assert
        tier.Should().BeNull();
    }

    [Theory]
    [InlineData(RiskTier.Auth, "auth authentication authorization authorize authn authz login logon logout signin signup oauth oidc openid jwt token tokens identity permission permissions rbac sso saml password passwords credential credentials session sessions mfa otp totp security entra msal claims apikey secret secrets keyvault topsecret")]
    [InlineData(RiskTier.Payments, "payment payments pay payid payto payout payouts payee payer stripe refund refunds charge charges billing invoice invoices ledger transaction transactions disbursement disbursements disburse repayment repayments directdebit debit bpay npp settlement settlements remittance fee fees interest loan loans collection collections")]
    [InlineData(RiskTier.Cards, "card cards creditcard debitcard pan cvv cvc cardholder tokenisation tokenization threeds")]
    [InlineData(RiskTier.BankLogin, "bank banks banking openbanking bankstatement bankstatements basiq illion yodlee proviso creditsense cdr consent consents")]
    [InlineData(RiskTier.PersonalData, "customer customers pii personal person profile profiles kyc aml address addresses dob birth birthdate phone mobile email emails contact contacts applicant applicants borrower borrowers licence license passport medicare document documents verification privacy gdpr tfn employment income payslip payslips member members")]
    public void MatchHighRiskTier_GivenEachKeywordOfATier_Should_ReturnThatTier(RiskTier expectedTier, string keywords)
    {
        // Arrange — the keyword table of design 5.3, row by row; no word is in two rows
        var words = keywords.Split(' ');

        // Act
        var tiers = words.Select(RiskKeywords.MatchHighRiskTier).ToList();

        // Assert
        tiers.Should().OnlyContain(tier => tier == expectedTier);
    }
}
