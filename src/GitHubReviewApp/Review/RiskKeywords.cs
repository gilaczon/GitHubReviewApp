namespace GitHubReviewApp.Review;

/// <summary>
/// Finds the high-risk area a path or a repository name belongs to from the words in it (design 5.3). The text is split into lower-case words
/// at every character that is not a letter or digit and at camelCase boundaries, and a tier matches when one of those words is one of its
/// keywords. Only whole words count: "Spanner" is not "pan", and "Tokenizer" is not "token". The keyword lists are a starting set, tuned here
/// with tests.
/// </summary>
public static class RiskKeywords
{
    // In the order of RiskTier, so the first tier that matches is the highest risk one.
    private static readonly TierKeywords[] _highRiskTiers =
    [
        new(RiskTier.Auth,
        [
            "auth", "authentication", "authorization", "authorize", "authn", "authz", "login", "logon", "logout", "signin", "signup", "oauth",
            "oidc", "openid", "jwt", "token", "tokens", "identity", "permission", "permissions", "rbac", "sso", "saml", "password", "passwords",
            "credential", "credentials", "session", "sessions", "mfa", "otp", "totp", "security", "entra", "msal", "claims", "apikey", "secret",
            "secrets", "keyvault", "topsecret"
        ]),
        new(RiskTier.Payments,
        [
            "payment", "payments", "pay", "payid", "payto", "payout", "payouts", "payee", "payer", "stripe", "refund", "refunds", "charge",
            "charges", "billing", "invoice", "invoices", "ledger", "transaction", "transactions", "disbursement", "disbursements", "disburse",
            "repayment", "repayments", "directdebit", "debit", "bpay", "npp", "settlement", "settlements", "remittance", "fee", "fees", "interest",
            "loan", "loans", "collection", "collections"
        ]),
        new(RiskTier.Cards,
        [
            "card", "cards", "creditcard", "debitcard", "pan", "cvv", "cvc", "cardholder", "tokenisation", "tokenization", "threeds"
        ]),
        new(RiskTier.BankLogin,
        [
            "bank", "banks", "banking", "openbanking", "bankstatement", "bankstatements", "basiq", "illion", "yodlee", "proviso", "creditsense",
            "cdr", "consent", "consents"
        ]),
        new(RiskTier.PersonalData,
        [
            "customer", "customers", "pii", "personal", "person", "profile", "profiles", "kyc", "aml", "address", "addresses", "dob", "birth",
            "birthdate", "phone", "mobile", "email", "emails", "contact", "contacts", "applicant", "applicants", "borrower", "borrowers", "licence",
            "license", "passport", "medicare", "document", "documents", "verification", "privacy", "gdpr", "tfn", "employment", "income", "payslip",
            "payslips", "member", "members"
        ])
    ];

    private sealed record TierKeywords(RiskTier Tier, HashSet<string> Keywords);

    /// <summary>
    /// The words of <paramref name="text"/>, lower-cased: split at every character that is not a letter or digit, then where a lower-case letter
    /// is followed by an upper-case one ("openBanking"), and where an acronym runs into the next word, before that word's capital ("IDAlias" is
    /// "ID" and "Alias"). A digit is part of the word it is in ("v2").
    /// </summary>
    public static IReadOnlyList<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var wordStart = -1;

        for (var index = 0; index <= text.Length; index++)
        {
            var isInWord = index < text.Length && char.IsLetterOrDigit(text[index]);

            if (isInWord && wordStart < 0)
            {
                wordStart = index;
            }
            else if (!isInWord && wordStart >= 0)
            {
                AddWordParts(tokens, text[wordStart..index]);
                wordStart = -1;
            }
        }

        return tokens;
    }

    /// <summary>
    /// The first of auth, payments, cards, bank login and personal data, in that order, that has a keyword among the words of
    /// <paramref name="text"/>; null when none does.
    /// </summary>
    public static RiskTier? MatchHighRiskTier(string text)
    {
        var tokens = Tokenize(text);

        foreach (var tier in _highRiskTiers)
        {
            if (tokens.Any(tier.Keywords.Contains))
            {
                return tier.Tier;
            }
        }

        return null;
    }

    private static void AddWordParts(List<string> tokens, string word)
    {
        var partStart = 0;

        for (var index = 1; index < word.Length; index++)
        {
            if (IsPartBoundary(word, index))
            {
                tokens.Add(word[partStart..index].ToLowerInvariant());
                partStart = index;
            }
        }

        tokens.Add(word[partStart..].ToLowerInvariant());
    }

    private static bool IsPartBoundary(string word, int index)
    {
        var previous = word[index - 1];
        var current = word[index];

        var isLowerThenUpper = char.IsLower(previous) && char.IsUpper(current);
        var isWordAfterAcronym = char.IsUpper(previous) && char.IsUpper(current) && index + 1 < word.Length && char.IsLower(word[index + 1]);

        return isLowerThenUpper || isWordAfterAcronym;
    }
}
