using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;
using Xunit;

namespace Mahjong.Architecture.Tests
{
    /// <summary>
    /// Accepted V5 I-5 / S-1 guard: exact project-declaration allowlist for the nine
    /// real src projects plus an assembly-reference allowlist for the assemblies that
    /// were actually emitted by those projects.
    ///
    /// The two graphs are intentionally different:
    ///   * project declarations are an EXACT allow/deny set (every declared edge must
    ///     be allowed, every allowed edge must be declared);
    ///   * compiled assemblies are checked against the transitive closure of the
    ///     allowed project edges, but only the edges the code actually needs are
    ///     required to be present. The compiler is free not to emit a declared but
    ///     unused reference (LocalHost currently has no source and LobbyService does
    ///     not use Contracts types, so those two declared edges are not in their DLLs).
    ///
    /// BCL and shared-framework references are classified, not rejected wholesale.
    /// Third-party references are always module-specific: only Wire/Client may emit
    /// Google.Protobuf; only LobbyService and Infrastructure may emit Npgsql.
    /// </summary>
    public sealed class DependencyDirectionTests
    {
        private static readonly string[] ExpectedProjectNames =
        {
            "Mahjong.Ai",
            "Mahjong.Client",
            "Mahjong.Contracts",
            "Mahjong.Contracts.Wire",
            "Mahjong.Domain",
            "Mahjong.Infrastructure",
            "Mahjong.LobbyService",
            "Mahjong.LocalHost",
            "Mahjong.TableRuntime"
        };

        private static readonly string[] NoStrings = Array.Empty<string>();

        [Fact]
        public void RepositoryHasExactlyNineProductionProjectsAndRules()
        {
            string root = FindRepositoryRoot();
            IReadOnlyList<ProjectGraph> projects = DiscoverProductionProjects(root);

            Assert.True(projects.Count == 9, "Expected exactly 9 production projects under dotnet/src, found " + projects.Count + ".");
            Assert.Equal(Sorted(ExpectedProjectNames), Sorted(projects.Select(project => project.Name)));

            IReadOnlyDictionary<string, ModuleRule> rules = BuildRules();
            Assert.True(rules.Count == 9, "Expected exactly 9 module rules, found " + rules.Count + ".");
            Assert.Equal(Sorted(ExpectedProjectNames), Sorted(rules.Keys));
        }

        [Fact]
        public void ProjectDeclarationGraphMatchesTheExactAllowlist()
        {
            string root = FindRepositoryRoot();
            IReadOnlyList<ProjectGraph> projects = DiscoverProductionProjects(root);
            IReadOnlyDictionary<string, ProjectGraph> byName =
                projects.ToDictionary(project => project.Name, project => project, StringComparer.Ordinal);

            IReadOnlyList<DependencyDiagnostic> diagnostics =
                ModuleDependencyValidator.ValidateProjectGraph(ExpectedProjectNames, byName, BuildRules());

            Assert.True(diagnostics.Count == 0, "Project declaration graph is not the exact allowlist:" + Environment.NewLine + Describe(diagnostics));
        }

        [Fact]
        public void AssemblyReferenceGraphIsWithinTheAllowedClosureAndKeepsRequiredPositiveEdges()
        {
            string root = FindRepositoryRoot();
            IReadOnlyDictionary<string, ModuleRule> rules = BuildRules();
            var assemblies = new Dictionary<string, AssemblyGraph>(StringComparer.Ordinal);

            foreach (ModuleRule rule in rules.Values.OrderBy(rule => rule.Name, StringComparer.Ordinal))
            {
                string path = ResolveAssemblyPath(root, rule.Name, rule.TargetFramework);
                assemblies.Add(rule.Name, ReadAssemblyGraph(rule.Name, path));
            }

            Assert.True(assemblies.Count == 9, "Expected exactly 9 production assemblies, found " + assemblies.Count + ".");
            Assert.DoesNotContain("Mahjong.Architecture.Tests", assemblies.Keys);

            IReadOnlyList<DependencyDiagnostic> diagnostics =
                ModuleDependencyValidator.ValidateAssemblyGraph(ExpectedProjectNames, assemblies, rules);

            Assert.True(diagnostics.Count == 0, "Assembly reference graph violates the allowlist:" + Environment.NewLine + Describe(diagnostics));
        }

        [Fact]
        public void VerifyScriptAutoDiscoveryRuleFindsThisUnmodifiedProject()
        {
            string root = FindRepositoryRoot();
            string script = File.ReadAllText(Path.Combine(root, "tools", "verify-v5.ps1"));

            // This project is one level below dotnet/tests, so it is covered by the
            // third discovery glob (dotnet\tests\*\*.csproj) and classified as a test
            // by the *.Tests basename check. It is NOT matched by the nested
            // dotnet\tests\*\*.Tests\*.Tests.csproj glob.
            Assert.True(
                script.Contains("'dotnet\\tests\\*\\*.csproj'", StringComparison.Ordinal),
                "tools/verify-v5.ps1 no longer contains the dotnet/tests/*/*.csproj discovery glob (script must not be edited by this task).");
            Assert.True(
                script.Contains("$isTest = $found.BaseName -like '*.Tests'", StringComparison.Ordinal),
                "tools/verify-v5.ps1 no longer classifies discovered projects by the *.Tests suffix.");

            string projectDirectory = Path.Combine(root, "dotnet", "tests", "Mahjong.Architecture.Tests");
            string projectFile = Path.Combine(projectDirectory, "Mahjong.Architecture.Tests.csproj");
            Assert.True(File.Exists(projectFile), "Architecture test project not found: " + projectFile);
            Assert.True(
                new DirectoryInfo(projectDirectory).Parent!.Name == "tests",
                "Project is not directly below dotnet/tests: " + projectDirectory);
            Assert.True(
                new DirectoryInfo(projectDirectory).Name.EndsWith(".Tests", StringComparison.Ordinal),
                "Project directory does not satisfy the script's *.Tests classification: " + projectDirectory);
            Assert.True(
                Path.GetFileName(projectFile).EndsWith(".Tests.csproj", StringComparison.Ordinal),
                "Project file does not match the dotnet/tests/*/*.csproj glob: " + projectFile);
        }

        [Fact]
        public void ForbiddenEdgeInjectedIntoARealProjectFileIsDetectedThroughXmlParsing()
        {
            string root = FindRepositoryRoot();
            string tempRoot = Path.Combine(Path.GetTempPath(), "mahjong-arch-guard-" + Guid.NewGuid().ToString("N"));
            try
            {
                string sourceRoot = Path.Combine(root, "dotnet", "src");
                foreach (string directory in Directory.GetDirectories(sourceRoot))
                {
                    string sourceProject = Directory.GetFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly).Single();
                    string targetDirectory = Path.Combine(tempRoot, "dotnet", "src", new DirectoryInfo(directory).Name);
                    Directory.CreateDirectory(targetDirectory);
                    File.Copy(sourceProject, Path.Combine(targetDirectory, Path.GetFileName(sourceProject)));
                }

                string tableRuntimeProject = Path.Combine(
                    tempRoot, "dotnet", "src", "Mahjong.TableRuntime", "Mahjong.TableRuntime.csproj");
                XDocument document = XDocument.Load(tableRuntimeProject, LoadOptions.None);
                var injectedGroup = new XElement("ItemGroup");
                injectedGroup.Add(new XElement(
                    "ProjectReference",
                    new XAttribute("Include", @"..\Mahjong.Client\Mahjong.Client.csproj")));
                document.Root!.Add(injectedGroup);
                document.Save(tableRuntimeProject);

                IReadOnlyList<ProjectGraph> projects = DiscoverProductionProjects(tempRoot);
                IReadOnlyDictionary<string, ProjectGraph> byName =
                    projects.ToDictionary(project => project.Name, project => project, StringComparer.Ordinal);

                IReadOnlyList<DependencyDiagnostic> diagnostics =
                    ModuleDependencyValidator.ValidateProjectGraph(ExpectedProjectNames, byName, BuildRules());

                DependencyDiagnostic diagnostic = Assert.Single(diagnostics, item => item.Code == DiagnosticCodes.ProjectForbiddenEdge);
                Assert.Equal("Mahjong.TableRuntime", diagnostic.From);
                Assert.Equal("Mahjong.Client", diagnostic.To);
            }
            finally
            {
                DeleteOwnedTempRoot(tempRoot);
            }
        }

        [Fact]
        public void RedirectedProjectReferenceToUndiscoveredPathIsRejected()
        {
            string root = FindRepositoryRoot();
            string tempRoot = CreateProductionXmlSandbox(root);
            try
            {
                string tableRuntimeProject = Path.Combine(
                    tempRoot, "dotnet", "src", "Mahjong.TableRuntime", "Mahjong.TableRuntime.csproj");
                XDocument document = XDocument.Load(tableRuntimeProject, LoadOptions.None);
                XElement reference = document.Descendants("ProjectReference")
                    .First(element => element.Attribute("Include")!.Value.Contains("Mahjong.Domain", StringComparison.Ordinal));
                reference.SetAttributeValue("Include", @"..\..\shadow\Mahjong.Domain.csproj");
                document.Save(tableRuntimeProject);

                IReadOnlyList<DependencyDiagnostic> diagnostics = ValidateProductionXmlSandbox(tempRoot);

                DependencyDiagnostic diagnostic =
                    Assert.Single(diagnostics, item => item.Code == DiagnosticCodes.ProjectReferenceTargetNotDiscovered);
                Assert.Equal("Mahjong.TableRuntime", diagnostic.From);
                Assert.True(
                    diagnostic.To.Contains("shadow", StringComparison.OrdinalIgnoreCase),
                    "Diagnostic must retain the raw shadow target, got: " + diagnostic);
                Assert.True(
                    diagnostic.Detail.Contains("same-basename", StringComparison.OrdinalIgnoreCase),
                    "Diagnostic must explain the same-basename shadow rejection, got: " + diagnostic);
            }
            finally
            {
                DeleteOwnedTempRoot(tempRoot);
            }
        }

        [Fact]
        public void RawAssemblyReferenceDeclarationIsRejected()
        {
            string root = FindRepositoryRoot();
            string tempRoot = CreateProductionXmlSandbox(root);
            try
            {
                string tableRuntimeProject = Path.Combine(
                    tempRoot, "dotnet", "src", "Mahjong.TableRuntime", "Mahjong.TableRuntime.csproj");
                XDocument document = XDocument.Load(tableRuntimeProject, LoadOptions.None);
                document.Root!.Add(new XElement(
                    "ItemGroup",
                    new XElement("Reference", new XAttribute("Include", "Newtonsoft.Json"))));
                document.Save(tableRuntimeProject);

                IReadOnlyList<DependencyDiagnostic> diagnostics = ValidateProductionXmlSandbox(tempRoot);

                DependencyDiagnostic diagnostic =
                    Assert.Single(diagnostics, item => item.Code == DiagnosticCodes.ProjectUnsupportedDeclaration);
                Assert.Equal("Mahjong.TableRuntime", diagnostic.From);
                Assert.Equal("Reference", diagnostic.To);
                Assert.True(
                    diagnostic.Detail.Contains("fails closed", StringComparison.OrdinalIgnoreCase),
                    "Diagnostic must state the fail-closed decision, got: " + diagnostic);
            }
            finally
            {
                DeleteOwnedTempRoot(tempRoot);
            }
        }

