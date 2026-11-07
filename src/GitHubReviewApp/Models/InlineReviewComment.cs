namespace GitHubReviewApp.Models;

/// <summary>A comment attached to a line on the right (new) side of a pull request diff.</summary>
public sealed record InlineReviewComment(string Path, int Line, string Body);
