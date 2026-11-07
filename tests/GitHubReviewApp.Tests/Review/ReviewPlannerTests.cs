namespace GitHubReviewApp.Tests.Review;

public class ReviewPlannerTests
{
    private static PullRequestFile Modified(string path, int lineCount = 200)
    {
        var lines = Enumerable.Range(1, lineCount).Select(_ => "+" + new string('x', 20));
        var patch = $"@@ -0,0 +1,{lineCount} @@\n" + string.Join("\n", lines);
        return new PullRequestFile(path, "modified", lineCount, 0, patch);
    }

    // Every line holds 21 characters that take 3 bytes each in UTF-8, so what the model reads is about 2.6 times as many bytes as characters.
    private static PullRequestFile MultiByte(string path, int lineCount = 200)
    {
        var lines = Enumerable.Range(1, lineCount).Select(_ => "+" + string.Concat(Enumerable.Repeat("日本語", 7)));
        var patch = $"@@ -0,0 +1,{lineCount} @@\n" + string.Join("\n", lines);
        return new PullRequestFile(path, "modified", lineCount, 0, patch);
    }

    private static int RenderedSize(PullRequestFile file)
    {
        return DiffRenderer.Render(file, int.MaxValue).RenderedDiff.Length;
    }

    // Measured here with the framework's own encoder, not with the property under test.
    private static int Utf8Size(PullRequestFile file)
    {
        return Encoding.UTF8.GetByteCount(DiffRenderer.Render(file, int.MaxValue).RenderedDiff);
    }

    private static int Utf8Size(ReviewChunk chunk)
    {
        return chunk.Files.Sum(planned => Encoding.UTF8.GetByteCount(planned.RenderedDiff));
    }

    private static ReviewPlanner BuildSut(ReviewOptions? options = null)
    {
        return new ReviewPlanner(Options.Create(options ?? new ReviewOptions()));
    }

    private static IEnumerable<string> ReviewedPaths(ReviewPlan plan)
    {
        return plan.Chunks.SelectMany(chunk => chunk.Files).Select(planned => planned.File.Path);
    }

    // ── Classification ───────────────────────────────────────────────────────

    [Fact]
    public void CreatePlan_GivenSmallPullRequest_Should_ReturnOneChunkContainingEveryFile()
    {
        // Arrange
        var files = new[] { Modified("a.cs", 5), Modified("b.cs", 5) };

        // Act
        var plan = BuildSut().CreatePlan(files);

        // Assert
        plan.Chunks.Should().ContainSingle();
        ReviewedPaths(plan).Should().Equal("a.cs", "b.cs");
        plan.SkippedFiles.Should().BeEmpty();
        plan.TotalFileCount.Should().Be(2);
        plan.ReviewedFileCount.Should().Be(2);
        plan.HasReviewableContent.Should().BeTrue();
    }

    [Fact]
    public void CreatePlan_GivenALockfileAndABinary_Should_SkipEachWithItsOwnReason()
    {
        // Arrange
        var files = new[]
        {
            Modified("src/Real.cs", 5),
            Modified("package-lock.json", 5),
            Modified("assets/logo.png", 5),
            Modified("wwwroot/js/app.min.js", 5)
        };

        // Act
        var plan = BuildSut().CreatePlan(files);

        // Assert
        ReviewedPaths(plan).Should().Equal("src/Real.cs");
        plan.SkippedFiles.Should().BeEquivalentTo(new[]
        {
            new SkippedFile("package-lock.json", FileSkipReason.DependencyLockfile),
            new SkippedFile("assets/logo.png", FileSkipReason.BinaryOrMinified),
            new SkippedFile("wwwroot/js/app.min.js", FileSkipReason.BinaryOrMinified)
        });
    }

    [Theory]
    [InlineData("Data/Migrations/001_Init.Designer.cs")]
    [InlineData("src/Payments/vendor/Refund.cs")]
    [InlineData("web/dist/index.js")]
    [InlineData("src/Auth/Auth.g.cs")]
    public void CreatePlan_GivenAGeneratedOrVendoredFileAndRoomInTheBudget_Should_StillReviewIt(string path)
    {
        // Arrange — such files still compile or run, and their author chose the name, so a name alone must not exempt them
        var files = new[] { Modified("src/Real.cs", 5), Modified(path, 5) };

        // Act
        var plan = BuildSut().CreatePlan(files);

        // Assert
        ReviewedPaths(plan).Should().Equal("src/Real.cs", path);
        plan.SkippedFiles.Should().BeEmpty();
    }

