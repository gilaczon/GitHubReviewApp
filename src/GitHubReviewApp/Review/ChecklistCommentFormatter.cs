namespace GitHubReviewApp.Review;

/// <summary>
/// Writes the comment the app posts on a pull request in the spec's layout (design 5.8), the "Review failed" notice, and the check run text made
/// from either. The model's summary, notes and findings, the author's branch names and login, the file paths and the model ids are text the app
/// did not write: credentials come out of it before it is cut to its limit (for the model's text, <see cref="ChecklistAnswerReader"/> has done
/// both once already), and it is rendered so that no mention, link, image, HTML or math in it is live. The app's own words and counts are
/// written as they are, and so are the repository's name, the pull request's number and the commit, which come from GitHub.
/// </summary>
internal static class ChecklistCommentFormatter
{
    private const string PassIcon = "✅";

    // The warning sign, then the selector (U+FE0F) that asks for its emoji form. The selector is invisible, so it is written as an escape.
    private const string ConcernIcon = "⚠\uFE0F";

    private const string FailIcon = "❌";
    private const string NotApplicableIcon = "➖";
    private const string StricterRatingShown = "Reviewed twice; the stricter rating of each check is shown.";
    private const int ShortShaLength = 10;

    // A heading (one to six number signs, then a space, a tab or nothing more) or a code fence (at least three backticks or tildes) at the
    // start of a text.
    private static readonly Regex _headingOrFence = new(
        @"\A(?:#{1,6}(?=[ \t]|\z)|`{3}|~{3})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The comment for a finished review: the headline, the summary, whether the runs agreed, the eight checks, the counts, the findings to fix
    /// (<paramref name="shownFindings"/>, in the order given), what was not reviewed in full, the footer and the marker. A file a finding names
    /// is shown as the changed file <paramref name="paths"/> matches it to, or as the model wrote it when it matches none.
    /// </summary>
    public static string FormatReview(
        ReviewContext context,
        MergedReview merged,
        ReviewOutcome outcome,
        IReadOnlyList<ChecklistFinding> shownFindings,
        PathResolver paths)
    {
        var parts = new List<string> { $"{outcome.Headline} - {outcome.Meaning}" };
        var summary = SanitizeSummary(merged.Summary);

        if (summary.Length > 0)
        {
            parts.Add(summary);
        }

        parts.Add(FormatAgreementLine(merged));
        parts.Add(string.Join('\n', merged.Checks.Select(FormatCheckLine).Prepend("Checklist (MoneyMe Code Review Checklist)")));
        parts.Add(FormatCountsLine(merged.Checks));

        if (shownFindings.Count > 0)
        {
            var statuses = merged.Checks.ToDictionary(check => check.Id, check => check.Status);
            var findingLines = shownFindings.Select(finding => FormatFindingLine(finding, statuses[finding.Check], paths));

            parts.Add(string.Join('\n', findingLines.Prepend("To fix, worst first")));
        }

        if (context.LeftOutFileCount > 0)
        {
            parts.Add(FormatCoverageLine(context, merged.CoverageNote));
        }

        var models = string.Join(" + ", context.Models.Select(model => Sanitize(model)));
        var footer = $"{FormatFooterStart(context)} ({context.IncludedFileCount} reviewed) · {models} at {ShortSha(context.HeadSha)}";

        parts.Add(footer + "\n" + ReviewCommentMarker.For(context.HeadSha, ReviewRecordOutcome.Reviewed));

        return string.Join("\n\n", parts);
    }

    /// <summary>
    /// The "Review failed" notice: no verdict, the reason, how to try again, and the footer without a model. <paramref name="reason"/> is the
    /// app's own words, one of <see cref="ReviewFailureReasons"/>, and is written as it is.
    /// </summary>
    public static string FormatFailure(ReviewContext context, string reason)
    {
        var footer = $"{FormatFooterStart(context)} · at {ShortSha(context.HeadSha)}";

        return string.Join(
            "\n\n",
            "Review failed - no verdict",
            reason,
            "Push a new commit, or close and re-open this pull request, to try again.",
            footer + "\n" + ReviewCommentMarker.For(context.HeadSha, ReviewRecordOutcome.Failed));
    }

    /// <summary>The check run's summary: the comment without its marker line, trimmed.</summary>
    public static string CheckRunSummary(string commentBody)
    {
        var body = commentBody.TrimEnd();
        var lastLineStart = body.LastIndexOf('\n') + 1;

        // The marker is the pull request's record of the review, which a check run, belonging to the commit, has no use for.
        return ReviewCommentMarker.TryRead(body[lastLineStart..], out _, out _) ? body[..lastLineStart].Trim() : body.Trim();
    }

    private static string FormatAgreementLine(MergedReview merged)
    {
        if (merged.RunCount == 1)
        {
            return "Reviewed once: the other review did not finish.";
        }

        if (merged.IsUnanimous)
        {
            return "Reviewed twice; both reviews agreed.";
        }

        // Two runs can rate a check differently and still come to the same verdict, so the verdicts are named only when they differ.
        return merged.OtherRunVerdict is { } otherVerdict && otherVerdict != merged.StricterRunVerdict
            ? $"{StricterRatingShown} They differed: {VerdictText(merged.StricterRunVerdict)} and {VerdictText(otherVerdict)}."
            : StricterRatingShown;
    }

    private static string FormatCheckLine(RatedCheck check)
    {
        var line = $"- {Icon(check.Status)} {Checklist.Label(check.Id)}";
        var note = Sanitize(check.Note, ChecklistAnswerReader.MaxNoteChars);

        // A pass has no note, and the reader keeps none for n/a. A fail can come without one: only a concern is given the placeholder.
        return check.Status switch
        {
            CheckStatus.Pass => line,
            CheckStatus.NotApplicable => line + ": not applicable",
            CheckStatus.Concern or CheckStatus.Fail when note.Length == 0 => line,
            CheckStatus.Concern or CheckStatus.Fail when check.IsSerious => $"{line}: SERIOUS: {note}",
            CheckStatus.Concern or CheckStatus.Fail => $"{line}: {note}",
            _ => throw new UnreachableException($"The check status {check.Status} has no line in the checklist.")
        };
    }

    private static string FormatCountsLine(IReadOnlyList<RatedCheck> checks)
    {
        string[] counts =
        [
            FormatCount(checks, CheckStatus.Pass, "pass", "pass"),
            FormatCount(checks, CheckStatus.Concern, "concern", "concerns"),
            FormatCount(checks, CheckStatus.Fail, "fail", "fails"),
            FormatCount(checks, CheckStatus.NotApplicable, "n/a", "n/a")
        ];

        return string.Join(", ", counts.Where(count => count.Length > 0));
    }

    private static string FormatCount(IReadOnlyList<RatedCheck> checks, CheckStatus status, string singular, string plural)
    {
        // A status no check has is left out of the line.
        var count = checks.Count(check => check.Status == status);

        return count switch
        {
            0 => string.Empty,
            1 => $"1 {singular}",
            _ => $"{count} {plural}"
        };
    }

    private static string FormatFindingLine(ChecklistFinding finding, CheckStatus status, PathResolver paths)
    {
        var issue = Sanitize(finding.Issue, ChecklistAnswerReader.MaxIssueChars);
        var fix = Sanitize(finding.Fix, ChecklistAnswerReader.MaxFixChars);
        var line = $"- {Icon(status)} {Checklist.Label(finding.Check)}{FormatLocation(finding, paths)}: {issue}";

        return fix.Length > 0 ? $"{line} Fix: {fix}" : line;
    }

    private static string FormatLocation(ChecklistFinding finding, PathResolver paths)
    {
        // The changed file's path, which the author chose, or the model's own text when it matches none (the reader has redacted that once
        // already). Either way credentials come out of it before InlineCode cuts it to its limit.
        var path = MarkdownSanitizer.InlineCode(SecretRedactor.Redact(paths.Resolve(finding.File) ?? finding.File), ChecklistAnswerReader.MaxFileChars);

        // With no path there is nothing to point at, and an empty pair of backticks would pair with the first double backtick after it, which
        // would turn the code the issue quotes into live text.
        if (path.Length == 0)
        {
            return string.Empty;
        }

        return finding.Line > 0 ? $" `{path}:{finding.Line}`" : $" `{path}`";
    }

    private static string FormatCoverageLine(ReviewContext context, string coverageNote)
    {
        var note = Sanitize(coverageNote, ChecklistAnswerReader.MaxCoverageNoteChars);

        if (note.Length > 0)
        {
            return $"Coverage: {note}";
        }

        // The model said nothing about what it could not read, so the app says how much that was.
        var files = context.FileCount == 1 ? "file" : "files";
        var verb = context.LeftOutFileCount == 1 ? "was" : "were";

        return $"Coverage: {context.LeftOutFileCount} of {context.FileCount} changed {files} {verb} not reviewed in full.";
    }

    private static string FormatFooterStart(ReviewContext context)
    {
        var files = context.FileCount == 1 ? "1 file" : $"{context.FileCount} files";

        return $"{context.Owner}/{context.Repo} #{context.Number} · {Sanitize(context.HeadRef)} → {Sanitize(context.BaseRef)} · "
            + $"by {Sanitize(context.Author)} · {files} +{context.Additions} -{context.Deletions}";
    }

    private static string Icon(CheckStatus status)
    {
        return status switch
        {
            CheckStatus.Pass => PassIcon,
            CheckStatus.Concern => ConcernIcon,
            CheckStatus.Fail => FailIcon,
            CheckStatus.NotApplicable => NotApplicableIcon,
            _ => throw new UnreachableException($"The check status {status} has no icon in the comment.")
        };
    }

    private static string VerdictText(ReviewVerdict verdict)
    {
        // The headline's name for the verdict, which the serious count does not change.
        return new ReviewOutcome(verdict, SeriousCount: 0).VerdictText;
    }

    private static string ShortSha(string headSha)
    {
        return headSha.Length > ShortShaLength ? headSha[..ShortShaLength] : headSha;
    }

    private static string Sanitize(string text, int maxChars = int.MaxValue)
    {
        // The model's text comes from ChecklistAnswerReader, which removed credentials before it cut the text to the limit used here: a key that
        // a cut has shortened no longer has the shape the redactor knows, so redacting only here would miss one the reader's cut went through.
        // Redacting again is a second layer for that text, and the only one for the author's branch names and login and the model ids.
        return MarkdownSanitizer.NeutralizeInline(SecretRedactor.Redact(text), maxChars);
    }

    private static string SanitizeSummary(string summary)
    {
        // The summary has a line of its own, under the headline, where text can start a block. The block sanitizer closes a code fence the
        // text opens and escapes the marker of a list, a quote or a rule; inline text needs neither. It keeps a heading, which would
        // out-shout the verdict, and a fence, which would show none of the text, so a summary that starts with either gets a backslash
        // first. It then shows as one paragraph of text, whatever it starts with.
        var text = SecretRedactor.Redact(summary.Trim());
        var escape = _headingOrFence.IsMatch(text) ? @"\" : string.Empty;

        // The backslash is markup and not part of the summary, so the summary's limit does not count it.
        return MarkdownSanitizer.Neutralize(escape + text, ChecklistAnswerReader.MaxSummaryChars + escape.Length);
    }
}
