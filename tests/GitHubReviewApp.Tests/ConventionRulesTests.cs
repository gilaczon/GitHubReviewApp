namespace GitHubReviewApp.Tests;

/// <summary>
/// Proves each convention rule can fail. A check that has never been seen to catch anything looks exactly like one that cannot: every rule is run
/// on a snippet that breaks it, which must be flagged, and on one that follows it, which must not.
/// </summary>
public class ConventionRulesTests
{
    // ── Layout ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("using System;\nnamespace A;\nclass C { }")]
    [InlineData("global using System;")]
    [InlineData("namespace A;\nusing Alias = System.String;\nclass C { }")]
    [InlineData("namespace A { using System.IO; class C { } }")]
    public void UsingDirectives_GivenAnyUsingDirective_Should_FlagIt(string source)
    {
        // Arrange

        // Act
        var findings = ConventionRules.UsingDirectives(source);

        // Assert
        findings.Should().ContainSingle();
    }

    [Fact]
    public void UsingDirectives_GivenOnlyAUsingStatementOrDeclaration_Should_FlagNothing()
    {
        // Arrange
        const string source = "namespace A;\nclass C { void M() { using var stream = new System.IO.MemoryStream(); using (new System.IO.MemoryStream()) { } } }";

        // Act
        var findings = ConventionRules.UsingDirectives(source);

        // Assert
        findings.Should().BeEmpty("a using statement is not a using directive");
    }

    [Fact]
    public void BlockScopedNamespaces_GivenABlockScopedNamespace_Should_FlagIt()
    {
        // Arrange

        // Act
        var findings = ConventionRules.BlockScopedNamespaces("namespace A { class C { } }");

        // Assert
        findings.Should().ContainSingle();
    }

    [Fact]
    public void BlockScopedNamespaces_GivenAFileScopedNamespace_Should_FlagNothing()
    {
        // Arrange

        // Act
        var findings = ConventionRules.BlockScopedNamespaces("namespace A.B;\nclass C { }");

        // Assert
        findings.Should().BeEmpty();
    }

    [Fact]
    public void FileScopedNamespace_GivenAFileScopedNamespace_Should_ReturnItsName()
    {
        // Arrange

        // Act
        var name = ConventionRules.FileScopedNamespace("namespace GitHubReviewApp.Tests.Review;\nclass C { }");

        // Assert
        name.Should().Be("GitHubReviewApp.Tests.Review");
    }

    [Fact]
    public void TopLevelTypeNames_GivenSeveralKindsOfType_Should_ListEachTopLevelOneAndNoNestedOne()
    {
        // Arrange
        const string source = "namespace A;\npublic class X { class Inner { } }\ninternal record Y(int Z);\nenum E { One }\ndelegate void D();";

        // Act
        var names = ConventionRules.TopLevelTypeNames(source);

        // Assert
        names.Should().Equal("X", "Y", "E", "D");
    }

    // ── Names ────────────────────────────────────────────────────────────────

    [Fact]
    public void PrivateFieldsWithoutLeadingUnderscore_GivenPrivateFieldsInEveryForm_Should_FlagEachOne()
    {
        // Arrange — including "private static readonly", which only constants may write in PascalCase, and a field with no modifier at all
        const string source = "class C { private static readonly int Foo = 1; private int bar; int baz; private readonly string a, b; }";

        // Act
        var findings = ConventionRules.PrivateFieldsWithoutLeadingUnderscore(source);

        // Assert
        findings.Should().HaveCount(5);
    }

    [Fact]
    public void PrivateFieldsWithoutLeadingUnderscore_GivenConstantsUnderscoredFieldsAndNonPrivateFields_Should_FlagNothing()
    {
        // Arrange
        const string source =
            "class C { private const int Foo = 1; private static readonly int _foo = 1; public int Bar; protected int Baz; internal int Qux; private protected int Quux; }";

        // Act
        var findings = ConventionRules.PrivateFieldsWithoutLeadingUnderscore(source);

        // Assert
        findings.Should().BeEmpty();
    }

