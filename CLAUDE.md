# CLAUDE.md

## BMAD Framework

BMAD is installed at: `C:\Users\RogelioAczon\_bmad`

When resolving BMAD task files, agents, or templates, use `C:\Users\RogelioAczon\_bmad` as `{project-root}/_bmad`.

## CRITICAL — Common Mistakes to Avoid

These rules are frequently violated. ALWAYS check your code against them before finishing:

1. **NEVER put `using` statements in source files.** ALL `using` directives go in `GlobalUsings.cs`. Source `.cs` files must have ZERO `using` lines at the top. This is non-negotiable.
2. **ALL private fields use `_camelCase`** — including `private static readonly` fields. The `PascalCase` rule for statics only applies to `const` fields. Example: `private static readonly ActivitySource _activitySource` NOT `ActivitySource`.
3. **Test method naming is exactly `MethodName_GivenContext_Should_ExpectedOutcome`** — four parts separated by underscores. Always include `Given` and `Should`.
4. **Every production code change MUST be covered by unit tests.** Adding or modifying behavior in a source file requires corresponding tests in the test project before the task is complete. Do not mark a task done until the new/changed behavior has test coverage.
5. **Before reporting any task as done, run a full solution build and all unit tests — both must pass.** Use `dotnet build` on the solution and `dotnet test` on the test project. Do not report success if either produces errors or failing tests.

## MoneyMe Naming Standards & Conventions

Source: https://moneyme1.atlassian.net/wiki/spaces/TECHNOLOGY/pages/1638334491

### Naming Convention Table

| Identifier | Convention | Notes |
|---|---|---|
| Project File | PascalCase | Match assembly name with root namespace |
| Source File | PascalCase | Match class name and file name. One class/enum/struct/record per file |
| Namespace | PascalCase | Use `Company.Product.Layer` (e.g., `MoneyMe.Horizon3.Core`) |
| Class, Struct, Record | PascalCase | Use nouns or noun phrases. Add suffix when subclassing (`HomeController : Controller`) |
| Interface | PascalCase | Always prefix with capital `I` |
| Generic Type Params | Single capital letter | `T`, `K`, etc. |
| Method | PascalCase | Use verb or verb-object pair |
| Property | PascalCase | Name should represent the entity it returns. Never prefix with Get/Set |
| Field (non-private) | PascalCase | Avoid non-private fields; use properties instead |
| Field (private, including static readonly) | _camelCase | Prefix with single underscore. NEVER use PascalCase for `private static readonly` — only `const` fields get PascalCase |
| Constant (`const`) | PascalCase | Only `const` fields use PascalCase |
| Enum | PascalCase | Both the type name and option values |
| Variable (inline) | camelCase | Avoid single chars (except loop vars). Never enumerate (name1, name2) |
| Parameter | camelCase | |

### Key Rules

- **Be verbose** — prefer descriptive names over abbreviations. `customerId` not `custId`, `address` not `addr`.
- **Abbreviations/acronyms (2+ letters)** — use PascalCase in names (`HtmlParser`, `XmlDocument`, `DbContext`), camelCase in variables/params (`_htmlText`, `xmlText`).
- **Booleans** — prefix with `Can`, `Is`, or `Has` (`isActive`, `hasAccess`, `canEdit`).
- **Aggregations** — append qualifiers: `Average`, `Count`, `Total`, `Min`, `Max` (`customerCount`, `totalAmount`).
- **No Hungarian notation** — never prefix with type indicators (`iCounter`, `szName`, `bIsActive`).
- **No numeric suffixes** — use descriptive names instead (`homeTeamScore` not `score1`).
- **No redundant type suffixes** — avoid `CustomerClass`, `EnumFeeTypes`, `AccessTokenRecord`.
- **No parent class name in properties** — use `Customer.Name` not `Customer.CustomerName` (unless disambiguating in compound classes).
- **Never start names with numbers** — `ForecastFor7Days` not `7DayForecast`.
- **Never use C# reserved words as names**.
- **Avoid conflicts** with .NET framework namespaces or types.
- **Names describe purpose, not type** — `age` not `intValue`, `customerNames` not `stringList`.

## C# Code Style

- **File-scoped namespaces** — always use `namespace X;` (with semicolon), never block-scoped `namespace X { }`. Enforced in `.editorconfig` as `csharp_style_namespace_declarations = file_scoped:error`.

## Global Usings Convention

**CRITICAL: NEVER write `using` statements at the top of source files. ALL usings go in `GlobalUsings.cs`.**

Every project MUST have a `GlobalUsings.cs` file containing ALL `using` directives used by the project. Individual `.cs` files must have ZERO `using` statements — no exceptions.

