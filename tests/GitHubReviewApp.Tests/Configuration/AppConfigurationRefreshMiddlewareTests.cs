namespace GitHubReviewApp.Tests.Configuration;

public class AppConfigurationRefreshMiddlewareTests
{
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

    [Fact]
    public async Task Invoke_GivenARefresher_Should_AskItToRefreshAndCallTheNextMiddleware()
    {
        // Arrange
        var firstRefresher = new Mock<IConfigurationRefresher>();
        var secondRefresher = new Mock<IConfigurationRefresher>();
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

        // Assert
        firstRefresher.Verify(r => r.TryRefreshAsync(It.IsAny<CancellationToken>()), Times.Once());
        secondRefresher.Verify(r => r.TryRefreshAsync(It.IsAny<CancellationToken>()), Times.Once());
        isNextCalled.Should().BeTrue();
    }
}
