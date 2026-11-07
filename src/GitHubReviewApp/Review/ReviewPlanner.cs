namespace GitHubReviewApp.Review;

/// <summary>
/// Decides how a pull request is reviewed: which files are worth sending, in which order when the budget
/// runs out, and how they are grouped into model requests. Everything it leaves out is reported, so the
/// review can say exactly what was and was not read.
/// </summary>
public sealed class ReviewPlanner
{
    // Below this much remaining budget there is no point rendering another file.
    private const int MinimumFileBytes = 400;

    // A truncated slice smaller than this is too little context to review, so the file is skipped instead.
    private const int MinimumUsefulPartialBytes = 2_000;

    private readonly ReviewOptions _options;

    public ReviewPlanner(IOptions<ReviewOptions> options)
    {
        _options = options.Value;
    }

    public ReviewPlan CreatePlan(IReadOnlyList<PullRequestFile> files)
    {
        var skippedFiles = new List<SkippedFile>();
        var candidates = new List<(PullRequestFile File, int Order, FilePriority Priority)>();

        for (var order = 0; order < files.Count; order++)
        {
            var file = files[order];
            var skipReason = ClassifySkip(file);

            if (skipReason is { } reason)
            {
                skippedFiles.Add(new SkippedFile(file.Path, reason));
                continue;
            }

            candidates.Add((file, order, DiffFileClassifier.GetPriority(file.Path, file.Status, file.Patch)));
        }

        var selected = SelectWithinBudget(candidates, skippedFiles);
        var chunks = BuildChunks(selected);

        return new ReviewPlan(chunks, skippedFiles, files.Count);
    }

    private static FileSkipReason? ClassifySkip(PullRequestFile file)
    {
        if (file.IsRemoved)
        {
            return FileSkipReason.Deleted;
        }

        if (DiffFileClassifier.GetSkipReason(file.Path) is { } unreadable)
        {
            return unreadable;
        }

        if (string.IsNullOrWhiteSpace(file.Patch))
        {
            return FileSkipReason.NoDiffAvailable;
        }

        return null;
    }

    private List<(PlannedFile Planned, int Order)> SelectWithinBudget(
        List<(PullRequestFile File, int Order, FilePriority Priority)> candidates,
        List<SkippedFile> skippedFiles)
    {
        // Review the most valuable files first; ties keep the order GitHub returned (alphabetical by path).
        var ordered = candidates
            .OrderBy(candidate => candidate.Priority)
            .ThenBy(candidate => candidate.Order)
            .ToList();

        var perFileLimit = Math.Min(_options.MaxFileBytes, _options.MaxChunkBytes);
        var selected = new List<(PlannedFile Planned, int Order)>();
        var usedBytes = 0;

        foreach (var candidate in ordered)
        {
            var remaining = _options.MaxTotalBytes - usedBytes;

            if (selected.Count >= _options.MaxFiles || remaining < MinimumFileBytes)
            {
                skippedFiles.Add(new SkippedFile(candidate.File.Path, FileSkipReason.OverBudget));
                continue;
            }

            var planned = DiffRenderer.Render(candidate.File, Math.Min(perFileLimit, remaining));

            // Keep scanning after a skip: a later, smaller file may still fit in what is left.
            if (planned.IsPartial && planned.RenderedBytes < MinimumUsefulPartialBytes)
            {
                skippedFiles.Add(new SkippedFile(candidate.File.Path, FileSkipReason.OverBudget));
                continue;
            }

            selected.Add((planned, candidate.Order));
            usedBytes += planned.RenderedBytes;
        }

        return selected;
    }

    private List<ReviewChunk> BuildChunks(List<(PlannedFile Planned, int Order)> selected)
    {
        // Back to path order so files from the same directory are reviewed together.
        var inFileOrder = selected.OrderBy(item => item.Order).Select(item => item.Planned);

        var chunks = new List<ReviewChunk>();
        var current = new List<PlannedFile>();
        var currentBytes = 0;

        foreach (var planned in inFileOrder)
        {
            var isChunkFull = current.Count > 0 && currentBytes + planned.RenderedBytes > _options.MaxChunkBytes;

            if (isChunkFull)
            {
                chunks.Add(new ReviewChunk(chunks.Count, current));
                current = new List<PlannedFile>();
                currentBytes = 0;
            }

            current.Add(planned);
            currentBytes += planned.RenderedBytes;
        }

        if (current.Count > 0)
        {
            chunks.Add(new ReviewChunk(chunks.Count, current));
        }

        return chunks;
    }
}
