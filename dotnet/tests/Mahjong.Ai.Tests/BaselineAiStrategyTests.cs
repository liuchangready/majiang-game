using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Domain;
using Xunit;

namespace Mahjong.Ai.Tests
{
    /// <summary>
    /// Behaviour matrix for the two deterministic baseline strategies ported from V4
    /// (ENG-014 TrivialAIStrategy.kt, ENG-015 TsumogiriAIStrategy.kt).
    ///
    /// The matrix is intentionally non-isomorphic: every divergence test feeds the exact
    /// same input to both strategies and asserts two different outputs, which is why they
    /// must remain two strategies. Wiring tests run the real <see cref="AiDecisionService"/>
    /// (ReasonAsync, Submit, Replay) instead of re-implementing its checks.
    /// </summary>
    public class BaselineAiStrategyTests
    {
        private const string RoundId = "round-baseline-1";
        private const string DecisionId = "turn-round-baseline-1-9-seat1";
        private const int Seat = 1;
        private const long Epoch = 0L;
        private const long Deadline = 23000L;

        private static readonly TrivialAiStrategy Trivial = new TrivialAiStrategy();
        private static readonly TsumogiriAiStrategy Tsumogiri = new TsumogiriAiStrategy();

        // Pin five: typeIndex 22, physical ids 88..89..90..91; copy 3 (91) is the red five.
        private static readonly TileId PinFiveNormal = new TileId(88);
        private static readonly TileId PinFiveRed = new TileId(91);

        // ---------------------------------------------------------------------
        // Trivial branch priority (V4: Ron > Tsumo > discard > Pass > first)
        // ---------------------------------------------------------------------

        [Fact]
        public void TrivialSelectsFirstRonEvenWhenTsumoPassAndDiscardComeFirst()
        {
            LegalActionOption pass = Pass("pass");
            LegalActionOption tsumo = Tsumo();
            LegalActionOption ron = Ron("ron");
            LegalActionOption discard = Discard(20, "discard-T20");

            AiStrategicChoice ronFirst = Trivial.TryChoose(
                new[] { pass, tsumo, ron, discard }, Hand(), null, CancellationToken.None)!;
            Assert.Equal(LegalActionKind.Ron, ronFirst.Action.Kind);
            Assert.Same(ron, ronFirst.Action);

            // A later Ron still beats an earlier Tsumo: the V4 scan checks all Rons first.
            AiStrategicChoice ronLast = Trivial.TryChoose(
                new[] { tsumo, ron }, Hand(), null, CancellationToken.None)!;
            Assert.Same(ron, ronLast.Action);
        }

        [Fact]
        public void TrivialSelectsFirstTsumoWhenRonAbsent()
        {
            LegalActionOption pass = Pass("pass");
            LegalActionOption tsumo = Tsumo();
            LegalActionOption discard = Discard(20, "discard-T20");

            AiStrategicChoice choice = Trivial.TryChoose(
                new[] { pass, tsumo, discard }, Hand(), null, CancellationToken.None)!;

            Assert.Same(tsumo, choice.Action);
            Assert.Equal(0d, choice.HeuristicScore);
        }

        [Fact]
        public void TrivialPrefersDiscardOverPassAndSelectsTheDrawnTile()
        {
            // V4 runs the discard branch before the pass branch, so a discard candidate
            // wins even when a Pass is offered first in the list.
            AiStrategicChoice choice = Trivial.TryChoose(
                new[] { Pass("pass"), Discard(33, "discard-T33") }, Hand(), new TileId(33), CancellationToken.None)!;

            Assert.Equal(LegalActionKind.Discard, choice.Action.Kind);
            Assert.Equal(new TileId(33), choice.Action.Tile);
        }

        [Fact]
        public void TrivialMatchesClosedHandLastTileWhenDrawnAbsent()
        {
            // After a meld the runtime may offer discards without a drawn tile; V4 matches
            // the closed hand's last tile before falling back to the last discard.
            IReadOnlyList<TileId> hand = Hand(20, 24);
            List<LegalActionOption> actions = new List<LegalActionOption>
            {
                Discard(24, "discard-T24"),
                Discard(20, "discard-T20")
            };

            AiStrategicChoice choice = Trivial.TryChoose(actions, hand, null, CancellationToken.None)!;

            Assert.Same(actions[0], choice.Action);
            Assert.Equal(new TileId(24), choice.Action.Tile);
        }

