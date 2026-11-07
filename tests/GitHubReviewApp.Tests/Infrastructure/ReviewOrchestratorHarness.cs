namespace GitHubReviewApp.Tests.Infrastructure;

/// <summary>
/// Wires an orchestrator to mocked GitHub, Claude and auth services with sensible defaults, and records
/// what was sent to them. A test overrides only the part it is about.
/// </summary>
internal sealed class ReviewOrchestratorHarness
{
    public const string Owner = "my-org";
    public const string Repo = "my-service";
    public const int PrNumber = 7;
    public const long InstallationId = 12345L;
    public const string HeadSha = "0123456789abcdef0123456789abcdef01234567";
    public const string BaseSha = "fedcba9876543210fedcba9876543210fedcba98";
    public const string Token = "ghs_repo_scoped_token";
    public const string AppLogin = "claude-test[bot]";

    /// <summary>New-side lines 10 (context), 11 (added), 12 and 13 (context).</summary>
    public const string SourcePatch = "@@ -1,3 +10,4 @@\n a\n+b\n c\n d";

    public static readonly ReviewQueueMessage QueuedMessage =
        new(Owner, Repo, PrNumber, "stale queued title", BaseSha, HeadSha, InstallationId);

    private readonly object _gate = new();
    private readonly List<(string Operation, string Token)> _usedTokens = [];
    private ModelRequestLimiter? _limiter;
    private int _pullRequestCalls;
    private int _reviewListCalls;
    private int _tokenCalls;

