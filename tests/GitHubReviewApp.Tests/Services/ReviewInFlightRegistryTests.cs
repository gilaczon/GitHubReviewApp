namespace GitHubReviewApp.Tests.Services;

public class ReviewInFlightRegistryTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public void TryBegin_GivenACommitNobodyIsReviewing_Should_ReturnALease()
    {
        // Arrange
        var sut = new ReviewInFlightRegistry();

        // Act
        using var lease = sut.TryBegin("o", "r", 7, Sha);

        // Assert
        lease.Should().NotBeNull();
    }

    [Fact]
    public void TryBegin_GivenTheSameCommitIsAlreadyBeingReviewed_Should_ReturnNullSoNoSecondReviewIsPaidFor()
    {
        // Arrange
        var sut = new ReviewInFlightRegistry();
        using var first = sut.TryBegin("o", "r", 7, Sha);

        // Act
        var second = sut.TryBegin("o", "r", 7, Sha);

        // Assert
        second.Should().BeNull();
    }

    [Fact]
    public void TryBegin_GivenTheFirstLeaseWasDisposed_Should_AllowTheCommitToBeReviewedAgain()
    {
        // Arrange — a retry of a failed run must be able to start
        var sut = new ReviewInFlightRegistry();
        sut.TryBegin("o", "r", 7, Sha)!.Dispose();

        // Act
        using var again = sut.TryBegin("o", "r", 7, Sha);

        // Assert
        again.Should().NotBeNull();
    }

    [Theory]
    [InlineData("o", "r", 8, Sha)]
    [InlineData("o", "other", 7, Sha)]
    [InlineData("other", "r", 7, Sha)]
    [InlineData("o", "r", 7, "fedcba9876543210fedcba9876543210fedcba98")]
    public void TryBegin_GivenAnotherPullRequestRepositoryOrCommit_Should_AllowBothToRun(string owner, string repo, int prNumber, string headSha)
    {
        // Arrange
        var sut = new ReviewInFlightRegistry();
        using var first = sut.TryBegin("o", "r", 7, Sha);

        // Act
        using var second = sut.TryBegin(owner, repo, prNumber, headSha);

        // Assert
        second.Should().NotBeNull();
    }

    [Fact]
    public void TryBegin_GivenTheSameCommitWrittenInAnotherLetterCase_Should_TreatItAsTheSameReview()
    {
        // Arrange — GitHub names are case-insensitive, so a redelivery that spells the owner differently is still the same pull request
        var sut = new ReviewInFlightRegistry();
        using var first = sut.TryBegin("My-Org", "My-Repo", 7, Sha);

        // Act
        var second = sut.TryBegin("my-org", "my-repo", 7, Sha.ToUpperInvariant());

        // Assert
        second.Should().BeNull();
    }

    [Fact]
    public void Dispose_GivenALeaseDisposedTwiceAfterAnotherRunBegan_Should_NotReleaseTheOtherRunsClaim()
    {
        // Arrange — the second disposal of a finished run must not free the commit for a run that is under way
        var sut = new ReviewInFlightRegistry();
        var finished = sut.TryBegin("o", "r", 7, Sha)!;
        finished.Dispose();
        using var underWay = sut.TryBegin("o", "r", 7, Sha);

        // Act
        finished.Dispose();
        var duplicate = sut.TryBegin("o", "r", 7, Sha);

        // Assert
        underWay.Should().NotBeNull();
        duplicate.Should().BeNull("the run that is under way still holds the commit");
    }
}