        [Theory]
        [InlineData(@"<ItemGroup><ProjectReference Include=""..\Mahjong.Domain\*.csproj"" /></ItemGroup>", "ProjectReference")]
        [InlineData(@"<ItemGroup><ProjectReference Include=""..\Mahjong.Domain\Mahjong.Domain.csproj;..\Mahjong.Ai\Mahjong.Ai.csproj"" /></ItemGroup>", "ProjectReference")]
        [InlineData(@"<Import Project=""..\evil.props"" />", "Import")]
        [InlineData(@"<ItemGroup><PackageReference Include=""$(UnknownPackage)"" Version=""1.0"" /></ItemGroup>", "PackageReference")]
        [InlineData(@"<ItemGroup><Reference Include=""Newtonsoft.Json"" /></ItemGroup>", "Reference")]
        [InlineData(@"<ItemGroup><ReferencePath Include=""..\evil\Newtonsoft.Json.dll"" /></ItemGroup>", "ReferencePath")]
        public void UnsupportedDeclarationFormsFailClosed(string fragment, string expectedElement)
        {
            string root = FindRepositoryRoot();
            string tempRoot = CreateProductionXmlSandbox(root);
            try
            {
                string tableRuntimeProject = Path.Combine(
                    tempRoot, "dotnet", "src", "Mahjong.TableRuntime", "Mahjong.TableRuntime.csproj");
                XDocument document = XDocument.Load(tableRuntimeProject, LoadOptions.None);
                document.Root!.Add(XElement.Parse(fragment));
                document.Save(tableRuntimeProject);

                IReadOnlyList<DependencyDiagnostic> diagnostics = ValidateProductionXmlSandbox(tempRoot);

                Assert.Contains(
                    diagnostics,
                    item => item.Code == DiagnosticCodes.ProjectUnsupportedDeclaration &&
                            item.From == "Mahjong.TableRuntime" &&
                            item.To == expectedElement);
            }
            finally
            {
                DeleteOwnedTempRoot(tempRoot);
            }
        }

        [Fact]
        public void RuleTableCannotWidenAnAssemblyAllowlistBeyondTheProjectClosure()
        {
            var rules = new Dictionary<string, ModuleRule>(BuildRules(), StringComparer.Ordinal);
            rules["Mahjong.Domain"] = rules["Mahjong.Domain"].WithMahjongAssemblyAllowlist(new[] { "Mahjong.Contracts" });
            SyntheticGraph graph = CreateSyntheticGraph();

            IReadOnlyList<DependencyDiagnostic> diagnostics =
                ModuleDependencyValidator.ValidateProjectGraph(graph.KnownProjects, graph.Projects, rules);

            DependencyDiagnostic diagnostic = Assert.Single(diagnostics, item => item.Code == DiagnosticCodes.RuleInconsistent);
            Assert.Equal("Mahjong.Domain", diagnostic.From);
            Assert.Equal("Mahjong.Contracts", diagnostic.To);
            Assert.True(
                diagnostic.Detail.Contains("closure", StringComparison.Ordinal),
                "Diagnostic must explain that the assembly edge is outside the project closure: " + diagnostic);
        }

        [Fact]
        public void SyntheticBaselineIsGreenBeforeMutation()
        {
            SyntheticGraph graph = CreateSyntheticGraph();

            IReadOnlyList<DependencyDiagnostic> projectDiagnostics =
                ModuleDependencyValidator.ValidateProjectGraph(graph.KnownProjects, graph.Projects, graph.Rules);
            IReadOnlyList<DependencyDiagnostic> assemblyDiagnostics =
                ModuleDependencyValidator.ValidateAssemblyGraph(graph.KnownProjects, graph.Assemblies, graph.Rules);

            Assert.True(projectDiagnostics.Count == 0, "Synthetic project baseline is not green:" + Environment.NewLine + Describe(projectDiagnostics));
            Assert.True(assemblyDiagnostics.Count == 0, "Synthetic assembly baseline is not green:" + Environment.NewLine + Describe(assemblyDiagnostics));
        }

        [Theory]
        [InlineData("Mahjong.Domain", "Mahjong.Contracts")]
        [InlineData("Mahjong.Contracts", "Mahjong.Contracts.Wire")]
        [InlineData("Mahjong.Contracts.Wire", "Mahjong.Domain")]
        [InlineData("Mahjong.Contracts.Wire", "Mahjong.Client")]
        [InlineData("Mahjong.Ai", "Mahjong.Contracts")]
        [InlineData("Mahjong.TableRuntime", "Mahjong.Contracts.Wire")]
        [InlineData("Mahjong.TableRuntime", "Mahjong.LocalHost")]
        [InlineData("Mahjong.Client", "Mahjong.Domain")]
        [InlineData("Mahjong.Client", "Mahjong.TableRuntime")]
        [InlineData("Mahjong.LocalHost", "Mahjong.Client")]
        [InlineData("Mahjong.LobbyService", "Mahjong.Domain")]
        [InlineData("Mahjong.LobbyService", "Mahjong.TableRuntime")]
        [InlineData("Mahjong.Infrastructure", "Mahjong.Client")]
        [InlineData("Mahjong.Domain", "Mahjong.Infrastructure")]
        public void ForbiddenProjectEdgesAreRejectedWithSpecificDiagnostics(string from, string to)
        {
            SyntheticGraph graph = CreateSyntheticGraph();
            Dictionary<string, ProjectGraph> projects = CopyProjects(graph);
            projects[from] = projects[from].WithProjectReference(to);

            IReadOnlyList<DependencyDiagnostic> diagnostics =
                ModuleDependencyValidator.ValidateProjectGraph(graph.KnownProjects, projects, graph.Rules);

            DependencyDiagnostic diagnostic = Assert.Single(diagnostics, item => item.Code == DiagnosticCodes.ProjectForbiddenEdge);
            Assert.Equal(from, diagnostic.From);
            Assert.Equal(to, diagnostic.To);
            Assert.True(
                diagnostic.Detail.Contains("forbidden declared project edge", StringComparison.Ordinal),
                "Diagnostic must name the forbidden edge, got: " + diagnostic);
        }

        [Fact]
        public void MissingAllowedProjectEdgeIsRejected()
        {
            SyntheticGraph graph = CreateSyntheticGraph();
            Dictionary<string, ProjectGraph> projects = CopyProjects(graph);
            projects["Mahjong.TableRuntime"] = projects["Mahjong.TableRuntime"].WithoutProjectReference("Mahjong.Domain");

            IReadOnlyList<DependencyDiagnostic> diagnostics =
                ModuleDependencyValidator.ValidateProjectGraph(graph.KnownProjects, projects, graph.Rules);

            DependencyDiagnostic diagnostic = Assert.Single(diagnostics, item => item.Code == DiagnosticCodes.ProjectMissingEdge);
            Assert.Equal("Mahjong.TableRuntime", diagnostic.From);
            Assert.Equal("Mahjong.Domain", diagnostic.To);
        }

        [Fact]
        public void UnknownMahjongProjectEdgeIsRejected()
        {
            SyntheticGraph graph = CreateSyntheticGraph();
            Dictionary<string, ProjectGraph> projects = CopyProjects(graph);
            projects["Mahjong.TableRuntime"] = projects["Mahjong.TableRuntime"].WithProjectReference("Mahjong.Shadow");

            IReadOnlyList<DependencyDiagnostic> diagnostics =
                ModuleDependencyValidator.ValidateProjectGraph(graph.KnownProjects, projects, graph.Rules);

            DependencyDiagnostic diagnostic = Assert.Single(diagnostics, item => item.Code == DiagnosticCodes.ProjectUnknownMahjongReference);
            Assert.Equal("Mahjong.TableRuntime", diagnostic.From);
            Assert.Equal("Mahjong.Shadow", diagnostic.To);
        }

        [Fact]
        public void UnknownMahjongProjectNodeIsRejected()
        {
            SyntheticGraph graph = CreateSyntheticGraph();
            Dictionary<string, ProjectGraph> projects = CopyProjects(graph);
            projects["Mahjong.Shadow"] = Project("Mahjong.Shadow");

            IReadOnlyList<DependencyDiagnostic> diagnostics =
                ModuleDependencyValidator.ValidateProjectGraph(graph.KnownProjects, projects, graph.Rules);

            DependencyDiagnostic diagnostic = Assert.Single(diagnostics, item => item.Code == DiagnosticCodes.ProjectUnknownMahjongProject);
            Assert.Equal("Mahjong.Shadow", diagnostic.From);
        }

        [Fact]
        public void UnknownThirdPartyPackageIsRejected()
        {
            SyntheticGraph graph = CreateSyntheticGraph();
            Dictionary<string, ProjectGraph> projects = CopyProjects(graph);
            projects["Mahjong.Client"] = projects["Mahjong.Client"].WithPackage("Newtonsoft.Json", "13.0.3", string.Empty);

            IReadOnlyList<DependencyDiagnostic> diagnostics =
                ModuleDependencyValidator.ValidateProjectGraph(graph.KnownProjects, projects, graph.Rules);

            DependencyDiagnostic diagnostic = Assert.Single(diagnostics, item => item.Code == DiagnosticCodes.ProjectForbiddenPackage);
            Assert.Equal("Mahjong.Client", diagnostic.From);
            Assert.Equal("Newtonsoft.Json", diagnostic.To);
        }

        [Fact]
        public void MissingDeclaredPackageIsRejected()
        {
            SyntheticGraph graph = CreateSyntheticGraph();
            Dictionary<string, ProjectGraph> projects = CopyProjects(graph);
            projects["Mahjong.Contracts.Wire"] = projects["Mahjong.Contracts.Wire"].WithoutPackage("Google.Protobuf");

            IReadOnlyList<DependencyDiagnostic> diagnostics =
                ModuleDependencyValidator.ValidateProjectGraph(graph.KnownProjects, projects, graph.Rules);

            DependencyDiagnostic diagnostic = Assert.Single(diagnostics, item => item.Code == DiagnosticCodes.ProjectMissingPackage);
            Assert.Equal("Mahjong.Contracts.Wire", diagnostic.From);
            Assert.Equal("Google.Protobuf", diagnostic.To);
        }

