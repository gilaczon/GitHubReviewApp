namespace GitHubReviewApp.Tests.Review;

/// <summary>
/// Opus reads the pull requests where a miss costs the most, and Sonnet the rest (design 5.4): Opus only when the change is over 40 lines and
/// it touches a high-risk area, whether through a changed file, the repository's name or the repository being listed as high risk.
/// </summary>
public class ModelSelectorTests
{
    private const string Owner = "my-org";
    private const string Repo = "my-service";
    private const string HeadSha = "0123456789abcdef0123456789abcdef01234567";
    private const string BaseSha = "fedcba9876543210fedcba9876543210fedcba98";
    private const string ListedOwner = "MoneyMeTeam";
    private const string ListedRepo = "Moneyme.Global.Library.VueApp";

    private static readonly RuntimeSettings _runtime = RuntimeSettings.Default;

    [Fact]
    public void Choose_GivenAHighRiskFileAndMoreThan40Lines_Should_ChooseOpus()
    {
        // Arrange — 41 lines, one over the threshold
        var sut = BuildSut();
        var input = InputOf("src/Auth/Login.cs");

        // Act
        var choice = sut.Choose(Owner, Repo, PullRequest(additions: 30, deletions: 11), input, _runtime);

        // Assert
        choice.Should().Be(new ModelChoice(_runtime.OpusModel, IsOpus: true, "high_risk"));
    }

    [Fact]
    public void Choose_GivenExactly40Lines_Should_ChooseSonnet()
    {
        // Arrange — the threshold is the most a Sonnet review may be, so 40 lines is not over it
        var sut = BuildSut();
        var input = InputOf("src/Auth/Login.cs");

        // Act
        var choice = sut.Choose(Owner, Repo, PullRequest(additions: 30, deletions: 10), input, _runtime);

        // Assert
        choice.Should().Be(new ModelChoice(_runtime.SonnetModel, IsOpus: false, "small_change"));
    }

    [Theory]
    [InlineData("tests/Auth/LoginTests.cs")]
    [InlineData("docs/payments.md")]
    [InlineData("Migrations/20240101_Init.Designer.cs")]
    [InlineData("package-lock.json")]
    public void Choose_GivenRiskOnlyInTestsDocsGeneratedOrUnreadableFiles_Should_ChooseSonnet(string path)
    {
        // Arrange — the path says auth or payments, but a test, a doc, generated code and a lockfile are not where the risk is
        var sut = BuildSut();
        var input = InputOf(path);

        // Act
        var choice = sut.Choose(Owner, Repo, PullRequest(additions: 400, deletions: 100), input, _runtime);

        // Assert
        choice.Should().Be(new ModelChoice(_runtime.SonnetModel, IsOpus: false, "low_risk"));
    }

    [Fact]
    public void Choose_GivenARepositoryNameWithARiskKeyword_Should_ChooseOpusForALargeChange()
    {
        // Arrange — nothing in the changed file says risk, only the repository does
        var sut = BuildSut();
        var input = InputOf("src/Util.cs");

        // Act
        var choice = sut.Choose(Owner, "MoneyMe.Payment.Api", PullRequest(additions: 400, deletions: 100), input, _runtime);

        // Assert
        choice.Should().Be(new ModelChoice(_runtime.OpusModel, IsOpus: true, "high_risk"));
    }

    [Theory]
    [InlineData("MoneyMeTeam", "Moneyme.Global.Library.VueApp")]
    [InlineData("moneymeteam", "MONEYME.GLOBAL.LIBRARY.VUEAPP")]
    public void Choose_GivenARepositoryListedAsHighRisk_Should_ChooseOpusForALargeChange(string owner, string repo)
    {
        // Arrange — the repository's name says neither bank login nor cards, so only the list can make this Opus
        var sut = BuildSut(new ReviewOptions { HighRiskRepositories = [$"{ListedOwner}/{ListedRepo}"] });
        var input = InputOf("src/Util.cs");

        // Act
        var choice = sut.Choose(owner, repo, PullRequest(additions: 400, deletions: 100), input, _runtime);

        // Assert
        choice.Should().Be(new ModelChoice(_runtime.OpusModel, IsOpus: true, "high_risk"));
    }

