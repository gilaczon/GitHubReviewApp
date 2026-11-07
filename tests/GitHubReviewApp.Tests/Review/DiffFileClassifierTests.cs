namespace GitHubReviewApp.Tests.Review;

public class DiffFileClassifierTests
{
    private static string NewFilePatch(params string[] lines)
    {
        return $"@@ -0,0 +1,{lines.Length} @@\n" + string.Join("\n", lines.Select(line => "+" + line));
    }

    // ── GetSkipReason ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("package-lock.json")]
    [InlineData("web/app/package-lock.json")]
    [InlineData("yarn.lock")]
    [InlineData("pnpm-lock.yaml")]
    [InlineData("src/Api/packages.lock.json")]
    [InlineData("go.sum")]
    [InlineData("Cargo.lock")]
    [InlineData("poetry.lock")]
    [InlineData("Gemfile.lock")]
    [InlineData("composer.lock")]
    [InlineData("src/Api/obj/project.assets.json")]
    [InlineData("PACKAGE-LOCK.JSON")]
    [InlineData("npm-shrinkwrap.json")]
    [InlineData("bun.lockb")]
    [InlineData("paket.lock")]
    [InlineData("Pipfile.lock")]
    [InlineData("ios/Podfile.lock")]
    [InlineData("pubspec.lock")]
    [InlineData("mix.lock")]
    [InlineData("gradle.lockfile")]
    [InlineData("flake.lock")]
    public void GetSkipReason_GivenALockfilePath_Should_BeDependencyLockfile(string path)
    {
        // Arrange

        // Act
        var result = DiffFileClassifier.GetSkipReason(path);

        // Assert
        result.Should().Be(FileSkipReason.DependencyLockfile);
    }

    [Theory]
    [InlineData("assets/logo.png")]
    [InlineData("assets/Photo.JPG")]
    [InlineData("fonts/inter.woff2")]
    [InlineData("lib/Vendor.dll")]
    [InlineData("src/Api/bin/Debug/net10.0/Api.dll")]
    [InlineData("release/app.zip")]
    [InlineData("docs/spec.pdf")]
    [InlineData("wwwroot/js/app.min.js")]
    [InlineData("site.min.css")]
    [InlineData("dist/bundle.js.map")]
    public void GetSkipReason_GivenABinaryOrMinifiedPath_Should_BeBinaryOrMinified(string path)
    {
        // Arrange

        // Act
        var result = DiffFileClassifier.GetSkipReason(path);

        // Assert
        result.Should().Be(FileSkipReason.BinaryOrMinified);
    }

    [Theory]
    [InlineData("src/Program.cs")]
    [InlineData("README.md")]
    [InlineData("package.json")]
    [InlineData("Dockerfile")]
    [InlineData("src/Designer.cs")]               // a file *named* Designer.cs, not a *.Designer.cs partial
    [InlineData("src/GeneratedCode/Helper.cs")]   // directory name alone does not make a file generated
    [InlineData("src/Binary/Reader.cs")]          // "bin" must match a whole path segment
    [InlineData("src/lockstep.cs")]
    [InlineData("Data/Migrations/20240101120000_Init.Designer.cs")]   // generated code is demoted, not skipped
    [InlineData("Views/Home.g.cs")]
    [InlineData("node_modules/lodash/index.js")]
    [InlineData("src/Payments/vendor/Refund.cs")]
    [InlineData("web/dist/index.js")]
    public void GetSkipReason_GivenAReadableTextFile_Should_BeNull(string path)
    {
        // Arrange

        // Act
        var result = DiffFileClassifier.GetSkipReason(path);

        // Assert
        result.Should().BeNull();
    }

    [Theory]
    [InlineData("lib/Vendor.dll", true)]
    [InlineData("tools/run.exe", true)]
    [InlineData("libs/app.jar", true)]
    [InlineData("native/libfoo.so", true)]
    [InlineData("native/libfoo.dylib", true)]
    [InlineData("out/Pkg.1.0.0.nupkg", true)]
    [InlineData("release/app.zip", true)]
    [InlineData("release/app.tar", true)]
    [InlineData("assets/logo.png", false)]
    [InlineData("docs/spec.pdf", false)]
    [InlineData("fonts/inter.woff2", false)]
    [InlineData("src/Program.cs", false)]
    public void IsExecutableOrArchive_GivenAPath_Should_BeTrueOnlyForBinariesThatCanRunOrCarryFiles(string path, bool isExpected)
    {
        // Arrange

        // Act
        var result = DiffFileClassifier.IsExecutableOrArchive(path);

        // Assert
        result.Should().Be(isExpected);
    }

