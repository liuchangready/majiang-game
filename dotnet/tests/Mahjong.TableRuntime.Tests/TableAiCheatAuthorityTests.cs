using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Mahjong.Ai;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    /// <summary>
    /// Minimal authority-path coverage for the new AI cheat capture/submit entry points.
    /// These tests intentionally stay on existing fixtures and focus on rejection side effects,
    /// the takeover spend fence, duplicate replay and durable recovery.
    /// </summary>
    public sealed class TableAiCheatAuthorityTests
    {
        private const int InitialPoints = 120;

        [Fact]
        public async Task NonAutomatedSeatCannotCaptureOrSubmitAiCheat()
        {
            await using ActorHarness harness = CreateHarness(
                P31Fixtures.Controllers(SeatControllerKind.LocalHuman));
            TableAiCheatCaptureResult captured = await harness.Actor.CaptureAiCheatOpportunityAsync(
                0,
                false,
                TestContext.Current.CancellationToken);
            Assert.Equal(TableAiCheatCaptureStatus.Rejected, captured.Status);
            Assert.Equal(TableRuntimeReasonCode.SeatNotAutomated, captured.ReasonCode);
            Assert.Null(captured.Opportunity);
        }

        [Fact]
        public async Task StaleStateControlAndRecoveryPlansAreRejectedWithoutFee()
        {
            await using (ActorHarness harness = CreateHarness(
                P31Fixtures.Controllers(SeatControllerKind.Computer)))
            {
                TableAiCheatPlan plan = await CaptureTileMarkPlanAsync(harness.Actor);
                long balance = CheatBalance(harness.Actor, 0);

                // Advance the round: the captured expected state version becomes stale.
                RoundStateMachine round = harness.Actor.GetRoundStateForHost();
                int seat = round.CurrentSeat;
                TileId discard = round.Tiles.TilesInZone(TileZone.Hand, seat)[0];
                TableOperationResult moved = await harness.SubmitAsync(P31Fixtures.Command(
                    RoundCommand.Discard(round.RoundId, seat, discard),
                    0,
                    "advance"));
                Assert.Equal(TableOperationStatus.Accepted, moved.Status);

                TableOperationResult staleState = await harness.Actor.SubmitAiCheatAsync(
                    plan,
                    false,
                    TestContext.Current.CancellationToken);
                Assert.Equal(TableOperationStatus.Rejected, staleState.Status);
                Assert.Equal(TableRuntimeReasonCode.StaleStateVersion, staleState.ReasonCode);
                Assert.Equal(balance, CheatBalance(harness.Actor, 0));
            }

            await using (ActorHarness harness = CreateHarness(
                P31Fixtures.Controllers(SeatControllerKind.Computer)))
            {
                TableAiCheatPlan plan = await CaptureTileMarkPlanAsync(harness.Actor);
                long balance = CheatBalance(harness.Actor, 0);
                TableAiCheatPlan controlForged = WithEnvelope(
                    plan,
                    controlEpoch: new ControlEpoch(plan.Envelope.ControlEpoch.Value + 1UL));
                TableOperationResult staleControl = await harness.Actor.SubmitAiCheatAsync(
                    controlForged,
                    false,
                    TestContext.Current.CancellationToken);
                Assert.Equal(TableOperationStatus.Rejected, staleControl.Status);
                Assert.Equal(TableRuntimeReasonCode.StaleControlEpoch, staleControl.ReasonCode);
                Assert.Equal(balance, CheatBalance(harness.Actor, 0));
            }
        }

        [Fact]
        public async Task TakeoverSeatCannotInitiatePaidCheatButExplicitPolicyCan()
        {
            await using ActorHarness harness = CreateHarness(
                P31Fixtures.Controllers(SeatControllerKind.Takeover));

            TableAiCheatCaptureResult blocked = await harness.Actor.CaptureAiCheatOpportunityAsync(
                0,
                false,
                TestContext.Current.CancellationToken);
            Assert.True(blocked.IsCaptured);
            Assert.Empty(blocked.Opportunity!.View.Options);

            TableAiCheatCaptureResult allowed = await harness.Actor.CaptureAiCheatOpportunityAsync(
                0,
                true,
                TestContext.Current.CancellationToken);
            Assert.True(allowed.IsCaptured);
            TableAiCheatPlan plan = Plan(allowed.Opportunity!, "tile_mark");
            long before = CheatBalance(harness.Actor, 0);

            TableOperationResult defaultDenied = await harness.Actor.SubmitAiCheatAsync(
                plan,
                false,
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Rejected, defaultDenied.Status);
            Assert.Equal(TableRuntimeReasonCode.TakeoverCheatSpendForbidden, defaultDenied.ReasonCode);
            Assert.Equal(before, CheatBalance(harness.Actor, 0));

            TableOperationResult explicitAllowed = await harness.Actor.SubmitAiCheatAsync(
                plan,
                true,
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, explicitAllowed.Status);
            long cost = before - CheatBalance(harness.Actor, 0);
            Assert.InRange(cost, 1, before);
            Assert.Equal(cost, harness.Actor.GetAiCheatSpentThisRound(0));
        }

        [Fact]
        public async Task DuplicateAiPlanIsRejectedAndChargedOnce()
        {
            await using ActorHarness harness = CreateHarness(
                P31Fixtures.Controllers(SeatControllerKind.Computer));
            TableAiCheatPlan plan = await CaptureTileMarkPlanAsync(harness.Actor);

            TableOperationResult first = await harness.Actor.SubmitAiCheatAsync(
                plan,
                false,
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, first.Status);
            long afterFirst = CheatBalance(harness.Actor, 0);
            long cost = InitialPoints - afterFirst;
            Assert.InRange(cost, 1, InitialPoints);
            Assert.Equal(cost, harness.Actor.GetAiCheatSpentThisRound(0));

            TableOperationResult replay = await harness.Actor.SubmitAiCheatAsync(
                plan,
                false,
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Rejected, replay.Status);
            Assert.True(
                replay.ReasonCode == TableRuntimeReasonCode.CheatReplayed ||
                replay.ReasonCode == TableRuntimeReasonCode.StaleStateVersion ||
                replay.ReasonCode == TableRuntimeReasonCode.StaleRecoveryGeneration,
                replay.ReasonCode + ": " + replay.Detail);
            Assert.Equal(afterFirst, CheatBalance(harness.Actor, 0));
            Assert.Equal(cost, harness.Actor.GetAiCheatSpentThisRound(0));
        }

        [Fact]
        public async Task DurableRecoveryOfPaidAiCheatKeepsLedgerAndConservationWithoutDoubleCharge()
        {
            RoundStateMachine round = P31Fixtures.Dealt("round-1");
            CheatState cheat = V32Fixtures.WithRegisteredRound(
                V32Fixtures.Cheat(InitialPoints),
                round.RoundId);
            TableCheckpoint checkpoint = WithCheat(
                DurableHarness.InitialCheckpoint(
                    round,
                    cheat.Ledger,
                    control: ComputerControlCheckpoint()),
                cheat);

            await using DurableHarness harness = await DurableHarness.CreateAsync(
                ledger: cheat.Ledger,
                initialCheckpoint: checkpoint,
                configure: options =>
                {
                    options.InitialControllers = P31Fixtures.Controllers(
                        SeatControllerKind.Computer,
                        SeatControllerKind.Computer,
                        SeatControllerKind.Computer,
                        SeatControllerKind.Computer);
                    options.CheatEconomy = V32Fixtures.Economy(InitialPoints);
                    options.SleeveBloomConfig = V32Fixtures.SleeveConfig();
                    options.InitialCheatState = cheat;
                });

            TableAiCheatPlan plan = await CaptureTileMarkPlanAsync(harness.Actor);
            TableOperationResult first = await harness.Actor.SubmitAiCheatAsync(
                plan,
                false,
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, first.Status);
            long afterFirst = harness.Actor.GetCheatLedgerForHost().Balances[0];
            long cost = InitialPoints - afterFirst;
            Assert.InRange(cost, 1, InitialPoints);

            TableRecoveryResult recovered = await harness.Actor.RecoverFromStoreAsync(
                TestContext.Current.CancellationToken);
            Assert.True(
                recovered.Status == TableRecoveryStatus.Recovered,
                recovered.ReasonCode + ": " + recovered.Detail);
            Assert.Equal(1, recovered.Report!.ReplayedCommands);
            Assert.True(recovered.Report.CheatStateRestored);
            Assert.Equal(afterFirst, harness.Actor.GetCheatLedgerForHost().Balances[0]);
            Assert.Equal(cost, harness.Actor.GetAiCheatSpentThisRound(0));
            ConservationReport conservation = TilePositionMap.Validate(
                harness.Actor.GetRoundStateForHost().Tiles);
            Assert.True(conservation.IsValid, string.Join("; ", conservation.Errors));
            Assert.Equal(136, conservation.AssignedCount);

            TableOperationResult replay = await harness.Actor.SubmitAiCheatAsync(
                plan,
                false,
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Rejected, replay.Status);
            Assert.True(
                replay.ReasonCode == TableRuntimeReasonCode.CheatReplayed ||
                replay.ReasonCode == TableRuntimeReasonCode.StaleStateVersion ||
                replay.ReasonCode == TableRuntimeReasonCode.StaleRecoveryGeneration,
                replay.ReasonCode + ": " + replay.Detail);
            Assert.Equal(afterFirst, harness.Actor.GetCheatLedgerForHost().Balances[0]);
            Assert.Equal(cost, harness.Actor.GetAiCheatSpentThisRound(0));

            TableAiCheatPlan fresh = await CaptureTileMarkPlanAsync(harness.Actor);
            TableAiCheatPlan staleGeneration = WithEnvelope(fresh, recoveryGeneration: 0L);
            TableOperationResult staleRecovery = await harness.Actor.SubmitAiCheatAsync(
                staleGeneration,
                false,
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Rejected, staleRecovery.Status);
            Assert.Equal(TableRuntimeReasonCode.StaleRecoveryGeneration, staleRecovery.ReasonCode);
            Assert.Equal(afterFirst, harness.Actor.GetCheatLedgerForHost().Balances[0]);
            Assert.Equal(cost, harness.Actor.GetAiCheatSpentThisRound(0));
        }

        [Fact]
        public async Task PaidBeamCaptureKeepsSafeDefaultAndCapsVisibleCandidatesWithoutWallFaces()
        {
            RoundStateMachine round = P31Fixtures.Dealt("round-1");
            IReadOnlyList<TileId> hand = round.Tiles.TilesInZone(TileZone.Hand, 0);
            var candidates = new List<BeamSwapCandidate>();
            for (int i = 0; i < 90; i++)
            {
                candidates.Add(new BeamSwapCandidate(new TileId(i), BeamSwapSlot.Wall(i)));
            }

            var firstOffer = new BeamSwapOffer(
                "beam-offer",
                0,
                BeamSwapWindow.Wall,
                candidates,
                hand.Skip(1).ToArray(),
                hand[0],
                0);
            CheatState firstState = V32Fixtures.WithRegisteredRound(
                V32Fixtures.Cheat(InitialPoints), round.RoundId)
                .WithBeamSwap(BeamSwapState.Restore(firstOffer, 0, 1));
            await using ActorHarness first = CreateHarness(
                P31Fixtures.Controllers(SeatControllerKind.Computer), firstState);

            TableAiCheatCaptureResult capture = await first.Actor.CaptureAiCheatOpportunityAsync(
                0,
                false,
                TestContext.Current.CancellationToken);
            Assert.True(capture.IsCaptured, capture.ReasonCode + ": " + capture.Detail);
            AiCheatOpportunity firstView = capture.Opportunity!.View;
            Assert.Equal("beam-offer", capture.Opportunity.OfferId);
            Assert.Equal(64, firstView.Options.Count);
            Assert.Equal("beam-keep", firstView.Options[0].OptionId);
            Assert.True(firstView.Options[0].IsSafeDefault);
            Assert.Equal(63, firstView.Options.Count(option => option.OptionId.StartsWith("beam-swap-", StringComparison.Ordinal)));
            Assert.Equal(hand.Count - 1, firstView.BeamEligibleHandTileIds.Count);
            Assert.Equal(90, firstView.BeamCandidates.Count);
            Assert.All(firstView.BeamCandidates, candidate =>
            {
                Assert.False(candidate.Marked);
                Assert.Null(candidate.TileId);
            });
            Assert.All(firstView.Options.Skip(1), option =>
            {
                Assert.False(option.TargetMarked);
                Assert.Null(option.TargetTileId);
                Assert.Single(option.TileIds);
                Assert.NotNull(option.TargetSlot);
            });

            // Change every hidden tile behind the unmarked slots while keeping the same visible facts.
            var changedHiddenCandidates = candidates
                .Select((candidate, index) => new BeamSwapCandidate(new TileId(135 - index), candidate.Slot))
                .ToArray();
            var hiddenChangedOffer = new BeamSwapOffer(
                "beam-offer",
                0,
                BeamSwapWindow.Wall,
                changedHiddenCandidates,
                hand.Skip(1).ToArray(),
                hand[0],
                0);
            CheatState hiddenChangedState = V32Fixtures.WithRegisteredRound(
                V32Fixtures.Cheat(InitialPoints), round.RoundId)
                .WithBeamSwap(BeamSwapState.Restore(hiddenChangedOffer, 0, 1));
            await using ActorHarness hiddenChanged = CreateHarness(
                P31Fixtures.Controllers(SeatControllerKind.Computer), hiddenChangedState);
            TableAiCheatCaptureResult hiddenChangedCapture = await hiddenChanged.Actor.CaptureAiCheatOpportunityAsync(
                0,
                false,
                TestContext.Current.CancellationToken);
            Assert.True(hiddenChangedCapture.IsCaptured);
            AiCheatOpportunity secondView = hiddenChangedCapture.Opportunity!.View;
            Assert.Equal(firstView.Options.Count, secondView.Options.Count);
            for (int i = 0; i < firstView.Options.Count; i++)
            {
                Assert.Equal(firstView.Options[i].OptionId, secondView.Options[i].OptionId);
                Assert.Equal(firstView.Options[i].CapabilityId, secondView.Options[i].CapabilityId);
                Assert.Equal(firstView.Options[i].Cost, secondView.Options[i].Cost);
                Assert.Equal(firstView.Options[i].IsSafeDefault, secondView.Options[i].IsSafeDefault);
                Assert.Equal(firstView.Options[i].IsOfferChoice, secondView.Options[i].IsOfferChoice);
                Assert.Equal(firstView.Options[i].TileIds, secondView.Options[i].TileIds);
                Assert.Equal(firstView.Options[i].TargetSlot, secondView.Options[i].TargetSlot);
                Assert.Equal(firstView.Options[i].TargetMarked, secondView.Options[i].TargetMarked);
                Assert.Equal(firstView.Options[i].TargetTileId, secondView.Options[i].TargetTileId);
            }

            for (int i = 0; i < firstView.BeamCandidates.Count; i++)
            {
                Assert.Equal(firstView.BeamCandidates[i].Slot, secondView.BeamCandidates[i].Slot);
                Assert.Equal(firstView.BeamCandidates[i].Marked, secondView.BeamCandidates[i].Marked);
                Assert.Equal(firstView.BeamCandidates[i].TileId, secondView.BeamCandidates[i].TileId);
            }
        }

        [Fact]
        public async Task PaidBeamCaptureRevealsOnlyAuthorizedMarkedSlotTileId()
        {
            RoundStateMachine round = P31Fixtures.Dealt("round-beam-marked");
            IReadOnlyList<TileId> hand = round.Tiles.TilesInZone(TileZone.Hand, 0);
            TileId markedWallTile = round.Tiles.TileAt(TilePosition.Wall(round.Tiles.WallPointer));
            TileId unmarkedWallTile = round.Tiles.TileAt(TilePosition.Wall(round.Tiles.WallPointer + 1));
            var candidates = new[]
            {
                new BeamSwapCandidate(unmarkedWallTile, BeamSwapSlot.Wall(round.Tiles.WallPointer + 1)),
                new BeamSwapCandidate(markedWallTile, BeamSwapSlot.Wall(round.Tiles.WallPointer))
            };
            var offer = new BeamSwapOffer(
                "beam-marked-offer",
                0,
                BeamSwapWindow.Wall,
                candidates,
                hand.Skip(1).Take(2).ToArray(),
                hand[0],
                0);
            TileId[] markFour = { markedWallTile, new TileId(132), new TileId(133), new TileId(134) };
            CheatState registered = V32Fixtures.WithRegisteredRound(V32Fixtures.Cheat(InitialPoints), round.RoundId);
            TileMarkState marks = registered.TileMarks
                .AddGroup(new TileMarkGroup("grp-mark-1", 0, markFour, round.RoundId, 1)).State;
            CheatState cheat = registered
                .WithTileMarks(marks)
                .WithBeamSwap(BeamSwapState.Restore(offer, 0, 1));

            await using ActorHarness harness = CreateHarness(
                P31Fixtures.Controllers(SeatControllerKind.Computer), cheat, round);
            TableAiCheatCaptureResult capture = await harness.Actor.CaptureAiCheatOpportunityAsync(
                0, false, TestContext.Current.CancellationToken);
            Assert.True(capture.IsCaptured, capture.ReasonCode + ": " + capture.Detail);

            AiCheatOpportunity view = capture.Opportunity!.View;
            AiBeamSwapCandidateFacts markedFact = view.BeamCandidates.Single(c => c.Slot.Index == round.Tiles.WallPointer);
            AiBeamSwapCandidateFacts unmarkedFact = view.BeamCandidates.Single(c => c.Slot.Index == round.Tiles.WallPointer + 1);
            Assert.True(markedFact.Marked);
            Assert.Equal(markedWallTile.Value, markedFact.TileId);
            Assert.False(unmarkedFact.Marked);
            Assert.Null(unmarkedFact.TileId);

            // Marked slot sorts ahead of unmarked slot in swap options.
            Assert.Equal("beam-keep", view.Options[0].OptionId);
            Assert.True(view.Options[1].TargetMarked);
            Assert.Equal(markedWallTile, view.Options[1].TargetTileId);
            Assert.False(view.Options[3].TargetMarked);
            Assert.Null(view.Options[3].TargetTileId);
        }

        [Fact]
        public async Task OctopusThirdSleeveCandidateIsCapturedAndCommittedForNonZeroSeat()
        {
            const int seat = 1;
            RoundStateMachine round = RoundStateMachine.CreateDealt(
                new RoundSpec("round-octopus-ai", seat, 1, 0, 0, new long[] { 25000, 25000, 25000, 25000 }, 0),
                TilePositionMap.CreateDealt(RecordedRandomDraw.Standard("round-octopus-ai"), seat),
                RoundPhase.CheatWindow);
            int turn = round.Events.Count(evt => evt.Type == RoundEventType.TileDrawn || evt.Type == RoundEventType.RinshanDrawn);
            TileId first = round.Tiles.DrawnTile!.Value.Tile;
            TileId second = round.Tiles.TileAt(TilePosition.Wall(round.Tiles.WallPointer));
            TileId third = round.Tiles.TileAt(TilePosition.Wall(round.Tiles.WallPointer + 1));
            var offer = new SleeveBloomOffer(
                "octopus-offer", seat, SleeveBloomDrawKind.Normal, first, second, turn,
                SleeveBloomOfferMode.Immediate, thirdTile: third);
            string[] characters = { "player", "octopus", "player", "player" };
            CheatState cheat = V32Fixtures.WithRegisteredRound(V32Fixtures.Cheat(InitialPoints), round.RoundId)
                .WithCharacters(characters)
                .WithSleeveBloom(new SleeveBloomState().WithImmediateOffer(offer).WithUsedOnTurn(turn));

            await using ActorHarness harness = CreateHarness(
                P31Fixtures.Controllers(SeatControllerKind.Computer), cheat, round);
            TableAiCheatCaptureResult capture = await harness.Actor.CaptureAiCheatOpportunityAsync(
                seat, false, TestContext.Current.CancellationToken);
            Assert.True(capture.IsCaptured, capture.ReasonCode + ": " + capture.Detail);
            Assert.Equal("octopus-offer", capture.Opportunity!.OfferId);
            Assert.Equal(third, capture.Opportunity.View.SleeveThirdTileId);
            AiCheatOption thirdOption = Assert.Single(
                capture.Opportunity.View.Options,
                option => option.OptionId == "sleeve-third");
            Assert.Equal(new[] { third }, thirdOption.TileIds);
            TableAiCheatPlan thirdPlan = capture.Opportunity.PlanFor("sleeve-third")!;

            TableOperationResult committed = await harness.Actor.SubmitAiCheatAsync(
                thirdPlan, false, TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, committed.Status);
            Assert.Equal(third, harness.Actor.GetRoundStateForHost().Tiles.DrawnTile!.Value.Tile);
            Assert.Equal(InitialPoints, CheatBalance(harness.Actor, seat));
            Assert.Equal(136, TilePositionMap.Validate(harness.Actor.GetRoundStateForHost().Tiles).AssignedCount);
        }

        [Fact]
        public async Task NonConfirmedRinshanOctopusStillOmitsThirdSleeveCandidate()
        {
            const int seat = 1;
            int[] dealerSlots = { 0, 1, 2, 3, 16, 17, 18, 19, 32, 33, 34, 35, 48, 52 };
            int[] handTiles = { 0, 1, 2, 3, 36, 40, 44, 48, 52, 56, 60, 64, 68, 72 };
            var specific = new Dictionary<int, int>();
            for (int i = 0; i < dealerSlots.Length; i++)
            {
                specific[dealerSlots[i]] = handTiles[i];
            }

            var used = new HashSet<int>(specific.Values);
            var remaining = new Queue<int>(Enumerable.Range(0, TileId.Count).Where(id => !used.Contains(id)));
            var order = new TileId[TileId.Count];
            for (int i = 0; i < TileId.Count; i++)
            {
                order[i] = new TileId(specific.TryGetValue(i, out int val) ? val : remaining.Dequeue());
            }

            RoundStateMachine dealt = RoundStateMachine.CreateDealt(
                new RoundSpec("round-octopus-rinshan", seat, 1, 0, 0, new long[] { 25000, 25000, 25000, 25000 }, 0),
                TilePositionMap.CreateDealt(RecordedRandomDraw.Create("rinshan-wall", order), seat),
                RoundPhase.CheatWindow);
            RoundTransition kanTransition = dealt.Execute(RoundCommand.ClosedKan(
                dealt.RoundId, seat, new TileId(0), new TileId(1), new TileId(2), new TileId(3)));
            Assert.True(kanTransition.IsAccepted, kanTransition.Error?.ToString());
            RoundStateMachine round = kanTransition.State!;
            Assert.Equal(DrawSource.Rinshan, round.Tiles.DrawnTile!.Value.Source);
            CheatState cheat = V32Fixtures.WithRegisteredRound(V32Fixtures.Cheat(InitialPoints), round.RoundId)
                .WithCharacters(new[] { "player", "octopus", "player", "player" });

            await using ActorHarness harness = CreateHarness(
                P31Fixtures.Controllers(SeatControllerKind.Computer), cheat, round);
            TableAiCheatCaptureResult capture = await harness.Actor.CaptureAiCheatOpportunityAsync(
                seat, false, TestContext.Current.CancellationToken);
            Assert.True(capture.IsCaptured);
            TableAiCheatPlan activate = capture.Opportunity!.PlanFor("sleeve-activate")!;
            TableOperationResult activated = await harness.Actor.SubmitAiCheatAsync(
                activate, false, TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, activated.Status);

            TableAiCheatCaptureResult offerCapture = await harness.Actor.CaptureAiCheatOpportunityAsync(
                seat, false, TestContext.Current.CancellationToken);
            Assert.True(offerCapture.IsCaptured);
            Assert.Null(offerCapture.Opportunity!.View.SleeveThirdTileId);
            Assert.DoesNotContain(offerCapture.Opportunity.View.Options, o => o.OptionId == "sleeve-third");
            Assert.Equal(2, offerCapture.Opportunity.View.Options.Count);
        }

        [Fact]
        public async Task TwoChoiceSleeveRejectsThirdWithoutConsumingTheOffer()
        {
            const int seat = 1;
            RoundStateMachine round = RoundStateMachine.CreateDealt(
                new RoundSpec("round-octopus-two", seat, 1, 0, 0, new long[] { 25000, 25000, 25000, 25000 }, 0),
                TilePositionMap.CreateDealt(RecordedRandomDraw.Standard("round-octopus-two"), seat),
                RoundPhase.CheatWindow);
            int turn = round.Events.Count(evt => evt.Type == RoundEventType.TileDrawn || evt.Type == RoundEventType.RinshanDrawn);
            TileId first = round.Tiles.DrawnTile!.Value.Tile;
            TileId second = round.Tiles.TileAt(TilePosition.Wall(round.Tiles.WallPointer));
            var offer = new SleeveBloomOffer(
                "two-choice-offer", seat, SleeveBloomDrawKind.Normal, first, second, turn,
                SleeveBloomOfferMode.Immediate);
            CheatState cheat = V32Fixtures.WithRegisteredRound(V32Fixtures.Cheat(InitialPoints), round.RoundId)
                .WithCharacters(new[] { "player", "octopus", "player", "player" })
                .WithSleeveBloom(new SleeveBloomState().WithImmediateOffer(offer).WithUsedOnTurn(turn));

            await using ActorHarness harness = CreateHarness(
                P31Fixtures.Controllers(SeatControllerKind.Computer), cheat, round);
            TableAiCheatCaptureResult capture = await harness.Actor.CaptureAiCheatOpportunityAsync(
                seat, false, TestContext.Current.CancellationToken);
            Assert.True(capture.IsCaptured);
            Assert.DoesNotContain(capture.Opportunity!.View.Options, option => option.OptionId == "sleeve-third");
            ulong version = (await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken)).StateVersion.Value;
            TableCheatCommandEnvelope capturedEnvelope = capture.Opportunity.Plans[0].Envelope;

            var command = new RoundCheatCommand(
                round.RoundId,
                seat,
                RoundCheatAction.SubmitSleeveBloomChoice,
                "invalid-third",
                RoundCheatPayload.SleeveBloomChoice(offer.OfferId, SleeveBloomKeep.Third));
            var envelope = new TableCheatCommandEnvelope(
                command,
                new RequestId("invalid-third"),
                new StateVersion(version),
                capturedEnvelope.OwnerEpoch,
                capturedEnvelope.ControlEpoch,
                scopeId: new RequestScopeId("ai-test"),
                api: "TEST_CHEAT",
                payloadSha256: TablePayloadSha256.Compute(System.Text.Encoding.UTF8.GetBytes("invalid-third")));
            TableOperationResult rejected = await harness.Actor.SubmitCheatAsync(
                envelope, TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Rejected, rejected.Status);
            Assert.Equal("CHEAT_INVALID_SELECTION", rejected.ReasonCode);
            Assert.Equal(version, (await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken)).StateVersion.Value);
            Assert.Equal(first, harness.Actor.GetRoundStateForHost().Tiles.DrawnTile!.Value.Tile);
            Assert.Equal(InitialPoints, CheatBalance(harness.Actor, seat));
            TableAiCheatCaptureResult stillOpen = await harness.Actor.CaptureAiCheatOpportunityAsync(
                seat, false, TestContext.Current.CancellationToken);
            Assert.Equal(offer.OfferId, stillOpen.Opportunity!.OfferId);
            Assert.Equal(136, TilePositionMap.Validate(harness.Actor.GetRoundStateForHost().Tiles).AssignedCount);
        }

        [Fact]
        public async Task AiCheatCaptureFreezesPendingIdentityAndDeadlineAtExactBoundary()
        {
            await using ActorHarness harness = CreateHarness(P31Fixtures.Controllers(SeatControllerKind.Computer));
            var pending = new PendingDecision(
                new DecisionKey("round-1", 0, "ai-pending"),
                0L,
                new[] { new LegalActionOption(LegalActionKind.Tsumo, 0) });
            Assert.Equal(TableOperationStatus.Configured,
                (await harness.Actor.OpenDecisionAsync(pending, TestContext.Current.CancellationToken)).Status);

            TableAiCheatCaptureResult capture = await harness.Actor.CaptureAiCheatOpportunityAsync(
                0, false, TestContext.Current.CancellationToken);
            Assert.True(capture.IsCaptured);
            Assert.Equal("ai-pending", capture.Opportunity!.DecisionId!.Value.Value);
            Assert.Equal(2000L, capture.Opportunity.DeadlineMillis);
            TableAiCheatPlan plan = capture.Opportunity.Plans[0];
            Assert.Equal(capture.Opportunity.DeadlineMillis, plan.Envelope.DecisionDeadlineMillis);

            harness.Clock.Now = 2000L;
            TableOperationResult expired = await harness.Actor.SubmitAiCheatAsync(
                plan, false, TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Rejected, expired.Status);
            Assert.Equal(TableRuntimeReasonCode.DeadlineExpired, expired.ReasonCode);
            Assert.Equal(InitialPoints, CheatBalance(harness.Actor, 0));
            Assert.Equal(0UL, (await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken)).StateVersion.Value);
        }

        private static (RoundStateMachine Round, int Seat) NormalDrawSeatOneRound(string roundId)
        {
            RoundStateMachine dealt = P31Fixtures.Dealt(roundId);
            TileId discard = dealt.Tiles.TilesInZone(TileZone.Hand, 0)[0];
            RoundTransition transitioned = dealt.Execute(RoundCommand.Discard(
                dealt.RoundId, 0, discard, Array.Empty<int>()));
            Assert.True(transitioned.IsAccepted, transitioned.Error?.ToString());
            RoundStateMachine next = transitioned.State!;
            Assert.Equal(1, next.CurrentSeat);
            Assert.Equal(RoundPhase.CheatWindow, next.Phase);
            return (next, 1);
        }

        [Fact]
        public async Task ImmediateOfferWithoutPendingHasNoSentinelDeadlineAndAcceptsAcrossClockTicks()
        {
            (RoundStateMachine round, int seat) = NormalDrawSeatOneRound("round-imm-clock");
            await using ActorHarness harness = CreateHarness(
                P31Fixtures.Controllers(SeatControllerKind.Computer), initialRound: round);
            TableAiCheatCaptureResult activateCapture = await harness.Actor.CaptureAiCheatOpportunityAsync(
                seat, false, TestContext.Current.CancellationToken);
            Assert.True(activateCapture.IsCaptured, activateCapture.ReasonCode + ": " + activateCapture.Detail);
            Assert.Null(activateCapture.Opportunity!.DecisionId);
            Assert.Null(activateCapture.Opportunity.DeadlineMillis);
            TableAiCheatPlan activatePlan = activateCapture.Opportunity.PlanFor("sleeve-activate")!;
            Assert.Null(activatePlan.Envelope.DecisionDeadlineMillis);

            harness.Clock.Now = 5000L;
            TableOperationResult activated = await harness.Actor.SubmitAiCheatAsync(
                activatePlan, false, TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, activated.Status);

            TableAiCheatCaptureResult offerCapture = await harness.Actor.CaptureAiCheatOpportunityAsync(
                seat, false, TestContext.Current.CancellationToken);
            Assert.True(offerCapture.IsCaptured);
            Assert.Null(offerCapture.Opportunity!.DeadlineMillis);
            Assert.Null(offerCapture.Opportunity.OfferDeadlineMillis);
            harness.Clock.Now = 9000L;
            TableOperationResult chosen = await harness.Actor.SubmitAiCheatAsync(
                offerCapture.Opportunity.PlanFor("sleeve-second")!,
                false,
                TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, chosen.Status);
            Assert.Equal(136, TilePositionMap.Validate(harness.Actor.GetRoundStateForHost().Tiles).AssignedCount);
        }

        [Fact]
        public async Task OffTurnSleevePlanFreezesAndChecksCapturedWindowIdDeadlineAndCommitsValidChoice()
        {
            RoundStateMachine round = P31Fixtures.Dealt("round-ai-offturn-window");
            int fromSeat = round.CurrentSeat;
            int seat = (fromSeat + 1) % 4;
            TileId discard = round.Tiles.TilesInZone(TileZone.Hand, fromSeat)[0];
            RoundTransition discarded = round.Execute(RoundCommand.Discard(
                round.RoundId, fromSeat, discard, new[] { 1, 2, 3 }));
            Assert.True(discarded.IsAccepted, discarded.Error?.ToString());
            round = discarded.State!;
            Assert.NotNull(round.Window);
            const string windowId = "bloom-window-ai-offturn";
            int turn = round.Events.Count(evt => evt.Type == RoundEventType.TileDrawn || evt.Type == RoundEventType.RinshanDrawn);
            TileId first = round.Tiles.TileAt(TilePosition.Wall(round.Tiles.WallPointer));
            TileId second = round.Tiles.TileAt(TilePosition.Wall(round.Tiles.WallPointer + 1));
            var offer = new SleeveBloomOffer(
                "offturn-ai-window-offer", seat, SleeveBloomDrawKind.Normal, first, second, turn,
                SleeveBloomOfferMode.OffTurn, windowId, deadlineMillis: 5000, createdAtMillis: 1000, bootId: string.Empty);
            CheatState cheat = V32Fixtures.WithRegisteredRound(V32Fixtures.Cheat(InitialPoints), round.RoundId)
                .WithSleeveBloom(new SleeveBloomState().WithOffTurnOffer(offer));

            // 1. Stale window is rejected zero-side-effect, then valid off-turn AI plan commits before deadline (4999 < 5000).
            await using (ActorHarness harness = CreateHarness(
                P31Fixtures.Controllers(SeatControllerKind.Computer), cheat, round))
            {
                TableAiCheatCaptureResult capture = await harness.Actor.CaptureAiCheatOpportunityAsync(
                    seat, false, TestContext.Current.CancellationToken);
                Assert.True(capture.IsCaptured, capture.ReasonCode + ": " + capture.Detail);
                Assert.Equal(offer.OfferId, capture.Opportunity!.OfferId);
                Assert.Equal(windowId, capture.Opportunity.WindowId);
                Assert.Equal(5000L, capture.Opportunity.OfferDeadlineMillis);
                TableAiCheatPlan plan = capture.Opportunity.PlanFor("sleeve-second")!;
                Assert.Equal(windowId, plan.Envelope.WindowId);
                Assert.Equal(5000L, plan.Envelope.OfferDeadlineMillis);

                TableAiCheatPlan staleWindow = WithEnvelope(plan, windowId: "different-window");
                TableOperationResult rejected = await harness.Actor.SubmitAiCheatAsync(
                    staleWindow, false, TestContext.Current.CancellationToken);
                Assert.Equal(TableOperationStatus.Rejected, rejected.Status);
                Assert.Equal(TableRuntimeReasonCode.StaleDecision, rejected.ReasonCode);
                Assert.Equal(0UL, (await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken)).StateVersion.Value);
                Assert.Equal(InitialPoints, CheatBalance(harness.Actor, seat));
                Assert.Equal(offer.OfferId, (await harness.Actor.CaptureAiCheatOpportunityAsync(
                    seat, false, TestContext.Current.CancellationToken)).Opportunity!.OfferId);

                harness.Clock.Now = 4999L;
                TableOperationResult accepted = await harness.Actor.SubmitAiCheatAsync(
                    plan, false, TestContext.Current.CancellationToken);
                Assert.True(accepted.Status == TableOperationStatus.Accepted, accepted.ReasonCode + ": " + accepted.Detail);
                Assert.Equal(1UL, (await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken)).StateVersion.Value);
                Assert.Equal(136, TilePositionMap.Validate(harness.Actor.GetRoundStateForHost().Tiles).AssignedCount);
            }

            // 2. Exact deadline boundary (5000 == 5000) rejects zero-side-effect in actor so authority tick sweeps First.
            await using (ActorHarness expiredHarness = CreateHarness(
                P31Fixtures.Controllers(SeatControllerKind.Computer), cheat, round))
            {
                TableAiCheatCaptureResult capture = await expiredHarness.Actor.CaptureAiCheatOpportunityAsync(
                    seat, false, TestContext.Current.CancellationToken);
                TableAiCheatPlan plan = capture.Opportunity!.PlanFor("sleeve-second")!;
                expiredHarness.Clock.Now = 5000L;
                TableOperationResult expired = await expiredHarness.Actor.SubmitAiCheatAsync(
                    plan, false, TestContext.Current.CancellationToken);
                Assert.Equal(TableOperationStatus.Rejected, expired.Status);
                Assert.Equal(TableRuntimeReasonCode.DeadlineExpired, expired.ReasonCode);
                Assert.Equal(0UL, (await expiredHarness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken)).StateVersion.Value);
                Assert.Equal(offer.OfferId, (await expiredHarness.Actor.CaptureAiCheatOpportunityAsync(
                    seat, false, TestContext.Current.CancellationToken)).Opportunity!.OfferId);
            }
        }

        [Fact]
        public async Task ZeroSideEffectRejectionMatrixPreservesOffersLedgerAndConservation()
        {
            (RoundStateMachine round, int seat) = NormalDrawSeatOneRound("round-matrix");
            int turn = round.Events.Count(evt => evt.Type == RoundEventType.TileDrawn || evt.Type == RoundEventType.RinshanDrawn);
            IReadOnlyList<TileId> hand = round.Tiles.TilesInZone(TileZone.Hand, seat);
            TileId drawn = round.Tiles.DrawnTile!.Value.Tile;
            TileId[] eligibleHand = hand.Where(tile => tile != drawn).ToArray();
            var beamCandidates = new[]
            {
                new BeamSwapCandidate(round.Tiles.TileAt(TilePosition.Wall(round.Tiles.WallPointer)), BeamSwapSlot.Wall(round.Tiles.WallPointer))
            };
            var beamOffer = new BeamSwapOffer(
                "beam-matrix-offer", seat, BeamSwapWindow.Wall, beamCandidates, eligibleHand, drawn, turn);
            CheatState cheat = V32Fixtures.WithRegisteredRound(V32Fixtures.Cheat(InitialPoints), round.RoundId)
                .WithBeamSwap(BeamSwapState.Restore(beamOffer, turn, 1));
            await using ActorHarness harness = CreateHarness(
                P31Fixtures.Controllers(SeatControllerKind.Computer), cheat, round);

            TableAiCheatCaptureResult capture = await harness.Actor.CaptureAiCheatOpportunityAsync(
                seat, false, TestContext.Current.CancellationToken);
            Assert.True(capture.IsCaptured, capture.ReasonCode + ": " + capture.Detail);
            TableAiCheatPlan validSwap = capture.Opportunity!.Plans.First(p => p.Option.OptionId.StartsWith("beam-swap-", StringComparison.Ordinal));

            // 1. Cross-round
            TableOperationResult roundMismatch = await harness.Actor.SubmitAiCheatAsync(
                WithEnvelope(validSwap, roundId: "other-round"), false, TestContext.Current.CancellationToken);
            Assert.Equal(TableRuntimeReasonCode.RoundMismatch, roundMismatch.ReasonCode);

            // 2. OwnerEpoch mismatch
            TableOperationResult staleOwner = await harness.Actor.SubmitAiCheatAsync(
                WithEnvelope(validSwap, ownerEpoch: new OwnerEpoch(999)), false, TestContext.Current.CancellationToken);
            Assert.Equal(TableRuntimeReasonCode.StaleOwnerEpoch, staleOwner.ReasonCode);

            // 3. DecisionId mismatch (when no pending decision is open)
            TableOperationResult decMismatch = await harness.Actor.SubmitAiCheatAsync(
                WithEnvelope(validSwap, decisionId: new DecisionId("forged-dec")), false, TestContext.Current.CancellationToken);
            Assert.Equal(TableRuntimeReasonCode.DecisionMismatch, decMismatch.ReasonCode);

            // 4. OfferId mismatch
            TableOperationResult offerMismatch = await harness.Actor.SubmitAiCheatAsync(
                WithEnvelope(validSwap, payload: RoundCheatPayload.BeamSwapChoice(
                    "wrong-offer", BeamSwapAction.KeepOriginal, null, null)),
                false,
                TestContext.Current.CancellationToken);
            Assert.Equal("CHEAT_OFFER_MISMATCH", offerMismatch.ReasonCode);

            // 5. Forged hand tile (drawn tile is not eligible)
            TableOperationResult forgedHand = await harness.Actor.SubmitAiCheatAsync(
                WithEnvelope(validSwap, payload: RoundCheatPayload.BeamSwapChoice(
                    beamOffer.OfferId, BeamSwapAction.Swap, drawn, beamCandidates[0].Slot)),
                false,
                TestContext.Current.CancellationToken);
            Assert.Equal("CHEAT_INVALID_SELECTION", forgedHand.ReasonCode);

            // 6. Forged target slot (not in frozen candidates)
            TableOperationResult forgedSlot = await harness.Actor.SubmitAiCheatAsync(
                WithEnvelope(validSwap, payload: RoundCheatPayload.BeamSwapChoice(
                    beamOffer.OfferId, BeamSwapAction.Swap, eligibleHand[0], BeamSwapSlot.Wall(round.Tiles.WallPointer + 5))),
                false,
                TestContext.Current.CancellationToken);
            Assert.Equal("CHEAT_CANDIDATE_NOT_FOUND", forgedSlot.ReasonCode);

            // 7. Reclaim control advances ControlEpoch -> old plan rejected as StaleControlEpoch
            TableControlResult reclaimed = await harness.Actor.ReclaimControlAsync(
                seat, new OwnerEpoch(1), new ControlEpoch(0), "reclaim", TestContext.Current.CancellationToken);
            Assert.Equal(TableControlStatus.Changed, reclaimed.Status);
            TableOperationResult staleControl = await harness.Actor.SubmitAiCheatAsync(
                validSwap, false, TestContext.Current.CancellationToken);
            Assert.Equal(TableRuntimeReasonCode.StaleControlEpoch, staleControl.ReasonCode);

            // Zero side effects across all 7 rejections: version, balance, 136 tiles, and beamOffer intact.
            Assert.Equal(0UL, (await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken)).StateVersion.Value);
            Assert.Equal(InitialPoints, CheatBalance(harness.Actor, seat));
            Assert.True(harness.Actor.HasActiveCheatOffer(seat));
            Assert.Equal(136, TilePositionMap.Validate(harness.Actor.GetRoundStateForHost().Tiles).AssignedCount);
        }

        [Fact]
        public async Task TimeoutWithPendingImmediateOfferSettlesFirstAndReturnsWithoutAutoDiscard()
        {
            (RoundStateMachine round, int seat) = NormalDrawSeatOneRound("round-timeout-offer");
            await using ActorHarness harness = CreateHarness(
                P31Fixtures.Controllers(SeatControllerKind.Computer), initialRound: round);
            TableAiCheatCaptureResult actCap = await harness.Actor.CaptureAiCheatOpportunityAsync(
                seat, false, TestContext.Current.CancellationToken);
            TableOperationResult activated = await harness.Actor.SubmitAiCheatAsync(
                actCap.Opportunity!.PlanFor("sleeve-activate")!, false, TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, activated.Status);
            long afterActivateBalance = CheatBalance(harness.Actor, seat);
            Assert.Equal(InitialPoints - 10, afterActivateBalance);

            RoundStateMachine current = harness.Actor.GetRoundStateForHost();
            TileId drawn = current.Tiles.DrawnTile!.Value.Tile;
            var pending = new PendingDecision(
                new DecisionKey(current.RoundId, seat, "timeout-offer-dec"),
                0L,
                new[] { new LegalActionOption(LegalActionKind.Discard, seat, drawn) },
                drawn);
            Assert.Equal(TableOperationStatus.Configured,
                (await harness.Actor.OpenDecisionAsync(pending, TestContext.Current.CancellationToken)).Status);

            harness.Clock.Now = 2000L;
            TableTickResult tick = await harness.Actor.TickAsync(
                TableTickRequest.Empty, TestContext.Current.CancellationToken);
            TableTickEntry entry = Assert.Single(tick.Entries);
            Assert.Equal(TickEntryKind.Timeout, entry.Kind);
            Assert.Equal(TableOperationStatus.Accepted, entry.Transition!.Status);
            Assert.False(harness.Actor.HasActiveCheatOffer(seat));
            Assert.Equal(drawn, harness.Actor.GetRoundStateForHost().Tiles.DrawnTile!.Value.Tile);
            Assert.Equal(0, harness.Actor.GetRoundStateForHost().Tiles.CountAt(TileZone.Discard, seat));
            Assert.Equal(afterActivateBalance, CheatBalance(harness.Actor, seat));
            Assert.Equal(136, TilePositionMap.Validate(harness.Actor.GetRoundStateForHost().Tiles).AssignedCount);
        }

        [Fact]
        public async Task LegalOrdinaryCommandSettlesOfferAtomicallyWhileIllegalOrdinaryCommandKeepsOffer()
        {
            (RoundStateMachine round, int seat) = NormalDrawSeatOneRound("round-legal-settle");
            await using ActorHarness harness = CreateHarness(
                P31Fixtures.Controllers(SeatControllerKind.Computer), initialRound: round);
            TableAiCheatCaptureResult actCap = await harness.Actor.CaptureAiCheatOpportunityAsync(
                seat, false, TestContext.Current.CancellationToken);
            TableOperationResult activated = await harness.Actor.SubmitAiCheatAsync(
                actCap.Opportunity!.PlanFor("beam-activate")!, false, TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Accepted, activated.Status);
            Assert.True(harness.Actor.HasActiveCheatOffer(seat));

            RoundStateMachine current = harness.Actor.GetRoundStateForHost();
            TileId foreignTile = current.Tiles.TilesInZone(TileZone.Hand, 0)[0];
            TableOperationResult illegal = await harness.SubmitAsync(P31Fixtures.Command(
                RoundCommand.Discard(current.RoundId, seat, foreignTile), 1, "illegal-discard"));
            Assert.Equal(TableOperationStatus.Rejected, illegal.Status);
            Assert.True(harness.Actor.HasActiveCheatOffer(seat));
            Assert.Equal(1UL, (await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken)).StateVersion.Value);

            TileId ownTile = current.Tiles.TilesInZone(TileZone.Hand, seat)[0];
            TableOperationResult legal = await harness.SubmitAsync(P31Fixtures.Command(
                RoundCommand.Discard(current.RoundId, seat, ownTile), 1, "legal-discard"));
            Assert.Equal(TableOperationStatus.Accepted, legal.Status);
            Assert.False(harness.Actor.HasActiveCheatOffer(seat));
            Assert.Equal(2UL, (await harness.Actor.GetSnapshotAsync(TestContext.Current.CancellationToken)).StateVersion.Value);
            Assert.Equal(136, TilePositionMap.Validate(harness.Actor.GetRoundStateForHost().Tiles).AssignedCount);
        }

        [Fact]
        public async Task TakeoverCanResolveAlreadyPaidOfferViaSafeDefaultWithoutNewSpend()
        {
            (RoundStateMachine round, int seat) = NormalDrawSeatOneRound("round-takeover-paid");
            int turn = round.Events.Count(evt => evt.Type == RoundEventType.TileDrawn || evt.Type == RoundEventType.RinshanDrawn);
            IReadOnlyList<TileId> hand = round.Tiles.TilesInZone(TileZone.Hand, seat);
            TileId drawn = round.Tiles.DrawnTile!.Value.Tile;
            TileId[] eligibleHand = hand.Where(tile => tile != drawn).ToArray();
            var beamOffer = new BeamSwapOffer(
                "beam-takeover-offer",
                seat,
                BeamSwapWindow.Wall,
                new[] { new BeamSwapCandidate(round.Tiles.TileAt(TilePosition.Wall(round.Tiles.WallPointer)), BeamSwapSlot.Wall(round.Tiles.WallPointer)) },
                eligibleHand,
                drawn,
                turn);
            CheatState cheat = V32Fixtures.WithRegisteredRound(V32Fixtures.Cheat(InitialPoints), round.RoundId)
                .WithBeamSwap(BeamSwapState.Restore(beamOffer, turn, 1));
            await using ActorHarness harness = CreateHarness(
                P31Fixtures.Controllers(SeatControllerKind.Takeover), cheat, round);

            TableAiCheatCaptureResult capture = await harness.Actor.CaptureAiCheatOpportunityAsync(
                seat, false, TestContext.Current.CancellationToken);
            Assert.True(capture.IsCaptured, capture.ReasonCode + ": " + capture.Detail);
            Assert.True(capture.Opportunity!.View.IsOfferChoice);
            TableAiCheatPlan keepPlan = capture.Opportunity.PlanFor("beam-keep")!;
            Assert.True(keepPlan.Option.IsSafeDefault);

            TableOperationResult resolved = await harness.Actor.SubmitAiCheatAsync(
                keepPlan, false, TestContext.Current.CancellationToken);
            Assert.True(resolved.Status == TableOperationStatus.Accepted, resolved.ReasonCode + ": " + resolved.Detail);
            Assert.False(harness.Actor.HasActiveCheatOffer(seat));
            Assert.Equal(InitialPoints, CheatBalance(harness.Actor, seat));
            Assert.Equal(136, TilePositionMap.Validate(harness.Actor.GetRoundStateForHost().Tiles).AssignedCount);
        }

        [Fact]
        public async Task TileMarkCandidateCombinationsAreSortedAndShareTheSixtyFourSlotLimit()
        {
            // Normal-draw seat: sleeve-activate, beam-activate, then 62 lexicographical tile-mark options (= 64).
            (RoundStateMachine round, int seat) = NormalDrawSeatOneRound("round-mark-limit");
            await using ActorHarness harness = CreateHarness(
                P31Fixtures.Controllers(SeatControllerKind.Computer), initialRound: round);
            TableAiCheatCaptureResult captured = await harness.Actor.CaptureAiCheatOpportunityAsync(
                seat, false, TestContext.Current.CancellationToken);
            Assert.True(captured.IsCaptured, captured.ReasonCode + ": " + captured.Detail);
            AiCheatOption[] options = captured.Opportunity!.View.Options.ToArray();
            Assert.Equal(64, options.Length);
            Assert.Equal("sleeve-activate", options[0].OptionId);
            Assert.Equal("beam-activate", options[1].OptionId);
            AiCheatOption[] marks = options.Where(option => option.CapabilityId == "tile_mark").ToArray();
            Assert.Equal(62, marks.Length);
            Assert.Equal(marks.Length, marks.Select(option => option.OptionId).Distinct(StringComparer.Ordinal).Count());

            TileId[] firstGroup = harness.Actor.GetRoundStateForHost().Tiles.TilesInZone(TileZone.Hand, seat)
                .OrderBy(tile => tile.Value).Take(4).ToArray();
            Assert.Equal("tile-mark-" + string.Join("-", firstGroup.Select(tile => tile.Value)), marks[0].OptionId);
            Assert.Equal(firstGroup, marks[0].TileIds);

            // DealExtra initial dealer turn: sleeve/beam cannot activate, so all 64 slots belong to tile_mark.
            await using ActorHarness dealtHarness = CreateHarness(P31Fixtures.Controllers(SeatControllerKind.Computer));
            TableAiCheatCaptureResult dealtCap = await dealtHarness.Actor.CaptureAiCheatOpportunityAsync(
                0, false, TestContext.Current.CancellationToken);
            Assert.True(dealtCap.IsCaptured);
            AiCheatOption[] dealtOpts = dealtCap.Opportunity!.View.Options.ToArray();
            Assert.Equal(64, dealtOpts.Length);
            Assert.All(dealtOpts, o => Assert.Equal("tile_mark", o.CapabilityId));
        }

        [Fact]
        public async Task SameVersionDuplicateAiRequestHitsBindingAsCheatReplayedAndDetectsPayloadConflict()
        {
            (RoundStateMachine round, int seat) = NormalDrawSeatOneRound("round-dup-ai");
            await using ActorHarness harness = CreateHarness(
                P31Fixtures.Controllers(SeatControllerKind.Computer), initialRound: round);
            TableAiCheatCaptureResult capture = await harness.Actor.CaptureAiCheatOpportunityAsync(
                seat, false, TestContext.Current.CancellationToken);
            Assert.True(capture.IsCaptured);
            TableAiCheatPlan plan = capture.Opportunity!.PlanFor("sleeve-activate")!;

            // Inject a same-version prior binding as if the current version's request was already bound,
            // proving ValidateAuthorityIdentity -> _cheatRequestBindings returns CheatReplayed / Conflict.
            var bindingsField = typeof(TableActor).GetField(
                "_cheatRequestBindings",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            object bindings = bindingsField.GetValue(harness.Actor)!;
            Type bindingType = typeof(TableActor).GetNestedType(
                "CheatRequestBinding",
                System.Reflection.BindingFlags.NonPublic)!;
            object boundEntry = Activator.CreateInstance(
                bindingType,
                plan.Envelope.ScopeId!.Value,
                plan.Envelope.Api!,
                plan.Envelope.PayloadSha256!.Value,
                TableOperationResult.Rejected(new StateVersion(0), TableRuntimeReasonCode.CheatReplayed, "prior"))!;
            bindings.GetType().GetMethod("set_Item")!.Invoke(bindings, new[] { plan.Envelope.RequestId.Value, boundEntry });

            TableOperationResult replayed = await harness.Actor.SubmitAiCheatAsync(
                plan, false, TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Rejected, replayed.Status);
            Assert.Equal(TableRuntimeReasonCode.CheatReplayed, replayed.ReasonCode);
            Assert.Equal(InitialPoints, CheatBalance(harness.Actor, seat));

            // Same RequestId with a tampered payload digest returns Conflict without charge.
            TableAiCheatPlan conflicting = WithEnvelope(
                plan,
                payloadSha256: TablePayloadSha256.Compute(new byte[] { 9, 9, 9 }));
            TableOperationResult conflict = await harness.Actor.SubmitAiCheatAsync(
                conflicting, false, TestContext.Current.CancellationToken);
            Assert.Equal(TableOperationStatus.Conflict, conflict.Status);
            Assert.Equal(InitialPoints, CheatBalance(harness.Actor, seat));
        }

        private static ActorHarness CreateHarness(
            IReadOnlyDictionary<int, SeatControllerKind> controllers,
            CheatState? initialCheatState = null,
            RoundStateMachine? initialRound = null)
        {
            var clock = new ManualTableClock { Now = 1000L };
            var commitPort = new RecordingCommitPort();
            var sink = new RecordingPublicationSink(commitPort);
            RoundStateMachine round = initialRound ?? P31Fixtures.Dealt("round-1");
            CheatState cheat = V32Fixtures.WithRegisteredRound(
                V32Fixtures.Cheat(InitialPoints),
                round.RoundId);
            var options = new TableActorOptions
            {
                RoomId = "room-1",
                MailboxCapacity = 16,
                ControlReserve = 16,
                DecisionClockConfig = new DecisionClockConfig(1000L, 0L, ReserveScope.Round),
                InitialControllers = controllers,
                CheatEconomy = V32Fixtures.Economy(InitialPoints),
                SleeveBloomConfig = V32Fixtures.SleeveConfig(),
                InitialCheatState = initialCheatState ?? cheat
            };
            var actor = new TableActor(options, round, commitPort, clock, sink);
            return new ActorHarness(actor, clock, commitPort, sink, round);
        }

        private static async Task<TableAiCheatPlan> CaptureTileMarkPlanAsync(TableActor actor)
        {
            TableAiCheatCaptureResult captured = await actor.CaptureAiCheatOpportunityAsync(
                0,
                false,
                TestContext.Current.CancellationToken);
            Assert.True(captured.IsCaptured, captured.ReasonCode + ": " + captured.Detail);
            Assert.Null(captured.Opportunity!.DecisionId);
            Assert.Null(captured.Opportunity.DeadlineMillis);
            TableAiCheatPlan plan = Plan(captured.Opportunity, "tile_mark");
            Assert.Null(plan.Envelope.DecisionId);
            Assert.Null(plan.Envelope.DecisionDeadlineMillis);
            return plan;
        }

        private static TableAiCheatPlan Plan(TableAiCheatOpportunity opportunity, string capabilityId)
        {
            TableAiCheatPlan? plan = opportunity.Plans.FirstOrDefault(
                candidate => string.Equals(candidate.Option.CapabilityId, capabilityId, StringComparison.Ordinal));
            Assert.NotNull(plan);
            return plan!;
        }

        private static TableAiCheatPlan WithEnvelope(
            TableAiCheatPlan source,
            ControlEpoch? controlEpoch = null,
            long? recoveryGeneration = null,
            string? windowId = null,
            string? roundId = null,
            OwnerEpoch? ownerEpoch = null,
            DecisionId? decisionId = null,
            RoundCheatPayload? payload = null,
            TablePayloadSha256? payloadSha256 = null)
        {
            TableCheatCommandEnvelope envelope = source.Envelope;
            RoundCheatCommand command = roundId == null && payload == null
                ? envelope.CheatCommand
                : new RoundCheatCommand(
                    roundId ?? envelope.CheatCommand.RoundId,
                    envelope.CheatCommand.Seat,
                    envelope.CheatCommand.Action,
                    envelope.CheatCommand.RequestId,
                    payload ?? envelope.CheatCommand.Payload);
            var changed = new TableCheatCommandEnvelope(
                command,
                envelope.RequestId,
                envelope.ExpectedStateVersion,
                ownerEpoch ?? envelope.OwnerEpoch,
                controlEpoch ?? envelope.ControlEpoch,
                decisionId ?? envelope.DecisionId,
                recoveryGeneration ?? envelope.RecoveryGeneration,
                envelope.ScopeId,
                envelope.Api,
                payloadSha256 ?? envelope.PayloadSha256,
                envelope.DecisionDeadlineMillis,
                envelope.OfferDeadlineMillis,
                windowId ?? envelope.WindowId);
            return new TableAiCheatPlan(source.Option, changed);
        }

        private static long CheatBalance(TableActor actor, int seat)
        {
            return actor.GetCheatLedgerForHost().Balances[seat];
        }

        private static TableControlCheckpoint ComputerControlCheckpoint()
        {
            var states = new SeatControlState[4];
            var preferred = new SeatControllerKind[4];
            for (int seat = 0; seat < 4; seat++)
            {
                states[seat] = new SeatControlState(
                    seat,
                    ConnectionStatus.Connected,
                    SeatControllerKind.Computer,
                    0L,
                    null,
                    null,
                    null);
                preferred[seat] = SeatControllerKind.LocalHuman;
            }

            return new TableControlCheckpoint(
                new ControlRegistrySnapshot(new DisconnectPolicy(), states, preferred),
                0L);
        }

        private static TableCheckpoint WithCheat(TableCheckpoint source, CheatState cheat)
        {
            byte[] payload = TableCheatLedgerCheckpointCodec.Encode(
                TableCheatLedgerCheckpoint.FromCheatState(cheat));
            return new TableCheckpoint(
                source.RoomId,
                source.StateVersion,
                source.LastEventSeq,
                source.StateFingerprint,
                source.RulesetVersion,
                source.StatePayload,
                source.StatePayloadSha256,
                source.ControlVersion,
                source.ControlPayload,
                source.ControlPayloadSha256,
                source.TimingPayload,
                source.TimingPayloadSha256,
                payload,
                TablePayloadSha256.Compute(payload),
                source.CommittedFactsPayload,
                source.CommittedFactsPayloadSha256,
                source.CreatedAtUtcMs,
                source.BootId,
                source.SchemaVersion,
                source.TimingVersion,
                source.CapturedMonotonicMillis,
                source.CommittedHistoryPayload,
                source.CommittedHistoryPayloadSha256,
                source.CommittedCommandBindingsPayload,
                source.CommittedCommandBindingsPayloadSha256);
        }
    }
}
