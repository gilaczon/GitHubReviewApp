namespace GitHubReviewApp.Tests.Review;

/// <summary>The checklist vocabulary: the eight checks, their order, their wire ids and their labels.</summary>
public class ChecklistTests
{
    [Fact]
    public void Order_GivenTheChecklist_Should_ListTheEightChecksInTheSpecOrder()
    {
        // Arrange
        var expected = new[]
        {
            CheckId.StandardsDesign,
            CheckId.Functionality,
            CheckId.Performance,
            CheckId.Security,
            CheckId.Testing,
            CheckId.ErrorHandling,
            CheckId.Scalability,
            CheckId.Dependencies
        };

        // Act
        var order = Checklist.Order;

        // Assert
        order.Should().Equal(expected);
    }

    [Theory]
    [InlineData(CheckId.StandardsDesign, "standards-design")]
    [InlineData(CheckId.Functionality, "functionality")]
    [InlineData(CheckId.Performance, "performance")]
    [InlineData(CheckId.Security, "security")]
    [InlineData(CheckId.Testing, "testing")]
    [InlineData(CheckId.ErrorHandling, "error-handling")]
    [InlineData(CheckId.Scalability, "scalability")]
    [InlineData(CheckId.Dependencies, "dependencies")]
    public void WireId_GivenACheck_Should_ReturnTheSpecId(CheckId id, string expectedWireId)
    {
        // Arrange — the check and its expected wire id come from the test data
        // Act
        var wireId = Checklist.WireId(id);

        // Assert
        wireId.Should().Be(expectedWireId);
    }

    [Theory]
    [InlineData(CheckId.StandardsDesign, "Coding standards & design")]
    [InlineData(CheckId.Functionality, "Functionality & implementation")]
    [InlineData(CheckId.Performance, "Performance")]
    [InlineData(CheckId.Security, "Security")]
    [InlineData(CheckId.Testing, "Testing")]
    [InlineData(CheckId.ErrorHandling, "Error handling")]
    [InlineData(CheckId.Scalability, "Scalability")]
    [InlineData(CheckId.Dependencies, "Dependencies")]
    public void Label_GivenACheck_Should_ReturnTheSpecLabel(CheckId id, string expectedLabel)
    {
        // Arrange — the check and its expected label come from the test data
        // Act
        var label = Checklist.Label(id);

        // Assert
        label.Should().Be(expectedLabel);
    }

    [Theory]
    [InlineData("standards-design", CheckId.StandardsDesign)]
    [InlineData("functionality", CheckId.Functionality)]
    [InlineData("performance", CheckId.Performance)]
    [InlineData("security", CheckId.Security)]
    [InlineData("testing", CheckId.Testing)]
    [InlineData("error-handling", CheckId.ErrorHandling)]
    [InlineData("scalability", CheckId.Scalability)]
    [InlineData("dependencies", CheckId.Dependencies)]
    public void TryParse_GivenASpecId_Should_ReturnItsCheck(string wireId, CheckId expectedId)
    {
        // Arrange — the wire id and the check it names come from the test data
        // Act
        var isParsed = Checklist.TryParse(wireId, out var id);

        // Assert
        isParsed.Should().BeTrue();
        id.Should().Be(expectedId);
    }

    [Theory]
    [InlineData("Security")]
    [InlineData("security ")]
    [InlineData("secure")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_GivenAnythingElse_Should_ReturnFalse(string? wireId)
    {
        // Arrange — the text that is not a wire id comes from the test data
        // Act
        var isParsed = Checklist.TryParse(wireId, out _);

        // Assert
        isParsed.Should().BeFalse();
    }
}