        [Fact]
        public void BuildOnlyPackageMustRemainPrivateAssetsAll()
        {
            SyntheticGraph graph = CreateSyntheticGraph();
            Dictionary<string, ProjectGraph> projects = CopyProjects(graph);
            projects["Mahjong.Contracts.Wire"] = projects["Mahjong.Contracts.Wire"].WithPackage("Grpc.Tools", "2.68.1", string.Empty);

            IReadOnlyList<DependencyDiagnostic> diagnostics =
                ModuleDependencyValidator.ValidateProjectGraph(graph.KnownProjects, projects, graph.Rules);

            DependencyDiagnostic diagnostic = Assert.Single(diagnostics, item => item.Code == DiagnosticCodes.ProjectBuildOnlyPackageLeaked);
            Assert.Equal("Mahjong.Contracts.Wire", diagnostic.From);
            Assert.Equal("Grpc.Tools", diagnostic.To);
        }

        [Theory]
        [InlineData("Mahjong.Domain", "Mahjong.Contracts")]
        [InlineData("Mahjong.Contracts.Wire", "Mahjong.Domain")]
        [InlineData("Mahjong.Contracts.Wire", "Mahjong.Client")]
        [InlineData("Mahjong.Ai", "Mahjong.Contracts")]
        [InlineData("Mahjong.Client", "Mahjong.Domain")]
        [InlineData("Mahjong.Client", "Mahjong.TableRuntime")]
        [InlineData("Mahjong.LocalHost", "Mahjong.Client")]
        [InlineData("Mahjong.LobbyService", "Mahjong.TableRuntime")]
        [InlineData("Mahjong.TableRuntime", "Mahjong.Contracts.Wire")]
        [InlineData("Mahjong.Infrastructure", "Mahjong.Client")]
        [InlineData("Mahjong.Domain", "Mahjong.Infrastructure")]
        public void ForbiddenAssemblyMahjongReferencesAreRejectedWithSpecificDiagnostics(string from, string to)
        {
            SyntheticGraph graph = CreateSyntheticGraph();
            Dictionary<string, AssemblyGraph> assemblies = CopyAssemblies(graph);
            assemblies[from] = assemblies[from].WithReference(to);

            IReadOnlyList<DependencyDiagnostic> diagnostics =
                ModuleDependencyValidator.ValidateAssemblyGraph(graph.KnownProjects, assemblies, graph.Rules);

            DependencyDiagnostic diagnostic = Assert.Single(diagnostics, item => item.Code == DiagnosticCodes.AssemblyForbiddenMahjongReference);
            Assert.Equal(from, diagnostic.From);
            Assert.Equal(to, diagnostic.To);
            Assert.True(
                diagnostic.Detail.Contains("forbidden Mahjong assembly reference", StringComparison.Ordinal),
                "Diagnostic must name the forbidden assembly edge, got: " + diagnostic);
        }

        [Fact]
        public void UnknownMahjongAssemblyReferenceIsRejected()
        {
            SyntheticGraph graph = CreateSyntheticGraph();
            Dictionary<string, AssemblyGraph> assemblies = CopyAssemblies(graph);
            assemblies["Mahjong.Client"] = assemblies["Mahjong.Client"].WithReference("Mahjong.Shadow");

            IReadOnlyList<DependencyDiagnostic> diagnostics =
                ModuleDependencyValidator.ValidateAssemblyGraph(graph.KnownProjects, assemblies, graph.Rules);

            DependencyDiagnostic diagnostic = Assert.Single(diagnostics, item => item.Code == DiagnosticCodes.AssemblyUnknownMahjongReference);
            Assert.Equal("Mahjong.Client", diagnostic.From);
            Assert.Equal("Mahjong.Shadow", diagnostic.To);
        }

        [Fact]
        public void UnknownMahjongAssemblyNodeIsRejected()
        {
            SyntheticGraph graph = CreateSyntheticGraph();
            Dictionary<string, AssemblyGraph> assemblies = CopyAssemblies(graph);
            assemblies["Mahjong.Shadow"] = Assembly("Mahjong.Shadow", "netstandard");

            IReadOnlyList<DependencyDiagnostic> diagnostics =
                ModuleDependencyValidator.ValidateAssemblyGraph(graph.KnownProjects, assemblies, graph.Rules);

            DependencyDiagnostic diagnostic = Assert.Single(diagnostics, item => item.Code == DiagnosticCodes.AssemblyUnknownMahjongAssembly);
            Assert.Equal("Mahjong.Shadow", diagnostic.From);
        }

        [Theory]
        [InlineData("Mahjong.Client", "Newtonsoft.Json")]
        [InlineData("Mahjong.LobbyService", "MySql.Data")]
        [InlineData("Mahjong.Contracts.Wire", "Npgsql")]
        [InlineData("Mahjong.Domain", "Google.Protobuf")]
        [InlineData("Mahjong.Infrastructure", "Google.Protobuf")]
        public void UnknownThirdPartyAssemblyReferenceIsRejected(string from, string to)
        {
            SyntheticGraph graph = CreateSyntheticGraph();
            Dictionary<string, AssemblyGraph> assemblies = CopyAssemblies(graph);
            assemblies[from] = assemblies[from].WithReference(to);

            IReadOnlyList<DependencyDiagnostic> diagnostics =
                ModuleDependencyValidator.ValidateAssemblyGraph(graph.KnownProjects, assemblies, graph.Rules);

            DependencyDiagnostic diagnostic = Assert.Single(diagnostics, item => item.Code == DiagnosticCodes.AssemblyForbiddenThirdParty);
            Assert.Equal(from, diagnostic.From);
            Assert.Equal(to, diagnostic.To);
            Assert.True(
                diagnostic.Detail.Contains("third-party", StringComparison.OrdinalIgnoreCase),
                "Diagnostic must name the third-party assembly, got: " + diagnostic);
        }

        [Fact]
        public void MissingRequiredAssemblyMahjongReferenceIsRejected()
        {
            SyntheticGraph graph = CreateSyntheticGraph();
            Dictionary<string, AssemblyGraph> assemblies = CopyAssemblies(graph);
            assemblies["Mahjong.TableRuntime"] = assemblies["Mahjong.TableRuntime"].WithoutReference("Mahjong.Domain");

            IReadOnlyList<DependencyDiagnostic> diagnostics =
                ModuleDependencyValidator.ValidateAssemblyGraph(graph.KnownProjects, assemblies, graph.Rules);

            DependencyDiagnostic diagnostic = Assert.Single(diagnostics, item => item.Code == DiagnosticCodes.AssemblyMissingMahjongReference);
            Assert.Equal("Mahjong.TableRuntime", diagnostic.From);
            Assert.Equal("Mahjong.Domain", diagnostic.To);
        }

        [Fact]
        public void MissingRequiredThirdPartyAssemblyReferenceIsRejected()
        {
            SyntheticGraph graph = CreateSyntheticGraph();
            Dictionary<string, AssemblyGraph> assemblies = CopyAssemblies(graph);
            assemblies["Mahjong.Contracts.Wire"] = assemblies["Mahjong.Contracts.Wire"].WithoutReference("Google.Protobuf");

            IReadOnlyList<DependencyDiagnostic> diagnostics =
                ModuleDependencyValidator.ValidateAssemblyGraph(graph.KnownProjects, assemblies, graph.Rules);

            DependencyDiagnostic diagnostic = Assert.Single(diagnostics, item => item.Code == DiagnosticCodes.AssemblyMissingThirdParty);
            Assert.Equal("Mahjong.Contracts.Wire", diagnostic.From);
            Assert.Equal("Google.Protobuf", diagnostic.To);
        }

        [Fact]
        public void LegalBclSharedFrameworkAndPerModuleThirdPartyReferencesStayGreen()
        {
            SyntheticGraph graph = CreateSyntheticGraph();
            Dictionary<string, AssemblyGraph> assemblies = CopyAssemblies(graph);
            assemblies["Mahjong.Domain"] = assemblies["Mahjong.Domain"].WithReference("System.Collections");
            assemblies["Mahjong.Contracts"] = assemblies["Mahjong.Contracts"].WithReference("System.Runtime");
            assemblies["Mahjong.LobbyService"] = assemblies["Mahjong.LobbyService"].WithReference("Microsoft.AspNetCore.Http");
            assemblies["Mahjong.LobbyService"] = assemblies["Mahjong.LobbyService"].WithReference("Microsoft.Extensions.DependencyInjection.Abstractions");
            assemblies["Mahjong.LobbyService"] = assemblies["Mahjong.LobbyService"].WithReference("Npgsql");
            assemblies["Mahjong.Contracts.Wire"] = assemblies["Mahjong.Contracts.Wire"].WithReference("Google.Protobuf");

            IReadOnlyList<DependencyDiagnostic> diagnostics =
                ModuleDependencyValidator.ValidateAssemblyGraph(graph.KnownProjects, assemblies, graph.Rules);

            Assert.True(
                diagnostics.Count == 0,
                "BCL/shared-framework and per-module approved third-party references must not be rejected:" + Environment.NewLine + Describe(diagnostics));
        }

        [Fact]
        public void FrameworkCatalogUsesExactRealAssemblyIdentity()
        {
            Assert.True(FrameworkAssemblyCatalog.PlatformAssemblyCount > 100, "Platform framework catalogue is unexpectedly small.");
            Assert.True(FrameworkAssemblyCatalog.SharedFrameworkAssemblyCount > 50, "Shared-framework catalogue is unexpectedly small.");

            Assert.Equal(ReferenceKind.Bcl, ReferenceClassifier.Classify("netstandard"));
            Assert.Equal(ReferenceKind.Bcl, ReferenceClassifier.Classify("System.Collections"));
            Assert.Equal(ReferenceKind.SharedFramework, ReferenceClassifier.Classify("Microsoft.AspNetCore.Http"));
            Assert.Equal(ReferenceKind.SharedFramework, ReferenceClassifier.Classify("Microsoft.Extensions.Logging.Abstractions"));

            Assert.Equal(ReferenceKind.ThirdParty, ReferenceClassifier.Classify("System.Reactive"));
            Assert.Equal(ReferenceKind.ThirdParty, ReferenceClassifier.Classify("System.IO.Abstractions"));
            Assert.Equal(ReferenceKind.ThirdParty, ReferenceClassifier.Classify("Microsoft.Extensions.UnknownVendor"));
        }

        [Theory]
        [InlineData("System.Reactive", DiagnosticCodes.AssemblyForbiddenThirdParty)]
        [InlineData("System.IO.Abstractions", DiagnosticCodes.AssemblyForbiddenThirdParty)]
        [InlineData("Microsoft.AspNetCore.Http", DiagnosticCodes.AssemblyUnauthorizedSharedFramework)]
        [InlineData("Microsoft.Extensions.UnknownVendor", DiagnosticCodes.AssemblyForbiddenThirdParty)]
        public void DomainCannotBorrowSystemOrMicrosoftPrefixesAsCover(string reference, string expectedCode)
        {
            SyntheticGraph graph = CreateSyntheticGraph();
            Dictionary<string, AssemblyGraph> assemblies = CopyAssemblies(graph);
            assemblies["Mahjong.Domain"] = assemblies["Mahjong.Domain"].WithReference(reference);

            IReadOnlyList<DependencyDiagnostic> diagnostics =
                ModuleDependencyValidator.ValidateAssemblyGraph(graph.KnownProjects, assemblies, graph.Rules);

            DependencyDiagnostic diagnostic = Assert.Single(diagnostics, item => item.Code == expectedCode);
            Assert.Equal("Mahjong.Domain", diagnostic.From);
            Assert.Equal(reference, diagnostic.To);
            Assert.True(
                diagnostic.Detail.Contains("third-party", StringComparison.OrdinalIgnoreCase) ||
                diagnostic.Detail.Contains("shared-framework", StringComparison.OrdinalIgnoreCase),
                "Diagnostic must explain why the prefix is not a BCL/framework passport: " + diagnostic);
        }

