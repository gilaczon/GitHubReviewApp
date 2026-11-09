namespace GitHubReviewApp.Tests.Services;

public class MockClaudeServiceTests
{
    private static MockClaudeService BuildSut(TimeSpan? latency = null)
    {
        return new MockClaudeService(NullLogger<MockClaudeService>.Instance, latency ?? TimeSpan.Zero);
    }

    [Fact]
    public async Task ReviewAsync_GivenARunRequest_Should_ReturnACompletedAnswerRatingEveryCheck()
    {
        // Arrange
        var request = new ClaudeRunRequest("claude-sonnet-5-5", "system", "user");

        // Act
        var result = await BuildSut().ReviewAsync(request, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(RunOutcome.Completed);
        result.Model.Should().Be("mock");
        result.Attempts.Should().BeEmpty("no model was called, so nothing was spent");
        result.Answer.Should().NotBeNull();
        result.Answer!.Rated.Keys.Should().BeEquivalentTo(Enum.GetValues<CheckId>());
        result.Answer.Rated.Values.Should().OnlyContain(check => check.Status == CheckStatus.Pass && check.Note.Length == 0 && !check.IsSerious);
        result.Answer.Summary.Should().Be("Mock review: no model was called.");
        result.Answer.Findings.Should().BeEmpty();
        result.Answer.CoverageNote.Should().BeEmpty();
    }

    [Fact]
    public async Task ReviewAsync_GivenARunRequestAndACancelledToken_Should_ThrowOperationCanceled()
    {
        // Arrange
        var sut = BuildSut(latency: TimeSpan.FromSeconds(30));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // Act
        var act = () => sut.ReviewAsync(new ClaudeRunRequest("claude-sonnet-5-5", "s", "u"), cancellation.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
