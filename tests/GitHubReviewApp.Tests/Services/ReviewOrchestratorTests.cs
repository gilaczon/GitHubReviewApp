namespace GitHubReviewApp.Tests.Services;

[Collection(TelemetryCollection.Name)]
public class ReviewOrchestratorTests
{
    private const string Owner = ReviewOrchestratorHarness.Owner;
    private const string Repo = ReviewOrchestratorHarness.Repo;
    private const string HeadSha = ReviewOrchestratorHarness.HeadSha;
    private const string BaseSha = ReviewOrchestratorHarness.BaseSha;
    private const string AppLogin = ReviewOrchestratorHarness.AppLogin;
    private const string EarlierSha = "1111111111111111111111111111111111111111";

    // A run that hangs must fail the test, not hang the suite.
    private static readonly TimeSpan _hangGuard = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The reads of the pull request in the test where a part fails while another finds the commit replaced: the first two are ordinary, and the third
    /// does not answer until the failure has happened, and then says the commit has moved.
    /// </summary>
    private sealed class PullRequestReadsThatEndInANewerCommit
    {
        private readonly PullRequestInfo _current;
        private readonly PullRequestInfo _newer;
        private int _reads;

        public PullRequestReadsThatEndInANewerCommit(PullRequestInfo current, PullRequestInfo newer)
        {
            _current = current;
            _newer = newer;
        }

        public TaskCompletionSource ThirdReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FailureHappened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<PullRequestInfo> ReadAsync(string owner, string repo, int prNumber, string token, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _reads) < 3)
            {
                return _current;
            }

            ThirdReadStarted.SetResult();
            await FailureHappened.Task;
            await Settle.QueuedWorkAsync();