    [Fact]
    public void BooleansWithoutAQuestionPrefix_GivenBooleansNamedLikeNounsOrVerbs_Should_FlagEachOne()
    {
        // Arrange — "hash" and "island" start with the letters of a prefix without being one
        const string source =
            "class C { bool done; public bool Ready { get; } void M(bool flag) { bool ok = true; bool hash = false; bool island = true; } }";

        // Act
        var findings = ConventionRules.BooleansWithoutAQuestionPrefix(source);

        // Assert
        findings.Should().HaveCount(6);
    }

    [Fact]
    public void BooleansWithoutAQuestionPrefix_GivenBooleansWithAnIsHasOrCanPrefix_Should_FlagNothing()
    {
        // Arrange
        const string source =
            "class C { bool _isDone; public bool HasReady { get; } void M(bool canRetry) { bool isOk = true; bool Is = false; var anything = Compute(); } }";

        // Act
        var findings = ConventionRules.BooleansWithoutAQuestionPrefix(source);

        // Assert
        findings.Should().BeEmpty();
    }

    // ── Behaviour ────────────────────────────────────────────────────────────

    [Fact]
    public void BlockingCalls_GivenEveryWayToBlockOnATask_Should_FlagEachOne()
    {
        // Arrange
        const string source = "class C { void M(Task t) { var a = t.Result; t.Wait(); t.Wait(1000); t.GetAwaiter().GetResult(); Thread.Sleep(1); } }";

        // Act
        var findings = ConventionRules.BlockingCalls(source);

        // Assert
        findings.Should().HaveCount(5);
    }

    [Fact]
    public void BlockingCalls_GivenOnlyAwaitingAndAsyncWaits_Should_FlagNothing()
    {
        // Arrange
        const string source = "class C { async Task M(Task t, object o) { await t; await t.WaitAsync(default(CancellationToken)); var r = o.ToString(); } }";

        // Act
        var findings = ConventionRules.BlockingCalls(source);

        // Assert
        findings.Should().BeEmpty();
    }

    [Fact]
    public void DelayCalls_GivenATaskDelay_Should_FlagIt()
    {
        // Arrange

        // Act
        var findings = ConventionRules.DelayCalls("class C { async Task M() { await Task.Delay(10); } }");

        // Assert
        findings.Should().ContainSingle();
    }

