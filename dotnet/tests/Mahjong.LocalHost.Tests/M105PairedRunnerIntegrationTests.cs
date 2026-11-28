using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Ai;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Mahjong.TableRuntime;
using Xunit;

namespace Mahjong.LocalHost.Tests
{
    [Collection("LocalHostSerial")]
    public sealed class M105PairedRunnerIntegrationTests
    {
        private sealed class RecordingObserver : ILocalAiDecisionObserver
        {
            private readonly object _gate = new object();
            private readonly List<LocalAiDecisionRecord> _records = new List<LocalAiDecisionRecord>();

            public void OnDecision(LocalAiDecisionRecord record)
            {
                lock (_gate)
                {
                    _records.Add(record);
                }
            }

            public LocalAiDecisionRecord[] Snapshot()
            {
                lock (_gate)
                {
                    return _records.ToArray();
                }
            }
        }

        private sealed class DeterministicShuffleEnv
        {
            private readonly ulong _seed;
            private ulong _state;
            private int _id;

            public DeterministicShuffleEnv(ulong seed)
            {
                _seed = seed;
                _state = seed;
            }

            public LocalHostEnvironment Create()
            {
                return new LocalHostEnvironment
                {
                    NextInt = bound =>
                    {
                        _state += 0x9E3779B97F4A7C15UL;
                        ulong z = _state;
                        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                        z ^= z >> 31;
                        return (int)(z % (ulong)bound);
                    },
                    NewId = () => "m105-id-" + _seed.ToString("X16") + "-" + Interlocked.Increment(ref _id),
                };
            }
        }

        [Fact]
        public async Task PairedLegsShareIdenticalInitialDealWhileCorrelatingOpportunitiesReceiptsAndLedger()
        {
            CancellationToken ct = TestContext.Current.CancellationToken;
            const ulong shuffleSeed = 0x1122334455667788UL;

            var obsCandidate = new RecordingObserver();
            var obsControl = new RecordingObserver();

            await using var harnessA = new P41Harness();
            await using var harnessB = new P41Harness();

            static CharacterAiProfile[] MakeProfiles(string targetProfile) => new[]
            {
                CharacterAiProfile.CreateBasic("basic-s0", seed: 1001UL),
                string.Equals(targetProfile, LocalAiProfiles.ScoredCheatBasic, StringComparison.Ordinal)
                    ? CharacterAiProfile.CreateScoredBasic(
                        LocalAiProfiles.ScoredCheatBasic,
                        CharacterStyle.Neutral,
                        new AiCheatPolicy(enabled: true, maxSpendPerRound: 60, reservePoints: 0),
                        seed: 2002UL)
                    : CharacterAiProfile.CreateRandomCheatBasic(
                        LocalAiProfiles.RandomCheatBasic,
                        new AiCheatPolicy(enabled: true, maxSpendPerRound: 60, reservePoints: 0),
                        CharacterStyle.Neutral,
                        seed: 2002UL),
                CharacterAiProfile.CreateBasic("basic-s2", seed: 3003UL),
                CharacterAiProfile.CreateBasic("basic-s3", seed: 4004UL),
            };

            harnessA.Options.RoomId = "m105-cand";
            harnessA.Options.RulesetVersion = 1;
            harnessA.Options.CharacterIds = new[] { "octopus", "octopus", "octopus", "octopus" };
            harnessA.Options.SeatPlan = LocalTableSeatPlan.FromHumanSeats(Array.Empty<int>());
            harnessA.Options.Environment = new DeterministicShuffleEnv(shuffleSeed).Create();
            harnessA.Options.Ai = new LocalAiOptions { SeatProfiles = MakeProfiles(LocalAiProfiles.ScoredCheatBasic) };
            harnessA.Options.AiDecisionObserver = obsCandidate;
            harnessA.Options.AutoPlay = false;

            harnessB.Options.RoomId = "m105-ctrl";
            harnessB.Options.RulesetVersion = 1;
            harnessB.Options.CharacterIds = new[] { "octopus", "octopus", "octopus", "octopus" };
            harnessB.Options.SeatPlan = LocalTableSeatPlan.FromHumanSeats(Array.Empty<int>());
            harnessB.Options.Environment = new DeterministicShuffleEnv(shuffleSeed).Create();
            harnessB.Options.Ai = new LocalAiOptions { SeatProfiles = MakeProfiles(LocalAiProfiles.RandomCheatBasic) };
            harnessB.Options.AiDecisionObserver = obsControl;
            harnessB.Options.AutoPlay = false;

            LocalRoundSummary summaryA;
            LocalRoundSummary summaryB;
            await using (LocalTableRoom roomA = await LocalTableHost.OpenRoomAsync(harnessA.Options, ct))
            await using (LocalTableRoom roomB = await LocalTableHost.OpenRoomAsync(harnessB.Options, ct))
            {
                // Before any AI runs, verify identical dealt initial hands and wall order for shared shuffleSeed.
                RoundStateMachine stateA = roomA.AuthorityForTest.ActorForTest.GetRoundStateForHost();
                RoundStateMachine stateB = roomB.AuthorityForTest.ActorForTest.GetRoundStateForHost();
                for (int s = 0; s < 4; s++)
                {
                    Assert.Equal(
                        stateA.Tiles.TilesInZone(TileZone.Hand, s),
                        stateB.Tiles.TilesInZone(TileZone.Hand, s));
                }
                Assert.Equal(stateA.Tiles.DoraIndicators, stateB.Tiles.DoraIndicators);

                roomA.AuthorityForTest.DriverForTest.Start();
                roomB.AuthorityForTest.DriverForTest.Start();

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(90));
                do
                {
                    await Task.Delay(25, timeout.Token);
                    summaryA = await LocalTableHost.GetRoundSummaryAsync(harnessA.Options.SaveDirectory, timeout.Token);
                    summaryB = await LocalTableHost.GetRoundSummaryAsync(harnessB.Options.SaveDirectory, timeout.Token);
                }
                while (!summaryA.Finished || !summaryB.Finished);

                await roomA.AuthorityForTest.DriverForTest.StopAsync();
                await roomB.AuthorityForTest.DriverForTest.StopAsync();
            }

