namespace GitHubReviewApp.Tests.Infrastructure;

/// <summary>
/// The project's coding conventions as checks over C# syntax. Each rule takes the source text of one file and returns what breaks it, so a test
/// can run it over every real file and, with a deliberately bad snippet, prove that it can fail. Checks are syntactic: they know names and
/// shapes, not types, so a rule that needs a type to be sure (such as the blocking calls) is written to be stricter, not looser, than it would
/// need to be.
/// </summary>
internal static class ConventionRules
{
    private static readonly string[] _testAttributeNames = ["Fact", "Theory", "LiveFact"];

    // The names FluentAssertions gives an assertion about an exception. A test that has one of them is a test that expects (or rules out) a throw.
    private static readonly string[] _exceptionAssertionNames = ["Throw", "ThrowAsync", "ThrowExactly", "ThrowExactlyAsync", "NotThrow", "NotThrowAsync"];

    // The comments that mark the sections of a test. Each is matched at the start of the comment text, so a word that merely appears later in one does not count.
    private static readonly Regex _arrangeAndActSection = new(@"^Arrange\s*&\s*Act\b", RegexOptions.CultureInvariant);
    private static readonly Regex _arrangeSection = new(@"^Arrange\b", RegexOptions.CultureInvariant);
    private static readonly Regex _actAndAssertSection = new(@"^Act\s*&\s*Assert\b", RegexOptions.CultureInvariant);
    private static readonly Regex _actSection = new(@"^Act\b", RegexOptions.CultureInvariant);
    private static readonly Regex _assertSection = new(@"^Assert\b", RegexOptions.CultureInvariant);

    // Characters that cannot be seen in an editor, or that change which way text reads. A test that needs one writes its escape sequence.
    private static readonly HashSet<int> _invisibleCodePoints =
    [
        0x00A0, 0x00AD, 0x061C, 0x180E, 0x200B, 0x200C, 0x200D, 0x200E, 0x200F, 0x2060, 0xFEFF,
        0x202A, 0x202B, 0x202C, 0x202D, 0x202E,
        0x2066, 0x2067, 0x2068, 0x2069
    ];

    // Method names that return a bool wherever they are found, so a variable they initialise is a boolean even where its type is written as var.
    private static readonly string[] _boolReturningMethods = ["Any", "All", "Contains", "ContainsKey", "StartsWith", "EndsWith", "IsMatch", "TryGetValue", "TryParse", "Equals"];

    private static readonly Regex _testMethodName = new(@"^[A-Za-z0-9]+_Given[A-Za-z0-9]+_Should_[A-Za-z0-9]+$", RegexOptions.CultureInvariant);

    // ── Layout ───────────────────────────────────────────────────────────────

    /// <summary>Every using directive, global or not, wherever it sits. Outside GlobalUsings.cs there must be none.</summary>
    public static IReadOnlyList<string> UsingDirectives(string source)
    {
        return Root(source).DescendantNodes().OfType<UsingDirectiveSyntax>()
            .Select(directive => Describe(directive, directive.ToString().Trim()))
            .ToList();
    }

    public static IReadOnlyList<string> BlockScopedNamespaces(string source)
    {
        return Root(source).DescendantNodes().OfType<NamespaceDeclarationSyntax>()
            .Select(declaration => Describe(declaration, $"namespace {declaration.Name} {{ }}"))
            .ToList();
    }

    public static string? FileScopedNamespace(string source)
    {
        return Root(source).DescendantNodes().OfType<FileScopedNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
    }

    /// <summary>The names of the types and delegates declared at the top level of the file, in order.</summary>
    public static IReadOnlyList<string> TopLevelTypeNames(string source)
    {
        var root = Root(source);

        var topLevelMembers = root.Members
            .SelectMany(member => member is BaseNamespaceDeclarationSyntax declaration ? declaration.Members : [member]);

        return topLevelMembers
            .Select(member => member switch
            {
                BaseTypeDeclarationSyntax type => type.Identifier.Text,
                DelegateDeclarationSyntax @delegate => @delegate.Identifier.Text,
                _ => null
            })
            .OfType<string>()
            .ToList();
    }

    // ── Names ────────────────────────────────────────────────────────────────

    /// <summary>Private fields that are not constants and do not start with an underscore. Static and readonly ones included: only constants are PascalCase.</summary>
    public static IReadOnlyList<string> PrivateFieldsWithoutLeadingUnderscore(string source)
    {
        return Root(source).DescendantNodes().OfType<FieldDeclarationSyntax>()
            .Where(field => IsPrivate(field.Modifiers) && !field.Modifiers.Any(SyntaxKind.ConstKeyword))
            .SelectMany(field => field.Declaration.Variables)
            .Where(variable => !variable.Identifier.Text.StartsWith('_'))
            .Select(variable => Describe(variable, variable.Identifier.Text))
            .ToList();
    }