    [Fact]
    public void DelayCalls_GivenAnAsyncWaitOnAGate_Should_FlagNothing()
    {
        // Arrange

        // Act
        var findings = ConventionRules.DelayCalls("class C { async Task M(Task gate) { await gate.WaitAsync(default(CancellationToken)); } }");

        // Assert
        findings.Should().BeEmpty();
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public void XunitAssertCalls_GivenPlainGenericAndQualifiedAsserts_Should_FlagEachOne()
    {
        // Arrange
        const string source = "class T { void M() { Assert.Equal(1, 1); Assert.Throws<X>(() => { }); Xunit.Assert.True(true); } }";

        // Act
        var findings = ConventionRules.XunitAssertCalls(source);

        // Assert
        findings.Should().HaveCount(3);
    }

    [Fact]
    public void XunitAssertCalls_GivenFluentAssertionsAndMockVerification_Should_FlagNothing()
    {
        // Arrange
        const string source = "class T { void M() { value.Should().Be(1); mock.Verify(m => m.Foo()); FluentAssert.Check(); } }";

        // Act
        var findings = ConventionRules.XunitAssertCalls(source);

        // Assert
        findings.Should().BeEmpty();
    }

    [Fact]
    public void TestMethodsWithControlFlow_GivenTestsWithConditionalsLoopsAndTry_Should_FlagEachOne()
    {
        // Arrange — one of them inside a lambda, which is still logic in the test
        const string source =
            "class T { [Fact] void A() { if (true) { } } [Theory] void B() { foreach (var x in new int[0]) { } } [Fact] void C() { try { } catch { } } " +
            "[Fact] void D() { Func<int> f = () => { while (true) { } }; } [Fact] void E() { switch (1) { default: break; } } }";

        // Act
        var findings = ConventionRules.TestMethodsWithControlFlow(source);

        // Assert
        findings.Should().HaveCount(5);
    }

    [Fact]
    public void TestMethodsWithControlFlow_GivenLogicOnlyInAHelperAndNoneInTheTest_Should_FlagNothing()
    {
        // Arrange
        const string source = "class T { [Fact] void A() { Helper(); var x = true ? 1 : 2; } void Helper() { if (true) { } } }";

        // Act
        var findings = ConventionRules.TestMethodsWithControlFlow(source);

        // Assert
        findings.Should().BeEmpty();
    }

    [Theory]
    [InlineData("class T { [Fact] void A() { var x = 1; } }")]
    [InlineData("class T { [Fact] void A() { // Arrange\n // Assert\n } }")]
    [InlineData("class T { [Theory] void A() { // Act\n // Assert\n } }")]
    [InlineData("class T { [LiveFact] void A() { // Arrange\n // Act\n } }")]
    public void TestMethodsMissingSections_GivenATestWithoutAllThreeSections_Should_FlagIt(string source)
    {
        // Arrange

        // Act
        var findings = ConventionRules.TestMethodsMissingSections(source);

        // Assert
        findings.Should().ContainSingle();
    }

    [Theory]
    [InlineData("class T { [Fact] void A() { // Arrange\n // Act\n // Assert\n } }")]
    [InlineData("class T { [Fact] void A() { // Arrange — some reason\n // Act\n // Assert — what is shown\n } }")]
    [InlineData("class T { [Fact] async Task A() { // Arrange\n // Act & Assert\n await act.Should().ThrowAsync<X>(); } }")]
    [InlineData("class T { [Fact] void A() { // Arrange\n // Act & Assert\n act.Should().Throw<X>(); } }")]
    [InlineData("class T { [Fact] async Task A() { // Arrange\n // Act & Assert\n await act.Should().NotThrowAsync(); } }")]
    [InlineData("class T { void Helper() { } }")]
    public void TestMethodsMissingSections_GivenCompleteSectionsOrNoTest_Should_FlagNothing(string source)
    {
        // Arrange

        // Act
        var findings = ConventionRules.TestMethodsMissingSections(source);

        // Assert
        findings.Should().BeEmpty();
    }

    [Theory]
    [InlineData("class T { [Fact] void A() { // Arrange & Act\n // Assert\n } }")]
    [InlineData("class T { [Fact] void A() { // Arrange & Act & Assert\n } }")]
    [InlineData("class T { [Fact] void A() { // Arrange\n // Act & Assert\n x.Should().Be(1); } }")]
    [InlineData("class T { [Fact] void A() { // Act\n // Assert\n } }")]
    public void TestMethodsMissingSections_GivenSectionsThatAreCombinedWhereTheyMayNotBe_Should_FlagIt(string source)
    {
        // Arrange

        // Act
        var findings = ConventionRules.TestMethodsMissingSections(source);

        // Assert
        findings.Should().ContainSingle();
    }

    [Theory]
    [InlineData("class T { [Theory] [InlineData(\"// Arrange // Act // Assert\")] void A(string s) { var x = 1; } }")]
    [InlineData("class T { [Fact] void A() { var s = \"// Arrange\\n// Act\\n// Assert\"; } }")]
    [InlineData("class T { [Fact] void A() { var s = 1; /* Arrange */ /* Act */ /* Assert */ } }")]
    public void TestMethodsMissingSections_GivenTheWordsOnlyInAnAttributeAStringOrABlockComment_Should_FlagIt(string source)
    {
        // Arrange

        // Act
        var findings = ConventionRules.TestMethodsMissingSections(source);

        // Assert
        findings.Should().ContainSingle("only single-line comments in the body mark a section");
    }

    [Theory]
    [InlineData("class T { void M() { using var spans = new SpanRecorder(\"x\"); } }")]
    [InlineData("class T { void M() { using var metrics = new MetricsRecorder(); } }")]
    [InlineData("[Collection(\"Other\")] class T { void M() { using var metrics = new MetricsRecorder(); } }")]
    [InlineData("class T { void M() { var listener = new MeterListener(); } }")]
    public void TelemetryTestsOutsideTheTelemetryCollection_GivenARecorderAndNoTelemetryCollection_Should_FlagTheClass(string source)
    {
        // Arrange

        // Act
        var findings = ConventionRules.TelemetryTestsOutsideTheTelemetryCollection(source);

        // Assert
        findings.Should().ContainSingle();
    }

    [Theory]
    [InlineData("[Collection(TelemetryCollection.Name)] class T { void M() { using var metrics = new MetricsRecorder(); } }")]
    [InlineData("[Xunit.Collection(TelemetryCollection.Name)] class T { void M() { using var spans = new SpanRecorder(\"x\"); } }")]
    [InlineData("class T { void M() { var list = new List<int>(); } }")]
    public void TelemetryTestsOutsideTheTelemetryCollection_GivenTheCollectionOrNoRecorder_Should_FlagNothing(string source)
    {
        // Arrange

        // Act
        var findings = ConventionRules.TelemetryTestsOutsideTheTelemetryCollection(source);

        // Assert
        findings.Should().BeEmpty();
    }

    [Theory]
    [InlineData("class T { void M() { var l = new MeterListener(); } }")]
    [InlineData("class T { void M() { var l = new ActivityListener(); } }")]
    public void HandRolledTelemetryListeners_GivenAListenerCreatedInATest_Should_FlagIt(string source)
    {
        // Arrange

        // Act
        var findings = ConventionRules.HandRolledTelemetryListeners(source);

        // Assert
        findings.Should().ContainSingle();
    }

    [Fact]
    public void HandRolledTelemetryListeners_GivenTheRecorders_Should_FlagNothing()
    {
        // Arrange

        // Act
        var findings = ConventionRules.HandRolledTelemetryListeners("class T { void M() { using var m = new MetricsRecorder(); using var s = new SpanRecorder(\"x\"); } }");

        // Assert
        findings.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0x200B)]
    [InlineData(0x200C)]
    [InlineData(0x200D)]
    [InlineData(0x2060)]
    [InlineData(0x00A0)]
    public void InvisibleCharacters_GivenAnInvisibleCharacterInTheSource_Should_FlagItByItsCodePoint(int codePoint)
    {
        // Arrange
        var source = $"class C {{\n    string s = \"a{(char)codePoint}b\";\n}}";

        // Act
        var findings = ConventionRules.InvisibleCharacters(source);

        // Assert
        findings.Should().ContainSingle().Which.Should().Be($"line 2: U+{codePoint:X4}");
    }