        [Fact]
        public void LobbyUnknownMicrosoftExtensionsVendorIsRejected()
        {
            SyntheticGraph graph = CreateSyntheticGraph();
            Dictionary<string, AssemblyGraph> assemblies = CopyAssemblies(graph);
            assemblies["Mahjong.LobbyService"] =
                assemblies["Mahjong.LobbyService"].WithReference("Microsoft.Extensions.UnknownVendor");

            IReadOnlyList<DependencyDiagnostic> diagnostics =
                ModuleDependencyValidator.ValidateAssemblyGraph(graph.KnownProjects, assemblies, graph.Rules);

            DependencyDiagnostic diagnostic = Assert.Single(diagnostics, item => item.Code == DiagnosticCodes.AssemblyForbiddenThirdParty);
            Assert.Equal("Mahjong.LobbyService", diagnostic.From);
            Assert.Equal("Microsoft.Extensions.UnknownVendor", diagnostic.To);
        }

        [Fact]
        public void LobbyRealSharedFrameworkReferenceIsAuthorized()
        {
            SyntheticGraph graph = CreateSyntheticGraph();
            Dictionary<string, AssemblyGraph> assemblies = CopyAssemblies(graph);
            assemblies["Mahjong.LobbyService"] =
                assemblies["Mahjong.LobbyService"].WithReference("Microsoft.AspNetCore.Http");

            IReadOnlyList<DependencyDiagnostic> diagnostics =
                ModuleDependencyValidator.ValidateAssemblyGraph(graph.KnownProjects, assemblies, graph.Rules);

            Assert.True(diagnostics.Count == 0, "Lobby must be allowed to use the real ASP.NET Core shared framework:" + Environment.NewLine + Describe(diagnostics));
        }

        [Theory]
        [InlineData("Mahjong.Domain", ReferenceKind.Mahjong)]
        [InlineData("Mahjong.Contracts.Wire", ReferenceKind.Mahjong)]
        [InlineData("netstandard", ReferenceKind.Bcl)]
        [InlineData("mscorlib", ReferenceKind.Bcl)]
        [InlineData("System.Runtime", ReferenceKind.Bcl)]
        [InlineData("System.Data.Common", ReferenceKind.Bcl)]
        [InlineData("Microsoft.CSharp", ReferenceKind.Bcl)]
        [InlineData("Microsoft.Win32.Registry", ReferenceKind.Bcl)]
        [InlineData("Microsoft.AspNetCore", ReferenceKind.SharedFramework)]
        [InlineData("Microsoft.AspNetCore.Http.Abstractions", ReferenceKind.SharedFramework)]
        [InlineData("Microsoft.Extensions.Logging.Abstractions", ReferenceKind.SharedFramework)]
        [InlineData("Microsoft.Net.Http.Headers", ReferenceKind.SharedFramework)]
        [InlineData("Google.Protobuf", ReferenceKind.ThirdParty)]
        [InlineData("Npgsql", ReferenceKind.ThirdParty)]
        [InlineData("Newtonsoft.Json", ReferenceKind.ThirdParty)]
        [InlineData("Microsoft.Data.SqlClient", ReferenceKind.ThirdParty)]
        [InlineData("Grpc.Core", ReferenceKind.ThirdParty)]
        [InlineData("System.Reactive", ReferenceKind.ThirdParty)]
        [InlineData("System.IO.Abstractions", ReferenceKind.ThirdParty)]
        [InlineData("Microsoft.Extensions.UnknownVendor", ReferenceKind.ThirdParty)]
        public void ReferenceClassifierSeparatesBclFrameworkMahjongAndThirdParty(string name, ReferenceKind expected)
        {
            Assert.Equal(expected, ReferenceClassifier.Classify(name));
        }

        private static IReadOnlyDictionary<string, ModuleRule> BuildRules()
        {
            var rules = new Dictionary<string, ModuleRule>(StringComparer.Ordinal)
            {
                ["Mahjong.Domain"] = new ModuleRule(
                    name: "Mahjong.Domain",
                    targetFramework: "netstandard2.1",
                    allowedProjectReferences: NoStrings,
                    allowedPackages: NoStrings,
                    buildOnlyPackages: NoStrings,
                    allowedMahjongAssemblyReferences: NoStrings,
                    requiredMahjongAssemblyReferences: NoStrings,
                    allowedThirdPartyAssemblyReferences: NoStrings,
                    requiredThirdPartyAssemblyReferences: NoStrings),

                ["Mahjong.Contracts"] = new ModuleRule(
                    name: "Mahjong.Contracts",
                    targetFramework: "netstandard2.1",
                    allowedProjectReferences: NoStrings,
                    allowedPackages: NoStrings,
                    buildOnlyPackages: NoStrings,
                    allowedMahjongAssemblyReferences: NoStrings,
                    requiredMahjongAssemblyReferences: NoStrings,
                    allowedThirdPartyAssemblyReferences: NoStrings,
                    requiredThirdPartyAssemblyReferences: NoStrings),

                ["Mahjong.Contracts.Wire"] = new ModuleRule(
                    name: "Mahjong.Contracts.Wire",
                    targetFramework: "netstandard2.1",
                    allowedProjectReferences: new[] { "Mahjong.Contracts" },
                    allowedPackages: new[] { "Google.Protobuf", "Grpc.Tools" },
                    buildOnlyPackages: new[] { "Grpc.Tools" },
                    allowedMahjongAssemblyReferences: new[] { "Mahjong.Contracts" },
                    requiredMahjongAssemblyReferences: new[] { "Mahjong.Contracts" },
                    allowedThirdPartyAssemblyReferences: new[] { "Google.Protobuf" },
                    requiredThirdPartyAssemblyReferences: new[] { "Google.Protobuf" }),

                ["Mahjong.Ai"] = new ModuleRule(
                    name: "Mahjong.Ai",
                    targetFramework: "netstandard2.1",
                    allowedProjectReferences: new[] { "Mahjong.Domain" },
                    allowedPackages: NoStrings,
                    buildOnlyPackages: NoStrings,
                    allowedMahjongAssemblyReferences: new[] { "Mahjong.Domain" },
                    requiredMahjongAssemblyReferences: new[] { "Mahjong.Domain" },
                    allowedThirdPartyAssemblyReferences: NoStrings,
                    requiredThirdPartyAssemblyReferences: NoStrings),

                ["Mahjong.TableRuntime"] = new ModuleRule(
                    name: "Mahjong.TableRuntime",
                    targetFramework: "netstandard2.1",
                    allowedProjectReferences: new[] { "Mahjong.Ai", "Mahjong.Contracts", "Mahjong.Domain" },
                    allowedPackages: NoStrings,
                    buildOnlyPackages: NoStrings,
                    allowedMahjongAssemblyReferences: new[] { "Mahjong.Ai", "Mahjong.Contracts", "Mahjong.Domain" },
                    requiredMahjongAssemblyReferences: new[] { "Mahjong.Ai", "Mahjong.Contracts", "Mahjong.Domain" },
                    allowedThirdPartyAssemblyReferences: NoStrings,
                    requiredThirdPartyAssemblyReferences: NoStrings),

                ["Mahjong.Client"] = new ModuleRule(
                    name: "Mahjong.Client",
                    targetFramework: "netstandard2.1",
                    allowedProjectReferences: new[] { "Mahjong.Contracts", "Mahjong.Contracts.Wire" },
                    allowedPackages: NoStrings,
                    buildOnlyPackages: NoStrings,
                    allowedMahjongAssemblyReferences: new[] { "Mahjong.Contracts", "Mahjong.Contracts.Wire" },
                    requiredMahjongAssemblyReferences: new[] { "Mahjong.Contracts", "Mahjong.Contracts.Wire" },
                    allowedThirdPartyAssemblyReferences: new[] { "Google.Protobuf" },
                    requiredThirdPartyAssemblyReferences: new[] { "Google.Protobuf" }),

                ["Mahjong.LocalHost"] = new ModuleRule(
                    name: "Mahjong.LocalHost",
                    targetFramework: "netstandard2.1",
                    allowedProjectReferences: new[] { "Mahjong.TableRuntime" },
                    allowedPackages: NoStrings,
                    buildOnlyPackages: NoStrings,
                    allowedMahjongAssemblyReferences: new[]
                    {
                        "Mahjong.Ai",
                        "Mahjong.Contracts",
                        "Mahjong.Domain",
                        "Mahjong.TableRuntime"
                    },
                    requiredMahjongAssemblyReferences: NoStrings,
                    allowedThirdPartyAssemblyReferences: NoStrings,
                    requiredThirdPartyAssemblyReferences: NoStrings),

                ["Mahjong.LobbyService"] = new ModuleRule(
                    name: "Mahjong.LobbyService",
                    targetFramework: "net8.0",
                    allowedProjectReferences: new[] { "Mahjong.Contracts" },
                    allowedPackages: new[] { "Npgsql" },
                    buildOnlyPackages: NoStrings,
                    allowedMahjongAssemblyReferences: new[] { "Mahjong.Contracts" },
                    requiredMahjongAssemblyReferences: NoStrings,
                    allowedThirdPartyAssemblyReferences: new[] { "Npgsql" },
                    requiredThirdPartyAssemblyReferences: new[] { "Npgsql" },
                    allowSharedFramework: true),

                ["Mahjong.Infrastructure"] = new ModuleRule(
                    name: "Mahjong.Infrastructure",
                    targetFramework: "net8.0",
                    allowedProjectReferences: new[] { "Mahjong.Contracts", "Mahjong.Domain", "Mahjong.TableRuntime" },
                    allowedPackages: new[] { "Npgsql" },
                    buildOnlyPackages: NoStrings,
                    allowedMahjongAssemblyReferences: new[] { "Mahjong.Ai", "Mahjong.Contracts", "Mahjong.Domain", "Mahjong.TableRuntime" },
                    requiredMahjongAssemblyReferences: new[] { "Mahjong.Contracts", "Mahjong.Domain", "Mahjong.TableRuntime" },
                    allowedThirdPartyAssemblyReferences: new[] { "Npgsql" },
                    requiredThirdPartyAssemblyReferences: new[] { "Npgsql" })
            };

            return rules;
        }