    // ── GetPriority ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("src/Api/Program.cs")]
    [InlineData("web/src/App.vue")]
    [InlineData("web/src/index.ts")]
    [InlineData("scripts/deploy.ps1")]
    [InlineData("db/migrations/001_add_table.sql")]
    [InlineData("infra/main.bicep")]
    [InlineData("infra/main.tf")]
    [InlineData("Dockerfile")]
    [InlineData("src/Api/Dockerfile.prod")]
    [InlineData(".github/workflows/ci.yml")]
    public void GetPriority_GivenSourceOrPipelineFile_Should_BeSource(string path)
    {
        // Arrange

        // Act
        var result = DiffFileClassifier.GetPriority(path);

        // Assert
        result.Should().Be(FilePriority.Source);
    }

    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("src/Api/Api.csproj")]
    [InlineData("Directory.Packages.props")]
    [InlineData("config/app.yaml")]
    [InlineData("docker-compose.yml")]
    [InlineData("pyproject.toml")]
    public void GetPriority_GivenConfigurationFile_Should_BeConfiguration(string path)
    {
        // Arrange

        // Act
        var result = DiffFileClassifier.GetPriority(path);

        // Assert
        result.Should().Be(FilePriority.Configuration);
    }

    [Theory]
    [InlineData("tests/Api.Tests/OrderServiceTests.cs")]
    [InlineData("src/Api.Tests/Helpers.cs")]
    [InlineData("src/Api/OrderServiceTests.cs")]
    [InlineData("web/__tests__/cart.ts")]
    [InlineData("web/src/cart.spec.ts")]
    [InlineData("web/src/cart.test.js")]
    [InlineData("svc/test_orders.py")]
    [InlineData("svc/orders_test.go")]
    [InlineData("tests/fixtures/payload.json")]
    [InlineData("e2e/login.ts")]
    public void GetPriority_GivenTestFile_Should_BeTests(string path)
    {
        // Arrange

        // Act
        var result = DiffFileClassifier.GetPriority(path);

        // Assert
        result.Should().Be(FilePriority.Tests);
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("docs/design.md")]
    [InlineData("tests/README.md")]
    [InlineData("notes.txt")]
    [InlineData("web/src/site.css")]
    [InlineData("web/src/site.scss")]
    [InlineData("LICENSE")]
    [InlineData("src/Api/Latest.cs.bak")]
    public void GetPriority_GivenDocumentationStyleOrUnknownFile_Should_BeOther(string path)
    {
        // Arrange

        // Act
        var result = DiffFileClassifier.GetPriority(path);

        // Assert
        result.Should().Be(FilePriority.Other);
    }

    [Fact]
    public void GetPriority_GivenSourceFileWhoseNameMerelyEndsInTest_Should_NotBeTreatedAsATest()
    {
        // Arrange — "Latest.cs" ends in "test.cs" only if case is ignored

        // Act
        var result = DiffFileClassifier.GetPriority("src/Api/Latest.cs");

        // Assert
        result.Should().Be(FilePriority.Source);
    }

    [Theory]
    [InlineData("Data/Migrations/20240101120000_Init.Designer.cs")]
    [InlineData("Data/Migrations/AppDbContextModelSnapshot.cs")]
    [InlineData("Views/Home.g.cs")]
    [InlineData("Views/Home.g.i.cs")]
    [InlineData("Client/Api.generated.cs")]
    [InlineData("web/src/api.generated.ts")]
    [InlineData("proto/user.pb.go")]
    [InlineData("proto/schema_pb2.py")]
    [InlineData("web/__snapshots__/button.test.tsx.snap")]
    [InlineData("node_modules/lodash/index.js")]
    [InlineData("src/web/node_modules/x/y.js")]
    [InlineData("vendor/lib/thing.php")]
    [InlineData("src/Payments/vendor/Refund.cs")]
    [InlineData("src/Api/obj/Generated.cs")]
    [InlineData("dist/main.js")]
    [InlineData(".vs/config/applicationhost.config")]
    [InlineData("wwwroot/lib/bootstrap/bootstrap.js")]
    public void GetPriority_GivenAGeneratedOrVendoredPath_Should_BeTheLowestPriority(string path)
    {
        // Arrange

        // Act
        var result = DiffFileClassifier.GetPriority(path);

        // Assert
        result.Should().Be(FilePriority.GeneratedOrVendored);
    }

    [Theory]
    [InlineData("// <auto-generated>")]
    [InlineData("// <auto-generated />")]
    [InlineData("//     <auto-generated>")]
    [InlineData("-- <auto-generated>")]
    [InlineData("// @generated")]
    [InlineData("# @generated SignedSource<<abc>>")]
    [InlineData("/* @generated */")]
    [InlineData(" * @generated")]
    [InlineData("// Code generated by protoc-gen-go. DO NOT EDIT.")]
    public void GetPriority_GivenACanonicalGeneratorHeaderInANewFile_Should_BeTheLowestPriority(string header)
    {
        // Arrange
        var patch = NewFilePatch("//----------", header, "namespace Foo;", "class Bar { }");

        // Act
        var result = DiffFileClassifier.GetPriority("src/Client.cs", "added", patch);

        // Assert
        result.Should().Be(FilePriority.GeneratedOrVendored);
    }

    [Fact]
    public void GetPriority_GivenACanonicalHeaderInAWindowsLineEndingPatch_Should_BeTheLowestPriority()
    {
        // Arrange
        var patch = NewFilePatch("// <auto-generated>", "class Bar { }").Replace("\n", "\r\n");

        // Act
        var result = DiffFileClassifier.GetPriority("src/Client.cs", "added", patch);

        // Assert
        result.Should().Be(FilePriority.GeneratedOrVendored);
    }

    [Theory]
    [InlineData("// NOTE: do not edit the expiry check without sign-off")]
    [InlineData("/* DO NOT EDIT - this file was generated */")]
    [InlineData("// this file was auto-generated by hand")]
    [InlineData("// code generated here, edit freely")]
    [InlineData("// Code generated by hand. Safe to edit.")]
    [InlineData("var note = \"<auto-generated>\";")]
    [InlineData("// see also @generated files")]
    public void GetPriority_GivenALooseOrNonCanonicalPhraseInANewFile_Should_NotDemoteIt(string line)
    {
        // Arrange — any author can write these words in any comment, so they decide nothing
        var patch = NewFilePatch(line, "namespace Foo;", "class Bar { }");

        // Act
        var result = DiffFileClassifier.GetPriority("src/Client.cs", "added", patch);

        // Assert
        result.Should().Be(FilePriority.Source);
    }

    [Theory]
    [InlineData("modified")]
    [InlineData("renamed")]
    [InlineData("changed")]
    public void GetPriority_GivenACanonicalHeaderInAFileThatIsNotNew_Should_NotDemoteIt(string status)
    {
        // Arrange — the first lines of the patch of a changed file are what the author touched, not the top of the file
        var patch = NewFilePatch("// <auto-generated>", "namespace Foo;");

        // Act
        var result = DiffFileClassifier.GetPriority("src/Auth/Validator.cs", status, patch);

        // Assert
        result.Should().Be(FilePriority.Source);
    }

    [Fact]
    public void GetPriority_GivenACanonicalHeaderBeyondTheFirstFiveAddedLines_Should_NotDemoteIt()
    {
        // Arrange
        var patch = NewFilePatch("using A;", "using B;", "using C;", "using D;", "using E;", "// <auto-generated>", "class Bar { }");

        // Act
        var result = DiffFileClassifier.GetPriority("src/Client.cs", "added", patch);

        // Assert
        result.Should().Be(FilePriority.Source);
    }

    [Fact]
    public void GetPriority_GivenAFirstHunkThatDoesNotStartAtLineOne_Should_NotDemoteIt()
    {
        // Arrange
        var patch = "@@ -0,0 +40,2 @@\n+// <auto-generated>\n+class Bar { }";

        // Act
        var result = DiffFileClassifier.GetPriority("src/Client.cs", "added", patch);

        // Assert
        result.Should().Be(FilePriority.Source);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void GetPriority_GivenANewFileWithNoPatch_Should_FollowItsPath(string? patch)
    {
        // Arrange

        // Act
        var result = DiffFileClassifier.GetPriority("src/Client.cs", "added", patch);

        // Assert
        result.Should().Be(FilePriority.Source);
    }
}
