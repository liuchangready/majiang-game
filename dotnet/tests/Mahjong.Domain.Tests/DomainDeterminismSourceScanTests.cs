using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Mahjong.Domain.Tests
{
    /// <summary>
    /// V5-1-REVIEW §9 S-2（I-4 落地形）：Mahjong.Domain 生产源码的确定性 source-scan 守护。
    ///
    /// 本文件不修改 Domain 生产代码，只做词法级事实扫描：
    /// - 枚举 dotnet/src/Mahjong.Domain 下全部真实 .cs，排除任何 bin/obj 段；
    /// - 零文件、目录缺失、仓库根定位失败一律硬失败，绝不以 total=0 表示 PASS；
    /// - 注释与字符串/字符字面量先被等价长度遮蔽，仍保留原行号/列号；
    /// - 每组禁用符号覆盖普通、全限定、global:: 及跨行空白形式；
    /// - 自检调用真实扫描函数，不比对实现里的正则字符串；
    /// - 行为灵敏度：复制真实生产源到进程 TEMP，注入 System.DateTime.UtcNow 后必须
    ///   以 文件:行:列 + 符号 诊断失败，且原仓库文件哈希不变。
    ///
    /// 明确边界：这是小型词法守卫，不是 C# 解析器/IL 检查器。不展开 using 别名、
    /// 不分析反射/动态调用、不追踪跨模块数据流、不扫描插值字符串孔洞；浮点只证明
    /// 已接受的 ShuffleConfig 调参不被误报，不宣称覆盖全部浮点权威数据流。
    /// </summary>
    public class DomainDeterminismSourceScanTests
    {
        private const string OwnedTempPrefix = "mj-domain-determinism-";
        private const int MinimumExpectedDomainFiles = 40;

        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        [Fact]
        public void RealDomainProductionSourcesContainNoImplicitAuthorityApis()
        {
            string repositoryRoot = DomainDeterminismScan.LocateRepositoryRoot(AppContext.BaseDirectory);
            ScanReport report = DomainDeterminismScan.ScanRepository(repositoryRoot);

            Assert.True(
                report.FilesScanned >= MinimumExpectedDomainFiles,
                "Domain source enumeration returned only " + report.FilesScanned +
                " files; expected at least " + MinimumExpectedDomainFiles +
                ". A truncated/empty enumeration must not look like a clean scan.");
            Assert.True(
                report.Violations.Count == 0,
                "Domain determinism source scan found implicit authority APIs:" + Environment.NewLine +
                report.FormatDiagnostics());

            string domainRoot = Path.Combine(repositoryRoot, "dotnet", "src", "Mahjong.Domain");
            int independentlyCounted = Directory
                .GetFiles(domainRoot, "*.cs", SearchOption.AllDirectories)
                .Count(path => !DomainDeterminismScan.HasGeneratedPathSegment(domainRoot, path));
            Assert.Equal(independentlyCounted, report.FilesScanned);
        }

        [Fact]
        public void EveryForbiddenGroupFailsWithFileLineColumnAndSymbol()
        {
            // 每一行都是独立禁用组/写法；任何一行漏报都会让本 Fact 失败并列出漏报项。
            (string Name, string Source, string Symbol)[] cases =
            {
                ("new Random", "class Probe { object M() => new Random(1); }", "new Random"),
                ("new System.Random", "class Probe { object M() => new System.Random(1); }", "new Random"),
                ("new global::System.Random", "class Probe { object M() => new global::System.Random(1); }", "new Random"),
                ("new newline Random", "class Probe { object M() => new\n    Random(1); }", "new Random"),
                ("DateTime.Now", "class Probe { object M() => DateTime.Now; }", "DateTime.Now"),
                ("System.DateTime.UtcNow", "class Probe { object M() => System.DateTime.UtcNow; }", "DateTime.UtcNow"),
                ("global::System.DateTime.Now", "class Probe { object M() => global::System.DateTime.Now; }", "DateTime.Now"),
                ("DateTime newline dot Now", "class Probe { object M() => DateTime\n    .Now; }", "DateTime.Now"),
                ("DateTimeOffset.Now", "class Probe { object M() => DateTimeOffset.Now; }", "DateTimeOffset.Now"),
                ("System.DateTimeOffset.UtcNow", "class Probe { object M() => System.DateTimeOffset.UtcNow; }", "DateTimeOffset.UtcNow"),
                ("global::System.DateTimeOffset.Now", "class Probe { object M() => global::System.DateTimeOffset.Now; }", "DateTimeOffset.Now"),
                ("Guid.NewGuid", "class Probe { object M() => Guid.NewGuid(); }", "Guid.NewGuid"),
                ("System.Guid.NewGuid", "class Probe { object M() => System.Guid.NewGuid(); }", "Guid.NewGuid"),
                ("global::System.Guid.NewGuid", "class Probe { object M() => global::System.Guid.NewGuid(); }", "Guid.NewGuid"),
                ("Stopwatch.StartNew", "class Probe { object M() => Stopwatch.StartNew(); }", "Stopwatch"),
                ("System.Diagnostics.Stopwatch", "class Probe { object M() => new System.Diagnostics.Stopwatch(); }", "Stopwatch"),
                ("global::System.Diagnostics.Stopwatch", "class Probe { object M() => global::System.Diagnostics.Stopwatch.StartNew(); }", "Stopwatch"),
                ("Environment.TickCount", "class Probe { int M() => Environment.TickCount; }", "Environment.TickCount"),
                ("System.Environment.TickCount64", "class Probe { long M() => System.Environment.TickCount64; }", "Environment.TickCount"),
                ("Thread.Sleep", "class Probe { void M() => Thread.Sleep(1); }", "Thread.Sleep"),
                ("System.Threading.Thread.Sleep", "class Probe { void M() => System.Threading.Thread.Sleep(1); }", "Thread.Sleep"),
                ("Task.Delay", "class Probe { object M() => Task.Delay(1); }", "Task.Delay"),
                ("System.Threading.Tasks.Task.Delay", "class Probe { object M() => System.Threading.Tasks.Task.Delay(1); }", "Task.Delay"),
                ("Math.Random", "class Probe { double M() => Math.Random(); }", "Math.Random"),
                ("System.Math.Random", "class Probe { double M() => System.Math.Random(); }", "Math.Random"),
                ("Random.Shared", "class Probe { int M() => Random.Shared.Next(); }", "Random.Shared"),
                ("System.Random.Shared", "class Probe { int M() => System.Random.Shared.Next(); }", "Random.Shared")
            };

            var failures = new List<string>();
            foreach ((string name, string source, string expectedSymbol) in cases)
            {
                using TempDomainTree tree = TempDomainTree.Create();
                tree.WriteDomain("Probe.cs", source);
                ScanReport report = DomainDeterminismScan.ScanDomainDirectory(tree.DomainRoot);

                if (report.Violations.Count == 0)
                {
                    failures.Add(name + ": scanner did not fail");
                    continue;
                }

                DomainDeterminismViolation violation = report.Violations[0];
                if (!string.Equals(expectedSymbol, violation.Symbol, StringComparison.Ordinal))
                {
                    failures.Add(name + ": symbol=" + violation.Symbol + ", expected " + expectedSymbol);
                }

                if (!string.Equals("Probe.cs", violation.File, StringComparison.Ordinal))
                {
                    failures.Add(name + ": file=" + violation.File + ", expected Probe.cs");
                }

                if (violation.Line < 1 || violation.Column < 1)
                {
                    failures.Add(name + ": invalid position " + violation.Line + ":" + violation.Column);
                }

                string diagnostic = violation.ToString();
                if (!diagnostic.Contains("Probe.cs:" + violation.Line + ":" + violation.Column, StringComparison.Ordinal) ||
                    !diagnostic.Contains("symbol=" + expectedSymbol, StringComparison.Ordinal))
                {
                    failures.Add(name + ": diagnostic lacks file:line:column/symbol -> " + diagnostic);
                }
            }

            Assert.True(failures.Count == 0, "forbidden-groups self-check failures:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
        }

        [Fact]
        public void CommentsStringsAndLegalLookalikesAreNotFlagged()
        {
            string source = string.Join(
                "\n",
                "class Probe",
                "{",
                "    // DateTime.Now new Random() Guid.NewGuid() Stopwatch.StartNew()",
                "    /* System.DateTime.UtcNow",
                "       System.Threading.Thread.Sleep(1) */",
                "    string a = \"DateTime.Now\";",
                "    string b = @\"Guid.NewGuid()\";",
                "    string c = \"new Random(1)\";",
                "    string d = \"escaped \\\" DateTime.UtcNow\";",
                "    char quote = '\"';",
                "    int RecordedRandomDraw = 1;",
                "    int RandomDrawFactory = 2;",
                "    int MyStopwatch = 3;",
                "    int StopwatchFactory = 4;",
                "    int TaskDelayed = 5;",
                "    int ThreadSleeper = 6;",
                "    int Guidance = 7;",
                "    int DateTimeOffsetValue = 8;",
                "    object persisted = new PersistedUtcTime(new DateTimeOffset(ticks, TimeSpan.Zero));",
                "    long storedTicks = persisted.Value.UtcDateTime.Ticks;",
                "    DateTimeOffset fromUnix = DateTimeOffset.FromUnixTimeMilliseconds(0);",
                "    DateTimeOffset fromSeconds = DateTimeOffset.FromUnixTimeSeconds(0);",
                "    Guid empty = Guid.Empty;",
                "    object completed = Task.CompletedTask;",
                "    double strength = 0.75;",
                "}");

            using TempDomainTree tree = TempDomainTree.Create();
            tree.WriteDomain("Probe.cs", source);
            ScanReport report = DomainDeterminismScan.ScanDomainDirectory(tree.DomainRoot);

            Assert.True(
                report.Violations.Count == 0,
                "comments/strings/legal lookalikes were flagged:" + Environment.NewLine + report.FormatDiagnostics());

            // 防止“所有行都被遮蔽”式假绿：同一扫描器在去掉遮蔽后必须仍能命中真实代码。
            ScanReport positive = DomainDeterminismScan.ScanDomainDirectory(tree.DomainRoot, sampleText: "class P { object M() => DateTime.Now; }");
            Assert.Single(positive.Violations);
            Assert.Equal("DateTime.Now", positive.Violations[0].Symbol);
        }

        [Fact]
        public void GeneratedFoldersAreExcludedAndVacuousScansFailClosed()
        {
            using TempDomainTree tree = TempDomainTree.Create();
            tree.WriteDomain("Clean.cs", "class Clean { }");
            tree.WriteDomain("bin/Generated.cs", "class BadBin { object M() => DateTime.Now; }");
            tree.WriteDomain("obj/Debug/Generated.cs", "class BadObj { object M() => Guid.NewGuid(); }");
            tree.WriteDomain("nested/bin/Generated.cs", "class BadNested { void M() => Thread.Sleep(1); }");

            ScanReport report = DomainDeterminismScan.ScanDomainDirectory(tree.DomainRoot);
            Assert.Equal(1, report.FilesScanned);
            Assert.Empty(report.Violations);

            using TempDomainTree empty = TempDomainTree.Create();
            Directory.CreateDirectory(empty.DomainRoot);
            Assert.Throws<InvalidOperationException>(() => DomainDeterminismScan.ScanDomainDirectory(empty.DomainRoot));
            Assert.Throws<InvalidOperationException>(() => DomainDeterminismScan.ScanDomainDirectory(Path.Combine(empty.Root, "missing")));
            Assert.Throws<ArgumentException>(() => DomainDeterminismScan.ScanDomainDirectory("  "));
        }

        [Fact]
        public void RepositoryRootAndEnumerationFailuresAreTerminal()
        {
            string repositoryRoot = DomainDeterminismScan.LocateRepositoryRoot(AppContext.BaseDirectory);
            Assert.True(File.Exists(Path.Combine(repositoryRoot, "AGENTS.md")));
            Assert.True(Directory.Exists(Path.Combine(repositoryRoot, "dotnet", "src", "Mahjong.Domain")));

            Assert.Throws<ArgumentException>(() => DomainDeterminismScan.LocateRepositoryRoot(""));
            Assert.Throws<ArgumentException>(() => DomainDeterminismScan.LocateRepositoryRoot("   "));
            Assert.Throws<System.IO.DirectoryNotFoundException>(
                () => DomainDeterminismScan.LocateRepositoryRoot(Path.Combine(Path.GetTempPath(), OwnedTempPrefix + "does-not-exist")));

            using TempDomainTree tree = TempDomainTree.Create();
            Assert.Throws<InvalidOperationException>(
                () => DomainDeterminismScan.LocateRepositoryRoot(tree.Root, _ => false));
        }

        [Fact]
        public void DiagnosticsCarryExactSourceLinesForMultipleViolations()
        {
            string source = string.Join(
                "\n",
                "class Probe",
                "{",
                "    void M()",
                "    {",
                "        var now = DateTime.Now;",
                "        var id = System.Guid.NewGuid();",
                "    }",
                "}");

            using TempDomainTree tree = TempDomainTree.Create();
            tree.WriteDomain("Sample.cs", source);
            ScanReport report = DomainDeterminismScan.ScanDomainDirectory(tree.DomainRoot);

            Assert.Equal(2, report.Violations.Count);
            Assert.Equal(5, report.Violations[0].Line);
            Assert.Equal("DateTime.Now", report.Violations[0].Symbol);
            Assert.Equal(6, report.Violations[1].Line);
            Assert.Equal("Guid.NewGuid", report.Violations[1].Symbol);
            Assert.All(report.Violations, v => Assert.True(v.Column > 0));

            string diagnostics = report.FormatDiagnostics();
            Assert.Contains("Sample.cs:5:", diagnostics, StringComparison.Ordinal);
            Assert.Contains("Sample.cs:6:", diagnostics, StringComparison.Ordinal);
            Assert.Contains("symbol=DateTime.Now", diagnostics, StringComparison.Ordinal);
            Assert.Contains("symbol=Guid.NewGuid", diagnostics, StringComparison.Ordinal);
        }

        [Fact]
        public void InjectedImplicitApiIntoProductionCopyFailsAndLeavesRepositoryUntouched()
        {
            string repositoryRoot = DomainDeterminismScan.LocateRepositoryRoot(AppContext.BaseDirectory);
            string productionRoot = Path.Combine(repositoryRoot, "dotnet", "src", "Mahjong.Domain");
            string originalPath = Path.Combine(productionRoot, "DecisionClock.cs");
            string originalText = File.ReadAllText(originalPath, StrictUtf8);
            string originalHash = Sha256Hex(originalText);

            using TempDomainTree tree = TempDomainTree.Create();
            IReadOnlyList<string> sources = DomainDeterminismScan.EnumerateDomainSources(productionRoot);
            foreach (string sourceFile in sources)
            {
                string relative = Path.GetRelativePath(productionRoot, sourceFile);
                tree.Write(Path.Combine("dotnet", "src", "Mahjong.Domain", relative), File.ReadAllText(sourceFile, StrictUtf8));
            }

            // 先证明副本本身是干净基线，避免把“复制时已污染”误当注入命中。
            ScanReport baseline = DomainDeterminismScan.ScanDomainDirectory(tree.DomainRoot);
            Assert.Empty(baseline.Violations);
            Assert.Equal(sources.Count, baseline.FilesScanned);

            // 测试目录里的违例不得被当作 Domain 生产源码扫描。
            tree.Write(
                Path.Combine("dotnet", "tests", "Mahjong.Domain.Tests", "IgnoredProbe.cs"),
                "class IgnoredProbe { object M() => System.DateTime.Now; }");

            string injectedLine = "        internal static System.DateTime InjectedWallClock() => System.DateTime.UtcNow;";
            int newlinesBeforeInjection = originalText.Count(ch => ch == '\n');
            File.AppendAllText(tree.DomainRootFile("DecisionClock.cs"), "\n" + injectedLine + "\n", Utf8NoBom);

            ScanReport injected = DomainDeterminismScan.ScanDomainDirectory(tree.DomainRoot);
            Assert.Single(injected.Violations);
            DomainDeterminismViolation violation = injected.Violations[0];
            Assert.Equal("DecisionClock.cs", violation.File);
            Assert.Equal(newlinesBeforeInjection + 2, violation.Line);
            Assert.Equal("DateTime.UtcNow", violation.Symbol);
            Assert.Equal("DecisionClock.cs:" + violation.Line + ":" + violation.Column, violation.Location);
            Assert.Contains("System.DateTime.UtcNow", violation.SourceText, StringComparison.Ordinal);

            string injectedDiagnostics = injected.FormatDiagnostics();
            Assert.Contains("DecisionClock.cs:" + violation.Line + ":" + violation.Column, injectedDiagnostics, StringComparison.Ordinal);
            Assert.Contains("symbol=DateTime.UtcNow", injectedDiagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain("IgnoredProbe", injectedDiagnostics, StringComparison.Ordinal);

            // 原仓库源文件未被改写（测试全程只在进程 TEMP 的副本上操作）。
            Assert.Equal(originalHash, Sha256Hex(File.ReadAllText(originalPath, StrictUtf8)));
        }

        private static string Sha256Hex(string text)
        {
            using SHA256 sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(StrictUtf8.GetBytes(text)));
        }

        private sealed class TempDomainTree : IDisposable
        {
            private TempDomainTree(string root)
            {
                Root = root;
                DomainRoot = Path.Combine(root, "dotnet", "src", "Mahjong.Domain");
            }

            public string Root { get; }

            public string DomainRoot { get; }

            public static TempDomainTree Create()
            {
                string root = Path.Combine(Path.GetTempPath(), OwnedTempPrefix + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                return new TempDomainTree(root);
            }

            public void Write(string relativePath, string content)
            {
                string target = ResolveInsideRoot(relativePath);
                WriteAbsolute(target, content);
            }

            public void WriteDomain(string relativePath, string content)
            {
                Write(Path.Combine("dotnet", "src", "Mahjong.Domain", relativePath), content);
            }

            private static void WriteAbsolute(string target, string content)
            {
                string? directory = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(target, content, Utf8NoBom);
            }

            public string DomainRootFile(string relativePath) => ResolveInsideRoot(Path.Combine("dotnet", "src", "Mahjong.Domain", relativePath));

            public void Dispose()
            {
                DeleteOwnedRoot(Root);
            }

            private string ResolveInsideRoot(string relativePath)
            {
                string rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Root));
                string target = Path.GetFullPath(Path.Combine(rootFull, relativePath));
                if (!target.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Refusing to write outside owned temp root: " + target);
                }

                return target;
            }

            private static void DeleteOwnedRoot(string root)
            {
                string tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
                string candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

                if (string.Equals(candidate, tempRoot, StringComparison.OrdinalIgnoreCase) ||
                    !candidate.StartsWith(tempRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    !Path.GetFileName(candidate).StartsWith(OwnedTempPrefix, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Refusing recursive delete outside owned temp root: " + candidate);
                }

                if (Directory.Exists(candidate))
                {
                    Directory.Delete(candidate, recursive: true);
                }
            }
        }
    }

    internal sealed class DomainDeterminismViolation
    {
        public DomainDeterminismViolation(string file, int line, int column, string symbol, string sourceText)
        {
            File = file;
            Line = line;
            Column = column;
            Symbol = symbol;
            SourceText = sourceText;
        }

        public string File { get; }

        public int Line { get; }

        public int Column { get; }

        public string Symbol { get; }

        public string SourceText { get; }

        public string Location => File + ":" + Line + ":" + Column;

        public override string ToString() =>
            "DOMAIN-DETERMINISM-VIOLATION " + Location + " symbol=" + Symbol + " source=" + SourceText;
    }

    internal sealed class ScanReport
    {
        public ScanReport(int filesScanned, IReadOnlyList<DomainDeterminismViolation> violations)
        {
            FilesScanned = filesScanned;
            Violations = violations;
        }

        public int FilesScanned { get; }

        public IReadOnlyList<DomainDeterminismViolation> Violations { get; }

        public string FormatDiagnostics()
        {
            if (Violations.Count == 0)
            {
                return "DOMAIN-DETERMINISM-PASS files=" + FilesScanned + " violations=0";
            }

            return "DOMAIN-DETERMINISM-FAIL files=" + FilesScanned + " violations=" + Violations.Count +
                   Environment.NewLine +
                   string.Join(Environment.NewLine, Violations.Select(v => "  " + v));
        }
    }

    /// <summary>
    /// 真实扫描函数。测试通过本类执行，不重复实现正则匹配。
    /// </summary>
    internal static class DomainDeterminismScan
    {
        private const string DomainRelative = "dotnet/src/Mahjong.Domain";

        private static readonly ForbiddenPattern[] Patterns = BuildPatterns();

        public static string LocateRepositoryRoot(string startDirectory) =>
            LocateRepositoryRoot(startDirectory, IsRepositoryRoot);

        public static string LocateRepositoryRoot(string startDirectory, Func<string, bool> isRepositoryRoot)
        {
            if (string.IsNullOrWhiteSpace(startDirectory))
            {
                throw new ArgumentException("startDirectory must not be blank.", nameof(startDirectory));
            }

            if (isRepositoryRoot == null)
            {
                throw new ArgumentNullException(nameof(isRepositoryRoot));
            }

            string fullStart = Path.GetFullPath(startDirectory);
            if (!Directory.Exists(fullStart))
            {
                throw new DirectoryNotFoundException("Repository-root search start directory does not exist: " + fullStart);
            }

            var current = new DirectoryInfo(fullStart);
            while (current != null)
            {
                if (isRepositoryRoot(current.FullName))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new InvalidOperationException(
                "Could not locate the repository root above '" + fullStart +
                "' (expected AGENTS.md, dotnet/Mahjong.sln and dotnet/src/Mahjong.Domain).");
        }

        public static ScanReport ScanRepository(string repositoryRoot)
        {
            if (string.IsNullOrWhiteSpace(repositoryRoot))
            {
                throw new ArgumentException("repositoryRoot must not be blank.", nameof(repositoryRoot));
            }

            string domainRoot = Path.Combine(Path.GetFullPath(repositoryRoot), "dotnet", "src", "Mahjong.Domain");
            return ScanDomainDirectory(domainRoot);
        }

        public static ScanReport ScanDomainDirectory(string domainDirectory) => ScanDomainDirectory(domainDirectory, null);

        public static ScanReport ScanDomainDirectory(string domainDirectory, string? sampleText)
        {
            string fullDomain = ResolveDomainDirectory(domainDirectory);
            if (sampleText != null)
            {
                return ScanText("<inline-sample>", sampleText);
            }

            IReadOnlyList<string> files = EnumerateDomainSources(fullDomain);
            var violations = new List<DomainDeterminismViolation>();

            foreach (string file in files)
            {
                string text = File.ReadAllText(file, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true));
                string relative = Path.GetRelativePath(fullDomain, file).Replace('\\', '/');
                violations.AddRange(ScanSource(relative, text).Violations);
            }

            violations.Sort((left, right) =>
            {
                int byFile = string.CompareOrdinal(left.File, right.File);
                if (byFile != 0)
                {
                    return byFile;
                }

                int byLine = left.Line.CompareTo(right.Line);
                return byLine != 0 ? byLine : left.Column.CompareTo(right.Column);
            });

            return new ScanReport(files.Count, violations);
        }

        public static IReadOnlyList<string> EnumerateDomainSources(string domainDirectory)
        {
            string fullDomain = ResolveDomainDirectory(domainDirectory);
            string[] files = Directory.GetFiles(fullDomain, "*.cs", SearchOption.AllDirectories);
            var production = files
                .Where(path => !HasGeneratedPathSegment(fullDomain, path))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            if (production.Count == 0)
            {
                throw new InvalidOperationException(
                    "No production .cs files found under '" + fullDomain +
                    "' after excluding bin/obj; refusing to turn an empty enumeration into PASS.");
            }

            foreach (string file in production)
            {
                string fullFile = Path.GetFullPath(file);
                if (!fullFile.StartsWith(fullDomain + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Enumerated source escaped the Domain root: " + fullFile);
                }
            }

            return production;
        }

        public static bool HasGeneratedPathSegment(string domainDirectory, string file)
        {
            string relative = Path.GetRelativePath(Path.GetFullPath(domainDirectory), Path.GetFullPath(file));
            string[] segments = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            return segments.Any(segment =>
                string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase));
        }

        private static string ResolveDomainDirectory(string domainDirectory)
        {
            if (string.IsNullOrWhiteSpace(domainDirectory))
            {
                throw new ArgumentException("domainDirectory must not be blank.", nameof(domainDirectory));
            }

            string fullDomain = Path.TrimEndingDirectorySeparator(Path.GetFullPath(domainDirectory));
            if (!Directory.Exists(fullDomain))
            {
                throw new InvalidOperationException("Domain source directory does not exist: " + fullDomain);
            }

            return fullDomain;
        }

        private static bool IsRepositoryRoot(string directory) =>
            File.Exists(Path.Combine(directory, "AGENTS.md")) &&
            File.Exists(Path.Combine(directory, "dotnet", "Mahjong.sln")) &&
            Directory.Exists(Path.Combine(directory, "dotnet", "src", "Mahjong.Domain"));

        private static ScanReport ScanText(string fileName, string text)
        {
            var violations = ScanSource(fileName, text).Violations;
            return new ScanReport(1, violations);
        }

        private static (IReadOnlyList<DomainDeterminismViolation> Violations, string MaskedText) ScanSource(string fileName, string source)
        {
            string masked = MaskCommentsAndLiterals(source);
            var violations = new List<DomainDeterminismViolation>();

            foreach (ForbiddenPattern pattern in Patterns)
            {
                foreach (Match match in pattern.Regex.Matches(masked))
                {
                    (int line, int column) = ResolvePosition(source, match.Index);
                    violations.Add(new DomainDeterminismViolation(
                        fileName,
                        line,
                        column,
                        pattern.Symbol,
                        ExtractLine(source, match.Index)));
                }
            }

            return (violations, masked);
        }

        private static (int Line, int Column) ResolvePosition(string text, int index)
        {
            int line = 1;
            int lineStart = 0;
            int limit = Math.Min(index, text.Length);
            for (int i = 0; i < limit; i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                    lineStart = i + 1;
                }
            }

            return (line, index - lineStart + 1);
        }

        private static string ExtractLine(string text, int index)
        {
            int safeIndex = Math.Min(index, Math.Max(0, text.Length - 1));
            int lineStart = safeIndex == 0 ? 0 : text.LastIndexOf('\n', safeIndex - 1) + 1;
            int lineEnd = text.IndexOf('\n', safeIndex);
            if (lineEnd < 0)
            {
                lineEnd = text.Length;
            }

            string snippet = text.Substring(lineStart, lineEnd - lineStart).Trim();
            return snippet.Length <= 240 ? snippet : snippet.Substring(0, 240) + "...";
        }

        /// <summary>
        /// 把注释、字符串、字符字面量替换为等长空格（保留 \r/\n），使正则只看到代码 token。
        /// 能力边界：不解析插值字符串孔洞，不展开 #if，不做 C# 语义分析。
        /// </summary>
        private static string MaskCommentsAndLiterals(string source)
        {
            char[] masked = source.ToCharArray();
            int i = 0;
            while (i < source.Length)
            {
                char current = source[i];
                char next = i + 1 < source.Length ? source[i + 1] : '\0';

                if (current == '/' && next == '/')
                {
                    int start = i;
                    while (i < source.Length && source[i] != '\n' && source[i] != '\r')
                    {
                        i++;
                    }

                    MaskRange(masked, start, i);
                }
                else if (current == '/' && next == '*')
                {
                    int start = i;
                    i += 2;
                    while (i < source.Length)
                    {
                        if (source[i] == '*' && i + 1 < source.Length && source[i + 1] == '/')
                        {
                            i += 2;
                            break;
                        }

                        i++;
                    }

                    MaskRange(masked, start, i);
                }
                else if (current == '@' && next == '"')
                {
                    MaskVerbatimString(masked, source, ref i, prefixLength: 1);
                }
                else if (current == '$' && next == '@' && i + 2 < source.Length && source[i + 2] == '"')
                {
                    MaskVerbatimString(masked, source, ref i, prefixLength: 2);
                }
                else if (current == '@' && next == '$' && i + 2 < source.Length && source[i + 2] == '"')
                {
                    MaskVerbatimString(masked, source, ref i, prefixLength: 2);
                }
                else if (current == '"' || (current == '$' && next == '"'))
                {
                    MaskRegularString(masked, source, ref i, prefixLength: current == '$' ? 1 : 0);
                }
                else if (current == '\'')
                {
                    MaskCharLiteral(masked, source, ref i);
                }
                else
                {
                    i++;
                }
            }

            return new string(masked);
        }

        private static void MaskRegularString(char[] masked, string source, ref int i, int prefixLength)
        {
            int start = i;
            i += prefixLength;
            if (i < source.Length && source[i] == '"')
            {
                i++;
            }

            while (i < source.Length)
            {
                if (source[i] == '\\')
                {
                    i = Math.Min(source.Length, i + 2);
                    continue;
                }

                if (source[i] == '"')
                {
                    i++;
                    break;
                }

                i++;
            }

            MaskRange(masked, start, i);
        }

        private static void MaskVerbatimString(char[] masked, string source, ref int i, int prefixLength)
        {
            int start = i;
            i += prefixLength;
            if (i < source.Length && source[i] == '"')
            {
                i++;
            }

            while (i < source.Length)
            {
                if (source[i] == '"' && i + 1 < source.Length && source[i + 1] == '"')
                {
                    i += 2;
                    continue;
                }

                if (source[i] == '"')
                {
                    i++;
                    break;
                }

                i++;
            }

            MaskRange(masked, start, i);
        }

        private static void MaskCharLiteral(char[] masked, string source, ref int i)
        {
            int start = i;
            i++;
            while (i < source.Length)
            {
                if (source[i] == '\\')
                {
                    i = Math.Min(source.Length, i + 2);
                    continue;
                }

                if (source[i] == '\'')
                {
                    i++;
                    break;
                }

                i++;
            }

            MaskRange(masked, start, i);
        }

        private static void MaskRange(char[] masked, int start, int endExclusive)
        {
            int end = Math.Min(endExclusive, masked.Length);
            for (int index = start; index < end; index++)
            {
                if (masked[index] != '\r' && masked[index] != '\n')
                {
                    masked[index] = ' ';
                }
            }
        }

        private static ForbiddenPattern[] BuildPatterns()
        {
            const RegexOptions Options = RegexOptions.Compiled | RegexOptions.CultureInvariant;
            const string Global = @"(?:global\s*::\s*)?";
            const string System = @"(?:System\s*\.\s*)?";

            return new[]
            {
                Pattern("new Random", @"(?<![\w@])new\s+" + Global + System + @"Random\s*\(", Options),
                Pattern("DateTime.Now", @"(?<![\w@])" + Global + System + @"DateTime\s*\.\s*Now\b", Options),
                Pattern("DateTime.UtcNow", @"(?<![\w@])" + Global + System + @"DateTime\s*\.\s*UtcNow\b", Options),
                Pattern("DateTimeOffset.Now", @"(?<![\w@])" + Global + System + @"DateTimeOffset\s*\.\s*Now\b", Options),
                Pattern("DateTimeOffset.UtcNow", @"(?<![\w@])" + Global + System + @"DateTimeOffset\s*\.\s*UtcNow\b", Options),
                Pattern("Guid.NewGuid", @"(?<![\w@])" + Global + System + @"Guid\s*\.\s*NewGuid\b", Options),
                Pattern("Stopwatch", @"(?<![\w@])" + Global + System + @"(?:Diagnostics\s*\.\s*)?Stopwatch\b", Options),
                Pattern("Environment.TickCount", @"(?<![\w@])" + Global + System + @"Environment\s*\.\s*TickCount(?:64)?\b", Options),
                Pattern("Thread.Sleep", @"(?<![\w@])" + Global + System + @"(?:Threading\s*\.\s*)?Thread\s*\.\s*Sleep\b", Options),
                Pattern("Task.Delay", @"(?<![\w@])" + Global + System + @"(?:Threading\s*\.\s*)?(?:Tasks\s*\.\s*)?Task\s*\.\s*Delay\b", Options),
                Pattern("Math.Random", @"(?<![\w@])" + Global + System + @"Math\s*\.\s*Random\b", Options),
                Pattern("Random.Shared", @"(?<![\w@])" + Global + System + @"Random\s*\.\s*Shared\b", Options)
            };
        }

        private static ForbiddenPattern Pattern(string symbol, string regex, RegexOptions options) =>
            new ForbiddenPattern(symbol, new Regex(regex, options));

        private sealed class ForbiddenPattern
        {
            public ForbiddenPattern(string symbol, Regex regex)
            {
                Symbol = symbol;
                Regex = regex;
            }

            public string Symbol { get; }

            public Regex Regex { get; }
        }
    }
}
