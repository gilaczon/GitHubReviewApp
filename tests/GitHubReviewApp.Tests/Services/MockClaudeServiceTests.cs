namespace GitHubReviewApp.Tests.Services;

public class MockClaudeServiceTests
{
    private static MockClaudeService BuildSut(TimeSpan? latency = null)
    {
        return new MockClaudeService(NullLogger<MockClaudeService>.Instance, latency ?? TimeSpan.Zero);
    }

    [Fact]
    public async Task ReviewAsync_GivenARequestWithTwoFiles_Should_ReturnACompletedResultThatCountsThem()
    {
        // Arrange
        var request = new ClaudeReviewRequest("system", "<files>\n<file path=\"a.cs\">x</file>\n<file path=\"b.cs\">y</file>\n</files>");

        // Act
        var result = await BuildSut().ReviewAsync(request, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(ChunkOutcome.Completed);
        result.Summary.Should().Contain("2 file(s)");
        result.Model.Should().Be("mock");
    }

    [Fact]
    public async Task ReviewAsync_GivenAnyRequest_Should_ReturnNoFindingsSoNothingUnanchoredIsEverPosted()
    {
        // Arrange
        var request = new ClaudeReviewRequest("system", "user");

        // Act
        var result = await BuildSut().ReviewAsync(request, CancellationToken.None);

        // Assert
        result.Findings.Should().BeEmpty();
    }

    [Fact]
    public async Task ReviewAsync_GivenAnyRequest_Should_LabelTheReplyAsAMockSoItCannotBeMistakenForARealReview()
    {
        // Arrange
        var request = new ClaudeReviewRequest("system", "user");

        // Act
        var result = await BuildSut().ReviewAsync(request, CancellationToken.None);

        // Assert
        result.Summary.Should().Contain("MOCK");
    }

    [Fact]
    public async Task ReviewAsync_GivenACancelledToken_Should_ThrowOperationCanceled()
    {
        // Arrange
        var sut = BuildSut(latency: TimeSpan.FromSeconds(30));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // Act
        var act = () => sut.ReviewAsync(new ClaudeReviewRequest("s", "u"), cancellation.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
