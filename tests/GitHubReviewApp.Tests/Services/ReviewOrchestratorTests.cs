namespace GitHubReviewApp.Tests.Services;

[Collection(TelemetryCollection.Name)]
public class ReviewOrchestratorTests
{
    private const string Owner = ReviewOrchestratorHarness.Owner;
    private const string Repo = ReviewOrchestratorHarness.Repo;
    private const int PrNumber = ReviewOrchestratorHarness.PrNumber;
    private const string HeadSha = ReviewOrchestratorHarness.HeadSha;
    private const string SonnetModel = ReviewOrchestratorHarness.SonnetModel;
    private const string OpusModel = ReviewOrchestratorHarness.OpusModel;
    private const string EarlierSha = "1111111111111111111111111111111111111111";
    private const string NewerSha = "ffffffffffffffffffffffffffffffffffffffff";
    private const string RejectedDetail = "HTTP 400: the service's own words about the request";
    private const string RecordPrefix = "Review record";

    // A run that hangs must fail the test, not hang the suite.
    private static readonly TimeSpan _hangGuard = TimeSpan.FromSeconds(20);

    /// <summary>Every way one run can end without an answer while the other completes, with the index of the run that completes: either one.</summary>
    public static IEnumerable<object[]> EndingsOfTheOtherRun()
    {
        object[] endings =
        [
            ReviewOrchestratorHarness.Ended(RunOutcome.Refused),
            ReviewOrchestratorHarness.Ended(RunOutcome.InvalidOutput),
            ReviewOrchestratorHarness.Ended(RunOutcome.Rejected, detail: RejectedDetail),
            ReviewOrchestratorHarness.Ended(RunOutcome.OutputTruncated),
            new HttpRequestException("The AI service is unavailable.")
        ];

        return endings.SelectMany(ending => new object[][] { [ending, 0], [ending, 1] });
    }

    /// <summary>
    /// A second changed file beside <c>src/Foo.cs</c>, the diff budget, and whether the review is partial: read whole; sent no diff by GitHub; cut,
    /// with 3,000 lines of about 22 bytes against the 60,000-byte file limit; or left out over a 1,000-byte budget with 100 lines.
    /// </summary>
    public static IEnumerable<object[]> SecondFilesAndWhetherTheReviewIsPartial()
    {
        const int defaultMaxDiffBytes = 180_000;

        yield return [new PullRequestFile("src/Bar.cs", "modified", 1, 0, ReviewOrchestratorHarness.SourcePatch), defaultMaxDiffBytes, false];
        yield return [new PullRequestFile("src/Bar.cs", "modified", 1, 0, null), defaultMaxDiffBytes, true];
        yield return [ReviewOrchestratorHarness.ModifiedFile("src/Bar.cs", 3_000), defaultMaxDiffBytes, true];
        yield return [ReviewOrchestratorHarness.ModifiedFile("src/Bar.cs", 100), 1_000, true];
    }

    private static Task<ClaudeRunResult> Answer(object ending)
    {
        return ending switch
        {
            ClaudeRunResult result => Task.FromResult(result),
            Exception exception => Task.FromException<ClaudeRunResult>(exception),
            _ => throw new ArgumentException("An ending is a result or an exception.", nameof(ending))
        };
    }

    private static ClaudeRunRequest RequestFor(ReviewOrchestratorHarness harness)
    {
        var input = new ReviewInputBuilder(Options.Create(harness.Options)).Build(Owner, Repo, harness.PullRequest, harness.Files);

        return new ClaudeRunRequest(SonnetModel, ChecklistPrompt.SystemPrompt, input.UserMessage);
    }

    private static string Comment(ReviewOrchestratorHarness harness)
    {
        return harness.CreatedComments.Should().ContainSingle().Subject.Body;
    }

    private static void ShouldBeMarked(string body, ReviewRecordOutcome expected)
    {
        ReviewCommentMarker.TryRead(body, out var markedSha, out var outcome).Should().BeTrue();
        markedSha.Should().Be(HeadSha);
        outcome.Should().Be(expected);
    }

    private static void ShouldHavePublishedNothing(ReviewOrchestratorHarness harness)
    {
        harness.CreatedCheckRuns.Should().BeEmpty();
        harness.CreatedComments.Should().BeEmpty();
        harness.UpdatedComments.Should().BeEmpty();
    }

    // ── A finished review ────────────────────────────────────────────────────

    [Fact]
    public async Task ReviewPullRequestAsync_GivenTwoCompletedRuns_Should_PublishTheMergedVerdictAsACheckRunAndThenTheComment()
    {
        // Arrange
        var harness = new ReviewOrchestratorHarness();

        // Act
        var result = await harness.RunAsync();

        // Assert
        result.Should().Be(ReviewRunResult.Posted(ReviewVerdict.PassWithComments, runCount: 2, includedFileCount: 1, isPartial: false));
        harness.CallOrder.Should().Equal("check-run", "comment:create");

        var checkRun = harness.CreatedCheckRuns.Should().ContainSingle().Subject;
        checkRun.HeadSha.Should().Be(HeadSha);
        checkRun.Conclusion.Should().Be(CheckRunConclusion.Neutral);
        checkRun.Title.Should().Be("PASS WITH COMMENTS");
        checkRun.ExternalId.Should().Be("7");
        checkRun.Summary.Should().StartWith("PASS WITH COMMENTS - ").And.NotContain("moneyme-ai-review");

        var body = Comment(harness);
        body.Should().Contain("Reviewed twice; both reviews agreed.");
        body.Should().Contain($"· {SonnetModel} at 0123456789", "both runs were answered by one model, named once");
        ShouldBeMarked(body, ReviewRecordOutcome.Reviewed);
    }