    [Fact]
    public void InvisibleCharacters_GivenTheEscapeSequenceAndAByteOrderMarkAtTheStart_Should_FlagNothing()
    {
        // Arrange — the escape is plain text a reader can see; a BOM at the start of a file is how some editors save UTF-8
        var source = (char)0xFEFF + "class C { string s = " + (char)0x22 + "a" + (char)0x5C + "u200Bb" + (char)0x22 + "; }";

        // Act
        var findings = ConventionRules.InvisibleCharacters(source);

        // Assert
        findings.Should().BeEmpty();
    }

    // ── Member order ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("class C { void M() { } int _f; }")]
    [InlineData("class C { int P { get; } int _f; }")]
    [InlineData("class C { C() { } int _f; }")]
    [InlineData("class C { class N { } C() { } }")]
    [InlineData("class C { void M() { } int P { get; } }")]
    [InlineData("class C { void M() { } class N { } }")]
    public void MembersOutOfOrder_GivenAMemberThatComesAfterOneOfALaterGroup_Should_FlagIt(string source)
    {
        // Arrange

        // Act
        var findings = ConventionRules.MembersOutOfOrder(source);

        // Assert
        findings.Should().ContainSingle();
    }

    [Fact]
    public void MembersOutOfOrder_GivenFieldsConstructorsNestedTypesPropertiesThenMethods_Should_FlagNothing()
    {
        // Arrange
        const string source = "class C { int _f; event System.Action E; C() { } class N { } int P { get; } void M() { } void N2() { } }";

        // Act
        var findings = ConventionRules.MembersOutOfOrder(source);

        // Assert
        findings.Should().BeEmpty();
    }

