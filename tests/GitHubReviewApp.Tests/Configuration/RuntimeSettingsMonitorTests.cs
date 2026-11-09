namespace GitHubReviewApp.Tests.Configuration;

public class RuntimeSettingsMonitorTests
{
    private const string ValidSonnetModel = "claude-sonnet-9-9";
    private const string ValidOpusModel = "claude-opus-9-9";

    /// <summary>A configuration whose values a test can change and then reload, as App Configuration's refresh does.</summary>
    private sealed class ReloadableConfiguration
    {
        private readonly MemoryConfigurationProvider _provider;

        public ReloadableConfiguration(Dictionary<string, string?> initialValues)
        {
            Root = new ConfigurationBuilder()
                .Add(new MemoryConfigurationSource { InitialData = initialValues })
                .Build();
            _provider = Root.Providers.OfType<MemoryConfigurationProvider>().Single();
        }

        public IConfigurationRoot Root { get; }

        public void Change(string key, string? value)
        {
            _provider.Set(key, value);
        }
    }

    private sealed class FailingProvider : ConfigurationProvider
    {
        public bool IsFailing { get; set; }

        public override bool TryGet(string key, out string? value)
        {
            return IsFailing ? throw new InvalidOperationException("the provider failed") : base.TryGet(key, out value);
        }
    }

