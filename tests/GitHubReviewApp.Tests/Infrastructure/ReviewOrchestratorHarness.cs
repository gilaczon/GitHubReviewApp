namespace GitHubReviewApp.Tests.Infrastructure;

/// <summary>
/// Wires a checklist orchestrator to mocked GitHub, Claude, auth and runtime settings, with the real input builder, model selector, publisher,
/// in-flight registry, limiter and result cache, and records what was sent. GitHub's comments are kept per pull request, so a comment the app
/// writes is in the next list, as on GitHub. A test overrides only the part it is about.
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
    public const string SonnetModel = "claude-sonnet-5-5";
    public const string OpusModel = "claude-opus-5-5";

    /// <summary>New-side lines 10 (context), 11 (added), 12 and 13 (context).</summary>
    public const string SourcePatch = "@@ -1,3 +10,4 @@\n a\n+b\n c\n d";

    public static readonly ReviewQueueMessage QueuedMessage =
        new(Owner, Repo, PrNumber, "stale queued title", BaseSha, HeadSha, InstallationId);

    private readonly object _gate = new();
    private readonly Dictionary<int, List<IssueComment>> _comments = [];
    private readonly List<string> _callOrder = [];
    private readonly List<CheckRunRequest> _createdCheckRuns = [];
    private readonly List<(int PrNumber, string Body)> _createdComments = [];
    private readonly List<(long Id, string Body)> _updatedComments = [];
    private readonly List<ClaudeRunRequest> _claudeRequests = [];
    private readonly List<(string Operation, string Token)> _usedTokens = [];
    private ModelRequestLimiter? _limiter;
    private int _pullRequestCalls;
    private int _tokenCalls;
    private int _claudeCalls;
    private int _commentWrites;
    private long _lastCommentId = 5000;

    public ReviewOrchestratorHarness()
    {
        Cache = new ModelResultCache(Time);

        Auth.Setup(a => a.GetInstallationTokenAsync(InstallationId, Repo, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                Interlocked.Increment(ref _tokenCalls);
                return Volatile.Read(ref _claudeCalls) == 0 ? Token : TokenAfterModel ?? Token;
            });

        Auth.Setup(a => a.GetAppLoginAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AppLogin);

        // As the real service: with no token fetched yet, nothing says the permission is missing.
        Auth.Setup(a => a.CanWriteChecks(InstallationId, Repo))
            .Returns(() => Volatile.Read(ref _tokenCalls) == 0 || CanWriteChecks);

        Runtime.Setup(r => r.Current).Returns(() => RuntimeSettings);

        // Every GitHub call accepts any token and records the one it was given, so a test can tell which token each call used.
        GitHub.Setup(g => g.GetPullRequestAsync(Owner, Repo, It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, int, string, CancellationToken>((_, _, _, token, _) =>
            {
                RecordToken("pull-request", token);
                var call = Interlocked.Increment(ref _pullRequestCalls);
                return Task.FromResult(PullRequestByCall?.Invoke(call) ?? (call == 1 ? PullRequest : PullRequestAfterModel ?? PullRequest));
            });

        GitHub.Setup(g => g.GetPullRequestFilesAsync(Owner, Repo, It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, int, string, CancellationToken>((_, _, _, token, _) =>
            {
                RecordToken("files", token);
                return Task.FromResult<IReadOnlyList<PullRequestFile>>(Files);
            });

        GitHub.Setup(g => g.ListIssueCommentsAsync(Owner, Repo, It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, int, string, CancellationToken>((_, _, prNumber, token, _) =>
            {
                lock (_gate)
                {
                    _usedTokens.Add(("comments", token));
                    return Task.FromResult<IReadOnlyList<IssueComment>>(CommentsOn(prNumber).ToList());
                }
            });

        GitHub.Setup(g => g.CreateCheckRunAsync(Owner, Repo, It.IsAny<CheckRunRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, CheckRunRequest, string, CancellationToken>((_, _, checkRun, token, _) =>
            {
                lock (_gate)
                {
                    _usedTokens.Add(("check-run", token));
                    _callOrder.Add("check-run");
                    _createdCheckRuns.Add(checkRun);
                    return Task.FromResult(9000L + _createdCheckRuns.Count);
                }
            });

        GitHub.Setup(g => g.CreateIssueCommentAsync(Owner, Repo, It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, int, string, string, CancellationToken>(async (_, _, prNumber, body, token, _) =>
            {
                await CommentWriteHandler(Interlocked.Increment(ref _commentWrites) - 1);

                lock (_gate)
                {
                    var id = ++_lastCommentId;
                    CommentsOn(prNumber).Add(new IssueComment(id, AppLogin, body));
                    _usedTokens.Add(("comment:create", token));
                    _callOrder.Add("comment:create");
                    _createdComments.Add((prNumber, body));
                    return id;
                }
            });

        GitHub.Setup(g => g.UpdateIssueCommentAsync(Owner, Repo, It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, long, string, string, CancellationToken>(async (_, _, commentId, body, token, _) =>
            {
                await CommentWriteHandler(Interlocked.Increment(ref _commentWrites) - 1);

                lock (_gate)
                {
                    _usedTokens.Add(("comment:update", token));
                    foreach (var comments in _comments.Values)
                    {
                        var index = comments.FindIndex(comment => comment.Id == commentId);

                        if (index >= 0)
                        {
                            comments[index] = comments[index] with { Body = body };
                        }
                    }

                    _callOrder.Add("comment:update");
                    _updatedComments.Add((commentId, body));
                }
            });

        Claude.Setup(c => c.ReviewAsync(It.IsAny<ClaudeRunRequest>(), It.IsAny<CancellationToken>()))
            .Returns<ClaudeRunRequest, CancellationToken>((request, cancellationToken) =>
            {
                lock (_gate)
                {
                    _claudeRequests.Add(request);
                }

                return ClaudeHandler(Interlocked.Increment(ref _claudeCalls) - 1, request, cancellationToken);
            });
    }

    public Mock<IGitHubAppAuthService> Auth { get; } = new();

    public Mock<IGitHubService> GitHub { get; } = new();

    public Mock<IClaudeService> Claude { get; } = new();

    public Mock<IRuntimeSettings> Runtime { get; } = new();

    public ReviewOptions Options { get; set; } = new();

    public ClaudeOptions ClaudeOptions { get; set; } = new()
    {
        Pricing = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase)
        {
            [SonnetModel] = new ModelPrice
            {
                InputPerMillionTokens = 2m,
                OutputPerMillionTokens = 10m,
                CacheWritePerMillionTokens = 2.5m,
                CacheReadPerMillionTokens = 0.2m
            },
            [OpusModel] = new ModelPrice
            {
                InputPerMillionTokens = 4m,
                OutputPerMillionTokens = 20m,
                CacheWritePerMillionTokens = 5m,
                CacheReadPerMillionTokens = 0.2m
            }
        }
    };

    public RuntimeSettings RuntimeSettings { get; set; } = RuntimeSettings.Default;

    public FakeTimeProvider Time { get; } = new();

    public ListLogger<ReviewOrchestrator> Logger { get; } = new();

    /// <summary>Shared by every orchestrator this harness builds, as the process-wide singletons are in production.</summary>
    public ReviewInFlightRegistry InFlight { get; } = new();

    /// <summary>The limiter every orchestrator this harness builds shares. It is created from <see cref="Options"/> the first time it is read, so set them first.</summary>
    public ModelRequestLimiter Limiter => _limiter ??= new ModelRequestLimiter(Microsoft.Extensions.Options.Options.Create(Options));

    public ModelResultCache Cache { get; }

    /// <summary>
    /// When set, returned by every installation token fetch made once the model has been called, as a token cache returns a new token once the
    /// first one is close to expiring after minutes of model work. Unset, every fetch returns <see cref="Token"/>.
    /// </summary>
    public string? TokenAfterModel { get; set; }

    /// <summary>What <c>CanWriteChecks</c> answers once a token has been fetched. False means the installation never granted the Checks permission.</summary>
    public bool CanWriteChecks { get; set; } = true;

    /// <summary>When set, decides the pull request returned by the Nth <c>GetPullRequestAsync</c> call (1-based).</summary>
    public Func<int, PullRequestInfo?>? PullRequestByCall { get; set; }

    public PullRequestInfo PullRequest { get; set; } = new(
        "open",
        false,
        "Add retries",
        HeadSha,
        BaseSha,
        ChangedFileCount: 2,
        Additions: 10,
        Deletions: 2,
        Body: "Retries the call once.",
        AuthorLogin: "octocat",
        BaseRef: "main",
        HeadRef: "feature/retries");

    /// <summary>When set, returned by every <c>GetPullRequestAsync</c> call after the first (the check after the model has finished).</summary>
    public PullRequestInfo? PullRequestAfterModel { get; set; }

    public List<PullRequestFile> Files { get; set; } =
    [
        new("src/Foo.cs", "modified", 1, 0, SourcePatch),
        new("package-lock.json", "modified", 5, 5, "@@ -1 +1 @@\n+x")
    ];

    /// <summary>Answers the Nth model call (0-based) of this harness, across every review it runs.</summary>
    public Func<int, ClaudeRunRequest, CancellationToken, Task<ClaudeRunResult>> ClaudeHandler { get; set; } =
        (_, _, _) => Task.FromResult(Completed());

    /// <summary>Runs before the Nth comment write (0-based, creates and updates together) is recorded; a throw fails that write.</summary>
    public Func<int, Task> CommentWriteHandler { get; set; } = _ => Task.CompletedTask;

    /// <summary>The comments on this harness's pull request, the app's own included once written.</summary>
    public List<IssueComment> Comments => CommentsOn(PrNumber);

    public IReadOnlyList<string> CallOrder => Snapshot(_callOrder);

    public IReadOnlyList<CheckRunRequest> CreatedCheckRuns => Snapshot(_createdCheckRuns);

    public IReadOnlyList<(int PrNumber, string Body)> CreatedComments => Snapshot(_createdComments);

    public IReadOnlyList<(long Id, string Body)> UpdatedComments => Snapshot(_updatedComments);

    public IReadOnlyList<ClaudeRunRequest> ClaudeRequests => Snapshot(_claudeRequests);

    /// <summary>Every GitHub call in the order it was made (<c>pull-request</c>, <c>files</c>, <c>comments</c>, <c>check-run</c>, <c>comment:create</c>, <c>comment:update</c>), with the token it used.</summary>
    public IReadOnlyList<(string Operation, string Token)> UsedTokens => Snapshot(_usedTokens);

    /// <summary>
    /// A run that answered: every check passes but Functionality, a concern with <paramref name="note"/>, and one finding on <c>src/Foo.cs</c>
    /// line 11. The <paramref name="ratings"/> replace those of the checks they name; a check given <see langword="null"/> is left out of the
    /// answer. A concern or fail carries <paramref name="note"/>, and the checks in <paramref name="serious"/> are flagged serious.
    /// </summary>
    public static ClaudeRunResult Completed(
        IReadOnlyList<(CheckId Id, CheckStatus? Status)>? ratings = null,
        string model = SonnetModel,
        IReadOnlyList<ChecklistFinding>? findings = null,
        string summary = "Adds a retry.",
        string note = "Retries twice.",
        string coverageNote = "",
        IReadOnlyList<CheckId>? serious = null)
    {
        var rated = Checklist.Order.ToDictionary(id => id, id => (CheckStatus?)CheckStatus.Pass);
        rated[CheckId.Functionality] = CheckStatus.Concern;

        foreach (var (id, status) in ratings ?? [])
        {
            rated[id] = status;
        }

        var checks = rated
            .Where(pair => pair.Value is not null)
            .ToDictionary(
                pair => pair.Key,
                pair => new RatedCheck(
                    pair.Key,
                    pair.Value!.Value,
                    pair.Value is CheckStatus.Concern or CheckStatus.Fail ? note : string.Empty,
                    (serious ?? []).Contains(pair.Key)));

        var answer = new RunAnswer(
            checks,
            findings ?? [new ChecklistFinding(CheckId.Functionality, "src/Foo.cs", 11, "Retries twice.", "Retry once.")],
            summary,
            coverageNote);

        return new ClaudeRunResult(RunOutcome.Completed, answer, model, [new ModelAttempt(model, new ClaudeUsage(1_000, 500, 0, 0))]);
    }

    /// <summary>A run that ended without an answer.</summary>
    public static ClaudeRunResult Ended(RunOutcome outcome, string model = SonnetModel, string? detail = null)
    {
        return new ClaudeRunResult(outcome, null, model, [new ModelAttempt(model, new ClaudeUsage(1_000, 10, 0, 0))], detail);
    }

    /// <summary>A comment written by the app's bot login, ending with the marker for <paramref name="headSha"/>.</summary>
    public static IssueComment OwnComment(long id, string headSha, ReviewRecordOutcome outcome, string author = AppLogin)
    {
        return new IssueComment(id, author, "Earlier result\n\n" + ReviewCommentMarker.For(headSha, outcome));
    }

    public static PullRequestFile ModifiedFile(string path, int lineCount = 10)
    {
        var lines = Enumerable.Range(1, lineCount).Select(_ => "+" + new string('x', 20));
        var patch = $"@@ -0,0 +1,{lineCount} @@\n" + string.Join("\n", lines);
        return new PullRequestFile(path, "modified", lineCount, 0, patch);
    }

    /// <summary>Adds a comment to a pull request, as another instance or a person writing at that moment would.</summary>
    public void AddComment(int prNumber, IssueComment comment)
    {
        lock (_gate)
        {
            CommentsOn(prNumber).Add(comment);
        }
    }

    public List<IssueComment> CommentsOn(int prNumber)
    {
        lock (_gate)
        {
            if (!_comments.TryGetValue(prNumber, out var comments))
            {
                comments = [];
                _comments[prNumber] = comments;
            }

            return comments;
        }
    }

    public ReviewOrchestrator BuildSut()
    {
        var options = Microsoft.Extensions.Options.Options.Create(Options);

        return new ReviewOrchestrator(
            Auth.Object,
            GitHub.Object,
            Claude.Object,
            new ReviewInputBuilder(options),
            new ModelSelector(options),
            new ReviewPublisher(GitHub.Object, NullLogger<ReviewPublisher>.Instance),
            InFlight,
            Limiter,
            Cache,
            Runtime.Object,
            options,
            Microsoft.Extensions.Options.Options.Create(ClaudeOptions),
            Time,
            Logger);
    }

    public Task<ReviewRunResult> RunAsync(ReviewQueueMessage? message = null, CancellationToken cancellationToken = default)
    {
        return BuildSut().ReviewPullRequestAsync(message ?? QueuedMessage, cancellationToken);
    }

    private void RecordToken(string operation, string token)
    {
        lock (_gate)
        {
            _usedTokens.Add((operation, token));
        }
    }

    private List<T> Snapshot<T>(List<T> items)
    {
        lock (_gate)
        {
            return items.ToList();
        }
    }
}