        [Fact]
        public void TrivialFallsToLastDiscardInListOrderWhenNothingMatches()
        {
            // Neither the drawn tile nor the closed hand's last tile appears among the
            // discard candidates: V4 returns the LAST discard in list order, not the first.
            IReadOnlyList<TileId> hand = Hand(40);
            List<LegalActionOption> actions = new List<LegalActionOption>
            {
                Discard(24, "discard-T24"),
                Discard(28, "discard-T28"),
                Discard(20, "discard-T20")
            };

            AiStrategicChoice choice = Trivial.TryChoose(actions, hand, null, CancellationToken.None)!;

            Assert.Same(actions[2], choice.Action);
        }

        [Fact]
        public void TrivialPassesWhenOnlyMeldCallsAndPassExist()
        {
            // The V4 KDoc's "never meld, never riichi" comes from this branch: with no win
            // and no discard candidate the first Pass is taken over meld/riichi calls.
            List<LegalActionOption> actions = new List<LegalActionOption>
            {
                Option(LegalActionKind.Chi, 40, "chi"),
                Option(LegalActionKind.Pon, null, "pon"),
                Option(LegalActionKind.Kan, null, "kan"),
                Pass("pass")
            };

            AiStrategicChoice choice = Trivial.TryChoose(actions, Hand(), null, CancellationToken.None)!;

            Assert.Same(actions[3], choice.Action);
        }

        [Fact]
        public void TrivialTakesFirstLegalActionAsFinalFallback()
        {
            // No win, no discard, no pass: V4 returns legalActions.first() of any kind,
            // including riichi — the source does not veto it, only ordering hides it.
            List<LegalActionOption> actions = new List<LegalActionOption>
            {
                Option(LegalActionKind.Kan, null, "kan"),
                Option(LegalActionKind.Riichi, 20, "riichi")
            };

            AiStrategicChoice choice = Trivial.TryChoose(actions, Hand(), null, CancellationToken.None)!;

            Assert.Same(actions[0], choice.Action);
        }

        // ---------------------------------------------------------------------
        // Tsumogiri branch priority (V4: Pass > drawn discard > first discard > first)
        // ---------------------------------------------------------------------

        [Fact]
        public void TsumogiriPassesEvenWhenWinDiscardAndMeldCandidatesExist()
        {
            // This is the deepest behavioural difference from Trivial: Tsumogiri never
            // declares a win. The first viewer Pass precedes every other branch.
            List<LegalActionOption> actions = new List<LegalActionOption>
            {
                Tsumo(),
                Ron("ron"),
                Discard(33, "discard-T33"),
                Option(LegalActionKind.Pon, null, "pon"),
                Pass("pass")
            };

            AiStrategicChoice choice = Tsumogiri.TryChoose(actions, Seat, new TileId(33), CancellationToken.None)!;

            Assert.Same(actions[4], choice.Action);
            Assert.Equal(LegalActionKind.Pass, choice.Action.Kind);
        }

        [Fact]
        public void TsumogiriDiscardsDrawnTileWhenNoPassExists()
        {
            // The drawn-tile discard is selected even when it is not the first discard.
            List<LegalActionOption> actions = new List<LegalActionOption>
            {
                Discard(20, "discard-T20"),
                Discard(33, "discard-T33")
            };

            AiStrategicChoice choice = Tsumogiri.TryChoose(actions, Seat, new TileId(33), CancellationToken.None)!;

            Assert.Same(actions[1], choice.Action);
        }

        [Fact]
        public void TsumogiriTakesFirstDiscardWhenDrawnAbsentOrNotDiscardable()
        {
            List<LegalActionOption> noDrawn = new List<LegalActionOption>
            {
                Discard(24, "discard-T24"),
                Discard(20, "discard-T20")
            };
            AiStrategicChoice absent = Tsumogiri.TryChoose(noDrawn, Seat, null, CancellationToken.None)!;
            Assert.Same(noDrawn[0], absent.Action);

            // The drawn tile is not among the candidates: the first viewer discard wins,
            // the drawn tile value itself is never invented as a choice.
            List<LegalActionOption> drawnIllegal = new List<LegalActionOption>
            {
                Discard(24, "discard-T24"),
                Discard(20, "discard-T20")
            };
            AiStrategicChoice illegal = Tsumogiri.TryChoose(drawnIllegal, Seat, new TileId(91), CancellationToken.None)!;
            Assert.Same(drawnIllegal[0], illegal.Action);
        }

