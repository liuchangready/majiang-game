using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Mahjong.Domain.Tests
{
    /// <summary>
    /// Proves the scoring golden consumer is bound to every exported expected output leaf.
    /// The pristine fixture is first run through the exact same comparison entry point used by
    /// the mutation cases, so a broken harness cannot make a mutation test pass vacuously.
    /// </summary>
    public sealed class ScoringGoldenBindingTests
    {
        private static readonly string[] Sections =
        {
            "winSamples", "noWinSamples", "fuSamples", "doraSamples",
            "settlementSamples", "scoreSamples", "randomSamples"
        };

        private static readonly Dictionary<string, int> SamplesPerSection = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["winSamples"] = 48,
            ["noWinSamples"] = 4,
            ["fuSamples"] = 9,
            ["doraSamples"] = 6,
            ["settlementSamples"] = 24,
            ["scoreSamples"] = 147,
            ["randomSamples"] = 10_000
        };

        // Every normalized leaf path below this section prefix must have a declared output
        // binding. Paths use "[]" for each array level, exactly like the inspection script.
        private static readonly Dictionary<string, string[]> ExpectedPathsBySection = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["winSamples"] = new[]
            {
                "winSamples[].expected.dora.indicatorCount",
                "winSamples[].expected.dora.indicatorTiles[]",
                "winSamples[].expected.dora.redCount",
                "winSamples[].expected.dora.redTiles[]",
                "winSamples[].expected.dora.totalHan",
                "winSamples[].expected.dora.uraCount",
                "winSamples[].expected.dora.uraTiles[]",
                "winSamples[].expected.fu.finalFu",
                "winSamples[].expected.fu.lines[].description",
                "winSamples[].expected.fu.lines[].fu",
                "winSamples[].expected.fu.roundedUpTo",
                "winSamples[].expected.fu.specialNote",
                "winSamples[].expected.fu.subtotal",
                "winSamples[].expected.honbaAddition",
                "winSamples[].expected.isYakuman",
                "winSamples[].expected.paymentPlan.components[].amount",
                "winSamples[].expected.paymentPlan.components[].count",
                "winSamples[].expected.paymentPlan.components[].payerRole",
                "winSamples[].expected.paymentPlan.kind",
                "winSamples[].expected.paymentPlan.total",
                "winSamples[].expected.riichiAddition",
                "winSamples[].expected.score.basePoints",
                "winSamples[].expected.score.capCode",
                "winSamples[].expected.score.fu",
                "winSamples[].expected.score.han",
                "winSamples[].expected.score.isYakuman",
                "winSamples[].expected.score.rawBasePoints",
                "winSamples[].expected.score.rawBasePointsExact",
                "winSamples[].expected.score.yakumanMultiplier",
                "winSamples[].expected.scoreHan",
                "winSamples[].expected.status",
                "winSamples[].expected.totalHan",
                "winSamples[].expected.totalPoints",
                "winSamples[].expected.yaku[].code",
                "winSamples[].expected.yaku[].displayName",
                "winSamples[].expected.yaku[].han",
                "winSamples[].expected.yakumanCount"
            },
            ["noWinSamples"] = new[]
            {
                "noWinSamples[].expected.status"
            },
            ["fuSamples"] = new[]
            {
                "fuSamples[].expected.finalFu",
                "fuSamples[].expected.lines[].description",
                "fuSamples[].expected.lines[].fu",
                "fuSamples[].expected.roundedUpTo",
                "fuSamples[].expected.specialNote",
                "fuSamples[].expected.subtotal"
            },
            ["doraSamples"] = new[]
            {
                "doraSamples[].expected.indicatorCount",
                "doraSamples[].expected.indicatorTiles[]",
                "doraSamples[].expected.redCount",
                "doraSamples[].expected.redTiles[]",
                "doraSamples[].expected.totalHan",
                "doraSamples[].expected.uraCount",
                "doraSamples[].expected.uraTiles[]"
            },
            ["settlementSamples"] = new[]
            {
                "settlementSamples[].expected.deltas[]",
                "settlementSamples[].expected.newScores[]",
                "settlementSamples[].expected.remainingRiichiSticks"
            },
            ["scoreSamples"] = new[]
            {
                "scoreSamples[].expected.basePoints",
                "scoreSamples[].expected.capCode",
                "scoreSamples[].expected.isYakuman",
                "scoreSamples[].expected.paymentPlans.dealerRon.components[].amount",
                "scoreSamples[].expected.paymentPlans.dealerRon.components[].count",
                "scoreSamples[].expected.paymentPlans.dealerRon.components[].payerRole",
                "scoreSamples[].expected.paymentPlans.dealerRon.kind",
                "scoreSamples[].expected.paymentPlans.dealerRon.total",
                "scoreSamples[].expected.paymentPlans.dealerTsumo.components[].amount",
                "scoreSamples[].expected.paymentPlans.dealerTsumo.components[].count",
                "scoreSamples[].expected.paymentPlans.dealerTsumo.components[].payerRole",
                "scoreSamples[].expected.paymentPlans.dealerTsumo.kind",
                "scoreSamples[].expected.paymentPlans.dealerTsumo.total",
                "scoreSamples[].expected.paymentPlans.nonDealerRon.components[].amount",
                "scoreSamples[].expected.paymentPlans.nonDealerRon.components[].count",
                "scoreSamples[].expected.paymentPlans.nonDealerRon.components[].payerRole",
                "scoreSamples[].expected.paymentPlans.nonDealerRon.kind",
                "scoreSamples[].expected.paymentPlans.nonDealerRon.total",
                "scoreSamples[].expected.paymentPlans.nonDealerTsumo.components[].amount",
                "scoreSamples[].expected.paymentPlans.nonDealerTsumo.components[].count",
                "scoreSamples[].expected.paymentPlans.nonDealerTsumo.components[].payerRole",
                "scoreSamples[].expected.paymentPlans.nonDealerTsumo.kind",
                "scoreSamples[].expected.paymentPlans.nonDealerTsumo.total",
                "scoreSamples[].expected.payments.dealerRon",
                "scoreSamples[].expected.payments.dealerTsumo",
                "scoreSamples[].expected.payments.nonDealerRon",
                "scoreSamples[].expected.payments.nonDealerTsumoDealer",
                "scoreSamples[].expected.payments.nonDealerTsumoOther",
                "scoreSamples[].expected.rawBasePoints",
                "scoreSamples[].expected.rawBasePointsExact",
                "scoreSamples[].expected.yakumanMultiplier"
            },
            ["randomSamples"] = new[]
            {
                "randomSamples[].expected.basePoints",
                "randomSamples[].expected.capCode",
                "randomSamples[].expected.fu",
                "randomSamples[].expected.isYakuman",
                "randomSamples[].expected.scoreHan",
                "randomSamples[].expected.status",
                "randomSamples[].expected.totalPoints",
                "randomSamples[].expected.yaku[].code",
                "randomSamples[].expected.yaku[].han",
                "randomSamples[].expected.yakuHan",
                "randomSamples[].expected.yakumanCount"
            }
        };

        [Fact]
        public void ExpectedOutputLeafPathSetIsExactlyClassified()
        {
            JsonElement root = ScoringGoldenTests.BindingGoldenRoot;
            var failures = new List<string>();
            int declaredTotal = 0;
            int actualTotal = 0;
            int sampleTotal = 0;

            foreach (string section in Sections)
            {
                JsonElement array = root.GetProperty(section);
                int samples = array.GetArrayLength();
                sampleTotal += samples;
                Assert.Equal(SamplesPerSection[section], samples);

                var actual = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonElement sample in array.EnumerateArray())
                {
                    CollectExpectedLeafPaths(sample, section, actual);
                }

                string[] declared = ExpectedPathsBySection[section];
                Assert.Equal(declared.Length, declared.Distinct(StringComparer.Ordinal).Count());
                declaredTotal += declared.Length;
                actualTotal += actual.Count;

                foreach (string path in declared)
                {
                    if (!actual.Contains(path))
                    {
                        failures.Add("declared but absent: " + path);
                    }
                }

                foreach (string path in actual)
                {
                    if (!declared.Contains(path, StringComparer.Ordinal))
                    {
                        failures.Add("present but unclassified: " + path);
                    }
                }
            }

            Assert.Equal(10_238, sampleTotal);
            Assert.Equal(96, declaredTotal);
            Assert.Equal(96, actualTotal);
            Assert.True(failures.Count == 0,
                "expected output path set differs from the declared binding set:\n" + string.Join("\n", failures));
        }

        [Fact]
        public void PristineFixturePassesThroughTheBindingHarness()
        {
            JsonElement root = ScoringGoldenTests.BindingGoldenRoot;
            foreach (string section in Sections)
            {
                foreach (JsonElement sample in root.GetProperty(section).EnumerateArray())
                {
                    ScoringGoldenTests.CompareSampleForBindingTests(section, sample);
                }
            }
        }

        [Fact]
        public void WinExpectedOutputsAreBehaviorallyBound()
        {
            AssertSectionBindings("winSamples");
        }

        [Fact]
        public void NoWinExpectedOutputsAreBehaviorallyBound()
        {
            AssertSectionBindings("noWinSamples");
        }

        [Fact]
        public void FuExpectedOutputsAreBehaviorallyBound()
        {
            AssertSectionBindings("fuSamples");
        }

        [Fact]
        public void DoraExpectedOutputsAreBehaviorallyBound()
        {
            AssertSectionBindings("doraSamples");
        }

        [Fact]
        public void SettlementExpectedOutputsAreBehaviorallyBound()
        {
            AssertSectionBindings("settlementSamples");
        }

        [Fact]
        public void ScoreExpectedOutputsAreBehaviorallyBound()
        {
            AssertSectionBindings("scoreSamples");
        }

        [Fact]
        public void RandomExpectedOutputsAreBehaviorallyBound()
        {
            AssertSectionBindings("randomSamples");
        }

        [Fact]
        public void WinStatusExpectationRejectsDeletionWrongTypeAndInversion()
        {
            JsonElement sample = FirstSample("winSamples");
            AssertRejectedAfterEdit(sample, "winSamples", "deleted status", node =>
            {
                ((JsonObject)node["expected"]!).Remove("status");
            });
            AssertRejectedAfterEdit(sample, "winSamples", "numeric status", node =>
            {
                ((JsonObject)node["expected"]!)["status"] = 123;
            });
            AssertRejectedAfterEdit(sample, "winSamples", "inverted status", node =>
            {
                ((JsonObject)node["expected"]!)["status"] = "noWin";
            });
        }

        [Fact]
        public void WinTotalPointsExpectationRejectsDeletionWrongTypeAndOffset()
        {
            JsonElement sample = FirstSample("winSamples");
            AssertRejectedAfterEdit(sample, "winSamples", "deleted totalPoints", node =>
            {
                ((JsonObject)node["expected"]!).Remove("totalPoints");
            });
            AssertRejectedAfterEdit(sample, "winSamples", "string totalPoints", node =>
            {
                ((JsonObject)node["expected"]!)["totalPoints"] = "1000";
            });
            AssertRejectedAfterEdit(sample, "winSamples", "offset totalPoints", node =>
            {
                JsonObject expected = (JsonObject)node["expected"]!;
                expected["totalPoints"] = expected["totalPoints"]!.GetValue<long>() + 1;
            });
        }

        private static void AssertSectionBindings(string section)
        {
            JsonElement array = ScoringGoldenTests.BindingGoldenRoot.GetProperty(section);
            var firstSampleByPath = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            string[] declared = ExpectedPathsBySection[section];

            foreach (JsonElement sample in array.EnumerateArray())
            {
                var paths = new HashSet<string>(StringComparer.Ordinal);
                CollectExpectedLeafPaths(sample, section, paths);
                foreach (string path in paths)
                {
                    if (!firstSampleByPath.ContainsKey(path))
                    {
                        firstSampleByPath[path] = sample;
                    }
                }
            }

            var failures = new List<string>();
            foreach (string path in declared)
            {
                if (!firstSampleByPath.TryGetValue(path, out JsonElement sample))
                {
                    failures.Add(path + ": no source sample");
                    continue;
                }

                string mutatedJson = MutateExpectedLeaf(sample, path);
                using JsonDocument document = JsonDocument.Parse(mutatedJson);
                try
                {
                    ScoringGoldenTests.CompareSampleForBindingTests(section, document.RootElement);
                    failures.Add(path + ": mutated expectation was accepted");
                }
                catch (Exception)
                {
                    // The production comparison path rejected the changed expected value.
                }
            }

            Assert.True(failures.Count == 0,
                section + " has unbound expected output leaves:\n" + string.Join("\n", failures));
        }

        private static void AssertRejectedAfterEdit(
            JsonElement sample,
            string section,
            string label,
            Action<JsonObject> edit)
        {
            JsonObject root = (JsonObject)JsonNode.Parse(sample.GetRawText())!;
            edit(root);
            using JsonDocument document = JsonDocument.Parse(root.ToJsonString());
            bool rejected = false;
            try
            {
                ScoringGoldenTests.CompareSampleForBindingTests(section, document.RootElement);
            }
            catch (Exception)
            {
                rejected = true;
            }

            Assert.True(rejected, label + " must be rejected by the production comparison path");
        }

        private static JsonElement FirstSample(string section)
        {
            return ScoringGoldenTests.BindingGoldenRoot.GetProperty(section).EnumerateArray().First();
        }

        private static void CollectExpectedLeafPaths(JsonElement sample, string section, ISet<string> output)
        {
            CollectLeafPaths(sample.GetProperty("expected"), section + "[]" + ".expected", output);
        }

        private static void CollectLeafPaths(JsonElement node, string path, ISet<string> output)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in node.EnumerateObject())
                {
                    CollectLeafPaths(property.Value, path + "." + property.Name, output);
                }

                return;
            }

            if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in node.EnumerateArray())
                {
                    CollectLeafPaths(item, path + "[]", output);
                }

                return;
            }

            output.Add(path);
        }

        private static string MutateExpectedLeaf(JsonElement sample, string fullPath)
        {
            JsonNode root = JsonNode.Parse(sample.GetRawText()) ?? throw new InvalidOperationException("sample did not parse");
            int sectionEnd = fullPath.IndexOf("[]", StringComparison.Ordinal);
            string relative = fullPath.Substring(sectionEnd + 3);

            JsonNode? current = root;
            JsonNode? parent = null;
            string? objectKey = null;
            int arrayIndex = -1;

            foreach (string rawToken in relative.Split('.'))
            {
                bool array = rawToken.EndsWith("[]", StringComparison.Ordinal);
                string name = array ? rawToken.Substring(0, rawToken.Length - 2) : rawToken;
                if (current is not JsonObject obj)
                {
                    throw new InvalidOperationException("expected object at " + name);
                }

                parent = obj;
                objectKey = name;
                arrayIndex = -1;
                current = obj[name];

                if (array)
                {
                    if (current is not JsonArray items || items.Count == 0)
                    {
                        throw new InvalidOperationException("expected non-empty array at " + name);
                    }

                    parent = items;
                    objectKey = null;
                    arrayIndex = 0;
                    current = items[0];
                }
            }

            JsonNode mutated = current == null ? JsonValue.Create("~mutant")! : MutateValue(current, fullPath);
            if (parent is JsonObject parentObject && objectKey != null)
            {
                parentObject[objectKey] = mutated;
            }
            else if (parent is JsonArray parentArray && arrayIndex >= 0)
            {
                parentArray[arrayIndex] = mutated;
            }
            else
            {
                throw new InvalidOperationException("cannot replace leaf " + fullPath);
            }

            return root.ToJsonString();
        }

        private static JsonNode MutateValue(JsonNode current, string fullPath)
        {
            JsonElement value = JsonSerializer.SerializeToElement(current);
            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    string text = value.GetString()!;
                    if (fullPath.EndsWith(".expected.status", StringComparison.Ordinal))
                    {
                        return JsonValue.Create(text == "win" ? "noWin" : "win")!;
                    }

                    return JsonValue.Create(text + "~mutant")!;
                case JsonValueKind.Number:
                    return JsonValue.Create(value.GetInt64() + 1)!;
                case JsonValueKind.True:
                    return JsonValue.Create(false)!;
                case JsonValueKind.False:
                    return JsonValue.Create(true)!;
                case JsonValueKind.Null:
                    return JsonValue.Create("~mutant")!;
                default:
                    throw new InvalidOperationException("unsupported leaf kind " + value.ValueKind + " at " + fullPath);
            }
        }
    }
}
