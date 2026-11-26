using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Mahjong.TableRuntime;
using Mahjong.TableService;
using Xunit;
using WireCommandType = Mahjong.Contracts.Wire.CommandType;
using WireSleeveBloomChoice = Mahjong.Contracts.SleeveBloomChoice;

namespace Mahjong.TableService.Tests
{
    /// <summary>
    /// D-1-03: preserves FIRST, SECOND, and THIRD through both SleeveBloom choice entrypoints
    /// of TableCommandMapper, keeps invalid enum values rejected at protocol boundary, and lets
    /// the authoritative TableActor reject THIRD on ordinary two-candidate rooms without mutating state.
    /// </summary>
    public sealed class SleeveBloomChoiceMappingTests
    {
        private static TableConnectionIdentity PlayerIdentity(string roomId = "room-sb-map", int seat = 0)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return new TableConnectionIdentity(
                "sess-1",
                roomId,
                TableAdmissionRole.Player,
                seat,
                "acct-" + seat,
                "scope-" + seat,
                now,
                now + 60_000,
                "jti-1");
        }

        private static BusinessPayload BuildChoicePayload(Contracts.CommandKind kind, string offerId, uint rawChoice)
        {
            var writer = new CanonicalPayloadWriter((ushort)kind);
            writer.WriteString(CommandPayloadFields.OfferId, offerId);
            writer.WriteUInt32(CommandPayloadFields.Choice, rawChoice);
            return writer.Build();
        }

        private static Envelope CommandEnvelope(
            string roomId,
            int seat,
            string requestId,
            Contracts.CommandKind kind,
            BusinessPayload payload,
            ulong expectedStateVersion = 1)
        {
            var wireType = (WireCommandType)(int)kind;
            return new Envelope
            {
                MessageType = MessageType.Command,
                SessionId = "sess-1",
                RoomId = roomId,
                SeatId = seat,
                RequestId = requestId,
                BindingEpoch = 1,
                OwnerEpoch = 1,
                ControlEpoch = 0,
                Command = new Command
                {
                    Type = wireType,
                    ExpectedStateVersion = expectedStateVersion,
                    CanonicalPayload = Google.Protobuf.ByteString.CopyFrom(payload.AsSpan().ToArray())
                }
            };
        }

        private static RoundStateMachine NoClaimsRound(string roomId)
        {
            var fixture = new N1aTable { Dealer = 0 };
            fixture.Hand(0, new[]
            {
                new TileId(0), new TileId(1), new TileId(32), new TileId(33),
                new TileId(36), new TileId(37), new TileId(68), new TileId(69),
                new TileId(72), new TileId(73), new TileId(104), new TileId(105),
                new TileId(108)
            });
            fixture.Drawn(0, new TileId(112));
            return fixture.Build(roomId + "-r1");
        }

