namespace GitHubReviewApp.Tests.Configuration;

public class AppConfigurationRefreshMiddlewareTests
{
    // A refresh that never starts must fail the test, not hang the suite.
    private static readonly TimeSpan _hangGuard = TimeSpan.FromSeconds(20);

    private static Mock<FunctionContext> ContextWith(IServiceProvider services)
    {
        var context = new Mock<FunctionContext>();
        context.Setup(c => c.InstanceServices).Returns(services);

        return context;
    }

    [Fact]
    public async Task Invoke_GivenNoRefresherProvider_Should_CallTheNextMiddleware()
    {
        // Arrange
        var context = ContextWith(new ServiceCollection().BuildServiceProvider());
        var isNextCalled = false;
        var sut = new AppConfigurationRefreshMiddleware();

        // Act
        await sut.Invoke(context.Object, _ =>
        {
            isNextCalled = true;
            return Task.CompletedTask;
        });

        // Assert
        isNextCalled.Should().BeTrue();
    }

    /// <summary>A refresher whose <c>TryRefreshAsync</c> completes <paramref name="wasAsked"/>, since the middleware now starts it off-thread.</summary>
    private static Mock<IConfigurationRefresher> RefresherSignalling(TaskCompletionSource wasAsked)
    {
        var refresher = new Mock<IConfigurationRefresher>();
        refresher.Setup(r => r.TryRefreshAsync(It.IsAny<CancellationToken>()))
            .Callback(() => wasAsked.TrySetResult())
            .ReturnsAsync(true);

        return refresher;
    }

    [Fact]
    public async Task Invoke_GivenARefresher_Should_AskItToRefreshAndCallTheNextMiddleware()
    {
        // Arrange
        var firstAsked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAsked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRefresher = RefresherSignalling(firstAsked);
        var secondRefresher = RefresherSignalling(secondAsked);
        var provider = new Mock<IConfigurationRefresherProvider>();
        provider.Setup(p => p.Refreshers).Returns([firstRefresher.Object, secondRefresher.Object]);
        var services = new ServiceCollection().AddSingleton(provider.Object).BuildServiceProvider();
        var context = ContextWith(services);
        var isNextCalled = false;
        var sut = new AppConfigurationRefreshMiddleware();

        // Act
        await sut.Invoke(context.Object, _ =>
        {
            isNextCalled = true;
            return Task.CompletedTask;
        });
        await Task.WhenAll(firstAsked.Task, secondAsked.Task).WaitAsync(_hangGuard);

        // Assert
        firstRefresher.Verify(r => r.TryRefreshAsync(It.IsAny<CancellationToken>()), Times.Once());
        secondRefresher.Verify(r => r.TryRefreshAsync(It.IsAny<CancellationToken>()), Times.Once());
        isNextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Invoke_GivenAnActivityOnTheInvocation_Should_StartTheRefreshOutsideItSoItsSpansAreNotOrphans()
    {
        // Arrange — the host's invocation activity is never exported, so a refresh whose spans parent to it shows as orphans in the trace view
        Activity? activitySeenByTheRefresh = null;
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresher = new Mock<IConfigurationRefresher>();
        refresher.Setup(r => r.TryRefreshAsync(It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                activitySeenByTheRefresh = Activity.Current;
                asked.TrySetResult();
            })
            .ReturnsAsync(true);
        var provider = new Mock<IConfigurationRefresherProvider>();
        provider.Setup(p => p.Refreshers).Returns([refresher.Object]);
        var context = ContextWith(new ServiceCollection().AddSingleton(provider.Object).BuildServiceProvider());
        using var invocation = new Activity("host.invocation").Start();
        var sut = new AppConfigurationRefreshMiddleware();

        // Act
        await sut.Invoke(context.Object, _ => Task.CompletedTask);
        await asked.Task.WaitAsync(_hangGuard);

        // Assert
        activitySeenByTheRefresh.Should().BeNull("the refresh runs without the invocation's execution context");
    }
}
