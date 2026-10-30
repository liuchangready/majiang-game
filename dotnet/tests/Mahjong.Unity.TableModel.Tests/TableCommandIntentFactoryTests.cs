using System;
using System.Collections.Generic;
using System.Linq;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.Unity.Table;
using Xunit;

namespace Mahjong.Unity.TableModel.Tests
{
    public sealed class TableCommandIntentFactoryTests
    {
        private static readonly RequestId Request = new RequestId("request-p43");
        private static readonly RequestScopeId Scope = new RequestScopeId(P43TestData.ScopeId);

        private static TableCommandOwnerContext Owner()
        {
            return new TableCommandOwnerContext(Request, Scope);
        }

        private static PlayerTableViewState View(ManualTableClock clock, PlayerSnapshot? snapshot = null)
        {
            return new PlayerTableProjectionMapper(clock).Project(P43TestData.View(snapshot ?? P43TestData.PlayerSnapshot()));
        }

        private static TestPayload Payload(TableCommandIntent intent)
        {
            return new TestPayload(intent);
        }

        [Fact]
        public void ActionPayloadPreservesEverySnapshotFieldAndOwnerIdentity()
        {
            var clock = new ManualTableClock();
            PlayerTableViewState view = View(clock);
            var factory = new TableCommandIntentFactory(clock);

            TableCommandIntent intent = factory.CreateSubmitAction(view, view.Own!.Decision!.Candidates[0], Owner());
            TestPayload document = Payload(intent);

            Assert.Equal(CommandKind.SubmitAction, intent.Kind);
            Assert.Equal(P43TestData.RoundId, document.RequireString(CommandPayloadFields.RoundIdField, "roundId"));
            Assert.Equal("decision-p43", document.RequireString(CommandPayloadFields.DecisionIdField, "decisionId"));
            Assert.Equal(7UL, document.RequireUInt64(CommandPayloadFields.ControlEpochField, "controlEpoch"));
            Assert.Equal((uint)ProtocolActionType.DiscardTile, document.RequireUInt32(CommandPayloadFields.ActionType, "actionType"));
            Assert.Equal("discard-13", document.RequireString(CommandPayloadFields.OptionId, "optionId"));
            Assert.Equal(new uint[] { 13 }, document.RequireRepeatedUInt32(CommandPayloadFields.TileIds, "tileIds"));
            Assert.False(document.HasField(CommandPayloadFields.MeldType));

            Assert.Equal(Request, intent.Command.RequestId);
            Assert.Equal(new RequestScopeId(P43TestData.ScopeId), intent.Command.ScopeId!.Value);
            Assert.Equal(new StateVersion(5), intent.Command.ExpectedStateVersion);
            Assert.Equal(new RoundId(P43TestData.RoundId), intent.Command.RoundId!.Value);
            Assert.Equal(new DecisionId("decision-p43"), intent.Command.DecisionId!.Value);
            Assert.Equal(new ControlEpoch(7), intent.Command.ControlEpoch!.Value);
            Assert.Equal(new OwnerEpoch(3), intent.Command.OwnerEpoch!.Value);
        }

        [Fact]
        public void SleeveChoiceUsesOfferIdAndNeverFabricatesRoundId()
        {
            var clock = new ManualTableClock();
            PlayerTableViewState view = View(clock);
            var factory = new TableCommandIntentFactory(clock);

            TableCommandIntent intent = factory.CreateSubmitSleeveBloomChoice(
                view,
                false,
                SleeveBloomChoice.Second,
                Owner());
            TestPayload document = Payload(intent);

            Assert.Equal(CommandKind.SubmitSleeveBloomChoice, intent.Kind);
            Assert.Equal("offer-1", document.RequireString(CommandPayloadFields.OfferId, "offerId"));
            Assert.Equal((uint)SleeveBloomChoice.Second, document.RequireUInt32(CommandPayloadFields.Choice, "choice"));
            Assert.False(intent.Command.RoundId.HasValue);
            Assert.Equal("offer-1", intent.OfferId);
        }