    [Fact]
    public void CreatePlan_GivenAGeneratedFileAndABudgetForOnlyOneFile_Should_SpendItOnTheHandWrittenFile()
    {
        // Arrange — the generated file is listed first, so only its priority can put the other file ahead of it
        var generated = Modified("Data/Migrations/001_Init.Designer.cs");
        var handWritten = Modified("src/Real.cs");
        var options = new ReviewOptions { MaxTotalBytes = RenderedSize(handWritten) + 100 };

        // Act
        var plan = BuildSut(options).CreatePlan([generated, handWritten]);

        // Assert
        ReviewedPaths(plan).Should().Equal("src/Real.cs");
        plan.SkippedFiles.Should().ContainSingle().Which.Should().Be(new SkippedFile("Data/Migrations/001_Init.Designer.cs", FileSkipReason.OverBudget));
    }

    [Fact]
    public void CreatePlan_GivenANewFileWithACanonicalGeneratedHeaderAndABudgetForOnlyOneFile_Should_SpendItOnTheOtherFile()
    {
        // Arrange
        var header = "@@ -0,0 +1,3 @@\n+// <auto-generated>\n+" + new string('x', 400) + "\n+" + new string('y', 400);
        var generated = new PullRequestFile("src/Client.cs", "added", 3, 0, header);
        var handWritten = Modified("src/Real.cs");
        var options = new ReviewOptions { MaxTotalBytes = RenderedSize(handWritten) + 100 };

        // Act
        var plan = BuildSut(options).CreatePlan([generated, handWritten]);

        // Assert
        ReviewedPaths(plan).Should().Equal("src/Real.cs");
    }

    [Fact]
    public void CreatePlan_GivenADoNotEditCommentInAModifiedFile_Should_NotLetItOptOutOfReviewOrDemoteIt()
    {
        // Arrange — the opt-out an author could use to hide a change from the reviewer
        var patch = "@@ -1,3 +1,4 @@\n+// NOTE: do not edit the expiry check without sign-off\n if (a)\n {\n }";
        var guarded = new PullRequestFile("src/Auth/JwtValidator.cs", "modified", 1, 0, patch);
        var other = Modified("src/Other.cs", 5);
        var options = new ReviewOptions { MaxTotalBytes = RenderedSize(other) + 100 + 200 };

        // Act
        var plan = BuildSut(options).CreatePlan([guarded, other]);

        // Assert
        ReviewedPaths(plan).Should().Contain("src/Auth/JwtValidator.cs");
    }

