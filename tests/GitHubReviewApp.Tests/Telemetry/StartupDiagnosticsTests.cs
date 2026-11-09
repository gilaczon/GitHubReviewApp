namespace GitHubReviewApp.Tests.Telemetry;

public class StartupDiagnosticsTests
{
    private const string OpaqueDsn = "opaque-dsn-value-that-must-never-be-logged";

    private static IConfiguration ConfigurationWith(string? dsn)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["UptraceDsn"] = dsn })
            .Build();
    }

    private static IHostEnvironment EnvironmentNamed(string name)
    {
        var environment = new Mock<IHostEnvironment>();
        environment.Setup(e => e.EnvironmentName).Returns(name);

        return environment.Object;
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("")]
    public void LogAuthorAllowList_GivenNoAllowListOutsideDevelopment_Should_WarnThatEveryAuthorCanTriggerAReview(string environmentName)
    {
        // Arrange — right for a private repository, but on a public one anyone can open a pull request and spend model tokens
        var logger = new ListLogger<StartupDiagnosticsTests>();

        // Act
        StartupDiagnostics.LogAuthorAllowList(logger, new ReviewOptions(), EnvironmentNamed(environmentName));

        // Assert
        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Message.Should().Contain("AllowedAuthorAssociations").And.Contain("every");
    }

    [Fact]
    public void LogAuthorAllowList_GivenNoAllowListInDevelopment_Should_LogItAsInformation()
    {
        // Arrange
        var logger = new ListLogger<StartupDiagnosticsTests>();

        // Act
        StartupDiagnostics.LogAuthorAllowList(logger, new ReviewOptions(), EnvironmentNamed("Development"));

        // Assert
        logger.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Information);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public void LogAuthorAllowList_GivenAnAllowList_Should_NameTheAssociationsThatMayTriggerAReview(string environmentName)
    {
        // Arrange
        var logger = new ListLogger<StartupDiagnosticsTests>();
        var options = new ReviewOptions { AllowedAuthorAssociations = ["member", "OWNER"] };

        // Act
        StartupDiagnostics.LogAuthorAllowList(logger, options, EnvironmentNamed(environmentName));

        // Assert
        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Information);
        entry.Message.Should().Contain("MEMBER, OWNER");
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public void LogTelemetryState_GivenADsn_Should_SayTelemetryIsOnWithoutQuotingTheDsn(string environmentName)
    {
        // Arrange
        var logger = new ListLogger<StartupDiagnosticsTests>();

        // Act
        StartupDiagnostics.LogTelemetryState(logger, ConfigurationWith(OpaqueDsn), EnvironmentNamed(environmentName));

        // Assert
        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Information);
        entry.Message.Should().Contain("telemetry is on").And.NotContain(OpaqueDsn);
    }

    [Fact]
    public void LogTelemetryState_GivenNoDsnInDevelopment_Should_LogItAsExpectedNotAsAWarning()
    {
        // Arrange
        var logger = new ListLogger<StartupDiagnosticsTests>();

        // Act
        StartupDiagnostics.LogTelemetryState(logger, ConfigurationWith(null), EnvironmentNamed("Development"));

        // Assert
        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Information);
        entry.Message.Should().Contain("Development");
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("QA")]
    [InlineData("")]
    public void LogTelemetryState_GivenNoDsnOutsideDevelopment_Should_WarnSoNobodyLearnsOfItDuringAnIncident(string environmentName)
    {
        // Arrange
        var logger = new ListLogger<StartupDiagnosticsTests>();

        // Act
        StartupDiagnostics.LogTelemetryState(logger, ConfigurationWith(null), EnvironmentNamed(environmentName));

        // Assert
        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Message.Should().Contain("UptraceDsn").And.Contain("no traces");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void LogTelemetryState_GivenABlankDsn_Should_CountItAsMissing(string blank)
    {
        // Arrange
        var logger = new ListLogger<StartupDiagnosticsTests>();

        // Act
        StartupDiagnostics.LogTelemetryState(logger, ConfigurationWith(blank), EnvironmentNamed("Production"));

        // Assert
        logger.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
    }

    [Fact]
    public void LogRuntimeSettingsSource_GivenNoAppConfiguration_Should_SayTheAppSettingsValuesApply()
    {
        // Arrange
        var logger = new ListLogger<StartupDiagnosticsTests>();

        // Act
        StartupDiagnostics.LogRuntimeSettingsSource(logger, isAppConfigurationAdded: false);

        // Assert
        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Information);
        entry.Message.Should().Contain("AppConfigurationConnectionString").And.Contain("appsettings");
    }

    [Fact]
    public void LogRuntimeSettingsSource_GivenAppConfiguration_Should_SayTheStoreOverridesTheAppSettingsValues()
    {
        // Arrange
        var logger = new ListLogger<StartupDiagnosticsTests>();

        // Act
        StartupDiagnostics.LogRuntimeSettingsSource(logger, isAppConfigurationAdded: true);

        // Assert
        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Information);
        entry.Message.Should().Contain("App Configuration").And.Contain("GitHubReviewApp:Runtime");
    }
}
