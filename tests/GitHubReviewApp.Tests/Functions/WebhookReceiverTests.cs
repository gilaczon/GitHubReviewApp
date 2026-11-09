namespace GitHubReviewApp.Tests.Functions;

public class WebhookReceiverTests
{
    private const string TestWebhookSecret = "test-webhook-secret-for-unit-tests";

    // ── Signature validation ─────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_GivenInvalidHmacSignature_Should_Respond401AndQueueNothing()
    {
        // Arrange
        var body = BuildPullRequestPayload("opened", isDraft: false);
        var request = BuildRequest(body,
            ("X-GitHub-Event", "pull_request"),
            ("X-Hub-Signature-256", "sha256=deadbeefdeadbeefdeadbeefdeadbeef00000000000000000000000000000000"));

        // Act
        var result = await BuildSut().RunAsync(request);

        // Assert
        StatusOf(result).Should().Be(HttpStatusCode.Unauthorized);
        result.QueueMessage.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_GivenSignatureMadeWithTheWrongSecret_Should_Respond401()
    {
        // Arrange
        var body = BuildPullRequestPayload("opened", isDraft: false);
        var request = BuildRequest(body,
            ("X-GitHub-Event", "pull_request"),
            ("X-Hub-Signature-256", ComputeSignature(body, "some-other-secret")));

        // Act
        var result = await BuildSut().RunAsync(request);

        // Assert
        StatusOf(result).Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RunAsync_GivenMissingSignatureHeader_Should_Respond401()
    {
        // Arrange
        var body = BuildPullRequestPayload("opened", isDraft: false);
        var request = BuildRequest(body, ("X-GitHub-Event", "pull_request"));

        // Act
        var result = await BuildSut().RunAsync(request);

        // Assert
        StatusOf(result).Should().Be(HttpStatusCode.Unauthorized);
        result.QueueMessage.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_GivenSignatureWithoutSha256Prefix_Should_Respond401()
    {
        // Arrange
        var body = BuildPullRequestPayload("opened", isDraft: false);
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
        StatusOf(result).Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RunAsync_GivenABadSignatureOnANonPullRequestEvent_Should_StillRespond401()
    {
        // Arrange — unsigned traffic is rejected before anything else about it is looked at
        var request = BuildRequest("{}",
            ("X-GitHub-Event", "push"),
            ("X-Hub-Signature-256", "sha256=00"));

        // Act
        var result = await BuildSut().RunAsync(request);

        // Assert
        StatusOf(result).Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RunAsync_GivenABody_Should_ValidateTheSignatureAgainstTheExactBytesReceived()
    {
        // Arrange — one changed character in the body must invalidate a signature made over the original
        var original = BuildPullRequestPayload("opened", isDraft: false);
        var tampered = original.Replace("Test PR", "Evil PR", StringComparison.Ordinal);
        var request = BuildRequest(tampered,
            ("X-GitHub-Event", "pull_request"),
            ("X-Hub-Signature-256", ComputeSignature(original, TestWebhookSecret)));

        // Act
        var result = await BuildSut().RunAsync(request);

        // Assert
        StatusOf(result).Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Misconfiguration must be loud ────────────────────────────────────────

    [Fact]
    public async Task RunAsync_GivenWebhookSecretNotConfigured_Should_ThrowSoGitHubSeesAFailedDelivery()
    {
        // Arrange — swallowing this would make every delivery look successful while nothing is reviewed
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var body = BuildPullRequestPayload("opened", isDraft: false);
        var request = BuildRequest(body,
            ("X-GitHub-Event", "pull_request"),
            ("X-Hub-Signature-256", ComputeSignature(body, TestWebhookSecret)));

        // Act
        var act = () => BuildSut(config).RunAsync(request);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*GitHubWebhookSecret*");
    }

    [Fact]
    public async Task RunAsync_GivenNoSecretAndNoSignatureHeader_Should_StillThrowSoEveryRequestFailsLoudly()
    {
        // Arrange — a misconfigured deployment must fail on every request, not only on well-formed ones
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var request = BuildRequest("{}", ("X-GitHub-Event", "pull_request"));

        // Act
        var act = () => BuildSut(config).RunAsync(request);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*GitHubWebhookSecret*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RunAsync_GivenAnEmptyWebhookSecret_Should_ThrowBecauseAnEmptyKeyLetsAnyoneForgeASignature(string secret)
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["GitHubWebhookSecret"] = secret })
            .Build();
        var body = BuildPullRequestPayload("opened", isDraft: false);
        var request = BuildRequest(body,
            ("X-GitHub-Event", "pull_request"),
            ("X-Hub-Signature-256", ComputeSignature(body, secret)));

        // Act
        var act = () => BuildSut(config).RunAsync(request);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── Event and action filtering ───────────────────────────────────────────

    [Theory]
    [InlineData("push")]
    [InlineData("ping")]
    [InlineData("issues")]
    public async Task RunAsync_GivenAnEventThatIsNotAPullRequest_Should_Respond200AndQueueNothing(string eventName)
    {
        // Arrange
        var body = BuildPullRequestPayload("opened", isDraft: false);
        var request = BuildRequest(body,
            ("X-GitHub-Event", eventName),
            ("X-Hub-Signature-256", ComputeSignature(body, TestWebhookSecret)));

        // Act
        var result = await BuildSut().RunAsync(request);

        // Assert
        StatusOf(result).Should().Be(HttpStatusCode.OK);
        result.QueueMessage.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_GivenMissingGitHubEventHeader_Should_Respond400()
    {
        // Arrange
        var body = BuildPullRequestPayload("opened", isDraft: false);
        var request = BuildRequest(body, ("X-Hub-Signature-256", ComputeSignature(body, TestWebhookSecret)));

        // Act
        var result = await BuildSut().RunAsync(request);

        // Assert
        StatusOf(result).Should().Be(HttpStatusCode.BadRequest);
        result.QueueMessage.Should().BeNull();
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("edited")]
    [InlineData("labeled")]
    [InlineData("assigned")]
    [InlineData("converted_to_draft")]
    [InlineData("")]
    public async Task RunAsync_GivenAPullRequestActionThatNeedsNoReview_Should_Respond200AndQueueNothing(string action)
    {
        // Arrange
        var body = BuildPullRequestPayload(action, isDraft: false);
        var request = BuildSignedPullRequestRequest(body);

        // Act
        var result = await BuildSut().RunAsync(request);

        // Assert
        StatusOf(result).Should().Be(HttpStatusCode.OK);
        result.QueueMessage.Should().BeNull();
    }

    [Theory]
    [InlineData("opened")]
    [InlineData("synchronize")]
    [InlineData("reopened")]
    [InlineData("ready_for_review")]
    public async Task RunAsync_GivenAnActionThatChangesWhatShouldBeReviewed_Should_Respond202AndQueueTheReview(string action)
    {
        // Arrange
        var body = BuildPullRequestPayload(action, isDraft: false);
        var request = BuildSignedPullRequestRequest(body);

        // Act
        var result = await BuildSut().RunAsync(request);

        // Assert
        StatusOf(result).Should().Be(HttpStatusCode.Accepted);
        result.QueueMessage.Should().NotBeNull();
    }

    [Fact]
    public async Task RunAsync_GivenADraftPullRequest_Should_Respond200AndQueueNothing()
    {
        // Arrange
        var body = BuildPullRequestPayload("opened", isDraft: true);
        var request = BuildSignedPullRequestRequest(body);

        // Act
        var result = await BuildSut().RunAsync(request);

        // Assert
        StatusOf(result).Should().Be(HttpStatusCode.OK);
        result.QueueMessage.Should().BeNull();
    }

    // ── Queue message ────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_GivenValidOpenedEvent_Should_PopulateAllReviewQueueMessageFields()
    {
        // Arrange
        var body = BuildFullPullRequestPayload(
            action: "opened", isDraft: false,
            owner: "my-org", repo: "my-service",
            prNumber: 42, prTitle: "feat: add awesome feature",
            baseSha: "abc123base", headSha: "def456head",
            installationId: 999888777L);
        var request = BuildSignedPullRequestRequest(body);

        // Act
        var result = await BuildSut().RunAsync(request);

        // Assert
        result.QueueMessage.Should().Be(new ReviewQueueMessage(
            Owner: "my-org",
            Repo: "my-service",
            PrNumber: 42,
            PrTitle: "feat: add awesome feature",
            BaseSha: "abc123base",
            HeadSha: "def456head",
            InstallationId: 999888777L));
    }

    [Fact]
    public async Task RunAsync_GivenAnAcceptedEvent_Should_ExplainTheOutcomeInTheResponseBodyForGitHubsDeliveryLog()
    {
        // Arrange
        var request = BuildSignedPullRequestRequest(BuildPullRequestPayload("opened", isDraft: false));

        // Act
        var result = await BuildSut().RunAsync(request);

        // Assert
        ((FakeHttpResponseData)result.HttpResponse).ReadBodyText().Should().Contain("queued");
    }

    // ── Malformed payloads ───────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{\"action\": \"opened\"")]
    [InlineData("null")]
    public async Task RunAsync_GivenAnUnreadablePayloadWithAValidSignature_Should_Respond400(string body)
    {
        // Arrange
        var request = BuildSignedPullRequestRequest(body);

        // Act
        var result = await BuildSut().RunAsync(request);

        // Assert
        StatusOf(result).Should().Be(HttpStatusCode.BadRequest);
        result.QueueMessage.Should().BeNull();
    }

    [Theory]
    [InlineData("""{"action":"opened"}""")]                                                                           // nothing else
    [InlineData("""{"action":"opened","pull_request":{"number":1,"head":{"sha":"h"}},"repository":{"name":"r","owner":{"login":""}},"installation":{"id":1}}""")]   // no owner
    [InlineData("""{"action":"opened","pull_request":{"number":0,"head":{"sha":"h"}},"repository":{"name":"r","owner":{"login":"o"}},"installation":{"id":1}}""")]  // no PR number
    [InlineData("""{"action":"opened","pull_request":{"number":1,"head":{"sha":""}},"repository":{"name":"r","owner":{"login":"o"}},"installation":{"id":1}}""")]   // no head sha
    [InlineData("""{"action":"opened","pull_request":{"number":1,"head":{"sha":"h"}},"repository":{"name":"r","owner":{"login":"o"}}}""")]                         // no installation
    public async Task RunAsync_GivenAPayloadMissingWhatAReviewNeeds_Should_Respond400InsteadOfQueueingABrokenMessage(string body)
    {
        // Arrange
        var request = BuildSignedPullRequestRequest(body);

        // Act
        var result = await BuildSut().RunAsync(request);

        // Assert
        StatusOf(result).Should().Be(HttpStatusCode.BadRequest);
        result.QueueMessage.Should().BeNull();
    }

    // ── Bodies that are not plain ASCII ──────────────────────────────────────

    private static byte[] Bytes(string text)
    {
        return Encoding.UTF8.GetBytes(text);
    }

    private static string ComputeSignature(byte[] body, string secret)
    {
        return "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)).ToLowerInvariant();
    }

    [Fact]
    public async Task RunAsync_GivenANonAsciiTitleSignedOverItsRawUtf8Bytes_Should_Respond202AndKeepTheTitle()
    {
        // Arrange — titles with accents, CJK and emoji are routine, and GitHub signs the bytes it sent
        const string title = "Añadir reintentos 日本語 🚀";
        var body = Bytes(BuildFullPullRequestPayload("opened", false, "o", "r", 5, title, "base", "head", 1L));
        var request = FakeHttpRequestData.CreateFromBytes(body, ("X-GitHub-Event", "pull_request"), ("X-Hub-Signature-256", ComputeSignature(body, TestWebhookSecret)));

        // Act
        var result = await BuildSut().RunAsync(request);

        // Assert
        StatusOf(result).Should().Be(HttpStatusCode.Accepted);
        result.QueueMessage!.PrTitle.Should().Be(title);
    }

    [Fact]
    public async Task RunAsync_GivenABodyWithAByteOrderMarkSignedOverItsRawBytes_Should_StillBeAccepted()
    {
        // Arrange — the signature covers the bytes as sent, mark included, so the mark must not change which bytes are hashed
        var json = Bytes(BuildPullRequestPayload("opened", isDraft: false));
        var body = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(json).ToArray();
        var request = FakeHttpRequestData.CreateFromBytes(body, ("X-GitHub-Event", "pull_request"), ("X-Hub-Signature-256", ComputeSignature(body, TestWebhookSecret)));

        // Act
        var result = await BuildSut().RunAsync(request);

        // Assert
        StatusOf(result).Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task RunAsync_GivenInvalidUtf8WithAValidSignature_Should_Respond400NotAnAuthenticationFailure()
    {
        // Arrange — decoding to a string and hashing that would have changed the bytes and reported 401 for a correctly signed delivery
        var body = new byte[] { 0x7B, 0xFF, 0xFE, 0x7D };
        var request = FakeHttpRequestData.CreateFromBytes(body, ("X-GitHub-Event", "pull_request"), ("X-Hub-Signature-256", ComputeSignature(body, TestWebhookSecret)));

        // Act
        var result = await BuildSut().RunAsync(request);

        // Assert
        StatusOf(result).Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Explicit nulls and missing objects ───────────────────────────────────

    [Theory]
    [InlineData("""{"action":"opened","pull_request":null,"repository":{"name":"r","owner":{"login":"o"}},"installation":{"id":1}}""")]
    [InlineData("""{"action":"opened","pull_request":{"number":1,"head":null},"repository":{"name":"r","owner":{"login":"o"}},"installation":{"id":1}}""")]
    [InlineData("""{"action":"opened","pull_request":{"number":1,"head":{"sha":"h"}},"repository":null,"installation":{"id":1}}""")]
    [InlineData("""{"action":"opened","pull_request":{"number":1,"head":{"sha":"h"}},"repository":{"name":"r","owner":null},"installation":{"id":1}}""")]
    [InlineData("""{"action":"opened","pull_request":{"number":1,"head":{"sha":"h"}},"repository":{"name":"r","owner":{"login":"o"}},"installation":null}""")]
    [InlineData("""{"action":"opened","pull_request":{"number":1,"head":{"sha":null}},"repository":{"name":null,"owner":{"login":null}},"installation":{"id":1}}""")]
    public async Task RunAsync_GivenAnExplicitNullWhereAReviewNeedsAValue_Should_Respond400NotACrash(string body)
    {
        // Arrange — System.Text.Json assigns a JSON null over a property initializer, so "= new()" is not protection
        var request = BuildSignedPullRequestRequest(body);

        // Act
        var result = await BuildSut().RunAsync(request);

        // Assert
        StatusOf(result).Should().Be(HttpStatusCode.BadRequest);
        result.QueueMessage.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_GivenANullTitleAndBase_Should_StillQueueTheReviewBecauseNeitherIsNeeded()
    {
        // Arrange — the processor reads the title and base commit from the API, so a payload without them is still usable
        const string body = """{"action":"opened","pull_request":{"number":1,"title":null,"base":null,"head":{"sha":"h"}},"repository":{"name":"r","owner":{"login":"o"}},"installation":{"id":1}}""";
        var request = BuildSignedPullRequestRequest(body);

        // Act
        var result = await BuildSut().RunAsync(request);

        // Assert
        StatusOf(result).Should().Be(HttpStatusCode.Accepted);
        result.QueueMessage.Should().Be(new ReviewQueueMessage("o", "r", 1, string.Empty, string.Empty, "h", 1L));
    }

    // ── Size limit ───────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_GivenADeclaredLengthOverGitHubsMaximum_Should_Respond413WithoutReadingTheBody()
    {
        // Arrange — an anonymous caller must not be able to make the app buffer an arbitrary amount of memory before the signature is checked
        var body = new EndlessZeroStream(WebhookReceiver.MaxBodyBytes + 1L);
        var request = FakeHttpRequestData.CreateFromStream(
            body, ("X-GitHub-Event", "pull_request"), ("Content-Length", (WebhookReceiver.MaxBodyBytes + 1L).ToString(System.Globalization.CultureInfo.InvariantCulture)));

        // Act
        var result = await BuildSut().RunAsync(request);

        // Assert
        StatusOf(result).Should().Be(HttpStatusCode.RequestEntityTooLarge);
        body.BytesRead.Should().Be(0);
        result.QueueMessage.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_GivenADeclaredLengthExactlyAtTheLimit_Should_ReadTheBodyAndGoOnToCheckTheSignature()
    {
        // Arrange — the limit is the largest body that is accepted, so one at exactly that size must reach the signature check, which this one fails
        var body = new EndlessZeroStream(WebhookReceiver.MaxBodyBytes);
        var request = FakeHttpRequestData.CreateFromStream(
            body, ("X-GitHub-Event", "pull_request"), ("Content-Length", WebhookReceiver.MaxBodyBytes.ToString(CultureInfo.InvariantCulture)));

        // Act
        var result = await BuildSut().RunAsync(request);

        // Assert
        StatusOf(result).Should().Be(HttpStatusCode.Unauthorized);
        body.BytesRead.Should().Be(WebhookReceiver.MaxBodyBytes);
    }

    [Fact]
    public async Task RunAsync_GivenABodyExactlyAtTheLimitAndNoDeclaredLength_Should_ReadItAllAndGoOnToCheckTheSignature()
    {
        // Arrange — the capped read, which a chunked upload goes through, must also stop at "more than" the limit and not at the limit
        var body = new EndlessZeroStream(WebhookReceiver.MaxBodyBytes);
        var request = FakeHttpRequestData.CreateFromStream(body, ("X-GitHub-Event", "pull_request"));

        // Act
        var result = await BuildSut().RunAsync(request);

        // Assert
        StatusOf(result).Should().Be(HttpStatusCode.Unauthorized);
        body.BytesRead.Should().Be(WebhookReceiver.MaxBodyBytes);
    }

    [Fact]
    public async Task RunAsync_GivenABodyOneByteOverTheLimitAndNoDeclaredLength_Should_Respond413()
    {
        // Arrange
        var body = new EndlessZeroStream(WebhookReceiver.MaxBodyBytes + 1L);
        var request = FakeHttpRequestData.CreateFromStream(body, ("X-GitHub-Event", "pull_request"));

        // Act
        var result = await BuildSut().RunAsync(request);

        // Assert
        StatusOf(result).Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task RunAsync_GivenABodyThatNeverStopsAndNoDeclaredLength_Should_Respond413AfterReadingNoMoreThanTheLimitAndOneBuffer()
    {
        // Arrange — a chunked upload has no Content-Length, so the read itself must be capped. The stream is a hundred times the limit: a read that
        // took the whole body before it counted would show, which a stream only twice the limit could never do against a bound that large
        var body = new EndlessZeroStream(WebhookReceiver.MaxBodyBytes * 100L);
        var request = FakeHttpRequestData.CreateFromStream(body, ("X-GitHub-Event", "pull_request"));

        // Act
        var result = await BuildSut().RunAsync(request);

        // Assert
        StatusOf(result).Should().Be(HttpStatusCode.RequestEntityTooLarge);
        body.BytesRead.Should().BeLessThanOrEqualTo(WebhookReceiver.MaxBodyBytes + WebhookReceiver.ReadBufferBytes);
    }

    [Fact]
    public async Task RunAsync_GivenADeclaredLengthOfTwoMebibytes_Should_Respond413WithoutReadingTheBody()
    {
        // Arrange — real pull_request payloads are tens of kilobytes, so the limit is far below GitHub's own 25 MiB ceiling: an anonymous caller
        // should not be able to make the app buffer more than a review could ever need
        const long twoMebibytes = 2L * 1024 * 1024;
        var body = new EndlessZeroStream(twoMebibytes);
        var request = FakeHttpRequestData.CreateFromStream(
            body, ("X-GitHub-Event", "pull_request"), ("Content-Length", twoMebibytes.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        // Act
        var result = await BuildSut().RunAsync(request);

        // Assert
        StatusOf(result).Should().Be(HttpStatusCode.RequestEntityTooLarge);
        body.BytesRead.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_GivenAPayloadWithTheLongestPullRequestDescriptionGitHubAllows_Should_StillQueueTheReview()
    {
        // Arrange — a description can hold 65,536 characters, each up to four bytes, so the limit must leave room for about a quarter of a megabyte
        var longestDescription = string.Concat(Enumerable.Repeat("\U0001F600", 65_536));
        var payload = "{\"action\":\"opened\",\"pull_request\":{\"number\":1,\"title\":\"t\",\"body\":\"" + longestDescription + "\",\"head\":{\"sha\":\"h\"}},"
            + "\"repository\":{\"name\":\"r\",\"owner\":{\"login\":\"o\"}},\"installation\":{\"id\":1}}";
        var request = BuildSignedPullRequestRequest(payload);

        // Act
        var result = await BuildSut().RunAsync(request);

        // Assert
        StatusOf(result).Should().Be(HttpStatusCode.Accepted);
    }

    // ── What is logged before the signature is checked ───────────────────────

    [Theory]
    [InlineData("72d3162e-cc78-11e3-81ab-4c9367dc0958", "72d3162e-cc78-11e3-81ab-4c9367dc0958")]
    [InlineData("not-a-guid", "invalid")]
    [InlineData("<script>alert(1)</script>", "invalid")]
    [InlineData("72d3162e-cc78-11e3-81ab-4c9367dc0958 and a forged log line", "invalid")]
    public async Task RunAsync_GivenADeliveryHeader_Should_PutOnlyAGuidInTheLogScopeBecauseItIsReadBeforeTheSignatureIsChecked(string header, string expected)
    {
        // Arrange — anyone can send this header, and it ends up in every log line of the request
        var logger = new ListLogger<WebhookReceiver>();
        var request = BuildRequest("{}", ("X-GitHub-Delivery", header));

        // Act
        await BuildSut(logger: logger).RunAsync(request);

        // Assert
        var scope = logger.Scopes.OfType<Dictionary<string, object>>().Should().ContainSingle().Subject;
        scope["DeliveryId"].Should().Be(expected);
    }

    [Fact]
    public async Task RunAsync_GivenNoDeliveryHeader_Should_LogTheDeliveryAsUnknown()
    {
        // Arrange
        var logger = new ListLogger<WebhookReceiver>();
        var request = BuildRequest("{}");

        // Act
        await BuildSut(logger: logger).RunAsync(request);

        // Assert
        var scope = logger.Scopes.OfType<Dictionary<string, object>>().Should().ContainSingle().Subject;
        scope["DeliveryId"].Should().Be("unknown");
    }

    [Fact]
    public async Task RunAsync_GivenAMisconfiguredSecretAndAnOversizedBody_Should_StillThrowSoTheMisconfigurationIsNeverMasked()
    {
        // Arrange
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var request = FakeHttpRequestData.CreateFromStream(
            new EndlessZeroStream(1), ("Content-Length", (WebhookReceiver.MaxBodyBytes + 1L).ToString(System.Globalization.CultureInfo.InvariantCulture)));

        // Act
        var act = () => BuildSut(config).RunAsync(request);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*GitHubWebhookSecret*");
    }

    // ── Which target branches are reviewed ───────────────────────────────────

    private static ReviewOptions ShippedTargetBranches()
    {
        return new ReviewOptions { TargetBranches = ["main", "master", "qa", "production"] };
    }

    private static string PayloadTargeting(string? baseBranch)
    {
        var refField = baseBranch is null ? string.Empty : $",\"ref\":\"{baseBranch}\"";

        return "{\"action\":\"opened\",\"pull_request\":{\"number\":1,\"title\":\"t\",\"base\":{\"sha\":\"b\"" + refField + "},\"head\":{\"sha\":\"h\"}},"
            + "\"repository\":{\"name\":\"r\",\"owner\":{\"login\":\"o\"}},\"installation\":{\"id\":1}}";
    }

    [Theory]
    [InlineData("main")]
    [InlineData("master")]
    [InlineData("qa")]
    [InlineData("production")]
    [InlineData("Main")]
    public async Task RunAsync_GivenAPullRequestIntoAListedBranch_Should_QueueTheReview(string baseBranch)
    {
        // Arrange
        var request = BuildSignedPullRequestRequest(PayloadTargeting(baseBranch));

        // Act
        var result = await BuildSut(options: ShippedTargetBranches()).RunAsync(request);

        // Assert
        StatusOf(result).Should().Be(HttpStatusCode.Accepted);
        result.QueueMessage.Should().NotBeNull();
    }

    [Theory]
    [InlineData("feature/retries")]
    [InlineData("develop")]
    [InlineData("main-backup")]
    [InlineData(null)]
    public async Task RunAsync_GivenAPullRequestIntoAnotherBranch_Should_IgnoreItWithoutQueuing(string? baseBranch)
    {
        // Arrange — a payload that does not name its base cannot be shown to target a listed branch
        var request = BuildSignedPullRequestRequest(PayloadTargeting(baseBranch));

        // Act
        var result = await BuildSut(options: ShippedTargetBranches()).RunAsync(request);

        // Assert
        StatusOf(result).Should().Be(HttpStatusCode.OK);
        result.QueueMessage.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_GivenATargetBranchEntryWithStrayWhitespace_Should_StillMatchTheBranch()
    {
        // Arrange — the validator accepts " qa ", so the receiver must read it the same way
        var options = new ReviewOptions { TargetBranches = [" qa "] };
        var request = BuildSignedPullRequestRequest(PayloadTargeting("qa"));

        // Act
        var result = await BuildSut(options: options).RunAsync(request);

        // Assert
        StatusOf(result).Should().Be(HttpStatusCode.Accepted);
    }

    [Theory]
    [InlineData("feature/retries")]
    [InlineData(null)]
    public async Task RunAsync_GivenNoTargetBranches_Should_QueuePullRequestsIntoEveryBranch(string? baseBranch)
    {
        // Arrange
        var request = BuildSignedPullRequestRequest(PayloadTargeting(baseBranch));

        // Act
        var result = await BuildSut(options: new ReviewOptions()).RunAsync(request);

        // Assert
        StatusOf(result).Should().Be(HttpStatusCode.Accepted);
    }

    // ── Who may trigger a review ─────────────────────────────────────────────

    private static string PayloadWithAssociation(string? association)
    {
        var field = association is null ? string.Empty : $"\"author_association\":\"{association}\",";

        return "{\"action\":\"opened\",\"pull_request\":{" + field + "\"number\":1,\"title\":\"t\",\"head\":{\"sha\":\"h\"}},"
            + "\"repository\":{\"name\":\"r\",\"owner\":{\"login\":\"o\"}},\"installation\":{\"id\":1}}";
    }

    [Theory]
    [InlineData("MEMBER")]
    [InlineData("owner")]
    [InlineData("COLLABORATOR")]
    public async Task RunAsync_GivenAnAllowListOfAuthorAssociations_Should_QueueTheReviewOfAnAuthorOnIt(string association)
    {
        // Arrange — an opt-in control for public repositories, where anyone can open a pull request and spend model tokens
        var options = new ReviewOptions { AllowedAuthorAssociations = ["OWNER", "MEMBER", "COLLABORATOR"] };
        var request = BuildSignedPullRequestRequest(PayloadWithAssociation(association));

        // Act
        var result = await BuildSut(options: options).RunAsync(request);

        // Assert
        StatusOf(result).Should().Be(HttpStatusCode.Accepted);
        result.QueueMessage.Should().NotBeNull();
    }

    [Theory]
    [InlineData("FIRST_TIME_CONTRIBUTOR")]
    [InlineData("NONE")]
    [InlineData(null)]
    public async Task RunAsync_GivenAnAllowListOfAuthorAssociations_Should_IgnoreTheReviewOfAnAuthorNotOnIt(string? association)
    {
        // Arrange
        var options = new ReviewOptions { AllowedAuthorAssociations = ["OWNER", "MEMBER", "COLLABORATOR"] };
        var request = BuildSignedPullRequestRequest(PayloadWithAssociation(association));

        // Act
        var result = await BuildSut(options: options).RunAsync(request);

        // Assert
        StatusOf(result).Should().Be(HttpStatusCode.OK);
        result.QueueMessage.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_GivenAnAllowListEntryWithStrayWhitespaceAndCase_Should_StillMatchTheAuthor()
    {
        // Arrange — the validator accepts " member " as MEMBER, so the receiver must read it the same way, or it would match nobody and every review would be skipped
        var options = new ReviewOptions { AllowedAuthorAssociations = [" member "] };
        var request = BuildSignedPullRequestRequest(PayloadWithAssociation("MEMBER"));

        // Act
        var result = await BuildSut(options: options).RunAsync(request);

        // Assert
        StatusOf(result).Should().Be(HttpStatusCode.Accepted);
    }

    [Theory]
    [InlineData("FIRST_TIME_CONTRIBUTOR")]
    [InlineData("NONE")]
    [InlineData(null)]
    public async Task RunAsync_GivenNoAllowList_Should_QueueEveryonesPullRequestsAsBefore(string? association)
    {
        // Arrange
        var request = BuildSignedPullRequestRequest(PayloadWithAssociation(association));

        // Act
        var result = await BuildSut().RunAsync(request);

        // Assert
        StatusOf(result).Should().Be(HttpStatusCode.Accepted);
    }

    // ── The on/off switch ────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_GivenReviewsSwitchedOff_Should_AnswerOkAndQueueNothing()
    {
        // Arrange
        var request = BuildSignedPullRequestRequest(BuildPullRequestPayload("opened", isDraft: false));

        // Act
        var result = await BuildSut(isReviewEnabled: false).RunAsync(request);

        // Assert
        StatusOf(result).Should().Be(HttpStatusCode.OK);
        result.QueueMessage.Should().BeNull();
        ((FakeHttpResponseData)result.HttpResponse).ReadBodyText().Should().Be("Ignored: reviews are switched off.");
    }

    [Fact]
    public async Task RunAsync_GivenReviewsSwitchedOffAndABadSignature_Should_StillAnswerUnauthorized()
    {
        // Arrange — the switch is looked at only after the caller has proved who it is
        var body = BuildPullRequestPayload("opened", isDraft: false);
        var request = BuildRequest(body,
            ("X-GitHub-Event", "pull_request"),
            ("X-Hub-Signature-256", ComputeSignature(body, "some-other-secret")));

        // Act
        var result = await BuildSut(isReviewEnabled: false).RunAsync(request);

        // Assert
        StatusOf(result).Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RunAsync_GivenReviewsSwitchedOffAndAnActionThatNeedsNoReview_Should_GiveTheActionsOwnAnswer()
    {
        // Arrange
        var request = BuildSignedPullRequestRequest(BuildPullRequestPayload("closed", isDraft: false));

        // Act
        var result = await BuildSut(isReviewEnabled: false).RunAsync(request);

        // Assert
        ((FakeHttpResponseData)result.HttpResponse).ReadBodyText().Should().Contain("needs no review");
    }

    [Fact]
    public async Task RunAsync_GivenReviewsSwitchedOffAndAnAuthorNotOnTheAllowList_Should_GiveTheAllowListsAnswer()
    {
        // Arrange — the switch is the last check (design 5.1, step 1), so the answers that mean "needs no review" are the same either way
        var options = new ReviewOptions { AllowedAuthorAssociations = ["OWNER", "MEMBER", "COLLABORATOR"] };
        var request = BuildSignedPullRequestRequest(PayloadWithAssociation("NONE"));

        // Act
        var result = await BuildSut(options: options, isReviewEnabled: false).RunAsync(request);

        // Assert
        StatusOf(result).Should().Be(HttpStatusCode.OK);
        result.QueueMessage.Should().BeNull();
        ((FakeHttpResponseData)result.HttpResponse).ReadBodyText().Should().Be("Ignored: the pull request author is not allowed to trigger a review.");
    }

    [Fact]
    public async Task RunAsync_GivenReviewsSwitchedOffAndAPayloadMissingRequiredFields_Should_StillAnswerBadRequest()
    {
        // Arrange — a malformed payload is the sender's fault whatever the switch says
        const string payload = "{\"action\":\"opened\",\"pull_request\":{\"number\":1,\"title\":\"t\"},\"repository\":{\"name\":\"r\",\"owner\":{\"login\":\"o\"}},\"installation\":{\"id\":1}}";
        var request = BuildSignedPullRequestRequest(payload);

        // Act
        var result = await BuildSut(isReviewEnabled: false).RunAsync(request);

        // Assert
        StatusOf(result).Should().Be(HttpStatusCode.BadRequest);
        result.QueueMessage.Should().BeNull();
    }

    // ── Factory helpers ──────────────────────────────────────────────────────

    private static HttpStatusCode StatusOf(WebhookOutput output)
    {
        return output.HttpResponse.StatusCode;
    }

    private static IRuntimeSettings RuntimeSettingsWith(bool isReviewEnabled)
    {
        var runtime = new Mock<IRuntimeSettings>();
        runtime.Setup(r => r.Current).Returns(RuntimeSettings.Default with { IsReviewEnabled = isReviewEnabled });

        return runtime.Object;
    }

    private static WebhookReceiver BuildSut(
        IConfiguration? config = null,
        ReviewOptions? options = null,
        ILogger<WebhookReceiver>? logger = null,
        bool isReviewEnabled = true)
    {
        config ??= new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GitHubWebhookSecret"] = TestWebhookSecret
            })
            .Build();

        return new WebhookReceiver(
            config,
            Options.Create(options ?? new ReviewOptions()),
            RuntimeSettingsWith(isReviewEnabled),
            logger ?? NullLogger<WebhookReceiver>.Instance);
    }

    private static FakeHttpRequestData BuildRequest(
        string body, params (string Name, string Value)[] headers)
        => FakeHttpRequestData.Create(body, headers: headers);

    private static FakeHttpRequestData BuildSignedPullRequestRequest(string body)
        => BuildRequest(body,
            ("X-GitHub-Event", "pull_request"),
            ("X-Hub-Signature-256", ComputeSignature(body, TestWebhookSecret)));

    private static string ComputeSignature(string body, string secret)
    {
        var hash = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(body));
        return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    // ── Payload builders ─────────────────────────────────────────────────────

    private static string BuildPullRequestPayload(string action, bool isDraft)
        => BuildFullPullRequestPayload(
            action: action, isDraft: isDraft,
            owner: "test-owner", repo: "test-repo",
            prNumber: 1, prTitle: "Test PR",
            baseSha: "baseSha123", headSha: "headSha456",
            installationId: 1L);

    private static string BuildFullPullRequestPayload(
        string action, bool isDraft,
        string owner, string repo,
        int prNumber, string prTitle,
        string baseSha, string headSha,
        long installationId)
    {
        var draftValue = isDraft ? "true" : "false";
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