        [Fact]
        public void TsumogiriTakesFirstLegalActionWhenNoPassOrDiscardExists()
        {
            List<LegalActionOption> actions = new List<LegalActionOption>
            {
                Option(LegalActionKind.Chi, 40, "chi"),
                Option(LegalActionKind.Riichi, 20, "riichi")
            };

            AiStrategicChoice choice = Tsumogiri.TryChoose(actions, Seat, null, CancellationToken.None)!;

            Assert.Same(actions[0], choice.Action);
        }

        // ---------------------------------------------------------------------
        // Divergence: the same input must produce different strategies' choices
        // ---------------------------------------------------------------------

        [Fact]
        public void StrategiesDivergeOnResponseWindowThatAlsoOffersADiscard()
        {
            List<LegalActionOption> actions = new List<LegalActionOption>
            {
                Pass("pass"),
                Discard(20, "discard-T20")
            };

            AiStrategicChoice trivial = Trivial.TryChoose(actions, Hand(), new TileId(20), CancellationToken.None)!;
            AiStrategicChoice tsumogiri = Tsumogiri.TryChoose(actions, Seat, new TileId(20), CancellationToken.None)!;

            Assert.Same(actions[1], trivial.Action);
            Assert.Same(actions[0], tsumogiri.Action);
            Assert.NotSame(trivial.Action, tsumogiri.Action);
        }

        [Fact]
        public void StrategiesDivergeOnWinCandidates()
        {
            List<LegalActionOption> actions = new List<LegalActionOption> { Pass("pass"), Ron("ron") };

            Assert.Equal(LegalActionKind.Ron, Trivial.TryChoose(actions, Hand(), null, CancellationToken.None)!.Action.Kind);
            Assert.Equal(LegalActionKind.Pass, Tsumogiri.TryChoose(actions, Seat, null, CancellationToken.None)!.Action.Kind);

            List<LegalActionOption> tsumoActions = new List<LegalActionOption> { Discard(20, "discard-T20"), Tsumo() };
            Assert.Equal(LegalActionKind.Tsumo, Trivial.TryChoose(tsumoActions, Hand(), null, CancellationToken.None)!.Action.Kind);
            Assert.Equal(LegalActionKind.Discard, Tsumogiri.TryChoose(tsumoActions, Seat, null, CancellationToken.None)!.Action.Kind);
        }

        [Fact]
        public void StrategiesDivergeOnMultipleDiscardsWithoutDrawnTile()
        {
            // Trivial matches the closed hand's last tile (T28); Tsumogiri takes the first
            // discard in list order (T20). Same input, two different choices.
            IReadOnlyList<TileId> hand = Hand(20, 28);
            List<LegalActionOption> actions = new List<LegalActionOption>
            {
                Discard(20, "discard-T20"),
                Discard(24, "discard-T24"),
                Discard(28, "discard-T28")
            };

            AiStrategicChoice trivial = Trivial.TryChoose(actions, hand, null, CancellationToken.None)!;
            AiStrategicChoice tsumogiri = Tsumogiri.TryChoose(actions, Seat, null, CancellationToken.None)!;

            Assert.Same(actions[2], trivial.Action);
            Assert.Same(actions[0], tsumogiri.Action);
        }

        [Fact]
        public void StrategiesDivergeWhenHandLastTileHasNoDiscardCandidate()
        {
            // Hand last (T40) matches nothing: Trivial falls to the LAST discard, Tsumogiri
            // to the FIRST. Same input, two different choices.
            IReadOnlyList<TileId> hand = Hand(40);
            List<LegalActionOption> actions = new List<LegalActionOption>
            {
                Discard(24, "discard-T24"),
                Discard(28, "discard-T28"),
                Discard(20, "discard-T20")
            };

            Assert.Same(actions[2], Trivial.TryChoose(actions, hand, null, CancellationToken.None)!.Action);
            Assert.Same(actions[0], Tsumogiri.TryChoose(actions, Seat, null, CancellationToken.None)!.Action);
        }

