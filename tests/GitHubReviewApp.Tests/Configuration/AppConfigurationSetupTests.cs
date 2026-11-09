namespace GitHubReviewApp.Tests.Configuration;

public class AppConfigurationSetupTests
{
    private static IHostEnvironment EnvironmentNamed(string name)
    {
        var environment = new Mock<IHostEnvironment>();
        environment.Setup(e => e.EnvironmentName).Returns(name);

        return environment.Object;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddRuntimeAppConfiguration_GivenNoConnectionString_Should_AddNothingAndReturnFalse(string? connectionString)
    {
        // Arrange
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { [AppConfigurationSetup.ConnectionStringSecretName] = connectionString });
        var services = new ServiceCollection();
        var sourceCountBefore = ((IConfigurationBuilder)configuration).Sources.Count;

        // Act
        var isAdded = configuration.AddRuntimeAppConfiguration(services, EnvironmentNamed("Production"));

        // Assert
        isAdded.Should().BeFalse();
        ((IConfigurationBuilder)configuration).Sources.Should().HaveCount(sourceCountBefore);
        services.Should().BeEmpty();
        services.BuildServiceProvider().GetService<IConfigurationRefresherProvider>().Should().BeNull();
    }

    [Fact]
    public void AddRuntimeAppConfiguration_GivenNoSecretKeyAtAll_Should_AddNothingAndReturnFalse()
    {
        // Arrange
        var configuration = new ConfigurationManager();
        var services = new ServiceCollection();

        // Act
        var isAdded = configuration.AddRuntimeAppConfiguration(services, EnvironmentNamed("Production"));

        // Assert
        isAdded.Should().BeFalse();
        services.Should().BeEmpty();
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
