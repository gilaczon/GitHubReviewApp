namespace GitHubReviewApp.ReviewPreview.Tests;

public class PreviewAuthServiceTests
{
    private const string FirstToken = "gho_first";
    private const string SecondToken = "gho_second";
    private const string Repo = "my-service";

    private readonly Mock<IGitHubTokenSource> _tokenSource = new();

    public PreviewAuthServiceTests()
    {
        _tokenSource.SetupSequence(source => source.GetTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(FirstToken)
            .ReturnsAsync(SecondToken);
    }

    [Fact]
    public async Task GetInstallationTokenAsync_GivenTwoCalls_Should_AskTheCliOnce()
    {
        // Arrange
        var sut = new PreviewAuthService(_tokenSource.Object);

        // Act
        var first = await sut.GetInstallationTokenAsync(0, Repo, CancellationToken.None);
        var second = await sut.GetInstallationTokenAsync(0, Repo, CancellationToken.None);

        // Assert
        first.Should().Be(FirstToken);
        second.Should().Be(FirstToken);
        _tokenSource.Verify(source => source.GetTokenAsync(It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task ForgetInstallationToken_GivenATokenHeld_Should_FetchANewOneNextTime()
    {
        // Arrange
        var sut = new PreviewAuthService(_tokenSource.Object);
        await sut.GetInstallationTokenAsync(0, Repo, CancellationToken.None);

        // Act
        sut.ForgetInstallationToken(0, Repo);
        var token = await sut.GetInstallationTokenAsync(0, Repo, CancellationToken.None);

        // Assert
        token.Should().Be(SecondToken);
    }

    [Fact]
    public async Task GetAppLoginAsync_GivenAPreview_Should_BeALoginNoCommentCanHave()
    {
        // Arrange
        var sut = new PreviewAuthService(_tokenSource.Object);

        // Act
        var login = await sut.GetAppLoginAsync(CancellationToken.None);

        // Assert
        login.Should().Be(PreviewAuthService.PreviewLogin);
        sut.CanWriteChecks(0, Repo).Should().BeTrue("the review then builds its check run, which the preview captures");
    }
}
