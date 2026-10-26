using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Mahjong.Domain;
using Mahjong.Domain.Stacking;
using Xunit;

namespace Mahjong.Domain.Tests
{
    /// <summary>
    /// 读取 V4 测试导出器真实运行产出的 shuffle-v1.json，并与 C# Domain 逐值对账。
    ///
    /// 差距声明（文件 rngGapNote / 开放决策 D-09）：V4 墙来自 JVM Random(seed)，
    /// 不是跨运行时可重放规范，因此本文件绝不断言 C# 能逐字节复现 V4 墙；
    /// 断言分两类：
    /// 1. 纯数学逐值一致：槽位矩阵、需求向量、重叠度矩阵（双精度确定）；
    /// 2. 结构法律一致：导出的真实 V4 墙满足 136 张唯一、赤五恒 3、
    ///    四人/无目标/零额度恒等、死山 122..135 隔离、排序无关标记。
    /// 所有期望值来自 V4 真实运行的导出物，不以 C# 实现为 oracle。
    /// </summary>
    public sealed class ShuffleGoldenTests
    {
        private static readonly Lazy<JsonDocument> Golden = new Lazy<JsonDocument>(LoadGolden);

        [Fact]
        public void GoldenFileCarriesRealV4Provenance()
        {
            JsonElement root = Golden.Value.RootElement;
            Assert.Equal("v5-shuffle-golden-v1", root.GetProperty("schema").GetString());
            Assert.Equal(1, root.GetProperty("generatorVersion").GetInt32());
            Assert.Equal(1, root.GetProperty("rulesetVersion").GetInt32());
            Assert.Equal("exported", root.GetProperty("extractionStatus").GetString());

            string commit = root.GetProperty("v4Commit").GetString()!;
            Assert.Equal(40, commit.Length);
            Assert.True(commit.All(Uri.IsHexDigit), "v4Commit must be a real 40-hex git commit");

            string command = root.GetProperty("generatedByCommand").GetString()!;
            Assert.False(string.IsNullOrWhiteSpace(command));
            Assert.Contains("V5ShuffleGoldenExporterTest", command);

            // 差距声明必须随文件存在：禁止把 V4 墙误当跨运行时可复现契约
            string rngGap = root.GetProperty("rngGapNote").GetString()!;
            Assert.Contains("D-09", rngGap);
            Assert.Contains("behavior samples", rngGap);

            JsonElement generation = root.GetProperty("generationResult");
            Assert.Equal("PASSED", generation.GetProperty("exporterTestResult").GetString());
            int cases = root.GetProperty("cases").GetArrayLength();
            Assert.Equal(32, cases);
            Assert.Equal(cases, generation.GetProperty("cases").GetInt32());
            Assert.Equal(16, generation.GetProperty("slotMatrixEntries").GetInt32());
            Assert.True(generation.GetProperty("threeCasterChangedSeeds").GetInt32() > 0);
        }

        [Fact]
        public void SlotMatrixMatchesCSharpShuffleSlots()
        {
            JsonElement matrix = Golden.Value.RootElement.GetProperty("slotMatrix");
            Assert.Equal(16, matrix.GetArrayLength());

            var seen = new HashSet<(int Dealer, int Seat)>();
            foreach (JsonElement entry in matrix.EnumerateArray())
            {
                int dealer = entry.GetProperty("dealerSeat").GetInt32();
                int seat = entry.GetProperty("seat").GetInt32();
                Assert.True(seen.Add((dealer, seat)), "duplicate dealer/seat entry");
                Assert.InRange(dealer, 0, 3);
                Assert.InRange(seat, 0, 3);

                Assert.Equal(
                    ShuffleSlots.Initial(dealer, seat).ToList(),
                    entry.GetProperty("initialSlots").EnumerateArray().Select(v => v.GetInt32()).ToList());
                Assert.Equal(
                    ShuffleSlots.Draws(dealer, seat, 18).ToList(),
                    entry.GetProperty("drawSlots").EnumerateArray().Select(v => v.GetInt32()).ToList());
                Assert.DoesNotContain(
                    entry.GetProperty("drawSlots").EnumerateArray().Select(v => v.GetInt32()),
                    slot => slot >= ShuffleSlots.DeadWallStart);
            }

            Assert.Equal(16, seen.Count);
        }