        [Fact]
        public void StrategiesFollowListOrderNotTileValueOrder()
        {
            // Candidate order 60, 20, 40 is neither ascending nor descending; the choices
            // prove list position (first / last), not tile value, decides the fallback.
            IReadOnlyList<TileId> hand = Hand(40);
            List<LegalActionOption> actions = new List<LegalActionOption>
            {
                Discard(60, "discard-T60"),
                Discard(20, "discard-T20"),
                Discard(40, "discard-T40")
            };

            Assert.Same(actions[2], Trivial.TryChoose(actions, hand, null, CancellationToken.None)!.Action);
            Assert.Same(actions[0], Tsumogiri.TryChoose(actions, Seat, null, CancellationToken.None)!.Action);
        }

        // ---------------------------------------------------------------------
        // Entity identity, candidate identity and input integrity
        // ---------------------------------------------------------------------

        [Fact]
        public void TrivialDistinguishesSameTypePhysicalCopiesIncludingRedFive()
        {
            // V4 compared Tile data-class equality (suit, rank, copyIndex): the red five
            // (copy 3) never matched a normal five copy. TileId value equality keeps the
            // entity distinction, so the drawn red five selects its own physical candidate.
            IReadOnlyList<TileId> hand = Hand(60);
            List<LegalActionOption> actions = new List<LegalActionOption>
            {
                Discard(88, "discard-pin5-copy0"),
                Discard(91, "discard-pin5-red")
            };

            AiStrategicChoice redDrawn = Trivial.TryChoose(actions, hand, PinFiveRed, CancellationToken.None)!;
            Assert.Same(actions[1], redDrawn.Action);
            Assert.Equal("discard-pin5-red", redDrawn.Action.OptionId);

            AiStrategicChoice normalDrawn = Trivial.TryChoose(actions, hand, PinFiveNormal, CancellationToken.None)!;
            Assert.Same(actions[0], normalDrawn.Action);
            Assert.Equal("discard-pin5-copy0", normalDrawn.Action.OptionId);

            // Tsumogiri applies the same physical identity on its drawn-discard branch.
            Assert.Same(
                actions[1],
                Tsumogiri.TryChoose(actions, Seat, PinFiveRed, CancellationToken.None)!.Action);
        }

        [Fact]
        public void TrivialDoesNotMatchDrawnTypeOnADifferentCopyAndFallsToHandLast()
        {
            // The drawn tile is physically illegal while only a same-type different copy is
            // legal: the type must NOT stand in for the entity; the hand-last fallback runs.
            IReadOnlyList<TileId> hand = Hand(40, 60);
            List<LegalActionOption> actions = new List<LegalActionOption>
            {
                Discard(88, "discard-pin5-copy0"),
                Discard(60, "discard-T60")
            };

            AiStrategicChoice choice = Trivial.TryChoose(actions, hand, PinFiveRed, CancellationToken.None)!;

            Assert.Same(actions[1], choice.Action);
            Assert.Equal(new TileId(60), choice.Action.Tile);
        }

        [Fact]
        public void StrategiesReturnExactCandidateInstancePreservingOptionIdAndTile()
        {
            AiObservation observation = Observation(new[]
            {
                Discard(91, "discard-pin5-red"),
                Pass("pass")
            }, drawn: PinFiveRed);

            AiStrategicChoice trivial = Trivial.Decide(observation, CancellationToken.None);
            Assert.Same(observation.LegalActions[0], trivial.Action);
            Assert.Equal("discard-pin5-red", trivial.Action.OptionId);
            Assert.Equal(PinFiveRed, trivial.Action.Tile!.Value);

            AiStrategicChoice tsumogiri = Tsumogiri.Decide(observation, CancellationToken.None);
            Assert.Same(observation.LegalActions[1], tsumogiri.Action);
            Assert.Equal("pass", tsumogiri.Action.OptionId);
            Assert.Null(tsumogiri.Action.Tile);
        }