            foreach ((LocalRoundSummary summary, RecordingObserver observer) in new[] { (summaryA, obsCandidate), (summaryB, obsControl) })
            {
                Assert.True(summary.Finished);
                Assert.True(summary.WallConserved, string.Join("; ", summary.ConservationErrors));
                Assert.Equal(136, summary.WallAssignedCount);
                Assert.Equal(100_000L, summary.FinalScores.Sum() + summary.RemainingRiichiSticks * 1000L);

                LocalAiDecisionRecord[] records = observer.Snapshot();
                Assert.NotEmpty(records);

                // Every ordinary accepted submission links to a prior selected opportunity with DecisionId + RequestId + EventSeq.
                var selectedKeys = records
                    .Where(r => r.DecisionKind == "ordinary" && r.Status == "selected")
                    .Select(r =>
                    {
                        Assert.False(string.IsNullOrWhiteSpace(r.DecisionId));
                        Assert.True(r.LegalOptionCount > 0);
                        Assert.False(string.IsNullOrWhiteSpace(r.LegalCapabilities));
                        return $"{r.RoundId}|{r.Seat}|{r.DecisionId}|{r.StateVersion}";
                    })
                    .ToHashSet(StringComparer.Ordinal);
                Assert.NotEmpty(selectedKeys);

                foreach (LocalAiDecisionRecord ord in records.Where(r => r.DecisionKind == "ordinary" && r.Status == "accepted"))
                {
                    Assert.Contains($"{ord.RoundId}|{ord.Seat}|{ord.DecisionId}|{ord.StateVersion}", selectedKeys);
                    Assert.False(string.IsNullOrWhiteSpace(ord.RequestId));
                    Assert.False(string.IsNullOrWhiteSpace(ord.ReceiptStatus));
                    Assert.NotNull(ord.EventSeq);
                    Assert.NotNull(ord.AuthoritativeStateVersionAfter);
                }

                // Per-seat ledger reconciliation: 120 - balance == SeatCheatUsage == sum(accepted cheat OptionCost).
                for (int seat = 0; seat < 4; seat++)
                {
                    int ledgerSpent = 120 - summary.CheatBalances[seat];
                    int seatUsageSpent = summary.SeatCheatUsage.Where(u => u.Seat == seat).Sum(u => u.SpentPoints);
                    int observedSpent = records
                        .Where(r => r.Seat == seat && r.DecisionKind == "cheat" && r.Status == "accepted")
                        .Sum(r => r.OptionCost ?? 0);
                    Assert.Equal(ledgerSpent, seatUsageSpent);
                    Assert.Equal(ledgerSpent, observedSpent);
                }

                // Target seat (seat 1) has recorded cheat opportunities (skip or accepted) with non-zero LegalOptionCount.
                Assert.Contains(records, r => r.Seat == 1 && (r.DecisionKind == "cheat" || r.DecisionKind == "cheat-skip") && r.LegalOptionCount > 0);
            }
        }