    [Fact]
    public void MembersOutOfOrder_GivenSeveralMisplacedMembersInOneType_Should_ReportOnlyTheFirst()
    {
        // Arrange
        const string source = "class C { void M() { } int _f; int _g; C() { } }";

        // Act
        var findings = ConventionRules.MembersOutOfOrder(source);

        // Assert
        findings.Should().ContainSingle("one misplaced member is one finding, not a finding for everything after it");
    }

    // ── The clock ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("class T { void M() { var x = DateTime.Now; } }")]
    [InlineData("class T { void M() { var x = DateTime.UtcNow; } }")]
    [InlineData("class T { void M() { var x = DateTime.Today; } }")]
    [InlineData("class T { void M() { var x = DateTimeOffset.UtcNow.AddHours(1); } }")]
    [InlineData("class T { void M() { var x = DateTimeOffset.Now; } }")]
    public void ClockReads_GivenAReadOfTheSystemClock_Should_FlagIt(string source)
    {
        // Arrange

        // Act
        var findings = ConventionRules.ClockReads(source);

        // Assert
        findings.Should().ContainSingle();
    }

    [Theory]
    [InlineData("class T { void M() { var x = time.GetUtcNow(); var y = DateTimeOffset.MinValue; var z = new DateTime(2026, 1, 1); } }")]
    [InlineData("class T { void M() { var x = Order.Now; var y = Clock.UtcNow; } }")]
    public void ClockReads_GivenAnInjectedClockAFixedDateOrAnUnrelatedNow_Should_FlagNothing(string source)
    {
        // Arrange

        // Act
        var findings = ConventionRules.ClockReads(source);

        // Assert
        findings.Should().BeEmpty();
    }

    // ── Conditional expressions in a test ────────────────────────────────────

    [Theory]
    [InlineData("class T { [Fact] void A() { var x = true ? 1 : 2; } }")]
    [InlineData("class T { [Theory] void A() { var x = 1 switch { 1 => 2, _ => 3 }; } }")]
    public void TestMethodsWithConditionalExpressions_GivenOneWrittenInTheTest_Should_FlagIt(string source)
    {
        // Arrange

        // Act
        var findings = ConventionRules.TestMethodsWithConditionalExpressions(source);

        // Assert
        findings.Should().ContainSingle();
    }

    [Fact]
    public void TestMethodsWithConditionalExpressions_GivenOnesInAHelperAndInsideALambdaOrLocalFunction_Should_FlagNothing()
    {
        // Arrange — how a fake decides what to answer is not the test's own logic
        const string source =
            "class T { [Fact] void A() { Func<int, int> fake = n => n > 0 ? 1 : 2; int Local() { return true ? 1 : 2; } } int Helper() { return true ? 1 : 2; } }";

        // Act
        var findings = ConventionRules.TestMethodsWithConditionalExpressions(source);

        // Assert
        findings.Should().BeEmpty();
    }

    // ── Test names ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Format_GivenAReview_Should_PostIt")]
    [InlineData("A_GivenB_Should_C")]
    public void TestMethodNameProblem_GivenAWellFormedName_Should_ReturnNull(string name)
    {
        // Arrange

        // Act
        var problem = ConventionRules.TestMethodNameProblem(name);

        // Assert
        problem.Should().BeNull();
    }