    [Theory]
    [InlineData("OtherOrg", "Moneyme.Global.Library.VueApp")]
    [InlineData("MoneyMeTeam", "Moneyme.Global.Library.Other")]
    public void Choose_GivenARepositoryThatIsNotTheListedOne_Should_ChooseSonnetForALargeChange(string owner, string repo)
    {
        // Arrange — an entry is an owner and a repository, matched whole: the same repository name under another owner is another repository
        var sut = BuildSut(new ReviewOptions { HighRiskRepositories = [$"{ListedOwner}/{ListedRepo}"] });
        var input = InputOf("src/Util.cs");

        // Act
        var choice = sut.Choose(owner, repo, PullRequest(additions: 400, deletions: 100), input, _runtime);

        // Assert
        choice.Should().Be(new ModelChoice(_runtime.SonnetModel, IsOpus: false, "low_risk"));
    }

    [Fact]
    public void Choose_GivenAPipelineChange_Should_ChooseOpus()
    {
        // Arrange — pipelines are the last tier that counts as high risk
        var sut = BuildSut();
        var input = InputOf(".github/workflows/deploy.yml");

        // Act
        var choice = sut.Choose(Owner, Repo, PullRequest(additions: 40, deletions: 10), input, _runtime);

        // Assert
        choice.Should().Be(new ModelChoice(_runtime.OpusModel, IsOpus: true, "high_risk"));
    }

    [Fact]
    public void Choose_GivenAConfigOnlyChange_Should_ChooseSonnet()
    {
        // Arrange — config is the tier just below pipelines, and does not count
        var sut = BuildSut();
        var input = InputOf("appsettings.json");

        // Act
        var choice = sut.Choose(Owner, Repo, PullRequest(additions: 150, deletions: 50), input, _runtime);

        // Assert
        choice.Should().Be(new ModelChoice(_runtime.SonnetModel, IsOpus: false, "low_risk"));
    }

    [Fact]
    public void Choose_GivenAThresholdSetInTheOptions_Should_UseItInPlaceOfForty()
    {
        // Arrange
        var sut = BuildSut(new ReviewOptions { OpusLineThreshold = 100 });
        var input = InputOf("src/Auth/Login.cs");

        // Act
        var atTheThreshold = sut.Choose(Owner, Repo, PullRequest(additions: 100, deletions: 0), input, _runtime);
        var overTheThreshold = sut.Choose(Owner, Repo, PullRequest(additions: 99, deletions: 2), input, _runtime);

        // Assert
        atTheThreshold.Reason.Should().Be("small_change");
        overTheThreshold.Reason.Should().Be("high_risk");
    }

    [Theory]
    [InlineData("claude-sonnet-custom", "claude-opus-custom")]
    [InlineData("claude-sonnet-4-6", "claude-opus-4-6")]
    public void Choose_GivenRuntimeModelIds_Should_ReturnThem(string sonnetModel, string opusModel)
    {
        // Arrange — the ids come from the settings an operator can change while the app runs, never from the code
        var sut = BuildSut();
        var runtime = new RuntimeSettings(IsReviewEnabled: true, sonnetModel, opusModel);
        var input = InputOf("src/Auth/Login.cs");

        // Act
        var opus = sut.Choose(Owner, Repo, PullRequest(additions: 30, deletions: 11), input, runtime);
        var sonnet = sut.Choose(Owner, Repo, PullRequest(additions: 1, deletions: 0), input, runtime);

        // Assert
        opus.Model.Should().Be(opusModel);
        sonnet.Model.Should().Be(sonnetModel);
    }

    private static ModelSelector BuildSut(ReviewOptions? options = null)
    {
        return new ModelSelector(Options.Create(options ?? new ReviewOptions()));
    }

    private static PullRequestInfo PullRequest(int additions, int deletions)
    {
        return new PullRequestInfo("open", false, "Add retries", HeadSha, BaseSha, 1, additions, deletions);
    }

    // One changed file, in the risk tier the classifier gives its path.
    private static ReviewInput InputOf(string path)
    {
        var file = new PullRequestFile(path, "modified", 1, 0, "@@ -1 +1 @@\n-a\n+b");
        var inputFile = new ReviewInputFile(file, DiffFileClassifier.GetRiskTier(file), DiffInclusion.Included);

        return new ReviewInput(string.Empty, [inputFile]);
    }
}