        [Fact]
        public void OffTurnSleeveChoiceUsesItsOwnOfferAndWindow()
        {
            var clock = new ManualTableClock();
            PlayerTableViewState view = View(clock);
            var factory = new TableCommandIntentFactory(clock);

            TableCommandIntent intent = factory.CreateSubmitSleeveBloomChoice(
                view,
                true,
                SleeveBloomChoice.First,
                Owner());
            TestPayload document = Payload(intent);

            Assert.Equal(CommandKind.SubmitSleeveBloomOffTurnChoice, intent.Kind);
            Assert.Equal("offer-off", document.RequireString(CommandPayloadFields.OfferId, "offerId"));
            Assert.Equal((uint)SleeveBloomChoice.First, document.RequireUInt32(CommandPayloadFields.Choice, "choice"));
            Assert.False(intent.Command.RoundId.HasValue);
            Assert.Equal("window-1", intent.WindowId);
        }

        [Fact]
        public void BeamSwapChoiceCarriesOfferHandAndSlot()
        {
            var clock = new ManualTableClock();
            PlayerTableViewState view = View(clock);
            var factory = new TableCommandIntentFactory(clock);

            TableCommandIntent intent = factory.CreateSubmitBeamSwapChoice(
                view,
                BeamSwapChoice.Swap,
                1,
                view.Own!.Cheat!.BeamSwap!.Candidates[1],
                Owner());
            TestPayload document = Payload(intent);

            Assert.Equal(CommandKind.SubmitBeamSwapChoice, intent.Kind);
            Assert.Equal("beam-1", document.RequireString(CommandPayloadFields.OfferId, "offerId"));
            Assert.Equal((uint)BeamSwapChoice.Swap, document.RequireUInt32(CommandPayloadFields.Choice, "choice"));
            Assert.Equal(1U, document.RequireUInt32(CommandPayloadFields.HandTileId, "handTileId"));
            Assert.Equal(4U, document.RequireUInt32(CommandPayloadFields.TargetSlot, "targetSlot"));
            Assert.False(intent.Command.RoundId.HasValue);
        }

        [Fact]
        public void BeamSwapKeepOriginalOmitsHandAndSlot()
        {
            var clock = new ManualTableClock();
            PlayerTableViewState view = View(clock);
            var factory = new TableCommandIntentFactory(clock);

            TableCommandIntent intent = factory.CreateSubmitBeamSwapChoice(
                view,
                BeamSwapChoice.KeepOriginal,
                null,
                null,
                Owner());
            TestPayload document = Payload(intent);

            Assert.Equal((uint)BeamSwapChoice.KeepOriginal, document.RequireUInt32(CommandPayloadFields.Choice, "choice"));
            Assert.False(document.HasField(CommandPayloadFields.HandTileId));
            Assert.False(document.HasField(CommandPayloadFields.TargetSlot));
        }

        [Fact]
        public void StaleBeamCandidateIsRejected()
        {
            var clock = new ManualTableClock();
            PlayerTableViewState oldView = View(clock);
            PlayerTableProjectionMapper mapper = new PlayerTableProjectionMapper(clock);
            PlayerSnapshot next = P43TestData.PlayerSnapshot(stateVersion: 6, eventSeq: 106);
            next.PrivateView.Cheat.BeamSwap.Candidates[1].TileId = 21;
            next.PrivateView.Cheat.BeamSwap.Candidates[1].Slot = 9;
            next.PrivateView.Cheat.BeamSwap.Candidates[1].SlotIndex = 9;
            PlayerTableViewState newView = mapper.Project(P43TestData.View(next));
            var factory = new TableCommandIntentFactory(clock);

            TableIntentUnavailableException error = Assert.Throws<TableIntentUnavailableException>(() =>
                factory.CreateSubmitBeamSwapChoice(
                    newView,
                    BeamSwapChoice.Swap,
                    1,
                    oldView.Own!.Cheat!.BeamSwap!.Candidates[1],
                    Owner()));
            Assert.Equal("STALE_SLOT", error.Code);
        }