    [Theory]
    [InlineData("Format_Should_PostIt")]
    [InlineData("Format_GivenAReview")]
    [InlineData("FormatGivenAReviewShouldPostIt")]
    [InlineData("Format_given_a_review_should_post_it")]
    [InlineData("Format_GivenAReview_Should")]
    [InlineData("Format_Given_Should_PostIt")]
    public void TestMethodNameProblem_GivenANameThatBreaksThePattern_Should_SayWhy(string name)
    {
        // Arrange

        // Act
        var problem = ConventionRules.TestMethodNameProblem(name);

        // Assert
        problem.Should().NotBeNull();
    }

    // ── Braces ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("class C { void M() { if (true) Do(); } }")]
    [InlineData("class C { void M() { if (true) { } else Do(); } }")]
    [InlineData("class C { void M() { for (var i = 0; i < 3; i++) Do(); } }")]
    [InlineData("class C { void M() { foreach (var x in y) Do(); } }")]
    [InlineData("class C { void M() { while (true) Do(); } }")]
    [InlineData("class C { void M() { do Do(); while (true); } }")]
    public void StatementsWithoutBraces_GivenAConditionalOrLoopWithoutBraces_Should_FlagIt(string source)
    {
        // Arrange

        // Act
        var findings = ConventionRules.StatementsWithoutBraces(source);

        // Assert
        findings.Should().ContainSingle();
    }

    [Fact]
    public void StatementsWithoutBraces_GivenBracesEverywhereAndAnElseIf_Should_FlagNothing()
    {
        // Arrange
        const string source = "class C { void M() { if (a) { Do(); } else if (b) { Do(); } else { Do(); } foreach (var x in y) { Do(); } while (a) { Do(); } } }";

        // Act
        var findings = ConventionRules.StatementsWithoutBraces(source);

        // Assert
        findings.Should().BeEmpty();
    }

    [Fact]
    public void StatementsWithoutBraces_GivenAnElseIfWhoseOwnBodyHasNoBraces_Should_FlagThatBody()
    {
        // Arrange
        const string source = "class C { void M() { if (a) { Do(); } else if (b) Do(); } }";

        // Act
        var findings = ConventionRules.StatementsWithoutBraces(source);

        // Assert
        findings.Should().ContainSingle();
    }

    // ── What a GlobalUsings file may hold ────────────────────────────────────

    [Theory]
    [InlineData("global using System;\nusing System.IO;")]
    [InlineData("global using System;\nclass Oops { }")]
    [InlineData("global using System;\nnamespace N;")]
    public void AnythingButGlobalUsings_GivenAnythingElseInTheFile_Should_FlagIt(string source)
    {
        // Arrange

        // Act
        var findings = ConventionRules.AnythingButGlobalUsings(source);

        // Assert
        findings.Should().ContainSingle();
    }

    [Fact]
    public void AnythingButGlobalUsings_GivenOnlyGlobalUsings_Should_FlagNothing()
    {
        // Arrange
        const string source = "global using System;\nglobal using System.Linq;\nglobal using Alias = System.String;";

        // Act
        var findings = ConventionRules.AnythingButGlobalUsings(source);

        // Assert
        findings.Should().BeEmpty();
    }

    // ── The namespace a test declares ────────────────────────────────────────

    [Theory]
    [InlineData("Services/ClaudeServiceTests.cs", "GitHubReviewApp.Tests.Services")]
    [InlineData("Infrastructure/Deep/Thing.cs", "GitHubReviewApp.Tests.Infrastructure.Deep")]
    [InlineData("SourceConventionTests.cs", "GitHubReviewApp.Tests")]
    [InlineData("Review\\MarkdownTests.cs", "GitHubReviewApp.Tests.Review")]
    public void TestNamespaceFor_GivenAFileInsideTheTestProject_Should_NameTheProjectThenEachFolder(string relativePath, string expected)
    {
        // Arrange

        // Act
        var name = ConventionRules.TestNamespaceFor("GitHubReviewApp.Tests", relativePath);

        // Assert
        name.Should().Be(expected);
    }

