namespace GitHubReviewApp.Models;

/// <summary>
/// One parsed line of a patch. <see cref="OldLine"/> is set for context and removed lines,
/// <see cref="NewLine"/> for context and added lines. <see cref="Text"/> excludes the leading diff marker.
/// </summary>
public sealed record PatchLine(PatchLineKind Kind, int? OldLine, int? NewLine, string Text);