        [Fact]
        public void TileMarkRequiresExactlyFourSelectableDistinctTiles()
        {
            var clock = new ManualTableClock();
            PlayerTableViewState view = View(clock);
            var factory = new TableCommandIntentFactory(clock);

            TableCommandIntent intent = factory.CreateSubmitTileMark(view, new uint[] { 4, 1, 3, 2 }, Owner());
            TestPayload document = Payload(intent);
            Assert.Equal(new uint[] { 1, 2, 3, 4 }, document.RequireRepeatedUInt32(CommandPayloadFields.TileMarkTileIds, "tileIds"));

            Assert.Equal(
                "TILE_MARK_REQUIRES_FOUR",
                Assert.Throws<TableIntentUnavailableException>(() =>
                    factory.CreateSubmitTileMark(view, new uint[] { 1, 2, 3 }, Owner())).Code);
            Assert.Equal(
                "TILE_MARK_DUPLICATE",
                Assert.Throws<TableIntentUnavailableException>(() =>
                    factory.CreateSubmitTileMark(view, new uint[] { 1, 2, 3, 3 }, Owner())).Code);
            Assert.Equal(
                "TILE_MARK_NOT_SELECTABLE",
                Assert.Throws<TableIntentUnavailableException>(() =>
                    factory.CreateSubmitTileMark(view, new uint[] { 1, 2, 3, 99 }, Owner())).Code);
        }

        [Fact]
        public void ShufflePlanUsesConfirmedTargetAndUserSeeThroughOnly()
        {
            var clock = new ManualTableClock();
            PlayerTableViewState view = View(clock);
            var factory = new TableCommandIntentFactory(clock);

            TableCommandIntent intent = factory.CreateSubmitShufflePlan(view, true, Owner());
            TestPayload document = Payload(intent);

            Assert.Equal(CommandKind.SubmitShufflePlan, intent.Kind);
            Assert.Equal(P43TestData.RoundId, document.RequireString(CommandPayloadFields.RoundIdField, "roundId"));
            Assert.Equal(2U, document.RequireUInt32(CommandPayloadFields.ShuffleTarget, "target"));
            Assert.True(document.RequireBool(CommandPayloadFields.SeeThrough, "seeThrough"));
            Assert.False(intent.Command.DecisionId.HasValue);
        }

        [Fact]
        public void ShufflePlanRejectsDisabledSeeThrough()
        {
            var clock = new ManualTableClock();
            PlayerSnapshot snapshot = P43TestData.PlayerSnapshot();
            snapshot.PrivateView.Cheat.Preparation.SeeThroughEnabled = false;
            PlayerTableViewState view = View(clock, snapshot);
            var factory = new TableCommandIntentFactory(clock);

            Assert.Equal(
                "SEE_THROUGH_DISABLED",
                Assert.Throws<TableIntentUnavailableException>(() =>
                    factory.CreateSubmitShufflePlan(view, true, Owner())).Code);
        }

        [Fact]
        public void OffTurnActivationCarriesWindowRoundDecisionAndControl()
        {
            var clock = new ManualTableClock();
            PlayerSnapshot snapshot = P43TestData.PlayerSnapshot();
            snapshot.PrivateView.Cheat.OffTurnSleeveBloom = null;
            snapshot.PrivateView.Cheat.SleeveBloomReservation = null;
            PlayerTableViewState view = View(clock, snapshot);
            var factory = new TableCommandIntentFactory(clock);

            TableCommandIntent intent = factory.CreateSubmitSleeveBloomOffTurn(view, Owner());
            TestPayload document = Payload(intent);

            Assert.Equal(CommandKind.SubmitSleeveBloomOffTurn, intent.Kind);
            Assert.Equal(P43TestData.RoundId, document.RequireString(CommandPayloadFields.RoundIdField, "roundId"));
            Assert.Equal("decision-p43", document.RequireString(CommandPayloadFields.DecisionIdField, "decisionId"));
            Assert.Equal(7UL, document.RequireUInt64(CommandPayloadFields.ControlEpochField, "controlEpoch"));
            Assert.Equal("window-1", document.RequireString(CommandPayloadFields.WindowId, "windowId"));
        }

        [Fact]
        public void ActivationIsRejectedWhenItsOfferIsAlreadyOpen()
        {
            var clock = new ManualTableClock();
            PlayerTableViewState view = View(clock);
            var factory = new TableCommandIntentFactory(clock);

            Assert.Equal(
                "OFFER_ALREADY_OPEN",
                Assert.Throws<TableIntentUnavailableException>(() =>
                    factory.CreateSubmitSleeveBloom(view, Owner())).Code);
            Assert.Equal(
                "OFFER_ALREADY_OPEN",
                Assert.Throws<TableIntentUnavailableException>(() =>
                    factory.CreateSubmitBeamSwap(view, Owner())).Code);
        }

