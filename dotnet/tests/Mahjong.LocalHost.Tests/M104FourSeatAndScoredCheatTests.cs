using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Ai;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Mahjong.Domain.Rules;
using Mahjong.TableRuntime;
using Xunit;

namespace Mahjong.LocalHost.Tests
{
    [Collection("LocalHostSerial")]
    public sealed class M104FourSeatAndScoredCheatTests
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

        // M1T-06b (Positive): 4 seats configured with independent profiles/strategies/styles/policies
        // preserve seat-level bindings and characters across initial round, renchan, dealer rotation,
        // West extension, cold match restart, and reset per-round budget cleanly on each new roundId.
        [Fact]
        public async Task M1T06b_FourSeatProfilesPropagateAcrossRenchanRotationWestExtensionAndResetRoundBudget()
        {
            await using var harness = new P41Harness();
            CancellationToken ct = TestContext.Current.CancellationToken;
            string root = Path.Combine(harness.Root, "m104-match-prop");

            string[] characters = { "player", "octopus", "player", "octopus" };
            CharacterAiProfile[] seatProfiles =
            {
                CharacterAiProfile.CreateBasic("p0-human-fallback"),
                CharacterAiProfile.CreateScoredBasic(
                    profileId: "p1-octopus-scored",
                    style: new CharacterStyle(aggression: 0.8, meldTendency: 0.4, flushTendency: 0.6, defense: 0.3, cheatAggression: 0.9),
                    cheatPolicy: new AiCheatPolicy(enabled: true, maxSpendPerRound: 30, reservePoints: 10),
                    seed: 101UL),
                CharacterAiProfile.CreateRandomCheatBasic(
                    profileId: "p2-random-cheat",
                    cheatPolicy: new AiCheatPolicy(enabled: true, maxSpendPerRound: 20, reservePoints: 0),
                    seed: 202UL),
                CharacterAiProfile.CreateLegacyHeuristic("p3-legacy")
            };

            var observer = new RecordingObserver();
            var options = new LocalMatchHostOptions
            {
                SaveDirectory = root,
                MatchId = "m104-match",
                OwnerInstanceId = "owner-m104",
                LeaseDuration = TimeSpan.FromMinutes(5),
                Environment = harness.Options.Environment,
                // 1 hand per wind + allowWestExtension=true:
                // Round 1 (East 1, dealer 0): seat 0 wins -> Renchan (Round 2: East 1, dealer 0, honba 1)
                // Round 2 (East 1, dealer 0): seat 1 wins -> Rotation into South 1 (Round 3: dealer 1)
                // Round 3 (South 1, dealer 1): seat 2 wins -> All < 30,000 -> West extension (Round 4: West 1, dealer 2)!
                Rules = new MatchRuleConfig(MatchLength.Hanchan, handsPerWind: 1, allowWestExtension: true),
                SeatPlan = LocalTableSeatPlan.SingleHumanSeatZero,
                CharacterIds = characters,
                Ai = new LocalAiOptions
                {
                    SeatProfiles = seatProfiles
                },
                AiDecisionObserver = observer
            };
            options.RoundFactory = (state, _) =>
            {
                int winner = state.Ordinal switch
                {
                    1 => 0, // renchan (dealer 0 wins)
                    2 => 1, // rotation (seat 1 wins -> dealer 1, South 1)
                    _ => 2  // rotation into West extension (seat 2 wins -> dealer 2, West 1)
                };
                return BuildQuickWinnerRound(
                    state.CurrentRoundId,
                    state.DealerSeat,
                    state.RoundWind,
                    state.RoundNumber,
                    state.Honba,
                    state.Scores,
                    state.RiichiSticks,
                    winnerSeat: winner);
            };

            string r1Id;
            string r2Id;
            string r3Id;
            string r4Id;

            LocalMatchCoordinator match = await LocalMatchCoordinator.OpenAsync(options, ct);
            try
            {
                Assert.Equal(characters, match.Snapshot.CharacterIds);
                Assert.NotNull(match.Snapshot.SeatProfiles);
                Assert.Equal(
                    new[] { "p0-human-fallback", "p1-octopus-scored", "p2-random-cheat", "p3-legacy" },
                    match.Snapshot.SeatProfileIds);

                // Round 1 (Initial): dealer 0, East 1
                LocalMatchRound r1 = await match.OpenRoundAsync(ct);
                r1Id = r1.RoundId;
                await r1.Channel.OpenAsync(new TableChannelConnectRequest(r1.Ticket, "m104-p0-r1", null), ct);
                LocalTableRuntime rt1 = ((LocalTableChannel)r1.Channel).RuntimeForMatch!;
                Assert.Equal(characters, rt1.ActorForTest.GetSeatCharacterIdsForHost());
                Assert.Contains("s1:profile=p1-octopus-scored,strategy=scored-cheat-basic", rt1.DriverForTest.DescribeSeatBindingsForTest());
                Assert.Contains("s2:profile=p2-random-cheat,strategy=baseline-basic-random-cheat", rt1.DriverForTest.DescribeSeatBindingsForTest());
                Assert.Contains("s3:profile=p3-legacy,strategy=legacy-heuristic", rt1.DriverForTest.DescribeSeatBindingsForTest());

                TableSession s1 = await PlayRoundToEndAsync(r1, ct, seat0Action: ProtocolActionType.DiscardTile, discardTile: 132u, match);
                LocalMatchSnapshot afterR1 = await match.CompleteRoundAsync(ct);
                await s1.CloseAsync("TEST_DONE", CancellationToken.None);
                Assert.Equal(0, afterR1.DealerSeat);
                Assert.Equal(1, afterR1.Honba);
                Assert.True(afterR1.Scores.All(s => s < 30_000), "afterR1 scores unexpectedly >=30k: " + string.Join(",", afterR1.Scores));
                Assert.Equal(characters, afterR1.CharacterIds);
                Assert.Equal(seatProfiles.Select(p => p.ProfileId).ToArray(), afterR1.SeatProfileIds);
            }
            finally
            {
                await match.DisposeAsync();
            }

            // Cold restart coordinator with DIFFERENT options -> persisted v3 SeatProfiles & CharacterIds MUST win!
            var conflicting = new LocalMatchHostOptions
            {
                SaveDirectory = root,
                MatchId = "m104-match",
                OwnerInstanceId = "owner-m104-restarted",
                LeaseDuration = TimeSpan.FromMinutes(5),
                Environment = harness.Options.Environment,
                Rules = options.Rules,
                SeatPlan = LocalTableSeatPlan.SingleHumanSeatZero,
                CharacterIds = new[] { "octopus", "player", "octopus", "player" },
                Ai = new LocalAiOptions { ProfileId = LocalAiProfiles.LegacyHeuristic },
                AiDecisionObserver = observer,
                RoundFactory = options.RoundFactory
            };

            LocalMatchCoordinator restarted = await LocalMatchCoordinator.OpenAsync(conflicting, ct);
            try
            {
                Assert.Equal(characters, restarted.Snapshot.CharacterIds);
                Assert.Equal(
                    new[] { "p0-human-fallback", "p1-octopus-scored", "p2-random-cheat", "p3-legacy" },
                    restarted.Snapshot.SeatProfileIds);

                // Round 2 (Renchan): dealer still 0, new roundId, budget reset to 0
                LocalMatchRound r2 = await restarted.OpenRoundAsync(ct);
                r2Id = r2.RoundId;
                Assert.NotEqual(r1Id, r2Id);
                await r2.Channel.OpenAsync(new TableChannelConnectRequest(r2.Ticket, "m104-p0-r2", null), ct);
                LocalTableRuntime rt2 = ((LocalTableChannel)r2.Channel).RuntimeForMatch!;
                Assert.Equal(characters, rt2.ActorForTest.GetSeatCharacterIdsForHost());
                for (int s = 0; s < 4; s++)
                {
                    Assert.Equal(0, rt2.ActorForTest.GetAiCheatSpentThisRound(s));
                }

                TableSession s2 = await PlayRoundToEndAsync(r2, ct, seat0Action: ProtocolActionType.DiscardTile, discardTile: 123u, restarted);
                LocalMatchSnapshot afterR2 = await restarted.CompleteRoundAsync(ct);
                await s2.CloseAsync("TEST_DONE", CancellationToken.None);
                string r2Obs = string.Join("; ", observer.Snapshot().Where(r => r.RoundId == r2Id).Take(8).Select(r => $"s{r.Seat}/{r.Phase}/{r.DecisionKind}:{r.OptionId}({r.Status})"));
                string r2Discards = string.Join(",", rt2.ActorForTest.GetRoundStateForHost().Tiles.TilesInZone(TileZone.Discard, 0).Select(t => t.Value));
                Assert.True(afterR2.DealerSeat == 1 && afterR2.Records[^1].WinnerSeat == 1,
                    $"R2 unexpected: dealer={afterR2.DealerSeat}, rec={afterR2.Records[^1].OutcomeKind}(w={afterR2.Records[^1].WinnerSeat}), s0Disc=[{r2Discards}], obs=[{r2Obs}]");
                Assert.Equal(2, afterR2.RoundWind); // South 1
                Assert.True(afterR2.Scores.All(s => s < 30_000), "afterR2 scores unexpectedly >=30k: " + string.Join(",", afterR2.Scores));

                // Round 3 (Rotation into South 1): dealer 1, new roundId, profiles & characters unchanged
                LocalMatchRound r3 = await restarted.OpenRoundAsync(ct);
                r3Id = r3.RoundId;
                Assert.NotEqual(r2Id, r3Id);
                await r3.Channel.OpenAsync(new TableChannelConnectRequest(r3.Ticket, "m104-p0-r3", null), ct);
                LocalTableRuntime rt3 = ((LocalTableChannel)r3.Channel).RuntimeForMatch!;
                Assert.Equal(characters, rt3.ActorForTest.GetSeatCharacterIdsForHost());
                Assert.Contains("s1:profile=p1-octopus-scored,strategy=scored-cheat-basic", rt3.DriverForTest.DescribeSeatBindingsForTest());

                TableSession s3 = await PlayRoundToEndAsync(r3, ct, seat0Action: null, discardTile: null, restarted);
                LocalMatchSnapshot afterR3 = await restarted.CompleteRoundAsync(ct);
                await s3.CloseAsync("TEST_DONE", CancellationToken.None);
                string recSummary = string.Join(" | ", afterR3.Records.Select(r => $"{r.RoundId}:{r.OutcomeKind}(w={r.WinnerSeat},l={r.LoserSeat})[{string.Join(",", r.FinalScores)}]"));
                string r3Diag = $"DealerSeat={afterR3.DealerSeat}, RoundWind={afterR3.RoundWind}, RoundNumber={afterR3.RoundNumber}, Honba={afterR3.Honba}, Finished={afterR3.Finished}, Reason={afterR3.FinishReason}, Records={recSummary}, Scores=[{string.Join(",", afterR3.Scores)}]";
                Assert.True(afterR3.DealerSeat == 2, "Expected DealerSeat=2 after R3, got: " + r3Diag);
                Assert.Equal(3, afterR3.RoundWind); // West extension!

                // Round 4 (West extension): dealer 2, new roundId, profiles & characters still match seat 0..3
                LocalMatchRound r4 = await restarted.OpenRoundAsync(ct);
                r4Id = r4.RoundId;
                Assert.NotEqual(r3Id, r4Id);
                await r4.Channel.OpenAsync(new TableChannelConnectRequest(r4.Ticket, "m104-p0-r4", null), ct);
                LocalTableRuntime rt4 = ((LocalTableChannel)r4.Channel).RuntimeForMatch!;
                Assert.Equal(characters, rt4.ActorForTest.GetSeatCharacterIdsForHost());
                Assert.Contains("s1:profile=p1-octopus-scored,strategy=scored-cheat-basic", rt4.DriverForTest.DescribeSeatBindingsForTest());
                Assert.Contains("s2:profile=p2-random-cheat,strategy=baseline-basic-random-cheat", rt4.DriverForTest.DescribeSeatBindingsForTest());
                Assert.Contains("s3:profile=p3-legacy,strategy=legacy-heuristic", rt4.DriverForTest.DescribeSeatBindingsForTest());
                for (int s = 0; s < 4; s++)
                {
                    Assert.Equal(0, rt4.ActorForTest.GetAiCheatSpentThisRound(s));
                }
            }
            finally
            {
                await restarted.DisposeAsync();
            }

            Assert.Contains(observer.Snapshot(), r => r.ProfileId == "p1-octopus-scored" || r.ProfileId == "p2-random-cheat");
        }

