namespace GitHubReviewApp.Review;

/// <summary>
/// Builds the one user message of a checklist review (design 5.3): the pull request's metadata, its description in a tag of its own,
/// every changed file highest risk first with what happened to its diff, and the diffs that fit the budget, in the same order. Every
/// value the author controls passes through <see cref="PromptSanitizer.NeutralizeTags"/>, so none can close or open the tags around it.
/// </summary>
public sealed class ReviewInputBuilder
{
    // A cut slice smaller than this is too little of the file to review, so the file is left out instead.
    private const int MinimumUsefulPartialBytes = 2_000;

    private readonly ReviewOptions _options;

    public ReviewInputBuilder(IOptions<ReviewOptions> options)
    {
        _options = options.Value;
    }

    public ReviewInput Build(string owner, string repo, PullRequestInfo pullRequest, IReadOnlyList<PullRequestFile> files)
    {
        var (placedFiles, diffs) = PlaceFiles(files);
        var input = new ReviewInput(string.Empty, placedFiles);
        var message = new StringBuilder();

        AppendMetadata(message, owner, repo, pullRequest);
        AppendDescription(message, pullRequest.Body);
        AppendChangedFiles(message, input);
        AppendDiff(message, diffs);

        return input with { UserMessage = message.ToString() };
    }

    // Walks the files highest risk first, in GitHub's order within a tier, and sends each diff that fits what is left of the budget. A file
    // that does not fit is left out and the walk goes on, since a later, smaller file may still fit. Each diff costs its own bytes and one
    // more for the line break after it.
    private (List<ReviewInputFile> Files, List<PlannedFile> Diffs) PlaceFiles(IReadOnlyList<PullRequestFile> files)
    {
        var ordered = files
            .Select((file, index) => (File: file, Index: index, Tier: DiffFileClassifier.GetRiskTier(file)))
            .OrderBy(item => item.Tier)
            .ThenBy(item => item.Index);

        var placed = new List<ReviewInputFile>(files.Count);
        var diffs = new List<PlannedFile>();
        var usedBytes = 0;

        foreach (var (file, _, tier) in ordered)
        {
            DiffInclusion inclusion;

            if (tier == RiskTier.NotReadable)
            {
                inclusion = DiffFileClassifier.IsLockfile(file.Path) ? DiffInclusion.Lockfile : DiffInclusion.BinaryOrMinified;
            }
            else if (string.IsNullOrWhiteSpace(file.Patch))
            {
                inclusion = DiffInclusion.NoDiff;
            }
            else
            {
                var rendered = DiffRenderer.Render(file, _options.MaxFileBytes);
                var renderedBytes = rendered.RenderedBytes;
                var isOverBudget = renderedBytes + 1 > _options.MaxDiffBytes - usedBytes;
                var isTooSmallASlice = rendered.IsPartial && renderedBytes < MinimumUsefulPartialBytes;

                if (isOverBudget || isTooSmallASlice)
                {
                    inclusion = DiffInclusion.OverBudget;
                }
                else
                {
                    inclusion = rendered.IsPartial ? DiffInclusion.Partial : DiffInclusion.Included;
                    diffs.Add(rendered);
                    usedBytes += renderedBytes + 1;
                }
            }

            placed.Add(new ReviewInputFile(file, tier, inclusion));
        }

        return (placed, diffs);
    }

    private static void AppendMetadata(StringBuilder message, string owner, string repo, PullRequestInfo pullRequest)
    {
        message.Append("<pr_metadata>\n");
        message.Append("repository: ").Append(Inline(owner)).Append('/').Append(Inline(repo)).Append('\n');
        message.Append("title: ").Append(Inline(pullRequest.Title)).Append('\n');
        message.Append("author: ").Append(Inline(pullRequest.AuthorLogin)).Append('\n');
        message.Append("base_branch: ").Append(Inline(pullRequest.BaseRef)).Append('\n');
        message.Append("head_branch: ").Append(Inline(pullRequest.HeadRef)).Append('\n');
        message.Append("additions: ").Append(Number(pullRequest.Additions)).Append('\n');
        message.Append("deletions: ").Append(Number(pullRequest.Deletions)).Append('\n');
        message.Append("head_sha: ").Append(Inline(pullRequest.HeadSha)).Append('\n');
        message.Append("</pr_metadata>\n\n");
    }

