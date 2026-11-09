namespace GitHubReviewApp.Tests.Configuration;

public class ReviewOptionsValidatorTests
{
    private static readonly ReviewOptionsValidator _sut = new();

    public static IEnumerable<object[]> SettingsThatCannotWork()
    {
        yield return [new ReviewOptions { MaxFileBytes = 0 }, "Review:MaxFileBytes"];
        yield return [new ReviewOptions { MaxFileBytes = 1_999 }, "Review:MaxFileBytes"];
        yield return [new ReviewOptions { MaxConcurrentModelRequests = 0 }, "Review:MaxConcurrentModelRequests"];
        yield return [new ReviewOptions { AllowedAuthorAssociations = ["MEMBERS"] }, "Review:AllowedAuthorAssociations"];

        yield return [new ReviewOptions { MaxReviewDurationSeconds = 0 }, "Review:MaxReviewDurationSeconds"];
        yield return [new ReviewOptions { MaxReviewDurationSeconds = 86_401 }, "Review:MaxReviewDurationSeconds"];

        yield return [new ReviewOptions { MaxListedFiles = 0 }, "Review:MaxListedFiles"];
        yield return [new ReviewOptions { MaxDescriptionChars = 0 }, "Review:MaxDescriptionChars"];

        yield return [new ReviewOptions { OpusLineThreshold = -1 }, "Review:OpusLineThreshold"];

        // Never matched: a branch name holds no space, so a blank entry or one with a space inside reviews nothing it was meant to
        yield return [new ReviewOptions { TargetBranches = [""] }, "Review:TargetBranches"];
        yield return [new ReviewOptions { TargetBranches = ["  "] }, "Review:TargetBranches"];
        yield return [new ReviewOptions { TargetBranches = ["main master"] }, "Review:TargetBranches"];

        // Never matched: an entry is an owner and a repository, so a blank or half-written one would leave a risky repository on Sonnet
        yield return [new ReviewOptions { HighRiskRepositories = [""] }, "Review:HighRiskRepositories"];
        yield return [new ReviewOptions { HighRiskRepositories = ["  "] }, "Review:HighRiskRepositories"];
        yield return [new ReviewOptions { HighRiskRepositories = ["MoneyMeTeam"] }, "Review:HighRiskRepositories"];
        yield return [new ReviewOptions { HighRiskRepositories = ["MoneyMeTeam/"] }, "Review:HighRiskRepositories"];
        yield return [new ReviewOptions { HighRiskRepositories = ["/Moneyme.Global.Library.VueApp"] }, "Review:HighRiskRepositories"];
        yield return [new ReviewOptions { HighRiskRepositories = ["MoneyMeTeam/Moneyme/Extra"] }, "Review:HighRiskRepositories"];
        yield return [new ReviewOptions { HighRiskRepositories = [" MoneyMeTeam/Moneyme.Global.Library.VueApp"] }, "Review:HighRiskRepositories"];
        yield return [new ReviewOptions { HighRiskRepositories = ["MoneyMeTeam /Moneyme.Global.Library.VueApp"] }, "Review:HighRiskRepositories"];
        yield return [new ReviewOptions { HighRiskRepositories = ["MoneyMeTeam/Moneyme.Global.Library.VueApp\t"] }, "Review:HighRiskRepositories"];
    }

