namespace GitHubReviewApp.Tests.Services;

public class ModelRequestLimiterTests
{
    // A wait that never ends must fail the test, not hang the run.
    private static readonly TimeSpan _hangGuard = TimeSpan.FromSeconds(20);

    private static ModelRequestLimiter BuildSut(int limit)
    {
        return new ModelRequestLimiter(Options.Create(new ReviewOptions { MaxConcurrentModelRequests = limit }));
    }

    [Fact]
    public async Task AcquireAsync_GivenFreeSlots_Should_GrantUpToTheLimitWithoutWaiting()
    {
        // Arrange
        var sut = BuildSut(limit: 2);

        // Act
        var first = sut.AcquireAsync(CancellationToken.None);
        var second = sut.AcquireAsync(CancellationToken.None);

        // Assert
        first.IsCompleted.Should().BeTrue();
        second.IsCompleted.Should().BeTrue();
        (await first).Dispose();
        (await second).Dispose();
    }

    [Fact]
    public async Task AcquireAsync_GivenEverySlotIsTaken_Should_KeepTheNextCallerWaitingUntilASlotIsGivenBack()
    {
        // Arrange
        var sut = BuildSut(limit: 2);
        var first = await sut.AcquireAsync(CancellationToken.None);
        var second = await sut.AcquireAsync(CancellationToken.None);

        // Act
        var third = sut.AcquireAsync(CancellationToken.None);
        var isWaitingBeforeARelease = !third.IsCompleted;
        first.Dispose();
        var granted = await third.WaitAsync(_hangGuard);

        // Assert
        isWaitingBeforeARelease.Should().BeTrue("both slots were taken");
        granted.Dispose();
        second.Dispose();
    }

    [Fact]
    public async Task Dispose_GivenTheSameSlotGivenBackTwice_Should_FreeItOnlyOnce()
    {
        // Arrange — a double release would hand out a slot that does not exist, and the limit would quietly rise by one for as long as the process lives
        var sut = BuildSut(limit: 1);
        var slot = await sut.AcquireAsync(CancellationToken.None);
        slot.Dispose();
        slot.Dispose();

        // Act
        var firstAfter = await sut.AcquireAsync(CancellationToken.None);
        var secondAfter = sut.AcquireAsync(CancellationToken.None);

        // Assert
        secondAfter.IsCompleted.Should().BeFalse("the limit is one, and only one slot was ever freed");
        firstAfter.Dispose();
        (await secondAfter.WaitAsync(_hangGuard)).Dispose();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task AcquireAsync_GivenALimitBelowOne_Should_StillLetOneRequestRunAtATimeInsteadOfNone(int limit)
    {
        // Arrange
        var sut = BuildSut(limit);

        // Act
        var first = await sut.AcquireAsync(CancellationToken.None).WaitAsync(_hangGuard);
        var second = sut.AcquireAsync(CancellationToken.None);

        // Assert
        second.IsCompleted.Should().BeFalse();
        first.Dispose();
        (await second.WaitAsync(_hangGuard)).Dispose();
    }

    [Fact]
    public async Task AcquireAsync_GivenAWaitThatIsCancelled_Should_ThrowAndLeaveNoSlotBehind()
    {
        // Arrange
        var sut = BuildSut(limit: 1);
        var held = await sut.AcquireAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var waiting = sut.AcquireAsync(cancellation.Token);

        // Act
        await cancellation.CancelAsync();
        var act = () => waiting.WaitAsync(_hangGuard);
        await act.Should().ThrowAsync<OperationCanceledException>();
        held.Dispose();

        // Assert — a slot lost to the cancelled wait would leave this one waiting for good
        var next = await sut.AcquireAsync(CancellationToken.None).WaitAsync(_hangGuard);
        next.Dispose();
    }

    [Fact]
    public async Task AcquireAsync_GivenATokenThatIsAlreadyCancelled_Should_ThrowWithoutTakingASlot()
    {
        // Arrange
        var sut = BuildSut(limit: 1);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // Act
        var act = () => sut.AcquireAsync(cancellation.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        // Assert
        var next = await sut.AcquireAsync(CancellationToken.None).WaitAsync(_hangGuard);
        next.Dispose();
    }
}