    private sealed class FailingSource(FailingProvider provider) : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder)
        {
            return provider;
        }
    }

    private static ReloadableConfiguration ConfigurationWith(params (string Key, string? Value)[] values)
    {
        return new ReloadableConfiguration(values.ToDictionary(pair => pair.Key, pair => pair.Value));
    }

    private static ReloadableConfiguration ValidConfiguration()
    {
        return ConfigurationWith(
            ("Runtime:IsReviewEnabled", "true"),
            ("Runtime:SonnetModel", ValidSonnetModel),
            ("Runtime:OpusModel", ValidOpusModel));
    }

    [Fact]
    public void Current_GivenNoRuntimeSection_Should_UseTheDefaults()
    {
        // Arrange
        var configuration = ConfigurationWith();

        // Act
        using var sut = new RuntimeSettingsMonitor(configuration.Root, NullLogger<RuntimeSettingsMonitor>.Instance);

        // Assert
        sut.Current.Should().Be(RuntimeSettings.Default);
    }

    [Fact]
    public void Current_GivenValidValues_Should_ReadThem()
    {
        // Arrange
        var configuration = ConfigurationWith(
            ("Runtime:IsReviewEnabled", "FALSE"),
            ("Runtime:SonnetModel", ValidSonnetModel),
            ("Runtime:OpusModel", ValidOpusModel));

        // Act
        using var sut = new RuntimeSettingsMonitor(configuration.Root, NullLogger<RuntimeSettingsMonitor>.Instance);

        // Assert
        sut.Current.Should().Be(new RuntimeSettings(false, ValidSonnetModel, ValidOpusModel));
    }

    [Fact]
    public void Current_GivenOnlyOneKey_Should_TakeTheDefaultForTheOthers()
    {
        // Arrange
        var configuration = ConfigurationWith(("Runtime:OpusModel", ValidOpusModel));

        // Act
        using var sut = new RuntimeSettingsMonitor(configuration.Root, NullLogger<RuntimeSettingsMonitor>.Instance);

        // Assert
        sut.Current.Should().Be(RuntimeSettings.Default with { OpusModel = ValidOpusModel });
    }

    [Fact]
    public void Current_GivenAReloadWithValidValues_Should_PublishTheNewSnapshot()
    {
        // Arrange
        var configuration = ValidConfiguration();
        using var sut = new RuntimeSettingsMonitor(configuration.Root, NullLogger<RuntimeSettingsMonitor>.Instance);
        var before = sut.Current;
        configuration.Change("Runtime:IsReviewEnabled", "false");
        configuration.Change("Runtime:SonnetModel", "claude-sonnet-10");

        // Act
        configuration.Root.Reload();

        // Assert
        before.Should().Be(new RuntimeSettings(true, ValidSonnetModel, ValidOpusModel));
        sut.Current.Should().Be(new RuntimeSettings(false, "claude-sonnet-10", ValidOpusModel));
    }

    public static IEnumerable<object[]> InvalidModelIds()
    {
        yield return ["gpt-4"];
        yield return ["claude sonnet"];
        yield return [""];
        yield return ["claude-" + new string('a', 94)];
        yield return ["Claude-Sonnet"];
        yield return ["claude-sonnet\n"];
    }

    [Theory]
    [MemberData(nameof(InvalidModelIds))]
    public void Current_GivenAReloadWithAnInvalidModelId_Should_KeepTheLastGoodModelIdApplyTheOtherValuesAndWarn(string invalidModelId)
    {
        // Arrange
        var configuration = ValidConfiguration();
        var logger = new ListLogger<RuntimeSettingsMonitor>();
        using var sut = new RuntimeSettingsMonitor(configuration.Root, logger);
        var lastGood = sut.Current;
        configuration.Change("Runtime:IsReviewEnabled", "false");
        configuration.Change("Runtime:SonnetModel", invalidModelId);

        // Act
        configuration.Root.Reload();

        // Assert
        sut.Current.Should().Be(lastGood with { IsReviewEnabled = false });
        var warning = logger.Entries.Should().ContainSingle().Subject;
        warning.Level.Should().Be(LogLevel.Warning);
        warning.Message.Should().Contain("Runtime:SonnetModel");
    }

    [Fact]
    public void Current_GivenAnInvalidValue_Should_NeverWriteTheValueToTheLog()
    {
        // Arrange
        const string invalidValue = "gpt-the-value-that-must-not-be-logged";
        var configuration = ValidConfiguration();
        var logger = new ListLogger<RuntimeSettingsMonitor>();
        using var sut = new RuntimeSettingsMonitor(configuration.Root, logger);
        configuration.Change("Runtime:SonnetModel", invalidValue);

        // Act
        configuration.Root.Reload();

        // Assert
        logger.Entries.Should().ContainSingle().Which.Message.Should().NotContain(invalidValue);
    }

    [Fact]
    public void Current_GivenAReloadWithAnInvalidOpusModelId_Should_NameTheOpusKey()
    {
        // Arrange
        var configuration = ValidConfiguration();
        var logger = new ListLogger<RuntimeSettingsMonitor>();
        using var sut = new RuntimeSettingsMonitor(configuration.Root, logger);
        configuration.Change("Runtime:OpusModel", "gpt-4");

        // Act
        configuration.Root.Reload();

        // Assert
        logger.Entries.Should().ContainSingle().Which.Message.Should().Contain("Runtime:OpusModel");
    }

    [Fact]
    public void Current_GivenAReloadWithASwitchThatIsNotABoolean_Should_KeepTheLastGoodSwitchApplyTheModelIdsAndWarn()
    {
        // Arrange
        const string notABoolean = "maybe-later";
        var configuration = ValidConfiguration();
        var logger = new ListLogger<RuntimeSettingsMonitor>();
        using var sut = new RuntimeSettingsMonitor(configuration.Root, logger);
        var lastGood = sut.Current;
        configuration.Change("Runtime:IsReviewEnabled", notABoolean);
        configuration.Change("Runtime:OpusModel", "claude-opus-10");

        // Act
        configuration.Root.Reload();

        // Assert
        sut.Current.Should().Be(lastGood with { OpusModel = "claude-opus-10" });
        var warning = logger.Entries.Should().ContainSingle().Subject;
        warning.Level.Should().Be(LogLevel.Warning);
        warning.Message.Should().Contain("Runtime:IsReviewEnabled").And.NotContain(notABoolean);
    }

    [Fact]
    public void Current_GivenAReloadAfterAnInvalidOne_Should_PublishTheValidValuesAgain()
    {
        // Arrange
        var configuration = ValidConfiguration();
        using var sut = new RuntimeSettingsMonitor(configuration.Root, NullLogger<RuntimeSettingsMonitor>.Instance);
        configuration.Change("Runtime:SonnetModel", "gpt-4");
        configuration.Root.Reload();
        configuration.Change("Runtime:SonnetModel", "claude-sonnet-10");

        // Act
        configuration.Root.Reload();

        // Assert
        sut.Current.SonnetModel.Should().Be("claude-sonnet-10");
    }

    [Fact]
    public void Current_GivenAnInvalidModelIdAtStartup_Should_UseThatKeysDefaultApplyTheOtherValuesAndWarn()
    {
        // Arrange
        var configuration = ConfigurationWith(
            ("Runtime:IsReviewEnabled", "false"),
            ("Runtime:SonnetModel", "gpt-4"),
            ("Runtime:OpusModel", ValidOpusModel));
        var logger = new ListLogger<RuntimeSettingsMonitor>();

        // Act
        using var sut = new RuntimeSettingsMonitor(configuration.Root, logger);

        // Assert
        sut.Current.Should().Be(new RuntimeSettings(false, RuntimeSettings.Default.SonnetModel, ValidOpusModel));
        logger.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
    }

    [Fact]
    public void Current_GivenTheSwitchOffAndAnInvalidModelIdAtStartup_Should_SwitchReviewsOff()
    {
        // Arrange
        var configuration = ConfigurationWith(
            ("Runtime:IsReviewEnabled", "false"),
            ("Runtime:SonnetModel", "gpt-4"));

        // Act
        using var sut = new RuntimeSettingsMonitor(configuration.Root, NullLogger<RuntimeSettingsMonitor>.Instance);

        // Assert
        sut.Current.IsReviewEnabled.Should().BeFalse();
    }

    [Fact]
    public void Current_GivenAStaleInvalidModelIdAndTheSwitchTurnedOff_Should_SwitchReviewsOff()
    {
        // Arrange
        var configuration = ValidConfiguration();
        using var sut = new RuntimeSettingsMonitor(configuration.Root, NullLogger<RuntimeSettingsMonitor>.Instance);
        configuration.Change("Runtime:OpusModel", "gpt-4");
        configuration.Root.Reload();
        configuration.Change("Runtime:IsReviewEnabled", "false");

        // Act
        configuration.Root.Reload();

        // Assert
        sut.Current.Should().Be(new RuntimeSettings(false, ValidSonnetModel, ValidOpusModel));
    }

    [Fact]
    public void Current_GivenAnInvalidSwitchAndAValidModelIdAtStartup_Should_KeepTheSwitchDefaultAndApplyTheModelId()
    {
        // Arrange
        var configuration = ConfigurationWith(
            ("Runtime:IsReviewEnabled", "maybe-later"),
            ("Runtime:SonnetModel", ValidSonnetModel));

        // Act
        using var sut = new RuntimeSettingsMonitor(configuration.Root, NullLogger<RuntimeSettingsMonitor>.Instance);

        // Assert
        sut.Current.Should().Be(RuntimeSettings.Default with { SonnetModel = ValidSonnetModel });
    }

    [Fact]
    public void Current_GivenAReloadWithEveryValueInvalid_Should_KeepTheSameSnapshotAndWarnForEachKey()
    {
        // Arrange
        var configuration = ValidConfiguration();
        var logger = new ListLogger<RuntimeSettingsMonitor>();
        using var sut = new RuntimeSettingsMonitor(configuration.Root, logger);
        var lastGood = sut.Current;
        configuration.Change("Runtime:IsReviewEnabled", "maybe-later");
        configuration.Change("Runtime:SonnetModel", "gpt-4");
        configuration.Change("Runtime:OpusModel", "gpt-4");

        // Act
        configuration.Root.Reload();

        // Assert
        sut.Current.Should().BeSameAs(lastGood);
        logger.Entries.Should().HaveCount(3);
    }

    [Fact]
    public void Reload_GivenAnInvalidValue_Should_NotThrowFromTheChangeCallback()
    {
        // Arrange
        var configuration = ValidConfiguration();
        using var sut = new RuntimeSettingsMonitor(configuration.Root, NullLogger<RuntimeSettingsMonitor>.Instance);
        configuration.Change("Runtime:SonnetModel", "gpt-4");

        // Act & Assert
        configuration.Root.Invoking(root => root.Reload()).Should().NotThrow();
    }

    [Fact]
    public void Reload_GivenAConfigurationThatThrowsWhenRead_Should_NotThrowAndKeepTheLastGoodSnapshot()
    {
        // Arrange
        var provider = new FailingProvider();
        var root = new ConfigurationBuilder().Add(new FailingSource(provider)).Build();
        var logger = new ListLogger<RuntimeSettingsMonitor>();
        using var sut = new RuntimeSettingsMonitor(root, logger);
        provider.IsFailing = true;

        // Act & Assert
        root.Invoking(configurationRoot => configurationRoot.Reload()).Should().NotThrow();
        sut.Current.Should().Be(RuntimeSettings.Default);
        logger.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
    }

    [Fact]
    public void Current_GivenADisposedMonitor_Should_StopFollowingReloads()
    {
        // Arrange
        var configuration = ValidConfiguration();
        var sut = new RuntimeSettingsMonitor(configuration.Root, NullLogger<RuntimeSettingsMonitor>.Instance);
        sut.Dispose();
        configuration.Change("Runtime:SonnetModel", "claude-sonnet-10");

        // Act
        configuration.Root.Reload();

        // Assert
        sut.Current.SonnetModel.Should().Be(ValidSonnetModel);
    }
}
