namespace GitHubReviewApp.Tests;

/// <summary>
/// Turns the rules in CLAUDE.md that are most often broken into checks that fail the build, so they are enforced by the test run instead of by a
/// reviewer remembering them. The rules live in <see cref="ConventionRules"/>, which parses the source; ConventionRulesTests shows that each
/// one can fail.
/// </summary>
public class SourceConventionTests
{
    private const string TestNamespaceRoot = "GitHubReviewApp.Tests";

    private static readonly string _repositoryRoot = RepositoryRoot.FullPath;
    private static readonly string _productionFolder = Path.Combine("src", "GitHubReviewApp");
    private static readonly string _testFolder = Path.Combine("tests", "GitHubReviewApp.Tests");

    private sealed record SourceFile(string Path, string RelativePath, string Text);

    private static List<SourceFile> Sources(string project, bool isGlobalUsingsIncluded = false)
    {
        var folder = Path.Combine(_repositoryRoot, project);

        var files = Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(Path.GetRelativePath(folder, path)))
            .Where(path => isGlobalUsingsIncluded || Path.GetFileName(path) != "GlobalUsings.cs")
            .Select(path => new SourceFile(path, Path.GetRelativePath(_repositoryRoot, path), File.ReadAllText(path)))
            .ToList();

        // A rule that is run over no files passes, whatever the sources hold, so finding none is itself a failure.
        files.Should().NotBeEmpty($"{project} must have source files for the rules to judge");