    [Theory]
    [InlineData(CheckId.Security, CheckStatus.Fail, CheckRunConclusion.Failure, "FAIL")]
    [InlineData(CheckId.Functionality, CheckStatus.Pass, CheckRunConclusion.Success, "PASS")]
    [InlineData(CheckId.Functionality, CheckStatus.Concern, CheckRunConclusion.Neutral, "PASS WITH COMMENTS")]
    public async Task ReviewPullRequestAsync_GivenEachVerdict_Should_ConcludeTheCheckRunToMatch(
        CheckId check, CheckStatus status, CheckRunConclusion conclusion, string title)
    {
        // Arrange
        var harness = new ReviewOrchestratorHarness
        {
            ClaudeHandler = (_, _, _) => Task.FromResult(ReviewOrchestratorHarness.Completed([(check, status)]))
        };

        // Act
        await harness.RunAsync();

        // Assert
        var checkRun = harness.CreatedCheckRuns.Should().ContainSingle().Subject;
        checkRun.Conclusion.Should().Be(conclusion);
        checkRun.Title.Should().Be(title);
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenTheTwoRuns_Should_SendIdenticalRequestsThatAreInFlightTogether()
    {
        // Arrange — each request only finishes once the other has arrived, so the review completes only if the two overlap
        var probe = new ConcurrencyProbe(2);
        var harness = new ReviewOrchestratorHarness
        {
            ClaudeHandler = async (_, _, cancellationToken) =>
            {
                await probe.EnterAndWaitForPeersAsync(cancellationToken);
                return ReviewOrchestratorHarness.Completed();
            }
        };

        // Act
        await harness.RunAsync().WaitAsync(_hangGuard);

        // Assert
        probe.MaxInFlight.Should().Be(2);
        harness.ClaudeRequests.Should().HaveCount(2);
        harness.ClaudeRequests[1].Should().Be(harness.ClaudeRequests[0]);
        harness.ClaudeRequests[0].Should().Be(RequestFor(harness));
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenTheOnlyModelSlotIsHeldByAnotherReview_Should_WaitForItAndThenRunTheTwoRunsOneAtATime()
    {
        // Arrange — the test holds the process's only slot, and the first run holds the slot until the test lets it finish. Each run takes a slot
        // of its own, so the second run cannot start while the first one works.
        var harness = new ReviewOrchestratorHarness { Options = new ReviewOptions { MaxConcurrentModelRequests = 1 } };
        var firstRunStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRunMayFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.ClaudeHandler = async (index, _, cancellationToken) =>
        {
            firstRunStarted.TrySetResult();
            await (index == 0 ? firstRunMayFinish.Task : Task.CompletedTask).WaitAsync(cancellationToken);

            return ReviewOrchestratorHarness.Completed();
        };
        var heldSlot = await harness.Limiter.AcquireAsync(CancellationToken.None);

        // Act
        var run = harness.RunAsync().WaitAsync(_hangGuard);
        await Settle.QueuedWorkAsync();
        var requestsWhileTheSlotWasHeld = harness.ClaudeRequests.Count;
        heldSlot.Dispose();
        await firstRunStarted.Task.WaitAsync(_hangGuard);
        await Settle.QueuedWorkAsync();
        var requestsWhileTheFirstRunWorked = harness.ClaudeRequests.Count;
        firstRunMayFinish.SetResult();
        var result = await run;

        // Assert
        requestsWhileTheSlotWasHeld.Should().Be(0, "no run starts without a slot of its own");
        requestsWhileTheFirstRunWorked.Should().Be(1, "the second run waits for the slot the first run holds");
        result.RunCount.Should().Be(2);
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenAFirstRunAlreadyInTheCache_Should_StillCallTheModelForTheSecondRun()
    {
        // Arrange — the two runs send the same request, so a key without the run index would hand the second run the first one's answer
        var harness = new ReviewOrchestratorHarness();
        harness.Cache.Add(ModelResultCache.KeyFor(RequestFor(harness), 0), ReviewOrchestratorHarness.Completed());

        // Act
        var result = await harness.RunAsync();

        // Assert
        harness.ClaudeRequests.Should().ContainSingle();
        result.RunCount.Should().Be(2);
    }

    [Theory]
    [MemberData(nameof(EndingsOfTheOtherRun))]
    public async Task ReviewPullRequestAsync_GivenOneRunDoesNotComplete_Should_PostTheOtherRunsVerdictAsReviewedOnce(object ending, int completedRun)
    {
        // Arrange — whichever run completes, A or B, its verdict alone is published, with the footer naming the model that gave it
        var harness = new ReviewOrchestratorHarness
        {
            ClaudeHandler = (index, _, _) => index == completedRun ? Task.FromResult(ReviewOrchestratorHarness.Completed()) : Answer(ending)
        };

        // Act
        var result = await harness.RunAsync();

        // Assert
        result.Should().Be(ReviewRunResult.Posted(ReviewVerdict.PassWithComments, runCount: 1, includedFileCount: 1, isPartial: false));
        harness.ClaudeRequests.Should().HaveCount(2);
        var body = Comment(harness);
        body.Should().Contain("Reviewed once: the other review did not finish.");
        body.Should().Contain($"(1 reviewed) · {SonnetModel} at ");
        body.Should().NotContain(RejectedDetail, "the service's text about a rejected request stays in the logs");
        ShouldBeMarked(body, ReviewRecordOutcome.Reviewed);
        harness.CreatedCheckRuns.Should().ContainSingle().Which.Conclusion.Should().Be(CheckRunConclusion.Neutral);
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenBothRunsFailTransiently_Should_RethrowAndPublishNothing()
    {
        // Arrange
        var harness = new ReviewOrchestratorHarness
        {
            ClaudeHandler = (_, _, _) => Task.FromException<ClaudeRunResult>(new HttpRequestException("The AI service is unavailable."))
        };

        // Act
        var act = () => harness.RunAsync();

        // Assert
        await act.Should().ThrowAsync<HttpRequestException>();
        ShouldHavePublishedNothing(harness);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task ReviewPullRequestAsync_GivenOneRunRefusedAndOneFailedTransiently_Should_RethrowTheTransientFailure(int failingRun)
    {
        // Arrange — a retry may well complete both, so a failure that can clear up outranks a run that ended without an answer
        var harness = new ReviewOrchestratorHarness
        {
            ClaudeHandler = (index, _, _) => index == failingRun
                ? Task.FromException<ClaudeRunResult>(new HttpRequestException("The AI service is unavailable."))
                : Task.FromResult(ReviewOrchestratorHarness.Ended(RunOutcome.Refused))
        };

        // Act
        var act = () => harness.RunAsync();

        // Assert
        await act.Should().ThrowAsync<HttpRequestException>();
        ShouldHavePublishedNothing(harness);
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenOneRunFailedTransientlyAndOnePermanently_Should_RethrowTheTransientFailure()
    {
        // Arrange
        var harness = new ReviewOrchestratorHarness
        {
            ClaudeHandler = (index, _, _) => index == 0
                ? Task.FromException<ClaudeRunResult>(new InvalidOperationException("Secret 'GitHubReviewAppAnthropicApiKey' is not configured."))
                : Task.FromException<ClaudeRunResult>(new HttpRequestException("The AI service is unavailable."))
        };

        // Act
        var act = () => harness.RunAsync();

        // Assert
        await act.Should().ThrowAsync<HttpRequestException>();
        ShouldHavePublishedNothing(harness);
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenBothRunsThrow_Should_LogTheFailureItDoesNotRethrowAsAWarning()
    {
        // Arrange — only one failure can be rethrown; the other would otherwise be seen by nobody
        var harness = new ReviewOrchestratorHarness
        {
            ClaudeHandler = (index, _, _) => index == 0
                ? Task.FromException<ClaudeRunResult>(new HttpRequestException("The AI service is unavailable."))
                : Task.FromException<ClaudeRunResult>(new InvalidOperationException("Secret 'GitHubReviewAppAnthropicApiKey' is not configured."))
        };

        // Act
        var act = () => harness.RunAsync();

        // Assert
        await act.Should().ThrowAsync<HttpRequestException>();
        var entry = harness.Logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Warning).Subject;
        entry.Message.Should().Contain("Run 1 of the review of my-org/my-service#7 also failed");
        entry.Exception.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenBothRunsFailPermanently_Should_Rethrow()
    {
        // Arrange — the processor reports a failure that retrying cannot fix to the author at once
        var harness = new ReviewOrchestratorHarness
        {
            ClaudeHandler = (_, _, _) => Task.FromException<ClaudeRunResult>(new InvalidOperationException("Secret 'GitHubReviewAppAnthropicApiKey' is not configured."))
        };

        // Act
        var act = () => harness.RunAsync();

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*GitHubReviewAppAnthropicApiKey*");
        ShouldHavePublishedNothing(harness);
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenOneRunRefusedAndOneFailedPermanently_Should_RethrowThePermanentFailure()
    {
        // Arrange
        var harness = new ReviewOrchestratorHarness
        {
            ClaudeHandler = (index, _, _) => index == 0
                ? Task.FromResult(ReviewOrchestratorHarness.Ended(RunOutcome.Refused))
                : Task.FromException<ClaudeRunResult>(new InvalidOperationException("Secret 'GitHubReviewAppAnthropicApiKey' is not configured."))
        };

        // Act
        var act = () => harness.RunAsync();

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        ShouldHavePublishedNothing(harness);
    }

    [Theory]
    [InlineData(RunOutcome.Refused, RunOutcome.Refused, ReviewFailureReasons.AllRefused)]
    [InlineData(RunOutcome.OutputTruncated, RunOutcome.OutputTruncated, ReviewFailureReasons.AllTruncated)]
    [InlineData(RunOutcome.InvalidOutput, RunOutcome.InvalidOutput, ReviewFailureReasons.AllInvalid)]
    [InlineData(RunOutcome.Rejected, RunOutcome.Rejected, ReviewFailureReasons.AllRejected)]
    [InlineData(RunOutcome.Refused, RunOutcome.InvalidOutput, ReviewFailureReasons.NoneFinished)]
    public async Task ReviewPullRequestAsync_GivenBothRunsEndWithoutAnAnswer_Should_PublishReviewFailed(
        RunOutcome first, RunOutcome second, string reason)
    {
        // Arrange
        var harness = new ReviewOrchestratorHarness
        {
            ClaudeHandler = (index, _, _) => Task.FromResult(ReviewOrchestratorHarness.Ended(index == 0 ? first : second, detail: RejectedDetail))
        };

        // Act
        var result = await harness.RunAsync();

        // Assert
        result.Should().Be(ReviewRunResult.Failed());
        harness.CallOrder.Should().Equal("check-run", "comment:create");

        var checkRun = harness.CreatedCheckRuns.Should().ContainSingle().Subject;
        checkRun.Conclusion.Should().Be(CheckRunConclusion.Neutral);
        checkRun.Title.Should().Be("Review failed");
        checkRun.ExternalId.Should().Be("7");
        checkRun.Summary.Should().Contain(reason).And.NotContain(RejectedDetail);

        var body = Comment(harness);
        body.Should().StartWith("Review failed - no verdict").And.Contain(reason).And.NotContain(RejectedDetail);
        ShouldBeMarked(body, ReviewRecordOutcome.Failed);
    }

    [Theory]
    [InlineData(RunOutcome.Rejected, RunOutcome.Rejected, LogLevel.Error)]
    [InlineData(RunOutcome.Refused, RunOutcome.Refused, LogLevel.Warning)]
    [InlineData(RunOutcome.Rejected, RunOutcome.Refused, LogLevel.Warning)]
    public async Task ReviewPullRequestAsync_GivenBothRunsEndWithoutAnAnswer_Should_LogAnErrorOnlyWhenEveryRunWasRejected(
        RunOutcome first, RunOutcome second, LogLevel expectedLevel)
    {
        // Arrange — a request the service rejects is wrong in itself (a model that does not take the output format or the effort), so every review
        // fails the same way until an operator acts: the one place that says so above Information. The service's text stays out of the line.
        var harness = new ReviewOrchestratorHarness
        {
            ClaudeHandler = (index, _, _) => Task.FromResult(ReviewOrchestratorHarness.Ended(index == 0 ? first : second, detail: RejectedDetail))
        };

        // Act
        await harness.RunAsync();

        // Assert
        var entry = harness.Logger.Entries.Should().ContainSingle(entry => entry.Level >= LogLevel.Warning).Subject;
        entry.Level.Should().Be(expectedLevel);
        entry.Message.Should().Contain("my-org/my-service#7").And.Contain(first.ToString()).And.Contain(SonnetModel).And.Contain("unless the pull request has moved on")
            .And.NotContain(RejectedDetail);
    }

    // ── Reviewed once per pull request and commit ────────────────────────────

    [Fact]
    public async Task ReviewPullRequestAsync_GivenThisPullRequestAlreadyReviewedAtTheHead_Should_SkipWithoutCallingTheModel()
    {
        // Arrange — a redelivered webhook, or a reopen of a pull request whose commit was reviewed
        var harness = new ReviewOrchestratorHarness();
        harness.Comments.Add(ReviewOrchestratorHarness.OwnComment(100, HeadSha, ReviewRecordOutcome.Reviewed));

        // Act
        var result = await harness.RunAsync();

        // Assert
        result.Should().Be(ReviewRunResult.Skipped(ReviewSkipReason.AlreadyReviewed));
        harness.ClaudeRequests.Should().BeEmpty();
        ShouldHavePublishedNothing(harness);
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenSomeoneElsesCommentWithTheMarker_Should_StillReview()
    {
        // Arrange — anyone can paste the marker into a comment, and a forged one must not be able to suppress a review
        var harness = new ReviewOrchestratorHarness();
        harness.Comments.Add(ReviewOrchestratorHarness.OwnComment(100, HeadSha, ReviewRecordOutcome.Reviewed, author: "octocat"));

        // Act
        var result = await harness.RunAsync();

        // Assert
        result.Status.Should().Be(ReviewRunStatus.Posted);
        harness.CreatedComments.Should().ContainSingle();
        harness.UpdatedComments.Should().BeEmpty("a comment by anyone else is never edited");
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenAnotherPullRequestReviewedTheSameCommit_Should_StillReviewThisOne()
    {
        // Arrange — one branch with pull requests into two bases: each has its own diff, and each needs its own review and comment
        var harness = new ReviewOrchestratorHarness();
        await harness.RunAsync(ReviewOrchestratorHarness.QueuedMessage with { PrNumber = 8 });

        // Act
        var result = await harness.RunAsync();

        // Assert
        result.Status.Should().Be(ReviewRunStatus.Posted);
        harness.CreatedComments.Select(comment => comment.PrNumber).Should().Equal(8, PrNumber);
        harness.UpdatedComments.Should().BeEmpty();
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenAFailedRecordForTheHead_Should_ReviewAgain()
    {
        // Arrange — the "Review failed" notice tells the author a reopen tries again
        var harness = new ReviewOrchestratorHarness();
        harness.Comments.Add(ReviewOrchestratorHarness.OwnComment(100, HeadSha, ReviewRecordOutcome.Failed));

        // Act
        var result = await harness.RunAsync();

        // Assert
        result.Status.Should().Be(ReviewRunStatus.Posted);
        harness.ClaudeRequests.Should().HaveCount(2);
        var update = harness.UpdatedComments.Should().ContainSingle().Subject;
        update.Id.Should().Be(100);
        ShouldBeMarked(update.Body, ReviewRecordOutcome.Reviewed);
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenTheHeadMovedWhileTheModelWorked_Should_PublishNothing()
    {
        // Arrange
        var harness = new ReviewOrchestratorHarness();
        harness.PullRequestAfterModel = harness.PullRequest with { HeadSha = NewerSha };

        // Act
        var result = await harness.RunAsync();

        // Assert
        result.Should().Be(ReviewRunResult.Skipped(ReviewSkipReason.Superseded));
        harness.ClaudeRequests.Should().HaveCount(2);
        ShouldHavePublishedNothing(harness);
    }

    [Theory]
    [InlineData("closed", false, ReviewSkipReason.NotOpen)]
    [InlineData("open", true, ReviewSkipReason.Draft)]
    public async Task ReviewPullRequestAsync_GivenThePullRequestClosedOrTurnedDraftWhileTheModelWorked_Should_PublishNothing(
        string state, bool isDraft, ReviewSkipReason reason)
    {
        // Arrange
        var harness = new ReviewOrchestratorHarness();
        harness.PullRequestAfterModel = harness.PullRequest with { State = state, IsDraft = isDraft };

        // Act
        var result = await harness.RunAsync();

        // Assert
        result.Should().Be(ReviewRunResult.Skipped(reason));
        ShouldHavePublishedNothing(harness);
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenAnotherInstanceFinishedFirst_Should_NotPublishASecondTime()
    {
        // Arrange — another instance wrote its record of this commit while the model was working here
        var harness = new ReviewOrchestratorHarness();
        harness.ClaudeHandler = (index, _, _) =>
        {
            harness.AddComment(PrNumber, ReviewOrchestratorHarness.OwnComment(100 + index, HeadSha, ReviewRecordOutcome.Reviewed));
            return Task.FromResult(ReviewOrchestratorHarness.Completed());
        };

        // Act
        var result = await harness.RunAsync();

        // Assert
        result.Should().Be(ReviewRunResult.Skipped(ReviewSkipReason.AlreadyReviewed));
        ShouldHavePublishedNothing(harness);
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenTheSameCommitInProgressInThisProcess_Should_Skip()
    {
        // Arrange — a reopen or a redelivery queued a second message for a commit that is being reviewed right now
        var harness = new ReviewOrchestratorHarness();
        using var otherRun = harness.InFlight.TryBegin(Owner, Repo, PrNumber, HeadSha);

        // Act
        var result = await harness.RunAsync();

        // Assert
        otherRun.Should().NotBeNull();
        result.Should().Be(ReviewRunResult.Skipped(ReviewSkipReason.InProgress));
        harness.ClaudeRequests.Should().BeEmpty();
        ShouldHavePublishedNothing(harness);
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenARunThatFailed_Should_ReleaseItsClaimSoTheRetryIsNotSkipped()
    {
        // Arrange
        var harness = new ReviewOrchestratorHarness
        {
            ClaudeHandler = (_, _, _) => Task.FromException<ClaudeRunResult>(new HttpRequestException("The AI service is unavailable."))
        };

        // Act
        var act = () => harness.RunAsync();
        await act.Should().ThrowAsync<HttpRequestException>();
        using var claim = harness.InFlight.TryBegin(Owner, Repo, PrNumber, HeadSha);

        // Assert
        claim.Should().NotBeNull();
    }

    [Theory]
    [InlineData("closed", false, HeadSha, ReviewSkipReason.NotOpen)]
    [InlineData("open", true, HeadSha, ReviewSkipReason.Draft)]
    [InlineData("open", false, NewerSha, ReviewSkipReason.Superseded)]
    public async Task ReviewPullRequestAsync_GivenAPullRequestNotWorthReviewing_Should_SkipBeforeReadingItsComments(
        string state, bool isDraft, string headSha, ReviewSkipReason reason)
    {
        // Arrange
        var harness = new ReviewOrchestratorHarness();
        harness.PullRequest = harness.PullRequest with { State = state, IsDraft = isDraft, HeadSha = headSha };

        // Act
        var result = await harness.RunAsync();

        // Assert
        result.Should().Be(ReviewRunResult.Skipped(reason));
        harness.ClaudeRequests.Should().BeEmpty();
        harness.GitHub.Verify(
            g => g.ListIssueCommentsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never());
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenNoChangedFiles_Should_SkipAsNothingToReview()
    {
        // Arrange
        var harness = new ReviewOrchestratorHarness { Files = [] };

        // Act
        var result = await harness.RunAsync();

        // Assert
        result.Should().Be(ReviewRunResult.Skipped(ReviewSkipReason.NothingToReview));
        harness.ClaudeRequests.Should().BeEmpty();
        ShouldHavePublishedNothing(harness);
    }

    // ── Publishing ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ReviewPullRequestAsync_GivenNoChecksPermission_Should_PostTheCommentWithoutACheckRun()
    {
        // Arrange
        var harness = new ReviewOrchestratorHarness { CanWriteChecks = false };

        // Act
        var result = await harness.RunAsync();

        // Assert
        result.Status.Should().Be(ReviewRunStatus.Posted);
        harness.CreatedCheckRuns.Should().BeEmpty();
        ShouldBeMarked(Comment(harness), ReviewRecordOutcome.Reviewed);
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenOurEarlierComment_Should_UpdateItInsteadOfAddingOne()
    {
        // Arrange — the app's comment from an earlier commit, after an older one of someone else's that quotes a marker
        var harness = new ReviewOrchestratorHarness();
        harness.Comments.Add(ReviewOrchestratorHarness.OwnComment(50, EarlierSha, ReviewRecordOutcome.Reviewed, author: "octocat"));
        harness.Comments.Add(ReviewOrchestratorHarness.OwnComment(100, EarlierSha, ReviewRecordOutcome.Reviewed));

        // Act
        await harness.RunAsync();

        // Assert
        harness.CreatedComments.Should().BeEmpty();
        var update = harness.UpdatedComments.Should().ContainSingle().Subject;
        update.Id.Should().Be(100);
        ShouldBeMarked(update.Body, ReviewRecordOutcome.Reviewed);
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenTheCommentFailsAfterTheCheckRun_Should_RetryWithoutANewModelCallOrASecondComment()
    {
        // Arrange — GitHub fails between the check run and the comment, and the queue retries the message on the same instance
        var harness = new ReviewOrchestratorHarness
        {
            CommentWriteHandler = attempt => attempt == 0
                ? Task.FromException(new GitHubApiException("GitHub is unavailable.", HttpStatusCode.ServiceUnavailable, false))
                : Task.CompletedTask
        };

        // Act
        var firstAttempt = () => harness.RunAsync();
        await firstAttempt.Should().ThrowAsync<GitHubApiException>();
        var retry = await harness.RunAsync();

        // Assert
        retry.Status.Should().Be(ReviewRunStatus.Posted);
        harness.ClaudeRequests.Should().HaveCount(2, "the retry reuses both finished runs");
        harness.CreatedComments.Should().ContainSingle();
        harness.CreatedCheckRuns.Should().HaveCount(2, "a second, identical check run is harmless");
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenRunsThatNameOneFileDifferently_Should_MergeTheirFindingsIntoOne()
    {
        // Arrange — the second run writes the diff's own prefix
        var harness = new ReviewOrchestratorHarness
        {
            ClaudeHandler = (index, _, _) => Task.FromResult(ReviewOrchestratorHarness.Completed(
                findings: [new ChecklistFinding(CheckId.Functionality, index == 0 ? "src/Foo.cs" : "b/src/Foo.cs", 11, "Retries twice.", "Retry once.")]))
        };

        // Act
        await harness.RunAsync();

        // Assert
        var body = Comment(harness);
        Regex.Matches(body, Regex.Escape("`src/Foo.cs:11`")).Should().ContainSingle();
        body.Should().NotContain("b/src/Foo.cs");
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenRunsAnsweredByDifferentModels_Should_NameBothInTheFooterRunAFirst()
    {
        // Arrange — the second run's request fell back to another model after a refusal
        var harness = new ReviewOrchestratorHarness
        {
            ClaudeHandler = (index, _, _) => Task.FromResult(ReviewOrchestratorHarness.Completed(model: index == 0 ? SonnetModel : "claude-opus-4-8"))
        };

        // Act
        await harness.RunAsync();

        // Assert
        Comment(harness).Should().Contain($"· {SonnetModel} + claude-opus-4-8 at 0123456789");
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenMoreChangedFilesThanGitHubListed_Should_CountTheUnlistedFilesAsNotReviewedButNotAsPartial()
    {
        // Arrange — GitHub lists at most 3,000 files; these two are listed, the lockfile is never read, and 3,003 more were not listed.
        // The comment counts the unlisted files as not reviewed; partial is only partial + over budget + no diff among the listed files.
        using var metrics = new MetricsRecorder();
        var harness = new ReviewOrchestratorHarness();
        harness.PullRequest = harness.PullRequest with { ChangedFileCount = 3_005 };

        // Act
        var result = await harness.RunAsync();

        // Assert
        var body = Comment(harness);
        body.Should().Contain("3004 of 3005 changed files were not reviewed in full.");
        body.Should().Contain("3005 files +10 -2 (1 reviewed)");
        result.IsPartial.Should().BeFalse();
        metrics.Has("github_review_app.reviews.partial").Should().BeFalse();
        harness.Logger.Entries.Should().ContainSingle(entry => entry.Message.StartsWith(RecordPrefix, StringComparison.Ordinal))
            .Which.Message.Should().Contain("3004 left out, 3003 not listed by GitHub", "the record counts what the comment counts");
    }

    [Theory]
    [MemberData(nameof(SecondFilesAndWhetherTheReviewIsPartial))]
    public async Task ReviewPullRequestAsync_GivenASecondFileThatWasReadWholeCutLeftOutOrSentNoDiff_Should_BePartialUnlessItWasReadWhole(
        PullRequestFile secondFile, int maxDiffBytes, bool isPartial)
    {
        // Arrange
        using var metrics = new MetricsRecorder();
        var harness = new ReviewOrchestratorHarness
        {
            Options = new ReviewOptions { MaxDiffBytes = maxDiffBytes },
            Files = [new PullRequestFile("src/Foo.cs", "modified", 1, 0, ReviewOrchestratorHarness.SourcePatch), secondFile]
        };

        // Act
        var result = await harness.RunAsync();

        // Assert
        result.IsPartial.Should().Be(isPartial);
        metrics.Has("github_review_app.reviews.partial").Should().Be(isPartial);
    }

    // ── The installation token ───────────────────────────────────────────────

    [Fact]
    public async Task ReviewPullRequestAsync_GivenTheModelTookMinutes_Should_UseAFreshTokenForEveryCallAfterIt()
    {
        // Arrange — the token fetched at the start could expire while the model works, so it is fetched again before anything after the model
        const string freshToken = "ghs_fetched_after_the_model";
        const string firstToken = ReviewOrchestratorHarness.Token;
        var harness = new ReviewOrchestratorHarness { TokenAfterModel = freshToken };

        // Act
        await harness.RunAsync();

        // Assert
        harness.ClaudeRequests.Should().HaveCount(2);
        harness.UsedTokens.Should().Equal(
            ("pull-request", firstToken),
            ("comments", firstToken),
            ("files", firstToken),
            ("pull-request", freshToken),
            ("comments", freshToken),
            ("check-run", freshToken),
            ("comment:create", freshToken));
    }

    // ── The model ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReviewPullRequestAsync_GivenAHighRiskLargeChange_Should_RunBothReviewsOnOpus()
    {
        // Arrange — over 40 lines in an auth file
        using var metrics = new MetricsRecorder();
        var harness = new ReviewOrchestratorHarness { Files = [ReviewOrchestratorHarness.ModifiedFile("src/Auth/LoginController.cs", 50)] };
        harness.PullRequest = harness.PullRequest with { ChangedFileCount = 1, Additions = 50, Deletions = 0 };

        // Act
        await harness.RunAsync();

        // Assert
        harness.ClaudeRequests.Select(request => request.Model).Should().Equal(OpusModel, OpusModel);
        metrics.Sum("github_review_app.review.model_choice", ("tier", "opus"), ("reason", "high_risk")).Should().Be(1);
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenNewRuntimeModelIds_Should_UseTheCurrentSnapshot()
    {
        // Arrange — the settings change after the orchestrator was built, as an App Configuration refresh would change them
        var harness = new ReviewOrchestratorHarness();
        var sut = harness.BuildSut();
        harness.RuntimeSettings = RuntimeSettings.Default with { SonnetModel = "claude-sonnet-6-0" };

        // Act
        await sut.ReviewPullRequestAsync(ReviewOrchestratorHarness.QueuedMessage, CancellationToken.None);

        // Assert
        harness.ClaudeRequests.Select(request => request.Model).Should().Equal("claude-sonnet-6-0", "claude-sonnet-6-0");
    }

    // ── The review record and the metrics ────────────────────────────────────

    [Fact]
    public async Task ReviewPullRequestAsync_GivenAFinishedReview_Should_LogOneRecordWithNoDiffOrModelText()
    {
        // Arrange — every piece of text the author or the model wrote is a sentinel the record must not hold
        var harness = new ReviewOrchestratorHarness
        {
            Files = [new PullRequestFile("src/Foo.cs", "modified", 1, 0, "@@ -1,1 +1,2 @@\n a\n+DIFF-SENTINEL")],
            ClaudeHandler = (_, _, _) => Task.FromResult(ReviewOrchestratorHarness.Completed(
                findings: [new ChecklistFinding(CheckId.Functionality, "src/Foo.cs", 2, "ISSUE-SENTINEL", "FIX-SENTINEL")],
                summary: "SUMMARY-SENTINEL",
                note: "NOTE-SENTINEL",
                coverageNote: "COVERAGE-SENTINEL"))
        };
        harness.PullRequest = harness.PullRequest with { Title = "TITLE-SENTINEL", Body = "BODY-SENTINEL" };

        // Act
        await harness.RunAsync();

        // Assert
        var record = harness.Logger.Entries.Where(entry => entry.Message.StartsWith(RecordPrefix, StringComparison.Ordinal))
            .Should().ContainSingle().Subject;
        record.Level.Should().Be(LogLevel.Information);
        record.Message.Should().Contain(HeadSha);
        Regex.Matches(record.Message, Regex.Escape("standards-design=pass functionality=concern performance=pass")).Should().HaveCount(2, "both runs' statuses are listed");
        record.Message.Should().NotContain("SENTINEL");
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenAReviewThatFailed_Should_LogOneRecordAsReviewFailed()
    {
        // Arrange
        var harness = new ReviewOrchestratorHarness
        {
            ClaudeHandler = (_, _, _) => Task.FromResult(ReviewOrchestratorHarness.Ended(RunOutcome.Refused))
        };

        // Act
        await harness.RunAsync();

        // Assert
        var record = harness.Logger.Entries.Where(entry => entry.Message.StartsWith(RecordPrefix, StringComparison.Ordinal))
            .Should().ContainSingle().Subject;
        record.Message.Should().Contain("review_failed").And.Contain("Refused");
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenAReviewThatWasNotPublished_Should_LogNoRecord()
    {
        // Arrange — a review discarded because the commit moved on is not a finished review; the newer commit's review is
        var harness = new ReviewOrchestratorHarness();
        harness.PullRequestAfterModel = harness.PullRequest with { HeadSha = NewerSha };

        // Act
        await harness.RunAsync();

        // Assert
        harness.Logger.Entries.Should().NotContain(entry => entry.Message.StartsWith(RecordPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenAFinishedReview_Should_CountTheModelChoiceCheckStatusesSeriousFlagsCostAndCoverage()
    {
        // Arrange — each run used 1,000 input and 500 output tokens at Sonnet's $2 and $10 per million: $0.007, twice. One file of the pull
        // request's +10 -2 is reviewed; the other is a lockfile, never read, so the review is not partial.
        using var metrics = new MetricsRecorder();
        var harness = new ReviewOrchestratorHarness
        {
            ClaudeHandler = (_, _, _) => Task.FromResult(ReviewOrchestratorHarness.Completed(serious: [CheckId.Functionality]))
        };

        // Act
        await harness.RunAsync();

        // Assert
        metrics.Sum("github_review_app.review.model_choice", ("tier", "sonnet"), ("reason", "small_change")).Should().Be(1);
        metrics.Sum("github_review_app.review.check_status", ("check", "functionality"), ("status", "concern")).Should().Be(1);
        metrics.Sum("github_review_app.review.check_status", ("check", "security"), ("status", "pass")).Should().Be(1);
        metrics.Sum("github_review_app.review.check_status").Should().Be(8);
        metrics.Sum("github_review_app.review.serious").Should().Be(1);
        metrics.Sum("github_review_app.review.cost", ("tier", "sonnet")).Should().Be(0.014);
        metrics.Sum("github_review_app.review.disagreements").Should().Be(0);
        metrics.Sum("github_review_app.files.reviewed").Should().Be(1);
        metrics.Sum("github_review_app.pull_request.changed_lines").Should().Be(12);
        metrics.Has("github_review_app.reviews.partial").Should().BeFalse();
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenRunsThatDisagree_Should_CountTheDisagreementInChecksAndInVerdict()
    {
        // Arrange — the second run fails Security, so the runs differ on a check and on their own verdicts
        using var metrics = new MetricsRecorder();
        var harness = new ReviewOrchestratorHarness
        {
            ClaudeHandler = (index, _, _) => Task.FromResult(index == 0
                ? ReviewOrchestratorHarness.Completed()
                : ReviewOrchestratorHarness.Completed([(CheckId.Security, CheckStatus.Fail)]))
        };

        // Act
        var result = await harness.RunAsync();

        // Assert
        result.Verdict.Should().Be(ReviewVerdict.Fail);
        metrics.Sum("github_review_app.review.disagreements", ("kind", "checks")).Should().Be(1);
        metrics.Sum("github_review_app.review.disagreements", ("kind", "verdict")).Should().Be(1);
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenAModelWithNoPrice_Should_RecordNoCostAndWarnWithTheModelId()
    {
        // Arrange — a guess would read as the real cost
        using var metrics = new MetricsRecorder();
        var harness = new ReviewOrchestratorHarness
        {
            ClaudeHandler = (_, _, _) => Task.FromResult(ReviewOrchestratorHarness.Completed(model: "claude-unpriced-1"))
        };

        // Act
        await harness.RunAsync();

        // Assert
        metrics.Has("github_review_app.review.cost").Should().BeFalse();
        harness.Logger.Entries.Should().Contain(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("claude-unpriced-1"));
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenASkippedReview_Should_CountItWithTheReasonAsATag()
    {
        // Arrange
        using var metrics = new MetricsRecorder();
        var harness = new ReviewOrchestratorHarness { Files = [] };

        // Act
        await harness.RunAsync();

        // Assert
        metrics.Sum("github_review_app.reviews.skipped", ("reason", "NothingToReview")).Should().Be(1);
    }

    // ── Time and cancellation ────────────────────────────────────────────────

    [Fact]
    public async Task ReviewPullRequestAsync_GivenAReviewThatOutlivesItsLimit_Should_ThrowATimeout()
    {
        // Arrange — with no limit of its own the review would run until the Functions host gave up, which restarts the worker and every review on it
        var harness = new ReviewOrchestratorHarness { Options = new ReviewOptions { MaxReviewDurationSeconds = 600 } };
        var modelStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var modelWasCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.ClaudeHandler = async (_, _, cancellationToken) =>
        {
            modelStarted.TrySetResult();
            await Stall.UntilCancelledAsync(cancellationToken, modelWasCancelled);

            return ReviewOrchestratorHarness.Completed();
        };

        // Act
        var run = harness.RunAsync();
        await modelStarted.Task.WaitAsync(_hangGuard);
        harness.Time.Advance(TimeSpan.FromSeconds(601));
        var act = () => run.WaitAsync(_hangGuard);

        // Assert
        var failure = (await act.Should().ThrowAsync<TimeoutException>()).Which;
        failure.Message.Should().Contain("600-second");
        FailureClassifier.IsTransient(failure).Should().BeTrue();
        modelWasCancelled.Task.IsCompletedSuccessfully.Should().BeTrue("the request is abandoned, not left running");
        ShouldHavePublishedNothing(harness);
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenOneRunFinishedBeforeTheLimitRanOut_Should_KeepItForTheRetry()
    {
        // Arrange — the guide promises that a run which finished before the deadline is not paid for again by the retry on the same instance
        var harness = new ReviewOrchestratorHarness { Options = new ReviewOptions { MaxReviewDurationSeconds = 600 } };
        var firstRunAnswered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRunStalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.ClaudeHandler = async (index, _, cancellationToken) =>
        {
            (index == 0 ? firstRunAnswered : secondRunStalled).TrySetResult();
            await (index == 0 ? Task.CompletedTask : Stall.UntilCancelledAsync(cancellationToken));

            return ReviewOrchestratorHarness.Completed();
        };

        // Act
        var firstAttempt = harness.RunAsync();
        await Task.WhenAll(firstRunAnswered.Task, secondRunStalled.Task).WaitAsync(_hangGuard);
        harness.Time.Advance(TimeSpan.FromSeconds(601));
        var act = () => firstAttempt.WaitAsync(_hangGuard);
        await act.Should().ThrowAsync<TimeoutException>();
        harness.ClaudeHandler = (_, _, _) => Task.FromResult(ReviewOrchestratorHarness.Completed());
        var retry = await harness.RunAsync();

        // Assert
        harness.ClaudeRequests.Should().HaveCount(3, "the first attempt made two calls and the retry paid only for the run that had not finished");
        retry.Status.Should().Be(ReviewRunStatus.Posted);
        retry.RunCount.Should().Be(2);
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenTheHostCancelsWhileTheModelWorks_Should_ThrowOperationCanceledAndPublishNothing()
    {
        // Arrange — a host shutdown is not a failure of the run, so it is not caught as one
        var harness = new ReviewOrchestratorHarness();
        using var shutdown = new CancellationTokenSource();
        var modelStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.ClaudeHandler = async (_, _, cancellationToken) =>
        {
            modelStarted.TrySetResult();
            await Stall.UntilCancelledAsync(cancellationToken);

            return ReviewOrchestratorHarness.Completed();
        };

        // Act
        var run = harness.RunAsync(cancellationToken: shutdown.Token);
        await modelStarted.Task.WaitAsync(_hangGuard);
        await shutdown.CancelAsync();
        var act = () => run.WaitAsync(_hangGuard);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        ShouldHavePublishedNothing(harness);
    }
}
