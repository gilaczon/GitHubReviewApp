namespace GitHubReviewApp.Models;

/// <summary>
/// A comment on a pull request's conversation, which GitHub keeps as an issue comment. <paramref name="Id"/> grows with every new comment, so the
/// lowest is the oldest. <paramref name="AuthorLogin"/> is empty for a comment GitHub gives no author, such as one from a deleted account.
/// </summary>
public sealed record IssueComment(long Id, string AuthorLogin, string Body);
