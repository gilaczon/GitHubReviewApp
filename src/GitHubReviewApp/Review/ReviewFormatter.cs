namespace GitHubReviewApp.Review;

/// <summary>
/// Turns a merged report into what is posted on the pull request: a markdown body that states plainly what was and was not
/// reviewed, plus inline comments for findings that sit on lines GitHub will accept. The app's own statements come first and are
/// built from counts, never from model text. All model text passes through <see cref="MarkdownSanitizer"/>.
/// </summary>
public sealed class ReviewFormatter
{
    internal const int ClosingReserve = 4_200;
    internal const string CutNotice = "\n\n_The rest of this review was cut for length._\n\n";

    private const int MaxBodyChars = 60_000;
    private const int FindingsBudgetChars = 45_000;
    private const int FindingsHeadroomChars = 1_000;
    private const int MaxSummaryChars = 2_000;
    private const int MaxSummaries = 5;
    private const int MaxFindingTitleChars = 200;
    private const int MaxFindingBodyChars = 4_000;
    private const int MaxCategoryChars = 40;
    private const int MaxListedFileNames = 10;
    private const int MaxListedNameChars = 120;
    private const int ShortShaLength = 7;
    private const int GitHubFileListLimit = 3_000;

    private readonly ReviewOptions _options;

    public ReviewFormatter(IOptions<ReviewOptions> options)
    {
        _options = options.Value;
    }

    private sealed record CoverageFigures(
        int ChangedCount,
        int ReviewedCount,
        int DeletedCount,
        int UnlistedCount,
        int NotCompletedCount,
        int PartialCount,
        bool HasGaps);

    /// <param name="model">The model that reviewed the pull request, or null when nothing could be read and no model was run.</param>
    public FormattedReview Format(
        ReviewReport report,
        ReviewPlan plan,
        PullRequestInfo pullRequest,
        string? model,
        bool hasInlineComments)
    {
        var inlineFindings = new List<ReviewFinding>();
        var bodyFindings = new List<ReviewFinding>();

        foreach (var finding in report.Findings)
        {
            var canGoInline = hasInlineComments
                && finding.IsAnchored
                && inlineFindings.Count < _options.MaxInlineComments;

            (canGoInline ? inlineFindings : bodyFindings).Add(finding);
        }

        var comments = inlineFindings
            .Select(finding => new InlineReviewComment(finding.Path, finding.Line, FormatInlineBody(finding)))
            .ToList();

        var body = BuildBody(report, plan, pullRequest, model, inlineFindings.Count, bodyFindings);

        return new FormattedReview(body, comments);
    }

    internal static string CutForLength(string text, int limit)
    {
        var cut = Math.Max(limit - CutNotice.Length - ClosingReserve, 0);

        // Cut at the end of a line when there is one to cut at. The text of a code span is kept as written, so a span cut off before its closing
        // backtick would hand that text over as markup. Every code span this app writes or keeps is on one line.
        var lineEnd = cut > 0 && cut < text.Length ? text.LastIndexOf('\n', cut - 1) : -1;

        if (lineEnd > 0)
        {
            cut = lineEnd;
        }

        // Do not split a surrogate pair.
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        // The cut can land inside a code fence or the coverage details block. Left open, either would swallow the footer and its marker.
        var piece = MarkdownSanitizer.CloseOpenFences(text[..cut]);

        if (CountOccurrences(piece, "<details>") > CountOccurrences(piece, "</details>"))
        {
            piece += "\n</details>";
        }

        return piece + CutNotice;
    }

    private static CoverageFigures MeasureCoverage(ReviewReport report, ReviewPlan plan, PullRequestInfo pullRequest)
    {
        // GitHub lists at most 3,000 files, so the listing can be shorter than the pull request. Files it did not list were not reviewed.
        var unlistedCount = Math.Max(pullRequest.ChangedFileCount - plan.TotalFileCount, 0);
        var deletedCount = plan.SkippedFiles.Count(skipped => skipped.Reason == FileSkipReason.Deleted);
        var notCompletedCount = report.IncompleteChunks.Sum(chunk => chunk.FilePaths.Count);
        var partialCount = plan.PartialFileCount;
        var reviewedCount = Math.Max(plan.ReviewedFileCount - notCompletedCount, 0);
        var changedCount = plan.TotalFileCount - deletedCount + unlistedCount;

        var hasGaps = plan.SkippedFiles.Any(skipped => skipped.Reason != FileSkipReason.Deleted)
            || partialCount > 0
            || notCompletedCount > 0
            || unlistedCount > 0;

        return new CoverageFigures(changedCount, reviewedCount, deletedCount, unlistedCount, notCompletedCount, partialCount, hasGaps);
    }