    // ── Characters that cannot be seen ───────────────────────────────────────

    [Theory]
    [InlineData(0x202A)]
    [InlineData(0x202B)]
    [InlineData(0x202C)]
    [InlineData(0x202D)]
    [InlineData(0x202E)]
    [InlineData(0x2066)]
    [InlineData(0x2067)]
    [InlineData(0x2068)]
    [InlineData(0x2069)]
    [InlineData(0x200E)]
    [InlineData(0x200F)]
    [InlineData(0x00AD)]
    [InlineData(0x061C)]
    public void InvisibleCharacters_GivenATextDirectionOrFormatCharacterWrittenIntoTheSource_Should_FlagIt(int codePoint)
    {
        // Arrange — the characters that make code read in a different order from the one it runs in
        var source = "class C { string s = " + (char)0x22 + "a" + (char)codePoint + "b" + (char)0x22 + "; }";

        // Act
        var findings = ConventionRules.InvisibleCharacters(source);

        // Assert
        findings.Should().ContainSingle().Which.Should().Contain($"U+{codePoint:X4}");
    }

    // ── Booleans written with var ────────────────────────────────────────────

    [Theory]
    [InlineData("class C { void M() { var done = a == b; } }")]
    [InlineData("class C { void M() { var found = items.Any(x => x > 1); } }")]
    [InlineData("class C { void M() { var matches = name.Contains(\"x\"); } }")]
    [InlineData("class C { void M() { var both = a && b; } }")]
    [InlineData("class C { void M() { var missing = !items.Any(); } }")]
    [InlineData("class C { void M() { var big = count > 3; } }")]
    [InlineData("class C { void M() { var typed = value is string; } }")]
    [InlineData("class C { void M() { var flag = true; } }")]
    public void BooleansWithoutAQuestionPrefix_GivenAVarThatCanOnlyHoldABoolean_Should_FlagItsName(string source)
    {
        // Arrange

        // Act
        var findings = ConventionRules.BooleansWithoutAQuestionPrefix(source);

        // Assert
        findings.Should().ContainSingle();
    }

    [Theory]
    [InlineData("class C { void M() { var isDone = a == b; var hasAny = items.Any(); var canRun = a && b; } }")]
    [InlineData("class C { void M() { var count = items.Count; var first = items.First(); var text = name.Trim(); } }")]
    public void BooleansWithoutAQuestionPrefix_GivenQuestionNamesOrNonBooleanInitialisers_Should_FlagNothing(string source)
    {
        // Arrange

        // Act
        var findings = ConventionRules.BooleansWithoutAQuestionPrefix(source);

        // Assert
        findings.Should().BeEmpty();
    }

    // ── Blocking through a null-conditional access ───────────────────────────

    [Theory]
    [InlineData("class C { void M() { var r = task?.Result; } }")]
    [InlineData("class C { void M() { task?.Wait(); } }")]
    [InlineData("class C { void M() { task?.Wait(1000); } }")]
    public void BlockingCalls_GivenABlockingCallThroughANullConditionalAccess_Should_FlagIt(string source)
    {
        // Arrange

        // Act
        var findings = ConventionRules.BlockingCalls(source);

        // Assert
        findings.Should().ContainSingle();
    }

    // ── Telemetry recorders held in a field ──────────────────────────────────

    [Fact]
    public void TelemetryTestsOutsideTheTelemetryCollection_GivenARecorderOnlyHeldInAFieldOrReturnedByAHelper_Should_FlagTheClass()
    {
        // Arrange — no "new" in the class: the recorder comes from somewhere else but is still process-wide
        const string source = "class T { private MetricsRecorder _metrics; MetricsRecorder Start() { return Factory.Create(); } }";

        // Act
        var findings = ConventionRules.TelemetryTestsOutsideTheTelemetryCollection(source);

        // Assert
        findings.Should().ContainSingle();
    }
}