    public ReviewOrchestratorHarness()
    {
        Cache = new ChunkResultCache(Time);

        Auth.Setup(a => a.GetInstallationTokenAsync(InstallationId, Repo, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Interlocked.Increment(ref _tokenCalls) == 1 ? Token : TokenAfterModel ?? Token);

        Auth.Setup(a => a.GetAppLoginAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AppLogin);

        Auth.Setup(a => a.CanReadContents(InstallationId, Repo))
            .Returns(() => CanReadContents);

        GitHub.Setup(g => g.GetPullRequestAsync(Owner, Repo, PrNumber, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, int, string, CancellationToken>((_, _, _, token, _) =>
            {
                Use("pull-request", token);
                var call = Interlocked.Increment(ref _pullRequestCalls);
                return Task.FromResult(PullRequestByCall?.Invoke(call) ?? (call == 1 ? PullRequest : PullRequestAfterReview ?? PullRequest));
            });

        GitHub.Setup(g => g.ListReviewsAsync(Owner, Repo, PrNumber, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, int, string, CancellationToken>((_, _, _, token, _) =>
            {
                Use("reviews", token);
                IReadOnlyList<PullRequestReviewSummary> reviews = Interlocked.Increment(ref _reviewListCalls) == 1 ? Reviews : ReviewsAfterModel ?? Reviews;
                return Task.FromResult(reviews);
            });

        GitHub.Setup(g => g.GetPullRequestFilesAsync(Owner, Repo, PrNumber, Token, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Files);

        GitHub.Setup(g => g.GetRepositoryGuidelinesAsync(Owner, Repo, BaseSha, Token, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => GuidelinesResult ?? new RepositoryGuidelines(Guidelines, IsUnreadable: false));

        GitHub.Setup(g => g.DeleteReviewAsync(Owner, Repo, PrNumber, It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, int, long, string, CancellationToken>((_, _, _, reviewId, token, _) =>
            {
                Use("delete", token);
                CallOrder.Add($"delete:{reviewId}");
                return DeleteHandler(reviewId);
            });

        GitHub.Setup(g => g.PostReviewAsync(Owner, Repo, PrNumber, It.IsAny<ReviewSubmission>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, int, ReviewSubmission, string, CancellationToken>((_, _, _, submission, token, _) =>
            {
                Use("post", token);
                CallOrder.Add("post");
                PostedReviews.Add(submission);
                return PostHandler(submission);
            });

        Claude.Setup(c => c.ReviewAsync(It.IsAny<ClaudeReviewRequest>(), It.IsAny<CancellationToken>()))
            .Returns<ClaudeReviewRequest, CancellationToken>(async (request, cancellationToken) =>
            {
                lock (ClaudeRequests)
                {
                    ClaudeRequests.Add(request);
                }

                return await ClaudeHandler(request, cancellationToken);
            });
    }

    public Mock<IGitHubAppAuthService> Auth { get; } = new();

    public Mock<IGitHubService> GitHub { get; } = new();

    public Mock<IClaudeService> Claude { get; } = new();

    public ReviewOptions Options { get; set; } = new();

    public FakeTimeProvider Time { get; } = new();

    /// <summary>Shared by every orchestrator this harness builds, as the process-wide singletons are in production.</summary>
    public ReviewInFlightRegistry InFlight { get; } = new();

    /// <summary>
    /// The limiter every orchestrator this harness builds shares. It is created from <see cref="Options"/> the first time it is read, so set them
    /// first. A test can hold its slots to keep the parts of a review waiting while it changes the world around them.
    /// </summary>
    public ModelRequestLimiter Limiter => _limiter ??= new ModelRequestLimiter(Microsoft.Extensions.Options.Options.Create(Options));

    public ChunkResultCache Cache { get; }

    /// <summary>What <c>CanReadContents</c> answers. False means the installation was never granted the contents permission.</summary>
    public bool CanReadContents { get; set; } = true;

    /// <summary>When set, the token returned by every call after the first (the one fetched after the model has finished).</summary>
    public string? TokenAfterModel { get; set; }

    /// <summary>The token each GitHub call was made with, in call order.</summary>
    public IReadOnlyList<(string Operation, string Token)> UsedTokens
    {
        get
        {
            lock (_gate)
            {
                return _usedTokens.ToList();
            }
        }
    }

    /// <summary>When set, decides the pull request returned by the Nth <c>GetPullRequestAsync</c> call (1-based).</summary>
    public Func<int, PullRequestInfo?>? PullRequestByCall { get; set; }

    public RepositoryGuidelines? GuidelinesResult { get; set; }

    public PullRequestInfo PullRequest { get; set; } =
        new("open", false, "Fresh API title", HeadSha, BaseSha, ChangedFileCount: 2, Additions: 10, Deletions: 2);

    /// <summary>When set, returned by the second <c>GetPullRequestAsync</c> call (the check after the review).</summary>
    public PullRequestInfo? PullRequestAfterReview { get; set; }

    public List<PullRequestFile> Files { get; set; } =
    [
        new("src/Foo.cs", "modified", 1, 0, SourcePatch),
        new("package-lock.json", "modified", 5, 5, "@@ -1 +1 @@\n+x")
    ];

    public List<PullRequestReviewSummary> Reviews { get; set; } = [];

    /// <summary>When set, returned by the second <c>ListReviewsAsync</c> call (the re-read just before posting).</summary>
    public List<PullRequestReviewSummary>? ReviewsAfterModel { get; set; }

    public string? Guidelines { get; set; }

    public Func<ClaudeReviewRequest, CancellationToken, Task<ClaudeReviewResult>> ClaudeHandler { get; set; } =
        (_, _) => Task.FromResult(DefaultReply());

    public Func<ReviewSubmission, Task> PostHandler { get; set; } = _ => Task.CompletedTask;

    public Func<long, Task> DeleteHandler { get; set; } = _ => Task.CompletedTask;

    public List<ReviewSubmission> PostedReviews { get; } = [];

    public List<ClaudeReviewRequest> ClaudeRequests { get; } = [];

    public List<string> CallOrder { get; } = [];

    public static ClaudeReviewResult DefaultReply()
    {
        return new ClaudeReviewResult(
            ChunkOutcome.Completed,
            "Adds a feature.",
            [new ReviewFinding("src/Foo.cs", 11, ReviewSeverity.High, "bug", "Null dereference", "The value can be null.")],
            "claude-test",
            new ClaudeUsage(100, 50, 0, 0));
    }

    public static PullRequestFile ModifiedFile(string path, int lineCount = 100)
    {
        var lines = Enumerable.Range(1, lineCount).Select(_ => "+" + new string('x', 20));
        var patch = $"@@ -0,0 +1,{lineCount} @@\n" + string.Join("\n", lines);
        return new PullRequestFile(path, "modified", lineCount, 0, patch);
    }

    public ReviewOrchestrator BuildSut()
    {
        var options = Microsoft.Extensions.Options.Options.Create(Options);

        return new ReviewOrchestrator(
            Auth.Object,
            GitHub.Object,
            Claude.Object,
            new ReviewPlanner(options),
            new ReviewAggregator(options),
            new ReviewFormatter(options),
            InFlight,
            Limiter,
            Cache,
            options,
            Time,
            NullLogger<ReviewOrchestrator>.Instance);
    }

    public Task<ReviewRunResult> RunAsync(CancellationToken cancellationToken = default)
    {
        return BuildSut().ReviewPullRequestAsync(QueuedMessage, cancellationToken);
    }

    private void Use(string operation, string token)
    {
        lock (_gate)
        {
            _usedTokens.Add((operation, token));
        }
    }
}