        [Fact]
        public void NextRoundRequiresConfirmedRoundEndAndPreservesRoundId()
        {
            var clock = new ManualTableClock();
            PlayerSnapshot snapshot = P43TestData.PlayerSnapshot();
            PlayerTableViewState playing = View(clock, snapshot);
            var factory = new TableCommandIntentFactory(clock);

            Assert.Equal(
                "NEXT_ROUND_NOT_AVAILABLE",
                Assert.Throws<TableIntentUnavailableException>(() =>
                    factory.CreateNextRound(playing, Owner())).Code);

            snapshot.PublicView.Board.Phase = Mahjong.Contracts.Wire.GamePhase.RoundEnd;
            PlayerTableViewState ended = View(clock, snapshot);
            TableCommandIntent intent = factory.CreateNextRound(ended, Owner());
            TestPayload document = Payload(intent);

            Assert.Equal(CommandKind.NextRound, intent.Kind);
            Assert.Equal(P43TestData.RoundId, document.RequireString(CommandPayloadFields.RoundIdField, "roundId"));
            Assert.False(document.HasField(CommandPayloadFields.DecisionIdField));
        }

        [Fact]
        public void ReleaseControlUsesConfirmedEpochAndDedicatedV2PayloadTags()
        {
            var clock = new ManualTableClock();
            PlayerTableViewState view = View(clock);
            var factory = new TableCommandIntentFactory(clock);

            TableCommandIntent release = factory.CreateReleaseControl(view, Owner());
            TestPayload document = Payload(release);

            Assert.Equal(CommandKind.ReleaseControl, release.Kind);
            Assert.Equal(CommandKind.ReleaseControl, release.Command.Kind);
            Assert.Equal(P43TestData.RoundId, release.RoundId);
            Assert.Equal(7UL, release.ControlEpoch);
            Assert.Equal(7UL, release.Command.ControlEpoch!.Value.Value);
            Assert.Equal(new RoundId(P43TestData.RoundId), release.Command.RoundId!.Value);
            Assert.Equal(P43TestData.RoundId, document.RequireString(CommandPayloadFields.ControlRoundId, "roundId"));
            Assert.Equal(7UL, document.RequireUInt64(CommandPayloadFields.ExpectedControlEpochV2, "expectedControlEpoch"));
            Assert.False(document.HasField(CommandPayloadFields.ExpectedControlEpoch));
            Assert.False(document.HasField(CommandPayloadFields.RoundIdField));
        }

        [Fact]
        public void ClaimControlUsesConfirmedEpochAndRejectsWrongTransition()
        {
            var clock = new ManualTableClock();
            var factory = new TableCommandIntentFactory(clock);
            PlayerSnapshot automated = P43TestData.PlayerSnapshot(controlledByComputer: true, controlEpoch: 11, controlVersion: 4);
            PlayerTableViewState automatedView = View(clock, automated);

            TableCommandIntent claim = factory.CreateClaimControl(automatedView, Owner());
            TestPayload document = Payload(claim);

            Assert.Equal(CommandKind.ClaimControl, claim.Kind);
            Assert.Equal(11UL, claim.ControlEpoch);
            Assert.Equal(11UL, claim.Command.ControlEpoch!.Value.Value);
            Assert.Equal(11UL, document.RequireUInt64(CommandPayloadFields.ExpectedControlEpochV2, "expectedControlEpoch"));
            Assert.Equal(P43TestData.RoundId, document.RequireString(CommandPayloadFields.ControlRoundId, "roundId"));

            PlayerTableViewState human = View(clock);
            Assert.Equal(
                TableCommandIntentFactory.ControlAlreadyHuman,
                Assert.Throws<TableIntentUnavailableException>(() => factory.CreateClaimControl(human, Owner())).Code);
            Assert.Equal(
                TableCommandIntentFactory.ControlAlreadyAutomated,
                Assert.Throws<TableIntentUnavailableException>(() => factory.CreateReleaseControl(automatedView, Owner())).Code);
        }

