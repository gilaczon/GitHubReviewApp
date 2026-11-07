namespace GitHubReviewApp.Review;

/// <summary>
/// Merges the results of every Claude request for one pull request: checks each finding's location against
/// the real diff, removes duplicates, ranks by severity, caps the total, and records which requests did not
/// complete so the review can say exactly what was not read.
/// </summary>
public sealed class ReviewAggregator
{
    private readonly ReviewOptions _options;

    public ReviewAggregator(IOptions<ReviewOptions> options)
    {
        _options = options.Value;
    }

    public ReviewReport Aggregate(ReviewPlan plan, IReadOnlyList<ClaudeReviewResult> results)
    {
        if (results.Count != plan.Chunks.Count)
        {
            throw new ArgumentException(
                $"Expected one result per chunk, but the plan has {plan.Chunks.Count} chunks and {results.Count} results were given.",
                nameof(results));
        }

        var resolver = new PathResolver(plan.Chunks.SelectMany(chunk => chunk.Files).Select(planned => planned.File));
        var lineIndexes = new Dictionary<string, DiffLineIndex>(StringComparer.Ordinal);

        var summaries = new List<string>();
        var findings = new List<ReviewFinding>();
        var incompleteChunks = new List<IncompleteChunk>();
        var usage = ClaudeUsage.Empty;

        // Findings the model says it left out of its own reply, because it was told to list only the most severe few.
        var modelOmittedCount = 0;

        // Entries in the findings lists that were not readable findings, so the review can say that something the model wrote is not shown.
        var droppedCount = 0;

        for (var index = 0; index < results.Count; index++)
        {
            var chunk = plan.Chunks[index];
            var result = results[index];
            usage = usage.Plus(result.Usage);

            if (result.Outcome != ChunkOutcome.Completed)
            {
                var filePaths = chunk.Files.Select(planned => planned.File.Path).ToList();
                incompleteChunks.Add(new IncompleteChunk(chunk.Index, filePaths, result.Outcome, result.Detail));
                continue;
            }

            if (!string.IsNullOrWhiteSpace(result.Summary))
            {
                summaries.Add(result.Summary.Trim());
            }

            modelOmittedCount += Math.Clamp(result.OmittedFindingCount, 0, ReviewResponseParser.MaxOmittedFindings);
            droppedCount += Math.Clamp(result.DroppedFindingCount, 0, ReviewResponseParser.MaxOmittedFindings);

            foreach (var finding in result.Findings)
            {
                findings.Add(Anchor(finding, resolver, lineIndexes));
            }
        }

        var ranked = RankAndDeduplicate(findings);
        var omittedCount = modelOmittedCount;

        if (ranked.Count > _options.MaxFindings)
        {
            omittedCount += ranked.Count - _options.MaxFindings;
            ranked = ranked.Take(_options.MaxFindings).ToList();
        }

        return new ReviewReport(summaries, ranked, omittedCount, incompleteChunks, usage, DroppedFindingCount: droppedCount);
    }

    private static ReviewFinding Anchor(
        ReviewFinding finding,
        PathResolver resolver,
        Dictionary<string, DiffLineIndex> lineIndexes)
    {
        var resolvedPath = resolver.Resolve(finding.Path);

        if (resolvedPath is null)
        {
            return finding with { Path = finding.Path.Trim().Replace('\\', '/'), IsAnchored = false };
        }

        if (!lineIndexes.TryGetValue(resolvedPath, out var lineIndex))
        {
            lineIndex = DiffLineIndex.FromPatch(resolver.GetPatch(resolvedPath));
            lineIndexes[resolvedPath] = lineIndex;
        }

        var isAnchored = finding.Line > 0 && lineIndex.Contains(finding.Line);

        return finding with { Path = resolvedPath, IsAnchored = isAnchored };
    }

    private static List<ReviewFinding> RankAndDeduplicate(List<ReviewFinding> findings)
    {
        var ranked = findings
            .OrderByDescending(finding => finding.Severity)
            .ThenBy(finding => finding.Path, StringComparer.Ordinal)
            .ThenBy(finding => finding.Line)
            .ThenBy(finding => finding.Title, StringComparer.Ordinal);

        var seen = new HashSet<(string Path, int Line, string Title)>();
        var unique = new List<ReviewFinding>();

        // Ranked first, so when two requests report the same problem the more severe copy is kept.
        foreach (var finding in ranked)
        {
            if (seen.Add((finding.Path, finding.Line, finding.Title.Trim().ToUpperInvariant())))
            {
                unique.Add(finding);
            }
        }

        return unique;
    }
}
