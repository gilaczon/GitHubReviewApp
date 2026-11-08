namespace GitHubReviewApp.Tests.Review;

/// <summary>
/// The words of a path or repository name (design 5.3): split at every character that is not a letter or digit and at camelCase and acronym
/// boundaries, lower-cased; and the first high-risk tier, in enum order, that has one of those words as a keyword. Whole words only.
/// </summary>
public class RiskKeywordsTests
{
    [Theory]
    [InlineData("src/OpenBanking/BankStatementParser_v2.cs", "src open banking bank statement parser v2 cs")]
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