    [Fact]
    public void Validate_GivenTheSmallestAndLargestSettingsThatWork_Should_Succeed()
    {
        // Arrange
        var options = new ReviewOptions
        {
            MaxFileBytes = 10_000,
            MaxDiffBytes = 10_000,
            MaxListedFiles = 1,
            MaxDescriptionChars = 1,
            MaxConcurrentModelRequests = 1,
            MaxReviewDurationSeconds = 86_400
        };

        // Act
        var result = _sut.Validate(Options.DefaultName, options);

        // Assert
        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_GivenAZeroThresholdAndWellFormedListEntries_Should_Succeed()
    {
        // Arrange — a threshold of zero sends every change that touches a risky area to Opus
        var options = new ReviewOptions
        {
            OpusLineThreshold = 0,
            HighRiskRepositories = ["MoneyMeTeam/Moneyme.Global.Library.VueApp", "my-org/my-service"],
            TargetBranches = ["main", " qa ", "release/2026.11"]
        };

        // Act
        var result = _sut.Validate(Options.DefaultName, options);

        // Assert
        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_GivenAMalformedHighRiskRepository_Should_FailNamingTheEntry()
    {
        // Arrange
        var options = new ReviewOptions { HighRiskRepositories = ["MoneyMeTeam/Moneyme.Global.Library.VueApp", "NoSlashHere"] };

        // Act
        var result = _sut.Validate(Options.DefaultName, options);

        // Assert
        result.Failures.Should().ContainSingle().Which.Should().Contain("'NoSlashHere'", "the message names the entry that is wrong");
    }

    [Fact]
    public void Validate_GivenAReviewLimitShorterThanOneModelRequestMayTake_Should_FailNamingBothSettings()
    {
        // Arrange — a request that is allowed 15 minutes can never finish inside a review that is cut off after 14
        var sut = new ReviewOptionsValidator(claudeOptions: Options.Create(new ClaudeOptions { RequestTimeoutSeconds = 900 }));
        var options = new ReviewOptions { MaxReviewDurationSeconds = 899 };

        // Act
        var result = sut.Validate(Options.DefaultName, options);

        // Assert
        result.Failed.Should().BeTrue();
        result.Failures.Should().ContainSingle().Which.Should().Contain("Review:MaxReviewDurationSeconds").And.Contain("Claude:RequestTimeoutSeconds");
    }

    [Fact]
    public void Validate_GivenAFileLimitAboveTheDiffBudget_Should_FailNamingBothSettings()
    {
        // Arrange — a file cut at 60,000 bytes could be larger than the whole diff, and be left out where a smaller cut would have fitted
        var options = new ReviewOptions { MaxFileBytes = 60_000, MaxDiffBytes = 59_999 };

        // Act
        var result = _sut.Validate(Options.DefaultName, options);

        // Assert
        result.Failures.Should().ContainSingle().Which.Should().Contain("Review:MaxFileBytes").And.Contain("Review:MaxDiffBytes");
    }

    [Fact]
    public void Validate_GivenAFileLimitUnderTenThousandBytes_Should_FailNamingTheSetting()
    {
        // Arrange — a cut slice under 2,000 bytes is left out as a line too long to show, which is true only when the limit leaves room for
        // many lines: just above 2,000, almost every file that needs a cut would be left out that way, though its lines are short
        var options = new ReviewOptions { MaxFileBytes = 9_999 };

        // Act
        var result = _sut.Validate(Options.DefaultName, options);

        // Assert
        result.Failed.Should().BeTrue();
        result.Failures.Should().ContainSingle().Which.Should().Be("Review:MaxFileBytes must be at least 10000, but it is 9999.");
    }

    [Fact]
    public void Validate_GivenAFileLimitOfTenThousandBytes_Should_Succeed()
    {
        // Arrange
        var options = new ReviewOptions { MaxFileBytes = 10_000 };

        // Act
        var result = _sut.Validate(Options.DefaultName, options);

        // Assert
        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_GivenNoDiffBudget_Should_FailSayingItMustBeAtLeastOne()
    {
        // Arrange
        var options = new ReviewOptions { MaxDiffBytes = 0 };

        // Act
        var result = _sut.Validate(Options.DefaultName, options);

        // Assert — the file limit, now above the budget, is reported as well
        result.Failures.Should().Contain("Review:MaxDiffBytes must be at least 1, but it is 0.");
    }

    [Fact]
    public void Validate_GivenAReviewLimitExactlyAsLongAsOneModelRequest_Should_Succeed()
    {
        // Arrange
        var sut = new ReviewOptionsValidator(claudeOptions: Options.Create(new ClaudeOptions { RequestTimeoutSeconds = 900 }));
        var options = new ReviewOptions { MaxReviewDurationSeconds = 900 };

        // Act
        var result = sut.Validate(Options.DefaultName, options);

        // Assert
        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_GivenTheDefaultOptionsOfBothSections_Should_Succeed()
    {
        // Arrange — the two defaults must agree with each other, or a fresh deployment fails to start
        var sut = new ReviewOptionsValidator(claudeOptions: Options.Create(new ClaudeOptions()));

        // Act
        var result = sut.Validate(Options.DefaultName, new ReviewOptions());

        // Assert
        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("AllowedAuthorAssociations", "OWNER,MEMBER")]
    [InlineData("HighRiskRepositories", "a/b,c/d")]
    [InlineData("TargetBranches", "main,qa")]
    public void Validate_GivenAListSettingWrittenAsOneValue_Should_FailBecauseTheBinderIgnoresItAndFallsBackToTheDefault(string setting, string value)
    {
        // Arrange — "Review__AllowedAuthorAssociations=OWNER,MEMBER" binds to nothing, which leaves the list empty and lets every author trigger a review
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [$"Review:{setting}"] = value })
            .Build();
        var sut = new ReviewOptionsValidator(configuration);

        // Act
        var result = sut.Validate(Options.DefaultName, new ReviewOptions());

        // Assert
        result.Failed.Should().BeTrue();
        result.Failures.Should().ContainSingle().Which.Should().Contain($"Review:{setting}").And.Contain("list");
    }

    [Fact]
    public void Validate_GivenASeededListAlsoWrittenAsOneValue_Should_Fail()
    {
        // Arrange — appsettings.json seeds an entry, and "Review__HighRiskRepositories=a/b,c/d" in the app settings is one value: the binder keeps
        // the seeded entry and drops the value without a word, so both listed repositories would stay on Sonnet
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Review:HighRiskRepositories:0"] = "MoneyMeTeam/Moneyme.Global.Library.VueApp",
                ["Review:HighRiskRepositories"] = "a/b,c/d"
            })
            .Build();
        var options = configuration.GetSection(ReviewOptions.SectionName).Get<ReviewOptions>()!;
        var sut = new ReviewOptionsValidator(configuration);

        // Act
        var result = sut.Validate(Options.DefaultName, options);

        // Assert — the list keeps its entry, so the message does not say it falls back to its default
        result.Failures.Should().ContainSingle().Which.Should().Contain("Review:HighRiskRepositories").And.NotContain("default");
    }