        [Fact]
        public void DemandVectorsAndOverlapMatrixMatchCSharp()
        {
            JsonElement root = Golden.Value.RootElement;
            var targets = new Dictionary<string, ShuffleTarget>
            {
                { "KOKUSHI", ShuffleTarget.Kokushi },
                { "CHINITSU_MAN", ShuffleTarget.ChinitsuMan },
                { "DAISANGEN", ShuffleTarget.Daisangen },
                { "DAISUUSHII", ShuffleTarget.Daisuushii },
                { "CHINITSU_SOU", ShuffleTarget.ChinitsuSou },
                { "CHINITSU_PIN", ShuffleTarget.ChinitsuPin }
            };

            JsonElement vectors = root.GetProperty("demandVectors");
            Assert.Equal(6, vectors.EnumerateObject().Count());
            foreach (KeyValuePair<string, ShuffleTarget> entry in targets)
            {
                double[] expected = ShuffleTargets.Demand(entry.Value);
                double[] actual = vectors.GetProperty(entry.Key).EnumerateArray().Select(v => v.GetDouble()).ToArray();
                Assert.Equal(34, actual.Length);
                for (int i = 0; i < 34; i++)
                {
                    Assert.True(Math.Abs(expected[i] - actual[i]) < 1e-12, entry.Key + "[" + i + "]");
                }
            }

            // 重叠度矩阵：与 C# 一致 + 与测试内独立计算的冻结硬值一致
            JsonElement overlap = root.GetProperty("overlapMatrix");
            foreach (KeyValuePair<string, ShuffleTarget> a in targets)
            {
                foreach (KeyValuePair<string, ShuffleTarget> b in targets)
                {
                    double actual = overlap.GetProperty(a.Key).GetProperty(b.Key).GetDouble();
                    Assert.Equal(ShuffleTargets.Overlap(a.Value, b.Value), actual, 12);
                    Assert.Equal(actual, overlap.GetProperty(b.Key).GetProperty(a.Key).GetDouble(), 12);
                }
            }

            Assert.Equal(2.0 / Math.Sqrt(117.0), overlap.GetProperty("CHINITSU_MAN").GetProperty("KOKUSHI").GetDouble(), 12);
            Assert.Equal(3.0 / Math.Sqrt(39.0), overlap.GetProperty("DAISANGEN").GetProperty("KOKUSHI").GetDouble(), 12);
            Assert.Equal(4.0 / Math.Sqrt(52.0), overlap.GetProperty("DAISUUSHII").GetProperty("KOKUSHI").GetDouble(), 12);
            Assert.Equal(0.0, overlap.GetProperty("DAISANGEN").GetProperty("DAISUUSHII").GetDouble(), 12);
            Assert.Equal(1.0, overlap.GetProperty("KOKUSHI").GetProperty("KOKUSHI").GetDouble(), 12);
        }