        private static SyntheticGraph CreateSyntheticGraph()
        {
            var projects = new Dictionary<string, ProjectGraph>(StringComparer.Ordinal)
            {
                ["Mahjong.Domain"] = Project("Mahjong.Domain"),
                ["Mahjong.Contracts"] = Project("Mahjong.Contracts"),
                ["Mahjong.Contracts.Wire"] = new ProjectGraph(
                    "Mahjong.Contracts.Wire",
                    "synthetic/Mahjong.Contracts.Wire.csproj",
                    new[] { "Mahjong.Contracts" },
                    new[]
                    {
                        new PackageReferenceFact("Google.Protobuf", "3.28.3", string.Empty),
                        new PackageReferenceFact("Grpc.Tools", "2.68.1", "All")
                    }),
                ["Mahjong.Ai"] = Project("Mahjong.Ai", "Mahjong.Domain"),
                ["Mahjong.TableRuntime"] = Project("Mahjong.TableRuntime", "Mahjong.Ai", "Mahjong.Contracts", "Mahjong.Domain"),
                ["Mahjong.Client"] = Project("Mahjong.Client", "Mahjong.Contracts", "Mahjong.Contracts.Wire"),
                ["Mahjong.LocalHost"] = Project("Mahjong.LocalHost", "Mahjong.TableRuntime"),
                ["Mahjong.Infrastructure"] = new ProjectGraph(
                    "Mahjong.Infrastructure",
                    "synthetic/Mahjong.Infrastructure.csproj",
                    new[] { "Mahjong.Contracts", "Mahjong.Domain", "Mahjong.TableRuntime" },
                    new[] { new PackageReferenceFact("Npgsql", "8.0.9", string.Empty) }),
                ["Mahjong.LobbyService"] = new ProjectGraph(
                    "Mahjong.LobbyService",
                    "synthetic/Mahjong.LobbyService.csproj",
                    new[] { "Mahjong.Contracts" },
                    new[] { new PackageReferenceFact("Npgsql", "8.0.9", string.Empty) })
            };

            var assemblies = new Dictionary<string, AssemblyGraph>(StringComparer.Ordinal)
            {
                ["Mahjong.Domain"] = Assembly("Mahjong.Domain", "netstandard"),
                ["Mahjong.Contracts"] = Assembly("Mahjong.Contracts", "netstandard"),
                ["Mahjong.Contracts.Wire"] = Assembly("Mahjong.Contracts.Wire", "Google.Protobuf", "Mahjong.Contracts", "netstandard"),
                ["Mahjong.Ai"] = Assembly("Mahjong.Ai", "Mahjong.Domain", "netstandard"),
                ["Mahjong.TableRuntime"] = Assembly("Mahjong.TableRuntime", "Mahjong.Ai", "Mahjong.Contracts", "Mahjong.Domain", "netstandard"),
                ["Mahjong.Client"] = Assembly("Mahjong.Client", "Google.Protobuf", "Mahjong.Contracts", "Mahjong.Contracts.Wire", "netstandard"),
                // LocalHost has no source today, so its DLL does not emit the declared TableRuntime edge.
                ["Mahjong.LocalHost"] = Assembly("Mahjong.LocalHost", "netstandard"),
                ["Mahjong.Infrastructure"] = Assembly(
                    "Mahjong.Infrastructure", "Mahjong.Contracts", "Mahjong.Domain", "Mahjong.TableRuntime", "Npgsql", "System.Runtime"),
                // LobbyService declares Contracts but does not use its types, so that edge is not required at the assembly level.
                ["Mahjong.LobbyService"] = Assembly(
                    "Mahjong.LobbyService",
                    "Microsoft.AspNetCore",
                    "Microsoft.Extensions.Logging.Abstractions",
                    "netstandard",
                    "Npgsql",
                    "System.Runtime")
            };

            return new SyntheticGraph(ExpectedProjectNames, projects, assemblies, BuildRules());
        }

        private static ProjectGraph Project(string name, params string[] references)
        {
            return new ProjectGraph(name, "synthetic/" + name + ".csproj", references, Array.Empty<PackageReferenceFact>());
        }

        private static AssemblyGraph Assembly(string name, params string[] references)
        {
            return new AssemblyGraph(name, "synthetic/" + name + ".dll", references);
        }

        private static Dictionary<string, ProjectGraph> CopyProjects(SyntheticGraph graph)
        {
            return graph.Projects.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        }

        private static Dictionary<string, AssemblyGraph> CopyAssemblies(SyntheticGraph graph)
        {
            return graph.Assemblies.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        }

        private static IReadOnlyList<ProjectGraph> DiscoverProductionProjects(string repositoryRoot)
        {
            string sourceRoot = Path.Combine(repositoryRoot, "dotnet", "src");
            Assert.True(Directory.Exists(sourceRoot), "Production source root not found: " + sourceRoot);

            var projects = new List<ProjectGraph>();
            foreach (string directory in Directory.GetDirectories(sourceRoot).OrderBy(path => path, StringComparer.Ordinal))
            {
                string[] projectFiles = Directory.GetFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly);
                Assert.True(projectFiles.Length == 1, "Expected exactly one csproj in " + directory + ", found " + projectFiles.Length + ".");

                string projectFile = projectFiles[0];
                string name = Path.GetFileNameWithoutExtension(projectFile);
                Assert.True(
                    string.Equals(name, new DirectoryInfo(directory).Name, StringComparison.Ordinal),
                    "Project file name '" + name + "' does not match its directory '" + directory + "'.");

                XDocument document = XDocument.Load(projectFile, LoadOptions.None);
                XmlDeclarations declarations = ReadXmlDeclarations(document, projectFile);
                projects.Add(new ProjectGraph(
                    name,
                    Path.GetFullPath(projectFile),
                    declarations.ProjectReferences,
                    declarations.Packages,
                    declarations.Problems));
            }

