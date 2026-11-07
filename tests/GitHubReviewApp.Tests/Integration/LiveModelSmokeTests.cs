namespace GitHubReviewApp.Tests.Integration;

/// <summary>
/// Calls the real Anthropic API once with a tiny diff. Everything else in this suite talks to a fake, so this is the only check that the real
/// service accepts the request this app sends (model name, structured-output schema, effort, streaming) and that the real reply reads back
/// as a review. It is skipped unless opted in (see <see cref="LiveFactAttribute"/>) and has never been run by the people who wrote it:
/// it needs a key, and it costs money.
/// </summary>
[Trait("Category", "Live")]
public class LiveModelSmokeTests
{
    private const string TinyPatch =
        "@@ -0,0 +1,7 @@\n"
        + "+public string? FindUser(SqlConnection connection, string name)\n"
        + "+{\n"
        + "+    var command = connection.CreateCommand();\n"
        + "+    command.CommandText = \"SELECT Id FROM Users WHERE Name = '\" + name + \"'\";\n"
        + "+    using var reader = command.ExecuteReader();\n"
        + "+    return reader.Read() ? reader.GetString(0) : null;\n"
        + "+}";

    [LiveFact]
    public async Task ReviewAsync_GivenTheRealApiAndATinyDiff_Should_ReturnACompletedReviewReadableByTheParser()
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AnthropicApiKey"] = Environment.GetEnvironmentVariable(LiveFactAttribute.KeyVariable) })
            .Build();
        var options = new ClaudeOptions { MaxOutputTokens = 8_000, RequestTimeoutSeconds = 120, MaxRetries = 1 };
        var sut = new ClaudeService(new HttpClient(), configuration, Options.Create(options), NullLogger<ClaudeService>.Instance);

        var plan = new ReviewPlanner(Options.Create(new ReviewOptions()))
            .CreatePlan([new PullRequestFile("src/UserRepository.cs", "added", 7, 0, TinyPatch)]);
        var pullRequest = new PullRequestInfo("open", false, "Add user lookup", "a".PadRight(40, 'a'), "b".PadRight(40, 'b'), 1, 7, 0);
        var request = new ClaudeReviewRequest(
            ReviewPromptBuilder.BuildSystemPrompt(null),
            ReviewPromptBuilder.BuildUserMessage(pullRequest, plan.Chunks[0], plan.Chunks.Count));

        // Act
        var result = await sut.ReviewAsync(request, CancellationToken.None);

        // Assert — only the shape is checked: what a model finds in a diff is not something a test can promise
        result.Outcome.Should().Be(ChunkOutcome.Completed, "the real API must accept the request and return a review the parser can read");
        result.Summary.Should().NotBeNullOrWhiteSpace();
        result.Model.Should().StartWith("claude");
        result.Usage.OutputTokens.Should().BeGreaterThan(0);
    }
}