        // M1T-06b (Negative/Recovery + D-01-03 + D-01-04): Real activation/spend persistence via LocalHost,
        // cold reopen with normal profile blocked by restored spent budget on 2nd opportunity (resets on new roundId),
        // cold reopen with missing/incompatible strategy/model honoring host FallbackProfileId vs per-seat override,
        // safe settlement of the exact same persisted offer via IsSafeDefault with 0 extra spend, and v2 archive compat.
        [Fact]
        public async Task M1T06b_RecoveryPreservesLedgerSpendAndDegradesGracefullyOnMissingOrIncompatibleModel()
        {
            CancellationToken ct = TestContext.Current.CancellationToken;
            string[] characters = { "player", "octopus", "player", "player" };

            // Part A: Real AI activation ("sleeve-activate", -10) -> stop while ImmediateOffer is open ->
            // cold reopen under incompatible ModelVersion + missing StrategyId + host FallbackProfileId=LegacyHeuristic.
            await using (var harness = new P41Harness())
            {
                RoundStateMachine round = BuildSeat1ScoredCheatStartRound("round-m104-rec");
                harness.Options.SeatPlan = LocalTableSeatPlan.FromHumanSeats(Array.Empty<int>());
                harness.Options.CharacterIds = characters;
                harness.Options.InitialRound = round;
                harness.Options.AutoPlay = false;
                harness.Options.Ai = new LocalAiOptions
                {
                    SeatProfiles = new[]
                    {
                        CharacterAiProfile.CreateBasic("s0"),
                        CharacterAiProfile.CreateScoredBasic(
                            "s1-scored",
                            style: new CharacterStyle(aggression: 0.9, cheatAggression: 0.9),
                            cheatPolicy: new AiCheatPolicy(
                                enabled: true,
                                maxSpendPerRound: 10,
                                reservePoints: 0,
                                capabilityToggles: new Dictionary<string, bool> { ["tile_mark"] = false })),
                        CharacterAiProfile.CreateBasic("s2"),
                        CharacterAiProfile.CreateBasic("s3")
                    }
                };

                string persistedOfferId;
                int spentBeforeReopen;
                var offerOpenedTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                await using (LocalTableRoom room = await LocalTableHost.OpenRoomAsync(harness.Options, ct))
                {
                    LocalTableRuntime runtime = room.AuthorityForTest;
                    runtime.DriverForTest.BeforeAiCheatScoreOrSubmitForTest = async (seat, opp, stopToken) =>
                    {
                        if (seat == 1 && opp.View.IsOfferChoice && !string.IsNullOrEmpty(opp.OfferId))
                        {
                            offerOpenedTcs.TrySetResult(opp.OfferId!);
                            try
                            {
                                await Task.Delay(Timeout.InfiniteTimeSpan, stopToken).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                            }
                        }
                    };
                    runtime.DriverForTest.Start();
                    persistedOfferId = await LocalHostCommandTests.WithTimeoutAsync(offerOpenedTcs.Task, ct);
                    await runtime.DriverForTest.StopAsync();

                    Assert.True(runtime.ActorForTest.HasActiveCheatOffer(1));
                    spentBeforeReopen = runtime.ActorForTest.GetAiCheatSpentThisRound(1);
                    Assert.Equal(10, spentBeforeReopen);
                    Assert.Equal(110, runtime.ActorForTest.GetCheatLedgerForHost().Balances[1]);
                }

                // Cold reopen with incompatible ModelVersion + omitted per-seat fallback -> must use host FallbackProfileId (LegacyHeuristic),
                // whereas seat 2 with explicit fallbackStrategyId=Basic must override host LegacyHeuristic -> Basic!
                var observer = new RecordingObserver();
                var degradedOptions = new LocalTableHostOptions
                {
                    SaveDirectory = harness.Options.SaveDirectory,
                    RoomId = harness.Options.RoomId,
                    RulesetVersion = 1,
                    OwnerInstanceId = "owner-reopen-degraded",
                    LeaseDuration = TimeSpan.FromMinutes(5),
                    Environment = harness.Options.Environment,
                    SeatPlan = LocalTableSeatPlan.FromHumanSeats(Array.Empty<int>()),
                    CharacterIds = characters,
                    AutoPlay = false,
                    Ai = new LocalAiOptions
                    {
                        SeatProfiles = new[]
                        {
                            new CharacterAiProfile("s0-missing-strat", strategyId: null),
                            CharacterAiProfile.CreateScoredBasic(
                                "s1-bad-model",
                                cheatPolicy: new AiCheatPolicy(enabled: true, maxSpendPerRound: 10),
                                modelVersion: "onnx-v99-incompatible"),
                            new CharacterAiProfile(
                                "s2-unknown-explicit-basic",
                                strategyId: "future-m2-transformer",
                                fallbackStrategyId: LocalAiProfiles.Basic),
                            CharacterAiProfile.CreateBasic("s3")
                        },
                        FallbackProfileId = LocalAiProfiles.LegacyHeuristic
                    },
                    AiDecisionObserver = observer
                };

                await using (LocalTableRoom reopened = await LocalTableHost.OpenRoomAsync(degradedOptions, ct))
                {
                    LocalTableRuntime rt = reopened.AuthorityForTest;
                    Assert.Equal(characters, rt.ActorForTest.GetSeatCharacterIdsForHost());
                    Assert.Equal(spentBeforeReopen, rt.ActorForTest.GetAiCheatSpentThisRound(1));
                    Assert.True(rt.ActorForTest.HasActiveCheatOffer(1));
                    string bindings = rt.DriverForTest.DescribeSeatBindingsForTest();
                    Assert.Contains("s0:profile=s0-missing-strat,strategy=legacy-heuristic,degraded=True", bindings);
                    Assert.Contains("s1:profile=s1-bad-model,strategy=legacy-heuristic,degraded=True", bindings);
                    Assert.Contains("s2:profile=s2-unknown-explicit-basic,strategy=baseline-basic,degraded=True", bindings);

                    rt.DriverForTest.BeforeAiCheatScoreOrSubmitForTest = (seat, opp, _) =>
                    {
                        if (seat == 1 && opp.View.IsOfferChoice)
                        {
                            Assert.Equal(persistedOfferId, opp.OfferId);
                        }
                        return Task.CompletedTask;
                    };

                    rt.DriverForTest.Start();
                    LocalRoundSummary summary;
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(60));
                    do
                    {
                        await Task.Delay(25, timeout.Token);
                        summary = await LocalTableHost.GetRoundSummaryAsync(harness.Options.SaveDirectory, timeout.Token);
                    }
                    while (!summary.Finished);

                    await rt.DriverForTest.StopAsync();
                    Assert.False(rt.ActorForTest.HasActiveCheatOffer(1));
                    Assert.True(summary.WallConserved, string.Join("; ", summary.ConservationErrors));
                    Assert.Equal(136, summary.WallAssignedCount);
                    Assert.Equal(100_000L, summary.FinalScores.Sum() + summary.RemainingRiichiSticks * 1000L);
                    Assert.Equal(spentBeforeReopen, rt.ActorForTest.GetAiCheatSpentThisRound(1));
                    Assert.Equal(110, summary.CheatBalances[1]);

                    LocalAiDecisionRecord offerRecord = Assert.Single(
                        observer.Snapshot(),
                        r => r.RoundId == "round-m104-rec" && r.Seat == 1 && r.DecisionKind == "cheat" && r.CapabilityId == "sleeve_bloom");
                    Assert.Equal("accepted", offerRecord.Status);
                    Assert.Equal("sleeve-first", offerRecord.OptionId);

                    Assert.Contains(observer.Snapshot(), r =>
                        r.Seat == 0 && r.DecisionKind == "strategy-fallback" &&
                        r.ReasonCode == "STRATEGY_UNAVAILABLE" && r.StrategyId == LocalAiProfiles.LegacyHeuristic);
                    Assert.Contains(observer.Snapshot(), r =>
                        r.Seat == 1 && r.DecisionKind == "strategy-fallback" &&
                        r.ReasonCode == "MODEL_VERSION_INCOMPATIBLE" && r.StrategyId == LocalAiProfiles.LegacyHeuristic);
                    Assert.Contains(observer.Snapshot(), r =>
                        r.Seat == 2 && r.DecisionKind == "strategy-fallback" &&
                        r.ReasonCode == "STRATEGY_UNAVAILABLE" && r.StrategyId == LocalAiProfiles.Basic);
                }
            }

            // Part B (D-01-04): Cold recovery with NORMAL ScoredCheatBasic restores spent=10 from ledger,
            // blocks a 2nd activatable opportunity in the same round, and allows spending again on a new roundId!
            await using (var budgetHarness = new P41Harness())
            {
                RoundStateMachine round1 = BuildSeat1ScoredCheatStartRound("round-m104-cap-r1");
                var scoredSeat1 = CharacterAiProfile.CreateScoredBasic(
                    "s1-scored-cap",
                    style: new CharacterStyle(aggression: 0.9, cheatAggression: 0.9),
                    cheatPolicy: new AiCheatPolicy(
                        enabled: true,
                        maxSpendPerRound: 10,
                        reservePoints: 0,
                        capabilityToggles: new Dictionary<string, bool> { ["tile_mark"] = false }));

                budgetHarness.Options.SeatPlan = LocalTableSeatPlan.FromHumanSeats(Array.Empty<int>());
                budgetHarness.Options.CharacterIds = characters;
                budgetHarness.Options.InitialRound = round1;
                budgetHarness.Options.AutoPlay = false;
                budgetHarness.Options.Ai = new LocalAiOptions
                {
                    SeatProfiles = new[]
                    {
                        CharacterAiProfile.CreateBasic("s0"),
                        scoredSeat1,
                        CharacterAiProfile.CreateBasic("s2"),
                        CharacterAiProfile.CreateBasic("s3")
                    }
                };

                await using (LocalTableRoom room1 = await LocalTableHost.OpenRoomAsync(budgetHarness.Options, ct))
                {
                    LocalTableRuntime rt1 = room1.AuthorityForTest;
                    var turn1DiscardedAfterCheat = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    rt1.DriverForTest.AiSubmitCompletedForTest = (seat, _, result) =>
                    {
                        if (seat == 1 &&
                            result.Status == TableOperationStatus.Accepted &&
                            rt1.ActorForTest.GetAiCheatSpentThisRound(1) == 10 &&
                            !rt1.ActorForTest.HasActiveCheatOffer(1) &&
                            rt1.ActorForTest.GetRoundStateForHost().CurrentSeat != 1)
                        {
                            turn1DiscardedAfterCheat.TrySetResult(true);
                        }
                    };
                    rt1.DriverForTest.Start();
                    await LocalHostCommandTests.WithTimeoutAsync(turn1DiscardedAfterCheat.Task, ct);
                    await rt1.DriverForTest.StopAsync();
                    Assert.Equal(10, rt1.ActorForTest.GetAiCheatSpentThisRound(1));
                    Assert.Equal(110, rt1.ActorForTest.GetCheatLedgerForHost().Balances[1]);
                }

                // Reopen SAME roundId with SAME normal ScoredCheatBasic (maxSpendPerRound=10):
                // Seat 1 reaches Turn 2 CheatWindow with "sleeve-activate" available in view.Options,
                // yet restored spent=10 intercepts it cleanly -> 0 extra spend!
                var observerReopen = new RecordingObserver();
                budgetHarness.Options.OwnerInstanceId = "owner-cap-reopen";
                budgetHarness.Options.AiDecisionObserver = observerReopen;
                bool sawTurn2ActivatableCandidate = false;
                int turn2CapturedSpent = -1;
                await using (LocalTableRoom roomReopen = await LocalTableHost.OpenRoomAsync(budgetHarness.Options, ct))
                {
                    LocalTableRuntime rt2 = roomReopen.AuthorityForTest;
                    Assert.Equal(10, rt2.ActorForTest.GetAiCheatSpentThisRound(1));
                    var turn2Completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    rt2.DriverForTest.BeforeAiCheatScoreOrSubmitForTest = (seat, opp, _) =>
                    {
                        if (seat == 1 && !opp.View.IsOfferChoice &&
                            opp.View.Options.Any(o => o.OptionId == "sleeve-activate"))
                        {
                            sawTurn2ActivatableCandidate = true;
                            turn2CapturedSpent = opp.SpentThisRound;
                        }
                        return Task.CompletedTask;
                    };
                    rt2.DriverForTest.AiSubmitCompletedForTest = (seat, _, _) =>
                    {
                        if (seat == 1 && sawTurn2ActivatableCandidate)
                        {
                            turn2Completed.TrySetResult(true);
                        }
                    };
                    rt2.DriverForTest.Start();
                    await LocalHostCommandTests.WithTimeoutAsync(turn2Completed.Task, ct);
                    await rt2.DriverForTest.StopAsync();

                    Assert.True(sawTurn2ActivatableCandidate);
                    Assert.Equal(10, turn2CapturedSpent);
                    Assert.Equal(10, rt2.ActorForTest.GetAiCheatSpentThisRound(1));
                    Assert.Equal(110, rt2.ActorForTest.GetCheatLedgerForHost().Balances[1]);
                }

                // New roundId with the same profile resets GetAiCheatSpentThisRound(1) to 0 and spends again!
                await using var newRoundHarness = new P41Harness();
                newRoundHarness.Options.SeatPlan = LocalTableSeatPlan.FromHumanSeats(Array.Empty<int>());
                newRoundHarness.Options.CharacterIds = characters;
                newRoundHarness.Options.InitialRound = BuildSeat1ScoredCheatStartRound("round-m104-cap-r2");
                newRoundHarness.Options.AutoPlay = false;
                newRoundHarness.Options.Ai = budgetHarness.Options.Ai;
                await using (LocalTableRoom freshRoom = await LocalTableHost.OpenRoomAsync(newRoundHarness.Options, ct))
                {
                    LocalTableRuntime rtFresh = freshRoom.AuthorityForTest;
                    Assert.Equal(0, rtFresh.ActorForTest.GetAiCheatSpentThisRound(1));
                    rtFresh.DriverForTest.Start();
                    for (int i = 0; i < 120 && rtFresh.ActorForTest.GetAiCheatSpentThisRound(1) == 0; i++)
                    {
                        await Task.Delay(20, ct);
                    }
                    await rtFresh.DriverForTest.StopAsync();
                    Assert.Equal(10, rtFresh.ActorForTest.GetAiCheatSpentThisRound(1));
                    Assert.Equal(110, rtFresh.ActorForTest.GetCheatLedgerForHost().Balances[1]);
                }
            }