    [Fact]
    public void Validate_GivenAListSettingThatIsLeftEmpty_Should_Succeed()
    {
        // Arrange — an empty value is how an operator clears the list, which is the default
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Review:AllowedAuthorAssociations"] = string.Empty })
            .Build();
        var sut = new ReviewOptionsValidator(configuration);

        // Act
        var result = sut.Validate(Options.DefaultName, new ReviewOptions());

        // Assert
        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_GivenTheListSettingsWrittenAsLists_Should_Succeed()
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Review:AllowedAuthorAssociations:0"] = "OWNER",
                ["Review:AllowedAuthorAssociations:1"] = "MEMBER",
                ["Review:HighRiskRepositories:0"] = "my-org/my-service"
            })
            .Build();
        var options = configuration.GetSection(ReviewOptions.SectionName).Get<ReviewOptions>()!;
        var sut = new ReviewOptionsValidator(configuration);

        // Act
        var result = sut.Validate(Options.DefaultName, options);

        // Assert
        result.Succeeded.Should().BeTrue();
        options.AllowedAuthorAssociations.Should().Equal("OWNER", "MEMBER");
    }

    [Fact]
    public void Validate_GivenTheDefaultOptions_Should_Succeed()
    {
        // Arrange

        // Act
        var result = _sut.Validate(Options.DefaultName, new ReviewOptions());

        // Assert
        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(SettingsThatCannotWork))]
    public void Validate_GivenASettingThatCannotWork_Should_FailNamingTheSetting(ReviewOptions options, string expectedSetting)
    {
        // Arrange

        // Act
        var result = _sut.Validate(Options.DefaultName, options);

        // Assert
        result.Failed.Should().BeTrue();
        result.Failures.Should().ContainSingle().Which.Should().Contain(expectedSetting);
    }

    [Theory]
    [InlineData("OWNER")]
    [InlineData("member")]
    [InlineData("Collaborator")]
    [InlineData("CONTRIBUTOR")]
    [InlineData("FIRST_TIME_CONTRIBUTOR")]
    [InlineData("FIRST_TIMER")]
    [InlineData("MANNEQUIN")]
    [InlineData("NONE")]
    public void Validate_GivenAnAuthorAssociationGitHubUses_Should_Succeed(string association)
    {
        // Arrange

        // Act
        var result = _sut.Validate(Options.DefaultName, new ReviewOptions { AllowedAuthorAssociations = [association] });

        // Assert
        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_GivenAMisspeltAuthorAssociation_Should_FailBecauseItWouldSilentlyLockEveryoneOut()
    {
        // Arrange — "MEMBERS" matches no pull request, so every review would be skipped and nothing would say why
        var options = new ReviewOptions { AllowedAuthorAssociations = ["OWNER", "MEMBERS"] };

        // Act
        var result = _sut.Validate(Options.DefaultName, options);

        // Assert
        result.Failures.Should().ContainSingle().Which.Should().Contain("'MEMBERS'", "the message names the value that is wrong");
    }

    [Fact]
    public void Validate_GivenSeveralBadSettings_Should_ReportEveryOneSoTheyCanBeFixedInOneGo()
    {
        // Arrange
        var options = new ReviewOptions { MaxFileBytes = 0, MaxConcurrentModelRequests = 0, MaxListedFiles = 0 };

        // Act
        var result = _sut.Validate(Options.DefaultName, options);

        // Assert
        result.Failures.Should().HaveCount(3);
    }

    [Fact]
    public void KnownAuthorAssociations_GivenGitHubsDocumentedValues_Should_ListThemAll()
    {
        // Arrange

        // Act
        var associations = ReviewOptions.KnownAuthorAssociations;

        // Assert
        associations.Should().BeEquivalentTo(
            "COLLABORATOR", "CONTRIBUTOR", "FIRST_TIMER", "FIRST_TIME_CONTRIBUTOR", "MANNEQUIN", "MEMBER", "NONE", "OWNER");
    }
}
