namespace GitHubReviewApp.Tests.Services;

public class ModelResultCacheTests
{
    private static readonly ClaudeRunRequest _request = new("claude-sonnet-5-5", "system", "user");

    private static ClaudeRunResult Result(RunOutcome outcome = RunOutcome.Completed, string summary = "summary")
    {
        var answer = outcome == RunOutcome.Completed ? new RunAnswer(new Dictionary<CheckId, RatedCheck>(), [], summary, string.Empty) : null;

        return new ClaudeRunResult(outcome, answer, "claude-sonnet-5-5", []);
    }

    // Entries key-1, key-2 and so on, each stored one second after the one before it.
    private static void AddOneEntryPerSecond(ModelResultCache cache, FakeTimeProvider time, int count)
    {
        foreach (var number in Enumerable.Range(1, count))
        {
            cache.Add($"key-{number}", Result(summary: $"result {number}"));
            time.Advance(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public void Find_GivenACompletedResultThatWasStored_Should_ReturnIt()
    {
        // Arrange
        var sut = new ModelResultCache(new FakeTimeProvider());
        var stored = Result(summary: "kept");
        sut.Add("key", stored);

        // Act
        var found = sut.Find("key");

        // Assert
        found.Should().BeSameAs(stored);
    }

    [Fact]
    public void Find_GivenNothingStoredForTheKey_Should_ReturnNull()
    {
        // Arrange
        var sut = new ModelResultCache(new FakeTimeProvider());
        sut.Add("other", Result());

        // Act
        var found = sut.Find("key");

        // Assert
        found.Should().BeNull();
    }

    [Theory]
    [InlineData(RunOutcome.OutputTruncated)]
    [InlineData(RunOutcome.Refused)]
    [InlineData(RunOutcome.InvalidOutput)]
    [InlineData(RunOutcome.Rejected)]
    public void Add_GivenAResultThatDidNotComplete_Should_NotKeepItBecauseARetryMayDoBetter(RunOutcome outcome)
    {
        // Arrange
        var sut = new ModelResultCache(new FakeTimeProvider());

        // Act
        sut.Add("key", Result(outcome));

        // Assert
        sut.Find("key").Should().BeNull();
    }

    [Fact]
    public void Find_GivenAResultStoredExactlyAnHourAgo_Should_StillReturnIt()
    {
        // Arrange
        var time = new FakeTimeProvider();
        var sut = new ModelResultCache(time);
        sut.Add("key", Result());

        // Act
        time.Advance(TimeSpan.FromHours(1));

        // Assert
        sut.Find("key").Should().NotBeNull();
    }

    [Fact]
    public void Find_GivenAResultStoredMoreThanAnHourAgo_Should_ReturnNull()
    {
        // Arrange — an answer for a prompt that has not been asked for an hour may belong to a branch that has long moved on
        var time = new FakeTimeProvider();
        var sut = new ModelResultCache(time);
        sut.Add("key", Result());

        // Act
        time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        var found = sut.Find("key");

        // Assert
        found.Should().BeNull();
    }

    [Fact]
    public void Add_GivenMoreResultsThanTheCacheHolds_Should_DropTheOldestFirstAndKeepTheNewest()
    {
        // Arrange — 64 entries is the limit; add 70, one second apart
        var time = new FakeTimeProvider();
        var sut = new ModelResultCache(time);

        // Act
        AddOneEntryPerSecond(sut, time, count: 70);

        // Assert
        Enumerable.Range(1, 6).Select(number => sut.Find($"key-{number}")).Should().OnlyContain(found => found == null, "the six oldest were dropped");
        Enumerable.Range(7, 64).Select(number => sut.Find($"key-{number}")).Should().OnlyContain(found => found != null, "the 64 newest were kept");
    }

    [Fact]
    public void Add_GivenTheSameKeyAgain_Should_ReplaceTheResultAndRestartItsLifetime()
    {
        // Arrange
        var time = new FakeTimeProvider();
        var sut = new ModelResultCache(time);
        sut.Add("key", Result(summary: "old"));
        time.Advance(TimeSpan.FromMinutes(50));
        sut.Add("key", Result(summary: "new"));

        // Act
        time.Advance(TimeSpan.FromMinutes(50));
        var found = sut.Find("key");

        // Assert
        found.Should().NotBeNull();
        found!.Answer!.Summary.Should().Be("new");
    }

    [Fact]
    public void KeyFor_GivenTheSameRequestAndRunIndexTwice_Should_BeStable()
    {
        // Arrange
        var first = ModelResultCache.KeyFor(_request, 0);

        // Act
        var second = ModelResultCache.KeyFor(_request with { }, 0);

        // Assert
        first.Should().Be(second);
    }

    [Fact]
    public void KeyFor_GivenTheSameRequestAndDifferentRunIndexes_Should_Differ()
    {
        // Arrange — the two runs of a review send the same request, and each must keep its own answer
        var first = ModelResultCache.KeyFor(_request, 0);

        // Act
        var second = ModelResultCache.KeyFor(_request, 1);

        // Assert
        first.Should().NotBe(second);
    }

    [Fact]
    public void KeyFor_GivenADifferentModel_Should_Differ()
    {
        // Arrange — an answer from Sonnet is not the answer Opus was asked for
        var sonnet = ModelResultCache.KeyFor(_request, 0);

        // Act
        var opus = ModelResultCache.KeyFor(_request with { Model = "claude-opus-5-5" }, 0);

        // Assert
        sonnet.Should().NotBe(opus);
    }

    [Theory]
    [InlineData("system", "user", "system", "other")]
    [InlineData("system", "user", "other", "user")]
    [InlineData("ab", "c", "a", "bc")]
    public void KeyFor_GivenADifferentPromptOrTextMovedAcrossTheBoundary_Should_NotCollide(string systemA, string userA, string systemB, string userB)
    {
        // Arrange
        var keyA = ModelResultCache.KeyFor(_request with { SystemPrompt = systemA, UserMessage = userA }, 0);

        // Act
        var keyB = ModelResultCache.KeyFor(_request with { SystemPrompt = systemB, UserMessage = userB }, 0);

        // Assert — a changed prompt or diff must never find a stale answer
        keyA.Should().NotBe(keyB);
    }
}