    [Fact]
    public void CreatePlan_GivenRemovedFile_Should_SkipItAsDeleted()
    {
        // Arrange
        var removed = Modified("src/Old.cs", 5) with { Status = "removed" };

        // Act
        var plan = BuildSut().CreatePlan([removed, Modified("src/New.cs", 5)]);

        // Assert
        plan.SkippedFiles.Should().ContainSingle().Which.Should().Be(new SkippedFile("src/Old.cs", FileSkipReason.Deleted));
        ReviewedPaths(plan).Should().Equal("src/New.cs");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n ")]
    public void CreatePlan_GivenFileWithoutAPatch_Should_SkipItAsNoDiffAvailable(string? patch)
    {
        // Arrange
        var file = new PullRequestFile("src/Huge.cs", "modified", 9000, 0, patch);

        // Act
        var plan = BuildSut().CreatePlan([file]);

        // Assert
        plan.SkippedFiles.Should().ContainSingle().Which.Should().Be(new SkippedFile("src/Huge.cs", FileSkipReason.NoDiffAvailable));
        plan.HasReviewableContent.Should().BeFalse();
    }

    [Fact]
    public void CreatePlan_GivenEveryFileIsSkippable_Should_HaveNoReviewableContent()
    {
        // Arrange
        var files = new[] { Modified("yarn.lock", 5), Modified("logo.png", 5) };

        // Act
        var plan = BuildSut().CreatePlan(files);

        // Assert
        plan.Chunks.Should().BeEmpty();
        plan.HasReviewableContent.Should().BeFalse();
        plan.TotalFileCount.Should().Be(2);
    }

    // ── Budgeting ────────────────────────────────────────────────────────────

    [Fact]
    public void CreatePlan_GivenBudgetForTwoFiles_Should_ReviewSourceAndConfigurationBeforeTestsAndDocs()
    {
        // Arrange — input order is deliberately the reverse of priority
        var docs = Modified("docs/readme.md");
        var tests = Modified("tests/FooTests.cs");
        var config = Modified("appsettings.json");
        var source = Modified("src/Foo.cs");
        var options = new ReviewOptions { MaxTotalBytes = RenderedSize(source) + RenderedSize(config) + 100 };

        // Act
        var plan = BuildSut(options).CreatePlan([docs, tests, config, source]);

        // Assert
        ReviewedPaths(plan).Should().BeEquivalentTo("src/Foo.cs", "appsettings.json");
        plan.SkippedFiles.Should().BeEquivalentTo(new[]
        {
            new SkippedFile("tests/FooTests.cs", FileSkipReason.OverBudget),
            new SkippedFile("docs/readme.md", FileSkipReason.OverBudget)
        });
    }

    [Fact]
    public void CreatePlan_GivenFilesSelectedByPriority_Should_KeepOriginalOrderInsideChunks()
    {
        // Arrange
        var files = new[] { Modified("src/b.cs", 5), Modified("docs/a.md", 5), Modified("src/a.cs", 5) };

        // Act
        var plan = BuildSut().CreatePlan(files);

        // Assert
        ReviewedPaths(plan).Should().Equal("src/b.cs", "docs/a.md", "src/a.cs");
    }

    [Fact]
    public void CreatePlan_GivenFileLargerThanMaxFileBytes_Should_IncludeItPartiallyAndStillReviewTheNextFile()
    {
        // Arrange
        var big = Modified("src/Big.cs", 600);
        var small = Modified("src/Small.cs", 5);
        var options = new ReviewOptions { MaxFileBytes = 4_000 };

        // Act
        var plan = BuildSut(options).CreatePlan([big, small]);

        // Assert
        var planned = plan.Chunks.SelectMany(c => c.Files).ToList();
        planned.Should().HaveCount(2);
        planned[0].File.Path.Should().Be("src/Big.cs");
        planned[0].IsPartial.Should().BeTrue();
        planned[0].RenderedDiff.Length.Should().BeLessThanOrEqualTo(4_000);
        planned[1].IsPartial.Should().BeFalse();
        plan.PartialFileCount.Should().Be(1);
    }

    [Fact]
    public void CreatePlan_GivenLeftoverBudgetTooSmallForAUsefulPartial_Should_SkipThatFileButStillFitALaterSmallOne()
    {
        // Arrange — after the first file only ~1,500 characters remain: too little for a useful slice of
        // the second (large) file, but plenty for the third (small) one.
        var first = Modified("src/A.cs", 200);
        var second = Modified("src/B.cs", 400);
        var third = Modified("src/C.cs", 10);
        var options = new ReviewOptions { MaxTotalBytes = RenderedSize(first) + 1_500 };

        // Act
        var plan = BuildSut(options).CreatePlan([first, second, third]);

        // Assert
        ReviewedPaths(plan).Should().Equal("src/A.cs", "src/C.cs");
        plan.SkippedFiles.Should().ContainSingle().Which.Should().Be(new SkippedFile("src/B.cs", FileSkipReason.OverBudget));
    }

    [Fact]
    public void CreatePlan_GivenMoreFilesThanMaxFiles_Should_SkipTheExtraFilesAsOverBudget()
    {
        // Arrange
        var files = Enumerable.Range(1, 5).Select(i => Modified($"src/File{i}.cs", 5)).ToList();
        var options = new ReviewOptions { MaxFiles = 3 };

        // Act
        var plan = BuildSut(options).CreatePlan(files);

        // Assert
        plan.ReviewedFileCount.Should().Be(3);
        plan.SkippedFiles.Should().HaveCount(2).And.OnlyContain(s => s.Reason == FileSkipReason.OverBudget);
    }

    [Theory]
    [InlineData(400, true)]
    [InlineData(399, false)]
    public void CreatePlan_GivenALaterFileAndExactlyThatMuchBudgetLeft_Should_ReviewItOnlyWhenAtLeastTheMinimumRemains(int leftOver, bool isReviewed)
    {
        // Arrange — below 400 characters of budget there is no point rendering another file, even a tiny one
        var first = Modified("src/A.cs", 50);
        var tiny = Modified("src/B.cs", 2);
        var options = new ReviewOptions { MaxTotalBytes = RenderedSize(first) + leftOver };

        // Act
        var plan = BuildSut(options).CreatePlan([first, tiny]);

        // Assert
        ReviewedPaths(plan).Contains("src/B.cs").Should().Be(isReviewed);
    }

    [Fact]
    public void CreatePlan_GivenExactlyAsManyFilesAsMaxFiles_Should_ReviewEveryOne()
    {
        // Arrange
        var files = Enumerable.Range(1, 3).Select(i => Modified($"src/File{i}.cs", 5)).ToList();

        // Act
        var plan = BuildSut(new ReviewOptions { MaxFiles = 3 }).CreatePlan(files);

        // Assert
        plan.ReviewedFileCount.Should().Be(3);
        plan.SkippedFiles.Should().BeEmpty();
    }

    [Fact]
    public void CreatePlan_GivenMaxTotalBytesExactlyEqualToTheSizeOfEveryFile_Should_ReviewEveryFileWhole()
    {
        // Arrange
        var files = new[] { Modified("src/A.cs", 40), Modified("src/B.cs", 40) };
        var options = new ReviewOptions { MaxTotalBytes = RenderedSize(files[0]) + RenderedSize(files[1]) };

        // Act
        var plan = BuildSut(options).CreatePlan(files);

        // Assert
        plan.ReviewedFileCount.Should().Be(2);
        plan.PartialFileCount.Should().Be(0);
    }

    [Fact]
    public void CreatePlan_GivenAChunkLimitExactlyEqualToTwoFiles_Should_PutTwoFilesInTheChunk()
    {
        // Arrange
        var files = new[] { "a", "b", "c" }.Select(name => Modified($"src/{name}.cs", 40)).ToList();
        var options = new ReviewOptions { MaxChunkBytes = RenderedSize(files[0]) * 2 };

        // Act
        var plan = BuildSut(options).CreatePlan(files);

        // Assert
        plan.Chunks.Select(chunk => chunk.Files.Count).Should().Equal(2, 1);
    }

    [Fact]
    public void CreatePlan_GivenMaxFileBytesLargerThanMaxChunkBytes_Should_NeverBuildAChunkBiggerThanTheChunkLimit()
    {
        // Arrange
        var options = new ReviewOptions { MaxFileBytes = 500_000, MaxChunkBytes = 5_000, MaxTotalBytes = 100_000 };

        // Act
        var plan = BuildSut(options).CreatePlan([Modified("src/Big.cs", 1_000), Modified("src/Other.cs", 100)]);

        // Assert
        plan.Chunks.Should().NotBeEmpty("a check made of every chunk passes when there are none");
        plan.Chunks.Should().OnlyContain(chunk => chunk.TotalBytes <= 5_000);
    }

    // ── Chunking ─────────────────────────────────────────────────────────────

    [Fact]
    public void CreatePlan_GivenContentLargerThanTheChunkLimit_Should_SplitIntoMultipleChunksInFileOrder()
    {
        // Arrange
        var files = new[] { "a", "b", "c", "d", "e" }.Select(name => Modified($"src/{name}.cs", 100)).ToList();
        var oneFile = RenderedSize(files[0]);
        var options = new ReviewOptions { MaxChunkBytes = (oneFile * 2) + 50 };

        // Act
        var plan = BuildSut(options).CreatePlan(files);

        // Assert
        plan.Chunks.Select(c => c.Files.Select(f => f.File.Path).ToArray()).Should().BeEquivalentTo(
            new[]
            {
                new[] { "src/a.cs", "src/b.cs" },
                new[] { "src/c.cs", "src/d.cs" },
                new[] { "src/e.cs" }
            },
            options => options.WithStrictOrdering());
        plan.Chunks.Select(c => c.Index).Should().Equal(0, 1, 2);
        plan.Chunks.Should().OnlyContain(c => c.TotalBytes <= options.MaxChunkBytes);
    }

    // ── Budgets are UTF-8 bytes ──────────────────────────────────────────────

    [Fact]
    public void CreatePlan_GivenFilesOfMultiByteText_Should_ChunkByUtf8BytesSoNoRequestExceedsItsByteLimit()
    {
        // Arrange — both files fit one chunk if their characters are counted, but not if their bytes are
        var files = new[] { MultiByte("src/A.cs"), MultiByte("src/B.cs") };
        var options = new ReviewOptions { MaxChunkBytes = Utf8Size(files[0]) + (Utf8Size(files[0]) / 2) };

        // Act
        var plan = BuildSut(options).CreatePlan(files);

        // Assert
        plan.Chunks.Should().HaveCount(2);
        plan.Chunks.Select(Utf8Size).Should().OnlyContain(bytes => bytes <= options.MaxChunkBytes);
    }

    [Fact]
    public void CreatePlan_GivenFilesOfMultiByteText_Should_NotPlanMoreThanTheTotalByteBudget()
    {
        // Arrange — three files would pass a budget of characters; two of them already use up this many bytes
        var files = new[] { MultiByte("src/A.cs"), MultiByte("src/B.cs"), MultiByte("src/C.cs") };
        var options = new ReviewOptions { MaxTotalBytes = Utf8Size(files[0]) * 2 };

        // Act
        var plan = BuildSut(options).CreatePlan(files);

        // Assert
        plan.Chunks.Sum(Utf8Size).Should().BeLessThanOrEqualTo(options.MaxTotalBytes);
        plan.SkippedFiles.Should().NotBeEmpty("the third file did not fit");
    }

    [Fact]
    public void CreatePlan_GivenOneFileOfMultiByteTextLargerThanTheFileByteLimit_Should_CutItWithinTheLimitAndSayItIsPartial()
    {
        // Arrange — about 6,000 characters, so under a character limit of 8,000, but about 14,000 bytes
        var options = new ReviewOptions { MaxFileBytes = 8_000 };

        // Act
        var plan = BuildSut(options).CreatePlan([MultiByte("src/Big.cs")]);

        // Assert
        var planned = plan.Chunks.Should().ContainSingle().Which.Files.Should().ContainSingle().Subject;
        planned.IsPartial.Should().BeTrue();
        Encoding.UTF8.GetByteCount(planned.RenderedDiff).Should().BeLessThanOrEqualTo(options.MaxFileBytes);
    }

    // ── Scale ────────────────────────────────────────────────────────────────

    [Fact]
    public void CreatePlan_GivenAThousandLargeFiles_Should_StayWithinEveryBudgetAndAccountForEveryFile()
    {
        // Arrange — roughly 30 KB of patch per file
        var files = Enumerable.Range(1, 1_000).Select(i => Modified($"src/Module{i:D4}/Service.cs", 1_000)).ToList();
        var options = new ReviewOptions();

        // Act
        var plan = BuildSut(options).CreatePlan(files);

        // Assert
        plan.TotalFileCount.Should().Be(1_000);
        plan.ReviewedFileCount.Should().BeGreaterThan(0, "a plan that skipped everything would satisfy every limit below");
        plan.Chunks.Should().HaveCountGreaterThan(1, "a pull request this large cannot fit in one request");
        plan.ReviewedFileCount.Should().BeLessThanOrEqualTo(options.MaxFiles);
        plan.Chunks.Sum(c => c.TotalBytes).Should().BeLessThanOrEqualTo(options.MaxTotalBytes);
        plan.Chunks.Should().OnlyContain(c => c.TotalBytes <= options.MaxChunkBytes);
        (plan.ReviewedFileCount + plan.SkippedFiles.Count).Should().Be(1_000, "every file is either reviewed or reported as skipped");
    }
}