        [Fact]
        public void ExportedWallsSatisfyPhysicalLaws()
        {
            JsonElement cases = Golden.Value.RootElement.GetProperty("cases");
            Assert.Equal(32, cases.GetArrayLength());

            foreach (JsonElement entry in cases.EnumerateArray())
            {
                int seed = entry.GetProperty("seed").GetInt32();
                int dealer = entry.GetProperty("dealerSeat").GetInt32();
                Assert.Equal(seed % 4, dealer);

                // 方案与导出契约一致：席位 0/1/2 → 国士/大三元/万子清一色
                JsonElement plans = entry.GetProperty("plans");
                Assert.Equal(3, plans.GetArrayLength());
                string[] expectedTargets = { "KOKUSHI", "DAISANGEN", "CHINITSU_MAN" };
                for (int i = 0; i < 3; i++)
                {
                    Assert.Equal(i, plans[i].GetProperty("seat").GetInt32());
                    Assert.Equal(expectedTargets[i], plans[i].GetProperty("target").GetString());
                }

                Dictionary<string, int[]> walls = new[]
                {
                    ("baseWall", entry.GetProperty("baseWall")),
                    ("threeCasterWall", entry.GetProperty("threeCasterWall")),
                    ("fourCasterWall", entry.GetProperty("fourCasterWall")),
                    ("noTargetWall", entry.GetProperty("noTargetWall")),
                    ("zeroBudgetWall", entry.GetProperty("zeroBudgetWall"))
                }.ToDictionary(pair => pair.Item1, pair => pair.Item2.EnumerateArray().Select(v => v.GetInt32()).ToArray());

                foreach (KeyValuePair<string, int[]> wall in walls)
                {
                    Assert.Equal(136, wall.Value.Length);
                    Assert.Equal(136, wall.Value.Distinct().Count());
                    Assert.Equal(Enumerable.Range(0, 136), wall.Value.OrderBy(v => v));

                    int redFives = wall.Value.Count(v => new TileId(v).IsRedFive);
                    Assert.Equal(3, redFives);
                    Assert.Equal(3, entry.GetProperty("redFiveCount").GetInt32());
                }

                int[] baseWall = walls["baseWall"];
                int[] threeCaster = walls["threeCasterWall"];

                // 恒等律：四人 / 无目标 / 零额度墙与基础洗牌逐张相等
                Assert.Equal(baseWall, walls["fourCasterWall"]);
                Assert.Equal(baseWall, walls["noTargetWall"]);
                Assert.Equal(baseWall, walls["zeroBudgetWall"]);
                Assert.True(entry.GetProperty("fourCasterIdentity").GetBoolean());
                Assert.True(entry.GetProperty("reversedEqualsThree").GetBoolean());

                // 死山 122..135 隔离：三人做牌墙的王牌段与基础洗牌逐张相等
                for (int slot = ShuffleSlots.DeadWallStart; slot <= ShuffleSlots.DeadWallEnd; slot++)
                {
                    Assert.Equal(baseWall[slot], threeCaster[slot]);
                }

                // 变化标记与原始数组自洽（不由实现给出，而是导出物内部一致性）
                Assert.Equal(!baseWall.SequenceEqual(threeCaster), entry.GetProperty("threeCasterChanged").GetBoolean());
            }
        }

        private static JsonDocument LoadGolden()
        {
            return JsonDocument.Parse(File.ReadAllText(FindGoldenPath()));
        }

        private static string FindGoldenPath([CallerFilePath] string sourceFile = "")
        {
            string? overrideRoot = Environment.GetEnvironmentVariable("MAHJONG_REPO_ROOT");
            if (!string.IsNullOrEmpty(overrideRoot))
            {
                string overridden = Path.Combine(overrideRoot!, "tests", "v5-golden", "shuffle-v1.json");
                if (File.Exists(overridden))
                {
                    return overridden;
                }
            }

            foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory(), Path.GetDirectoryName(sourceFile) ?? string.Empty })
            {
                var directory = new DirectoryInfo(start);
                while (directory != null)
                {
                    string candidate = Path.Combine(directory.FullName, "tests", "v5-golden", "shuffle-v1.json");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }

                    directory = directory.Parent;
                }
            }

            throw new FileNotFoundException(
                "shuffle-v1.json not found under " + AppContext.BaseDirectory +
                " or " + Directory.GetCurrentDirectory() + "; set MAHJONG_REPO_ROOT if the test binaries live outside the repository.");
        }
    }
}
