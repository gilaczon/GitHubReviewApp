namespace GitHubReviewApp.Tests.Services;

public class ChunkResultCacheTests
{
    private static ClaudeReviewResult Result(ChunkOutcome outcome = ChunkOutcome.Completed, string summary = "summary")
    {
        return new ClaudeReviewResult(outcome, summary, [], "claude-test", ClaudeUsage.Empty);
    }

    // Entries key-1, key-2 and so on, each stored one second after the one before it.
    private static void AddOneEntryPerSecond(ChunkResultCache cache, FakeTimeProvider time, int count)
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
        var sut = new ChunkResultCache(new FakeTimeProvider());
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
        var sut = new ChunkResultCache(new FakeTimeProvider());
        sut.Add("other", Result());

        // Act
        var found = sut.Find("key");

        // Assert
        found.Should().BeNull();
    }

    [Theory]
    [InlineData(ChunkOutcome.OutputTruncated)]
    [InlineData(ChunkOutcome.Refused)]
    [InlineData(ChunkOutcome.InvalidOutput)]
    [InlineData(ChunkOutcome.Rejected)]
    public void Add_GivenAResultThatDidNotComplete_Should_NotKeepItBecauseARetryMayDoBetter(ChunkOutcome outcome)
    {
        // Arrange
        var sut = new ChunkResultCache(new FakeTimeProvider());

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
        var sut = new ChunkResultCache(time);
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
        var sut = new ChunkResultCache(time);
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
        var sut = new ChunkResultCache(time);

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
        var sut = new ChunkResultCache(time);
        sut.Add("key", Result(summary: "old"));
        time.Advance(TimeSpan.FromMinutes(50));
        sut.Add("key", Result(summary: "new"));

        // Act
        time.Advance(TimeSpan.FromMinutes(50));
        var found = sut.Find("key");

        // Assert
        found.Should().NotBeNull();
        found!.Summary.Should().Be("new");
    }

    [Fact]
    public void KeyFor_GivenTheSamePromptsTwice_Should_BeStable()
    {
        // Arrange
        var first = ChunkResultCache.KeyFor("system", "user");

        // Act
        var second = ChunkResultCache.KeyFor("system", "user");

        // Assert
        first.Should().Be(second);
    }

    [Theory]
    [InlineData("system", "user", "system", "other")]
    [InlineData("system", "user", "other", "user")]
    [InlineData("ab", "c", "a", "bc")]
    public void KeyFor_GivenADifferentPromptOrTextMovedAcrossTheBoundary_Should_NotCollide(string systemA, string userA, string systemB, string userB)
    {
        // Arrange
        var keyA = ChunkResultCache.KeyFor(systemA, userA);

        // Act
        var keyB = ChunkResultCache.KeyFor(systemB, userB);

        // Assert — a changed guideline or diff must never find a stale answer
        keyA.Should().NotBe(keyB);
    }
}
