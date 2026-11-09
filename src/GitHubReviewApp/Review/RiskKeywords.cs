namespace GitHubReviewApp.Review;

/// <summary>
/// Finds the high-risk area a path or a repository name belongs to from the words in it (design 5.3). The text is split into lower-case words
/// at every character that is not a letter or digit, at camelCase boundaries and where letters and digits meet, and a tier matches when one of
/// those words is one of its keywords, or is one with a plural "s" added. Only whole words count: "Spanner" is not "pan", and "Tokenizer" is
/// not "token". A keyword right after a word that makes it a framework type is not a match: "ServiceCollection" (the .NET service container)
/// is not debt collection. The keyword lists are a starting set, tuned here with tests.
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

    // Framework types whose name ends in a keyword: the word before the keyword, for each keyword it changes. "IServiceCollection" and
    // "ObservableCollection" are everywhere in .NET code and say nothing about money; "DebtCollection" and "CollectionAgency" still match.
    private static readonly Dictionary<string, HashSet<string>> _frameworkWordsBefore = new()
    {
        ["collection"] = ["service", "observable", "concurrent", "blocking", "only", "keyed", "value", "key", "endpoint"]
    };

    private sealed record TierKeywords(RiskTier Tier, HashSet<string> Keywords);

    /// <summary>
    /// The words of <paramref name="text"/>, lower-cased: split at every character that is not a letter or digit, then where a lower-case letter
    /// is followed by an upper-case one ("openBanking"), where an acronym runs into the next word, before that word's capital ("IDAlias" is
    /// "ID" and "Alias"), and where a letter and a digit meet, so a run of digits is a word of its own ("Pbkdf2Hasher" is "Pbkdf", "2" and
    /// "Hasher"). An acronym keeps a plural "s" that ends the word or is followed by another word or a number ("JWTsValidator" is "JWTs" and
    /// "Validator").
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
    /// <paramref name="text"/>; null when none does. A word that ends in "s" also counts when it is a keyword without that one "s" ("jwts" is
    /// "jwt", "logins" is "login").
    /// </summary>
    public static RiskTier? MatchHighRiskTier(string text)
    {
        var tokens = Tokenize(text);

        foreach (var tier in _highRiskTiers)
        {
            for (var index = 0; index < tokens.Count; index++)
            {
                if (IsKeywordOrItsPlural(tier.Keywords, tokens[index]) && !IsFrameworkTypeName(tokens, index))
                {
                    return tier.Tier;
                }
            }
        }

        return null;
    }

    private static bool IsFrameworkTypeName(IReadOnlyList<string> tokens, int index)
    {
        var token = tokens[index];
        var keyword = token.EndsWith('s') && _frameworkWordsBefore.ContainsKey(token[..^1]) ? token[..^1] : token;

        return index > 0 && _frameworkWordsBefore.TryGetValue(keyword, out var wordsBefore) && wordsBefore.Contains(tokens[index - 1]);
    }

    private static bool IsKeywordOrItsPlural(HashSet<string> keywords, string token)
    {
        return keywords.Contains(token) || (token.EndsWith('s') && keywords.Contains(token[..^1]));
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
        var isLetterDigitBoundary = char.IsDigit(previous) != char.IsDigit(current);
        var isWordAfterAcronym = char.IsUpper(previous) && char.IsUpper(current) && index + 1 < word.Length && char.IsLower(word[index + 1])
            && !IsPluralOfAcronym(word, index + 1);

        return isLowerThenUpper || isLetterDigitBoundary || isWordAfterAcronym;
    }

    // The "s" of "JWTs" and "JWTsValidator": a lone "s" after an acronym that ends the word or is followed by another word or a number.
    private static bool IsPluralOfAcronym(string word, int index)
    {
        var isEndOfWord = index + 1 == word.Length;

        return word[index] == 's' && (isEndOfWord || char.IsUpper(word[index + 1]) || char.IsDigit(word[index + 1]));
    }
}