    private string BuildBody(
        ReviewReport report,
        ReviewPlan plan,
        PullRequestInfo pullRequest,
        string? model,
        int inlineCount,
        List<ReviewFinding> bodyFindings)
    {
        var coverage = MeasureCoverage(report, plan, pullRequest);

        var head = new StringBuilder("## AI code review\n\n");
        AppendHeadline(head, report, coverage);

        if (inlineCount > 0)
        {
            var verb = inlineCount == 1 ? "finding is posted as an inline comment" : "findings are posted as inline comments";
            head.Append(inlineCount).Append(' ').Append(verb).Append(" on the changed lines.\n\n");
        }

        AppendCoverage(head, report, plan, coverage);

        if (report.HasUnreadableGuidelines)
        {
            head.Append("_Repository review guidelines were not applied: the app cannot read repository contents. Grant it the Contents: Read permission to use them._\n\n");
        }

        AppendSummaries(head, report.Summaries);

        var tail = new StringBuilder();

        if (report.OmittedFindingCount > 0)
        {
            var noun = report.OmittedFindingCount == 1 ? "finding was" : "findings were";
            tail.Append('_').Append(report.OmittedFindingCount).Append(" more lower-severity ").Append(noun)
                .Append(" omitted to keep this review readable._\n\n");
        }

        if (report.DroppedFindingCount > 0)
        {
            var phrase = report.DroppedFindingCount == 1 ? "entry in the model's reply could not be read and is" : "entries in the model's reply could not be read and are";
            tail.Append('_').Append(report.DroppedFindingCount).Append(' ').Append(phrase).Append(" not shown._\n\n");
        }

        if (IsLarge(pullRequest))
        {
            tail.Append("> **This pull request is large** (")
                .Append(pullRequest.ChangedFileCount).Append(" files, ")
                .Append(pullRequest.ChangedLineCount).Append(" changed lines). ")
                .Append("Smaller pull requests get more thorough reviews, so consider splitting it.\n\n");
        }

        var footer = BuildFooter(pullRequest, model, coverage);

        // What is left of the budget after the app's own statements goes to the findings, so they can never push the coverage out.
        var findingsBudget = Math.Clamp(MaxBodyChars - head.Length - tail.Length - footer.Length - FindingsHeadroomChars, 0, FindingsBudgetChars);
        var findings = new StringBuilder();
        AppendFindings(findings, bodyFindings, findingsBudget);

        // Last line of defence for the 65,536 character limit on a review body.
        var core = head.ToString() + findings + tail;
        var coreLimit = MaxBodyChars - footer.Length;

        if (core.Length > coreLimit)
        {
            core = CutForLength(core, coreLimit);
        }

        return core + footer;
    }

    private static string BuildFooter(PullRequestInfo pullRequest, string? model, CoverageFigures coverage)
    {
        var shortSha = pullRequest.HeadSha.Length > ShortShaLength ? pullRequest.HeadSha[..ShortShaLength] : pullRequest.HeadSha;
        var commit = MarkdownSanitizer.InlineCode(shortSha);

        // A model that was asked and read nothing (every part refused, cut off or unreadable) has not reviewed the change, so it is not credited
        // with having done so.
        var line = model is null
            ? $"<sub>Coverage report for commit `{commit}`. No AI model was run.</sub>"
            : coverage.ReviewedCount == 0
                ? $"<sub>Coverage report for commit `{commit}`. The AI model (`{MarkdownSanitizer.InlineCode(model, MaxListedNameChars)}`) reviewed none of the changes.</sub>"
                : $"<sub>Reviewed by Claude (`{MarkdownSanitizer.InlineCode(model, MaxListedNameChars)}`) at commit `{commit}`.</sub>";

        return line + "\n\n" + ReviewMarker.For(pullRequest.HeadSha);
    }