        [Fact]
        public async Task AuthorityTimeoutDefaultEmitsCorrelatedTimeoutDefaultRecord()
        {
            CancellationToken ct = TestContext.Current.CancellationToken;
            var observer = new RecordingObserver();

            await using var harness = new P41Harness();
            harness.Options.SeatPlan = LocalTableSeatPlan.FromHumanSeats(Array.Empty<int>());
            harness.Options.Ai = new LocalAiOptions
            {
                SeatProfiles = new[]
                {
                    CharacterAiProfile.CreateBasic("s0"),
                    CharacterAiProfile.CreateBasic("s1"),
                    CharacterAiProfile.CreateBasic("s2"),
                    CharacterAiProfile.CreateBasic("s3"),
                }
            };
            harness.Options.AiDecisionObserver = observer;
            harness.Options.AutoPlay = false;

            await using LocalTableRoom room = await LocalTableHost.OpenRoomAsync(harness.Options, ct);
            LocalTableRuntime runtime = room.AuthorityForTest;
            TableActor actor = runtime.ActorForTest;
            RoundStateMachine round = actor.GetRoundStateForHost();

            IReadOnlyList<LegalActionOption> candidates = LocalCandidateProvider.DeriveTurnCandidates(
                round, round.CurrentSeat, new bool[4], new bool[4]);
            var pending = new PendingDecision(
                new DecisionKey(round.RoundId, round.CurrentSeat, "m105-timeout-dec"),
                0L,
                candidates,
                round.Tiles.DrawnTile?.Tile);
            TableOperationResult opened = await actor.OpenDecisionAsync(pending, ct);
            Assert.Equal(TableOperationStatus.Configured, opened.Status);

            // Advance the injected clock past the 20s decision deadline, then run Driver once so TickAsync executes timeout default!
            harness.Clock.MonotonicMillis += 25_000L;
            harness.Clock.UtcNowMs += 25_000L;
            long beforeIter = runtime.DriverForTest.CompletedIterationsForTest;
            runtime.DriverForTest.Start();
            await runtime.DriverForTest.WaitForIterationAfterForTest(beforeIter, ct);
            await runtime.DriverForTest.StopAsync();

            LocalAiDecisionRecord timeoutRec = Assert.Single(
                observer.Snapshot(),
                r => r.DecisionKind == "timeout-default");
            Assert.Equal("m105-timeout-dec", timeoutRec.DecisionId);
            Assert.Equal("accepted", timeoutRec.Status);
            Assert.True(timeoutRec.IsDefaultResolution);
            Assert.NotNull(timeoutRec.EventSeq);
            Assert.NotNull(timeoutRec.AuthoritativeStateVersionAfter);
        }

        [Fact]
        public async Task SanitizedObserverAndSummaryNeverExposeHiddenTilesWallOrderOrSeeds()
        {
            CancellationToken ct = TestContext.Current.CancellationToken;
            var observer = new RecordingObserver();

            await using var harness = new P41Harness();
            harness.Options.SeatPlan = LocalTableSeatPlan.FromHumanSeats(Array.Empty<int>());
            harness.Options.CharacterIds = new[] { "octopus", "octopus", "octopus", "octopus" };
            harness.Options.Ai = new LocalAiOptions
            {
                SeatProfiles = new[]
                {
                    CharacterAiProfile.CreateRandomCheatBasic("s0-rand", new AiCheatPolicy(enabled: true, maxSpendPerRound: 40), seed: 0xDEADBEEF1111UL),
                    CharacterAiProfile.CreateScoredBasic("s1-scored", CharacterStyle.Neutral, new AiCheatPolicy(enabled: true, maxSpendPerRound: 40), seed: 0xDEADBEEF2222UL),
                    CharacterAiProfile.CreateBasic("s2-basic", seed: 0xDEADBEEF3333UL),
                    CharacterAiProfile.CreateBasic("s3-basic", seed: 0xDEADBEEF4444UL),
                }
            };
            harness.Options.AiDecisionObserver = observer;

            LocalRoundSummary summary;
            await using (LocalTableRoom room = await LocalTableHost.OpenRoomAsync(harness.Options, ct))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                do
                {
                    await Task.Delay(25, timeout.Token);
                    summary = await LocalTableHost.GetRoundSummaryAsync(harness.Options.SaveDirectory, timeout.Token);
                }
                while (!summary.Finished);
            }

            LocalAiDecisionRecord[] records = observer.Snapshot();
            foreach (LocalAiDecisionRecord r in records)
            {
                Assert.DoesNotContain("DEADBEEF", r.Detail, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("DEADBEEF", r.OptionId, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("DEADBEEF", r.LegalCapabilities, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Wall", r.LegalCapabilities, StringComparison.OrdinalIgnoreCase);
            }

            Assert.Equal(136, summary.WallAssignedCount);
            Assert.True(summary.WallConserved);
        }
    }
}