- Place `GlobalUsings.cs` at the project root (next to `.csproj`)
- ALL non-implicit `using` directives go in `GlobalUsings.cs` as `global using` — no per-file `using` statements allowed
- When you need a new namespace, add it to `GlobalUsings.cs` — NEVER add `using` to the source file
- `<ImplicitUsings>enable</ImplicitUsings>` is set in `Directory.Build.props` (covers `System`, `System.Collections.Generic`, `System.Linq`, `System.Threading.Tasks`, etc.)
- When creating a new project, create its `GlobalUsings.cs` with all namespaces needed by that project
- If a `global using` causes ambiguity (e.g., `IResult`), resolve with namespace-qualified references in the source file — do NOT fall back to per-file `using`

## Unit Testing Conventions

Frameworks: **xUnit** + **FluentAssertions** + **Moq**

### Test Method Naming

**Pattern:** `MethodName_GivenContext_Should_ExpectedOutcome`

```csharp
// Good
SendAsync_GivenNoHandlerRegistered_Should_ThrowInvalidOperationException()
Success_GivenValidValue_Should_SetIsSuccessTrue()
PublishAsync_GivenNoHandlersRegistered_Should_NotThrow()

// Bad — don't describe implementation, describe behavior
SendAsync_ResolvesAndInvokesCommandHandler()
ValidationException_Returns422()
```

### Test Class & File Naming

- One test class per SUT class, named `{SutClassName}Tests`
- File name matches class name
- Namespace mirrors source, appends `.Tests` (e.g., `MoneyMe.Horizon3.Core.Tests.CQRS`)
- Test-only collaborators (fakes, stubs) live in the same folder as their tests

### Test Structure (AAA)

**Mandatory `// Arrange`, `// Act`, `// Assert` comments on every test.**

```csharp
[Fact]
public async Task SendAsync_GivenValidCommand_Should_ReturnSuccessResult()
{
    // Arrange
    var command = new TestCommand("hello");

    // Act
    var result = await _sut.SendAsync(command);

    // Assert
    result.IsSuccess.Should().BeTrue();
    result.Value.Should().Be("Handled: hello");
}
```

Use `// Act & Assert` combined only for exception tests.

### Assertion Rules

- **Always use FluentAssertions** — never `Assert.*` from xUnit
- Use specific assertions (`.Should().BeNull()`, `.Should().HaveCount()`) not `.Should().BeTrue()` on compound expressions
- Exception messages: use wildcard `*fragments*`, never full string match
- One logical concept per test; multiple `.Should()` calls are fine when describing a single outcome

### Mocking Rules

- **Moq only** — use `Mock<T>` for creating mocks, `_camelCase` for private mock fields
- `It.IsAny<T>()` for arguments you don't care about; `It.Is<T>(predicate)` only when the value is the behavior under test
- Use `.Setup()` for configuring behavior, `.Verify()` for asserting calls
- Use real `IServiceCollection` + `BuildServiceProvider()` for pipeline/DI wiring tests
- Write real in-memory fakes when the fake needs logic; prefer over deeply configured mocks

| Use real | Use `new Mock<T>()` |
|---|---|
| Value objects, records, DTOs | I/O boundaries (DB, HTTP, queue, file system) |
| `Result<T>`, `Error` | Interfaces with side effects (email, audit) |
| DI container for wiring tests | External services, non-deterministic deps |

### Test Data

- Use explicit, named values — `const string validEmail = "user@example.com"` not `"asdf"`
- No Bogus/random data in unit tests — use only in integration/load tests
- Use builder methods for complex objects, override only what matters per test

### Anti-Patterns — Do NOT

- Test implementation details — test observable outputs and side effects only
- Write logic (`if`, `for`, `try/catch`) in tests — use `[Theory]` with `[InlineData]` instead
- Use `Thread.Sleep`, `Task.Delay`, or `DateTime.Now` — inject `TimeProvider`
- Use `.Result` or `.Wait()` on async — always `await`
- Leave dead test code (`// TODO`, commented assertions, `Assert.True(true)`)
- Assert on exception messages from libraries you don't own

### Quick Reference

| Concern | Rule |
|---|---|
| Method naming | `MethodName_GivenContext_Should_ExpectedOutcome` |
| Class naming | `{SutClass}Tests` |
| Namespace | mirrors source, appends `.Tests` |
| AAA comments | mandatory on every test |
| Assertions | FluentAssertions only |
| Mocks | Moq only |
| Test data | explicit named values, no random |
| Async | always `await`, never `.Result`/`.Wait()` |
| Exception messages | wildcard `*fragments*` |