        return files;
    }

    // Judged by the path inside the project and not the whole path, so a repository that happens to live under a folder called bin is still checked.
    private static bool IsBuildOutput(string pathInsideTheProject)
    {
        return pathInsideTheProject.Split(Path.DirectorySeparatorChar).SkipLast(1).Any(segment => segment is "bin" or "obj");
    }

    private static List<SourceFile> ProductionSources()
    {
        return Sources(_productionFolder);
    }

    private static List<SourceFile> TestSources()
    {
        return Sources(_testFolder);
    }

    private static List<string> Offenders(IEnumerable<SourceFile> files, Func<string, IReadOnlyList<string>> rule)
    {
        return files
            .SelectMany(file => rule(file.Text).Select(finding => $"{file.RelativePath}: {finding}"))
            .ToList();
    }

    public static IEnumerable<object[]> ProjectFolders()
    {
        yield return [Path.Combine("src", "GitHubReviewApp")];
        yield return [Path.Combine("tests", "GitHubReviewApp.Tests")];
    }

    // ── Layout ───────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(ProjectFolders))]
    public void SourceFiles_GivenAnyProject_Should_HaveNoUsingDirectivesOutsideGlobalUsings(string project)
    {
        // Arrange
        var files = Sources(project);

        // Act
        var offenders = Offenders(files, ConventionRules.UsingDirectives);

        // Assert — every using belongs in GlobalUsings.cs (CLAUDE.md, "Common mistakes" rule 1)
        offenders.Should().BeEquivalentTo(Array.Empty<string>(), "all using directives go in GlobalUsings.cs");
    }

    [Theory]
    [MemberData(nameof(ProjectFolders))]
    public void GlobalUsings_GivenAnyProject_Should_HoldNothingButGlobalUsingDirectives(string project)
    {
        // Arrange
        var files = Sources(project, isGlobalUsingsIncluded: true).Where(file => Path.GetFileName(file.Path) == "GlobalUsings.cs").ToList();

        // Act
        var offenders = Offenders(files, ConventionRules.AnythingButGlobalUsings);

        // Assert
        files.Should().ContainSingle("each project has exactly one GlobalUsings.cs");
        offenders.Should().BeEquivalentTo(Array.Empty<string>());
    }

    [Theory]
    [MemberData(nameof(ProjectFolders))]
    public void SourceFiles_GivenAnyProject_Should_UseFileScopedNamespaces(string project)
    {
        // Arrange
        var files = Sources(project);

        // Act
        var offenders = Offenders(files, ConventionRules.BlockScopedNamespaces);

        // Assert
        offenders.Should().BeEquivalentTo(Array.Empty<string>(), "namespaces are file-scoped: 'namespace X;'");
    }

    [Theory]
    [MemberData(nameof(ProjectFolders))]
    public void SourceFiles_GivenAnyProject_Should_DeclareExactlyOneTopLevelTypeNamedAfterTheFile(string project)
    {
        // Arrange
        var files = Sources(project).Where(file => Path.GetFileName(file.Path) != "Program.cs").ToList();

        // Act
        var offenders = files
            .Select(file => (File: file, Types: ConventionRules.TopLevelTypeNames(file.Text)))
            .Where(item => item.Types.Count != 1 || item.Types[0] != Path.GetFileNameWithoutExtension(item.File.Path))
            .Select(item => $"{item.File.RelativePath}: declares [{string.Join(", ", item.Types)}]")
            .ToList();

        // Assert
        offenders.Should().BeEquivalentTo(Array.Empty<string>(), "one top-level type per file, named after the file");
    }

    // ── Names ────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(ProjectFolders))]
    public void SourceFiles_GivenAnyProject_Should_HaveNoNonConstPrivateFieldWithoutALeadingUnderscore(string project)
    {
        // Arrange
        var files = Sources(project);

        // Act
        var offenders = Offenders(files, ConventionRules.PrivateFieldsWithoutLeadingUnderscore);

        // Assert — "private static readonly" included; only const fields are PascalCase
        offenders.Should().BeEquivalentTo(Array.Empty<string>(), "private fields are _camelCase");
    }

    [Theory]
    [MemberData(nameof(ProjectFolders))]
    public void SourceFiles_GivenAnyProject_Should_NameBooleansWithAnIsHasOrCanPrefix(string project)
    {
        // Arrange
        var files = Sources(project);

        // Act
        var offenders = Offenders(files, ConventionRules.BooleansWithoutAQuestionPrefix);

        // Assert
        offenders.Should().BeEquivalentTo(Array.Empty<string>(), "booleans start with Is, Has or Can");
    }

    [Theory]
    [MemberData(nameof(ProjectFolders))]
    public void SourceFiles_GivenAnyProject_Should_OrderMembersAsFieldsConstructorsNestedTypesPropertiesThenMethods(string project)
    {
        // Arrange
        var files = Sources(project);

        // Act
        var offenders = Offenders(files, ConventionRules.MembersOutOfOrder);

        // Assert
        offenders.Should().BeEquivalentTo(Array.Empty<string>(), "CLAUDE.md: fields and events, constructors, nested types, properties, methods");
    }

    // ── Behaviour ────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(ProjectFolders))]
    public void SourceFiles_GivenAnyProject_Should_NotBlockOnAsyncWork(string project)
    {
        // Arrange
        var files = Sources(project);

        // Act
        var offenders = Offenders(files, ConventionRules.BlockingCalls);

        // Assert
        offenders.Should().BeEquivalentTo(Array.Empty<string>(), "always await; never .Result, .Wait() or Thread.Sleep");
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public void TestMethods_GivenTheTestAssembly_Should_BeNamedWithAGivenContextAndAShouldOutcome()
    {
        // Arrange
        var methods = typeof(SourceConventionTests).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(method => method.GetCustomAttribute<FactAttribute>() is not null)
            .ToList();

        // Act
        var offenders = methods
            .Where(method => ConventionRules.TestMethodNameProblem(method.Name) is not null)
            .Select(method => $"{method.DeclaringType!.Name}.{method.Name}")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        // Assert
        methods.Should().NotBeEmpty("this test is meaningless if it found no tests");
        offenders.Should().BeEquivalentTo(Array.Empty<string>(), "test names are MethodName_GivenContext_Should_ExpectedOutcome");
    }

    [Fact]
    public void TestMethods_GivenTheTestSources_Should_MarkTheArrangeActAndAssertSectionsOfEveryTest()
    {
        // Arrange
        var files = TestSources();

        // Act
        var offenders = Offenders(files, ConventionRules.TestMethodsMissingSections);

        // Assert
        offenders.Should().BeEquivalentTo(Array.Empty<string>(), "every test has // Arrange, // Act and // Assert comments");
    }

    [Fact]
    public void TestMethods_GivenTheTestSources_Should_UseFluentAssertionsAndNeverXunitAssertions()
    {
        // Arrange
        var files = TestSources();

        // Act
        var offenders = Offenders(files, ConventionRules.XunitAssertCalls);

        // Assert
        offenders.Should().BeEquivalentTo(Array.Empty<string>(), "assertions use FluentAssertions");
    }

    [Fact]
    public void TestMethods_GivenTheTestSources_Should_ContainNoConditionalsLoopsOrTryBlocks()
    {
        // Arrange
        var files = TestSources();

        // Act
        var offenders = Offenders(files, ConventionRules.TestMethodsWithControlFlow)
            .Concat(Offenders(files, ConventionRules.TestMethodsWithConditionalExpressions))
            .ToList();

        // Assert — logic belongs in a helper, or becomes a Theory with data
        offenders.Should().BeEquivalentTo(Array.Empty<string>(), "a test with logic needs a test of its own");
    }

    [Fact]
    public void TestSources_GivenTheTestTree_Should_NeverReadTheSystemClock()
    {
        // Arrange — the infrastructure folder checks tokens the real services sign with the real clock, so it is left out
        var files = TestSources().Where(file => !file.RelativePath.Contains($"{Path.DirectorySeparatorChar}Infrastructure{Path.DirectorySeparatorChar}", StringComparison.Ordinal)).ToList();

        // Act
        var offenders = Offenders(files, ConventionRules.ClockReads);

        // Assert
        offenders.Should().BeEquivalentTo(Array.Empty<string>(), "inject a TimeProvider: a test that reads the clock is a test that can fail at midnight");
    }

    [Theory]
    [MemberData(nameof(ProjectFolders))]
    public void SourceFiles_GivenAnyProject_Should_UseBracesOnEveryConditionalAndLoop(string project)
    {
        // Arrange
        var files = Sources(project);

        // Act
        var offenders = Offenders(files, ConventionRules.StatementsWithoutBraces);

        // Assert
        offenders.Should().BeEquivalentTo(Array.Empty<string>(), "CLAUDE.md: always use braces on conditional statements");
    }

    [Fact]
    public void TestSources_GivenTheTestTree_Should_NeverWaitForTimeToPassWithTaskDelay()
    {
        // Arrange
        var files = TestSources();

        // Act
        var offenders = Offenders(files, ConventionRules.DelayCalls);

        // Assert — a gate, a cancellation or an injected clock says exactly what is being waited for
        offenders.Should().BeEquivalentTo(Array.Empty<string>(), "use TimeProvider, a TaskCompletionSource or a cancellation, not a delay");
    }

    [Fact]
    public void TestSources_GivenTestsThatRecordTelemetry_Should_JoinTheTelemetryCollection()
    {
        // Arrange
        var files = TestSources().Where(file => !file.RelativePath.Contains($"{Path.DirectorySeparatorChar}Infrastructure{Path.DirectorySeparatorChar}", StringComparison.Ordinal)).ToList();

        // Act
        var offenders = Offenders(files, ConventionRules.TelemetryTestsOutsideTheTelemetryCollection);

        // Assert
        offenders.Should().BeEquivalentTo(Array.Empty<string>(), "the meters and activity sources are process-wide: [Collection(TelemetryCollection.Name)]");
    }

    [Fact]
    public void TestSources_GivenTheTestTree_Should_UseTheSharedRecordersAndNotListenersOfTheirOwn()
    {
        // Arrange
        var files = TestSources().Where(file => !file.RelativePath.Contains($"{Path.DirectorySeparatorChar}Infrastructure{Path.DirectorySeparatorChar}", StringComparison.Ordinal)).ToList();

        // Act
        var offenders = Offenders(files, ConventionRules.HandRolledTelemetryListeners);

        // Assert
        offenders.Should().BeEquivalentTo(Array.Empty<string>(), "use MetricsRecorder and SpanRecorder");
    }

    [Theory]
    [MemberData(nameof(ProjectFolders))]
    public void SourceFiles_GivenAnyProject_Should_HaveNoInvisibleCharactersWrittenIntoThem(string project)
    {
        // Arrange
        var files = Sources(project, isGlobalUsingsIncluded: true);

        // Act
        var offenders = Offenders(files, ConventionRules.InvisibleCharacters);

        // Assert
        offenders.Should().BeEquivalentTo(Array.Empty<string>(), "write an escape sequence such as \\u200B, which a reader can see");
    }

    [Fact]
    public void TestSources_GivenTheTestTree_Should_DeclareANamespaceThatMirrorsTheirFolder()
    {
        // Arrange
        var files = TestSources();

        // Act
        var offenders = files
            .Select(file => (File: file, Expected: ExpectedNamespace(file), Actual: ConventionRules.FileScopedNamespace(file.Text)))
            .Where(item => item.Actual != item.Expected)
            .Select(item => $"{item.File.RelativePath}: namespace is '{item.Actual}', expected '{item.Expected}'")
            .ToList();

        // Assert
        offenders.Should().BeEquivalentTo(Array.Empty<string>(), "the namespace of a test mirrors its folder, as CLAUDE.md says");
    }

    [Fact]
    public void TestSources_GivenAClassUnderTest_Should_SitInTheFolderThatMirrorsWhereTheClassLives()
    {
        // Arrange
        var productionFolders = ProductionSources()
            .GroupBy(file => Path.GetFileNameWithoutExtension(file.Path))
            .ToDictionary(group => group.Key, group => group.First().RelativePath.Split(Path.DirectorySeparatorChar)[2]);

        // Act
        var offenders = TestSources()
            .Where(file => file.Path.EndsWith("Tests.cs", StringComparison.Ordinal))
            .Select(file => (File: file, Subject: Path.GetFileNameWithoutExtension(file.Path)[..^"Tests".Length]))
            .Where(item => productionFolders.ContainsKey(item.Subject))
            .Where(item => FolderUnderTests(item.File) != productionFolders[item.Subject])
            .Select(item => $"{item.File.RelativePath}: tests {item.Subject}, which lives in '{productionFolders[item.Subject]}'")
            .ToList();

        // Assert
        productionFolders.Should().NotBeEmpty("this test is meaningless if it found no production files");
        offenders.Should().BeEquivalentTo(Array.Empty<string>(), "a test sits in the folder of the class it tests");
    }

    private static string FolderUnderTests(SourceFile file)
    {
        var segments = file.RelativePath.Split(Path.DirectorySeparatorChar).Skip(2).ToList();

        return segments.Count > 1 ? segments[0] : string.Empty;
    }

    private static string ExpectedNamespace(SourceFile file)
    {
        return ConventionRules.TestNamespaceFor(TestNamespaceRoot, Path.GetRelativePath(Path.Combine(_repositoryRoot, _testFolder), file.Path));
    }
}
