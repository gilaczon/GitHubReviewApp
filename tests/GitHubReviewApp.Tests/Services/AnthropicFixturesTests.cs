namespace GitHubReviewApp.Tests.Services;

/// <summary>
/// The Anthropic fixtures promise to look like what the API sends. A test that relies on one of those promises passes without it too (a stream
/// that never trickles still decodes), so each promise is checked here, where its absence is noticed.
/// </summary>
public class AnthropicFixturesTests
{
    private static async Task<string[]> EventNamesAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();

        return Regex.Matches(body, "^event: (.+)$", RegexOptions.Multiline).Select(match => match.Groups[1].Value).ToArray();
    }

    [Fact]
    public async Task Message_GivenAnyReply_Should_SendAPingAfterTheFirstBlockStarts()
    {
        // Arrange
        var response = AnthropicFixtures.Message();

        // Act
        var names = await EventNamesAsync(response);

        // Assert
        names.Take(4).Should().Equal("message_start", "content_block_start", "ping", "content_block_delta");
    }

    [Fact]
    public async Task Message_GivenAReplyWithSeveralBlocks_Should_SendAPingAfterEachBlockStarts()
    {
        // Arrange
        var response = AnthropicFixtures.Message([AnthropicFixtures.ThinkingBlock(), AnthropicFixtures.TextBlock(AnthropicFixtures.ReviewJson())]);

        // Act
        var names = await EventNamesAsync(response);

        // Assert
        names.Count(name => name == "ping").Should().Be(2);
    }

    [Fact]
    public async Task Message_GivenCharactersThatJsonWritersEscapeByDefault_Should_WriteThemAsTheyAre()
    {
        // Arrange
        var response = AnthropicFixtures.Message([AnthropicFixtures.TextBlock("a < b && c > d, café")]);

        // Act
        var body = await response.Content.ReadAsStringAsync();

        // Assert
        body.Should().Contain("<").And.Contain("&&").And.Contain("é");
        body.Should().NotContain("\\u003C").And.NotContain("\\u0026").And.NotContain("\\u00E9");
    }

    [Fact]
    public async Task Message_GivenRestatedUsage_Should_StartWithProvisionalFiguresAndEndWithTheFinalOnes()
    {
        // Arrange
        var response = AnthropicFixtures.Message(
            inputTokens: 1_200, outputTokens: 300, cacheReadTokens: 800, cacheCreationTokens: 50, isUsageRestatedInFinalEvent: true);

        // Act
        var body = await response.Content.ReadAsStringAsync();

        // Assert
        var start = body[..body.IndexOf("event: content_block_start", StringComparison.Ordinal)];
        var final = body[body.IndexOf("event: message_delta", StringComparison.Ordinal)..];
        start.Should().Contain("\"input_tokens\":1,").And.Contain("\"cache_read_input_tokens\":0");
        final.Should().Contain("\"input_tokens\":1200").And.Contain("\"output_tokens\":300").And.Contain("\"cache_read_input_tokens\":800").And.Contain("\"cache_creation_input_tokens\":50");
    }

    [Fact]
    public async Task Message_GivenNoRestatedUsage_Should_CarryOnlyTheOutputTokensInTheFinalEvent()
    {
        // Arrange
        var response = AnthropicFixtures.Message(inputTokens: 1_200, outputTokens: 300);

        // Act
        var body = await response.Content.ReadAsStringAsync();

        // Assert
        var final = body[body.IndexOf("event: message_delta", StringComparison.Ordinal)..];
        final.Should().Contain("\"output_tokens\":300").And.NotContain("input_tokens");
    }

    [Fact]
    public async Task Message_GivenBytesPerRead_Should_HandOutOnlyThatManyBytesPerReadAndTheWholeReplyInTheEnd()
    {
        // Arrange
        var expected = await AnthropicFixtures.Message().Content.ReadAsStringAsync();
        await using var stream = await AnthropicFixtures.Message(bytesPerRead: 3).Content.ReadAsStreamAsync();
        var buffer = new byte[64];

        // Act
        var firstRead = await stream.ReadAsync(buffer);
        using var rest = new MemoryStream();
        await stream.CopyToAsync(rest);
        var whole = Encoding.UTF8.GetString(buffer[..firstRead].Concat(rest.ToArray()).ToArray());

        // Assert
        firstRead.Should().Be(3);
        whole.Should().Be(expected);
    }
}