    private static void AppendHeadline(StringBuilder builder, ReviewReport report, CoverageFigures coverage)
    {
        if (report.Findings.Count > 0)
        {
            var counts = new[] { ReviewSeverity.Critical, ReviewSeverity.High, ReviewSeverity.Medium, ReviewSeverity.Low }
                .Select(severity => (Severity: severity, Count: report.Findings.Count(finding => finding.Severity == severity)))
                .Where(entry => entry.Count > 0)
                .Select(entry => $"{entry.Count} {entry.Severity.ToString().ToLowerInvariant()}");

            builder.Append("**Findings:** ").Append(report.Findings.Count)
                .Append(" (").Append(string.Join(", ", counts)).Append(")\n\n");
            return;
        }

        // Never claim a clean bill of health that the coverage does not support.
        if (coverage.ReviewedCount == 0)
        {
            builder.Append("**The AI reviewer did not read any of the changed files.** What was not reviewed is listed below.\n\n");
            return;
        }

        // Nor one that the model's own count contradicts: it said there were problems and left them out of the list.
        if (report.OmittedFindingCount > 0)
        {
            var noun = report.OmittedFindingCount == 1 ? "issue" : "issues";
            builder.Append("**The AI reviewer reported ").Append(report.OmittedFindingCount).Append(' ').Append(noun)
                .Append(" that this review does not list.** Treat it as incomplete.\n\n");
            return;
        }

        builder.Append(coverage.HasGaps
            ? "The AI reviewer found no issues in the parts it reviewed. Coverage is incomplete: see below.\n\n"
            : "The AI reviewer found no issues in the changes it reviewed. This is an automated review, not a security sign-off.\n\n");
    }

    private static void AppendCoverage(StringBuilder builder, ReviewReport report, ReviewPlan plan, CoverageFigures figures)
    {
        var deletedNote = figures.DeletedCount > 0
            ? $" Not counted: {Files(figures.DeletedCount)} deleted (no new code to review)."
            : string.Empty;

        if (!figures.HasGaps)
        {
            var scope = figures.ChangedCount == 1 ? "the changed file" : $"all {figures.ChangedCount} changed files";
            builder.Append("**Coverage:** reviewed ").Append(scope).Append('.').Append(deletedNote).Append("\n\n");
            return;
        }

        builder.Append("**Coverage:** reviewed ").Append(figures.ReviewedCount).Append(" of ").Append(ChangedFiles(figures.ChangedCount))
            .Append('.').Append(deletedNote).Append("\n\n");

        AppendGapSummary(builder, plan, figures);

        builder.Append("<details>\n<summary>Which files were not reviewed</summary>\n\n");

        foreach (var chunk in report.IncompleteChunks)
        {
            var label = plan.Chunks.Count > 1 ? $"Part {chunk.Index + 1} of {plan.Chunks.Count}" : "The review";
            builder.Append("- ").Append(label).Append(" could not be completed: ").Append(DescribeOutcome(chunk))
                .Append(". Files: ").Append(ListNames(chunk.FilePaths)).Append('\n');
        }

        AppendSkipped(builder, plan, FileSkipReason.DependencyLockfile, "Dependency lockfiles");
        AppendSkipped(builder, plan, FileSkipReason.BinaryOrMinified, "Binary or minified files");
        AppendSkipped(builder, plan, FileSkipReason.NoDiffAvailable, "No textual diff available");
        AppendSkipped(builder, plan, FileSkipReason.OverBudget, "Over the size budget");
        AppendSkipped(builder, plan, FileSkipReason.Deleted, "Deleted files");

        var partialPaths = plan.Chunks.SelectMany(chunk => chunk.Files).Where(planned => planned.IsPartial).Select(planned => planned.File.Path).ToList();

        if (partialPaths.Count > 0)
        {
            builder.Append("- Cut short at the size limit (").Append(partialPaths.Count).Append("): ")
                .Append(ListNames(partialPaths)).Append('\n');
        }

        builder.Append("\n</details>\n\n");
    }

