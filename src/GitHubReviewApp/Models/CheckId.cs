namespace GitHubReviewApp.Models;

/// <summary>One of the eight checks of MoneyMe's Code Review Checklist. The order the checks are shown in is <see cref="Checklist.Order"/>.</summary>
public enum CheckId
{
    StandardsDesign,
    Functionality,
    Performance,
    Security,
    Testing,
    ErrorHandling,
    Scalability,
    Dependencies
}