        private static CheatEconomyConfig DefaultEconomy() =>
            new CheatEconomyConfig(
                true,
                1000,
                new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    [CheatEconomyConfig.TileMarkCostKey] = 5,
                    [CheatEconomyConfig.SleeveBloomCostKey] = 10,
                    [CheatEconomyConfig.BeamSwapCostKey] = 10
                });

        private static SleeveBloomConfig DefaultSleeveConfig() =>
            new SleeveBloomConfig(true, 10000L);

        private static CheatState GetActorCheat(TableActor actor) =>
            (CheatState)typeof(TableActor)
                .GetField("_cheat", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(actor)!;

        private sealed class FixedClock : ITableClock
        {
            public long MonotonicMillis => 1000L;
            public long UtcNowMs => 1_700_000_000_000L;
        }

        [Theory]
        [InlineData(Contracts.CommandKind.SubmitSleeveBloomChoice, WireSleeveBloomChoice.First, SleeveBloomKeep.First)]
        [InlineData(Contracts.CommandKind.SubmitSleeveBloomChoice, WireSleeveBloomChoice.Second, SleeveBloomKeep.Second)]
        [InlineData(Contracts.CommandKind.SubmitSleeveBloomChoice, WireSleeveBloomChoice.Third, SleeveBloomKeep.Third)]
        [InlineData(Contracts.CommandKind.SubmitSleeveBloomOffTurnChoice, WireSleeveBloomChoice.First, SleeveBloomKeep.First)]
        [InlineData(Contracts.CommandKind.SubmitSleeveBloomOffTurnChoice, WireSleeveBloomChoice.Second, SleeveBloomKeep.Second)]
        [InlineData(Contracts.CommandKind.SubmitSleeveBloomOffTurnChoice, WireSleeveBloomChoice.Third, SleeveBloomKeep.Third)]
        public void BothSleeveBloomChoiceEntrypoints_PreserveFirstSecondAndThird(
            Contracts.CommandKind kind,
            WireSleeveBloomChoice wireChoice,
            SleeveBloomKeep expectedKeep)
        {
            var identity = PlayerIdentity();
            var options = new TableServiceOptions();
            BusinessPayload payload = BuildChoicePayload(kind, "offer-42", (uint)wireChoice);
            Envelope envelope = CommandEnvelope("room-sb-map", 0, "req-" + wireChoice, kind, payload);

            TableCommandRoute route = TableCommandMapper.Map(
                identity,
                envelope,
                new OwnerEpoch(1),
                0L,
                options,
                "round-1");

            var cheatRoute = Assert.IsType<TableCommandRoute.CheatCommand>(route);
            Assert.Equal(expectedKeep, cheatRoute.Envelope.CheatCommand.Payload.Keep);
            Assert.Equal("offer-42", cheatRoute.Envelope.CheatCommand.Payload.OfferId);
        }

        [Theory]
        [InlineData(Contracts.CommandKind.SubmitSleeveBloomChoice)]
        [InlineData(Contracts.CommandKind.SubmitSleeveBloomOffTurnChoice)]
        public void UnknownChoiceEnum_RemainsRejectedAtProtocolBoundary_WithUpdatedValidatorMessage(
            Contracts.CommandKind kind)
        {
            BusinessPayload payload = BuildChoicePayload(kind, "offer-42", 99U);
            CommandValidationException ex = Assert.Throws<CommandValidationException>(
                () => CommandPayloadValidator.Validate(kind, payload));
            Assert.Contains("FIRST, SECOND, or THIRD", ex.Message, StringComparison.Ordinal);

            var identity = PlayerIdentity();
            TableCommandRoute route = TableCommandMapper.Map(
                identity,
                CommandEnvelope("room-sb-map", 0, "req-bad", kind, payload),
                new OwnerEpoch(1),
                0L,
                new TableServiceOptions(),
                "round-1");

            var rejection = Assert.IsType<TableCommandRoute.PreActorRejection>(route);
            Assert.Equal("PAYLOAD_INVALID", rejection.DetailCode);
        }

        [Theory]
        [InlineData(false, WireSleeveBloomChoice.First)]
        [InlineData(false, WireSleeveBloomChoice.Second)]
        [InlineData(true, WireSleeveBloomChoice.First)]
        [InlineData(true, WireSleeveBloomChoice.Second)]
        public async Task PlainTwoCandidateRoom_AuthoritativelyRejectsThird_WithoutChangingOfferTilesOrBalance(
            bool offTurn,
            WireSleeveBloomChoice validFollowup)
        {
            string roomId = "room-two-cand-" + (offTurn ? "off" : "imm") + "-" + validFollowup;
            RoundStateMachine initialRound = NoClaimsRound(roomId);
            var options = new TableActorOptions
            {
                RoomId = roomId,
                InitialOwnerEpoch = new OwnerEpoch(1),
                InitialControllers = new Dictionary<int, SeatControllerKind>
                {
                    { 0, SeatControllerKind.RemoteHuman },
                    { 1, SeatControllerKind.RemoteHuman },
                    { 2, SeatControllerKind.RemoteHuman },
                    { 3, SeatControllerKind.RemoteHuman }
                },
                DecisionClockConfig = new DecisionClockConfig(),
                CheatEconomy = DefaultEconomy(),
                SleeveBloomConfig = DefaultSleeveConfig()
            };

            await using var actor = new TableActor(
                options,
                initialRound,
                new TableServiceCommitPort(new OwnerEpoch(1)),
                new FixedClock());

            var mapperOptions = new TableServiceOptions();
            var identity = PlayerIdentity(roomId, seat: 0);
            int turns = offTurn ? 1 : 4;
            ulong expectedVersion = 0;
            for (int i = 0; i < turns; i++)
            {
                int seat = i % 4;
                RoundStateMachine cur = actor.GetRoundStateForHost();
                TileId drawn = cur.Tiles.DrawnTile!.Value.Tile;
                TableOperationResult disc = await actor.SubmitAsync(
                    new TableCommandEnvelope(
                        RoundCommand.Discard(cur.RoundId, seat, drawn),
                        new RequestId("disc-" + i),
                        new StateVersion(expectedVersion++),
                        new OwnerEpoch(1),
                        new ControlEpoch(0)));
                Assert.True(disc.Status == TableOperationStatus.Accepted, disc.ReasonCode + " " + disc.Detail);
            }

            RoundStateMachine beforeAct = actor.GetRoundStateForHost();
            if (offTurn)
            {
                // Seat 0 discard opened discard window id "bloom-window-0-0-0".
                TableOperationResult act = await actor.SubmitCheatAsync(
                    new TableCheatCommandEnvelope(
                        new RoundCheatCommand(
                            beforeAct.RoundId,
                            0,
                            RoundCheatAction.SubmitSleeveBloomOffTurn,
                            "off-act",
                            RoundCheatPayload.OffTurn("bloom-window-0-0-0")),
                        new RequestId("off-act"),
                        new StateVersion(expectedVersion++),
                        new OwnerEpoch(1),
                        new ControlEpoch(0),
                        decisionId: null,
                        recoveryGeneration: 0L,
                        scopeId: new RequestScopeId("scope-0"),
                        api: "SUBMIT_SLEEVE_BLOOM_OFF_TURN",
                        payloadSha256: TablePayloadSha256.Compute(System.Text.Encoding.UTF8.GetBytes("off-act"))));
                Assert.True(act.Status == TableOperationStatus.Accepted, act.ReasonCode + " " + act.Detail);
            }
            else
            {
                TableTimingSnapshot timing = await actor.GetTimingSnapshotAsync(CancellationToken.None);
                DecisionId? immDecisionId = timing.Seats[0].Timing is { } t
                    ? new DecisionId(t.Key.DecisionId)
                    : (DecisionId?)null;
                TableOperationResult act = await actor.SubmitCheatAsync(
                    new TableCheatCommandEnvelope(
                        new RoundCheatCommand(
                            beforeAct.RoundId,
                            0,
                            RoundCheatAction.SubmitSleeveBloom,
                            "imm-act",
                            RoundCheatPayload.SleeveBloom()),
                        new RequestId("imm-act"),
                        new StateVersion(expectedVersion++),
                        new OwnerEpoch(1),
                        new ControlEpoch(0),
                        decisionId: immDecisionId,
                        recoveryGeneration: 0L,
                        scopeId: new RequestScopeId("scope-0"),
                        api: "SUBMIT_SLEEVE_BLOOM",
                        payloadSha256: TablePayloadSha256.Compute(System.Text.Encoding.UTF8.GetBytes("imm-act"))));
                Assert.True(act.Status == TableOperationStatus.Accepted, act.ReasonCode + " " + act.Detail);
            }

            CheatState cheatBefore = GetActorCheat(actor);
            SleeveBloomOffer offerBefore = (offTurn
                ? cheatBefore.SleeveBloom.OffTurnOfferFor(0)
                : cheatBefore.SleeveBloom.ImmediateOffer)!;
            Assert.NotNull(offerBefore);
            Assert.False(offerBefore.ThirdTile.HasValue);
            int balanceBefore = cheatBefore.Ledger.BalanceOf(0);
            ulong fingerprintBefore = actor.GetRoundStateForHost().SourceFingerprint;

            Contracts.CommandKind choiceKind = offTurn
                ? Contracts.CommandKind.SubmitSleeveBloomOffTurnChoice
                : Contracts.CommandKind.SubmitSleeveBloomChoice;

            // 1. Third maps cleanly via TableCommandMapper.Map (preserving Third) and is rejected by TableActor.
            Envelope thirdWire = CommandEnvelope(
                roomId,
                0,
                "try-third",
                choiceKind,
                BuildChoicePayload(choiceKind, offerBefore.OfferId, (uint)WireSleeveBloomChoice.Third),
                expectedStateVersion: expectedVersion);
            TableCommandRoute thirdRoute = TableCommandMapper.Map(
                identity, thirdWire, new OwnerEpoch(1), 0L, mapperOptions, beforeAct.RoundId);
            var thirdCheatRoute = Assert.IsType<TableCommandRoute.CheatCommand>(thirdRoute);
            Assert.Equal(SleeveBloomKeep.Third, thirdCheatRoute.Envelope.CheatCommand.Payload.Keep);

            TableOperationResult thirdResult = await actor.SubmitCheatAsync(thirdCheatRoute.Envelope);
            Assert.Equal(TableOperationStatus.Rejected, thirdResult.Status);

            CheatState cheatAfterReject = GetActorCheat(actor);
            SleeveBloomOffer offerAfterReject = (offTurn
                ? cheatAfterReject.SleeveBloom.OffTurnOfferFor(0)
                : cheatAfterReject.SleeveBloom.ImmediateOffer)!;
            Assert.Equal(offerBefore.OfferId, offerAfterReject.OfferId);
            Assert.Equal(balanceBefore, cheatAfterReject.Ledger.BalanceOf(0));
            Assert.Equal(fingerprintBefore, actor.GetRoundStateForHost().SourceFingerprint);

            // 2. Existing First / Second still map and apply cleanly on the same room/offer.
            Envelope validWire = CommandEnvelope(
                roomId,
                0,
                "try-valid",
                choiceKind,
                BuildChoicePayload(choiceKind, offerBefore.OfferId, (uint)validFollowup),
                expectedStateVersion: expectedVersion);
            TableCommandRoute validRoute = TableCommandMapper.Map(
                identity, validWire, new OwnerEpoch(1), 0L, mapperOptions, beforeAct.RoundId);
            var validCheatRoute = Assert.IsType<TableCommandRoute.CheatCommand>(validRoute);
            TableOperationResult validResult = await actor.SubmitCheatAsync(validCheatRoute.Envelope);
            Assert.True(validResult.Status == TableOperationStatus.Accepted, validResult.ReasonCode + " " + validResult.Detail);
        }
    }
}