    /// <summary>
    /// Booleans whose names do not start with Is, Has or Can: those declared with an explicit bool type, and those declared with var whose
    /// initialiser can only be a boolean (a comparison, a logical operator, a pattern test, a literal, or a call such as Any or Contains that
    /// returns one wherever it is found). A leading underscore is ignored. Methods are not included: their names are verbs.
    /// </summary>
    public static IReadOnlyList<string> BooleansWithoutAQuestionPrefix(string source)
    {
        var root = Root(source);

        var declared = new List<(SyntaxNode Node, string Name)>();
        declared.AddRange(root.DescendantNodes().OfType<VariableDeclarationSyntax>()
            .Where(declaration => IsBool(declaration.Type))
            .SelectMany(declaration => declaration.Variables)
            .Select(variable => ((SyntaxNode)variable, variable.Identifier.Text)));
        declared.AddRange(root.DescendantNodes().OfType<VariableDeclarationSyntax>()
            .Where(declaration => declaration.Type.IsVar)
            .SelectMany(declaration => declaration.Variables)
            .Where(variable => variable.Initializer is { } initializer && IsObviouslyBoolean(initializer.Value))
            .Select(variable => ((SyntaxNode)variable, variable.Identifier.Text)));
        declared.AddRange(root.DescendantNodes().OfType<PropertyDeclarationSyntax>()
            .Where(property => IsBool(property.Type))
            .Select(property => ((SyntaxNode)property, property.Identifier.Text)));
        declared.AddRange(root.DescendantNodes().OfType<ParameterSyntax>()
            .Where(parameter => parameter.Type is not null && IsBool(parameter.Type))
            .Select(parameter => ((SyntaxNode)parameter, parameter.Identifier.Text)));

        return declared
            .Where(item => !HasQuestionPrefix(item.Name))
            .Select(item => Describe(item.Node, item.Name))
            .ToList();
    }

    // ── Behaviour ────────────────────────────────────────────────────────────

    /// <summary>Calls that block on async work: .Result, .Wait(...), WaitAll, WaitAny, GetAwaiter().GetResult() and Thread.Sleep. Always await instead.</summary>
    public static IReadOnlyList<string> BlockingCalls(string source)
    {
        var root = Root(source);

        var properties = root.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Where(access => access.Name.Identifier.Text == "Result" && access.Parent is not InvocationExpressionSyntax)
            .Select(access => Describe(access, access.ToString()));

        // The same through a null-conditional access: task?.Result and task?.Wait().
        var conditionalProperties = root.DescendantNodes().OfType<MemberBindingExpressionSyntax>()
            .Where(binding => binding.Name.Identifier.Text == "Result" && binding.Parent is not InvocationExpressionSyntax)
            .Select(binding => Describe(binding, $"?{binding}"));

        var calls = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(IsBlockingInvocation)
            .Select(call => Describe(call, call.Expression.ToString()));

        return properties.Concat(conditionalProperties).Concat(calls).ToList();
    }