    private void AppendDescription(StringBuilder message, string description)
    {
        message.Append("<pr_description_untrusted>\n");
        message.Append(string.IsNullOrWhiteSpace(description) ? "(no description)" : PromptSanitizer.NeutralizeTags(CutDescription(description)));
        message.Append("\n</pr_description_untrusted>\n\n");
    }

    // The description keeps its own line breaks, as \n. It is cut before its tags are neutralized, so that the cut cannot leave the start of
    // a tag behind that the check would then miss.
    private string CutDescription(string description)
    {
        var text = description.ReplaceLineEndings("\n");

        if (text.Length <= _options.MaxDescriptionChars)
        {
            return text;
        }

        var keptLength = _options.MaxDescriptionChars;

        // Never keep the first half of a surrogate pair whose second half is cut off.
        if (char.IsHighSurrogate(text[keptLength - 1]))
        {
            keptLength--;
        }

        return text[..keptLength] + "\n[description cut]";
    }

    private void AppendChangedFiles(StringBuilder message, ReviewInput input)
    {
        var fileCount = input.Files.Count;
        var fullCount = input.IncludedFileCount - input.PartialFileCount;
        var leftOutCount = fileCount - input.IncludedFileCount;

        message.Append("<changed_files>\n");
        message.Append(Number(fileCount)).Append(fileCount == 1 ? " file changed" : " files changed").Append(", highest risk first. ");
        message.Append(Number(fullCount)).Append(" diffs included in full, ");
        message.Append(Number(input.PartialFileCount)).Append(" partly, ");
        message.Append(Number(leftOutCount)).Append(" left out.\n");

        foreach (var file in input.Files.Take(_options.MaxListedFiles))
        {
            AppendChangedFileLine(message, file, InclusionText(file.Inclusion));
        }

        if (fileCount > _options.MaxListedFiles)
        {
            message.Append("and ").Append(Number(fileCount - _options.MaxListedFiles)).Append(" more files not listed\n");
        }

        message.Append("</changed_files>\n\n");
    }

    private static void AppendChangedFileLine(StringBuilder message, ReviewInputFile file, string inclusion)
    {
        message.Append("- ").Append(Inline(file.File.Path));
        message.Append(" (").Append(Inline(file.File.Status));
        message.Append(", +").Append(Number(file.File.Additions));
        message.Append(" -").Append(Number(file.File.Deletions));
        message.Append(", ").Append(TierLabel(file.Tier));
        message.Append("): ").Append(inclusion).Append('\n');
    }

    private string InclusionText(DiffInclusion inclusion)
    {
        return inclusion switch
        {
            DiffInclusion.Included => "included",
            DiffInclusion.Partial => $"partial, cut at {Number(_options.MaxFileBytes / 1_000)} KB",
            DiffInclusion.OverBudget => $"left out: over the {Number(_options.MaxDiffBytes / 1_000)} KB limit",
            DiffInclusion.Lockfile => "left out: lockfile, not read",
            DiffInclusion.BinaryOrMinified => "left out: binary or minified, not read",
            _ => "left out: no diff from GitHub" // DiffInclusion.NoDiff, the last value
        };
    }

    private static string TierLabel(RiskTier tier)
    {
        return tier switch
        {
            RiskTier.Auth => "auth",
            RiskTier.Payments => "payments",
            RiskTier.Cards => "cards",
            RiskTier.BankLogin => "bank login",
            RiskTier.PersonalData => "personal data",
            RiskTier.Pipelines => "pipeline",
            RiskTier.Config => "config",
            RiskTier.Source => "source",
            RiskTier.Tests => "test",
            RiskTier.Docs => "docs",
            RiskTier.GeneratedOrVendored => "generated or vendored",
            _ => "not readable as text" // RiskTier.NotReadable, the last tier
        };
    }

    private static void AppendDiff(StringBuilder message, List<PlannedFile> diffs)
    {
        message.Append("<diff>\n");

        foreach (var diff in diffs)
        {
            message.Append(diff.RenderedDiff).Append('\n');
        }

        message.Append("</diff>");
    }

    // Every author-written value but the description is kept on one line, so it cannot add a line of its own to the block it is in.
    private static string Inline(string value)
    {
        return PromptSanitizer.NeutralizeTags(value.ReplaceLineEndings(" "));
    }

    private static string Number(int value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }
}
