using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Mahjong.Domain;
using Mahjong.Domain.Rules;
using Mahjong.TableRuntime;
using Xunit;

namespace Mahjong.LocalHost.Tests
{
    /// <summary>
    /// D-C-R1-01: the real LocalHost settlement fact port, not a default or hand-built port,
    /// must distinguish a response PASS from a real call/kan when deriving ippatsu.
    /// </summary>
    public sealed class LocalSettlementFactsPortTests
    {
        [Fact]
        public void RealOtherSeatPassDoesNotBreakRiichiIppatsu()
        {
            RoundStateMachine round = Fixture("DoubleRiichiRonWindow");
            RoundSettlementFacts facts = Resolve(round, 1);
            RoundSettlementCalculation calculation = RoundSettlementCalculator.CalculateRon(round, 1, facts);

            Assert.True(facts.FindSeat(1)!.IsIppatsu);
            Assert.False(facts.FindSeat(1)!.IsIppatsuInterruptedByCallOrKan);
            Assert.Equal(5, calculation.Breakdown!.TotalYakuHan);
            Assert.Equal(40, calculation.Breakdown.Fu.FinalFu);
            Assert.Equal(13000L, calculation.Breakdown.TotalPoints);
            Assert.Equal(
                new[] { "DOUBLE_RIICHI", "IPPATSU", "TANYAO", "IIPEIKO" },
                calculation.Breakdown.Yaku.Select(y => y.Code).ToArray());
        }

        [Fact]
        public void RealPonAfterRiichiStillInterruptsIppatsu()
        {
            RoundStateMachine round = Fixture("RiichiPonThenRonWindow");
            RoundSettlementFacts facts = Resolve(round, 1);
            RoundSettlementCalculation calculation = RoundSettlementCalculator.CalculateRon(round, 1, facts);

            Assert.False(facts.FindSeat(1)!.IsIppatsu);
            Assert.True(facts.FindSeat(1)!.IsIppatsuInterruptedByCallOrKan);
            Assert.Equal(4, calculation.Breakdown!.TotalYakuHan);
            Assert.Equal(40, calculation.Breakdown.Fu.FinalFu);
            Assert.Equal(9000L, calculation.Breakdown.TotalPoints);
            Assert.Equal(
                new[] { "DOUBLE_RIICHI", "TANYAO", "IIPEIKO" },
                calculation.Breakdown.Yaku.Select(y => y.Code).ToArray());
        }

        [Fact]
        public void RealOpenKanAfterRiichiAlsoInterruptsIppatsu()
        {
            RoundStateMachine round = Fixture("RiichiKanThenRonWindow");
            RoundSettlementFacts facts = Resolve(round, 1);
            RoundSettlementCalculation calculation = RoundSettlementCalculator.CalculateRon(round, 1, facts);

            Assert.False(facts.FindSeat(1)!.IsIppatsu);
            Assert.True(facts.FindSeat(1)!.IsIppatsuInterruptedByCallOrKan);
            Assert.Equal(4, calculation.Breakdown!.TotalYakuHan);
            Assert.Equal(40, calculation.Breakdown.Fu.FinalFu);
            Assert.Equal(9000L, calculation.Breakdown.TotalPoints);
            Assert.DoesNotContain(calculation.Breakdown.Yaku, y => y.Code == "IPPATSU");
        }

        private static RoundSettlementFacts Resolve(RoundStateMachine round, int winnerSeat)
        {
            var port = new LocalSettlementFactsPort
            {
                RoundProvider = () => round
            };
            var source = new TableSettlementSourceRef(round.RoundId, round.Version, round.SourceFingerprint);
            var intent = new TableSettlementIntent(round.RoundId, TableSettlementAction.Ron, winnerSeat);
            TableSettlementHostFacts host = port.Resolve(source, intent);

            Type builder = typeof(TableActor).Assembly.GetType(
                "Mahjong.TableRuntime.TableSettlementFactBuilder",
                throwOnError: true)!;
            return (RoundSettlementFacts)builder
                .GetMethod("Build", BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, new object[] { round, intent, host, "local-port-evidence", 1000L, 1000L })!;
        }

        private static RoundStateMachine Fixture(string method)
        {
            string root = P41Harness.FindRepositoryRoot();
            string path = Path.Combine(
                root,
                "dotnet",
                "tests",
                "Mahjong.TableRuntime.Tests",
                "bin",
                "Release",
                "net8.0",
                "Mahjong.TableRuntime.Tests.dll");
            if (!File.Exists(path))
            {
                path = Path.Combine(
                    root,
                    "dotnet",
                    "tests",
                    "Mahjong.TableRuntime.Tests",
                    "bin",
                    "Debug",
                    "net8.0",
                    "Mahjong.TableRuntime.Tests.dll");
            }

            Assert.True(File.Exists(path), "TableRuntime test fixture assembly was not built: " + path);
            Assembly assembly = Assembly.LoadFrom(path);
            Type type = assembly.GetType("Mahjong.TableRuntime.Tests.SettlementFixture", throwOnError: true)!;
            return (RoundStateMachine)type
                .GetMethod(method, BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, null)!;
        }
    }
}
