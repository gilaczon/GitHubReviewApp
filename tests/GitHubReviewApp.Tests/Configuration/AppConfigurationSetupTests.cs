namespace GitHubReviewApp.Tests.Configuration;

public class AppConfigurationSetupTests
{
    private const string WellFormedConnectionString = "Endpoint=https://127.0.0.1:1;Id=test-id;Secret=dGVzdC1zZWNyZXQ=";

    private static IHostEnvironment EnvironmentNamed(string name)
    {
        var environment = new Mock<IHostEnvironment>();
        environment.Setup(e => e.EnvironmentName).Returns(name);

        return environment.Object;
    }

    private static ConfigurationManager ConfigurationWithSecret(string? connectionString)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { [AppConfigurationSetup.ConnectionStringSecretName] = connectionString });

        return configuration;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddRuntimeAppConfiguration_GivenNoConnectionString_Should_AddNothingAndReturnNotConfigured(string? connectionString)
    {
        // Arrange
        var configuration = ConfigurationWithSecret(connectionString);
        var services = new ServiceCollection();
        var sourceCountBefore = ((IConfigurationBuilder)configuration).Sources.Count;

        // Act
        var outcome = configuration.AddRuntimeAppConfiguration(services, EnvironmentNamed("Production"));

        // Assert
        outcome.Should().Be(AppConfigurationOutcome.NotConfigured);
        ((IConfigurationBuilder)configuration).Sources.Should().HaveCount(sourceCountBefore);
        services.Should().BeEmpty();
        services.BuildServiceProvider().GetService<IConfigurationRefresherProvider>().Should().BeNull();
    }

    [Fact]
    public void AddRuntimeAppConfiguration_GivenNoSecretKeyAtAll_Should_AddNothingAndReturnNotConfigured()
    {
        // Arrange
        var configuration = new ConfigurationManager();
        var services = new ServiceCollection();

        // Act
        var outcome = configuration.AddRuntimeAppConfiguration(services, EnvironmentNamed("Production"));

        // Assert
        outcome.Should().Be(AppConfigurationOutcome.NotConfigured);
        services.Should().BeEmpty();
    }

    public static IEnumerable<object[]> MalformedConnectionStrings()
    {
        yield return ["Endpoint=https://store.azconfig.io"];
        yield return ["Endpoint=https://store.azconfig.io;Id=test-id"];
        yield return ["Endpoint=https://store.azconfig.io;Id=test-id;Secret="];
        yield return ["Endpoint=not-a-uri;Id=test-id;Secret=dGVzdC1zZWNyZXQ="];
        yield return ["Endpoint=https://store.azconfig.io;Id=test-id;Secret=not base64 at all!"];
        yield return ["Endpoint=https://store.azconfig.io;Secret=dGVzdC1zZWNyZXQ="];
        yield return ["\"Endpoint=https://store.azconfig.io;Id=test-id;Secret=dGVzdC1zZWNyZXQ=\""];
        yield return ["Endpoint=ftp://store.azconfig.io;Id=test-id;Secret=dGVzdC1zZWNyZXQ="];
        yield return ["https://store.azconfig.io"];
        yield return ["just some text"];
    }

    [Theory]
    [MemberData(nameof(MalformedConnectionStrings))]
    public void AddRuntimeAppConfiguration_GivenAMalformedConnectionString_Should_NotThrowAddNothingAndReturnInvalid(string connectionString)
    {
        // Arrange
        var configuration = ConfigurationWithSecret(connectionString);
        var services = new ServiceCollection();
        var sourceCountBefore = ((IConfigurationBuilder)configuration).Sources.Count;

        // Act
        var outcome = configuration.AddRuntimeAppConfiguration(services, EnvironmentNamed("Production"));

        // Assert
        outcome.Should().Be(AppConfigurationOutcome.Invalid);
        ((IConfigurationBuilder)configuration).Sources.Should().HaveCount(sourceCountBefore);
        services.Should().BeEmpty();
        services.BuildServiceProvider().GetService<IConfigurationRefresherProvider>().Should().BeNull();
    }

    [Fact]
    public void AddRuntimeAppConfiguration_GivenAWellFormedConnectionStringForAStoreThatCannotBeReached_Should_StartAndRegisterTheRefresher()
    {
        // Arrange
        var configuration = ConfigurationWithSecret(WellFormedConnectionString);
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);

        // Act
        var outcome = configuration.AddRuntimeAppConfiguration(services, EnvironmentNamed("Production"));

        // Assert
        outcome.Should().Be(AppConfigurationOutcome.Added);
        services.BuildServiceProvider().GetRequiredService<IConfigurationRefresherProvider>().Refreshers.Should().HaveCount(1);
    }

    [Theory]
    [InlineData(WellFormedConnectionString, true)]
    [InlineData("endpoint=https://store.azconfig.io;id=test-id;secret=dGVzdC1zZWNyZXQ=", true)]
    [InlineData("Endpoint=https://store.azconfig.io;Id=test-id;Secret=dGVzdC1zZWNyZXQ=;", true)]
    [InlineData("Endpoint=http://localhost:8080;Id=test-id;Secret=dGVzdA==", true)]
    [InlineData("Endpoint=https://store.azconfig.io;Id=test-id", false)]
    [InlineData("Endpoint=https://store.azconfig.io;Id=test-id;Secret=%%%", false)]
    [InlineData("Endpoint=/relative/path;Id=test-id;Secret=dGVzdA==", false)]
    [InlineData("", false)]
    public void IsWellFormedConnectionString_GivenAConnectionString_Should_CheckEndpointIdAndBase64Secret(string connectionString, bool isExpectedWellFormed)
    {
        // Arrange
        // (the connection string comes from the test data)

        // Act
        var isWellFormed = AppConfigurationSetup.IsWellFormedConnectionString(connectionString);

        // Assert
        isWellFormed.Should().Be(isExpectedWellFormed);
    }

    [Fact]
    public void StartupTimeout_GivenGitHubsTenSecondWebhookTimeout_Should_BeAFewSecondsAndWellBelowIt()
    {
        // Arrange
        var githubWebhookTimeout = TimeSpan.FromSeconds(10);

        // Act
        var timeout = AppConfigurationSetup.StartupTimeout;

        // Assert
        timeout.Should().Be(TimeSpan.FromSeconds(3));
        timeout.Should().BeLessThan(githubWebhookTimeout);
    }

    [Theory]
    [InlineData("Production", 120)]
    [InlineData("production", 120)]
    [InlineData("Development", 30)]
    [InlineData("Staging", 30)]
    [InlineData("", 30)]
    public void RefreshInterval_GivenAnEnvironment_Should_Be120SecondsInProductionAnd30Otherwise(string environmentName, int expectedSeconds)
    {
        // Arrange
        var environment = EnvironmentNamed(environmentName);

        // Act
        var interval = AppConfigurationSetup.RefreshInterval(environment);

        // Assert
        interval.Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Fact]
    public void Constants_GivenTheStoreLayout_Should_MatchTheKeysTheOperatorsSet()
    {
        // Arrange
        var expected = new[] { "AppConfigurationConnectionString", "GitHubReviewApp:", "GitHubReviewApp:Runtime:*", "GitHubReviewApp:Sentinel" };

        // Act
        var actual = new[]
        {
            AppConfigurationSetup.ConnectionStringSecretName,
            AppConfigurationSetup.KeyPrefix,
            AppConfigurationSetup.RuntimeKeyFilter,
            AppConfigurationSetup.SentinelKey
        };

        // Assert
        actual.Should().Equal(expected);
    }
}