        [Fact]
        public void ControlIntentWithoutDecisionStillUsesAuthoritativeControlState()
        {
            var clock = new ManualTableClock();
            PlayerSnapshot noDecision = P43TestData.PlayerSnapshot(
                includeDecision: false,
                controlledByComputer: true,
                controlEpoch: 4,
                controlVersion: 2);
            PlayerTableViewState view = View(clock, noDecision);
            var factory = new TableCommandIntentFactory(clock);

            TableCommandIntent claim = factory.CreateClaimControl(view, Owner());
            Assert.Equal(CommandKind.ClaimControl, claim.Kind);
            Assert.Equal(4UL, claim.ControlEpoch);
            Assert.Null(claim.DecisionId);
        }

        [Fact]
        public void MissingControlStateFailsClosedInsteadOfGuessingEpochZero()
        {
            var clock = new ManualTableClock();
            PlayerSnapshot noControl = P43TestData.PlayerSnapshot(includeControl: false);
            PlayerTableViewState view = View(clock, noControl);
            var factory = new TableCommandIntentFactory(clock);

            TableIntentUnavailableException claim = Assert.Throws<TableIntentUnavailableException>(() =>
                factory.CreateClaimControl(view, Owner()));
            TableIntentUnavailableException release = Assert.Throws<TableIntentUnavailableException>(() =>
                factory.CreateReleaseControl(view, Owner()));
            Assert.Equal(TableCommandIntentFactory.ControlStateUnavailable, claim.Code);
            Assert.Equal(TableCommandIntentFactory.ControlStateUnavailable, release.Code);
        }

        [Fact]
        public void SpectatorCannotBuildControlCommands()
        {
            var clock = new ManualTableClock();
            PlayerSnapshot spectator = new PlayerSnapshot
            {
                SessionId = P43TestData.SessionId,
                RoundId = P43TestData.RoundId,
                StateVersion = 5,
                PublicView = P43TestData.PublicMatch()
            };
            PlayerTableViewState view = new PlayerTableProjectionMapper(clock).Project(
                P43TestData.View(spectator, seat: -1, role: ProtocolRole.Spectator));
            var factory = new TableCommandIntentFactory(clock);

            Assert.Equal(
                "NO_PRIVATE_VIEW",
                Assert.Throws<TableIntentUnavailableException>(() =>
                    factory.CreateClaimControl(view, Owner())).Code);
        }

        [Fact]
        public void StaleActionAndControlEpochAreRejected()
        {
            var clock = new ManualTableClock();
            PlayerTableProjectionMapper mapper = new PlayerTableProjectionMapper(clock);
            PlayerTableViewState oldView = mapper.Project(P43TestData.View(P43TestData.PlayerSnapshot()));
            PlayerLegalActionState oldAction = oldView.Own!.Decision!.Candidates[0];

            PlayerSnapshot next = P43TestData.PlayerSnapshot(stateVersion: 6, eventSeq: 106);
            next.PrivateView.Decision.DecisionId = "decision-next";
            next.PrivateView.Decision.ControlEpoch = 8;
            PlayerTableViewState newView = mapper.Project(P43TestData.View(next));
            var factory = new TableCommandIntentFactory(clock);

            TableIntentUnavailableException wrongDecision = Assert.Throws<TableIntentUnavailableException>(() =>
                factory.CreateSubmitAction(newView, oldAction, Owner()));
            Assert.Equal("STALE_DECISION", wrongDecision.Code);

            PlayerLegalActionState newAction = newView.Own!.Decision!.Candidates[0];
            next.PrivateView.Decision.DecisionId = "decision-p43";
            next.PrivateView.Decision.ControlEpoch = 9;
            PlayerTableViewState newer = mapper.Project(P43TestData.View(next));
            Assert.Equal(
                "STALE_DECISION",
                Assert.Throws<TableIntentUnavailableException>(() =>
                    factory.CreateSubmitAction(newer, newAction, Owner())).Code);
        }

        [Fact]
        public void StaleRoundIsRejected()
        {
            var clock = new ManualTableClock();
            PlayerTableProjectionMapper mapper = new PlayerTableProjectionMapper(clock);
            PlayerTableViewState oldView = mapper.Project(P43TestData.View(P43TestData.PlayerSnapshot()));
            PlayerLegalActionState oldAction = oldView.Own!.Decision!.Candidates[0];

            PlayerSnapshot next = P43TestData.PlayerSnapshot(stateVersion: 6, eventSeq: 106, roundId: "round-next");
            PlayerTableViewState newView = mapper.Project(P43TestData.View(next));
            var factory = new TableCommandIntentFactory(clock);

            Assert.Equal(
                "STALE_DECISION",
                Assert.Throws<TableIntentUnavailableException>(() =>
                    factory.CreateSubmitAction(newView, oldAction, Owner())).Code);
        }

