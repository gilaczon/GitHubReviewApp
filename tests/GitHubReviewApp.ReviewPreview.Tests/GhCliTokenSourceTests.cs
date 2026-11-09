namespace GitHubReviewApp.ReviewPreview.Tests;

public class GhCliTokenSourceTests
{
    [Fact]
    public async Task GetTokenAsync_GivenACommandThatPrintsAValue_Should_ReturnItTrimmed()
    {
        // Arrange — "dotnet --version" exists wherever the tests run and prints one line, standing in for "gh auth token"
        var sut = new GhCliTokenSource("dotnet", "--version");

        // Act
        var token = await sut.GetTokenAsync(CancellationToken.None);

        // Assert
        token.Should().NotBeEmpty().And.Be(token.Trim());
    }

    [Fact]
    public async Task GetTokenAsync_GivenTheCliIsNotInstalled_Should_SayHowToSignIn()
    {
        // Arrange
        var sut = new GhCliTokenSource("gh-that-is-not-installed-anywhere", "auth token");

        // Act & Assert
        await sut.Invoking(source => source.GetTokenAsync(CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*could not be started*gh auth login*");
    }

    [Fact]
    public async Task GetTokenAsync_GivenACommandThatFails_Should_SayHowToSignIn()
    {
        // Arrange — an unknown dotnet command exits with an error, as "gh auth token" does when nobody is signed in
        var sut = new GhCliTokenSource("dotnet", "no-such-command-anywhere");

        // Act & Assert
        await sut.Invoking(source => source.GetTokenAsync(CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*gave no token*gh auth login*");
    }
}
