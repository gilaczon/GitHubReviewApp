namespace GitHubReviewApp.Tests.Configuration;

/// <summary>
/// Builds the real dependency-injection container from the real appsettings.json. Unit tests with mocks
/// cannot notice a missing registration or a bad lifetime; this does.
/// </summary>
public class ServiceRegistrationTests
{
    private static IConfiguration BuildConfiguration(Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["AnthropicApiKey"] = "sk-ant-test",
            ["GitHubAppPrivateKey"] = "pem",
            ["GitHubWebhookSecret"] = "secret"
        };

        foreach (var pair in extra ?? [])
        {
            settings[pair.Key] = pair.Value;
        }

        return new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false)
            .AddInMemoryCollection(settings)
            .Build();
    }

    private static ServiceProvider BuildProvider(bool isClaudeMocked = false, Dictionary<string, string?>? extra = null)
    {
        var configuration = BuildConfiguration(extra);
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton(configuration);
        services.AddReviewPipeline(configuration, isClaudeMocked);

        // Validate everything at build time: any registration that cannot be constructed fails here.
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    // ── Resolution ───────────────────────────────────────────────────────────

    [Fact]
    public void AddReviewPipeline_GivenProductionSettings_Should_ResolveEveryDependencyTheFunctionsNeed()
    {
        // Arrange
        using var provider = BuildProvider();

        // Act
        var act = () =>
        {
            ActivatorUtilities.CreateInstance<ReviewProcessor>(provider);
            ActivatorUtilities.CreateInstance<ReviewPoisonHandler>(provider);
            ActivatorUtilities.CreateInstance<WebhookReceiver>(provider);
        };

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void AddReviewPipeline_GivenProduction_Should_UseTheRealClaudeService()
    {
        // Arrange
        using var provider = BuildProvider(isClaudeMocked: false);

        // Act
        var claude = provider.GetRequiredService<IClaudeService>();

        // Assert
        claude.Should().BeOfType<ClaudeService>();
    }

    [Fact]
    public void AddReviewPipeline_GivenLocalDevelopment_Should_UseTheMockClaudeService()
    {
        // Arrange
        using var provider = BuildProvider(isClaudeMocked: true);

        // Act
        var claude = provider.GetRequiredService<IClaudeService>();

        // Assert
        claude.Should().BeOfType<MockClaudeService>();
    }

    [Fact]
    public void AddReviewPipeline_GivenTwoResolutions_Should_GiveEachReviewItsOwnOrchestrator()
    {
        // Arrange
        using var provider = BuildProvider();

        // Act
        var first = provider.GetRequiredService<IReviewOrchestrator>();
        var second = provider.GetRequiredService<IReviewOrchestrator>();

        // Assert
        first.Should().NotBeSameAs(second);
    }

    [Fact]
    public void AddReviewPipeline_GivenTheStatelessServices_Should_ShareOneInstanceOfEach()
    {
        // Arrange
        using var provider = BuildProvider();

        // Act
        var builders = (provider.GetRequiredService<ReviewInputBuilder>(), provider.GetRequiredService<ReviewInputBuilder>());
        var selectors = (provider.GetRequiredService<ModelSelector>(), provider.GetRequiredService<ModelSelector>());
        var auth = (provider.GetRequiredService<IGitHubAppAuthService>(), provider.GetRequiredService<IGitHubAppAuthService>());

        // Assert
        builders.Item1.Should().BeSameAs(builders.Item2);
        selectors.Item1.Should().BeSameAs(selectors.Item2);
        auth.Item1.Should().BeSameAs(auth.Item2, "the token cache lives in this service and must be shared");
    }

    [Theory]
    [InlineData(typeof(ReviewInFlightRegistry))]
    [InlineData(typeof(ModelRequestLimiter))]
    [InlineData(typeof(ModelResultCache))]
    public void AddReviewPipeline_GivenTheServicesConcurrentReviewsCoordinateThrough_Should_RegisterEachAsASingleton(Type serviceType)
    {
        // Arrange — a transient copy would give every review its own limiter, registry and cache, and nothing would be shared between reviews
        var configuration = BuildConfiguration();
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddReviewPipeline(configuration, isClaudeMocked: true);

        // Act
        var descriptor = services.Single(service => service.ServiceType == serviceType);

        // Assert
        descriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddReviewPipeline_GivenTheServicesConcurrentReviewsShareAskedForTwice_Should_HandBackTheSameLimiterRegistryAndCache()
    {
        // Arrange
        using var provider = BuildProvider();

        // Act
        var first = provider.GetRequiredService<ModelRequestLimiter>();
        var second = provider.GetRequiredService<ModelRequestLimiter>();
        var registries = (provider.GetRequiredService<ReviewInFlightRegistry>(), provider.GetRequiredService<ReviewInFlightRegistry>());
        var caches = (provider.GetRequiredService<ModelResultCache>(), provider.GetRequiredService<ModelResultCache>());

        // Assert
        first.Should().BeSameAs(second);
        registries.Item1.Should().BeSameAs(registries.Item2);
        caches.Item1.Should().BeSameAs(caches.Item2);
    }

    [Theory]
    [InlineData(typeof(IReviewOrchestrator))]
    [InlineData(typeof(IReviewFailureNotifier))]
    [InlineData(typeof(ReviewPublisher))]
    public void AddReviewPipeline_GivenTheServicesThatDependOnTypedHttpClients_Should_RegisterEachAsTransient(Type serviceType)
    {
        // Arrange — a singleton would pin one client, and its connection pool, for the life of the process, and never notice a DNS change
        var configuration = BuildConfiguration();
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddReviewPipeline(configuration, isClaudeMocked: true);

        // Act
        var descriptor = services.Single(service => service.ServiceType == serviceType);

        // Assert
        descriptor.Lifetime.Should().Be(ServiceLifetime.Transient);
    }

    [Fact]
    public void AddReviewPipeline_GivenTheGitHubClient_Should_GiveEachCallAMinuteBeforeAbandoningIt()
    {
        // Arrange — the framework's default is 100 seconds, and a GitHub call that hangs would hold the queue message for that long on every attempt
        using var provider = BuildProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        // Act
        using var client = factory.CreateClient(nameof(IGitHubService));

        // Assert
        client.Timeout.Should().Be(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void AddReviewPipeline_GivenTheServices_Should_RegisterOneRuntimeSettingsMonitor()
    {
        // Arrange
        using var provider = BuildProvider();

        // Act
        var first = provider.GetRequiredService<IRuntimeSettings>();
        var second = provider.GetRequiredService<IRuntimeSettings>();

        // Assert
        first.Should().BeOfType<RuntimeSettingsMonitor>();
        first.Should().BeSameAs(second, "the snapshot a reload publishes must be the one every review reads");
    }

    [Fact]
    public void AddReviewPipeline_GivenTheShippedAppsettings_Should_GiveTheRuntimeDefaults()
    {
        // Arrange — the Runtime section of appsettings.json is what applies without App Configuration, and it must not drift from the code's defaults
        using var provider = BuildProvider();

        // Act
        var current = provider.GetRequiredService<IRuntimeSettings>().Current;

        // Assert
        current.Should().Be(RuntimeSettings.Default);
        provider.GetRequiredService<IConfiguration>().GetSection("Runtime").GetChildren().Select(child => child.Key)
            .Should().BeEquivalentTo("IsReviewEnabled", "SonnetModel", "OpusModel");
    }

    // ── Settings binding ─────────────────────────────────────────────────────

    private static IEnumerable<string> UnknownKeys(IConfigurationSection section, Type optionsType)
    {
        return section.GetChildren()
            .Select(child => child.Key)
            .Where(key => optionsType.GetProperty(key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase) is null);
    }

    [Fact]
    public void ShippedAppsettings_GivenTheClaudeAndReviewSections_Should_OnlyUseKeysTheOptionsClassesKnow()
    {
        // Arrange — a misspelt key is silently ignored by the binder, so the code default would apply and nobody would notice
        var configuration = BuildConfiguration();

        // Act
        var unknown = UnknownKeys(configuration.GetSection(ClaudeOptions.SectionName), typeof(ClaudeOptions))
            .Concat(UnknownKeys(configuration.GetSection(ReviewOptions.SectionName), typeof(ReviewOptions)))
            .ToList();

        // Assert
        unknown.Should().BeEmpty();
    }

    [Fact]
    public void AddReviewPipeline_GivenClaudeSettings_Should_BindThemFromConfiguration()
    {
        // Arrange
        using var provider = BuildProvider(extra: new() { ["Claude:MaxOutputTokens"] = "12345", ["Claude:Effort"] = "" });

        // Act
        var options = provider.GetRequiredService<IOptions<ClaudeOptions>>().Value;

        // Assert — the values come from configuration, not from literals in code
        options.MaxOutputTokens.Should().Be(12_345);
        options.Effort.Should().BeEmpty();
    }

    [Fact]
    public void AddReviewPipeline_GivenTheShippedAppsettings_Should_SeedTheModelChoiceSettings()
    {
        // Arrange
        using var provider = BuildProvider();

        // Act
        var options = provider.GetRequiredService<IOptions<ReviewOptions>>().Value;

        // Assert — the repository holds the bank-login and Stripe card flows but its name says neither, so only this list sends it to Opus
        options.OpusLineThreshold.Should().Be(40);
        options.HighRiskRepositories.Should().Equal("MoneyMeTeam/Moneyme.Global.Library.VueApp");
    }

    [Fact]
    public void AddReviewPipeline_GivenNoClaudeOrReviewSections_Should_FallBackToTheCodeDefaults()
    {
        // Arrange
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var services = new ServiceCollection();
        services.AddReviewPipeline(configuration, isClaudeMocked: true);
        using var provider = services.BuildServiceProvider();

        // Act
        var claude = provider.GetRequiredService<IOptions<ClaudeOptions>>().Value;
        var review = provider.GetRequiredService<IOptions<ReviewOptions>>().Value;

        // Assert
        claude.Should().BeEquivalentTo(new ClaudeOptions());
        review.Should().BeEquivalentTo(new ReviewOptions());
    }

    // ── Settings are validated when the host starts ──────────────────────────

    // A real host, because that is what runs the startup validation in production: the worker's Program builds one and calls Run.
    private static async Task StartHostAsync(Dictionary<string, string?>? extra = null)
    {
        var configuration = BuildConfiguration(extra);

        using var host = new Microsoft.Extensions.Hosting.HostBuilder()
            .ConfigureServices((_, services) =>
            {
                services.AddSingleton(configuration);
                services.AddReviewPipeline(configuration, isClaudeMocked: true);
            })
            .Build();

        await host.StartAsync();
        await host.StopAsync();
    }

    [Fact]
    public void AddReviewPipeline_GivenAnInvalidReviewSetting_Should_RefuseToHandOutTheOptions()
    {
        // Arrange
        using var provider = BuildProvider(extra: new() { ["Review:MaxDiffBytes"] = "0" });

        // Act
        var act = () => provider.GetRequiredService<IOptions<ReviewOptions>>().Value;

        // Assert
        act.Should().Throw<OptionsValidationException>().WithMessage("*Review:MaxDiffBytes*");
    }

    [Fact]
    public async Task AddReviewPipeline_GivenAnInvalidClaudeSetting_Should_FailWhenTheHostStartsNotOnTheFirstReview()
    {
        // Arrange — a misspelt effort level used to surface as a failed review of the first pull request
        var settings = new Dictionary<string, string?> { ["Claude:Effort"] = "turbo" };

        // Act
        var act = () => StartHostAsync(settings);

        // Assert
        await act.Should().ThrowAsync<OptionsValidationException>().WithMessage("*Claude:Effort*");
    }

    [Fact]
    public async Task AddReviewPipeline_GivenAnInvalidReviewSetting_Should_FailWhenTheHostStarts()
    {
        // Arrange
        var settings = new Dictionary<string, string?> { ["Review:AllowedAuthorAssociations:0"] = "MEMBERS" };

        // Act
        var act = () => StartHostAsync(settings);

        // Assert
        await act.Should().ThrowAsync<OptionsValidationException>().WithMessage("*'MEMBERS'*");
    }

    [Fact]
    public async Task AddReviewPipeline_GivenAListSettingWrittenAsOneValue_Should_FailWhenTheHostStartsBecauseTheBinderWouldOtherwiseIgnoreItAndOpenTheGate()
    {
        // Arrange — "Review__AllowedAuthorAssociations=OWNER,MEMBER" in the app settings is one value; through the real container, as in production.
        // The strict binding refuses it before the validator is asked, and the validator keeps its own check for a binder that is not strict.
        var settings = new Dictionary<string, string?> { ["Review:AllowedAuthorAssociations"] = "OWNER,MEMBER" };

        // Act
        var act = () => StartHostAsync(settings);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Review:AllowedAuthorAssociations*");
    }

    [Theory]
    [InlineData("Review:AllowedAuthorAssociation:0", "OWNER", "AllowedAuthorAssociation")]
    [InlineData("Review:MaxFile", "10", "MaxFile")]
    [InlineData("Claude:Modell", "claude-x", "Modell")]
    public async Task AddReviewPipeline_GivenAMisspeltSettingName_Should_FailWhenTheHostStartsInsteadOfBeingIgnored(string key, string value, string name)
    {
        // Arrange — the binder skips a key it does not know and the code default applies without a word. For the author allow-list the default is
        // "everyone", so a missing letter in its name would open the gate.
        var settings = new Dictionary<string, string?> { [key] = value };

        // Act
        var act = () => StartHostAsync(settings);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage($"*{name}*");
    }

    [Theory]
    [InlineData("Claude:Model", "claude-sonnet-5-5", "Model")]
    [InlineData("Review:MaxTotalBytes", "1200000", "MaxTotalBytes")]
    [InlineData("Review:MaxFiles", "150", "MaxFiles")]
    [InlineData("Review:MaxParallelChunks", "3", "MaxParallelChunks")]
    [InlineData("Review:MaxInlineComments", "25", "MaxInlineComments")]
    [InlineData("Review:MaxFindings", "60", "MaxFindings")]
    [InlineData("Review:LargePullRequestFileThreshold", "50", "LargePullRequestFileThreshold")]
    [InlineData("Review:LargePullRequestLineThreshold", "2000", "LargePullRequestLineThreshold")]
    [InlineData("Review:MaxGuidelinesChars", "20000", "MaxGuidelinesChars")]
    public async Task AddReviewPipeline_GivenASettingTheReviewNoLongerHas_Should_FailWhenTheHostStartsNamingIt(string key, string value, string name)
    {
        // Arrange — a setting the review no longer reads would otherwise sit in an app's settings looking as if it still changed something
        var settings = new Dictionary<string, string?> { [key] = value };

        // Act
        var act = () => StartHostAsync(settings);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage($"*{name}*");
    }

    [Fact]
    public async Task AddReviewPipeline_GivenAReviewLimitShorterThanOneModelRequest_Should_FailWhenTheHostStarts()
    {
        // Arrange — the shipped request timeout is longer than this
        var settings = new Dictionary<string, string?> { ["Review:MaxReviewDurationSeconds"] = "60" };

        // Act
        var act = () => StartHostAsync(settings);

        // Assert
        await act.Should().ThrowAsync<OptionsValidationException>().WithMessage("*Review:MaxReviewDurationSeconds*Claude:RequestTimeoutSeconds*");
    }

    [Fact]
    public async Task AddReviewPipeline_GivenTheShippedAppsettings_Should_PassValidationAtStartup()
    {
        // Arrange

        // Act
        var act = () => StartHostAsync();

        // Assert
        await act.Should().NotThrowAsync();
    }

    // ── The shipped settings must make sense ─────────────────────────────────

    [Fact]
    public void ShippedClaudeSettings_GivenTheShippedAppsettings_Should_BeInternallyConsistent()
    {
        // Arrange
        using var provider = BuildProvider();

        // Act
        var options = provider.GetRequiredService<IOptions<ClaudeOptions>>().Value;

        // Assert
        options.MaxOutputTokens.Should().BeGreaterThanOrEqualTo(8_000, "thinking tokens count towards the output limit");
        options.RequestTimeoutSeconds.Should().BeInRange(60, 900, "it must expire well inside the 30 minute Flex Consumption function timeout (set functionTimeout in host.json on a plan with a shorter default)");
        options.MaxRetries.Should().BeInRange(0, 5);
        new[] { "", "low", "medium", "high", "xhigh", "max" }.Should().Contain(options.Effort?.ToLowerInvariant() ?? string.Empty);
    }

    [Fact]
    public void ShippedReviewSettings_GivenTheShippedAppsettings_Should_BeInternallyConsistent()
    {
        // Arrange
        using var provider = BuildProvider();

        // Act
        var options = provider.GetRequiredService<IOptions<ReviewOptions>>().Value;

        // Assert
        options.MaxReviewDurationSeconds.Should().BeInRange(
            provider.GetRequiredService<IOptions<ClaudeOptions>>().Value.RequestTimeoutSeconds,
            1_500,
            "a review is at least as long as one request and ends well inside the 30 minute Flex Consumption function timeout");
        options.MaxFileBytes.Should().BeLessThanOrEqualTo(options.MaxDiffBytes, "a file cut to its limit must fit in the diff");
        options.MaxConcurrentModelRequests.Should().BeInRange(1, 64);
    }
}