        [Fact]
        public void ExpiredDecisionAndOfferAreRejected()
        {
            var clock = new ManualTableClock();
            PlayerTableViewState view = View(clock);
            var factory = new TableCommandIntentFactory(clock);
            PlayerLegalActionState action = view.Own!.Decision!.Candidates[0];

            clock.Advance(8001);
            Assert.Equal(
                "DECISION_EXPIRED",
                Assert.Throws<TableIntentUnavailableException>(() =>
                    factory.CreateSubmitAction(view, action, Owner())).Code);
            Assert.Equal(
                "OFFER_EXPIRED",
                Assert.Throws<TableIntentUnavailableException>(() =>
                    factory.CreateSubmitSleeveBloomChoice(view, false, SleeveBloomChoice.First, Owner())).Code);
            Assert.Equal(
                "PREPARATION_EXPIRED",
                Assert.Throws<TableIntentUnavailableException>(() =>
                    factory.CreateSubmitShufflePlan(view, false, Owner())).Code);
        }

        [Fact]
        public void ScopeMismatchAndSpectatorCannotBuildCommands()
        {
            var clock = new ManualTableClock();
            PlayerTableViewState view = View(clock);
            var factory = new TableCommandIntentFactory(clock);
            var wrongScope = new TableCommandOwnerContext(Request, new RequestScopeId("other-scope"));

            Assert.Equal(
                "SCOPE_MISMATCH",
                Assert.Throws<TableIntentUnavailableException>(() =>
                    factory.CreateSubmitAction(view, view.Own!.Decision!.Candidates[0], wrongScope)).Code);

            PlayerTableViewState spectator = new PlayerTableProjectionMapper(clock).Project(
                P43TestData.View(null, P43TestData.PublicSnapshot(), seat: -1, role: ProtocolRole.Spectator));
            Assert.Equal(
                "NO_PRIVATE_VIEW",
                Assert.Throws<TableIntentUnavailableException>(() =>
                    factory.CreateNextRound(spectator, Owner())).Code);
        }

        [Fact]
        public void KnownContractDefect_OfficialCanonicalStringReaderKeepsLengthPrefix()
        {
            var clock = new ManualTableClock();
            PlayerTableViewState view = View(clock);
            var factory = new TableCommandIntentFactory(clock);
            TableCommandIntent intent = factory.CreateSubmitAction(view, view.Own!.Decision!.Candidates[0], Owner());

            // The production writer emits a four-byte little-endian string
            // length prefix. Contracts 1's CanonicalPayloadDocument.AsString
            // returns that prefix together with the text. The assertion keeps
            // the defect visible; it does not weaken or alter the emitted bytes.
            CanonicalPayloadDocument official = CanonicalPayloadParser.Parse(intent.Command.Payload);
            string value = official.RequireString(CommandPayloadFields.RoundIdField, "roundId");
            Assert.NotEqual(P43TestData.RoundId, value);
            Assert.EndsWith(P43TestData.RoundId, value, StringComparison.Ordinal);
            Assert.Equal(new byte[] { 9, 0, 0, 0 }, value.Substring(0, 4).Select(c => (byte)c).ToArray());
        }

        [Fact]
        public void SameRequestAndIntentRetainOriginalCanonicalBytes()
        {
            var clock = new ManualTableClock();
            PlayerTableViewState view = View(clock);
            var factory = new TableCommandIntentFactory(clock);

            TableCommandIntent first = factory.CreateSubmitAction(view, view.Own!.Decision!.Candidates[0], Owner());
            TableCommandIntent second = factory.CreateSubmitAction(view, view.Own.Decision.Candidates[0], Owner());

            Assert.Equal(first.Command.RequestId, second.Command.RequestId);
            Assert.Equal(first.Command.Payload.Digest, second.Command.Payload.Digest);
            Assert.Equal(first.Command.Payload.CopyBytes(), second.Command.Payload.CopyBytes());
            Assert.Equal(first.Key, second.Key);
        }
    }
}