        [Fact]
        public void FirstCandidateWinsAmongEqualKinds()
        {
            LegalActionOption ronA = Ron("ron-a");
            LegalActionOption ronB = Ron("ron-b");
            Assert.Same(ronA, Trivial.TryChoose(new[] { ronA, ronB }, Hand(), null, CancellationToken.None)!.Action);

            LegalActionOption passA = Pass("pass-a");
            LegalActionOption passB = Pass("pass-b");
            Assert.Same(
                passA,
                Tsumogiri.TryChoose(new[] { passA, passB }, Seat, null, CancellationToken.None)!.Action);
        }

        [Fact]
        public void TryChooseDoesNotModifyItsInputLists()
        {
            var actions = new List<LegalActionOption> { Pass("pass"), Discard(33, "discard-T33") };
            var hand = new List<TileId> { new TileId(20), new TileId(33) };
            LegalActionOption[] actionsSnapshot = actions.ToArray();
            TileId[] handSnapshot = hand.ToArray();

            Trivial.TryChoose(actions, hand, new TileId(33), CancellationToken.None);
            Tsumogiri.TryChoose(actions, Seat, new TileId(33), CancellationToken.None);

            Assert.Equal(actionsSnapshot.Length, actions.Count);
            for (int i = 0; i < actionsSnapshot.Length; i++)
            {
                Assert.Same(actionsSnapshot[i], actions[i]);
            }

            Assert.Equal(handSnapshot, hand);
        }

        // ---------------------------------------------------------------------
        // Empty candidates, cancellation and argument contracts
        // ---------------------------------------------------------------------

        [Fact]
        public void TryChooseReturnsNullForEmptyCandidatesMatchingV4()
        {
            // V4 decideAction returned null for an empty legal list; both ports keep that
            // contract on the nullable entry point instead of inventing an action.
            Assert.Null(Trivial.TryChoose(Array.Empty<LegalActionOption>(), Hand(), null, CancellationToken.None));
            Assert.Null(Tsumogiri.TryChoose(Array.Empty<LegalActionOption>(), Seat, null, CancellationToken.None));
        }

        [Fact]
        public void ObservationContractRejectsEmptyCandidateLists()
        {
            // Boundary: the null arm of Decide is unreachable through AiObservation, whose
            // constructor refuses an empty candidate list. IAiStrategy.Decide therefore
            // always receives at least one candidate; the strategies never widen that
            // validation.
            Assert.Throws<ArgumentException>(() => Observation(Array.Empty<LegalActionOption>()));
        }

        [Fact]
        public void StrategiesHonorCancellationInsteadOfFabricatingAChoice()
        {
            using (var source = new CancellationTokenSource())
            {
                source.Cancel();

                Assert.Throws<OperationCanceledException>(() =>
                    Trivial.TryChoose(new[] { Pass("pass") }, Hand(), null, source.Token));
                Assert.Throws<OperationCanceledException>(() =>
                    Tsumogiri.TryChoose(new[] { Pass("pass") }, Seat, null, source.Token));

                AiObservation observation = Observation(new[] { Pass("pass") });
                Assert.Throws<OperationCanceledException>(() => Trivial.Decide(observation, source.Token));
                Assert.Throws<OperationCanceledException>(() => Tsumogiri.Decide(observation, source.Token));
            }
        }