            return projects;
        }

        private static XmlDeclarations ReadXmlDeclarations(XDocument document, string projectPath)
        {
            var result = new XmlDeclarations();
            string projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath))
                ?? throw new InvalidOperationException("Cannot determine the directory of " + projectPath + ".");

            foreach (XElement element in document.Descendants())
            {
                string localName = element.Name.LocalName;
                if (string.Equals(localName, "ProjectReference", StringComparison.Ordinal))
                {
                    string? include = (string?)element.Attribute("Include");
                    string problem = ValidateSimpleInclude(element, include, "ProjectReference");
                    if (problem.Length > 0)
                    {
                        result.Problems.Add(new DeclarationProblem("ProjectReference", problem));
                        continue;
                    }

                    string canonical;
                    try
                    {
                        canonical = Path.GetFullPath(Path.Combine(projectDirectory, include!.Replace('\\', '/')));
                    }
                    catch (Exception exception)
                    {
                        result.Problems.Add(new DeclarationProblem(
                            "ProjectReference",
                            "cannot resolve Include '" + include + "': " + exception.Message));
                        continue;
                    }

                    string targetName = Path.GetFileNameWithoutExtension(canonical);
                    if (targetName.Length == 0)
                    {
                        result.Problems.Add(new DeclarationProblem(
                            "ProjectReference",
                            "Include '" + include + "' does not resolve to a project file name"));
                        continue;
                    }

                    result.ProjectReferences.Add(new ProjectReferenceFact(include!, targetName, canonical));
                }
                else if (string.Equals(localName, "PackageReference", StringComparison.Ordinal))
                {
                    string? include = (string?)element.Attribute("Include");
                    string problem = ValidateSimpleInclude(element, include, "PackageReference");
                    if (problem.Length > 0)
                    {
                        result.Problems.Add(new DeclarationProblem("PackageReference", problem));
                        continue;
                    }

                    result.Packages.Add(new PackageReferenceFact(
                        include!,
                        ReadAttributeOrChild(element, "Version"),
                        ReadAttributeOrChild(element, "PrivateAssets")));
                }
                else if (string.Equals(localName, "Reference", StringComparison.Ordinal) ||
                         string.Equals(localName, "ReferencePath", StringComparison.Ordinal) ||
                         string.Equals(localName, "COMReference", StringComparison.Ordinal) ||
                         string.Equals(localName, "FrameworkReference", StringComparison.Ordinal) ||
                         string.Equals(localName, "Import", StringComparison.Ordinal))
                {
                    result.Problems.Add(new DeclarationProblem(localName, DescribeUnsupportedDeclaration(element, localName)));
                }
            }

            return result;
        }

        private static string ValidateSimpleInclude(XElement element, string? include, string kind)
        {
            if (element.AncestorsAndSelf().Any(ancestor => ancestor.Attribute("Condition") != null))
            {
                return kind + " under a Condition is not modeled; the declaration graph fails closed";
            }

            if (element.Attribute("Update") != null || element.Attribute("Remove") != null)
            {
                return kind + " Update/Remove is not modeled; the declaration graph fails closed";
            }

            if (string.IsNullOrWhiteSpace(include))
            {
                return kind + " has no non-empty Include; the declaration graph fails closed";
            }

            if (include!.IndexOf(';') >= 0)
            {
                return kind + " with multiple Include items is not modeled; the declaration graph fails closed";
            }

            if (include.IndexOf('*') >= 0 || include.IndexOf('?') >= 0)
            {
                return kind + " with a wildcard Include is not modeled; the declaration graph fails closed";
            }

            if (include.Contains("$(", StringComparison.Ordinal) ||
                include.Contains("@(", StringComparison.Ordinal) ||
                include.Contains("%(", StringComparison.Ordinal))
            {
                return kind + " with an MSBuild property/item expression is not modeled; the declaration graph fails closed";
            }

            return string.Empty;
        }

        private static string DescribeUnsupportedDeclaration(XElement element, string localName)
        {
            string? include = (string?)element.Attribute("Include")
                ?? (string?)element.Attribute("Project")
                ?? (string?)element.Attribute("Update");
            return "<" + localName + "> declaration '" + (include ?? "(none)") +
                "' is not modeled; the project declaration graph fails closed instead of ignoring it";
        }

        private static string ReadAttributeOrChild(XElement element, string name)
        {
            string? attribute = (string?)element.Attribute(name);
            if (!string.IsNullOrEmpty(attribute))
            {
                return attribute!;
            }

            XElement? child = element.Elements().FirstOrDefault(candidate => candidate.Name.LocalName == name);
            return child == null ? string.Empty : child.Value.Trim();
        }

        private static string FindRepositoryRoot()
        {
            DirectoryInfo? current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                bool hasMarkers =
                    File.Exists(Path.Combine(current.FullName, "AGENTS.md")) &&
                    File.Exists(Path.Combine(current.FullName, "tools", "verify-v5.ps1")) &&
                    File.Exists(Path.Combine(current.FullName, "dotnet", "src", "Mahjong.Domain", "Mahjong.Domain.csproj"));
                if (hasMarkers)
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new InvalidOperationException(
                "Repository root (AGENTS.md + tools/verify-v5.ps1 + dotnet/src/Mahjong.Domain) was not found above " +
                AppContext.BaseDirectory + ".");
        }

        private static string CreateProductionXmlSandbox(string repositoryRoot)
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), "mahjong-arch-guard-" + Guid.NewGuid().ToString("N"));
            string sourceRoot = Path.Combine(repositoryRoot, "dotnet", "src");
            foreach (string directory in Directory.GetDirectories(sourceRoot))
            {
                string sourceProject = Directory.GetFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly).Single();
                string targetDirectory = Path.Combine(tempRoot, "dotnet", "src", new DirectoryInfo(directory).Name);
                Directory.CreateDirectory(targetDirectory);
                File.Copy(sourceProject, Path.Combine(targetDirectory, Path.GetFileName(sourceProject)));
            }

            return tempRoot;
        }

        private static IReadOnlyList<DependencyDiagnostic> ValidateProductionXmlSandbox(string tempRoot)
        {
            IReadOnlyList<ProjectGraph> projects = DiscoverProductionProjects(tempRoot);
            IReadOnlyDictionary<string, ProjectGraph> byName =
                projects.ToDictionary(project => project.Name, project => project, StringComparer.Ordinal);
            return ModuleDependencyValidator.ValidateProjectGraph(ExpectedProjectNames, byName, BuildRules());
        }

        private static void DeleteOwnedTempRoot(string path)
        {
            string fullPath = Path.GetFullPath(path);
            string tempPath = Path.GetFullPath(Path.GetTempPath());
            if (!Directory.Exists(fullPath))
            {
                return;
            }

            char[] separators = { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
            bool underProcessTemp =
                fullPath.StartsWith(tempPath, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(fullPath.TrimEnd(separators), tempPath.TrimEnd(separators), StringComparison.OrdinalIgnoreCase);
            bool ownedName = new DirectoryInfo(fullPath).Name.StartsWith("mahjong-arch-guard-", StringComparison.Ordinal);
            if (!underProcessTemp || !ownedName)
            {
                throw new InvalidOperationException("Refusing to delete a directory that this test does not own: " + fullPath);
            }

            if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("Refusing to recursively delete a reparse-point temp root: " + fullPath);
            }

            Directory.Delete(fullPath, recursive: true);
        }

        private static string ResolveAssemblyPath(string repositoryRoot, string name, string targetFramework)
        {
            string path = Path.Combine(repositoryRoot, "dotnet", "src", name, "bin", "Release", targetFramework, name + ".dll");
            Assert.True(
                File.Exists(path),
                "Fresh Release assembly not found: " + path + ". Build this project with -c Release before running the guard.");
            return path;
        }

        private static AssemblyGraph ReadAssemblyGraph(string name, string path)
        {
            using FileStream stream = File.OpenRead(path);
            using PEReader peReader = new PEReader(stream);
            Assert.True(peReader.HasMetadata, "Assembly has no metadata: " + path);

            MetadataReader reader = peReader.GetMetadataReader();
            Assert.True(reader.IsAssembly, "File is not an assembly: " + path);

            var references = new List<string>();
            foreach (AssemblyReferenceHandle handle in reader.AssemblyReferences)
            {
                AssemblyReference reference = reader.GetAssemblyReference(handle);
                references.Add(reader.GetString(reference.Name));
            }

            return new AssemblyGraph(name, path, references);
        }

        private static string[] Sorted(IEnumerable<string> values)
        {
            return values.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        }

        private static string Describe(IEnumerable<DependencyDiagnostic> diagnostics)
        {
            string[] lines = diagnostics.Select(diagnostic => diagnostic.ToString()).ToArray();
            return lines.Length == 0 ? "(none)" : string.Join(Environment.NewLine, lines);
        }

        private sealed class XmlDeclarations
        {
            public List<ProjectReferenceFact> ProjectReferences { get; } = new List<ProjectReferenceFact>();
            public List<PackageReferenceFact> Packages { get; } = new List<PackageReferenceFact>();
            public List<DeclarationProblem> Problems { get; } = new List<DeclarationProblem>();
        }

        private sealed class SyntheticGraph
        {
            public SyntheticGraph(
                IReadOnlyCollection<string> knownProjects,
                IReadOnlyDictionary<string, ProjectGraph> projects,
                IReadOnlyDictionary<string, AssemblyGraph> assemblies,
                IReadOnlyDictionary<string, ModuleRule> rules)
            {
                KnownProjects = knownProjects;
                Projects = projects;
                Assemblies = assemblies;
                Rules = rules;
            }

            public IReadOnlyCollection<string> KnownProjects { get; }
            public IReadOnlyDictionary<string, ProjectGraph> Projects { get; }
            public IReadOnlyDictionary<string, AssemblyGraph> Assemblies { get; }
            public IReadOnlyDictionary<string, ModuleRule> Rules { get; }
        }
    }

    internal sealed class ProjectGraph
    {
        public ProjectGraph(
            string name,
            string path,
            IEnumerable<string> projectReferences,
            IEnumerable<PackageReferenceFact> packageReferences)
            : this(
                name,
                path,
                SynthesizeDeclarations(path, projectReferences),
                packageReferences,
                Array.Empty<DeclarationProblem>())
        {
        }

        public ProjectGraph(
            string name,
            string path,
            IEnumerable<ProjectReferenceFact> declaredProjectReferences,
            IEnumerable<PackageReferenceFact> packageReferences,
            IEnumerable<DeclarationProblem> declarationProblems)
        {
            Name = name;
            Path = path;
            DeclaredProjectReferences = new List<ProjectReferenceFact>(declaredProjectReferences)
                .OrderBy(value => value.TargetName, StringComparer.Ordinal)
                .ThenBy(value => value.RawInclude, StringComparer.Ordinal)
                .ToArray();
            ProjectReferences = DeclaredProjectReferences
                .Select(value => value.TargetName)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            PackageReferences = new List<PackageReferenceFact>(packageReferences)
                .OrderBy(value => value.Name, StringComparer.Ordinal)
                .ToArray();
            DeclarationProblems = new List<DeclarationProblem>(declarationProblems)
                .OrderBy(value => value.Element, StringComparer.Ordinal)
                .ThenBy(value => value.Detail, StringComparer.Ordinal)
                .ToArray();
        }

        public string Name { get; }
        public string Path { get; }
        public IReadOnlyList<string> ProjectReferences { get; }
        public IReadOnlyList<ProjectReferenceFact> DeclaredProjectReferences { get; }
        public IReadOnlyList<PackageReferenceFact> PackageReferences { get; }
        public IReadOnlyList<DeclarationProblem> DeclarationProblems { get; }

        public ProjectGraph WithProjectReference(string reference)
        {
            string directory = System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
            var declaration = new ProjectReferenceFact(
                reference + ".csproj",
                reference,
                System.IO.Path.Combine(directory, reference + ".csproj"));
            return new ProjectGraph(
                Name,
                Path,
                DeclaredProjectReferences.Append(declaration),
                PackageReferences,
                DeclarationProblems);
        }

        public ProjectGraph WithoutProjectReference(string reference)
        {
            return new ProjectGraph(
                Name,
                Path,
                DeclaredProjectReferences.Where(value => !string.Equals(value.TargetName, reference, StringComparison.Ordinal)),
                PackageReferences,
                DeclarationProblems);
        }

        public ProjectGraph WithPackage(string name, string version, string privateAssets)
        {
            return new ProjectGraph(
                Name,
                Path,
                DeclaredProjectReferences,
                PackageReferences
                    .Where(package => !string.Equals(package.Name, name, StringComparison.Ordinal))
                    .Append(new PackageReferenceFact(name, version, privateAssets)),
                DeclarationProblems);
        }

        public ProjectGraph WithoutPackage(string name)
        {
            return new ProjectGraph(
                Name,
                Path,
                DeclaredProjectReferences,
                PackageReferences.Where(package => !string.Equals(package.Name, name, StringComparison.Ordinal)),
                DeclarationProblems);
        }

        private static IEnumerable<ProjectReferenceFact> SynthesizeDeclarations(
            string projectPath,
            IEnumerable<string> projectReferences)
        {
            string directory = System.IO.Path.GetDirectoryName(projectPath) ?? string.Empty;
            foreach (string reference in projectReferences)
            {
                string canonical = System.IO.Path.Combine(directory, reference + ".csproj");
                yield return new ProjectReferenceFact(reference + ".csproj", reference, canonical);
            }
        }
    }

    internal sealed class ProjectReferenceFact
    {
        public ProjectReferenceFact(string rawInclude, string targetName, string canonicalPath)
        {
            RawInclude = rawInclude;
            TargetName = targetName;
            CanonicalPath = canonicalPath;
        }

        public string RawInclude { get; }
        public string TargetName { get; }
        public string CanonicalPath { get; }
    }

    internal sealed class DeclarationProblem
    {
        public DeclarationProblem(string element, string detail)
        {
            Element = element;
            Detail = detail;
        }

        public string Element { get; }
        public string Detail { get; }
    }

    internal sealed class PackageReferenceFact
    {
        public PackageReferenceFact(string name, string version, string privateAssets)
        {
            Name = name;
            Version = version;
            PrivateAssets = privateAssets;
        }

        public string Name { get; }
        public string Version { get; }
        public string PrivateAssets { get; }
    }

    internal sealed class AssemblyGraph
    {
        public AssemblyGraph(string name, string path, IEnumerable<string> references)
        {
            Name = name;
            Path = path;
            References = new List<string>(references).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        }

        public string Name { get; }
        public string Path { get; }
        public IReadOnlyList<string> References { get; }

        public AssemblyGraph WithReference(string reference)
        {
            return new AssemblyGraph(Name, Path, References.Append(reference));
        }

        public AssemblyGraph WithoutReference(string reference)
        {
            return new AssemblyGraph(
                Name,
                Path,
                References.Where(value => !string.Equals(value, reference, StringComparison.Ordinal)));
        }
    }

    internal sealed class ModuleRule
    {
        public ModuleRule(
            string name,
            string targetFramework,
            IEnumerable<string> allowedProjectReferences,
            IEnumerable<string> allowedPackages,
            IEnumerable<string> buildOnlyPackages,
            IEnumerable<string> allowedMahjongAssemblyReferences,
            IEnumerable<string> requiredMahjongAssemblyReferences,
            IEnumerable<string> allowedThirdPartyAssemblyReferences,
            IEnumerable<string> requiredThirdPartyAssemblyReferences,
            bool allowSharedFramework = false)
        {
            Name = name;
            TargetFramework = targetFramework;
            AllowedProjectReferences = new HashSet<string>(allowedProjectReferences, StringComparer.Ordinal);
            AllowedPackages = new HashSet<string>(allowedPackages, StringComparer.Ordinal);
            BuildOnlyPackages = new HashSet<string>(buildOnlyPackages, StringComparer.Ordinal);
            AllowedMahjongAssemblyReferences = new HashSet<string>(allowedMahjongAssemblyReferences, StringComparer.Ordinal);
            RequiredMahjongAssemblyReferences = new HashSet<string>(requiredMahjongAssemblyReferences, StringComparer.Ordinal);
            AllowedThirdPartyAssemblyReferences = new HashSet<string>(allowedThirdPartyAssemblyReferences, StringComparer.Ordinal);
            RequiredThirdPartyAssemblyReferences = new HashSet<string>(requiredThirdPartyAssemblyReferences, StringComparer.Ordinal);
            AllowSharedFramework = allowSharedFramework;
        }

        public string Name { get; }
        public string TargetFramework { get; }
        public HashSet<string> AllowedProjectReferences { get; }
        public HashSet<string> AllowedPackages { get; }
        public HashSet<string> BuildOnlyPackages { get; }
        public HashSet<string> AllowedMahjongAssemblyReferences { get; }
        public HashSet<string> RequiredMahjongAssemblyReferences { get; }
        public HashSet<string> AllowedThirdPartyAssemblyReferences { get; }
        public HashSet<string> RequiredThirdPartyAssemblyReferences { get; }
        public bool AllowSharedFramework { get; }

        public ModuleRule WithMahjongAssemblyAllowlist(IEnumerable<string> allowedMahjongAssemblyReferences)
        {
            return new ModuleRule(
                Name,
                TargetFramework,
                AllowedProjectReferences,
                AllowedPackages,
                BuildOnlyPackages,
                allowedMahjongAssemblyReferences,
                RequiredMahjongAssemblyReferences,
                AllowedThirdPartyAssemblyReferences,
                RequiredThirdPartyAssemblyReferences,
                AllowSharedFramework);
        }
    }

    internal sealed class DependencyDiagnostic
    {
        public DependencyDiagnostic(string code, string from, string to, string detail)
        {
            Code = code;
            From = from;
            To = to;
            Detail = detail;
        }

        public string Code { get; }
        public string From { get; }
        public string To { get; }
        public string Detail { get; }

        public override string ToString()
        {
            return Code + " " + From + " -> " + To + ": " + Detail;
        }
    }

    internal static class DiagnosticCodes
    {
        public const string ProjectMissing = "PROJECT_MISSING";
        public const string ProjectForbiddenEdge = "PROJECT_FORBIDDEN_EDGE";
        public const string ProjectMissingEdge = "PROJECT_MISSING_EDGE";
        public const string ProjectUnknownMahjongReference = "PROJECT_UNKNOWN_MAHJONG_REFERENCE";
        public const string ProjectUnknownMahjongProject = "PROJECT_UNKNOWN_MAHJONG_PROJECT";
        public const string ProjectForbiddenPackage = "PROJECT_FORBIDDEN_PACKAGE";
        public const string ProjectMissingPackage = "PROJECT_MISSING_PACKAGE";
        public const string ProjectBuildOnlyPackageLeaked = "PROJECT_BUILD_ONLY_PACKAGE_LEAKED";
        public const string ProjectNotInRuleTable = "PROJECT_NOT_IN_RULE_TABLE";
        public const string ProjectUnsupportedDeclaration = "PROJECT_UNSUPPORTED_DECLARATION";
        public const string ProjectReferenceTargetNotDiscovered = "PROJECT_REFERENCE_TARGET_NOT_DISCOVERED";
        public const string ProjectReferenceIdentityMismatch = "PROJECT_REFERENCE_IDENTITY_MISMATCH";
        public const string ProjectInvalidReferencePath = "PROJECT_INVALID_REFERENCE_PATH";
        public const string ProjectInvalidProjectPath = "PROJECT_INVALID_PROJECT_PATH";
        public const string ProjectDuplicateCanonicalPath = "PROJECT_DUPLICATE_CANONICAL_PATH";
        public const string AssemblyMissing = "ASSEMBLY_MISSING";
        public const string AssemblyForbiddenMahjongReference = "ASSEMBLY_FORBIDDEN_MAHJONG_REFERENCE";
        public const string AssemblyMissingMahjongReference = "ASSEMBLY_MISSING_MAHJONG_REFERENCE";
        public const string AssemblyUnknownMahjongReference = "ASSEMBLY_UNKNOWN_MAHJONG_REFERENCE";
        public const string AssemblyUnknownMahjongAssembly = "ASSEMBLY_UNKNOWN_MAHJONG_ASSEMBLY";
        public const string AssemblyForbiddenThirdParty = "ASSEMBLY_FORBIDDEN_THIRD_PARTY";
        public const string AssemblyMissingThirdParty = "ASSEMBLY_MISSING_THIRD_PARTY";
        public const string AssemblyUnauthorizedSharedFramework = "ASSEMBLY_UNAUTHORIZED_SHARED_FRAMEWORK";
        public const string AssemblyNotInRuleTable = "ASSEMBLY_NOT_IN_RULE_TABLE";
        public const string RuleInconsistent = "RULE_INCONSISTENT";
    }

    public enum ReferenceKind
    {
        Mahjong,
        Bcl,
        SharedFramework,
        ThirdParty
    }

    /// <summary>
    /// Exact identity catalogue of the real shared frameworks installed next to the
    /// running CLR. Membership is decided by actual framework assembly file names,
    /// never by a System.* / Microsoft.Extensions.* prefix, so a NuGet package such as
    /// System.Reactive or System.IO.Abstractions cannot masquerade as BCL.
    /// </summary>
    internal static class FrameworkAssemblyCatalog
    {
        private static readonly Lazy<HashSet<string>> PlatformNames = new Lazy<HashSet<string>>(LoadPlatform);
        private static readonly Lazy<HashSet<string>> SharedFrameworkNames = new Lazy<HashSet<string>>(LoadSharedFramework);

        public static int PlatformAssemblyCount => PlatformNames.Value.Count;
        public static int SharedFrameworkAssemblyCount => SharedFrameworkNames.Value.Count;

        public static bool IsPlatform(string name)
        {
            return PlatformNames.Value.Contains(name);
        }

        public static bool IsSharedFramework(string name)
        {
            return SharedFrameworkNames.Value.Contains(name);
        }

        private static HashSet<string> LoadPlatform()
        {
            string directory = Path.GetDirectoryName(typeof(object).Assembly.Location)
                ?? throw new InvalidOperationException("Cannot locate the running Microsoft.NETCore.App directory.");
            HashSet<string> names = LoadAssemblyNames(directory);
            if (names.Count == 0)
            {
                throw new InvalidOperationException("No platform assemblies found in " + directory + ".");
            }

            return names;
        }

        private static HashSet<string> LoadSharedFramework()
        {
            string platformDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)
                ?? throw new InvalidOperationException("Cannot locate the running Microsoft.NETCore.App directory.");
            // <dotnet>/shared/Microsoft.NETCore.App/<version> -> <dotnet>/shared
            string sharedRoot = Path.GetFullPath(Path.Combine(platformDirectory, "..", ".."));
            string aspNetCoreRoot = Path.Combine(sharedRoot, "Microsoft.AspNetCore.App");
            if (!Directory.Exists(aspNetCoreRoot))
            {
                throw new InvalidOperationException("Microsoft.AspNetCore.App shared framework not found under " + aspNetCoreRoot + ".");
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string versionDirectory in Directory.GetDirectories(aspNetCoreRoot))
            {
                names.UnionWith(LoadAssemblyNames(versionDirectory));
            }

            if (names.Count == 0)
            {
                throw new InvalidOperationException("No shared-framework assemblies found under " + aspNetCoreRoot + ".");
            }

            return names;
        }

        private static HashSet<string> LoadAssemblyNames(string directory)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in Directory.GetFiles(directory, "*.dll"))
            {
                names.Add(Path.GetFileNameWithoutExtension(file));
            }

            return names;
        }
    }

    internal static class ReferenceClassifier
    {
        public static ReferenceKind Classify(string name)
        {
            if (name.StartsWith("Mahjong.", StringComparison.Ordinal))
            {
                return ReferenceKind.Mahjong;
            }

            if (FrameworkAssemblyCatalog.IsPlatform(name))
            {
                return ReferenceKind.Bcl;
            }

            if (FrameworkAssemblyCatalog.IsSharedFramework(name))
            {
                return ReferenceKind.SharedFramework;
            }

            return ReferenceKind.ThirdParty;
        }
    }

    internal static class ModuleDependencyValidator
    {
        private static readonly StringComparer FilePathComparer =
            Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        public static IReadOnlyList<DependencyDiagnostic> ValidateProjectGraph(
            IReadOnlyCollection<string> knownProjects,
            IReadOnlyDictionary<string, ProjectGraph> projects,
            IReadOnlyDictionary<string, ModuleRule> rules)
        {
            var diagnostics = new List<DependencyDiagnostic>();
            var known = new HashSet<string>(knownProjects, StringComparer.Ordinal);
            Dictionary<string, string> canonicalProjects = BuildCanonicalProjectMap(projects, diagnostics);

            foreach (ModuleRule rule in rules.Values.OrderBy(value => value.Name, StringComparer.Ordinal))
            {
                CheckRuleConsistency(rule, rules, diagnostics);

                if (!projects.TryGetValue(rule.Name, out ProjectGraph? project))
                {
                    diagnostics.Add(new DependencyDiagnostic(
                        DiagnosticCodes.ProjectMissing,
                        rule.Name,
                        "(none)",
                        "rule has no discovered production project"));
                    continue;
                }

                foreach (DeclarationProblem problem in project.DeclarationProblems)
                {
                    diagnostics.Add(new DependencyDiagnostic(
                        DiagnosticCodes.ProjectUnsupportedDeclaration,
                        rule.Name,
                        problem.Element,
                        problem.Detail));
                }

                foreach (ProjectReferenceFact declaration in project.DeclaredProjectReferences)
                {
                    string reference = declaration.TargetName;
                    if (ReferenceClassifier.Classify(reference) == ReferenceKind.Mahjong && !known.Contains(reference))
                    {
                        diagnostics.Add(new DependencyDiagnostic(
                            DiagnosticCodes.ProjectUnknownMahjongReference,
                            rule.Name,
                            reference,
                            "unknown Mahjong project reference (not one of the nine discovered projects)"));
                        continue;
                    }

                    string canonicalReference;
                    try
                    {
                        canonicalReference = Path.GetFullPath(declaration.CanonicalPath);
                    }
                    catch (Exception exception)
                    {
                        diagnostics.Add(new DependencyDiagnostic(
                            DiagnosticCodes.ProjectInvalidReferencePath,
                            rule.Name,
                            declaration.RawInclude,
                            "cannot resolve the declared target path: " + exception.Message));
                        continue;
                    }

                    if (!canonicalProjects.TryGetValue(canonicalReference, out string? resolvedName))
                    {
                        diagnostics.Add(new DependencyDiagnostic(
                            DiagnosticCodes.ProjectReferenceTargetNotDiscovered,
                            rule.Name,
                            declaration.RawInclude,
                            "resolved target '" + canonicalReference +
                            "' is not one of the nine discovered project files; same-basename shadow targets are rejected"));
                        continue;
                    }

                    if (!string.Equals(resolvedName, reference, StringComparison.Ordinal))
                    {
                        diagnostics.Add(new DependencyDiagnostic(
                            DiagnosticCodes.ProjectReferenceIdentityMismatch,
                            rule.Name,
                            declaration.RawInclude,
                            "target path resolves to project '" + resolvedName +
                            "' but the declared file name identifies '" + reference + "'"));
                        continue;
                    }

                    if (!rule.AllowedProjectReferences.Contains(reference))
                    {
                        diagnostics.Add(new DependencyDiagnostic(
                            DiagnosticCodes.ProjectForbiddenEdge,
                            rule.Name,
                            reference,
                            "forbidden declared project edge: " + rule.Name + " -> " + reference));
                    }
                }

                foreach (string expected in rule.AllowedProjectReferences.OrderBy(value => value, StringComparer.Ordinal))
                {
                    if (!project.ProjectReferences.Contains(expected))
                    {
                        diagnostics.Add(new DependencyDiagnostic(
                            DiagnosticCodes.ProjectMissingEdge,
                            rule.Name,
                            expected,
                            "required declared project edge is missing: " + rule.Name + " -> " + expected));
                    }
                }

                foreach (PackageReferenceFact package in project.PackageReferences)
                {
                    if (!rule.AllowedPackages.Contains(package.Name))
                    {
                        diagnostics.Add(new DependencyDiagnostic(
                            DiagnosticCodes.ProjectForbiddenPackage,
                            rule.Name,
                            package.Name,
                            "package is not in this module's exact package allowlist"));
                    }
                    else if (rule.BuildOnlyPackages.Contains(package.Name) &&
                             !string.Equals(package.PrivateAssets, "All", StringComparison.OrdinalIgnoreCase))
                    {
                        diagnostics.Add(new DependencyDiagnostic(
                            DiagnosticCodes.ProjectBuildOnlyPackageLeaked,
                            rule.Name,
                            package.Name,
                            "build-only package must keep PrivateAssets=All so it cannot become a runtime dependency"));
                    }
                }

                foreach (string expected in rule.AllowedPackages.OrderBy(value => value, StringComparer.Ordinal))
                {
                    if (!project.PackageReferences.Any(package => string.Equals(package.Name, expected, StringComparison.Ordinal)))
                    {
                        diagnostics.Add(new DependencyDiagnostic(
                            DiagnosticCodes.ProjectMissingPackage,
                            rule.Name,
                            expected,
                            "required package declaration is missing"));
                    }
                }
            }

            foreach (string projectName in projects.Keys.OrderBy(value => value, StringComparer.Ordinal))
            {
                if (rules.ContainsKey(projectName))
                {
                    continue;
                }

                diagnostics.Add(new DependencyDiagnostic(
                    projectName.StartsWith("Mahjong.", StringComparison.Ordinal)
                        ? DiagnosticCodes.ProjectUnknownMahjongProject
                        : DiagnosticCodes.ProjectNotInRuleTable,
                    projectName,
                    "(rules)",
                    "project exists but is absent from the exact nine-module rule table"));
            }

            return diagnostics;
        }

        public static IReadOnlyList<DependencyDiagnostic> ValidateAssemblyGraph(
            IReadOnlyCollection<string> knownProjects,
            IReadOnlyDictionary<string, AssemblyGraph> assemblies,
            IReadOnlyDictionary<string, ModuleRule> rules)
        {
            var diagnostics = new List<DependencyDiagnostic>();
            var known = new HashSet<string>(knownProjects, StringComparer.Ordinal);

            foreach (ModuleRule rule in rules.Values.OrderBy(value => value.Name, StringComparer.Ordinal))
            {
                CheckRuleConsistency(rule, rules, diagnostics);

                if (!assemblies.TryGetValue(rule.Name, out AssemblyGraph? assembly))
                {
                    diagnostics.Add(new DependencyDiagnostic(
                        DiagnosticCodes.AssemblyMissing,
                        rule.Name,
                        "(none)",
                        "rule has no fresh Release assembly"));
                    continue;
                }

                foreach (string reference in assembly.References)
                {
                    ReferenceKind kind = ReferenceClassifier.Classify(reference);
                    if (kind == ReferenceKind.Mahjong)
                    {
                        if (!known.Contains(reference))
                        {
                            diagnostics.Add(new DependencyDiagnostic(
                                DiagnosticCodes.AssemblyUnknownMahjongReference,
                                rule.Name,
                                reference,
                                "unknown Mahjong assembly reference (not one of the nine discovered projects)"));
                        }
                        else if (!rule.AllowedMahjongAssemblyReferences.Contains(reference))
                        {
                            diagnostics.Add(new DependencyDiagnostic(
                                DiagnosticCodes.AssemblyForbiddenMahjongReference,
                                rule.Name,
                                reference,
                                "forbidden Mahjong assembly reference: " + rule.Name + " -> " + reference));
                        }
                    }
                    else if (kind == ReferenceKind.SharedFramework && !rule.AllowSharedFramework)
                    {
                        diagnostics.Add(new DependencyDiagnostic(
                            DiagnosticCodes.AssemblyUnauthorizedSharedFramework,
                            rule.Name,
                            reference,
                            "shared-framework assembly is not authorized for this module: " + rule.Name + " -> " + reference));
                    }
                    else if (kind == ReferenceKind.ThirdParty &&
                             !rule.AllowedThirdPartyAssemblyReferences.Contains(reference))
                    {
                        diagnostics.Add(new DependencyDiagnostic(
                            DiagnosticCodes.AssemblyForbiddenThirdParty,
                            rule.Name,
                            reference,
                            "third-party assembly reference is not in this module's exact allowlist"));
                    }
                }

                foreach (string required in rule.RequiredMahjongAssemblyReferences.OrderBy(value => value, StringComparer.Ordinal))
                {
                    if (!assembly.References.Any(reference => string.Equals(reference, required, StringComparison.Ordinal)))
                    {
                        diagnostics.Add(new DependencyDiagnostic(
                            DiagnosticCodes.AssemblyMissingMahjongReference,
                            rule.Name,
                            required,
                            "required positive Mahjong assembly edge is missing: " + rule.Name + " -> " + required));
                    }
                }

                foreach (string required in rule.RequiredThirdPartyAssemblyReferences.OrderBy(value => value, StringComparer.Ordinal))
                {
                    if (!assembly.References.Any(reference => string.Equals(reference, required, StringComparison.Ordinal)))
                    {
                        diagnostics.Add(new DependencyDiagnostic(
                            DiagnosticCodes.AssemblyMissingThirdParty,
                            rule.Name,
                            required,
                            "required positive third-party assembly edge is missing: " + rule.Name + " -> " + required));
                    }
                }
            }

            foreach (string assemblyName in assemblies.Keys.OrderBy(value => value, StringComparer.Ordinal))
            {
                if (rules.ContainsKey(assemblyName))
                {
                    continue;
                }

                diagnostics.Add(new DependencyDiagnostic(
                    assemblyName.StartsWith("Mahjong.", StringComparison.Ordinal)
                        ? DiagnosticCodes.AssemblyUnknownMahjongAssembly
                        : DiagnosticCodes.AssemblyNotInRuleTable,
                    assemblyName,
                    "(rules)",
                    "assembly was inspected but is absent from the exact nine-module rule table"));
            }

            return diagnostics;
        }

        private static Dictionary<string, string> BuildCanonicalProjectMap(
            IReadOnlyDictionary<string, ProjectGraph> projects,
            List<DependencyDiagnostic> diagnostics)
        {
            var canonical = new Dictionary<string, string>(FilePathComparer);
            foreach (KeyValuePair<string, ProjectGraph> pair in projects.OrderBy(value => value.Key, StringComparer.Ordinal))
            {
                string fullPath;
                try
                {
                    fullPath = Path.GetFullPath(pair.Value.Path);
                }
                catch (Exception exception)
                {
                    diagnostics.Add(new DependencyDiagnostic(
                        DiagnosticCodes.ProjectInvalidProjectPath,
                        pair.Key,
                        pair.Value.Path,
                        "cannot resolve the project file path: " + exception.Message));
                    continue;
                }

                if (canonical.TryGetValue(fullPath, out string? existing))
                {
                    diagnostics.Add(new DependencyDiagnostic(
                        DiagnosticCodes.ProjectDuplicateCanonicalPath,
                        pair.Key,
                        existing,
                        "two graph nodes resolve to the same project file: " + fullPath));
                    continue;
                }

                canonical.Add(fullPath, pair.Key);
            }

            return canonical;
        }

        private static void CheckRuleConsistency(
            ModuleRule rule,
            IReadOnlyDictionary<string, ModuleRule> rules,
            List<DependencyDiagnostic> diagnostics)
        {
            HashSet<string> projectClosure = ProjectReferenceClosure(rule, rules);
            foreach (string allowed in rule.AllowedMahjongAssemblyReferences)
            {
                if (!projectClosure.Contains(allowed))
                {
                    diagnostics.Add(new DependencyDiagnostic(
                        DiagnosticCodes.RuleInconsistent,
                        rule.Name,
                        allowed,
                        "allowed Mahjong assembly reference is outside the module's allowed project-reference closure"));
                }
            }

            foreach (string required in rule.RequiredMahjongAssemblyReferences)
            {
                if (!rule.AllowedMahjongAssemblyReferences.Contains(required))
                {
                    diagnostics.Add(new DependencyDiagnostic(
                        DiagnosticCodes.RuleInconsistent,
                        rule.Name,
                        required,
                        "required Mahjong assembly reference is not also allowed"));
                }
            }

            foreach (string required in rule.RequiredThirdPartyAssemblyReferences)
            {
                if (!rule.AllowedThirdPartyAssemblyReferences.Contains(required))
                {
                    diagnostics.Add(new DependencyDiagnostic(
                        DiagnosticCodes.RuleInconsistent,
                        rule.Name,
                        required,
                        "required third-party assembly reference is not also allowed"));
                }
            }

            foreach (string buildOnly in rule.BuildOnlyPackages)
            {
                if (!rule.AllowedPackages.Contains(buildOnly))
                {
                    diagnostics.Add(new DependencyDiagnostic(
                        DiagnosticCodes.RuleInconsistent,
                        rule.Name,
                        buildOnly,
                        "build-only package is not also allowed"));
                }
            }
        }

        private static HashSet<string> ProjectReferenceClosure(
            ModuleRule rule,
            IReadOnlyDictionary<string, ModuleRule> rules)
        {
            var closure = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Queue<string>(rule.AllowedProjectReferences);
            while (pending.Count > 0)
            {
                string current = pending.Dequeue();
                if (!closure.Add(current))
                {
                    continue;
                }

                if (rules.TryGetValue(current, out ModuleRule? dependency))
                {
                    foreach (string next in dependency.AllowedProjectReferences)
                    {
                        pending.Enqueue(next);
                    }
                }
            }

            return closure;
        }
    }
}
