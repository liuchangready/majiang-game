using System;
using System.IO;
using Xunit;

namespace Mahjong.LocalHost.Tests
{
    public sealed class P41HarnessArtifactResolutionTests
    {
        [Theory]
        [InlineData("Debug")]
        [InlineData("Release")]
        public void IsolatedArtifactsResolveOnlyTheActiveConfiguration(string configuration)
        {
            using var fixture = new Fixture();
            string testOutput = fixture.CreateTestOutput(configuration);
            string expected = fixture.CreateTargetAssembly(configuration, "fresh-target");

            string actual = P41Harness.FindBuiltProjectAssembly(
                "Mahjong.TableRuntime.Tests", "Mahjong.TableRuntime.Tests.dll", testOutput);

            Assert.Equal(Path.GetFullPath(expected), Path.GetFullPath(actual));
            Assert.Equal("fresh-target", File.ReadAllText(actual));
        }

        [Fact]
        public void MissingIsolatedDependencyDoesNotFallBackToRepositoryReleaseOutput()
        {
            using var fixture = new Fixture();
            string testOutput = fixture.CreateTestOutput("Debug");
            string stale = Path.Combine(fixture.Root, "dotnet", "tests", "Mahjong.TableRuntime.Tests",
                "bin", "Release", "net8.0", "Mahjong.TableRuntime.Tests.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(stale)!);
            File.WriteAllText(stale, "stale-repository-release");

            var error = Assert.Throws<FileNotFoundException>(() =>
                P41Harness.FindBuiltProjectAssembly(
                    "Mahjong.TableRuntime.Tests", "Mahjong.TableRuntime.Tests.dll", testOutput));

            Assert.Contains(Path.Combine("artifacts", "bin", "Mahjong.TableRuntime.Tests", "Debug"),
                error.FileName ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        private sealed class Fixture : IDisposable
        {
            private readonly string _path;

            public Fixture()
            {
                string repo = P41Harness.FindRepositoryRoot();
                _path = Path.Combine(repo, "build", "p41-artifact-resolution-" + Guid.NewGuid().ToString("N"));
                Root = Path.Combine(_path, "repo");
                Directory.CreateDirectory(Path.Combine(Root, "dotnet"));
                File.WriteAllText(Path.Combine(Root, "dotnet", "Mahjong.sln"), "fixture");
            }

            public string Root { get; }

            public string CreateTestOutput(string configuration)
            {
                string output = Path.Combine(Root, "artifacts", "bin", "Mahjong.LocalHost.Tests", configuration);
                Directory.CreateDirectory(output);
                return output;
            }

            public string CreateTargetAssembly(string configuration, string contents)
            {
                string path = Path.Combine(Root, "artifacts", "bin", "Mahjong.TableRuntime.Tests",
                    configuration, "Mahjong.TableRuntime.Tests.dll");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, contents);
                return path;
            }

            public void Dispose()
            {
                if (Directory.Exists(_path)) Directory.Delete(_path, recursive: true);
            }
        }
    }
}