        [Fact]
        public async Task ReasonAsyncPropagatesCancellationBeforeAnyStrategyWork()
        {
            AiObservation observation = Observation(new[] { Discard(33, "discard-T33"), Pass("pass") });
            using (var source = new CancellationTokenSource())
            {
                source.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    new AiDecisionService(Trivial).ReasonAsync(observation, source.Token));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    new AiDecisionService(Tsumogiri).ReasonAsync(observation, source.Token));
            }
        }

        [Fact]
        public void TryChooseRejectsNullArguments()
        {
            Assert.Throws<ArgumentNullException>(() =>
                Trivial.TryChoose(null!, Hand(), null, CancellationToken.None));
            Assert.Throws<ArgumentNullException>(() =>
                Trivial.TryChoose(new[] { Pass("pass") }, null!, null, CancellationToken.None));
            Assert.Throws<ArgumentNullException>(() =>
                Tsumogiri.TryChoose(null!, Seat, null, CancellationToken.None));
        }

        // ---------------------------------------------------------------------
        // Algorithm identity, logging safety and the seat-filter boundary
        // ---------------------------------------------------------------------

        [Fact]
        public void StrategiesCarryDistinctStableAlgorithmIdentity()
        {
            Assert.NotEqual(Trivial.AlgorithmId, Tsumogiri.AlgorithmId);
            Assert.Equal(TrivialAiStrategy.AlgorithmIdValue, Trivial.AlgorithmId);
            Assert.Equal(TsumogiriAiStrategy.AlgorithmIdValue, Tsumogiri.AlgorithmId);
            Assert.True(Trivial.AlgorithmVersion >= 1);
            Assert.True(Tsumogiri.AlgorithmVersion >= 1);

            // Even the identical selected action cannot produce colliding recorded intents,
            // because the algorithm identity is part of the canonical intent bytes.
            AiObservation observation = Observation(new[] { Pass("pass"), Discard(20, "discard-T20") });
            LegalActionOption pass = observation.LegalActions[0];
            var trivialIntent = new CommandIntent(
                observation, pass, TrivialAiStrategy.AlgorithmIdValue, TrivialAiStrategy.AlgorithmVersionValue);
            var tsumogiriIntent = new CommandIntent(
                observation, pass, TsumogiriAiStrategy.AlgorithmIdValue, TsumogiriAiStrategy.AlgorithmVersionValue);
            Assert.NotEqual(trivialIntent.AuthorityHash, tsumogiriIntent.AuthorityHash);
        }

        [Fact]
        public void StrategyToStringIsDecisionIndependentAndCarriesNoTileData()
        {
            string trivialBefore = Trivial.ToString();
            string tsumogiriBefore = Tsumogiri.ToString();

            AiObservation observation = Observation(
                new[] { Discard(91, "discard-pin5-red"), Pass("pass") },
                hand: Hand(88, 91),
                drawn: PinFiveRed);
            Trivial.Decide(observation, CancellationToken.None);
            Tsumogiri.Decide(observation, CancellationToken.None);

            Assert.Equal(trivialBefore, Trivial.ToString());
            Assert.Equal(tsumogiriBefore, Tsumogiri.ToString());
            Assert.DoesNotContain("T88", trivialBefore, StringComparison.Ordinal);
            Assert.DoesNotContain("T91", trivialBefore, StringComparison.Ordinal);
            Assert.DoesNotContain("T88", tsumogiriBefore, StringComparison.Ordinal);
            Assert.DoesNotContain("T91", tsumogiriBefore, StringComparison.Ordinal);
        }

        [Fact]
        public void TsumogiriSeatFallbackArmsMirrorV4SourceButAreUnreachableViaObservation()
        {
            // The V4 source contains any-seat fallback arms after every viewer-scoped scan.
            // AiObservation rejects cross-seat candidates (ObservationRejectsForeignSeat...,
            // in AiDecisionTests), so Decide can never deliver them; these arms are covered
            // here directly over raw lists, which documents the port without weakening the
            // observation contract.
            int viewer = Seat;
            var foreignPass = new List<LegalActionOption> { Option(LegalActionKind.Pass, null, "pass", seat: 2) };
            Assert.Same(
                foreignPass[0],
                Tsumogiri.TryChoose(foreignPass, viewer, null, CancellationToken.None)!.Action);

            var foreignDiscard = new List<LegalActionOption>
            {
                new LegalActionOption(LegalActionKind.Discard, 2, new TileId(20), "foreign-discard")
            };
            Assert.Same(
                foreignDiscard[0],
                Tsumogiri.TryChoose(foreignDiscard, viewer, null, CancellationToken.None)!.Action);

            var foreignTsumogiri = new List<LegalActionOption>
            {
                new LegalActionOption(LegalActionKind.Discard, 2, PinFiveRed, "foreign-tsumogiri")
            };
            Assert.Same(
                foreignTsumogiri[0],
                Tsumogiri.TryChoose(foreignTsumogiri, viewer, PinFiveRed, CancellationToken.None)!.Action);
        }

        // ---------------------------------------------------------------------
        // Real service wiring: ReasonAsync -> CommandIntent -> Submit / Replay
        // ---------------------------------------------------------------------

        [Fact]
        public async Task ReasonAsyncTrivialIntentIsAdmittedAndAppliedByTheDomainArbiter()
        {
            AiObservation observation = Observation(
                new[] { Discard(91, "discard-pin5-red"), Pass("pass") },
                drawn: PinFiveRed);
            var service = new AiDecisionService(Trivial);

            AiDecision decision = await service.ReasonAsync(observation, TestContext.Current.CancellationToken);

            Assert.Same(observation, decision.Observation);
            Assert.Same(observation.LegalActions[0], decision.Intent.Action);
            Assert.Equal(TrivialAiStrategy.AlgorithmIdValue, decision.Intent.AlgorithmId);
            Assert.Equal(TrivialAiStrategy.AlgorithmVersionValue, decision.Intent.AlgorithmVersion);
            Assert.Equal(0d, decision.HeuristicScore);

            AiSubmissionResult result = service.Submit(decision, Authority(observation), 1000L);
            Assert.True(result.IsAccepted, result.Detail);

            // The accepted strategy intent feeds the Domain DeadlineArbiter unchanged.
            var clock = new DecisionClock();
            clock.Open(decision.Intent.DecisionKey, 0L);
            var control = new ControlRegistry();
            var pending = new PendingDecision(
                decision.Intent.DecisionKey,
                decision.Intent.ControlEpoch,
                observation.LegalActions,
                observation.MyDrawnTileId);
            TickResult tick = new DeadlineArbiter(clock, control).ProcessTick(
                new TickInput(observation.RoundId, 1000L, new[] { pending }, new[] { result.Submission! }));

            Assert.Empty(tick.Timeouts);
            Assert.Empty(tick.RejectedAi);
            Assert.Single(tick.AcceptedAi);
            Assert.Same(decision.Intent.Action, tick.AcceptedAi[0].Action);
        }

        [Fact]
        public async Task ReasonAsyncProducesDifferentIntentsPerStrategyOnTheSameObservation()
        {
            AiObservation observation = Observation(new[] { Pass("pass"), Ron("ron") });

            AiDecision trivial = await new AiDecisionService(Trivial)
                .ReasonAsync(observation, TestContext.Current.CancellationToken);
            AiDecision tsumogiri = await new AiDecisionService(Tsumogiri)
                .ReasonAsync(observation, TestContext.Current.CancellationToken);

            Assert.Same(observation.LegalActions[1], trivial.Intent.Action);
            Assert.Same(observation.LegalActions[0], tsumogiri.Intent.Action);
            Assert.Equal(TrivialAiStrategy.AlgorithmIdValue, trivial.Intent.AlgorithmId);
            Assert.Equal(TsumogiriAiStrategy.AlgorithmIdValue, tsumogiri.Intent.AlgorithmId);
            Assert.NotEqual(trivial.AuthorityHash, tsumogiri.AuthorityHash);

            // Both admitted results carry legal candidates only.
            Assert.True(observation.ContainsLegalAction(trivial.Intent.Action));
            Assert.True(observation.ContainsLegalAction(tsumogiri.Intent.Action));
        }

        [Fact]
        public async Task SubmitRejectsStaleRoundEpochAndLateStrategyIntents()
        {
            AiObservation observation = Observation(new[] { Discard(91, "discard-pin5-red"), Pass("pass") });
            var service = new AiDecisionService(Trivial);
            AiDecision decision = await service.ReasonAsync(observation, TestContext.Current.CancellationToken);

            AiSubmissionResult staleRound = service.Submit(
                decision,
                new AiAuthoritySnapshot(
                    observation.Seat,
                    "round-other",
                    new DecisionKey("round-other", observation.Seat, observation.DecisionId),
                    observation.ControlEpoch,
                    true,
                    false,
                    Deadline,
                    observation.LegalActions),
                1000L);
            Assert.Equal(TickRejectionCode.StaleRound, staleRound.Code);
            Assert.Null(staleRound.Submission);

            AiSubmissionResult staleEpoch = service.Submit(
                decision,
                new AiAuthoritySnapshot(
                    observation.Seat,
                    observation.RoundId,
                    observation.DecisionKey,
                    observation.ControlEpoch + 1L,
                    true,
                    false,
                    Deadline,
                    observation.LegalActions),
                1000L);
            Assert.Equal(TickRejectionCode.StaleEpoch, staleEpoch.Code);
            Assert.Null(staleEpoch.Submission);

            AiSubmissionResult late = service.Submit(decision, Authority(observation), Deadline);
            Assert.Equal(TickRejectionCode.DeadlineExpired, late.Code);
            Assert.Null(late.Submission);

            AiSubmissionResult fresh = service.Submit(decision, Authority(observation), Deadline - 1L);
            Assert.True(fresh.IsAccepted, fresh.Detail);
        }

        [Fact]
        public async Task ReplayRecordedStrategyIntentWithoutRunningTheStrategyAgain()
        {
            AiObservation observation = Observation(
                new[] { Discard(91, "discard-pin5-red"), Pass("pass") },
                drawn: PinFiveRed);
            var service = new AiDecisionService(Tsumogiri);
            AiDecision decision = await service.ReasonAsync(observation, TestContext.Current.CancellationToken);

            // A value-identical observation models a restart; replay injects the record.
            AiObservation replayObservation = Observation(
                new[] { Discard(91, "discard-pin5-red"), Pass("pass") },
                drawn: PinFiveRed);
            AiDecision replay = AiDecisionService.Replay(decision.Intent, replayObservation);

            Assert.Same(decision.Intent, replay.Intent);
            Assert.Equal(decision.AuthorityHash, replay.AuthorityHash);
            Assert.Equal(0d, replay.HeuristicScore);
            Assert.True(replayObservation.ContainsLegalAction(replay.Intent.Action));

            AiSubmissionResult result = service.Submit(replay, Authority(replayObservation), 1000L);
            Assert.True(result.IsAccepted, result.Detail);
            Assert.Equal(decision.AuthorityHash, result.RecordedIntent.AuthorityHash);
        }

        // ---------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------

        private static LegalActionOption Discard(int tileValue, string optionId) =>
            new LegalActionOption(LegalActionKind.Discard, Seat, new TileId(tileValue), optionId);

        private static LegalActionOption Pass(string optionId) =>
            new LegalActionOption(LegalActionKind.Pass, Seat, optionId: optionId);

        private static LegalActionOption Tsumo() =>
            new LegalActionOption(LegalActionKind.Tsumo, Seat);

        private static LegalActionOption Ron(string optionId) =>
            new LegalActionOption(LegalActionKind.Ron, Seat, optionId: optionId);

        private static LegalActionOption Option(LegalActionKind kind, int? tile, string optionId, int seat = Seat) =>
            new LegalActionOption(kind, seat, tile.HasValue ? new TileId(tile.Value) : (TileId?)null, optionId);

        private static IReadOnlyList<TileId> Hand(params int[] tileValues)
        {
            if (tileValues.Length == 0)
            {
                return new TileId[] { new TileId(20), new TileId(24), new TileId(28) };
            }

            var hand = new List<TileId>(tileValues.Length);
            foreach (int value in tileValues)
            {
                hand.Add(new TileId(value));
            }

            return hand;
        }

        private static AiObservation Observation(
            IReadOnlyList<LegalActionOption> actions,
            IReadOnlyList<TileId>? hand = null,
            TileId? drawn = null)
        {
            return new AiObservation(
                "session-baseline-1",
                new DecisionKey(RoundId, Seat, DecisionId),
                Epoch,
                7L,
                Deadline,
                RoundPhase.Discard,
                0,
                1,
                1,
                0,
                0,
                69,
                hand ?? Hand(),
                drawn,
                Array.Empty<AiVisibleMeld>(),
                Array.Empty<AiVisibleMeld>(),
                new[] { new AiVisibleDiscard(2, new TileId(60)) },
                new[] { new TileId(100) },
                new[] { 25000L, 25000L, 25000L, 25000L },
                actions);
        }

        private static AiAuthoritySnapshot Authority(AiObservation observation)
        {
            return new AiAuthoritySnapshot(
                observation.Seat,
                observation.RoundId,
                observation.DecisionKey,
                observation.ControlEpoch,
                true,
                false,
                observation.DecisionDeadlineMillis,
                observation.LegalActions);
        }
    }
}