    /// <summary>Task.Delay, which a test should not use to wait for something to happen: a gate or a cancellation says it exactly.</summary>
    public static IReadOnlyList<string> DelayCalls(string source)
    {
        return Root(source).DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(call => call.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "Task" }, Name.Identifier.Text: "Delay" })
            .Select(call => Describe(call, call.Expression.ToString()))
            .ToList();
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    /// <summary>Calls to xUnit's Assert, generic ones included. Assertions use FluentAssertions.</summary>
    public static IReadOnlyList<string> XunitAssertCalls(string source)
    {
        return Root(source).DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(call => call.Expression is MemberAccessExpressionSyntax { Expression: var target }
                && (target is IdentifierNameSyntax { Identifier.Text: "Assert" }
                    || target is MemberAccessExpressionSyntax { Name.Identifier.Text: "Assert" }))
            .Select(call => Describe(call, call.Expression.ToString()))
            .ToList();
    }

    /// <summary>Test methods that contain a conditional, a loop, a try or a switch. A test with logic needs a test of its own: use a Theory.</summary>
    public static IReadOnlyList<string> TestMethodsWithControlFlow(string source)
    {
        return TestMethods(source)
            .SelectMany(method => method.DescendantNodes()
                .Where(IsControlFlow)
                .Select(node => Describe(node, $"{method.Identifier.Text}: {node.Kind()}")))
            .ToList();
    }

    /// <summary>
    /// Test methods whose body does not mark an Arrange, an Act and an Assert section with comments of those names. The comments are read from the
    /// body only (an attribute's text or a string cannot supply them). "Arrange &amp; Act" is two sections written as one, and "Act &amp; Assert" is
    /// only for a test that expects an exception, where the act and the assertion are one statement.
    /// </summary>
    public static IReadOnlyList<string> TestMethodsMissingSections(string source)
    {
        return TestMethods(source)
            .Select(method => (Method: method, Problem: SectionProblem(method)))
            .Where(item => item.Problem is not null)
            .Select(item => Describe(item.Method, $"{item.Method.Identifier.Text}: {item.Problem}"))
            .ToList();
    }

    /// <summary>Test methods that wait for time to pass or read the clock themselves: DateTime and DateTimeOffset .Now, .UtcNow and .Today. Inject a TimeProvider.</summary>
    public static IReadOnlyList<string> ClockReads(string source)
    {
        return Root(source).DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Where(access => access.Expression is IdentifierNameSyntax { Identifier.Text: "DateTime" or "DateTimeOffset" }
                && access.Name.Identifier.Text is "Now" or "UtcNow" or "Today")
            .Select(access => Describe(access, access.ToString()))
            .ToList();
    }

    /// <summary>
    /// Test methods with a conditional (?:) or a switch expression written directly in them. One inside a lambda or a local function is how a fake
    /// decides what to answer, which is not the test's own logic and is left alone.
    /// </summary>
    public static IReadOnlyList<string> TestMethodsWithConditionalExpressions(string source)
    {
        return TestMethods(source)
            .SelectMany(method => method
                .DescendantNodes(node => node is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
                .Where(node => node is ConditionalExpressionSyntax or SwitchExpressionSyntax)
                .Select(node => Describe(node, $"{method.Identifier.Text}: {node.Kind()}")))
            .ToList();
    }

    /// <summary>A name that does not follow MethodName_GivenContext_Should_ExpectedOutcome, or null when it does.</summary>
    public static string? TestMethodNameProblem(string name)
    {
        return _testMethodName.IsMatch(name) ? null : $"'{name}' is not MethodName_GivenContext_Should_ExpectedOutcome";
    }

    /// <summary>An if, else, for, foreach, while or do whose body is not a block. CLAUDE.md: always use braces on conditional statements.</summary>
    public static IReadOnlyList<string> StatementsWithoutBraces(string source)
    {
        var root = Root(source);
        var bodies = new List<(SyntaxNode Owner, StatementSyntax Body, string Keyword)>();

        bodies.AddRange(root.DescendantNodes().OfType<IfStatementSyntax>().Select(statement => ((SyntaxNode)statement, statement.Statement, "if")));
        bodies.AddRange(root.DescendantNodes().OfType<ElseClauseSyntax>().Select(clause => ((SyntaxNode)clause, clause.Statement, "else")));
        bodies.AddRange(root.DescendantNodes().OfType<ForStatementSyntax>().Select(statement => ((SyntaxNode)statement, statement.Statement, "for")));
        bodies.AddRange(root.DescendantNodes().OfType<ForEachStatementSyntax>().Select(statement => ((SyntaxNode)statement, statement.Statement, "foreach")));
        bodies.AddRange(root.DescendantNodes().OfType<WhileStatementSyntax>().Select(statement => ((SyntaxNode)statement, statement.Statement, "while")));
        bodies.AddRange(root.DescendantNodes().OfType<DoStatementSyntax>().Select(statement => ((SyntaxNode)statement, statement.Statement, "do")));

        // "else if" is an else whose body is another if, which has its own braces to be judged.
        return bodies
            .Where(item => item.Body is not BlockSyntax && !(item.Keyword == "else" && item.Body is IfStatementSyntax))
            .Select(item => Describe(item.Owner, $"{item.Keyword} without braces"))
            .ToList();
    }

    /// <summary>Whatever a GlobalUsings file holds that is not a global using directive: any other directive, and any member at all.</summary>
    public static IReadOnlyList<string> AnythingButGlobalUsings(string source)
    {
        var root = Root(source);

        var otherDirectives = root.Usings
            .Where(directive => !directive.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword))
            .Select(directive => Describe(directive, directive.ToString().Trim()));

        var members = root.Members.Select(member => Describe(member, $"{member.Kind()}"));

        return otherDirectives.Concat(members).ToList();
    }

    /// <summary>The namespace a test file must declare: the test project's root, then one segment for each folder the file sits in below the project.</summary>
    public static string TestNamespaceFor(string projectNamespace, string pathRelativeToTheProjectFolder)
    {
        var folders = pathRelativeToTheProjectFolder.Replace('\\', '/').Split('/').SkipLast(1);

        return string.Join('.', new[] { projectNamespace }.Concat(folders));
    }

    private static string? SectionProblem(MethodDeclarationSyntax method)
    {
        var comments = (method.Body?.DescendantTrivia() ?? Enumerable.Empty<SyntaxTrivia>())
            .Where(trivia => trivia.IsKind(SyntaxKind.SingleLineCommentTrivia))
            .Select(trivia => trivia.ToString().TrimStart('/').Trim())
            .ToList();

        if (comments.Any(_arrangeAndActSection.IsMatch))
        {
            return "'// Arrange & Act' puts two sections in one comment: write '// Arrange' and '// Act'";
        }

        if (!comments.Any(_arrangeSection.IsMatch))
        {
            return "no // Arrange comment";
        }

        if (comments.Any(_actAndAssertSection.IsMatch))
        {
            return IsExceptionTest(method) ? null : "'// Act & Assert' is only for a test that expects an exception: write '// Act' and '// Assert'";
        }

        if (!comments.Any(_actSection.IsMatch))
        {
            return "no // Act comment";
        }

        return comments.Any(_assertSection.IsMatch) ? null : "no // Assert comment";
    }

    private static bool IsExceptionTest(MethodDeclarationSyntax method)
    {
        return method.DescendantNodes().OfType<SimpleNameSyntax>().Any(name => _exceptionAssertionNames.Contains(name.Identifier.Text));
    }

    /// <summary>
    /// Classes that record telemetry (the recorders, or a listener of their own) without joining the Telemetry collection. The activity sources
    /// and meters are process-wide, so two such classes running at once see each other's data and assert on it.
    /// </summary>
    public static IReadOnlyList<string> TelemetryTestsOutsideTheTelemetryCollection(string source)
    {
        var listenerTypes = new[] { "MetricsRecorder", "SpanRecorder", "MeterListener", "ActivityListener" };

        // By any mention of the name, not only a "new": a recorder can be held in a field or handed in by a helper.
        return Root(source).DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Where(type => type.DescendantNodes().OfType<IdentifierNameSyntax>().Any(name => listenerTypes.Contains(name.Identifier.Text)))
            .Where(type => !type.AttributeLists
                .SelectMany(list => list.Attributes)
                .Any(attribute => AttributeName(attribute) == "Collection"
                    && attribute.ArgumentList is { } arguments
                    && arguments.ToString().Contains("TelemetryCollection.Name", StringComparison.Ordinal)))
            .Select(type => Describe(type, type.Identifier.Text))
            .ToList();
    }

    /// <summary>Hand-made telemetry listeners in a test. The recorders in Infrastructure do it once, and correctly.</summary>
    public static IReadOnlyList<string> HandRolledTelemetryListeners(string source)
    {
        return Root(source).DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Where(creation => creation.Type.ToString() is "MeterListener" or "ActivityListener")
            .Select(creation => Describe(creation, creation.Type.ToString()))
            .ToList();
    }

    /// <summary>
    /// Zero-width, non-breaking space, soft hyphen and text-direction characters written into the source itself. They cannot be seen in an editor, and
    /// the direction ones (the "Trojan Source" characters) make code read in a different order from the one it runs in, so a test that needs one
    /// writes its escape sequence, where a reader can see what it is. A byte order mark at the very start of the file is allowed.
    /// </summary>
    public static IReadOnlyList<string> InvisibleCharacters(string source)
    {
        var findings = new List<string>();
        var lines = source.Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            var line = index == 0 ? lines[index].TrimStart((char)0xFEFF) : lines[index];

            findings.AddRange(line
                .Where(character => _invisibleCodePoints.Contains(character))
                .Distinct()
                .Select(character => $"line {index + 1}: U+{(int)character:X4}"));
        }

        return findings;
    }

    // ── Member order ─────────────────────────────────────────────────────────

    /// <summary>
    /// Members that come after a member of a later group. The order is fields and events, constructors, nested types, properties, then methods.
    /// Only the first member to break the order in each type is reported, so one misplaced member is one finding.
    /// </summary>
    public static IReadOnlyList<string> MembersOutOfOrder(string source)
    {
        var offenders = new List<string>();

        foreach (var type in Root(source).DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            var highestGroup = -1;
            var highestKind = string.Empty;

            foreach (var member in type.Members)
            {
                var group = GroupOf(member);

                if (group < 0)
                {
                    continue;
                }

                if (group < highestGroup)
                {
                    offenders.Add(Describe(member, $"{type.Identifier.Text}: {KindOf(member)} after {highestKind}"));
                    break;
                }

                if (group > highestGroup)
                {
                    highestGroup = group;
                    highestKind = KindOf(member);
                }
            }
        }

        return offenders;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static CompilationUnitSyntax Root(string source)
    {
        return CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
    }

    private static string Describe(SyntaxNode node, string what)
    {
        return $"line {node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: {what}";
    }

    private static IEnumerable<MethodDeclarationSyntax> TestMethods(string source)
    {
        return Root(source).DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(method => method.AttributeLists
                .SelectMany(list => list.Attributes)
                .Any(attribute => _testAttributeNames.Contains(AttributeName(attribute))));
    }

    private static string AttributeName(AttributeSyntax attribute)
    {
        var name = attribute.Name.ToString();
        var simple = name[(name.LastIndexOf('.') + 1)..];

        return simple.EndsWith("Attribute", StringComparison.Ordinal) ? simple[..^"Attribute".Length] : simple;
    }

    private static bool IsPrivate(SyntaxTokenList modifiers)
    {
        var hasAccessibility = modifiers.Any(modifier => modifier.Kind() is SyntaxKind.PublicKeyword or SyntaxKind.ProtectedKeyword or SyntaxKind.InternalKeyword);

        // "private protected" is protected. A member with no accessibility at all is private.
        return modifiers.Any(SyntaxKind.PrivateKeyword) ? !modifiers.Any(SyntaxKind.ProtectedKeyword) : !hasAccessibility;
    }

    private static bool IsBool(TypeSyntax type)
    {
        return type is PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.BoolKeyword };
    }

    private static bool HasQuestionPrefix(string name)
    {
        var bare = name.TrimStart('_');

        return new[] { "is", "has", "can" }.Any(prefix =>
            bare.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && (bare.Length == prefix.Length || char.IsUpper(bare[prefix.Length])));
    }

    private static bool IsObviouslyBoolean(ExpressionSyntax expression)
    {
        return expression switch
        {
            ParenthesizedExpressionSyntax parenthesized => IsObviouslyBoolean(parenthesized.Expression),
            PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalNotExpression } => true,
            BinaryExpressionSyntax binary => binary.Kind() is SyntaxKind.LogicalAndExpression or SyntaxKind.LogicalOrExpression
                or SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression or SyntaxKind.LessThanExpression
                or SyntaxKind.LessThanOrEqualExpression or SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression
                or SyntaxKind.IsExpression,
            IsPatternExpressionSyntax => true,
            LiteralExpressionSyntax literal => literal.Kind() is SyntaxKind.TrueLiteralExpression or SyntaxKind.FalseLiteralExpression,
            InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } => _boolReturningMethods.Contains(access.Name.Identifier.Text),
            _ => false
        };
    }

    private static bool IsBlockingInvocation(InvocationExpressionSyntax call)
    {
        // task?.Wait(): the call is made on a member binding instead of a member access.
        if (call.Expression is MemberBindingExpressionSyntax binding)
        {
            return binding.Name.Identifier.Text is "Wait" or "WaitAll" or "WaitAny";
        }

        if (call.Expression is not MemberAccessExpressionSyntax access)
        {
            return false;
        }

        var name = access.Name.Identifier.Text;

        // Any Wait blocks, with a timeout or without; WaitAsync is a different name and is not flagged.
        return name is "Wait" or "WaitAll" or "WaitAny"
            || (name == "GetResult" && access.Expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "GetAwaiter" } })
            || (name == "Sleep" && access.Expression is IdentifierNameSyntax { Identifier.Text: "Thread" });
    }

    private static bool IsControlFlow(SyntaxNode node)
    {
        return node is IfStatementSyntax or ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax
            or TryStatementSyntax or SwitchStatementSyntax;
    }

    private static int GroupOf(MemberDeclarationSyntax member)
    {
        return member switch
        {
            FieldDeclarationSyntax or EventFieldDeclarationSyntax => 0,
            ConstructorDeclarationSyntax => 1,
            BaseTypeDeclarationSyntax or DelegateDeclarationSyntax => 2,
            PropertyDeclarationSyntax or IndexerDeclarationSyntax or EventDeclarationSyntax => 3,
            MethodDeclarationSyntax or OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax or DestructorDeclarationSyntax => 4,
            _ => -1
        };
    }

    private static string KindOf(MemberDeclarationSyntax member)
    {
        return GroupOf(member) switch
        {
            0 => "a field",
            1 => "a constructor",
            2 => "a nested type",
            3 => "a property",
            _ => "a method"
        };
    }
}