    // One visible line per kind of gap, from counts only. The file names go in the details block, so a long list cannot bury these.
    private static void AppendGapSummary(StringBuilder builder, ReviewPlan plan, CoverageFigures figures)
    {
        var binaryPaths = plan.SkippedFiles.Where(skipped => skipped.Reason == FileSkipReason.BinaryOrMinified).Select(skipped => skipped.Path).ToList();
        var lines = new List<string>();

        AddGapLine(lines, figures.NotCompletedCount, "Could not be completed",
            "the model did not finish. The result stays attached to this commit, so push a new commit to request a fresh review.");
        AddGapLine(lines, figures.UnlistedCount, "Not listed by GitHub",
            $"GitHub lists at most {GitHubFileListLimit.ToString("N0", CultureInfo.InvariantCulture)} files for a pull request, so these were not reviewed.");
        AddGapLine(lines, Count(plan, FileSkipReason.OverBudget), "Over the review budget",
            "the pull request is larger than the reviewer can read.");
        AddGapLine(lines, Count(plan, FileSkipReason.NoDiffAvailable), "No diff from GitHub",
            "a rename, a binary, or a change too large for GitHub to diff.");
        AddGapLine(lines, Count(plan, FileSkipReason.DependencyLockfile), "Dependency lockfiles, not read",
            "check changes to package sources and hashes yourself.");
        AddGapLine(lines, binaryPaths.Count, "Binary or minified files, not read",
            binaryPaths.Any(DiffFileClassifier.IsExecutableOrArchive) ? "this includes executables or archives." : "nobody can review them as text.");
        AddGapLine(lines, figures.PartialCount, "Only partly read", "cut short by the size limit.");

        foreach (var line in lines)
        {
            builder.Append("- ").Append(line).Append('\n');
        }

        builder.Append('\n');
    }

    private static void AddGapLine(List<string> lines, int count, string label, string note)
    {
        if (count > 0)
        {
            lines.Add($"{label} ({count}): {note}");
        }
    }

    private static int Count(ReviewPlan plan, FileSkipReason reason)
    {
        return plan.SkippedFiles.Count(skipped => skipped.Reason == reason);
    }

    private static string Files(int count)
    {
        return count == 1 ? "1 file" : $"{count} files";
    }

    private static string ChangedFiles(int count)
    {
        return count == 1 ? "1 changed file" : $"{count} changed files";
    }

    // The model's own words come after everything the app states itself, quoted and labelled, so they cannot pass for the app's voice.
    private static void AppendSummaries(StringBuilder builder, IReadOnlyList<string> summaries)
    {
        var all = summaries
            .Select(summary => MarkdownSanitizer.Neutralize(SecretRedactor.Redact(summary.Trim()), MaxSummaryChars))
            .Where(summary => summary.Length > 0)
            .ToList();

        if (all.Count == 0)
        {
            return;
        }

        var lines = new List<string> { "**The AI model's summary** _(its own description of the change, not verified)_" };

        foreach (var summary in all.Take(MaxSummaries))
        {
            lines.Add(string.Empty);
            lines.AddRange(summary.Split('\n'));
        }

        if (all.Count > MaxSummaries)
        {
            lines.Add(string.Empty);
            lines.Add($"_{all.Count - MaxSummaries} more part summaries are not shown._");
        }

        builder.Append(string.Join('\n', lines.Select(line => line.Length == 0 ? ">" : "> " + line))).Append("\n\n");
    }

    private static void AppendFindings(StringBuilder builder, List<ReviewFinding> bodyFindings, int budgetChars)
    {
        if (bodyFindings.Count == 0)
        {
            return;
        }

        builder.Append("### Findings\n\n");

        var usedChars = 0;
        var notShownCount = 0;

        foreach (var finding in bodyFindings)
        {
            var item = FormatListItem(finding);

            if (usedChars + item.Length > budgetChars)
            {
                notShownCount++;
                continue;
            }

            builder.Append(item).Append('\n');
            usedChars += item.Length + 1;
        }

        builder.Append('\n');

        if (notShownCount > 0)
        {
            var noun = notShownCount == 1 ? "finding is" : "findings are";
            builder.Append('_').Append(notShownCount).Append(" more ").Append(noun)
                .Append(" not shown because of the review size limit._\n\n");
        }
    }

    private static void AppendSkipped(StringBuilder builder, ReviewPlan plan, FileSkipReason reason, string label)
    {
        var paths = plan.SkippedFiles.Where(skipped => skipped.Reason == reason).Select(skipped => skipped.Path).ToList();

        if (paths.Count > 0)
        {
            builder.Append("- ").Append(label).Append(" (").Append(paths.Count).Append("): ").Append(ListNames(paths)).Append('\n');
        }
    }