            // Part C: Old v2 .match archive (without SeatProfiles) loads cleanly and falls back to compatible default options!
            await using var v2Harness = new P41Harness();
            string v2Root = Path.Combine(v2Harness.Root, "legacy-v2-match");
            Directory.CreateDirectory(v2Root);
            string v2Path = Path.Combine(v2Root, "v2.match");
            WriteV2MatchFile(v2Path, "v2", characters);
            var compatOptions = new LocalMatchHostOptions
            {
                SaveDirectory = v2Root,
                MatchId = "v2",
                Environment = v2Harness.Options.Environment,
                Ai = new LocalAiOptions { ProfileId = LocalAiProfiles.Basic }
            };
            LocalMatchCoordinator v2Match = await LocalMatchCoordinator.OpenAsync(compatOptions, ct);
            try
            {
                Assert.Equal(characters, v2Match.Snapshot.CharacterIds);
                Assert.Null(v2Match.Snapshot.SeatProfiles);
                LocalMatchRound r = await v2Match.OpenRoundAsync(ct);
                await r.Channel.OpenAsync(new TableChannelConnectRequest(r.Ticket, "v2-p0", null), ct);
                LocalTableRuntime rtV2 = ((LocalTableChannel)r.Channel).RuntimeForMatch!;
                Assert.Contains("s1:profile=baseline-basic,strategy=baseline-basic", rtV2.DriverForTest.DescribeSeatBindingsForTest());
            }
            finally
            {
                await v2Match.DisposeAsync();
            }
        }

        // D-01-04 & Requirements 1, 4: Real TableSession submission creates paid SleeveBloomOffer (-10),
        // default Takeover (TakeoverMaySpend=false) forbids new paid initiation on an equivalent no-offer opportunity,
        // settles the exact real offer safely via IsSafeDefault with 0 extra spend, preserves 136 conserved tiles,
        // and ReclaimControl keeps rule characters intact.
        [Fact]
        public async Task TakeoverDefaultForbidsPaidInitiationSettlesAlreadyPaidOfferAndReclaimKeepsCharacters()
        {
            CancellationToken ct = TestContext.Current.CancellationToken;
            string[] characters = { "octopus", "player", "player", "player" };
            var policyNoTakeoverSpend = new AiCheatPolicy(
                enabled: true,
                maxSpendPerRound: 60,
                takeoverMaySpend: false,
                capabilityToggles: new Dictionary<string, bool> { ["tile_mark"] = false });

            // Sub-case A: Equivalent legal no-offer CheatWindow opportunity under default Takeover (TakeoverMaySpend=false) ->
            // Authority & ScoredCheatBasic forbid initiating paid cheat; balance stays 120!
            await using (var noOfferHarness = new P41Harness())
            {
                noOfferHarness.Options.SeatPlan = LocalTableSeatPlan.SingleHumanSeatZero;
                noOfferHarness.Options.CharacterIds = characters;
                noOfferHarness.Options.InitialRound = BuildSeat3DiscardStartRound("round-m104-to-nooffer");
                noOfferHarness.Options.AutoPlay = false;
                noOfferHarness.Options.Ai = new LocalAiOptions
                {
                    SeatProfiles = new[]
                    {
                        CharacterAiProfile.CreateScoredBasic("s0-oct-scored", cheatPolicy: policyNoTakeoverSpend),
                        CharacterAiProfile.CreateBasic("s1"),
                        CharacterAiProfile.CreateBasic("s2"),
                        CharacterAiProfile.CreateBasic("s3")
                    }
                };

                ITableChannel ch = await LocalTableHost.CreateChannelAsync(noOfferHarness.Options, ct);
                string ticket = LocalTableHost.IssueTicket(noOfferHarness.Options);
                await ch.OpenAsync(new TableChannelConnectRequest(ticket, "m104-to-a", null), ct);
                var session = new TableSession(ch, new InMemoryOutboxStore());
                await session.OpenAsync(new TableSessionOpenRequest(LocalTableHost.IssueTicket(noOfferHarness.Options), "m104-to-a"), ct);
                for (int i = 0; i < 20 && session.State != TableConnectionState.Ready && session.State != TableConnectionState.Faulted; i++)
                {
                    await session.PumpOnceAsync(ct);
                }
                Assert.True(session.State == TableConnectionState.Ready, $"session={session.State} err={session.LastError}");

                try
                {
                    LocalTableRuntime rt = ((LocalTableChannel)ch).RuntimeForMatch!;
                    await AdvanceSeat3DiscardToSeat0CheatWindowAsync(rt, ct);
                    Assert.Equal(TableControlStatus.Changed, (await actorForTO(rt).RequestTakeoverAsync(
                        0, new OwnerEpoch(1), new ControlEpoch(0), "disconnect-no-offer", ct)).Status);
                    TableActor actor = rt.ActorForTest;
                    static TableActor actorForTO(LocalTableRuntime r) => r.ActorForTest;

                    // Equivalent opportunity WITH takeover permission would have offered sleeve-activate,
                    // whereas default allowTakeoverPaidCheats=false blocks paid initiation.
                    TableAiCheatCaptureResult wouldAllow = await actor.CaptureAiCheatOpportunityAsync(0, true, ct);
                    Assert.True(wouldAllow.IsCaptured);
                    Assert.False(wouldAllow.Opportunity!.View.IsOfferChoice);
                    Assert.Contains(wouldAllow.Opportunity.View.Options, o => o.OptionId == "sleeve-activate");

                    TableAiCheatCaptureResult defaultTakeover = await actor.CaptureAiCheatOpportunityAsync(0, false, ct);
                    Assert.True(defaultTakeover.IsCaptured);
                    Assert.DoesNotContain(defaultTakeover.Opportunity!.View.Options, o => o.Cost > 0);

                    var s0Discarded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    rt.DriverForTest.AiSubmitCompletedForTest = (seat, _, result) =>
                    {
                        if (seat == 0 && result.Status == TableOperationStatus.Accepted &&
                            rt.ActorForTest.GetRoundStateForHost().CurrentSeat != 0)
                        {
                            s0Discarded.TrySetResult(true);
                        }
                    };
                    rt.DriverForTest.Start();
                    await LocalHostCommandTests.WithTimeoutAsync(s0Discarded.Task, ct);
                    await rt.DriverForTest.StopAsync();

                    Assert.Equal(0, actor.GetAiCheatSpentThisRound(0));
                    Assert.Equal(120, actor.GetCheatLedgerForHost().Balances[0]);
                    Assert.Equal(characters, actor.GetSeatCharacterIdsForHost());
                }
                finally
                {
                    await session.CloseAsync("TEST_DONE", CancellationToken.None);
                    await ch.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
                }
            }

            // Sub-case B: Real TableSession CommandKind.SubmitSleeveBloom creates persisted ImmediateOffer (-10 -> 110),
            // Seat 0 enters Takeover (TakeoverMaySpend=false), Driver settles the EXACT offer via IsSafeDefault with 0 extra spend,
            // and ReclaimControl bumps ControlEpoch while preserving rule characters.
            await using var harness = new P41Harness();
            var observer = new RecordingObserver();
            harness.Options.SeatPlan = LocalTableSeatPlan.SingleHumanSeatZero;
            harness.Options.CharacterIds = characters;
            harness.Options.InitialRound = BuildSeat3DiscardStartRound("round-m104-takeover");
            harness.Options.AutoPlay = false;
            harness.Options.AiDecisionObserver = observer;
            harness.Options.Ai = new LocalAiOptions
            {
                SeatProfiles = new[]
                {
                    CharacterAiProfile.CreateScoredBasic("s0-oct-scored", cheatPolicy: policyNoTakeoverSpend),
                    CharacterAiProfile.CreateBasic("s1"),
                    CharacterAiProfile.CreateBasic("s2"),
                    CharacterAiProfile.CreateBasic("s3")
                }
            };

            ITableChannel channel = await LocalTableHost.CreateChannelAsync(harness.Options, ct);
            string chTicket = LocalTableHost.IssueTicket(harness.Options);
            await channel.OpenAsync(new TableChannelConnectRequest(chTicket, "m104-to-p0", null), ct);
            var offerSession = new TableSession(channel, new InMemoryOutboxStore());
            await offerSession.OpenAsync(new TableSessionOpenRequest(LocalTableHost.IssueTicket(harness.Options), "m104-to-p0"), ct);
            using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            pumpCts.CancelAfter(TimeSpan.FromSeconds(30));
            Task pump = Task.Run(async () =>
            {
                try
                {
                    while (!pumpCts.IsCancellationRequested)
                    {
                        await offerSession.PumpOnceAsync(pumpCts.Token);
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException || ex is TableChannelClosedException || ex is InvalidOperationException)
                {
                }
            }, TestContext.Current.CancellationToken);

            try
            {
                for (int i = 0; i < 60 && offerSession.State != TableConnectionState.Ready && offerSession.State != TableConnectionState.Faulted; i++)
                {
                    await Task.Delay(10, pumpCts.Token);
                }
                Assert.True(offerSession.State == TableConnectionState.Ready, $"offerSession={offerSession.State} err={offerSession.LastError}");
                LocalTableRuntime runtimeBefore = ((LocalTableChannel)channel).RuntimeForMatch!;
                await AdvanceSeat3DiscardToSeat0CheatWindowAsync(runtimeBefore, ct);
                RoundStateMachine s0State = runtimeBefore.ActorForTest.GetRoundStateForHost();
                TableOperationResult openHuman = await runtimeBefore.ActorForTest.OpenDecisionAsync(
                    new PendingDecision(
                        new DecisionKey(s0State.RoundId, 0, "m104-human-dec-0"),
                        0L,
                        new[] { new LegalActionOption(LegalActionKind.Discard, 0, new TileId(120), optionId: "discard-120") },
                        s0State.Tiles.DrawnTile?.Tile),
                    ct);
                Assert.Equal(TableOperationStatus.Configured, openHuman.Status);
                for (int i = 0; i < 60 && (offerSession.PlayerSnapshot?.PrivateView?.Decision == null || offerSession.PlayerSnapshot.PublicView.Board.CurrentSeat != 0); i++)
                {
                    await Task.Delay(10, pumpCts.Token);
                }
                Assert.NotNull(offerSession.PlayerSnapshot?.PrivateView?.Decision);
                Assert.Equal(0, offerSession.PlayerSnapshot!.PublicView.Board.CurrentSeat);

                PlayerSnapshot snap = offerSession.PlayerSnapshot!;
                var decision = snap.PrivateView.Decision!;
                var sleevePayload = CommandPayloadFactory.SubmitSleeveBloom(snap.RoundId, decision.DecisionId, decision.ControlEpoch);
                const string requestId = "m104-human-sleeve-activate";
                var resolved = new TaskCompletionSource<TableReceiptResolvedEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler<TableReceiptResolvedEventArgs> onReceipt = (_, args) =>
                {
                    if (string.Equals(args.Command.RequestId, requestId, StringComparison.Ordinal))
                    {
                        resolved.TrySetResult(args);
                    }
                };
                offerSession.ReceiptResolved += onReceipt;
                try
                {
                    CommandSubmission submission = await offerSession.SubmitAsync(new ProtocolCommand(
                        CommandKind.SubmitSleeveBloom,
                        sleevePayload,
                        new RequestId(requestId),
                        offerSession.Binding!.Scope.ScopeId,
                        new StateVersion(snap.StateVersion),
                        new RoundId(snap.RoundId),
                        decisionId: new DecisionId(decision.DecisionId),
                        controlEpoch: new ControlEpoch(decision.ControlEpoch)), pumpCts.Token);
                    Assert.True(submission.Sent);
                    TableReceiptResolvedEventArgs receipt = await LocalHostCommandTests.WithTimeoutAsync(
                        resolved.Task,
                        pumpCts.Token);
                    Assert.Equal(Mahjong.Contracts.ReceiptStatus.Applied, receipt.Resolution.ReceiptStatus);
                }
                finally
                {
                    offerSession.ReceiptResolved -= onReceipt;
                }

                for (int i = 0; i < 60 && offerSession.PlayerSnapshot?.PrivateView?.Cheat?.SleeveBloom == null; i++)
                {
                    await Task.Delay(10, pumpCts.Token);
                }
                string realOfferId = offerSession.PlayerSnapshot!.PrivateView.Cheat!.SleeveBloom!.OfferId;
                Assert.False(string.IsNullOrEmpty(realOfferId));

                LocalTableRuntime runtime = ((LocalTableChannel)channel).RuntimeForMatch!;
                TableActor actor = runtime.ActorForTest;
                Assert.Equal(110, actor.GetCheatLedgerForHost().Balances[0]);
                Assert.True(actor.HasActiveCheatOffer(0));

                TableControlResult takeover = await actor.RequestTakeoverAsync(
                    0, new OwnerEpoch(1), new ControlEpoch(0), "disconnect-takeover", ct);
                Assert.Equal(TableControlStatus.Changed, takeover.Status);
                Assert.Equal(characters, actor.GetSeatCharacterIdsForHost());

                TableAiCheatCaptureResult offerCap = await actor.CaptureAiCheatOpportunityAsync(0, false, ct);
                Assert.True(offerCap.IsCaptured);
                Assert.True(offerCap.Opportunity!.View.IsOfferChoice);
                Assert.Equal(realOfferId, offerCap.Opportunity.OfferId);

                runtime.DriverForTest.Start();
                for (int i = 0; i < 100 && actor.HasActiveCheatOffer(0); i++)
                {
                    await Task.Delay(20, ct);
                }
                await runtime.DriverForTest.StopAsync();

                Assert.False(actor.HasActiveCheatOffer(0));
                Assert.Equal(110, actor.GetCheatLedgerForHost().Balances[0]); // 0 extra spend!
                Assert.Equal(136, TilePositionMap.Validate(actor.GetRoundStateForHost().Tiles).AssignedCount);
                Assert.Contains(observer.Snapshot(), r =>
                    r.Seat == 0 && r.DecisionKind == "cheat" && r.OptionId == "sleeve-first" && r.Status == "accepted");

                TableControlResult reclaim = await actor.ReclaimControlAsync(
                    0, new OwnerEpoch(1), new ControlEpoch(1), "reclaim", ct);
                Assert.Equal(TableControlStatus.Changed, reclaim.Status);
                Assert.Equal(2L, reclaim.Change!.NewEpoch);
                Assert.Equal(characters, actor.GetSeatCharacterIdsForHost());
            }
            finally
            {
                pumpCts.Cancel();
                await pump;
                await offerSession.CloseAsync("TEST_DONE", CancellationToken.None);
                await channel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
            }
        }

        // Requirement 1 & 4: Full all-AI round with mixed 4-seat profiles (ScoredCheatBasic, RandomCheatBasic,
        // Basic, LegacyHeuristic) completes cleanly with 136 conserved tiles, 100k zero-sum score, and
        // per-seat strategy/RNG isolation.
        [Fact]
        public async Task MixedFourSeatRoundCompletesWithConservationAndIsolatedRandomStreams()
        {
            CancellationToken ct = TestContext.Current.CancellationToken;
            await using var harness = new P41Harness();

            harness.Options.SeatPlan = LocalTableSeatPlan.FromHumanSeats(Array.Empty<int>());
            harness.Options.CharacterIds = new[] { "octopus", "player", "octopus", "player" };
            harness.Options.Ai = new LocalAiOptions
            {
                SeatProfiles = new[]
                {
                    CharacterAiProfile.CreateScoredBasic(
                        "s0-scored",
                        style: new CharacterStyle(aggression: 0.85, cheatAggression: 0.90),
                        cheatPolicy: new AiCheatPolicy(enabled: true, maxSpendPerRound: 30, reservePoints: 10)),
                    CharacterAiProfile.CreateRandomCheatBasic(
                        "s1-random",
                        cheatPolicy: new AiCheatPolicy(enabled: true, maxSpendPerRound: 20, reservePoints: 0),
                        seed: 4242UL),
                    CharacterAiProfile.CreateBasic("s2-basic"),
                    CharacterAiProfile.CreateLegacyHeuristic("s3-legacy")
                }
            };

            await using (LocalTableRoom room = await LocalTableHost.OpenRoomAsync(harness.Options, ct))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(90));
                LocalRoundSummary summary;
                do
                {
                    await Task.Delay(25, timeout.Token);
                    summary = await LocalTableHost.GetRoundSummaryAsync(harness.Options.SaveDirectory, timeout.Token);
                }
                while (!summary.Finished);

                Assert.True(summary.WallConserved, string.Join("; ", summary.ConservationErrors));
                Assert.Equal(136, summary.WallAssignedCount);
                Assert.Equal(100_000L, summary.FinalScores.Sum() + summary.RemainingRiichiSticks * 1000L);

                // Seat 2 (Basic) & Seat 3 (Legacy) never spend cheat points; Seat 0 <= 30; Seat 1 <= 20.
                Assert.InRange(120 - summary.CheatBalances[0], 0, 30);
                Assert.InRange(120 - summary.CheatBalances[1], 0, 20);
                Assert.Equal(120, summary.CheatBalances[2]);
                Assert.Equal(120, summary.CheatBalances[3]);
            }

            // Cross-seat RNG isolation proof: two RandomCheatBasic strategies on seat 1 with same seed
            // produce the exact same sequence even if another seat's strategy uses a different seed or runs more steps.
            var s1a = new RandomCheatBasicAiStrategy(new SeededAiRandom(LocalAiOptions.DeriveSeatSeed(99UL, 1)), new AiCheatPolicy(enabled: true));
            var s1b = new RandomCheatBasicAiStrategy(new SeededAiRandom(LocalAiOptions.DeriveSeatSeed(99UL, 1)), new AiCheatPolicy(enabled: true));
            var s2Other = new RandomCheatBasicAiStrategy(new SeededAiRandom(LocalAiOptions.DeriveSeatSeed(99UL, 2)), new AiCheatPolicy(enabled: true));
            var opp1 = new AiCheatOpportunity(
                1, "r-iso", 1, 0, 120, false,
                new[]
                {
                    new AiCheatOption("sleeve-activate", "sleeve_bloom", 10, false, false),
                    new AiCheatOption("beam-activate", "beam_swap", 10, false, false)
                });
            var opp2 = new AiCheatOpportunity(
                2, "r-iso", 1, 0, 120, false,
                new[]
                {
                    new AiCheatOption("sleeve-activate", "sleeve_bloom", 10, false, false),
                    new AiCheatOption("beam-activate", "beam_swap", 10, false, false)
                });

            var seqA = new List<string?>();
            var seqB = new List<string?>();
            for (int i = 0; i < 15; i++)
            {
                seqA.Add(s1a.ChooseCheat(opp1));
                _ = s2Other.ChooseCheat(opp2);
                _ = s2Other.ChooseCheat(opp2);
                seqB.Add(s1b.ChooseCheat(opp1));
            }

            Assert.Equal(seqA, seqB);
        }

        // D-01-01 (Authoritative atomic capture, visibility filtering, concurrency race, and activation SleeveDrawKind):
        // 1) Paired host/actor negative: changing another seat's ClosedKan tiles (including Seat 0's winning wait) and
        //    hidden unmarked wall slots produces identical VisibleContext, identical EV scores, and identical choice;
        //    other seat's ClosedKan never enters VisibleContext.VisibleMelds.
        // 2) Deterministic concurrency negative: advancing authority state between capture and scoring keeps captured
        //    StateVersion frozen, actor rejects stale plan, and ledger/136 tiles stay untouched.
        // 3) Activation SleeveDrawKind (Normal vs Rinshan) comes from the same frozen capture without leaking hidden tiles.
        [Fact]
        public async Task ScoredCaptureAtomicConcurrencyPrivacyAndDrawKindChecks()
        {
            CancellationToken ct = TestContext.Current.CancellationToken;

            // 1. Paired visibility negative: Seat 2 ClosedKan 4p(48..51) + hidden wall {77,78,79} vs
            //    Seat 2 ClosedKan 3s(80..83) + hidden wall {37,41,45}. Neither leaks into Seat 0's VisibleContext!
            RoundStateMachine roundKanA = BuildRoundWithSeat2ClosedKan("round-d0101-kana", new[] { 48, 49, 50, 51 }, new[] { 77, 78, 79 });
            RoundStateMachine roundKanB = BuildRoundWithSeat2ClosedKan("round-d0101-kanb", new[] { 80, 81, 82, 83 }, new[] { 37, 41, 45 });

            var seat0Profile = CharacterAiProfile.CreateScoredBasic(
                "s0-oct",
                style: new CharacterStyle(aggression: 0.9, cheatAggression: 0.9),
                cheatPolicy: new AiCheatPolicy(
                    enabled: true,
                    maxSpendPerRound: 60,
                    capabilityToggles: new Dictionary<string, bool> { ["tile_mark"] = false }));

            TableAiCheatOpportunity capA;
            TableAiCheatOpportunity capB;
            await using (var hA = new P41Harness())
            await using (var hB = new P41Harness())
            {
                hA.Options.SeatPlan = LocalTableSeatPlan.FromHumanSeats(Array.Empty<int>());
                hA.Options.CharacterIds = new[] { "octopus", "player", "player", "player" };
                hA.Options.InitialRound = roundKanA;
                hA.Options.AutoPlay = false;

                hB.Options.SeatPlan = LocalTableSeatPlan.FromHumanSeats(Array.Empty<int>());
                hB.Options.CharacterIds = new[] { "octopus", "player", "player", "player" };
                hB.Options.InitialRound = roundKanB;
                hB.Options.AutoPlay = false;

                await using LocalTableRoom roomA = await LocalTableHost.OpenRoomAsync(hA.Options, ct);
                await using LocalTableRoom roomB = await LocalTableHost.OpenRoomAsync(hB.Options, ct);
                TableAiCheatCaptureResult resA = await roomA.AuthorityForTest.ActorForTest.CaptureAiCheatOpportunityAsync(0, false, ct);
                TableAiCheatCaptureResult resB = await roomB.AuthorityForTest.ActorForTest.CaptureAiCheatOpportunityAsync(0, false, ct);
                Assert.True(resA.IsCaptured);
                Assert.True(resB.IsCaptured);
                capA = resA.Opportunity!;
                capB = resB.Opportunity!;
            }

            Assert.DoesNotContain(capA.VisibleContext!.VisibleMelds, m => m.Seat == 2);
            Assert.DoesNotContain(capB.VisibleContext!.VisibleMelds, m => m.Seat == 2);
            Assert.Equal(capA.VisibleContext.OwnHandTileIds, capB.VisibleContext.OwnHandTileIds);
            Assert.Equal(capA.VisibleContext.VisibleDiscards.Select(d => d.Tile.Value), capB.VisibleContext.VisibleDiscards.Select(d => d.Tile.Value));
            Assert.Equal(capA.VisibleContext.VisibleDoraIndicators, capB.VisibleContext.VisibleDoraIndicators);
            Assert.Equal(capA.View.SleeveDrawKind, capB.View.SleeveDrawKind);

            var stratA = new ScoredCheatBasicAiStrategy(seat0Profile);
            var stratB = new ScoredCheatBasicAiStrategy(seat0Profile);
            IReadOnlyList<ScoredCheatOptionEvaluation> evalA = stratA.EvaluateCheatOptions(capA.View, capA.VisibleContext);
            IReadOnlyList<ScoredCheatOptionEvaluation> evalB = stratB.EvaluateCheatOptions(capB.View, capB.VisibleContext);
            Assert.Equal(evalA.Count, evalB.Count);
            for (int i = 0; i < evalA.Count; i++)
            {
                Assert.Equal(evalA[i].OptionId, evalB[i].OptionId);
                Assert.Equal(evalA[i].Score, evalB[i].Score, precision: 6);
                Assert.Equal(evalA[i].ExpectedOrActualGain, evalB[i].ExpectedOrActualGain, precision: 6);
            }
            Assert.Equal(stratA.ChooseCheat(capA.View, capA.VisibleContext), stratB.ChooseCheat(capB.View, capB.VisibleContext));

            // 2. Deterministic concurrency negative: advance actor state AFTER CaptureAiCheatOpportunityAsync
            //    and BEFORE ScoredCheatBasicAiStrategy scores/submits -> capture version stays V, actor rejects stale plan,
            //    and ledger + 136 tiles have 0 side effects.
            await using (var raceHarness = new P41Harness())
            {
                var observer = new RecordingObserver();
                raceHarness.Options.SeatPlan = LocalTableSeatPlan.FromHumanSeats(Array.Empty<int>());
                raceHarness.Options.CharacterIds = new[] { "player", "octopus", "player", "player" };
                raceHarness.Options.InitialRound = BuildSeat1ScoredCheatStartRound("round-d0101-race");
                raceHarness.Options.AutoPlay = false;
                raceHarness.Options.AiDecisionObserver = observer;
                raceHarness.Options.Ai = new LocalAiOptions
                {
                    SeatProfiles = new[]
                    {
                        CharacterAiProfile.CreateBasic("s0"),
                        seat0Profile,
                        CharacterAiProfile.CreateBasic("s2"),
                        CharacterAiProfile.CreateBasic("s3")
                    }
                };

                await using LocalTableRoom raceRoom = await LocalTableHost.OpenRoomAsync(raceHarness.Options, ct);
                LocalTableRuntime rt = raceRoom.AuthorityForTest;
                long capturedVersion = -1;
                string? raceError = null;
                var raceSubmitted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                bool injectedRace = false;

                rt.DriverForTest.BeforeAiCheatScoreOrSubmitForTest = async (seat, opp, _) =>
                {
                    if (seat == 1 && !injectedRace && !opp.View.IsOfferChoice && opp.View.Options.Count > 0)
                    {
                        injectedRace = true;
                        capturedVersion = opp.StateVersion;
                        // Advance authoritative table state concurrently from Seat 1 CheatWindow(V) -> Seat 2(V+1) before scoring!
                        TileId discardTile = opp.VisibleContext!.OwnHandTileIds[0];
                        TablePayloadSha256 advHash = TablePayloadSha256.Compute(
                            Encoding.UTF8.GetBytes("race-adv-discard|" + opp.RoundId + "|" + capturedVersion));
                        var envelope = new TableCommandEnvelope(
                            RoundCommand.Discard(opp.RoundId, 1, discardTile, Array.Empty<int>()),
                            new RequestId("race-adv-discard"),
                            new StateVersion(checked((ulong)capturedVersion)),
                            opp.OwnerEpoch,
                            new ControlEpoch(checked((ulong)opp.ControlEpoch)),
                            opp.DecisionId,
                            opp.RecoveryGeneration,
                            rt.DriverForTest.ScopeForSeatForTest(1),
                            "RACE_ADVANCE_DISCARD",
                            advHash);
                        TableOperationResult adv = await rt.ActorForTest.SubmitAsync(envelope, ct).ConfigureAwait(false);
                        if (adv.Status != TableOperationStatus.Accepted)
                        {
                            raceError = $"adv={adv.Status}:{adv.ReasonCode}:{adv.Detail}";
                            raceSubmitted.TrySetResult(false);
                            return;
                        }
                        Assert.True(opp.StateVersion == capturedVersion, "Frozen capture version must stay immutable.");
                        Assert.Equal(2, rt.ActorForTest.GetRoundStateForHost().CurrentSeat);
                        TableSnapshot advancedSnap = await rt.ActorForTest.GetSnapshotAsync(ct).ConfigureAwait(false);
                        Assert.True(advancedSnap.StateVersion.Value > (ulong)capturedVersion);
                    }
                };
                rt.DriverForTest.AiSubmitCompletedForTest = (seat, _, _) =>
                {
                    if (injectedRace && seat == 2)
                    {
                        raceSubmitted.TrySetResult(true);
                    }
                };

                rt.DriverForTest.Start();
                await LocalHostCommandTests.WithTimeoutAsync(raceSubmitted.Task, ct);
                await rt.DriverForTest.StopAsync();

                Assert.Null(raceError);
                Assert.True(injectedRace);
                Assert.Contains(observer.Snapshot(), r =>
                    r.Seat == 1 && r.DecisionKind == "cheat-fallback" &&
                    r.Status == "rejected" && r.StateVersion == capturedVersion);
                Assert.Equal(0, rt.ActorForTest.GetAiCheatSpentThisRound(1));
                Assert.Equal(120, rt.ActorForTest.GetCheatLedgerForHost().Balances[1]);
                Assert.Equal(136, TilePositionMap.Validate(rt.ActorForTest.GetRoundStateForHost().Tiles).AssignedCount);
            }

            // 3. Normal vs Rinshan SleeveDrawKind populated atomically from same capture & yields distinct EV scores.
            RoundStateMachine normalRound = BuildSeat0ClosedKanNormalVsRinshanRound("round-d0101-norm", forRinshan: false);
            RoundStateMachine rinshanRound = BuildSeat0ClosedKanNormalVsRinshanRound("round-d0101-rins", forRinshan: true);
            await using (var hNorm = new P41Harness())
            await using (var hRins = new P41Harness())
            {
                hNorm.Options.SeatPlan = LocalTableSeatPlan.FromHumanSeats(Array.Empty<int>());
                hNorm.Options.CharacterIds = new[] { "octopus", "player", "player", "player" };
                hNorm.Options.InitialRound = normalRound;
                hNorm.Options.AutoPlay = false;

                hRins.Options.SeatPlan = LocalTableSeatPlan.FromHumanSeats(Array.Empty<int>());
                hRins.Options.CharacterIds = new[] { "octopus", "player", "player", "player" };
                hRins.Options.InitialRound = rinshanRound;
                hRins.Options.AutoPlay = false;

                await using LocalTableRoom roomN = await LocalTableHost.OpenRoomAsync(hNorm.Options, ct);
                await using LocalTableRoom roomR = await LocalTableHost.OpenRoomAsync(hRins.Options, ct);
                TableAiCheatCaptureResult capN = await roomN.AuthorityForTest.ActorForTest.CaptureAiCheatOpportunityAsync(0, false, ct);
                TableAiCheatCaptureResult capR = await roomR.AuthorityForTest.ActorForTest.CaptureAiCheatOpportunityAsync(0, false, ct);
                Assert.True(capN.IsCaptured);
                Assert.True(capR.IsCaptured);
                Assert.Equal(AiSleeveBloomDrawKind.Normal, capN.Opportunity!.View.SleeveDrawKind);
                Assert.Equal(AiSleeveBloomDrawKind.Rinshan, capR.Opportunity!.View.SleeveDrawKind);

                var evalStrat = new ScoredCheatBasicAiStrategy(seat0Profile);
                ScoredCheatOptionEvaluation nEval = evalStrat.EvaluateCheatOptions(capN.Opportunity.View, capN.Opportunity.VisibleContext)
                    .Single(e => e.OptionId == "sleeve-activate");
                ScoredCheatOptionEvaluation rEval = evalStrat.EvaluateCheatOptions(capR.Opportunity.View, capR.Opportunity.VisibleContext)
                    .Single(e => e.OptionId == "sleeve-activate");
                Assert.True(nEval.Score > rEval.Score, $"Normal={nEval.Score} must exceed Rinshan={rEval.Score}");
            }
        }

        // D-01-02 & D-01-03: Legacy single-profile callers preserve their ordinary strategy in default Takeover
        // (forbidding paid spend, honoring explicit TakeoverProfile override), and omitted per-seat fallback
        // respects host FallbackProfileId rather than being shadowed by Basic.
        [Fact]
        public async Task LegacySingleProfileTakeoverPreservesOrdinaryStrategyAndFallbackPrecedenceWorks()
        {
            CancellationToken ct = TestContext.Current.CancellationToken;

            // 1. Default single profile (LegacyHeuristic): normal + takeover both stay legacy-heuristic, takeoverSpend=False!
            await using (var hLegacy = new P41Harness())
            {
                hLegacy.Options.SeatPlan = LocalTableSeatPlan.SingleHumanSeatZero;
                hLegacy.Options.CharacterIds = new[] { "octopus", "player", "player", "player" };
                hLegacy.Options.InitialRound = BuildSeat3DiscardStartRound("round-d0102-legacy");
                hLegacy.Options.AutoPlay = false;

                ITableChannel ch = await LocalTableHost.CreateChannelAsync(hLegacy.Options, ct);
                await ch.OpenAsync(new TableChannelConnectRequest(LocalTableHost.IssueTicket(hLegacy.Options), "d0102-p0", null), ct);
                var session = new TableSession(ch, new InMemoryOutboxStore());
                await session.OpenAsync(new TableSessionOpenRequest(LocalTableHost.IssueTicket(hLegacy.Options), "d0102-p0"), ct);
                for (int i = 0; i < 20 && session.State != TableConnectionState.Ready && session.State != TableConnectionState.Faulted; i++)
                {
                    await session.PumpOnceAsync(ct);
                }
                Assert.True(session.State == TableConnectionState.Ready, $"session={session.State} err={session.LastError}");
                try
                {
                    LocalTableRuntime rt = ((LocalTableChannel)ch).RuntimeForMatch!;
                    await AdvanceSeat3DiscardToSeat0CheatWindowAsync(rt, ct);
                    string desc = rt.DriverForTest.DescribeSeatBindingsForTest();
                    Assert.Contains("s0:profile=legacy-heuristic,strategy=legacy-heuristic,degraded=False,cheats=False,takeoverStrategy=legacy-heuristic,takeoverSpend=False", desc);

                    Assert.Equal(TableControlStatus.Changed, (await rt.ActorForTest.RequestTakeoverAsync(
                        0, new OwnerEpoch(1), new ControlEpoch(0), "legacy-to", ct)).Status);
                    var s0Moved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    rt.DriverForTest.AiSubmitCompletedForTest = (seat, _, result) =>
                    {
                        if (seat == 0 && result.Status == TableOperationStatus.Accepted &&
                            rt.ActorForTest.GetRoundStateForHost().CurrentSeat != 0)
                        {
                            s0Moved.TrySetResult(true);
                        }
                    };
                    rt.DriverForTest.Start();
                    await LocalHostCommandTests.WithTimeoutAsync(s0Moved.Task, ct);
                    await rt.DriverForTest.StopAsync();
                    Assert.Equal(0, rt.ActorForTest.GetAiCheatSpentThisRound(0));
                }
                finally
                {
                    await session.CloseAsync("TEST_DONE", CancellationToken.None);
                    await ch.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
                }
            }

            // 2. Basic & RandomCheatBasic single-profile takeover bindings + explicit TakeoverProfile override.
            await using (var hOther = new P41Harness())
            {
                hOther.Options.SeatPlan = LocalTableSeatPlan.FromHumanSeats(Array.Empty<int>());
                hOther.Options.Ai = new LocalAiOptions { ProfileId = LocalAiProfiles.Basic };
                await using (LocalTableRoom rBasic = await LocalTableHost.OpenRoomAsync(hOther.Options, ct))
                {
                    Assert.Contains("s0:profile=baseline-basic,strategy=baseline-basic,degraded=False,cheats=False,takeoverStrategy=baseline-basic,takeoverSpend=False",
                        rBasic.AuthorityForTest.DriverForTest.DescribeSeatBindingsForTest());
                }
            }

            await using (var hRand = new P41Harness())
            {
                hRand.Options.SeatPlan = LocalTableSeatPlan.FromHumanSeats(Array.Empty<int>());
                hRand.Options.Ai = new LocalAiOptions
                {
                    ProfileId = LocalAiProfiles.RandomCheatBasic,
                    AllowCheats = true,
                    Seed = 77UL
                };
                await using (LocalTableRoom rRand = await LocalTableHost.OpenRoomAsync(hRand.Options, ct))
                {
                    Assert.Contains("s0:profile=baseline-basic-random-cheat,strategy=baseline-basic-random-cheat,degraded=False,cheats=True,takeoverStrategy=baseline-basic-random-cheat,takeoverSpend=False",
                        rRand.AuthorityForTest.DriverForTest.DescribeSeatBindingsForTest());
                }
            }

            await using (var hOverride = new P41Harness())
            {
                hOverride.Options.SeatPlan = LocalTableSeatPlan.SingleHumanSeatZero;
                hOverride.Options.Ai = new LocalAiOptions
                {
                    ProfileId = LocalAiProfiles.LegacyHeuristic,
                    TakeoverProfile = CharacterAiProfile.CreateBasic("explicit-takeover")
                };
                await using (LocalTableRoom rOv = await LocalTableHost.OpenRoomAsync(hOverride.Options, ct))
                {
                    Assert.Contains("s0:profile=legacy-heuristic,strategy=legacy-heuristic,degraded=False,cheats=False,takeoverStrategy=baseline-basic,takeoverSpend=False",
                        rOv.AuthorityForTest.DriverForTest.DescribeSeatBindingsForTest());
                }
            }

            // 3. Fallback precedence (D-01-03): omitted per-seat fallback uses host FallbackProfileId=LegacyHeuristic;
            //    explicit per-seat fallback=Basic overrides host LegacyHeuristic; default host fallback stays Basic.
            await using (var hFb = new P41Harness())
            {
                hFb.Options.SeatPlan = LocalTableSeatPlan.FromHumanSeats(Array.Empty<int>());
                hFb.Options.Ai = new LocalAiOptions
                {
                    SeatProfiles = new[]
                    {
                        new CharacterAiProfile("bad-null-fb", strategyId: null),
                        CharacterAiProfile.CreateScoredBasic("scored-bad-model-host-fb", modelVersion: "onnx-v99"),
                        CharacterAiProfile.CreateScoredBasic("scored-explicit-basic-fb", modelVersion: "onnx-v99", fallbackStrategyId: LocalAiProfiles.Basic),
                        new CharacterAiProfile("bad-invalid-fb", strategyId: "unknown-id", fallbackStrategyId: "not-a-strategy")
                    },
                    FallbackProfileId = LocalAiProfiles.LegacyHeuristic
                };
                await using (LocalTableRoom rFb = await LocalTableHost.OpenRoomAsync(hFb.Options, ct))
                {
                    string desc = rFb.AuthorityForTest.DriverForTest.DescribeSeatBindingsForTest();
                    Assert.Contains("s0:profile=bad-null-fb,strategy=legacy-heuristic,degraded=True", desc);
                    Assert.Contains("s1:profile=scored-bad-model-host-fb,strategy=legacy-heuristic,degraded=True", desc);
                    Assert.Contains("s2:profile=scored-explicit-basic-fb,strategy=baseline-basic,degraded=True", desc);
                    Assert.Contains("s3:profile=bad-invalid-fb,strategy=legacy-heuristic,degraded=True", desc);
                }
            }
        }

        // D-01-04 Host policy control matrix: Enabled, CapabilityToggles, ReservePoints, and MaxSpendPerRound
        // block paid initiation when restricted, allow paid activation under positive baseline, and still settle
        // real persisted paid offers safely with 0 extra deduction and 136 conserved tiles.
        [Fact]
        public async Task HostCheatPolicyGatesControlPaidActivationAndSettlePaidOffersSafely()
        {
            CancellationToken ct = TestContext.Current.CancellationToken;
            string[] characters = { "player", "octopus", "player", "player" };

            async Task<int> RunSingleTurnAndGetSpentAsync(AiCheatPolicy policy)
            {
                await using var h = new P41Harness();
                h.Options.SeatPlan = LocalTableSeatPlan.FromHumanSeats(Array.Empty<int>());
                h.Options.CharacterIds = characters;
                h.Options.InitialRound = BuildSeat1ScoredCheatStartRound("round-policy-" + Guid.NewGuid().ToString("N"));
                h.Options.AutoPlay = false;
                h.Options.Ai = new LocalAiOptions
                {
                    SeatProfiles = new[]
                    {
                        CharacterAiProfile.CreateBasic("s0"),
                        CharacterAiProfile.CreateScoredBasic(
                            "s1-gate",
                            style: new CharacterStyle(aggression: 0.9, cheatAggression: 0.9),
                            cheatPolicy: policy),
                        CharacterAiProfile.CreateBasic("s2"),
                        CharacterAiProfile.CreateBasic("s3")
                    }
                };

                await using LocalTableRoom room = await LocalTableHost.OpenRoomAsync(h.Options, ct);
                LocalTableRuntime rt = room.AuthorityForTest;
                var s1Discarded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                rt.DriverForTest.AiSubmitCompletedForTest = (seat, obs, result) =>
                {
                    if (seat == 1 && result.Status == TableOperationStatus.Accepted &&
                        rt.ActorForTest.GetRoundStateForHost().CurrentSeat != 1 &&
                        !rt.ActorForTest.HasActiveCheatOffer(1))
                    {
                        s1Discarded.TrySetResult(true);
                    }
                };
                rt.DriverForTest.Start();
                await LocalHostCommandTests.WithTimeoutAsync(s1Discarded.Task, ct);
                await rt.DriverForTest.StopAsync();
                Assert.Equal(136, TilePositionMap.Validate(rt.ActorForTest.GetRoundStateForHost().Tiles).AssignedCount);
                return rt.ActorForTest.GetAiCheatSpentThisRound(1);
            }

            // Positive baseline (sleeve_bloom allowed, maxSpend=10, reserve=0) spends 10.
            Assert.Equal(10, await RunSingleTurnAndGetSpentAsync(new AiCheatPolicy(
                enabled: true, maxSpendPerRound: 10, reservePoints: 0,
                capabilityToggles: new Dictionary<string, bool> { ["tile_mark"] = false })));

            // Negative controls block activation (0 spend):
            Assert.Equal(0, await RunSingleTurnAndGetSpentAsync(new AiCheatPolicy(
                enabled: false, maxSpendPerRound: 60, reservePoints: 0)));
            Assert.Equal(0, await RunSingleTurnAndGetSpentAsync(new AiCheatPolicy(
                enabled: true, maxSpendPerRound: 60, reservePoints: 0,
                capabilityToggles: new Dictionary<string, bool> { ["sleeve_bloom"] = false, ["tile_mark"] = false })));
            Assert.Equal(0, await RunSingleTurnAndGetSpentAsync(new AiCheatPolicy(
                enabled: true, maxSpendPerRound: 60, reservePoints: 115,
                capabilityToggles: new Dictionary<string, bool> { ["tile_mark"] = false })));
            Assert.Equal(0, await RunSingleTurnAndGetSpentAsync(new AiCheatPolicy(
                enabled: true, maxSpendPerRound: 60, reservePoints: 120)));
            Assert.Equal(0, await RunSingleTurnAndGetSpentAsync(new AiCheatPolicy(
                enabled: true, maxSpendPerRound: 0, reservePoints: 0)));

            // Verify a real persisted paid offer IS still settled via IsSafeDefault when reopened with
            // disabled capabilities / Enabled=false / MaxSpendPerRound=0!
            AiCheatPolicy[] offerBlockPolicies =
            {
                new AiCheatPolicy(enabled: false, maxSpendPerRound: 60),
                new AiCheatPolicy(enabled: true, maxSpendPerRound: 60, capabilityToggles: new Dictionary<string, bool> { ["sleeve_bloom"] = false }),
                new AiCheatPolicy(enabled: true, maxSpendPerRound: 0)
            };

            foreach (AiCheatPolicy settlePolicy in offerBlockPolicies)
            {
                await using var h = new P41Harness();
                h.Options.SeatPlan = LocalTableSeatPlan.FromHumanSeats(Array.Empty<int>());
                h.Options.CharacterIds = characters;
                h.Options.InitialRound = BuildSeat1ScoredCheatStartRound("round-offer-gate-" + Guid.NewGuid().ToString("N"));
                h.Options.AutoPlay = false;
                h.Options.Ai = new LocalAiOptions
                {
                    SeatProfiles = new[]
                    {
                        CharacterAiProfile.CreateBasic("s0"),
                        CharacterAiProfile.CreateScoredBasic(
                            "s1-open-offer",
                            style: new CharacterStyle(aggression: 0.9, cheatAggression: 0.9),
                            cheatPolicy: new AiCheatPolicy(enabled: true, maxSpendPerRound: 10, capabilityToggles: new Dictionary<string, bool> { ["tile_mark"] = false })),
                        CharacterAiProfile.CreateBasic("s2"),
                        CharacterAiProfile.CreateBasic("s3")
                    }
                };

                string realOfferId;
                var openedTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                await using (LocalTableRoom room = await LocalTableHost.OpenRoomAsync(h.Options, ct))
                {
                    LocalTableRuntime rt = room.AuthorityForTest;
                    rt.DriverForTest.BeforeAiCheatScoreOrSubmitForTest = async (seat, opp, stopToken) =>
                    {
                        if (seat == 1 && opp.View.IsOfferChoice && !string.IsNullOrEmpty(opp.OfferId))
                        {
                            openedTcs.TrySetResult(opp.OfferId!);
                            try { await Task.Delay(Timeout.InfiniteTimeSpan, stopToken).ConfigureAwait(false); }
                            catch (OperationCanceledException) { }
                        }
                    };
                    rt.DriverForTest.Start();
                    realOfferId = await LocalHostCommandTests.WithTimeoutAsync(openedTcs.Task, ct);
                    await rt.DriverForTest.StopAsync();
                    Assert.Equal(10, rt.ActorForTest.GetAiCheatSpentThisRound(1));
                }

                var observer = new RecordingObserver();
                h.Options.OwnerInstanceId = "owner-offer-settle";
                h.Options.AiDecisionObserver = observer;
                h.Options.Ai = new LocalAiOptions
                {
                    SeatProfiles = new[]
                    {
                        CharacterAiProfile.CreateBasic("s0"),
                        CharacterAiProfile.CreateScoredBasic("s1-restricted-settle", cheatPolicy: settlePolicy),
                        CharacterAiProfile.CreateBasic("s2"),
                        CharacterAiProfile.CreateBasic("s3")
                    }
                };
                await using (LocalTableRoom reopened = await LocalTableHost.OpenRoomAsync(h.Options, ct))
                {
                    LocalTableRuntime rt2 = reopened.AuthorityForTest;
                    Assert.True(rt2.ActorForTest.HasActiveCheatOffer(1));
                    rt2.DriverForTest.BeforeAiCheatScoreOrSubmitForTest = (seat, opp, _) =>
                    {
                        if (seat == 1 && opp.View.IsOfferChoice) Assert.Equal(realOfferId, opp.OfferId);
                        return Task.CompletedTask;
                    };
                    rt2.DriverForTest.Start();
                    for (int i = 0; i < 100 && rt2.ActorForTest.HasActiveCheatOffer(1); i++)
                    {
                        await Task.Delay(20, ct);
                    }
                    await rt2.DriverForTest.StopAsync();

                    Assert.False(rt2.ActorForTest.HasActiveCheatOffer(1));
                    Assert.Equal(10, rt2.ActorForTest.GetAiCheatSpentThisRound(1));
                    Assert.Equal(110, rt2.ActorForTest.GetCheatLedgerForHost().Balances[1]);
                    Assert.Equal(136, TilePositionMap.Validate(rt2.ActorForTest.GetRoundStateForHost().Tiles).AssignedCount);
                    Assert.Contains(observer.Snapshot(), r =>
                        r.Seat == 1 && r.DecisionKind == "cheat" && r.CapabilityId == "sleeve_bloom" && r.Status == "accepted");
                }
            }
        }

        private static RoundStateMachine BuildSeat1ScoredCheatStartRound(string roundId)
        {
            // Seat 0 holds 13 tiles + isolated 135; discards 135.
            // Seat 1 holds 1m2m3m 4m5m6m 7m8m9m 1s1s1s 2s (tenpai!) and draws North(120) at slot 53 ->
            // Turn 1 & Turn 2 wall draws (54..61) are isolated honors so Turn 2 stays at 1-shanten without winning.
            var specific = new Dictionary<int, int>();
            int[] seat0 = { 108, 109, 110, 112, 113, 114, 116, 117, 118, 124, 125, 128, 129, 135 };
            int[] s0Slots = { 0, 1, 2, 3, 16, 17, 18, 19, 32, 33, 34, 35, 48, 52 };
            int[] seat1 = { 0, 4, 8, 12, 16, 20, 24, 28, 32, 72, 73, 74, 76 };
            int[] s1Slots = { 4, 5, 6, 7, 20, 21, 22, 23, 36, 37, 38, 39, 49 };
            for (int i = 0; i < s0Slots.Length; i++) specific[s0Slots[i]] = seat0[i];
            for (int i = 0; i < s1Slots.Length; i++) specific[s1Slots[i]] = seat1[i];
            specific[53] = 120;
            specific[54] = 121;
            specific[55] = 122;
            specific[56] = 123;
            specific[57] = 119;
            specific[58] = 115;
            specific[59] = 134;
            specific[60] = 133;
            specific[61] = 132;

            TileId[] order = BuildWallOrder(specific);
            TilePositionMap dealt = TilePositionMap.CreateDealt(RecordedRandomDraw.Create(roundId + "-wall", order), 0);
            return RoundStateMachine.CreateDealt(
                new RoundSpec(roundId, 0, 1, 0, 0, new long[] { 25_000, 25_000, 25_000, 25_000 }, 0),
                dealt,
                RoundPhase.CheatWindow);
        }

        private static RoundStateMachine BuildSeat3DiscardStartRound(string roundId)
        {
            var specific = new Dictionary<int, int>();
            // Dealer is 3 (initial CommittedVersion=0); once Seat 3 discards 135 via Driver, Seat 0 draws North(120) at slot 53 (DrawSource.Normal).
            int[] seat3 = { 108, 109, 110, 112, 113, 114, 116, 117, 118, 124, 125, 128, 129, 135 };
            int[] s3Slots = { 0, 1, 2, 3, 16, 17, 18, 19, 32, 33, 34, 35, 48, 52 };
            int[] seat0 = { 0, 4, 8, 12, 16, 20, 24, 28, 32, 72, 73, 74, 76 };
            int[] s0Slots = { 4, 5, 6, 7, 20, 21, 22, 23, 36, 37, 38, 39, 49 };
            for (int i = 0; i < s3Slots.Length; i++) specific[s3Slots[i]] = seat3[i];
            for (int i = 0; i < s0Slots.Length; i++) specific[s0Slots[i]] = seat0[i];
            specific[53] = 120;
            specific[54] = 121;
            specific[55] = 122;
            specific[56] = 123;

            TilePositionMap dealt = TilePositionMap.CreateDealt(RecordedRandomDraw.Create(roundId + "-wall", BuildWallOrder(specific)), 3);
            return RoundStateMachine.CreateDealt(
                new RoundSpec(roundId, 3, 1, 0, 0, new long[] { 25_000, 25_000, 25_000, 25_000 }, 0),
                dealt,
                RoundPhase.Discard);
        }

        private static async Task AdvanceSeat3DiscardToSeat0CheatWindowAsync(LocalTableRuntime rt, CancellationToken ct)
        {
            // Submit Seat 3's discard directly through TableActor before DriverForTest.Start() is called
            // so DriverForTest remains unstarted until takeover/settlement verification begins.
            TableAiCheatCaptureResult seat3Cap = await rt.ActorForTest.CaptureAiCheatOpportunityAsync(3, false, ct);
            Assert.True(seat3Cap.IsCaptured && seat3Cap.Opportunity != null);
            TableAiCheatOpportunity opp = seat3Cap.Opportunity!;
            TablePayloadSha256 hash = TablePayloadSha256.Compute(
                Encoding.UTF8.GetBytes("seat3-setup-discard|" + opp.RoundId + "|" + opp.StateVersion));
            var envelope = new TableCommandEnvelope(
                RoundCommand.Discard(opp.RoundId, 3, new TileId(135), Array.Empty<int>()),
                new RequestId("seat3-setup-discard-" + Guid.NewGuid().ToString("N")),
                new StateVersion(checked((ulong)opp.StateVersion)),
                opp.OwnerEpoch,
                new ControlEpoch(checked((ulong)opp.ControlEpoch)),
                opp.DecisionId,
                opp.RecoveryGeneration,
                rt.DriverForTest.ScopeForSeatForTest(3),
                "SETUP_DISCARD",
                hash);
            TableOperationResult res = await rt.ActorForTest.SubmitAsync(envelope, ct);
            Assert.Equal(TableOperationStatus.Accepted, res.Status);

            RoundStateMachine cur = rt.ActorForTest.GetRoundStateForHost();
            Assert.Equal(0, cur.CurrentSeat);
            Assert.Equal(RoundPhase.CheatWindow, cur.Phase);
            Assert.Equal(DrawSource.Wall, cur.Tiles.DrawnTile?.Source);
        }

        private static RoundStateMachine BuildRoundWithSeat2ClosedKan(string roundId, int[] kanTiles, int[] extraWallHead)
        {
            var specific = new Dictionary<int, int>();
            int[] s2Hand = { kanTiles[0], kanTiles[1], kanTiles[2], kanTiles[3], 108, 109, 112, 113, 116, 117, 124, 125, 128, 135 };
            int[] s2Slots = { 0, 1, 2, 3, 16, 17, 18, 19, 32, 33, 34, 35, 48, 52 };
            int[] s3Hand = { 110, 114, 118, 126, 129, 132, 133, 40, 44, 56, 60, 64, 134 };
            int[] s3Slots = { 4, 5, 6, 7, 20, 21, 22, 23, 36, 37, 38, 39, 49 };
            int[] s0Hand = { 0, 4, 8, 12, 16, 20, 24, 28, 32, 72, 73, 74, 76 };
            int[] s0Slots = { 8, 9, 10, 11, 24, 25, 26, 27, 40, 41, 42, 43, 50 };
            int[] s1Hand = { 1, 2, 3, 6, 7, 10, 11, 14, 15, 18, 19, 22, 23 };
            int[] s1Slots = { 12, 13, 14, 15, 28, 29, 30, 31, 44, 45, 46, 47, 51 };
            for (int i = 0; i < s2Slots.Length; i++) specific[s2Slots[i]] = s2Hand[i];
            for (int i = 0; i < s3Slots.Length; i++) specific[s3Slots[i]] = s3Hand[i];
            for (int i = 0; i < s0Slots.Length; i++) specific[s0Slots[i]] = s0Hand[i];
            for (int i = 0; i < s1Slots.Length; i++) specific[s1Slots[i]] = s1Hand[i];
            specific[122] = 130; // Seat 2 Rinshan draw
            specific[53] = 131;  // Seat 3 draw
            specific[54] = 120;  // Seat 0 draw (Normal)
            specific[55] = extraWallHead[0];
            specific[56] = extraWallHead[1];
            specific[57] = extraWallHead[2];
            specific[126] = 115; // Dora indicator (South)

            TilePositionMap dealt = TilePositionMap.CreateDealt(RecordedRandomDraw.Create(roundId + "-wall", BuildWallOrder(specific)), 2);
            RoundStateMachine state = RoundStateMachine.CreateDealt(
                new RoundSpec(roundId, 2, 1, 0, 0, new long[] { 25_000, 25_000, 25_000, 25_000 }, 0),
                dealt,
                RoundPhase.Discard);
            state = ExecuteOk(state, RoundCommand.ClosedKan(roundId, 2, new TileId(kanTiles[0]), new TileId(kanTiles[1]), new TileId(kanTiles[2]), new TileId(kanTiles[3])));
            state = ExecuteOk(state, RoundCommand.Discard(roundId, 2, new TileId(130), Array.Empty<int>()));
            state = ExecuteOk(state, RoundCommand.Discard(roundId, 3, new TileId(131), Array.Empty<int>()));
            Assert.Equal(0, state.CurrentSeat);
            Assert.Equal(RoundPhase.CheatWindow, state.Phase);
            Assert.Equal(DrawSource.Wall, state.Tiles.DrawnTile?.Source);
            return state;
        }

        private static RoundStateMachine BuildSeat0ClosedKanNormalVsRinshanRound(string roundId, bool forRinshan)
        {
            var specific = new Dictionary<int, int>();
            int[] s0Hand = { 0, 4, 8, 12, 16, 20, 24, 28, 32, 72, 68, 69, 70, 71 };
            int[] s0Slots = { 0, 1, 2, 3, 16, 17, 18, 19, 32, 33, 34, 35, 48, 52 };
            int[] s1Hand = { 1, 9, 17, 25, 33, 41, 49, 57, 65, 85, 97, 105, 109 };
            int[] s1Slots = { 4, 5, 6, 7, 20, 21, 22, 23, 36, 37, 38, 39, 49 };
            int[] s2Hand = { 2, 10, 18, 26, 34, 42, 50, 58, 66, 86, 98, 106, 110 };
            int[] s2Slots = { 8, 9, 10, 11, 24, 25, 26, 27, 40, 41, 42, 43, 50 };
            int[] s3Hand = { 3, 11, 19, 27, 35, 43, 51, 59, 67, 87, 99, 107, 111 };
            int[] s3Slots = { 12, 13, 14, 15, 28, 29, 30, 31, 44, 45, 46, 47, 51 };
            for (int i = 0; i < s0Slots.Length; i++) specific[s0Slots[i]] = s0Hand[i];
            for (int i = 0; i < s1Slots.Length; i++) specific[s1Slots[i]] = s1Hand[i];
            for (int i = 0; i < s2Slots.Length; i++) specific[s2Slots[i]] = s2Hand[i];
            for (int i = 0; i < s3Slots.Length; i++) specific[s3Slots[i]] = s3Hand[i];
            specific[126] = 115;

            if (forRinshan)
            {
                specific[122] = 120; // Rinshan draw = North 120
                TilePositionMap dealt = TilePositionMap.CreateDealt(RecordedRandomDraw.Create(roundId + "-wall", BuildWallOrder(specific)), 0);
                RoundStateMachine state = RoundStateMachine.CreateDealt(
                    new RoundSpec(roundId, 0, 1, 0, 0, new long[] { 25_000, 25_000, 25_000, 25_000 }, 0),
                    dealt,
                    RoundPhase.Discard);
                state = ExecuteOk(state, RoundCommand.ClosedKan(roundId, 0, new TileId(68), new TileId(69), new TileId(70), new TileId(71)));
                Assert.Equal(0, state.CurrentSeat);
                Assert.Equal(RoundPhase.CheatWindow, state.Phase);
                Assert.Equal(DrawSource.Rinshan, state.Tiles.DrawnTile?.Source);
                return state;
            }

            specific[122] = 134;
            specific[53] = 132;
            specific[54] = 133;
            specific[55] = 135;
            specific[56] = 120; // Turn 2 Normal draw = North 120
            TilePositionMap dealtNorm = TilePositionMap.CreateDealt(RecordedRandomDraw.Create(roundId + "-wall", BuildWallOrder(specific)), 0);
            RoundStateMachine s = RoundStateMachine.CreateDealt(
                new RoundSpec(roundId, 0, 1, 0, 0, new long[] { 25_000, 25_000, 25_000, 25_000 }, 0),
                dealtNorm,
                RoundPhase.Discard);
            s = ExecuteOk(s, RoundCommand.ClosedKan(roundId, 0, new TileId(68), new TileId(69), new TileId(70), new TileId(71)));
            s = ExecuteOk(s, RoundCommand.Discard(roundId, 0, new TileId(134), Array.Empty<int>()));
            s = ExecuteOk(s, RoundCommand.Discard(roundId, 1, new TileId(132), Array.Empty<int>()));
            s = ExecuteOk(s, RoundCommand.Discard(roundId, 2, new TileId(133), Array.Empty<int>()));
            s = ExecuteOk(s, RoundCommand.Discard(roundId, 3, new TileId(135), Array.Empty<int>()));
            Assert.Equal(0, s.CurrentSeat);
            Assert.Equal(RoundPhase.CheatWindow, s.Phase);
            Assert.Equal(DrawSource.Wall, s.Tiles.DrawnTile?.Source);
            return s;
        }

        private static RoundStateMachine ExecuteOk(RoundStateMachine state, RoundCommand command)
        {
            RoundTransition tr = state.Execute(command);
            Assert.True(tr.IsAccepted && tr.State != null, $"Command {command.Action} seat={command.Seat} rejected: {tr.Error}");
            return tr.State!;
        }

        private static TileId[] BuildWallOrder(Dictionary<int, int> specific)
        {
            var used = new HashSet<int>(specific.Values);
            var remaining = new Queue<int>(Enumerable.Range(0, TileId.Count).Where(id => !used.Contains(id)));
            var order = new TileId[TileId.Count];
            for (int i = 0; i < TileId.Count; i++)
            {
                order[i] = new TileId(specific.TryGetValue(i, out int val) ? val : remaining.Dequeue());
            }

            return order;
        }

        private static RoundStateMachine BuildQuickWinnerRound(
            string roundId,
            int dealerSeat,
            int roundWind,
            int roundNumber,
            int honba,
            IReadOnlyList<long> scores,
            int riichiSticks,
            int winnerSeat)
        {
            var specific = new Dictionary<int, int>();
            specific[126] = 115; // DeadWall(4) dora indicator = South -> West dora (0 hits).
            specific[127] = 116; // DeadWall(5) = West -> North dora (0 hits).
            specific[131] = 119; // DeadWall(9) ura-dora indicator = West -> North ura-dora (0 hits).
            if (winnerSeat == 0)
            {
                // Dealer 0 starts 13-tile tenpai (123m 456m 789p Haku*3(124..126) 2s(76)) + Chun(132) at slot 52,
                // discards 132u (avoids Tenhou!), then wins by Ron or Tsumo on 2s(77/78/79) -> 500 all / 1500 (<30,000).
                int[] s0Tiles = { 0, 4, 8, 12, 16, 20, 60, 64, 68, 124, 125, 126, 76, 132 };
                int[] s0Slots = { 0, 1, 2, 3, 16, 17, 18, 19, 32, 33, 34, 35, 48, 52 };
                for (int i = 0; i < s0Slots.Length; i++) specific[s0Slots[i]] = s0Tiles[i];
                int[] s1Junk = { 1, 9, 17, 25, 33, 41, 49, 57, 65, 81, 97, 105, 113 };
                int[] s1Slots = { 4, 5, 6, 7, 20, 21, 22, 23, 36, 37, 38, 39, 49 };
                for (int i = 0; i < s1Slots.Length; i++) specific[s1Slots[i]] = s1Junk[i];
                specific[53] = 133;
                specific[54] = 134;
                specific[55] = 135;
                specific[56] = 77;
                specific[57] = 78;
                specific[58] = 79;
            }
            else if (winnerSeat == 1)
            {
                // Seat 0 holds scattered junk + North(123) at slot 52 and discards 123u on Turn 1.
                // Seat 1 holds 123m 789m 123p 99p 68s (kanchan 7s, no Pinfu/Akadora, 30 fu).
                // Turn 1 & Turn 2 use isolated honors (53..60) so Ippatsu expires, then Turn 3 draws 7s (61..64) ->
                // Riichi+Tsumo = 500/1000 (+2300 -> 27,300 < 30,000).
                int[] s1Tiles = { 0, 4, 8, 24, 28, 32, 36, 40, 44, 68, 69, 92, 100 };
                int[] s1Slots = { 4, 5, 6, 7, 20, 21, 22, 23, 36, 37, 38, 39, 49 };
                for (int i = 0; i < s1Slots.Length; i++) specific[s1Slots[i]] = s1Tiles[i];
                int[] s0Tiles = { 13, 21, 49, 53, 57, 65, 73, 77, 85, 89, 101, 109, 129, 123 };
                int[] s0Slots = { 0, 1, 2, 3, 16, 17, 18, 19, 32, 33, 34, 35, 48, 52 };
                for (int i = 0; i < s0Slots.Length; i++) specific[s0Slots[i]] = s0Tiles[i];
                int[] s2Junk = { 14, 22, 50, 54, 58, 66, 74, 78, 86, 90, 102, 110, 130 };
                int[] s2Slots = { 8, 9, 10, 11, 24, 25, 26, 27, 40, 41, 42, 43, 50 };
                for (int i = 0; i < s2Slots.Length; i++) specific[s2Slots[i]] = s2Junk[i];
                int[] s3Junk = { 15, 23, 51, 55, 59, 67, 75, 79, 87, 91, 103, 108, 128 };
                int[] s3Slots = { 12, 13, 14, 15, 28, 29, 30, 31, 44, 45, 46, 47, 51 };
                for (int i = 0; i < s3Slots.Length; i++) specific[s3Slots[i]] = s3Junk[i];
                specific[53] = 134;
                specific[54] = 135;
                specific[55] = 120;
                specific[56] = 121;
                specific[57] = 122;
                specific[58] = 117;
                specific[59] = 118;
                specific[60] = 111;
                specific[61] = 96;
                specific[62] = 97;
                specific[63] = 98;
                specific[64] = 99;
            }
            else
            {
                // Dealer is 1 (round 3). Seat 2 (dealer+1, BasicAiStrategy, slots 4..7,20..23,36..39,49) holds
                // 123m 789m 123p 99p 68s (kanchan 7s, no Pinfu/Akadora, 30 fu).
                // Turn 1 & Turn 2 use isolated honors (53..60) so Ippatsu expires, then Turn 3 Seat 2 draws 7s (61..64) ->
                // Riichi+Tsumo = 500/1000 (all <30,000) -> rotates cleanly into West extension!
                int[] s2WinHand = { 0, 4, 8, 24, 28, 32, 36, 40, 44, 68, 69, 92, 100 };
                int[] s2Slots = { 4, 5, 6, 7, 20, 21, 22, 23, 36, 37, 38, 39, 49 };
                int[] s0Hand = { 13, 21, 49, 53, 57, 65, 73, 77, 85, 89, 101, 109, 129 };
                int[] s0Slots = { 12, 13, 14, 15, 28, 29, 30, 31, 44, 45, 46, 47, 51 };
                int[] s1Junk = { 14, 22, 50, 54, 58, 66, 74, 78, 86, 90, 102, 110, 130, 131 };
                int[] s1Slots = { 0, 1, 2, 3, 16, 17, 18, 19, 32, 33, 34, 35, 48, 52 };
                int[] s3Junk = { 15, 23, 51, 55, 59, 67, 75, 79, 87, 91, 103, 108, 128 };
                int[] s3Slots = { 8, 9, 10, 11, 24, 25, 26, 27, 40, 41, 42, 43, 50 };
                for (int i = 0; i < s2Slots.Length; i++) specific[s2Slots[i]] = s2WinHand[i];
                for (int i = 0; i < s0Slots.Length; i++) specific[s0Slots[i]] = s0Hand[i];
                for (int i = 0; i < s1Slots.Length; i++) specific[s1Slots[i]] = s1Junk[i];
                for (int i = 0; i < s3Slots.Length; i++) specific[s3Slots[i]] = s3Junk[i];
                specific[53] = 134;
                specific[54] = 135;
                specific[55] = 120;
                specific[56] = 121;
                specific[57] = 122;
                specific[58] = 117;
                specific[59] = 118;
                specific[60] = 111;
                specific[61] = 96;
                specific[62] = 97;
                specific[63] = 98;
                specific[64] = 99;
            }

            var used = new HashSet<int>(specific.Values);
            var remaining = new Queue<int>(Enumerable.Range(0, TileId.Count).Where(id => !used.Contains(id)));
            var order = new TileId[TileId.Count];
            for (int i = 0; i < TileId.Count; i++)
            {
                order[i] = new TileId(specific.TryGetValue(i, out int val) ? val : remaining.Dequeue());
            }

            TilePositionMap dealt = TilePositionMap.CreateDealt(RecordedRandomDraw.Create(roundId + "-wall", order), dealerSeat);
            return RoundStateMachine.CreateDealt(
                new RoundSpec(roundId, dealerSeat, roundWind, roundNumber, honba, scores, riichiSticks),
                dealt,
                RoundPhase.CheatWindow);
        }

        private static async Task<TableSession> PlayRoundToEndAsync(
            LocalMatchRound round,
            CancellationToken ct,
            ProtocolActionType? seat0Action,
            uint? discardTile,
            LocalMatchCoordinator coordinator)
        {
            var session = new TableSession(round.Channel, new InMemoryOutboxStore());
            using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            pumpCts.CancelAfter(TimeSpan.FromSeconds(30));
            await session.OpenAsync(new TableSessionOpenRequest(round.Ticket, "m104-player"), pumpCts.Token);
            Task pump = Task.Run(async () =>
            {
                try
                {
                    while (!pumpCts.IsCancellationRequested)
                    {
                        await session.PumpOnceAsync(pumpCts.Token);
                    }
                }
                catch (Exception ex) when (
                    ex is OperationCanceledException ||
                    ex is TableChannelClosedException ||
                    ex is InvalidOperationException)
                {
                }
            }, TestContext.Current.CancellationToken);

            try
            {
                for (int i = 0; i < 60 && session.State != TableConnectionState.Ready; i++)
                {
                    await Task.Delay(10, pumpCts.Token);
                }

                Assert.Equal(TableConnectionState.Ready, session.State);

                string? lastSubmittedDecisionId = null;
                int actionSeq = 0;
                for (int i = 0; i < 1200 &&
                     session.PlayerSnapshot?.PublicView?.Board?.Phase != Mahjong.Contracts.Wire.GamePhase.RoundEnd; i++)
                {
                    var snap = session.PlayerSnapshot;
                    var decision = snap?.PrivateView?.Decision;
                    string? submissionKey = snap != null && decision != null
                        ? decision.DecisionId + "@" + snap.StateVersion
                        : null;
                    if (snap != null &&
                        decision != null &&
                        !string.Equals(lastSubmittedDecisionId, submissionKey, StringComparison.Ordinal))
                    {
                        lastSubmittedDecisionId = submissionKey;
                        actionSeq++;
                        Mahjong.Contracts.Wire.GamePhase phase = snap.PublicView?.Board?.Phase ?? Mahjong.Contracts.Wire.GamePhase.CheatWindow;
                        ProtocolActionType actionToSend;
                        uint[]? tiles;
                        bool canRunConfigured = seat0Action.HasValue && decision.Actions.Any(a =>
                            a.ActionType == (uint)seat0Action.Value &&
                            (!discardTile.HasValue || a.TileIds.Contains(discardTile.Value)));
                        if (decision.Actions.Any(a => a.ActionType == (uint)ProtocolActionType.DeclareTsumo))
                        {
                            actionToSend = ProtocolActionType.DeclareTsumo;
                            tiles = null;
                        }
                        else if (decision.Actions.Any(a => a.ActionType == (uint)ProtocolActionType.DeclareRon))
                        {
                            actionToSend = ProtocolActionType.DeclareRon;
                            tiles = null;
                        }
                        else if (canRunConfigured)
                        {
                            actionToSend = seat0Action!.Value;
                            tiles = discardTile.HasValue ? new[] { discardTile.Value } : null;
                        }
                        else if (decision.Actions.Any(a => a.ActionType == (uint)ProtocolActionType.Pass))
                        {
                            actionToSend = ProtocolActionType.Pass;
                            tiles = null;
                        }
                        else
                        {
                            var fallbackDiscard = decision.Actions
                                .Where(a => a.ActionType == (uint)ProtocolActionType.DiscardTile && a.TileIds.Count > 0)
                                .OrderByDescending(a => a.TileIds[0] >= 80 && (a.TileIds[0] < 124 || a.TileIds[0] > 127) ? 1 : 0)
                                .ThenByDescending(a => a.TileIds[0])
                                .FirstOrDefault();
                            if (fallbackDiscard != null)
                            {
                                actionToSend = ProtocolActionType.DiscardTile;
                                tiles = new[] { fallbackDiscard.TileIds[0] };
                            }
                            else
                            {
                                actionToSend = seat0Action ?? ProtocolActionType.Pass;
                                tiles = discardTile.HasValue ? new[] { discardTile.Value } : null;
                            }
                        }

                        var payload = CommandPayloadFactory.SubmitAction(
                            snap.RoundId, decision.DecisionId, decision.ControlEpoch, actionToSend, tileIds: tiles);
                        await session.SubmitAsync(new ProtocolCommand(
                            CommandKind.SubmitAction,
                            payload,
                            new RequestId("m104-act-" + snap.RoundId + "-" + actionSeq),
                            session.Binding!.Scope.ScopeId,
                            new StateVersion(snap.StateVersion),
                            new RoundId(snap.RoundId),
                            decisionId: new DecisionId(decision.DecisionId),
                            controlEpoch: new ControlEpoch(decision.ControlEpoch)), pumpCts.Token);
                    }

                    await Task.Delay(15, pumpCts.Token);
                }

                Assert.True(
                    session.PlayerSnapshot!.PublicView.Board.Phase == Mahjong.Contracts.Wire.GamePhase.RoundEnd,
                    "phase=" + session.PlayerSnapshot!.PublicView.Board.Phase + " diag=" + coordinator.DescribeRuntimeForTest());
                return session;
            }
            finally
            {
                pumpCts.Cancel();
                await pump;
            }
        }

        private static void WriteV2MatchFile(string path, string matchId, string[] characters)
        {
            byte[] payload;
            using (var body = new MemoryStream())
            using (var w = new BinaryWriter(body, new UTF8Encoding(false, true), true))
            {
                w.Write(matchId);
                w.Write(1);
                w.Write(1);
                w.Write(matchId + "-r1");
                w.Write(matchId + "-r1");
                w.Write(0);
                w.Write(1);
                w.Write(0);
                w.Write(0);
                w.Write(0);
                w.Write(4);
                for (int i = 0; i < 4; i++) w.Write(25_000L);
                w.Write(0);
                w.Write(false);
                w.Write(string.Empty);
                w.Write(0);
                w.Write(0);
                for (int seat = 0; seat < 4; seat++)
                {
                    w.Write(characters[seat]);
                    w.Write(1);
                }

                w.Flush();
                payload = body.ToArray();
            }

            using (var env = new MemoryStream())
            using (var w = new BinaryWriter(env, new UTF8Encoding(false, true), true))
            {
                w.Write(0x4D415443); // MATC
                w.Write(2); // V2 envelope without SeatProfiles
                w.Write(payload.Length);
                w.Write(TablePayloadSha256.Compute(payload).ToArray());
                w.Write(payload);
                w.Flush();
                File.WriteAllBytes(path, env.ToArray());
            }
        }
    }
}
