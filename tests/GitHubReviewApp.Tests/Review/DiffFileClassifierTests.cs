namespace GitHubReviewApp.Tests.Review;

public class DiffFileClassifierTests
{
    private static string NewFilePatch(params string[] lines)
    {
        return $"@@ -0,0 +1,{lines.Length} @@\n" + string.Join("\n", lines.Select(line => "+" + line));
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

    // ── GetRiskTier: what cannot be read, generated code and tests ───────────

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
    public void GetRiskTier_GivenALockfileBinaryOrMinifiedFile_Should_BeNotReadable(string path)
    {
        // Arrange
        var file = new PullRequestFile(path, "modified", 1, 0, null);

        // Act
        var tier = DiffFileClassifier.GetRiskTier(file);

        // Assert
        tier.Should().Be(RiskTier.NotReadable);
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
    [InlineData("Data/Migrations/20240101120000_Init.Designer.cs")]   // generated code is demoted, not left out
    [InlineData("node_modules/lodash/index.js")]
    [InlineData("web/dist/index.js")]
    public void GetRiskTier_GivenATextFile_Should_NotBeNotReadable(string path)
    {
        // Arrange
        var file = new PullRequestFile(path, "modified", 1, 0, null);

        // Act
        var tier = DiffFileClassifier.GetRiskTier(file);

        // Assert
        tier.Should().NotBe(RiskTier.NotReadable);
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
    public void GetRiskTier_GivenAGeneratedOrVendoredPath_Should_BeGeneratedOrVendored(string path)
    {
        // Arrange
        var file = new PullRequestFile(path, "modified", 1, 0, null);

        // Act
        var tier = DiffFileClassifier.GetRiskTier(file);

        // Assert
        tier.Should().Be(RiskTier.GeneratedOrVendored);
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
    public void GetRiskTier_GivenATestFile_Should_BeTests(string path)
    {
        // Arrange
        var file = new PullRequestFile(path, "modified", 1, 0, null);

        // Act
        var tier = DiffFileClassifier.GetRiskTier(file);

        // Assert
        tier.Should().Be(RiskTier.Tests);
    }

    [Fact]
    public void GetRiskTier_GivenASourceFileWhoseNameMerelyEndsInTest_Should_NotBeTreatedAsATest()
    {
        // Arrange — "Latest.cs" ends in "test.cs" only if case is ignored
        var file = new PullRequestFile("src/Api/Latest.cs", "modified", 1, 0, null);

        // Act
        var tier = DiffFileClassifier.GetRiskTier(file);

        // Assert
        tier.Should().Be(RiskTier.Source);
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
    public void GetRiskTier_GivenACanonicalGeneratorHeaderInANewFile_Should_BeGeneratedOrVendored(string header)
    {
        // Arrange
        var file = new PullRequestFile("src/Client.cs", "added", 4, 0, NewFilePatch("//----------", header, "namespace Foo;", "class Bar { }"));

        // Act
        var tier = DiffFileClassifier.GetRiskTier(file);

        // Assert
        tier.Should().Be(RiskTier.GeneratedOrVendored);
    }

    [Fact]
    public void GetRiskTier_GivenACanonicalHeaderInAWindowsLineEndingPatch_Should_BeGeneratedOrVendored()
    {
        // Arrange
        var file = new PullRequestFile("src/Client.cs", "added", 2, 0, NewFilePatch("// <auto-generated>", "class Bar { }").Replace("\n", "\r\n"));

        // Act
        var tier = DiffFileClassifier.GetRiskTier(file);

        // Assert
        tier.Should().Be(RiskTier.GeneratedOrVendored);
    }

    [Theory]
    [InlineData("// NOTE: do not edit the expiry check without sign-off")]
    [InlineData("/* DO NOT EDIT - this file was generated */")]
    [InlineData("// this file was auto-generated by hand")]
    [InlineData("// code generated here, edit freely")]
    [InlineData("// Code generated by hand. Safe to edit.")]
    [InlineData("var note = \"<auto-generated>\";")]
    [InlineData("// see also @generated files")]
    public void GetRiskTier_GivenALooseOrNonCanonicalPhraseInANewFile_Should_NotDemoteIt(string line)
    {
        // Arrange — any author can write these words in any comment, so they decide nothing
        var file = new PullRequestFile("src/Client.cs", "added", 3, 0, NewFilePatch(line, "namespace Foo;", "class Bar { }"));

        // Act
        var tier = DiffFileClassifier.GetRiskTier(file);

        // Assert
        tier.Should().Be(RiskTier.Source);
    }

    [Theory]
    [InlineData("modified")]
    [InlineData("renamed")]
    [InlineData("changed")]
    public void GetRiskTier_GivenACanonicalHeaderInAFileThatIsNotNew_Should_NotDemoteIt(string status)
    {
        // Arrange — the first lines of the patch of a changed file are what the author touched, not the top of the file
        var file = new PullRequestFile("src/Auth/Validator.cs", status, 2, 0, NewFilePatch("// <auto-generated>", "namespace Foo;"));

        // Act
        var tier = DiffFileClassifier.GetRiskTier(file);

        // Assert
        tier.Should().Be(RiskTier.Auth);
    }

    [Fact]
    public void GetRiskTier_GivenACanonicalHeaderBeyondTheFirstFiveAddedLines_Should_NotDemoteIt()
    {
        // Arrange
        var patch = NewFilePatch("using A;", "using B;", "using C;", "using D;", "using E;", "// <auto-generated>", "class Bar { }");
        var file = new PullRequestFile("src/Client.cs", "added", 7, 0, patch);

        // Act
        var tier = DiffFileClassifier.GetRiskTier(file);

        // Assert
        tier.Should().Be(RiskTier.Source);
    }

    [Fact]
    public void GetRiskTier_GivenAFirstHunkThatDoesNotStartAtLineOne_Should_NotDemoteIt()
    {
        // Arrange
        var file = new PullRequestFile("src/Client.cs", "added", 2, 0, "@@ -0,0 +40,2 @@\n+// <auto-generated>\n+class Bar { }");

        // Act
        var tier = DiffFileClassifier.GetRiskTier(file);

        // Assert
        tier.Should().Be(RiskTier.Source);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void GetRiskTier_GivenANewFileWithNoPatch_Should_FollowItsPath(string? patch)
    {
        // Arrange
        var file = new PullRequestFile("src/Client.cs", "added", 0, 0, patch);

        // Act
        var tier = DiffFileClassifier.GetRiskTier(file);

        // Assert
        tier.Should().Be(RiskTier.Source);
    }

    // ── GetRiskTier: every tier ──────────────────────────────────────────────

    [Theory]
    [InlineData("package-lock.json", RiskTier.NotReadable)]
    [InlineData("img/logo.png", RiskTier.NotReadable)]
    [InlineData("dist/app.min.js", RiskTier.NotReadable)]
    [InlineData("Migrations/20240101_Init.Designer.cs", RiskTier.GeneratedOrVendored)]
    [InlineData("tests/Auth/LoginTests.cs", RiskTier.Tests)]
    [InlineData("docs/payments.md", RiskTier.Docs)]
    [InlineData("README.md", RiskTier.Docs)]
    [InlineData(".github/workflows/ci.yml", RiskTier.Pipelines)]
    [InlineData("infra/main.bicep", RiskTier.Pipelines)]
    [InlineData("deploy/app.tf", RiskTier.Pipelines)]
    [InlineData("src/appsettings.Production.json", RiskTier.Config)]
    [InlineData("src/App.csproj", RiskTier.Config)]
    [InlineData("src/Auth/TokenService.cs", RiskTier.Auth)]
    [InlineData("src/Payments/appsettings.json", RiskTier.Payments)]
    [InlineData("src/Util/Strings.cs", RiskTier.Source)]
    // Each pipeline rule on its own: the existing file rules, the infrastructure extensions and the pipeline folders
    [InlineData("Dockerfile", RiskTier.Pipelines)]
    [InlineData("src/Api/Dockerfile.prod", RiskTier.Pipelines)]
    [InlineData("build/api.dockerfile", RiskTier.Pipelines)]
    [InlineData("azure-pipelines.yml", RiskTier.Pipelines)]
    [InlineData(".gitlab-ci.yml", RiskTier.Pipelines)]
    [InlineData("Jenkinsfile", RiskTier.Pipelines)]
    [InlineData("modules/network.bicep", RiskTier.Pipelines)]
    [InlineData("env/prod.bicepparam", RiskTier.Pipelines)]
    [InlineData("modules/network.tf", RiskTier.Pipelines)]
    [InlineData("env/prod.tfvars", RiskTier.Pipelines)]
    [InlineData("pipelines/build.yml", RiskTier.Pipelines)]
    [InlineData(".azuredevops/policies.yml", RiskTier.Pipelines)]
    [InlineData("infra/setup.sh", RiskTier.Pipelines)]
    [InlineData("deploy/run.ps1", RiskTier.Pipelines)]
    [InlineData("deployment/release.ps1", RiskTier.Pipelines)]
    [InlineData("helm/values.yaml", RiskTier.Pipelines)]
    [InlineData("charts/api/templates/service.yaml", RiskTier.Pipelines)]
    [InlineData("k8s/service.yaml", RiskTier.Pipelines)]
    [InlineData("kubernetes/ingress.yaml", RiskTier.Pipelines)]
    // Application code in a folder named like a pipeline folder is the application's; scripts and everything else there stay pipeline files
    [InlineData("src/components/charts/LineChart.vue", RiskTier.Source)]
    [InlineData("src/Api/Infra/AppDbContext.cs", RiskTier.Source)]
    [InlineData("src/Application/Pipelines/LoggingBehavior.cs", RiskTier.Source)]
    [InlineData("charts/app/templates/deployment.yaml", RiskTier.Pipelines)]
    [InlineData("charts/app/templates/_helpers.tpl", RiskTier.Pipelines)]
    [InlineData("infra/main.json", RiskTier.Pipelines)]
    [InlineData("deploy/run-migrations.ps1", RiskTier.Pipelines)]
    [InlineData("pipelines/templates/build.yml", RiskTier.Pipelines)]
    [InlineData("deploy/publish_release.py", RiskTier.Pipelines)]
    [InlineData("deployment/migrations/V12__add_index.sql", RiskTier.Pipelines)]
    // Each configuration rule on its own
    [InlineData(".env", RiskTier.Config)]
    [InlineData(".env.local", RiskTier.Config)]
    [InlineData("src/web.config", RiskTier.Config)]
    [InlineData("global.json", RiskTier.Config)]
    [InlineData("Directory.Build.props", RiskTier.Config)]
    [InlineData("docker-compose.yml", RiskTier.Config)]
    // Each documentation rule on its own
    [InlineData("notes.txt", RiskTier.Docs)]
    [InlineData("guide.rst", RiskTier.Docs)]
    [InlineData("manual.adoc", RiskTier.Docs)]
    [InlineData("page.mdx", RiskTier.Docs)]
    [InlineData("intro.markdown", RiskTier.Docs)]
    [InlineData("doc/guide.yaml", RiskTier.Docs)]
    [InlineData("Docs/api/openapi.yaml", RiskTier.Docs)]
    // A licence file is documentation, not personal data, though "license" is a personal-data keyword
    [InlineData("LICENSE", RiskTier.Docs)]
    [InlineData("licence", RiskTier.Docs)]
    [InlineData("COPYING", RiskTier.Docs)]
    [InlineData("COPYING.LESSER", RiskTier.Docs)]
    [InlineData("NOTICE", RiskTier.Docs)]
    [InlineData("LICENSE-MIT", RiskTier.Docs)]
    [InlineData("src/Lib/LICENSE", RiskTier.Docs)]
    [InlineData("LICENSES", RiskTier.Docs)]
    [InlineData("THIRD_PARTY_LICENSES", RiskTier.Docs)]
    [InlineData("THIRD-PARTY-NOTICES", RiskTier.Docs)]
    [InlineData("ThirdPartyNotices", RiskTier.Docs)]
    [InlineData("src/Verification/DriverLicenseValidator.cs", RiskTier.PersonalData)]
    [InlineData("src/Views/DriverLicenses.html", RiskTier.PersonalData)]
    // A source-code or configuration file named like a licence file is the application's own, classed as it would be anywhere else
    [InlineData("src/Models/License.cs", RiskTier.PersonalData)]
    [InlineData("src/Notices/Notice.vue", RiskTier.Source)]
    [InlineData("license.json", RiskTier.PersonalData)]
    // First match wins
    [InlineData("docs/logo.png", RiskTier.NotReadable)]
    [InlineData("node_modules/stripe/index.js", RiskTier.GeneratedOrVendored)]
    [InlineData("src/Auth/Auth.g.cs", RiskTier.GeneratedOrVendored)]
    [InlineData("tests/README.md", RiskTier.Tests)]
    [InlineData(".github/workflows/deploy-payments.yml", RiskTier.Payments)]
    [InlineData("web/src/site.css", RiskTier.Source)]
    [InlineData("Makefile", RiskTier.Source)]
    public void GetRiskTier_GivenAPath_Should_ReturnItsTier(string path, RiskTier expectedTier)
    {
        // Arrange
        var file = new PullRequestFile(path, "modified", 1, 0, null);

        // Act
        var tier = DiffFileClassifier.GetRiskTier(file);

        // Assert
        tier.Should().Be(expectedTier);
    }

    [Fact]
    public void GetRiskTier_GivenANewFileWithAGeneratorHeader_Should_BeGeneratedOrVendoredWhateverItsPathSays()
    {
        // Arrange — the path alone is an auth file; the canonical header of a new file says a tool wrote it
        var file = new PullRequestFile("src/Auth/Client.cs", "added", 2, 0, NewFilePatch("// <auto-generated>", "class Client { }"));

        // Act
        var tier = DiffFileClassifier.GetRiskTier(file);

        // Assert
        tier.Should().Be(RiskTier.GeneratedOrVendored);
    }

    // ── IsLockfile and IsDocumentation ───────────────────────────────────────

    [Theory]
    [InlineData("package-lock.json", true)]
    [InlineData("web/app/yarn.lock", true)]
    [InlineData("src/Api/packages.lock.json", true)]
    [InlineData("PACKAGE-LOCK.JSON", true)]
    [InlineData("package.json", false)]
    [InlineData("src/lockstep.cs", false)]
    [InlineData("assets/logo.png", false)]
    public void IsLockfile_GivenAPath_Should_BeTrueOnlyForADependencyLockfile(string path, bool isExpected)
    {
        // Arrange

        // Act
        var result = DiffFileClassifier.IsLockfile(path);

        // Assert
        result.Should().Be(isExpected);
    }

    [Theory]
    [InlineData("README.md", true)]
    [InlineData("guide.adoc", true)]
    [InlineData("docs/setup.yaml", true)]
    [InlineData("src/doc/Notes.cs", true)]
    [InlineData("LICENSE", true)]
    [InlineData("src/Models/License.cs", false)]       // a source file named like a licence file is code
    [InlineData("src/Program.cs", false)]
    [InlineData("docs", false)]                        // a file named docs is not a docs folder
    [InlineData("src/documents/Upload.cs", false)]     // the folder name must be docs or doc, whole
    public void IsDocumentation_GivenAPath_Should_BeTrueForADocumentationFileOrAnythingInADocsFolder(string path, bool isExpected)
    {
        // Arrange

        // Act
        var result = DiffFileClassifier.IsDocumentation(path);

        // Assert
        result.Should().Be(isExpected);
    }
}