            return _newer;
        }
    }

    /// <summary>
    /// Part B fails for a reason that can clear up (a dropped connection), but only once both other parts are in the middle of their model call, so the
    /// failure always finds them running. They only finish when the test lets them, and they report it if they are cancelled first.
    /// </summary>
    private sealed class PartBFailsWhileTheOthersAreWritten
    {
        private int _othersStarted;

        public bool IsPartBFailing { get; set; } = true;

        public TaskCompletionSource BothOthersStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource PartBHasFailed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource OthersMayFinish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AnotherPartWasCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ClaudeReviewResult> HandleAsync(ClaudeReviewRequest request, CancellationToken cancellationToken)
        {
            if (IsPartBFailing && request.UserMessage.Contains("src/B.cs", StringComparison.Ordinal))
            {
                await BothOthersStarted.Task.WaitAsync(cancellationToken);
                PartBHasFailed.SetResult();
                throw new HttpRequestException("connection reset");
            }

            if (Interlocked.Increment(ref _othersStarted) == 2)
            {
                BothOthersStarted.TrySetResult();
            }

            await Stall.UntilAsync(OthersMayFinish.Task, cancellationToken, AnotherPartWasCancelled);

            return ReviewOrchestratorHarness.DefaultReply();
        }
    }

    /// <summary>Part B stalls until it is cancelled. Part A (and any other part) waits for B to be running, then finishes.</summary>
    private sealed class PartAFinishesWhilePartBIsStillRunning
    {
        public TaskCompletionSource PartBStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource PartBWasCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ClaudeReviewResult> HandleAsync(ClaudeReviewRequest request, CancellationToken cancellationToken)
        {
            if (request.UserMessage.Contains("src/B.cs", StringComparison.Ordinal))
            {
                PartBStarted.SetResult();
                await Stall.UntilCancelledAsync(cancellationToken, PartBWasCancelled);
            }
            else
            {
                await PartBStarted.Task.WaitAsync(cancellationToken);
            }

            return ReviewOrchestratorHarness.DefaultReply();
        }
    }

    /// <summary>The first model request fails with a dropped connection, as one does on a bad day; every later one answers.</summary>
    private sealed class ModelFailsOnceThenAnswers
    {
        private int _requests;

        public Task<ClaudeReviewResult> HandleAsync(ClaudeReviewRequest request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _requests) == 1)
            {
                throw new HttpRequestException("connection reset");
            }

            return Task.FromResult(ReviewOrchestratorHarness.DefaultReply());
        }
    }

    private static PullRequestInfo WithHead(PullRequestInfo source, string headSha)
    {
        return source with { HeadSha = headSha };
    }

    // ── Happy path ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ReviewPullRequestAsync_GivenAnOpenPullRequest_Should_RequestATokenScopedToTheMessagesRepository()
    {
        // Arrange
        var harness = new ReviewOrchestratorHarness();

        // Act
        await harness.RunAsync();

        // Assert
        harness.Auth.Verify(a => a.GetInstallationTokenAsync(ReviewOrchestratorHarness.InstallationId, Repo, It.IsAny<CancellationToken>()), Times.AtLeastOnce());
        harness.Auth.Verify(a => a.GetInstallationTokenAsync(It.IsAny<long>(), It.Is<string>(repo => repo != Repo), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenAnOpenPullRequest_Should_PostOneReviewTiedToTheHeadCommitWithTheMarker()
    {
        // Arrange
        var harness = new ReviewOrchestratorHarness();

        // Act
        var result = await harness.RunAsync();

        // Assert
        var submission = harness.PostedReviews.Should().ContainSingle().Subject;
        submission.CommitId.Should().Be(HeadSha);
        ReviewMarker.IsAtEndOf(submission.Body, HeadSha).Should().BeTrue();
        result.Should().Be(ReviewRunResult.Posted(filesReviewed: 1, findingCount: 1, isPartial: false));
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenAFindingOnAChangedLine_Should_PostItAsAnInlineComment()
    {
        // Arrange
        var harness = new ReviewOrchestratorHarness();

        // Act
        await harness.RunAsync();

        // Assert
        var comment = harness.PostedReviews.Single().Comments.Should().ContainSingle().Subject;
        comment.Path.Should().Be("src/Foo.cs");
        comment.Line.Should().Be(11);
        comment.Body.Should().Contain("Null dereference");
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenALockfileInThePullRequest_Should_SayWhichFilesWereNotReviewedInTheReviewBody()
    {
        // Arrange
        var harness = new ReviewOrchestratorHarness();

        // Act
        await harness.RunAsync();

        // Assert
        var body = harness.PostedReviews.Single().Body;
        body.Should().Contain("reviewed 1 of 2 changed files").And.Contain("`package-lock.json`");
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenAStaleQueuedTitle_Should_SendTheTitleFromTheApiNotTheQueuedOne()
    {
        // Arrange
        var harness = new ReviewOrchestratorHarness();

        // Act
        await harness.RunAsync();

        // Assert
        var userMessage = harness.ClaudeRequests.Single().UserMessage;
        userMessage.Should().Contain("Fresh API title").And.NotContain("stale queued title");
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenALockfileInThePullRequest_Should_SendOnlyReviewableFilesToTheModel()
    {
        // Arrange
        var harness = new ReviewOrchestratorHarness();

        // Act
        await harness.RunAsync();

        // Assert
        var userMessage = harness.ClaudeRequests.Single().UserMessage;
        userMessage.Should().Contain("<file path=\"src/Foo.cs\"").And.NotContain("package-lock.json");
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenGuidelines_Should_ReadThemFromTheBaseCommitAndSendThemWithEveryRequest()
    {
        // Arrange
        var harness = new ReviewOrchestratorHarness { Guidelines = "Always use _camelCase for private fields." };

        // Act
        await harness.RunAsync();

        // Assert
        harness.GitHub.Verify(g => g.GetRepositoryGuidelinesAsync(Owner, Repo, BaseSha, ReviewOrchestratorHarness.Token, It.IsAny<CancellationToken>()), Times.Once());
        harness.ClaudeRequests.Single().SystemPrompt.Should().Contain("<repository_guidelines>").And.Contain("Always use _camelCase");
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenNoGuidelines_Should_SendThePromptWithoutAGuidelinesBlock()
    {
        // Arrange
        var harness = new ReviewOrchestratorHarness { Guidelines = null };

        // Act
        await harness.RunAsync();

        // Assert
        harness.ClaudeRequests.Single().SystemPrompt.Should().NotContain("<repository_guidelines>");
    }

    // ── Skipping ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReviewPullRequestAsync_GivenAClosedPullRequest_Should_SkipWithoutCallingClaudeOrPosting()
    {
        // Arrange
        var harness = new ReviewOrchestratorHarness { PullRequest = new("closed", false, "t", HeadSha, BaseSha, 1, 1, 1) };

        // Act
        var result = await harness.RunAsync();

        // Assert
        result.Should().Be(ReviewRunResult.Skipped(ReviewSkipReason.NotOpen));
        harness.ClaudeRequests.Should().BeEmpty();
        harness.PostedReviews.Should().BeEmpty();
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenADraftPullRequest_Should_Skip()
    {
        // Arrange
        var harness = new ReviewOrchestratorHarness { PullRequest = new("open", true, "t", HeadSha, BaseSha, 1, 1, 1) };

        // Act
        var result = await harness.RunAsync();

        // Assert
        result.Should().Be(ReviewRunResult.Skipped(ReviewSkipReason.Draft));
        harness.ClaudeRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenTheHeadMovedSinceTheEvent_Should_SkipBecauseANewerReviewIsQueued()
    {
        // Arrange
        var harness = new ReviewOrchestratorHarness();
        harness.PullRequest = WithHead(harness.PullRequest, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

        // Act
        var result = await harness.RunAsync();

        // Assert
        result.Should().Be(ReviewRunResult.Skipped(ReviewSkipReason.Superseded));
        harness.ClaudeRequests.Should().BeEmpty();
        harness.GitHub.Verify(g => g.GetPullRequestFilesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    private static PullRequestReviewSummary OwnReview(string body, string commitId = HeadSha, string state = "COMMENTED", long id = 1)
    {
        return new PullRequestReviewSummary(id, state, body, commitId, AppLogin);
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenThisAppsReviewAlreadyEndsWithTheMarkerForThisCommit_Should_SkipAsAlreadyReviewed()
    {
        // Arrange — a redelivered webhook or a retry after a successful post
        var harness = new ReviewOrchestratorHarness
        {
            Reviews = [OwnReview("text\n" + ReviewMarker.For(HeadSha))]
        };

        // Act
        var result = await harness.RunAsync();

        // Assert
        result.Should().Be(ReviewRunResult.Skipped(ReviewSkipReason.AlreadyReviewed));
        harness.ClaudeRequests.Should().BeEmpty();
        harness.PostedReviews.Should().BeEmpty();
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenAHumanReviewContainingTheMarker_Should_StillReviewSoTheMarkerCannotBeForgedToSuppressReviews()
    {
        // Arrange
        var harness = new ReviewOrchestratorHarness
        {
            Reviews = [new PullRequestReviewSummary(1, "COMMENTED", ReviewMarker.For(HeadSha), HeadSha, "octocat")]
        };

        // Act
        var result = await harness.RunAsync();

        // Assert
        result.Status.Should().Be(ReviewRunStatus.Posted);
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenAnotherBotsReviewEndsWithTheMarker_Should_StillReview()
    {
        // Arrange — "a bot" is not "this app": a second environment of the reviewer, or any other bot, must not stand in for it
        var harness = new ReviewOrchestratorHarness
        {
            Reviews = [new PullRequestReviewSummary(1, "COMMENTED", ReviewMarker.For(HeadSha), HeadSha, "other-reviewer[bot]")]
        };

        // Act
        var result = await harness.RunAsync();

        // Assert
        result.Status.Should().Be(ReviewRunStatus.Posted);
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenThisAppsReviewOfAnEarlierCommitQuotingThisCommitsMarker_Should_StillReviewIt()
    {
        // Arrange — the forgery: an earlier review of this app lists a file named like the marker of a commit about to be force-pushed
        var forged = $"Not reviewed: `x{ReviewMarker.For(HeadSha)}.png`\n\n{ReviewMarker.For(EarlierSha)}";
        var harness = new ReviewOrchestratorHarness { Reviews = [OwnReview(forged, EarlierSha)] };

        // Act
        var result = await harness.RunAsync();

        // Assert
        result.Status.Should().Be(ReviewRunStatus.Posted);
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenABotReviewForAnEarlierCommit_Should_ReviewTheNewCommit()
    {
        // Arrange
        var harness = new ReviewOrchestratorHarness
        {
            Reviews = [OwnReview(ReviewMarker.For(EarlierSha), EarlierSha)]
        };

        // Act
        var result = await harness.RunAsync();

        // Assert
        result.Status.Should().Be(ReviewRunStatus.Posted);
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenAnotherRunPostedThisCommitWhileTheModelWasWorking_Should_NotPostASecondReview()
    {
        // Arrange — a reopen or a manual redelivery queued a second message for the same commit, and it finished first
        var harness = new ReviewOrchestratorHarness
        {
            Reviews = [],
            ReviewsAfterModel = [OwnReview("done\n" + ReviewMarker.For(HeadSha))]
        };

        // Act
        var result = await harness.RunAsync();

        // Assert
        result.Should().Be(ReviewRunResult.Skipped(ReviewSkipReason.AlreadyReviewed));
        harness.PostedReviews.Should().BeEmpty();
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenEveryFileIsALockfileOrABinary_Should_PostACoverageReportWithoutCallingTheModel()
    {
        // Arrange — silence would look exactly like an app that is down
        var harness = new ReviewOrchestratorHarness
        {
            Files = [ReviewOrchestratorHarness.ModifiedFile("package-lock.json", 5), ReviewOrchestratorHarness.ModifiedFile("assets/logo.png", 5)]
        };

        // Act
        var result = await harness.RunAsync();

        // Assert
        result.Should().Be(ReviewRunResult.Posted(filesReviewed: 0, findingCount: 0, isPartial: true));
        harness.ClaudeRequests.Should().BeEmpty();
        harness.GitHub.Verify(g => g.GetRepositoryGuidelinesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());

        var body = harness.PostedReviews.Should().ContainSingle().Subject.Body;
        body.Should().Contain("The AI reviewer did not read any of the changed files.").And.Contain("No AI model was run.");
        body.Should().NotContain("found no issues");
        ReviewMarker.IsAtEndOf(body, HeadSha).Should().BeTrue("a redelivery must not post the same report again");
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenEveryFileWasDeleted_Should_SkipAsNothingToReviewBecauseThereIsNoNewCode()
    {
        // Arrange
        var removed = ReviewOrchestratorHarness.ModifiedFile("src/Old.cs", 5) with { Status = "removed" };
        var harness = new ReviewOrchestratorHarness { Files = [removed] };

        // Act
        var result = await harness.RunAsync();

        // Assert
        result.Should().Be(ReviewRunResult.Skipped(ReviewSkipReason.NothingToReview));
        harness.PostedReviews.Should().BeEmpty();
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenAPullRequestWithNoFilesAtAll_Should_SkipAsNothingToReview()
    {
        // Arrange
        var harness = new ReviewOrchestratorHarness { Files = [] };

        // Act
        var result = await harness.RunAsync();

        // Assert
        result.Should().Be(ReviewRunResult.Skipped(ReviewSkipReason.NothingToReview));
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenAGeneratedFileInThePullRequest_Should_SendItToTheModelToo()
    {
        // Arrange — generated and vendored code still compiles and runs, so it is demoted, not dropped
        var harness = new ReviewOrchestratorHarness
        {
            Files = [ReviewOrchestratorHarness.ModifiedFile("src/Real.cs", 5), ReviewOrchestratorHarness.ModifiedFile("Data/Init.Designer.cs", 5)]
        };

        // Act
        await harness.RunAsync();

        // Assert
        var userMessage = harness.ClaudeRequests.Single().UserMessage;
        userMessage.Should().Contain("<file path=\"src/Real.cs\"").And.Contain("<file path=\"Data/Init.Designer.cs\"");
    }

    // ── Pending drafts ───────────────────────────────────────────────────────

    [Fact]
    public async Task ReviewPullRequestAsync_GivenAStalePendingReview_Should_DeleteItBeforePostingTheNewOne()
    {
        // Arrange
        var harness = new ReviewOrchestratorHarness
        {
            Reviews =
            [
                OwnReview(string.Empty, commitId: string.Empty, state: "PENDING", id: 41),
                new PullRequestReviewSummary(42, "COMMENTED", "old", "x", "octocat")
            ]
        };

        // Act
        await harness.RunAsync();

        // Assert
        harness.CallOrder.Should().Equal("delete:41", "post");
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenSomeoneElsesPendingReview_Should_NotDeleteIt()
    {
        // Arrange — a draft by a person or another bot is never this app's to delete
        var harness = new ReviewOrchestratorHarness
        {
            Reviews =
            [
                new PullRequestReviewSummary(51, "PENDING", string.Empty, string.Empty, "octocat"),
                new PullRequestReviewSummary(52, "PENDING", string.Empty, string.Empty, "other-reviewer[bot]")
            ]
        };

        // Act
        await harness.RunAsync();

        // Assert
        harness.CallOrder.Should().Equal("post");
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenADraftAppearsWhileTheModelIsWorking_Should_DeleteItFromTheFreshList()
    {
        // Arrange
        var harness = new ReviewOrchestratorHarness
        {
            Reviews = [],
            ReviewsAfterModel = [OwnReview(string.Empty, commitId: string.Empty, state: "PENDING", id: 61)]
        };

        // Act
        await harness.RunAsync();

        // Assert
        harness.CallOrder.Should().Equal("delete:61", "post");
    }

    // ── Races ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReviewPullRequestAsync_GivenTheHeadMovesWhileTheModelIsReviewing_Should_DiscardTheReview()
    {
        // Arrange
        var harness = new ReviewOrchestratorHarness();
        harness.PullRequestAfterReview = WithHead(harness.PullRequest, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

        // Act
        var result = await harness.RunAsync();

        // Assert
        result.Should().Be(ReviewRunResult.Skipped(ReviewSkipReason.Superseded));
        harness.ClaudeRequests.Should().HaveCount(1, "the review was generated but must not be posted");
        harness.PostedReviews.Should().BeEmpty();
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenThePullRequestClosesWhileTheModelIsReviewing_Should_NotPost()
    {
        // Arrange
        var harness = new ReviewOrchestratorHarness();
        harness.PullRequestAfterReview = harness.PullRequest with { State = "closed" };

        // Act
        var result = await harness.RunAsync();

        // Assert
        result.Should().Be(ReviewRunResult.Skipped(ReviewSkipReason.NotOpen));
        harness.PostedReviews.Should().BeEmpty();
    }

    // ── Large pull requests ──────────────────────────────────────────────────

    private static ReviewOrchestratorHarness HarnessWithThreeChunks(Action<ReviewOptions>? _ = null, int maxParallelChunks = 3, int maxConcurrentModelRequests = 6)
    {
        var files = new[] { "src/A.cs", "src/B.cs", "src/C.cs" }.Select(path => ReviewOrchestratorHarness.ModifiedFile(path)).ToList();
        var oneFileBytes = DiffRenderer.Render(files[0], int.MaxValue).RenderedDiff.Length;

        return new ReviewOrchestratorHarness
        {
            Files = files,
            PullRequest = new("open", false, "Big change", HeadSha, BaseSha, 3, 300, 0),
            Options = new ReviewOptions
            {
                MaxChunkBytes = oneFileBytes + 50,
                MaxParallelChunks = maxParallelChunks,
                MaxConcurrentModelRequests = maxConcurrentModelRequests
            }
        };
    }

    private static async Task<ClaudeReviewResult> RejectPartBAndStallTheOthersAsync(ClaudeReviewRequest request, CancellationToken cancellationToken)
    {
        if (request.UserMessage.Contains("src/B.cs", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("the model rejected this part");
        }

        await Stall.UntilCancelledAsync(cancellationToken);

        return ReviewOrchestratorHarness.DefaultReply();
    }

    private static Task<ClaudeReviewResult> Completed(string summary = "ok")
    {
        return Task.FromResult(new ClaudeReviewResult(ChunkOutcome.Completed, summary, [], "claude-test", ClaudeUsage.Empty));
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenFilesThatDoNotFitOneRequest_Should_SendOneRequestPerChunkNumberedByPart()
    {
        // Arrange
        var harness = HarnessWithThreeChunks();
        harness.ClaudeHandler = (_, _) => Completed();

        // Act
        var result = await harness.RunAsync();

        // Assert
        harness.ClaudeRequests.Should().HaveCount(3);
        harness.ClaudeRequests.Select(r => r.UserMessage).Should().Contain(m => m.Contains("part 1 of 3"))
            .And.Contain(m => m.Contains("part 2 of 3")).And.Contain(m => m.Contains("part 3 of 3"));
        harness.PostedReviews.Should().ContainSingle();
        result.FilesReviewed.Should().Be(3);
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenSeveralRequests_Should_MergeTheirFindingsIntoOneReview()
    {
        // Arrange
        var harness = HarnessWithThreeChunks();
        harness.ClaudeHandler = (request, _) =>
        {
            var path = request.UserMessage.Contains("src/A.cs") ? "src/A.cs" : request.UserMessage.Contains("src/B.cs") ? "src/B.cs" : "src/C.cs";
            var finding = new ReviewFinding(path, 1, ReviewSeverity.Medium, "bug", $"Problem in {path}", "Details.");
            return Task.FromResult(new ClaudeReviewResult(ChunkOutcome.Completed, $"Part for {path}.", [finding], "claude-test", ClaudeUsage.Empty));
        };

        // Act
        var result = await harness.RunAsync();

        // Assert
        result.FindingCount.Should().Be(3);
        var submission = harness.PostedReviews.Single();
        submission.Comments.Select(c => c.Path).Should().BeEquivalentTo("src/A.cs", "src/B.cs", "src/C.cs");
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenManyRequests_Should_RunExactlyAsManyAtOnceAsTheConfiguredParallelism()
    {
        // Arrange — the first two requests wait for each other, so they only finish if they really overlap
        var files = Enumerable.Range(1, 6).Select(i => ReviewOrchestratorHarness.ModifiedFile($"src/File{i}.cs")).ToList();
        var oneFileBytes = DiffRenderer.Render(files[0], int.MaxValue).RenderedDiff.Length;
        var harness = new ReviewOrchestratorHarness
        {
            Files = files,
            PullRequest = new("open", false, "Big", HeadSha, BaseSha, 6, 600, 0),
            Options = new ReviewOptions { MaxChunkBytes = oneFileBytes + 50, MaxParallelChunks = 2 }
        };

        var probe = new ConcurrencyProbe(arrivalsNeeded: 2);
        harness.ClaudeHandler = async (_, cancellationToken) =>
        {
            await probe.EnterAndWaitForPeersAsync(cancellationToken);

            return await Completed().WaitAsync(cancellationToken);
        };

        // Act
        await harness.RunAsync();

        // Assert
        harness.ClaudeRequests.Should().HaveCount(6);
        probe.MaxInFlight.Should().Be(2);
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenASmallProcessWideLimit_Should_NeverRunMoreRequestsAtOnceThanTheLimitEvenWithMoreParallelism()
    {
        // Arrange — the limit is shared by every review this process runs, so it must hold even when one review asks for more parallelism
        var files = Enumerable.Range(1, 6).Select(i => ReviewOrchestratorHarness.ModifiedFile($"src/File{i}.cs")).ToList();
        var oneFileBytes = DiffRenderer.Render(files[0], int.MaxValue).RenderedDiff.Length;
        var harness = new ReviewOrchestratorHarness
        {
            Files = files,
            PullRequest = new("open", false, "Big", HeadSha, BaseSha, 6, 600, 0),
            Options = new ReviewOptions { MaxChunkBytes = oneFileBytes + 50, MaxParallelChunks = 6, MaxConcurrentModelRequests = 2 }
        };

        var probe = new ConcurrencyProbe(arrivalsNeeded: 2);
        harness.ClaudeHandler = async (_, cancellationToken) =>
        {
            await probe.EnterAndWaitForPeersAsync(cancellationToken);

            return await Completed().WaitAsync(cancellationToken);
        };

        // Act
        await harness.RunAsync();

        // Assert
        harness.ClaudeRequests.Should().HaveCount(6);
        probe.MaxInFlight.Should().Be(2);
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenOneRequestHitTheOutputLimit_Should_PostTheRestAndSayWhatWasMissed()
    {
        // Arrange
        var harness = HarnessWithThreeChunks();
        harness.ClaudeHandler = (request, _) => Task.FromResult(
            request.UserMessage.Contains("part 2 of 3")
                ? new ClaudeReviewResult(ChunkOutcome.OutputTruncated, string.Empty, [], "claude-test", ClaudeUsage.Empty, "output limit reached")
                : new ClaudeReviewResult(ChunkOutcome.Completed, "ok", [], "claude-test", ClaudeUsage.Empty));

        // Act
        var result = await harness.RunAsync();

        // Assert
        result.IsPartial.Should().BeTrue();
        result.FilesReviewed.Should().Be(2);
        var body = harness.PostedReviews.Single().Body;
        body.Should().Contain("Part 2 of 3").And.Contain("the model's output hit the length limit").And.Contain("`src/B.cs`");
        body.Should().NotContain("found no issues in the changes it reviewed");
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenOnePartIsRejectedByTheAiService_Should_PostTheOtherPartsAndNameTheRejectedOne()
    {
        // Arrange
        var harness = HarnessWithThreeChunks();
        harness.ClaudeHandler = (request, _) => Task.FromResult(
            request.UserMessage.Contains("part 3 of 3")
                ? new ClaudeReviewResult(ChunkOutcome.Rejected, string.Empty, [], "claude-test", ClaudeUsage.Empty, "400")
                : new ClaudeReviewResult(ChunkOutcome.Completed, "ok", [], "claude-test", ClaudeUsage.Empty));

        // Act
        var result = await harness.RunAsync();

        // Assert
        result.FilesReviewed.Should().Be(2);
        harness.PostedReviews.Single().Body.Should().Contain("Part 3 of 3").And.Contain("the AI service rejected this part of the request");
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenEveryPartIsRejectedByTheAiService_Should_ThrowSoItIsReportedAsAFailureNotPostedAsAReviewThatReadNothing()
    {
        // Arrange — a bad parameter, a wrong model name or an empty account refuses every part alike
        var harness = HarnessWithThreeChunks();
        harness.ClaudeHandler = (_, _) => Task.FromResult(
            new ClaudeReviewResult(ChunkOutcome.Rejected, string.Empty, [], string.Empty, ClaudeUsage.Empty, "HTTP 400: credit balance is too low"));

        // Act
        var act = () => harness.RunAsync();

        // Assert
        await act.Should().ThrowAsync<AiServiceRejectedRequestException>().WithMessage("*all 3*credit balance is too low*");
        harness.PostedReviews.Should().BeEmpty();
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenTheOnlyPartIsRejectedByTheAiService_Should_ThrowForTheSameReason()
    {
        // Arrange
        var harness = new ReviewOrchestratorHarness
        {
            ClaudeHandler = (_, _) => Task.FromResult(
                new ClaudeReviewResult(ChunkOutcome.Rejected, string.Empty, [], string.Empty, ClaudeUsage.Empty, "HTTP 413"))
        };

        // Act
        var act = () => harness.RunAsync();

        // Assert
        await act.Should().ThrowAsync<AiServiceRejectedRequestException>().WithMessage("*all 1*HTTP 413*");
        harness.PostedReviews.Should().BeEmpty();
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenARejectedPartAndATruncatedOne_Should_StillPostBecauseNotEveryPartWasRefused()
    {
        // Arrange — only a part that the service refused outright counts; a truncated reply is a different, partial outcome
        var harness = HarnessWithThreeChunks();
        harness.ClaudeHandler = (request, _) => Task.FromResult(
            request.UserMessage.Contains("part 1 of 3")
                ? new ClaudeReviewResult(ChunkOutcome.Rejected, string.Empty, [], string.Empty, ClaudeUsage.Empty, "HTTP 400")
                : new ClaudeReviewResult(ChunkOutcome.OutputTruncated, string.Empty, [], "claude-test", ClaudeUsage.Empty, "output limit reached"));

        // Act
        var result = await harness.RunAsync();

        // Assert
        result.Status.Should().Be(ReviewRunStatus.Posted);
        harness.PostedReviews.Should().ContainSingle();
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenTheHeadMovesAfterTheFirstPart_Should_NotStartTheRemainingPartsAndSkipAsSuperseded()
    {
        // Arrange — one part at a time, so the order is fixed: the head is read before each part
        var harness = HarnessWithThreeChunks(maxParallelChunks: 1);
        harness.ClaudeHandler = (_, _) => Completed();
        var moved = harness.PullRequest with { HeadSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" };
        harness.PullRequestByCall = call => call >= 3 ? moved : null;

        // Act
        var result = await harness.RunAsync();

        // Assert
        result.Should().Be(ReviewRunResult.Skipped(ReviewSkipReason.Superseded));
        harness.ClaudeRequests.Should().ContainSingle("the second and third parts were never started");
        harness.PostedReviews.Should().BeEmpty();
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenASingleChunkReview_Should_NotReadThePullRequestAgainBeforeTheModelRuns()
    {
        // Arrange
        var harness = new ReviewOrchestratorHarness();

        // Act
        await harness.RunAsync();

        // Assert — the initial read and the check after the model, nothing in between
        harness.GitHub.Verify(g => g.GetPullRequestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenOneOfThreePartsFailsForGood_Should_ThrowThatFailureAndNotASiblingsCancellation()
    {
        // Arrange — the other two parts wait to be cancelled; the real fault must be what surfaces, or the queue would retry a failure that cannot clear up
        var harness = HarnessWithThreeChunks();
        harness.ClaudeHandler = RejectPartBAndStallTheOthersAsync;

        // Act
        var act = () => harness.RunAsync().WaitAsync(_hangGuard);

        // Assert
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("rejected this part");
        harness.PostedReviews.Should().BeEmpty();
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenOnePartFailsAndTheMessageIsRetried_Should_NotPayAgainForThePartsThatFinished()
    {
        // Arrange — one part at a time: A finishes, B fails once, then the retry runs
        var harness = HarnessWithThreeChunks(maxParallelChunks: 1);
        var bFailures = 1;
        harness.ClaudeHandler = (request, _) =>
            request.UserMessage.Contains("src/B.cs") && Interlocked.Exchange(ref bFailures, 0) == 1
                ? throw new HttpRequestException("connection reset")
                : Completed("ok");

        // Act
        var firstAttempt = () => harness.RunAsync();
        await firstAttempt.Should().ThrowAsync<HttpRequestException>();
        var retry = await harness.RunAsync();

        // Assert — first attempt: A, then B (failed). Retry: A comes from the cache, so only B and C are sent.
        harness.ClaudeRequests.Should().HaveCount(4);
        harness.ClaudeRequests.Count(request => request.UserMessage.Contains("src/A.cs")).Should().Be(1, "A was finished before the failure");
        retry.Status.Should().Be(ReviewRunStatus.Posted);
        retry.FilesReviewed.Should().Be(3);
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenAPartThatWasNotCompleted_Should_SendItAgainOnARetryBecauseTheSecondTryMayDoBetter()
    {
        // Arrange
        var harness = new ReviewOrchestratorHarness
        {
            ClaudeHandler = (_, _) => Task.FromResult(new ClaudeReviewResult(ChunkOutcome.OutputTruncated, string.Empty, [], "claude-test", ClaudeUsage.Empty))
        };

        // Act
        await harness.RunAsync();
        harness.Reviews = [];
        await harness.RunAsync();

        // Assert
        harness.ClaudeRequests.Should().HaveCount(2);
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenDifferentGuidelinesOnARetry_Should_NotReuseAStaleAnswer()
    {
        // Arrange — the cache key covers the whole prompt, so the repository's rules changing changes the key
        var harness = new ReviewOrchestratorHarness { Guidelines = "Rule one." };

        // Act
        await harness.RunAsync();
        harness.Guidelines = "Rule two.";
        await harness.RunAsync();

        // Assert
        harness.ClaudeRequests.Should().HaveCount(2);
    }

    // ── One run per commit ───────────────────────────────────────────────────

    [Fact]
    public async Task ReviewPullRequestAsync_GivenThisProcessIsAlreadyReviewingTheCommit_Should_SkipAsInProgressWithoutSpendingAnything()
    {
        // Arrange — a reopen or a redelivery queued a second message for a commit that is being reviewed right now
        var harness = new ReviewOrchestratorHarness();
        using var otherRun = harness.InFlight.TryBegin(Owner, Repo, ReviewOrchestratorHarness.PrNumber, HeadSha);

        // Act
        var result = await harness.RunAsync();

        // Assert
        otherRun.Should().NotBeNull();
        result.Should().Be(ReviewRunResult.Skipped(ReviewSkipReason.InProgress));
        harness.ClaudeRequests.Should().BeEmpty();
        harness.PostedReviews.Should().BeEmpty();
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenARunFinishes_Should_ReleaseItsClaimSoALaterMessageCanReviewTheCommitAgain()
    {
        // Arrange
        var harness = new ReviewOrchestratorHarness();

        // Act
        await harness.RunAsync();
        using var claim = harness.InFlight.TryBegin(Owner, Repo, ReviewOrchestratorHarness.PrNumber, HeadSha);

        // Assert
        claim.Should().NotBeNull();
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenARunFails_Should_ReleaseItsClaimSoTheRetryIsNotSkipped()
    {
        // Arrange
        var harness = new ReviewOrchestratorHarness { ClaudeHandler = (_, _) => throw new HttpRequestException("Claude unavailable") };

        // Act
        var act = () => harness.RunAsync();
        await act.Should().ThrowAsync<HttpRequestException>();
        using var claim = harness.InFlight.TryBegin(Owner, Repo, ReviewOrchestratorHarness.PrNumber, HeadSha);

        // Assert
        claim.Should().NotBeNull();
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenADifferentCommitOfTheSamePullRequestIsBeingReviewed_Should_StillReviewThisOne()
    {
        // Arrange
        var harness = new ReviewOrchestratorHarness();
        using var otherCommit = harness.InFlight.TryBegin(Owner, Repo, ReviewOrchestratorHarness.PrNumber, "1111111111111111111111111111111111111111");

        // Act
        var result = await harness.RunAsync();

        // Assert
        result.Status.Should().Be(ReviewRunStatus.Posted);
    }

    // ── Repository guidelines ────────────────────────────────────────────────

    [Fact]
    public async Task ReviewPullRequestAsync_GivenTheAppCannotReadRepositoryContents_Should_NotAskForGuidelinesAndSayTheRulesWereNotApplied()
    {
        // Arrange — GitHub answers "not found" to a hidden file, so a missing file cannot be told from a forbidden one without this signal
        var harness = new ReviewOrchestratorHarness { CanReadContents = false };

        // Act
        await harness.RunAsync();

        // Assert
        harness.GitHub.Verify(g => g.GetRepositoryGuidelinesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
        harness.PostedReviews.Single().Body.Should().Contain("Repository review guidelines were not applied");
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenTheGuidelinesCouldNotBeReadForAnotherReason_Should_SayTheRulesWereNotApplied()
    {
        // Arrange
        var harness = new ReviewOrchestratorHarness { GuidelinesResult = RepositoryGuidelines.Unreadable };

        // Act
        await harness.RunAsync();

        // Assert
        harness.PostedReviews.Single().Body.Should().Contain("Repository review guidelines were not applied");
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenThereAreNoGuidelines_Should_NotMentionThem()
    {
        // Arrange
        var harness = new ReviewOrchestratorHarness { GuidelinesResult = RepositoryGuidelines.None };

        // Act
        await harness.RunAsync();

        // Assert
        harness.PostedReviews.Single().Body.Should().NotContain("guidelines");
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenReadingTheGuidelinesFailsForNow_Should_ThrowBeforeAnyModelCallSoTheMessageIsRetried()
    {
        // Arrange
        var harness = new ReviewOrchestratorHarness();
        harness.GitHub.Setup(g => g.GetRepositoryGuidelinesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("unavailable", HttpStatusCode.ServiceUnavailable, false));

        // Act
        var act = () => harness.RunAsync();

        // Assert
        await act.Should().ThrowAsync<GitHubApiException>();
        harness.ClaudeRequests.Should().BeEmpty();
        harness.PostedReviews.Should().BeEmpty();
    }

    // ── The installation token ───────────────────────────────────────────────

    [Fact]
    public async Task ReviewPullRequestAsync_GivenTheModelTookMinutes_Should_UseAFreshTokenForEveryCallAfterIt()
    {
        // Arrange — the token fetched at the start could expire while the model works
        var harness = new ReviewOrchestratorHarness { TokenAfterModel = "ghs_fetched_after_the_model" };

        // Act
        await harness.RunAsync();

        // Assert
        var beforeModel = harness.UsedTokens.Take(2).Select(use => use.Token);
        var afterModel = harness.UsedTokens.Skip(2).Select(use => use.Token);
        beforeModel.Should().OnlyContain(token => token == ReviewOrchestratorHarness.Token);
        afterModel.Should().NotBeEmpty().And.OnlyContain(token => token == "ghs_fetched_after_the_model");
        harness.UsedTokens.Last().Operation.Should().Be("post");
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenAMultiPartReview_Should_AskForAFreshTokenForEachPartsHeadCheck()
    {
        // Arrange — a part can start many minutes after the first token was fetched, and a cached token is only retired ten minutes before it expires
        var harness = HarnessWithThreeChunks(maxParallelChunks: 1);
        harness.TokenAfterModel = "ghs_fetched_later";
        harness.ClaudeHandler = (_, _) => Completed();

        // Act
        await harness.RunAsync();

        // Assert
        var pullRequestReads = harness.UsedTokens.Where(use => use.Operation == "pull-request").Select(use => use.Token).ToList();
        pullRequestReads.Should().HaveCount(5, "the first look, the check before each of the three parts, and the check before posting");
        pullRequestReads[0].Should().Be(ReviewOrchestratorHarness.Token);
        pullRequestReads.Skip(1).Should().OnlyContain(token => token == "ghs_fetched_later");
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenAPartsHeadCheckCannotBeRead_Should_ReviewThePartAnywayBecauseTheCheckBeforePostingStillRuns()
    {
        // Arrange — the look before a part only saves paying for work that would be thrown away, so it must not fail the part
        var harness = HarnessWithThreeChunks(maxParallelChunks: 1);
        harness.PullRequestByCall = call => call == 2 ? throw new GitHubApiException("bad gateway", HttpStatusCode.BadGateway, false) : null;
        harness.ClaudeHandler = (_, _) => Completed();

        // Act
        var result = await harness.RunAsync();

        // Assert
        result.Status.Should().Be(ReviewRunStatus.Posted);
        harness.ClaudeRequests.Should().HaveCount(3);
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenAPartsHeadCheckTimesOut_Should_ReviewThePartAnywayBecauseTheCheckBeforePostingStillRuns()
    {
        // Arrange — an HttpClient that gives up reports a cancelled task although nobody cancelled anything, and that is not a reason to fail the part
        var harness = HarnessWithThreeChunks(maxParallelChunks: 1);
        harness.PullRequestByCall = call => call == 2
            ? throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 60 seconds elapsing.", new TimeoutException())
            : null;
        harness.ClaudeHandler = (_, _) => Completed();

        // Act
        var result = await harness.RunAsync();

        // Assert
        result.Status.Should().Be(ReviewRunStatus.Posted);
        harness.ClaudeRequests.Should().HaveCount(3);
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenTheCommitIsReplacedWhilePartsWaitForAModelSlot_Should_SendNoRequestForTheOldCommit()
    {
        // Arrange — a wait for a free slot can take minutes when reviews share the instance, and the look at the pull request has to come after it,
        // not before. The test holds the only slot while the parts queue, and replaces the commit while they wait.
        var harness = HarnessWithThreeChunks(maxParallelChunks: 3, maxConcurrentModelRequests: 1);
        var moved = harness.PullRequest with { HeadSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" };
        harness.ClaudeHandler = (_, _) => Completed();
        var heldSlot = await harness.Limiter.AcquireAsync(CancellationToken.None);

        // Act
        var run = harness.RunAsync().WaitAsync(_hangGuard);
        await Settle.QueuedWorkAsync();
        harness.PullRequestByCall = _ => moved;
        heldSlot.Dispose();
        var result = await run;

        // Assert
        result.Should().Be(ReviewRunResult.Skipped(ReviewSkipReason.Superseded));
        harness.ClaudeRequests.Should().BeEmpty("every part was still waiting when the commit was replaced");
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenAPartFailsForAReasonThatCanClearUpWhileOthersWaitForASlot_Should_NotSendTheWaitingParts()
    {
        // Arrange — one slot, three parts: the first holds it until the test lets it fail, and the other two queue behind it
        var harness = HarnessWithThreeChunks(maxParallelChunks: 3, maxConcurrentModelRequests: 1);
        var letItFail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.ClaudeHandler = async (_, cancellationToken) =>
        {
            await letItFail.Task.WaitAsync(cancellationToken);

            throw new HttpRequestException("connection reset");
        };

        // Act
        var run = harness.RunAsync().WaitAsync(_hangGuard);
        await Settle.QueuedWorkAsync();
        letItFail.SetResult();
        var act = () => run;

        // Assert
        await act.Should().ThrowAsync<HttpRequestException>();
        harness.ClaudeRequests.Should().HaveCount(1, "the same rate limit or outage would meet the parts that were waiting, and the retry runs them");
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenAPartFailsForAReasonThatCanClearUpWhileAnotherFindsTheCommitReplaced_Should_SkipAsSupersededInsteadOfRetrying()
    {
        // Arrange — parts A and B run at once. B's look at the pull request is in flight while A's request fails, and it finds a newer commit. A retry
        // would only find the same, so the review is skipped. Pull request reads: the first look, A's check, B's check.
        var harness = HarnessWithThreeChunks(maxParallelChunks: 2);
        var script = new PullRequestReadsThatEndInANewerCommit(harness.PullRequest, harness.PullRequest with { HeadSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" });

        harness.GitHub.Setup(g => g.GetPullRequestAsync(
                ReviewOrchestratorHarness.Owner, ReviewOrchestratorHarness.Repo, ReviewOrchestratorHarness.PrNumber, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, int, string, CancellationToken>(script.ReadAsync);

        harness.ClaudeHandler = async (_, _) =>
        {
            await script.ThirdReadStarted.Task;
            script.FailureHappened.SetResult();

            throw new HttpRequestException("connection reset");
        };

        // Act
        var result = await harness.RunAsync().WaitAsync(_hangGuard);

        // Assert
        result.Should().Be(ReviewRunResult.Skipped(ReviewSkipReason.Superseded));
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenAPartsHeadCheckIsRefusedAs401_Should_ForgetTheTokenSoTheCheckBeforePostingUsesANewOne()
    {
        // Arrange
        var harness = HarnessWithThreeChunks(maxParallelChunks: 1);
        harness.PullRequestByCall = call => call == 2 ? throw new GitHubApiException("Bad credentials", HttpStatusCode.Unauthorized, false) : null;
        harness.ClaudeHandler = (_, _) => Completed();

        // Act
        var result = await harness.RunAsync();

        // Assert
        result.Status.Should().Be(ReviewRunStatus.Posted);
        harness.Auth.Verify(a => a.ForgetInstallationToken(ReviewOrchestratorHarness.InstallationId, ReviewOrchestratorHarness.Repo), Times.Once());
    }

    // ── Parts that fail or are overtaken while others run ────────────────────

    [Fact]
    public async Task ReviewPullRequestAsync_GivenOnePartFailsForAReasonThatCanClearUp_Should_LetTheOtherPartsFinishAndKeepTheirResultsForTheRetry()
    {
        // Arrange — the parts still running are paid for, and a retry that finds them finished only has to redo the part that failed
        var harness = HarnessWithThreeChunks();
        var script = new PartBFailsWhileTheOthersAreWritten();
        harness.ClaudeHandler = script.HandleAsync;

        // Act
        var firstRun = harness.RunAsync();
        await script.PartBHasFailed.Task.WaitAsync(_hangGuard);
        await Settle.QueuedWorkAsync();
        var wasCancelled = script.AnotherPartWasCancelled.Task.IsCompleted;
        script.OthersMayFinish.SetResult();
        var failedRun = () => firstRun.WaitAsync(_hangGuard);
        await failedRun.Should().ThrowAsync<HttpRequestException>();

        script.IsPartBFailing = false;
        harness.ClaudeRequests.Clear();
        await harness.RunAsync().WaitAsync(_hangGuard);

        // Assert
        wasCancelled.Should().BeFalse("a part that failed for a reason that can clear up says nothing against the parts still running");
        harness.ClaudeRequests.Should().ContainSingle().Which.UserMessage.Should().Contain("src/B.cs", "the other two were finished and kept");
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenAPartFailsForAReasonThatCanClearUp_Should_NotStartThePartsThatHaveNotBegun()
    {
        // Arrange — the next request would probably meet the same rate limit or outage, and the retry will run it
        var harness = HarnessWithThreeChunks(maxParallelChunks: 1);
        harness.ClaudeHandler = (request, _) => request.UserMessage.Contains("part 2 of 3", StringComparison.Ordinal)
            ? throw new HttpRequestException("connection reset")
            : Completed();

        // Act
        var act = () => harness.RunAsync().WaitAsync(_hangGuard);

        // Assert
        await act.Should().ThrowAsync<HttpRequestException>();
        harness.ClaudeRequests.Should().HaveCount(2, "the first part finished, the second failed, and the third was never started");
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenTheCommitIsReplacedWhileAnotherPartIsRunning_Should_CancelThatPartInsteadOfPayingForItsReply()
    {
        // Arrange — parts A and B run at once. A finishes once B is in the middle of its model call, which frees a worker for part C, and the check
        // before C (the fourth read of the pull request) finds a newer commit while B is still being written
        var harness = HarnessWithThreeChunks(maxParallelChunks: 2);
        var moved = harness.PullRequest with { HeadSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" };
        var script = new PartAFinishesWhilePartBIsStillRunning();
        harness.PullRequestByCall = call => call == 4 ? moved : null;
        harness.ClaudeHandler = script.HandleAsync;

        // Act
        var result = await harness.RunAsync().WaitAsync(_hangGuard);

        // Assert
        result.Should().Be(ReviewRunResult.Skipped(ReviewSkipReason.Superseded));
        script.PartBWasCancelled.Task.IsCompletedSuccessfully.Should().BeTrue("a reply for a commit that has been replaced is thrown away, so it is not worth waiting for");
        harness.PostedReviews.Should().BeEmpty();
    }

    // ── The time limit for a whole review ────────────────────────────────────

    [Fact]
    public async Task ReviewPullRequestAsync_GivenTheModelNeverAnswers_Should_CancelItAtTheTimeLimitAndThrowATimeoutThatIsWorthRetrying()
    {
        // Arrange — with no limit of its own the review would run until the Functions host gave up, which restarts the worker and takes every other review with it
        var harness = new ReviewOrchestratorHarness { Options = new ReviewOptions { MaxReviewDurationSeconds = 600 } };
        var modelStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var modelWasCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.ClaudeHandler = async (_, cancellationToken) =>
        {
            modelStarted.SetResult();
            await Stall.UntilCancelledAsync(cancellationToken, modelWasCancelled);

            return ReviewOrchestratorHarness.DefaultReply();
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
        harness.PostedReviews.Should().BeEmpty();
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenAPartIsStillWaitingForAModelSlotAtTheTimeLimit_Should_StopWaitingAndThrowATimeout()
    {
        // Arrange — the limit on model requests is shared by every review the process runs, so a part can wait behind other reviews for as long as they take
        var harness = HarnessWithThreeChunks();
        harness.Options = new ReviewOptions
        {
            MaxChunkBytes = harness.Options.MaxChunkBytes,
            MaxParallelChunks = 3,
            MaxConcurrentModelRequests = 1,
            MaxReviewDurationSeconds = 300
        };
        var modelStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.ClaudeHandler = async (_, cancellationToken) =>
        {
            modelStarted.TrySetResult();
            await Stall.UntilCancelledAsync(cancellationToken);

            return ReviewOrchestratorHarness.DefaultReply();
        };

        // Act
        var run = harness.RunAsync();
        await modelStarted.Task.WaitAsync(_hangGuard);
        harness.Time.Advance(TimeSpan.FromSeconds(301));
        var act = () => run.WaitAsync(_hangGuard);

        // Assert
        await act.Should().ThrowAsync<TimeoutException>();
        harness.ClaudeRequests.Should().ContainSingle("the other two parts never got a slot to wait for");
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenAReviewThatFinishesJustInsideTheTimeLimit_Should_PostItAsUsual()
    {
        // Arrange
        var harness = new ReviewOrchestratorHarness { Options = new ReviewOptions { MaxReviewDurationSeconds = 600 } };
        var modelStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var modelMayAnswer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.ClaudeHandler = async (_, cancellationToken) =>
        {
            modelStarted.SetResult();
            await modelMayAnswer.Task.WaitAsync(cancellationToken);

            return ReviewOrchestratorHarness.DefaultReply();
        };

        // Act
        var run = harness.RunAsync();
        await modelStarted.Task.WaitAsync(_hangGuard);
        harness.Time.Advance(TimeSpan.FromSeconds(599));
        modelMayAnswer.SetResult();
        var result = await run.WaitAsync(_hangGuard);

        // Assert
        result.Status.Should().Be(ReviewRunStatus.Posted);
    }

    // ── Pending drafts that cannot be cleared ────────────────────────────────

    [Fact]
    public async Task ReviewPullRequestAsync_GivenDeletingTheStaleReviewIsRefusedForGood_Should_StillAttemptToPost()
    {
        // Arrange
        var harness = new ReviewOrchestratorHarness
        {
            Reviews = [OwnReview(string.Empty, commitId: string.Empty, state: "PENDING", id: 41)],
            DeleteHandler = _ => throw new GitHubApiException("cannot delete", HttpStatusCode.Forbidden, false)
        };

        // Act
        var result = await harness.RunAsync();

        // Assert
        result.Status.Should().Be(ReviewRunStatus.Posted);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ReviewPullRequestAsync_GivenDeletingTheStaleReviewFailsForNow_Should_ThrowSoTheMessageIsRetriedInsteadOfPostingIntoAConflict(HttpStatusCode status)
    {
        // Arrange — posting while a draft still exists can be rejected, and that rejection would look permanent
        var harness = new ReviewOrchestratorHarness
        {
            Reviews = [OwnReview(string.Empty, commitId: string.Empty, state: "PENDING", id: 41)],
            DeleteHandler = _ => throw new GitHubApiException("unavailable", status, false)
        };

        // Act
        var act = () => harness.RunAsync();

        // Assert
        await act.Should().ThrowAsync<GitHubApiException>();
        harness.PostedReviews.Should().BeEmpty();
    }

    // ── Posting ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReviewPullRequestAsync_GivenGitHubRejectsTheInlineComments_Should_RetryWithEveryFindingInTheBody()
    {
        // Arrange
        var harness = new ReviewOrchestratorHarness();
        harness.PostHandler = submission => submission.Comments.Count > 0
            ? throw new GitHubApiException("Line must be part of the diff", HttpStatusCode.UnprocessableEntity, false)
            : Task.CompletedTask;

        // Act
        var result = await harness.RunAsync();

        // Assert
        result.Status.Should().Be(ReviewRunStatus.Posted);
        harness.PostedReviews.Should().HaveCount(2);
        var retry = harness.PostedReviews[1];
        retry.Comments.Should().BeEmpty();
        retry.Body.Should().Contain("Null dereference").And.Contain("`src/Foo.cs:11`");
        retry.CommitId.Should().Be(HeadSha);
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenTheBodyOnlyRetryIsAlsoRejected_Should_Throw()
    {
        // Arrange
        var harness = new ReviewOrchestratorHarness
        {
            PostHandler = _ => throw new GitHubApiException("rejected", HttpStatusCode.UnprocessableEntity, false)
        };

        // Act
        var act = () => harness.RunAsync();

        // Assert
        await act.Should().ThrowAsync<GitHubApiException>();
        harness.PostedReviews.Should().HaveCount(2, "one attempt with inline comments and exactly one retry without");
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenA422ForAReviewWithoutInlineComments_Should_NotRetryAndShouldThrow()
    {
        // Arrange
        var harness = new ReviewOrchestratorHarness
        {
            ClaudeHandler = (_, _) => Completed(),
            PostHandler = _ => throw new GitHubApiException("rejected", HttpStatusCode.UnprocessableEntity, false)
        };

        // Act
        var act = () => harness.RunAsync();

        // Assert
        await act.Should().ThrowAsync<GitHubApiException>();
        harness.PostedReviews.Should().ContainSingle();
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task ReviewPullRequestAsync_GivenAnyOtherPostFailure_Should_ThrowWithoutRetryingBodyOnly(HttpStatusCode status)
    {
        // Arrange
        var harness = new ReviewOrchestratorHarness
        {
            PostHandler = _ => throw new GitHubApiException("failed", status, false)
        };

        // Act
        var act = () => harness.RunAsync();

        // Assert
        (await act.Should().ThrowAsync<GitHubApiException>()).Which.StatusCode.Should().Be(status);
        harness.PostedReviews.Should().ContainSingle();
    }

    // ── Failures ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReviewPullRequestAsync_GivenClaudeFails_Should_PropagateTheExceptionAndPostNothing()
    {
        // Arrange
        var harness = new ReviewOrchestratorHarness
        {
            ClaudeHandler = (_, _) => throw new HttpRequestException("Claude unavailable")
        };

        // Act
        var act = () => harness.RunAsync();

        // Assert
        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("*Claude unavailable*");
        harness.PostedReviews.Should().BeEmpty();
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenTheFileListCannotBeRead_Should_PropagateTheGitHubException()
    {
        // Arrange
        var harness = new ReviewOrchestratorHarness();
        harness.GitHub.Setup(g => g.GetPullRequestFilesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("bad gateway", HttpStatusCode.BadGateway, false));

        // Act
        var act = () => harness.RunAsync();

        // Assert
        await act.Should().ThrowAsync<GitHubApiException>();
        harness.ClaudeRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenCancellationWhileTheModelIsWorking_Should_ThrowOperationCanceled()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource();
        var harness = new ReviewOrchestratorHarness
        {
            ClaudeHandler = async (_, cancellationToken) =>
            {
                started.SetResult();
                await Stall.UntilCancelledAsync(cancellationToken);
                return ReviewOrchestratorHarness.DefaultReply();
            }
        };

        // Act
        var run = harness.RunAsync(cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();
        var act = () => run.WaitAsync(_hangGuard);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        harness.PostedReviews.Should().BeEmpty();
    }

    // ── Telemetry ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReviewPullRequestAsync_GivenASkippedReview_Should_CountItWithTheReasonAsATag()
    {
        // Arrange
        using var metrics = new MetricsRecorder();
        var harness = new ReviewOrchestratorHarness { PullRequest = new("open", true, "t", HeadSha, BaseSha, 1, 1, 1) };

        // Act
        await harness.RunAsync();

        // Assert
        metrics.Sum("github_review_app.reviews.skipped", ("reason", "Draft")).Should().Be(1);
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenAPostedReview_Should_RecordFileFindingAndSizeMetrics()
    {
        // Arrange
        using var metrics = new MetricsRecorder();
        var harness = new ReviewOrchestratorHarness();

        // Act
        await harness.RunAsync();

        // Assert
        metrics.Sum("github_review_app.files.reviewed").Should().Be(1);
        metrics.Sum("github_review_app.files.skipped", ("reason", "DependencyLockfile")).Should().Be(1);
        metrics.Sum("github_review_app.findings", ("severity", "High")).Should().Be(1);
        metrics.Sum("github_review_app.pull_request.changed_lines").Should().Be(12);
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenSeveralFilesLeftOutAndSeveralFindings_Should_CountEachOfThemNotJustThatThereWasSome()
    {
        // Arrange — one skipped file and one finding cannot tell a count from the number one
        using var metrics = new MetricsRecorder();
        var harness = new ReviewOrchestratorHarness
        {
            Files =
            [
                new("src/Foo.cs", "modified", 1, 0, ReviewOrchestratorHarness.SourcePatch),
                new("package-lock.json", "modified", 5, 5, "@@ -1 +1 @@\n+x"),
                new("yarn.lock", "modified", 5, 5, "@@ -1 +1 @@\n+x")
            ],
            ClaudeHandler = (_, _) => Task.FromResult(new ClaudeReviewResult(
                ChunkOutcome.Completed,
                "ok",
                [
                    new ReviewFinding("src/Foo.cs", 11, ReviewSeverity.High, "bug", "first problem", "Details."),
                    new ReviewFinding("src/Foo.cs", 12, ReviewSeverity.High, "bug", "second problem", "Details.")
                ],
                "claude-test",
                ClaudeUsage.Empty))
        };

        // Act
        await harness.RunAsync();

        // Assert
        metrics.Sum("github_review_app.files.skipped", ("reason", "DependencyLockfile")).Should().Be(2);
        metrics.Sum("github_review_app.findings", ("severity", "High")).Should().Be(2);
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenAReviewThatIsDiscardedAsStale_Should_NotCountItsFilesAsReviewed()
    {
        // Arrange — files.reviewed describes posted reviews; a discarded or retried run must not inflate it
        using var metrics = new MetricsRecorder();
        var harness = new ReviewOrchestratorHarness();
        harness.PullRequestAfterReview = WithHead(harness.PullRequest, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

        // Act
        await harness.RunAsync();

        // Assert
        metrics.Sum("github_review_app.files.reviewed").Should().Be(0);
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenSeveralParts_Should_CountEveryModelRequestByHowItEnded()
    {
        // Arrange
        using var metrics = new MetricsRecorder();
        var harness = HarnessWithThreeChunks();
        harness.ClaudeHandler = (request, _) => Task.FromResult(
            request.UserMessage.Contains("part 2 of 3")
                ? new ClaudeReviewResult(ChunkOutcome.Refused, string.Empty, [], "claude-test", ClaudeUsage.Empty, "cyber")
                : new ClaudeReviewResult(ChunkOutcome.Completed, "ok", [], "claude-test", ClaudeUsage.Empty));

        // Act
        await harness.RunAsync();

        // Assert
        metrics.Sum("github_review_app.chunks", ("outcome", "Completed")).Should().Be(2);
        metrics.Sum("github_review_app.chunks", ("outcome", "Refused")).Should().Be(1);
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenAReviewThatFailsAndIsRetried_Should_CountThePlanOnlyOnce()
    {
        // Arrange — the queue runs the message again, and a plan counted for every attempt would double the files and lines of a pull request that retried
        using var metrics = new MetricsRecorder();
        var harness = new ReviewOrchestratorHarness();
        harness.ClaudeHandler = new ModelFailsOnceThenAnswers().HandleAsync;

        // Act
        var firstAttempt = () => harness.RunAsync();
        await firstAttempt.Should().ThrowAsync<HttpRequestException>();
        await harness.RunAsync();

        // Assert
        metrics.Sum("github_review_app.files.skipped", ("reason", "DependencyLockfile")).Should().Be(1);
        metrics.Measurements.Count(measurement => measurement.Name == "github_review_app.pull_request.changed_lines").Should().Be(1);
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenEveryFileWasDeleted_Should_StillCountTheFilesLeftOutEvenThoughNothingIsPosted()
    {
        // Arrange — the review is skipped as having no new code, but the operator still learns what was left out and why
        using var metrics = new MetricsRecorder();
        var harness = new ReviewOrchestratorHarness
        {
            Files = [ReviewOrchestratorHarness.ModifiedFile("src/Old.cs", 5) with { Status = "removed" }]
        };

        // Act
        var result = await harness.RunAsync();

        // Assert
        result.Should().Be(ReviewRunResult.Skipped(ReviewSkipReason.NothingToReview));
        metrics.Sum("github_review_app.files.skipped", ("reason", "Deleted")).Should().Be(1);
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenAReviewThatIsDiscardedAsStale_Should_NotCountThePlanBecauseTheNewCommitsReviewWill()
    {
        // Arrange
        using var metrics = new MetricsRecorder();
        var harness = new ReviewOrchestratorHarness();
        harness.PullRequestAfterReview = WithHead(harness.PullRequest, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

        // Act
        await harness.RunAsync();

        // Assert
        metrics.Sum("github_review_app.files.skipped").Should().Be(0);
        metrics.Measurements.Should().NotContain(measurement => measurement.Name == "github_review_app.pull_request.changed_lines");
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenAModelRequestThatThrows_Should_CountItAsFailedSoTheCounterMatchesTheRequestsThatWereMade()
    {
        // Arrange
        using var metrics = new MetricsRecorder();
        var harness = new ReviewOrchestratorHarness
        {
            ClaudeHandler = (_, _) => throw new InvalidOperationException("the model service is not configured")
        };

        // Act
        var act = () => harness.RunAsync();
        await act.Should().ThrowAsync<InvalidOperationException>();

        // Assert
        metrics.Sum("github_review_app.chunks", ("outcome", "Failed")).Should().Be(1);
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenARequestCancelledByTheCaller_Should_NotCountItAsFailedBecauseTheModelDidNothingWrong()
    {
        // Arrange
        using var metrics = new MetricsRecorder();
        var modelStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = new ReviewOrchestratorHarness
        {
            ClaudeHandler = async (_, cancellationToken) =>
            {
                modelStarted.SetResult();
                await Stall.UntilCancelledAsync(cancellationToken);

                return ReviewOrchestratorHarness.DefaultReply();
            }
        };
        using var cancellation = new CancellationTokenSource();

        // Act
        var run = harness.RunAsync(cancellation.Token);
        await modelStarted.Task.WaitAsync(_hangGuard);
        await cancellation.CancelAsync();
        var act = () => run.WaitAsync(_hangGuard);
        await act.Should().ThrowAsync<OperationCanceledException>();

        // Assert
        metrics.Sum("github_review_app.chunks", ("outcome", "Failed")).Should().Be(0);
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenAPartialReview_Should_CountItAsPartial()
    {
        // Arrange
        using var metrics = new MetricsRecorder();
        var harness = new ReviewOrchestratorHarness
        {
            Files = [ReviewOrchestratorHarness.ModifiedFile("src/A.cs"), ReviewOrchestratorHarness.ModifiedFile("src/B.cs")],
            Options = new ReviewOptions { MaxFiles = 1 }
        };

        // Act
        var result = await harness.RunAsync();

        // Assert
        result.IsPartial.Should().BeTrue("a file was left out for size");
        metrics.Sum("github_review_app.reviews.partial").Should().Be(1);
    }

    // ── What counts as a partial review ──────────────────────────────────────
    // Each test isolates one reason, so removing any one of them from the rule is noticed.

    [Fact]
    public async Task ReviewPullRequestAsync_GivenAFileCutShortAtTheSizeLimitAndNothingElseMissed_Should_CountTheReviewAsPartial()
    {
        // Arrange — the file was read, so nothing is skipped and no part failed, but only the first part of it was
        var harness = new ReviewOrchestratorHarness
        {
            Files = [ReviewOrchestratorHarness.ModifiedFile("src/Big.cs", 300)],
            Options = new ReviewOptions { MaxFileBytes = 5_000 },
            ClaudeHandler = (_, _) => Completed()
        };

        // Act
        var result = await harness.RunAsync();

        // Assert
        result.Should().Be(ReviewRunResult.Posted(filesReviewed: 1, findingCount: 0, isPartial: true));
        harness.PostedReviews.Single().Body.Should().Contain("Cut short at the size limit (1)").And.Contain("`src/Big.cs`");
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenAFileGitHubSentNoDiffForAndNothingElseMissed_Should_CountTheReviewAsPartial()
    {
        // Arrange — a change too large for GitHub to diff is code nobody read, unlike a lockfile or a binary that nobody could
        var harness = new ReviewOrchestratorHarness
        {
            Files = [ReviewOrchestratorHarness.ModifiedFile("src/A.cs", 5), new PullRequestFile("src/Huge.cs", "modified", 40_000, 10, null)],
            ClaudeHandler = (_, _) => Completed()
        };

        // Act
        var result = await harness.RunAsync();

        // Assert
        result.Should().Be(ReviewRunResult.Posted(filesReviewed: 1, findingCount: 0, isPartial: true));
        harness.PostedReviews.Single().Body.Should().Contain("No diff from GitHub (1)");
    }

    [Theory]
    [InlineData("package-lock.json", "modified")]
    [InlineData("assets/logo.png", "modified")]
    [InlineData("src/Old.cs", "removed")]
    public async Task ReviewPullRequestAsync_GivenTheOnlyFilesLeftOutAreOnesNobodyCouldRead_Should_NotCountTheReviewAsPartial(string path, string status)
    {
        // Arrange — a lockfile or a binary is never read and a deleted file has no new code, so leaving them out loses nothing
        var harness = new ReviewOrchestratorHarness
        {
            Files = [ReviewOrchestratorHarness.ModifiedFile("src/A.cs", 5), ReviewOrchestratorHarness.ModifiedFile(path, 5) with { Status = status }],
            ClaudeHandler = (_, _) => Completed()
        };

        // Act
        var result = await harness.RunAsync();

        // Assert
        result.Should().Be(ReviewRunResult.Posted(filesReviewed: 1, findingCount: 0, isPartial: false));
    }

    // ── What a review writes on the current span ─────────────────────────────

    [Fact]
    public async Task ReviewPullRequestAsync_GivenAPostedReview_Should_TagTheCurrentSpanWithWhatWasChangedReviewedAndFound()
    {
        // Arrange — the processor's span is the current one while the orchestrator runs, and these tags are what a trace is searched by
        using var spans = new SpanRecorder(ActivitySources.ReviewProcessorName);
        using var span = ActivitySources.ReviewProcessor.StartActivity("review");
        var harness = new ReviewOrchestratorHarness();

        // Act
        await harness.RunAsync();

        // Assert
        span!.GetTagItem("review.files_changed").Should().Be(2);
        span.GetTagItem("review.files_reviewed").Should().Be(1);
        span.GetTagItem("review.files_skipped").Should().Be(1);
        span.GetTagItem("review.chunks").Should().Be(1);
        span.GetTagItem("review.findings").Should().Be(1);
        span.GetTagItem("review.partial").Should().Be(false);
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenAPartialReview_Should_TagTheCurrentSpanAsPartial()
    {
        // Arrange
        using var spans = new SpanRecorder(ActivitySources.ReviewProcessorName);
        using var span = ActivitySources.ReviewProcessor.StartActivity("review");
        var harness = new ReviewOrchestratorHarness
        {
            Files = [ReviewOrchestratorHarness.ModifiedFile("src/A.cs"), ReviewOrchestratorHarness.ModifiedFile("src/B.cs")],
            Options = new ReviewOptions { MaxFiles = 1 },
            ClaudeHandler = (_, _) => Completed()
        };

        // Act
        await harness.RunAsync();

        // Assert
        span!.GetTagItem("review.partial").Should().Be(true);
        span.GetTagItem("review.files_changed").Should().Be(2);
        span.GetTagItem("review.files_skipped").Should().Be(1);
        span.GetTagItem("review.findings").Should().Be(0);
    }

    [Fact]
    public async Task ReviewPullRequestAsync_GivenEveryFileWasDeleted_Should_TagWhatWasPlannedButNoOutcomeBecauseNothingWasPosted()
    {
        // Arrange
        using var spans = new SpanRecorder(ActivitySources.ReviewProcessorName);
        using var span = ActivitySources.ReviewProcessor.StartActivity("review");
        var harness = new ReviewOrchestratorHarness
        {
            Files = [ReviewOrchestratorHarness.ModifiedFile("src/Old.cs", 5) with { Status = "removed" }]
        };

        // Act
        await harness.RunAsync();

        // Assert
        span!.GetTagItem("review.files_changed").Should().Be(1);
        span.GetTagItem("review.files_reviewed").Should().Be(0);
        span.GetTagItem("review.files_skipped").Should().Be(1);
        span.GetTagItem("review.chunks").Should().Be(0);
        span.GetTagItem("review.findings").Should().BeNull();
        span.GetTagItem("review.partial").Should().BeNull();
    }
}
