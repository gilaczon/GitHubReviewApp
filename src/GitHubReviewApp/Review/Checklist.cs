namespace GitHubReviewApp.Review;

/// <summary>The eight checks of MoneyMe's Code Review Checklist: their order, the ids the model's answer uses for them, and the labels they are shown with.</summary>
public static class Checklist
{
    /// <summary>The note of a check the reviewer left out, or rated a concern without saying why.</summary>
    public const string NotAssessedNote = "Not assessed by the reviewer.";

    private static readonly Entry[] _entries =
    [
        new(CheckId.StandardsDesign, "standards-design", "Coding standards & design"),
        new(CheckId.Functionality, "functionality", "Functionality & implementation"),
        new(CheckId.Performance, "performance", "Performance"),
        new(CheckId.Security, "security", "Security"),
        new(CheckId.Testing, "testing", "Testing"),
        new(CheckId.ErrorHandling, "error-handling", "Error handling"),
        new(CheckId.Scalability, "scalability", "Scalability"),
        new(CheckId.Dependencies, "dependencies", "Dependencies")
    ];

    private sealed record Entry(CheckId Id, string WireId, string Label);

    /// <summary>The checks in the order the checklist lists them.</summary>
    public static IReadOnlyList<CheckId> Order { get; } = Array.AsReadOnly(_entries.Select(entry => entry.Id).ToArray());

    /// <summary>The id the model's answer uses for a check, for example "error-handling".</summary>
    public static string WireId(CheckId id)
    {
        return _entries.Single(entry => entry.Id == id).WireId;
    }

    /// <summary>The label a check is shown with, for example "Coding standards &amp; design".</summary>
    public static string Label(CheckId id)
    {
        return _entries.Single(entry => entry.Id == id).Label;
    }

    /// <summary>Finds the check a wire id names. The match is exact and case-sensitive, so "Security" and "security " name no check.</summary>
    public static bool TryParse(string? wireId, out CheckId id)
    {
        var entry = _entries.FirstOrDefault(candidate => string.Equals(candidate.WireId, wireId, StringComparison.Ordinal));

        id = entry?.Id ?? default;

        return entry is not null;
    }
}
