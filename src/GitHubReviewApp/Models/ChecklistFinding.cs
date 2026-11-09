namespace GitHubReviewApp.Models;

/// <summary>One problem the model found: the check it belongs to, where it is, what is wrong and how to fix it.</summary>
public sealed record ChecklistFinding(CheckId Check, string File, int Line, string Issue, string Fix);
