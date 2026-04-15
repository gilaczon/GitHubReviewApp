namespace GitHubReviewApp.Tests;

public class WebhookReceiverTests
{
    private const string TestWebhookSecret = "test-webhook-secret-for-unit-tests";

    // ── Event type filtering ─────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_GivenNonPullRequestEvent_Should_ReturnNull()
    {
        // Arrange
        var body = BuildPullRequestPayload("opened", draft: false);
        var request = BuildRequest(body,
            ("X-GitHub-Event", "push"),
            ("X-Hub-Signature-256", ComputeSignature(body, TestWebhookSecret)));

        // Act
        var result = await BuildSut().RunAsync(request);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_GivenMissingGitHubEventHeader_Should_ReturnNull()
    {
        // Arrange
        var body = BuildPullRequestPayload("opened", draft: false);
        var request = BuildRequest(body,
            ("X-Hub-Signature-256", ComputeSignature(body, TestWebhookSecret)));

        // Act
        var result = await BuildSut().RunAsync(request);

        // Assert
        result.Should().BeNull();
    }

    // ── Signature validation ─────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_GivenInvalidHmacSignature_Should_ReturnNull()
    {
        // Arrange
        var body = BuildPullRequestPayload("opened", draft: false);
        var request = BuildRequest(body,
            ("X-GitHub-Event", "pull_request"),
            ("X-Hub-Signature-256", "sha256=deadbeefdeadbeefdeadbeefdeadbeef00000000000000000000000000000000"));

        // Act
        var result = await BuildSut().RunAsync(request);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_GivenMissingSignatureHeader_Should_ReturnNull()
    {
        // Arrange
        var body = BuildPullRequestPayload("opened", draft: false);
        var request = BuildRequest(body,
            ("X-GitHub-Event", "pull_request"));

        // Act
        var result = await BuildSut().RunAsync(request);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_GivenSignatureWithoutSha256Prefix_Should_ReturnNull()
    {
        // Arrange
        var body = BuildPullRequestPayload("opened", draft: false);
        var rawHex = Convert.ToHexString(
            HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(TestWebhookSecret),
                Encoding.UTF8.GetBytes(body)))
            .ToLowerInvariant();
        var request = BuildRequest(body,
            ("X-GitHub-Event", "pull_request"),
            ("X-Hub-Signature-256", rawHex));   // missing "sha256=" prefix

        // Act
        var result = await BuildSut().RunAsync(request);

        // Assert
        result.Should().BeNull();
    }

    // ── PR action filtering ──────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_GivenPullRequestClosedAction_Should_ReturnNull()
    {
        // Arrange
        var body = BuildPullRequestPayload("closed", draft: false);
        var request = BuildRequest(body,
            ("X-GitHub-Event", "pull_request"),
            ("X-Hub-Signature-256", ComputeSignature(body, TestWebhookSecret)));

        // Act
        var result = await BuildSut().RunAsync(request);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_GivenPullRequestReopenedAction_Should_ReturnNull()
    {
        // Arrange
        var body = BuildPullRequestPayload("reopened", draft: false);
        var request = BuildRequest(body,
            ("X-GitHub-Event", "pull_request"),
            ("X-Hub-Signature-256", ComputeSignature(body, TestWebhookSecret)));

        // Act
        var result = await BuildSut().RunAsync(request);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_GivenDraftPullRequest_Should_ReturnNull()
    {
        // Arrange
        var body = BuildPullRequestPayload("opened", draft: true);
        var request = BuildRequest(body,
            ("X-GitHub-Event", "pull_request"),
            ("X-Hub-Signature-256", ComputeSignature(body, TestWebhookSecret)));

        // Act
        var result = await BuildSut().RunAsync(request);

        // Assert
        result.Should().BeNull();
    }

    // ── Happy path ───────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_GivenOpenedPullRequestWithValidSignature_Should_ReturnReviewQueueMessage()
    {
        // Arrange
        var body = BuildPullRequestPayload("opened", draft: false);
        var request = BuildRequest(body,
            ("X-GitHub-Event", "pull_request"),
            ("X-Hub-Signature-256", ComputeSignature(body, TestWebhookSecret)));

        // Act
        var result = await BuildSut().RunAsync(request);

        // Assert
        result.Should().NotBeNull().And.BeOfType<ReviewQueueMessage>();
    }

    [Fact]
    public async Task RunAsync_GivenSynchronizePullRequestWithValidSignature_Should_ReturnReviewQueueMessage()
    {
        // Arrange
        var body = BuildPullRequestPayload("synchronize", draft: false);
        var request = BuildRequest(body,
            ("X-GitHub-Event", "pull_request"),
            ("X-Hub-Signature-256", ComputeSignature(body, TestWebhookSecret)));

        // Act
        var result = await BuildSut().RunAsync(request);

        // Assert
        result.Should().NotBeNull();
    }

    // ── ReviewQueueMessage field population ──────────────────────────────────

    [Fact]
    public async Task RunAsync_GivenValidOpenedEvent_Should_PopulateAllReviewQueueMessageFields()
    {
        // Arrange
        var body = BuildFullPullRequestPayload(
            action: "opened", draft: false,
            owner: "my-org", repo: "my-service",
            prNumber: 42, prTitle: "feat: add awesome feature",
            baseSha: "abc123base", headSha: "def456head",
            installationId: 999888777L);

        var request = BuildRequest(body,
            ("X-GitHub-Event", "pull_request"),
            ("X-Hub-Signature-256", ComputeSignature(body, TestWebhookSecret)));

        // Act
        var result = await BuildSut().RunAsync(request);

        // Assert
        result.Should().NotBeNull();
        result!.Owner.Should().Be("my-org");
        result.Repo.Should().Be("my-service");
        result.PrNumber.Should().Be(42);
        result.PrTitle.Should().Be("feat: add awesome feature");
        result.BaseSha.Should().Be("abc123base");
        result.HeadSha.Should().Be("def456head");
        result.InstallationId.Should().Be(999888777L);
    }

    // ── Exception handling ───────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_GivenWebhookSecretNotConfigured_Should_ReturnNull()
    {
        // Arrange — empty config means the secret key is absent; ValidateSignature throws
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        var sut = BuildSut(config);
        var body = BuildPullRequestPayload("opened", draft: false);
        var request = BuildRequest(body,
            ("X-GitHub-Event", "pull_request"),
            ("X-Hub-Signature-256", ComputeSignature(body, TestWebhookSecret)));

        // Act
        var result = await sut.RunAsync(request);

        // Assert
        result.Should().BeNull();
    }

    // ── Factory helpers ──────────────────────────────────────────────────────

    private static WebhookReceiver BuildSut(IConfiguration? config = null)
    {
        config ??= new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GitHubWebhookSecret"] = TestWebhookSecret
            })
            .Build();

        return new WebhookReceiver(config, NullLogger<WebhookReceiver>.Instance);
    }

    private static FakeHttpRequestData BuildRequest(
        string body, params (string Name, string Value)[] headers)
        => FakeHttpRequestData.Create(body, headers: headers);

    private static string ComputeSignature(string body, string secret)
    {
        var hash = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(body));
        return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    // ── Payload builders ─────────────────────────────────────────────────────

    private static string BuildPullRequestPayload(string action, bool draft)
        => BuildFullPullRequestPayload(
            action: action, draft: draft,
            owner: "test-owner", repo: "test-repo",
            prNumber: 1, prTitle: "Test PR",
            baseSha: "baseSha123", headSha: "headSha456",
            installationId: 1L);

    private static string BuildFullPullRequestPayload(
        string action, bool draft,
        string owner, string repo,
        int prNumber, string prTitle,
        string baseSha, string headSha,
        long installationId)
    {
        var draftValue = draft ? "true" : "false";
        return $$"""
            {
              "action": "{{action}}",
              "pull_request": {
                "number": {{prNumber}},
                "title": "{{prTitle}}",
                "draft": {{draftValue}},
                "base": { "sha": "{{baseSha}}" },
                "head": { "sha": "{{headSha}}" }
              },
              "repository": {
                "name": "{{repo}}",
                "owner": { "login": "{{owner}}" }
              },
              "installation": {
                "id": {{installationId}}
              }
            }
            """;
    }
}
