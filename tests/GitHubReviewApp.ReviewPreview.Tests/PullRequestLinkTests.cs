namespace GitHubReviewApp.ReviewPreview.Tests;

public class PullRequestLinkTests
{
    [Theory]
    [InlineData("https://github.com/my-org/my-service/pull/7")]
    [InlineData("https://github.com/my-org/my-service/pull/7/")]
    [InlineData("https://github.com/my-org/my-service/pull/7/files")]
    [InlineData("https://github.com/my-org/my-service/pull/7#issuecomment-1")]
    [InlineData("https://github.com/my-org/my-service/pull/7?w=1")]
    [InlineData("http://www.github.com/my-org/my-service/pull/7")]
    [InlineData("  https://github.com/my-org/my-service/pull/7  ")]
    [InlineData("my-org/my-service#7")]
    public void TryParse_GivenAPullRequestLink_Should_ReadTheOwnerRepositoryAndNumber(string text)
    {
        // Arrange
        var expected = new PullRequestLink("my-org", "my-service", 7);

        // Act
        var isParsed = PullRequestLink.TryParse(text, out var link);

        // Assert
        isParsed.Should().BeTrue();
        link.Should().Be(expected);
    }

    [Theory]
    [InlineData("https://github.com/my-org/my-service/issues/7")]
    [InlineData("https://github.com/my-org/my-service/commit/abc123")]
    [InlineData("https://gitlab.com/my-org/my-service/pull/7")]
    [InlineData("https://github.com/my-org/my-service/pull/0")]
    [InlineData("https://github.com/my-org/my-service/pull/")]
    [InlineData("https://github.com.evil.example/my-org/my-service/pull/7")]
    [InlineData("my-org/my-service")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_GivenSomethingElse_Should_RefuseIt(string? text)
    {
        // Arrange — an issue, a commit, another host, a lookalike host, no number, or nothing

        // Act
        var isParsed = PullRequestLink.TryParse(text, out var link);

        // Assert
        isParsed.Should().BeFalse();
        link.Should().BeNull();
    }

    [Fact]
    public void Url_GivenALink_Should_BeThePullRequestsPage()
    {
        // Arrange
        var link = new PullRequestLink("my-org", "my-service", 7);

        // Act
        var url = link.Url;

        // Assert
        url.Should().Be("https://github.com/my-org/my-service/pull/7");
        link.Name.Should().Be("my-org/my-service#7");
    }
}