    private static string DescribeOutcome(IncompleteChunk chunk)
    {
        return chunk.Outcome switch
        {
            ChunkOutcome.OutputTruncated => "the model's output hit the length limit",
            ChunkOutcome.Rejected => "the AI service rejected this part of the request, for example because it was too large",
            ChunkOutcome.Refused => string.IsNullOrWhiteSpace(chunk.Detail)
                ? "the model declined to review"
                : $"the model declined to review (category: `{MarkdownSanitizer.InlineCode(chunk.Detail, MaxListedNameChars)}`)",
            _ => "the model's response could not be read"
        };
    }

    private static string ListNames(IReadOnlyList<string> paths)
    {
        var listed = paths.Take(MaxListedFileNames).Select(path => $"`{MarkdownSanitizer.InlineCode(path, MaxListedNameChars)}`");
        var text = string.Join(", ", listed);

        return paths.Count > MaxListedFileNames ? $"{text}, and {paths.Count - MaxListedFileNames} more" : text;
    }

    private static string FormatListItem(ReviewFinding finding)
    {
        var builder = new StringBuilder("- ");
        builder.Append(FormatLabel(finding)).Append(' ');

        var location = FormatLocation(finding);
        if (location.Length > 0)
        {
            builder.Append(location).Append(": ");
        }

        builder.Append(FormatTitle(finding));

        // A blank line, not just a line break: a body on the very next line is part of the app's own paragraph, and a first line of the body that reads as
        // a table's delimiter row would turn the app's line into the table's header. GitHub cuts a header at every pipe, those inside code spans too,
        // which leaves the text of the spans (a path, a quoted name) as live markup.
        var body = FormatBody(finding);
        if (body.Length > 0)
        {
            builder.Append("\n\n").Append("  ").Append(body.Replace("\n", "\n  ", StringComparison.Ordinal));
        }

        return builder.ToString();
    }

    private static string FormatInlineBody(ReviewFinding finding)
    {
        var heading = $"{FormatLabel(finding)} {FormatTitle(finding)}";
        var body = FormatBody(finding);

        return body.Length == 0 ? heading : heading + "\n\n" + body;
    }

    private static string FormatLabel(ReviewFinding finding)
    {
        return $"**[{finding.Severity} | {SanitizeCategory(finding.Category)}]**";
    }

    private static string FormatTitle(ReviewFinding finding)
    {
        // Credentials come out before the text is cut to length, so one that straddles the cut is not left half written.
        return MarkdownSanitizer.NeutralizeInline(SecretRedactor.Redact(finding.Title), MaxFindingTitleChars);
    }

    private static string FormatBody(ReviewFinding finding)
    {
        return MarkdownSanitizer.Neutralize(SecretRedactor.Redact(finding.Body.Trim()), MaxFindingBodyChars).Trim();
    }

    private static string FormatLocation(ReviewFinding finding)
    {
        // A path that matches none of the pull request's files is the model's own text, so a credential in it is removed like one in a title.
        var path = MarkdownSanitizer.InlineCode(SecretRedactor.Redact(finding.Path), MaxListedNameChars);

        if (path.Length == 0)
        {
            return string.Empty;
        }

        return finding.Line > 0 ? $"`{path}:{finding.Line}`" : $"`{path}`";
    }

    private static string SanitizeCategory(string category)
    {
        // A credential goes first: once the label is cut down to letters, digits and hyphens, a key no longer has the characters its shape asks for.
        var cleaned = new string(SecretRedactor.Redact(category).Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-').ToArray()).Trim('-');

        // The model is asked for one of eight names, but the parser takes any string. The label is cut short, because it sits in every inline comment,
        // and it is read as text like the rest of the model's words, so a category of "gh-5" does not become a link to issue 5.
        return cleaned.Length == 0 ? "other" : MarkdownSanitizer.NeutralizeInline(cleaned, MaxCategoryChars);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var position = text.IndexOf(value, StringComparison.Ordinal);

        while (position >= 0)
        {
            count++;
            position = text.IndexOf(value, position + value.Length, StringComparison.Ordinal);
        }

        return count;
    }

    private bool IsLarge(PullRequestInfo pullRequest)
    {
        return pullRequest.ChangedFileCount > _options.LargePullRequestFileThreshold
            || pullRequest.ChangedLineCount > _options.LargePullRequestLineThreshold;
    }
}
