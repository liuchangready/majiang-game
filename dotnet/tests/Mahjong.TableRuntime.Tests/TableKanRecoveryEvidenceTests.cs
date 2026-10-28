using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Xunit;

namespace Mahjong.TableRuntime.Tests
{
    /// <summary>
    /// D-TR-KAN-02: recovery must validate real kan / replacement-draw wall evidence instead of
    /// rejecting every history containing KanDeclared/RinshanDrawn. The model is the Domain's
    /// physical one: both normal draws and kan replacement draws remove one live-wall tile; the
    /// declaration itself removes none. Every malformed/tampered field still fails closed.
    /// </summary>
    public sealed class TableKanRecoveryEvidenceTests
    {
        private static TileId Man(int rank, int copy) => TileId.FromTypeIndex(rank - 1, copy);
        private static TileId Dragon(int rank, int copy) => TileId.FromTypeIndex(30 + rank, copy);

        [Fact]
        public void OpenKanReplacementAndFollowingNormalDrawValidate()
        {
            Script script = OpenKanScript();
            Evidence evidence = BuildEvidence(script);
            TableVisibilityProjection.ValidateRecoveredEventFacts(
                evidence.History,
                evidence.Facts,
                evidence.Policy,
                script.Round);
        }

        [Fact]
        public void ClosedKanReplacementAndFollowingNormalDrawValidate()
        {
            Script script = ClosedKanScript();
            Evidence evidence = BuildEvidence(script);
            TableVisibilityProjection.ValidateRecoveredEventFacts(
                evidence.History,
                evidence.Facts,
                evidence.Policy,
                script.Round);
        }

        [Fact]
        public void AddedKanImmediateAndChankanWindowValidate()
        {
            foreach (bool chankan in new[] { false, true })
            {
                Script script = AddedKanScript(chankan);
                Evidence evidence = BuildEvidence(script);
                TableVisibilityProjection.ValidateRecoveredEventFacts(
                    evidence.History,
                    evidence.Facts,
                    evidence.Policy,
                    script.Round);
            }
        }

        [Fact]
        public void MultipleKansInOneRoundValidate()
        {
            Script script = MultiKanScript();
            Evidence evidence = BuildEvidence(script);
            Assert.Equal(2, script.Round.Tiles.KanCount);
            Assert.Equal(2, script.Round.Tiles.RinshanPointer);
            TableVisibilityProjection.ValidateRecoveredEventFacts(
                evidence.History,
                evidence.Facts,
                evidence.Policy,
                script.Round);
        }

        [Fact]
        public void LegalKanCheckpointPassesCheckpointValidator()
        {
            Script script = OpenKanScript();
            Evidence evidence = BuildEvidence(script);
            TableCheckpoint checkpoint = BuildCheckpoint(script, evidence);
            Assert.True(
                TableCheckpointValidator.TryValidate(checkpoint, out string reason, out string detail),
                reason + ": " + detail);
        }

        [Fact]
        public async Task CheckpointZeroTailOpenKanRecoveryInstallsAndContinues()
        {
            await AssertRealRecovery(OpenKanScript(), checkpointAtEnd: false);
        }

        [Fact]
        public async Task CheckpointZeroTailClosedKanRecoveryInstallsAndContinues()
        {
            await AssertRealRecovery(ClosedKanScript(), checkpointAtEnd: false);
        }

        [Fact]
        public async Task CheckpointZeroTailAddedKanChankanPassRecoveryInstallsAndContinues()
        {
            await AssertRealRecovery(AddedKanScript(chankan: true), checkpointAtEnd: false);
        }

        [Fact]
        public void ChankanRonTerminalEvidenceValidates()
        {
            Script script = AddedKanRonScript();
            Assert.True(script.Round.IsFinished);
            Evidence evidence = BuildEvidence(script);
            TableVisibilityProjection.ValidateRecoveredEventFacts(
                evidence.History,
                evidence.Facts,
                evidence.Policy,
                script.Round);
        }

        [Fact]
        public async Task CheckpointZeroTailChankanRonRecoveryInstallsTerminal()
        {
            await AssertRealRecovery(AddedKanRonScript(), checkpointAtEnd: false);
        }

        [Fact]
        public async Task CheckpointZeroTailMultiKanRecoveryInstallsAndContinues()
        {
            await AssertRealRecovery(MultiKanScript(), checkpointAtEnd: false);
        }

        [Fact]
        public async Task FinalCheckpointOpenKanRecoveryInstallsAndContinues()
        {
            await AssertRealRecovery(OpenKanScript(), checkpointAtEnd: true);
        }

        [Fact]
        public void CheckpointZeroTailSliceWithKanValidates()
        {
            Script script = OpenKanScript();
            Evidence evidence = BuildEvidence(script);
            int start = evidence.History.FindIndex(
                record => record.CommandVersion.Value == 2);
            Assert.True(start >= 0);
            List<TableEventRecord> tail = evidence.History.Skip(start).ToList();
            List<TableProjectionEventFact> tailFacts = evidence.Facts
                .Where(fact => fact.EventSeq.Value >= tail[0].Seq.Value)
                .ToList();
            TableVisibilityProjection.ValidateRecoveredEventFacts(
                tail,
                tailFacts,
                evidence.Policy,
                script.Round);
        }

        [Fact]
        public void TamperedWallRemainingFailsClosed()
        {
            Script script = OpenKanScript();
            Evidence evidence = BuildEvidence(script);
            int index = evidence.Facts.FindIndex(
                fact => fact.Type == ProtocolEventType.TileDrawn);
            Assert.True(index >= 0);
            TableProjectionEventFact original = evidence.Facts[index];
            Assert.NotNull(original.Facts.WallRemaining);
            evidence.Facts[index] = new TableProjectionEventFact(
                original.Type,
                original.EventSeq,
                original.CommandVersion,
                original.EventOrdinal,
                new TableProjectionEventFacts(
                    original.Facts.TurnNumber,
                    original.Facts.WallSize,
                    original.Facts.WallRemaining!.Value + 1U,
                    original.Facts.Meld,
                    original.Facts.FromSeat,
                    original.Facts.RiichiDiscard,
                    original.Facts.Tsumogiri,
                    original.Facts.DiscardTileId,
                    original.Facts.DoubleRiichi,
                    original.Facts.LoserSeat,
                    original.Facts.Yaku,
                    original.Facts.Han,
                    original.Facts.Fu,
                    original.Facts.Score,
                    original.Facts.Breakdown,
                    original.Facts.TenpaiPlayers,
                    original.Facts.Payments,
                    original.Facts.NewScores,
                    original.Facts.FinalScores,
                    original.Facts.WinnerSeat,
                    original.Facts.RoundWind,
                    original.Facts.RoundNumber,
                    original.Facts.OfferId,
                    original.Facts.KeptFirst,
                    original.Facts.ResultTileId,
                    original.Facts.BeamSwapResolution,
                    original.Facts.HandTileId,
                    original.Facts.TargetSlot));

            Assert.Throws<TableProjectionException>(() =>
                TableVisibilityProjection.ValidateRecoveredEventFacts(
                    evidence.History,
                    evidence.Facts,
                    evidence.Policy,
                    script.Round));
        }

        [Fact]
        public void MissingReplacementDrawFailsClosed()
        {
            Script script = OpenKanScript();
            Evidence evidence = BuildEvidence(script);
            int index = evidence.History.FindIndex(
                record => record.Type == RoundEventType.RinshanDrawn);
            Assert.True(index >= 0);
            ulong seq = evidence.History[index].Seq.Value;
            evidence.History.RemoveAt(index);
            evidence.Facts.RemoveAll(fact => fact.EventSeq.Value == seq);

            Assert.Throws<TableProjectionException>(() =>
                TableVisibilityProjection.ValidateRecoveredEventFacts(
                    evidence.History,
                    evidence.Facts,
                    evidence.Policy,
                    script.Round));
        }

        [Fact]
        public void TamperedKanMeldFailsClosed()
        {
            Script script = OpenKanScript();
            Evidence evidence = BuildEvidence(script);
            int index = evidence.Facts.FindIndex(
                fact => fact.Type == ProtocolEventType.KanDeclared);
            Assert.True(index >= 0);
            TableProjectionEventFact original = evidence.Facts[index];
            MeldView meld = original.Facts.Meld!;
            var tiles = meld.TileIds.ToArray();
            tiles[0] = (uint)Man(9, 3).Value;
            evidence.Facts[index] = new TableProjectionEventFact(
                original.Type,
                original.EventSeq,
                original.CommandVersion,
                original.EventOrdinal,
                new TableProjectionEventFacts(
                    original.Facts.TurnNumber,
                    original.Facts.WallSize,
                    original.Facts.WallRemaining,
                    new MeldView(meld.Type, tiles, meld.CalledTileId, meld.FromSeat),
                    original.Facts.FromSeat,
                    original.Facts.RiichiDiscard,
                    original.Facts.Tsumogiri,
                    original.Facts.DiscardTileId,
                    original.Facts.DoubleRiichi,
                    original.Facts.LoserSeat,
                    original.Facts.Yaku,
                    original.Facts.Han,
                    original.Facts.Fu,
                    original.Facts.Score,
                    original.Facts.Breakdown,
                    original.Facts.TenpaiPlayers,
                    original.Facts.Payments,
                    original.Facts.NewScores,
                    original.Facts.FinalScores,
                    original.Facts.WinnerSeat,
                    original.Facts.RoundWind,
                    original.Facts.RoundNumber,
                    original.Facts.OfferId,
                    original.Facts.KeptFirst,
                    original.Facts.ResultTileId,
                    original.Facts.BeamSwapResolution,
                    original.Facts.HandTileId,
                    original.Facts.TargetSlot));

            Assert.Throws<TableProjectionException>(() =>
                TableVisibilityProjection.ValidateRecoveredEventFacts(
                    evidence.History,
                    evidence.Facts,
                    evidence.Policy,
                    script.Round));
        }

        [Fact]
        public void ReorderedHistoryFailsClosed()
        {
            Script script = OpenKanScript();
            Evidence evidence = BuildEvidence(script);
            int index = evidence.History.FindIndex(
                record => record.Type == RoundEventType.RinshanDrawn);
            Assert.True(index > 0);
            TableEventRecord value = evidence.History[index];
            evidence.History.RemoveAt(index);
            evidence.History.Insert(index - 1, value);

            Assert.Throws<TableProjectionException>(() =>
                TableVisibilityProjection.ValidateRecoveredEventFacts(
                    evidence.History,
                    evidence.Facts,
                    evidence.Policy,
                    script.Round));
        }

        private sealed class Script
        {
            public Script(RoundStateMachine round)
            {
                InitialRound = round;
                Round = round;
            }

            public RoundStateMachine InitialRound { get; }

            public RoundStateMachine Round { get; set; }

            public int NextVersion { get; set; } = 1;

            public List<RoundCommand> Commands { get; } = new List<RoundCommand>();

            public List<EventDraft> Drafts { get; } = new List<EventDraft>();
        }

        private sealed class EventDraft
        {
            public EventDraft(int version, RoundEvent value, MeldView? meld)
            {
                Version = version;
                Value = value;
                Meld = meld;
            }

            public int Version { get; }

            public RoundEvent Value { get; }

            public MeldView? Meld { get; }
        }

        private sealed class Evidence
        {
            public Evidence(
                List<TableEventRecord> history,
                List<TableProjectionEventFact> facts,
                TableProjectionPolicy policy)
            {
                History = history;
                Facts = facts;
                Policy = policy;
            }

            public List<TableEventRecord> History { get; }

            public List<TableProjectionEventFact> Facts { get; }

            public TableProjectionPolicy Policy { get; }
        }

        private const ulong EvidenceFingerprint = 0x51A7E5D3C9B24F61UL;

        private static TableCheckpoint BuildCheckpoint(Script script, Evidence evidence)
        {
            int commandCount = (int)evidence.History[evidence.History.Count - 1].CommandVersion.Value;
            var publicFacts = new List<TableSeatPublicFacts>();
            var privateFacts = new List<TableSeatPrivateFacts>();
            for (int seat = 0; seat < 4; seat++)
            {
                var melds = new List<TableMeldFact>();
                for (int m = 0; m < script.Round.Melds.Count; m++)
                {
                    RoundMeld meld = script.Round.Melds[m];
                    if (meld.Seat != seat)
                    {
                        continue;
                    }

                    var tiles = new List<uint>();
                    for (int t = 0; t < meld.Tiles.Count; t++)
                    {
                        tiles.Add(unchecked((uint)meld.Tiles[t].Value));
                    }

                    ProtocolMeldType type = meld.Kind == RoundMeldKind.Chi
                        ? ProtocolMeldType.Chi
                        : meld.Kind == RoundMeldKind.Pon
                            ? ProtocolMeldType.Pon
                            : meld.Kind == RoundMeldKind.OpenKan
                                ? ProtocolMeldType.OpenKan
                                : meld.Kind == RoundMeldKind.ClosedKan
                                    ? ProtocolMeldType.ClosedKan
                                    : ProtocolMeldType.AddedKan;
                    bool closed = meld.Kind == RoundMeldKind.ClosedKan;
                    melds.Add(new TableMeldFact(
                        type,
                        tiles,
                        closed ? (uint?)null : unchecked((uint)meld.CalledTile.Value),
                        closed ? ProtocolConstants.NoSeat : meld.FromSeat));
                }

                IReadOnlyList<TileId> river = script.Round.Tiles.TilesInZone(TileZone.Discard, seat);
                var discards = new List<DiscardView>();
                var historyTiles = new List<uint>();
                for (int d = 0; d < river.Count; d++)
                {
                    uint tile = unchecked((uint)river[d].Value);
                    discards.Add(new DiscardView(tile, 0, false, true));
                    historyTiles.Add(tile);
                }

                publicFacts.Add(new TableSeatPublicFacts(
                    seat,
                    script.Round.IsRiichi(seat),
                    melds,
                    discards));
                privateFacts.Add(new TableSeatPrivateFacts(seat, false, false, historyTiles));
            }

            var data = new TableProjectionContextData(
                script.Round.RoundId,
                new StateVersion((ulong)commandCount),
                0UL,
                null,
                publicFacts,
                privateFacts,
                null,
                evidence.Policy,
                evidence.Facts);
            byte[] factsPayload = TableProjectionFactsCodec.Encode(data);
            var bindings = new List<TableCommandBinding>();
            int cursor = 0;
            while (cursor < evidence.History.Count)
            {
                ulong version = evidence.History[cursor].CommandVersion.Value;
                int count = 0;
                while (cursor < evidence.History.Count &&
                       evidence.History[cursor].CommandVersion.Value == version)
                {
                    count++;
                    cursor++;
                }

                bindings.Add(new TableCommandBinding(version, count, EvidenceFingerprint));
            }

            ulong lastSeq = evidence.History[evidence.History.Count - 1].Seq.Value;
            return TableCheckpointFactory.Create(
                "room-kan",
                script.Round,
                new StateVersion((ulong)commandCount),
                new EventSeq(lastSeq),
                new TableControlCheckpoint(
                    P31Fixtures.ControlSnapshot(
                        Enumerable.Range(0, 4)
                            .Select(seat => P31Fixtures.Connected(SeatControllerKind.Computer, 0L, seat))
                            .ToArray()),
                    0L),
                new TableTimingCheckpoint(
                    script.Round.RoundId,
                    new long[] { 0L, 0L, 0L, 0L },
                    Array.Empty<TableRecoveredDecision>(),
                    1UL,
                    1L,
                    "boot-kan"),
                1,
                "boot-kan",
                1UL,
                committedFactsPayload: factsPayload,
                timingVersion: 1L,
                capturedMonotonicMillis: 1L,
                committedHistory: evidence.History,
                committedCommandBindings: bindings);
        }

        private static void Execute(
            Script script,
            RoundCommand command,
            MeldView? meld = null)
        {
            RoundTransition transition = script.Round.Execute(command);
            Assert.True(transition.IsAccepted, transition.Error?.ToString());
            script.Commands.Add(command);
            script.Round = transition.State!;
            int version = script.NextVersion++;
            for (int i = 0; i < transition.Events.Count; i++)
            {
                RoundEvent value = transition.Events[i];
                MeldView? eventMeld = value.Type == RoundEventType.ChiDeclared ||
                                      value.Type == RoundEventType.PonDeclared ||
                                      value.Type == RoundEventType.KanDeclared
                    ? meld
                    : null;
                script.Drafts.Add(new EventDraft(version, value, eventMeld));
            }
        }

        private static async Task AssertRealRecovery(Script script, bool checkpointAtEnd)
        {
            const string room = DurableHarness.Room;
            const string owner = DurableHarness.Owner;
            const string account = "acct-kan-recovery";
            var source = new RecoveryProjectionSource();
            var clock = new ManualTableClock { Now = 1000L };
            var scopes = new InMemoryRequestScopeAuthority();
            TableScopeRecord scope = scopes.Issue(
                account,
                TableScopeRole.Player,
                room,
                (ulong)DurableHarness.UtcBase,
                (ulong)DurableHarness.UtcBase + 3_600_000UL);
            var store = new InMemoryTableStore(clock, scopes);
            CheatLedger ledger = DurableHarness.NewLedger();
            CheatLedgerApplyResult seed = ledger.Apply("kan-recovery-seed", 0, 0, "seed");
            Assert.True(seed.Success, seed.Error.ToString());
            ledger = seed.Ledger!;
            await store.CreateRoomAsync(
                BuildZeroCheckpoint(script.InitialRound, ledger),
                CancellationToken.None);
            TableLeaseAcquireResult lease = await store.AcquireAsync(
                room,
                owner,
                TimeSpan.FromMinutes(10),
                CancellationToken.None);
            Assert.True(lease.Acquired, lease.Detail);
            var durable = new TableDurableContext(
                store,
                store,
                store,
                store,
                scopes,
                owner,
                TimeSpan.FromMinutes(10),
                "boot-1",
                1,
                room)
            {
                RecoveryStateSink = new RecordingRecoveryStateSink()
            };
            durable.CheatLedgerProvider = () => ledger;
            durable.CheatStateProvider = () => new CheatState(
                ledger,
                new TileMarkState(),
                new SleeveBloomState(),
                new BeamSwapState());
            TableActorOptions Options(OwnerEpoch epoch) => new TableActorOptions
            {
                RoomId = room,
                MailboxCapacity = 16,
                ControlReserve = 16,
                DecisionClockConfig = new DecisionClockConfig(1000L, 0L, ReserveScope.Round),
                InitialControllers = P31Fixtures.Controllers(
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman,
                    SeatControllerKind.RemoteHuman),
                InitialOwnerEpoch = epoch
            };
            TableCommandEnvelope Envelope(
                RoundCommand command,
                int expectedVersion,
                string requestId,
                ulong ownerEpoch,
                long recoveryGeneration,
                string? decisionId = null)
            {
                byte[] payload = System.Text.Encoding.UTF8.GetBytes(
                    "KAN_RECOVERY|" + requestId + "|" + command.RoundId + "|" + command.Action);
                return new TableCommandEnvelope(
                    command,
                    new RequestId(requestId),
                    new StateVersion((ulong)expectedVersion),
                    new OwnerEpoch(ownerEpoch),
                    new ControlEpoch(0),
                    decisionId == null ? (DecisionId?)null : new DecisionId(decisionId),
                    recoveryGeneration,
                    scope.ScopeId,
                    "KAN_RECOVERY",
                    TablePayloadSha256.Compute(payload));
            }

            var legacy = new RecordingCommitPort();
            var sink = new RecordingPublicationSink(legacy);
            var first = new TableActor(
                Options(lease.Lease!.OwnerEpoch),
                script.InitialRound,
                legacy,
                clock,
                sink,
                null,
                source,
                null,
                durable);
            try
            {
                TableRecoveryResult installed = await first.RecoverFromStoreAsync(
                    TestContext.Current.CancellationToken);
                Assert.True(installed.Recovered, installed.ReasonCode + ": " + installed.Detail);
                TableSnapshot installedSnapshot = await first.GetSnapshotAsync(
                    TestContext.Current.CancellationToken);

                for (int i = 0; i < script.Commands.Count; i++)
                {
                    TableOperationResult result = await first.SubmitAsync(Envelope(
                        script.Commands[i],
                        i,
                        "kan-rec-" + i,
                        installedSnapshot.OwnerEpoch.Value,
                        1L));
                    Assert.True(
                        result.Status == TableOperationStatus.Accepted,
                        "command " + i + " " + result.ReasonCode + ": " + result.Detail);
                    Assert.Equal((ulong)(i + 1), result.CommandVersion!.Value.Value);
                }

                if (checkpointAtEnd)
                {
                    TableOperationResult saved = await first.SaveCheckpointAsync(
                        TestContext.Current.CancellationToken);
                    Assert.Equal(TableOperationStatus.Configured, saved.Status);
                }

                await first.CloseAsync(TestContext.Current.CancellationToken);
                clock.Now += 1000L;
                var service = new TableRecoveryService(store, store, store, clock);
                TableRecoveryResult recovered = await service.RecoverAsync(
                    room,
                    new TableRecoveryOptions(
                        owner,
                        "boot-2",
                        1,
                        TimeSpan.FromMinutes(10),
                        0L,
                        room,
                        requireCheatLedger: false),
                    TestContext.Current.CancellationToken);
                Assert.True(recovered.Recovered, recovered.ReasonCode + ": " + recovered.Detail);
                Assert.NotNull(recovered.Baseline);
                TableRecoveryBaseline baseline = recovered.Baseline!;
                Assert.Equal((ulong)script.Commands.Count, baseline.StateVersion.Value);
                Assert.Equal(script.Round.SourceFingerprint, baseline.Round.SourceFingerprint);
                ConservationReport conservation = TilePositionMap.Validate(baseline.Round.Tiles);
                Assert.True(conservation.IsValid, conservation.ToString());
                Assert.Equal(136, conservation.AssignedCount);
                Assert.Equal(0, conservation.UnassignedCount);

                var second = new TableActor(
                    Options(baseline.OwnerEpoch),
                    script.InitialRound,
                    new RecordingCommitPort(),
                    clock,
                    new RecordingPublicationSink(new RecordingCommitPort()),
                    null,
                    source,
                    null,
                    durable);
                try
                {
                    Assert.Equal(
                        TableOperationStatus.Configured,
                        (await second.BeginRecoveryAsync(
                            "kan-proof",
                            TestContext.Current.CancellationToken)).Status);
                    TableOperationResult completed = await second.CompleteRecoveryAsync(
                        baseline,
                        TestContext.Current.CancellationToken);
                    Assert.True(
                        completed.Status == TableOperationStatus.Configured,
                        completed.ReasonCode + ": " + completed.Detail);
                    TableTimingSnapshot timing = await second.GetTimingSnapshotAsync(
                        TestContext.Current.CancellationToken);
                    Assert.All(timing.Seats, seat => Assert.Null(seat.PendingKey));

                    RoundStateMachine round = baseline.Round;
                    if (!round.IsFinished)
                    {
                        int seat = round.CurrentSeat;
                        TileId tile = round.Tiles.TilesInZone(TileZone.Hand, seat)[0];
                        var decision = new PendingDecision(
                            new DecisionKey(round.RoundId, seat, "post-recovery"),
                            0L,
                            new[] { new LegalActionOption(LegalActionKind.Discard, seat, tile) },
                            tile);
                        Assert.Equal(
                            TableOperationStatus.Configured,
                            (await second.OpenDecisionAsync(
                                decision,
                                TestContext.Current.CancellationToken)).Status);
                        TableOperationResult applied = await second.SubmitAsync(Envelope(
                            RoundCommand.Discard(round.RoundId, seat, tile),
                            script.Commands.Count,
                            "post-recovery",
                            baseline.OwnerEpoch.Value,
                            1L,
                            "post-recovery"));
                        Assert.True(
                            applied.Status == TableOperationStatus.Accepted,
                            applied.ReasonCode + ": " + applied.Detail);
                        Assert.Equal(
                            (ulong)(script.Commands.Count + 1),
                            (await second.GetSnapshotAsync(
                                TestContext.Current.CancellationToken)).StateVersion.Value);
                    }
                }
                finally
                {
                    await second.DisposeAsync();
                }
            }
            finally
            {
                await first.DisposeAsync();
            }
        }

        private static TableCheckpoint BuildZeroCheckpoint(
            RoundStateMachine round,
            CheatLedger ledger)
        {
            var control = new TableControlCheckpoint(DurableHarness.DefaultControl(), 0L);
            var timing = new TableTimingCheckpoint(
                round.RoundId,
                new long[] { 0L, 0L, 0L, 0L },
                Array.Empty<TableRecoveredDecision>(),
                (ulong)DurableHarness.UtcBase,
                0L,
                DurableHarness.Boot);
            var source = new RecoveryProjectionSource();
            TableProjectionContextData data = source.Capture(new TableProjectionContextRequest(
                DurableHarness.Room,
                round,
                new StateVersion(0),
                new EventSeq(0),
                new OwnerEpoch(1),
                0L,
                0L,
                0L,
                DurableHarness.UtcBase,
                Array.Empty<TableEventRecord>()));
            byte[] facts = TableProjectionFactsCodec.Encode(data);
            return TableCheckpointFactory.Create(
                DurableHarness.Room,
                round,
                new StateVersion(0),
                new EventSeq(0),
                control,
                timing,
                1,
                DurableHarness.Boot,
                (ulong)DurableHarness.UtcBase,
                cheat: TableCheatLedgerCheckpoint.FromLedger(ledger),
                committedFactsPayload: facts,
                timingVersion: 1L,
                capturedMonotonicMillis: 0L,
                committedHistory: Array.Empty<TableEventRecord>(),
                committedCommandBindings: Array.Empty<TableCommandBinding>());
        }

        private sealed class RecoveryProjectionSource : ITableProjectionContextSource
        {
            public TableProjectionContextData Capture(TableProjectionContextRequest request)
            {
                return new TableProjectionContextData(
                    request.Round.RoundId,
                    request.StateVersion,
                    7UL,
                    null,
                    BuildPublicFacts(request.Round),
                    BuildPrivateFacts(request.Round),
                    null,
                    new TableProjectionPolicy(false, settlementBatch: true, revealSettlementUra: true),
                    BuildRequestFacts(request));
            }

            private static IReadOnlyList<TableSeatPublicFacts> BuildPublicFacts(RoundStateMachine round)
            {
                var result = new List<TableSeatPublicFacts>(4);
                for (int seat = 0; seat < 4; seat++)
                {
                    var melds = new List<TableMeldFact>();
                    for (int i = 0; i < round.Melds.Count; i++)
                    {
                        RoundMeld meld = round.Melds[i];
                        if (meld.Seat != seat)
                        {
                            continue;
                        }

                        var tiles = new uint[meld.Tiles.Count];
                        for (int t = 0; t < tiles.Length; t++)
                        {
                            tiles[t] = unchecked((uint)meld.Tiles[t].Value);
                        }

                        bool closed = meld.Kind == RoundMeldKind.ClosedKan;
                        ProtocolMeldType type = meld.Kind == RoundMeldKind.Chi
                            ? ProtocolMeldType.Chi
                            : meld.Kind == RoundMeldKind.Pon
                                ? ProtocolMeldType.Pon
                                : meld.Kind == RoundMeldKind.OpenKan
                                    ? ProtocolMeldType.OpenKan
                                    : closed
                                        ? ProtocolMeldType.ClosedKan
                                        : ProtocolMeldType.AddedKan;
                        melds.Add(new TableMeldFact(
                            type,
                            tiles,
                            closed ? (uint?)null : unchecked((uint)meld.CalledTile.Value),
                            closed ? ProtocolConstants.NoSeat : meld.FromSeat));
                    }

                    IReadOnlyList<TileId> river = round.Tiles.TilesInZone(TileZone.Discard, seat);
                    var discards = new List<DiscardView>(river.Count);
                    for (int i = 0; i < river.Count; i++)
                    {
                        discards.Add(new DiscardView(unchecked((uint)river[i].Value), 0, false, false));
                    }

                    result.Add(new TableSeatPublicFacts(seat, round.IsRiichi(seat), melds, discards));
                }

                return result;
            }

            private static IReadOnlyList<TableSeatPrivateFacts> BuildPrivateFacts(RoundStateMachine round)
            {
                var result = new List<TableSeatPrivateFacts>(4);
                for (int seat = 0; seat < 4; seat++)
                {
                    IReadOnlyList<TileId> river = round.Tiles.TilesInZone(TileZone.Discard, seat);
                    var history = new uint[river.Count];
                    for (int i = 0; i < river.Count; i++)
                    {
                        history[i] = unchecked((uint)river[i].Value);
                    }

                    result.Add(new TableSeatPrivateFacts(seat, false, false, history));
                }

                return result;
            }
        }

        private static IReadOnlyList<TableProjectionEventFact> BuildRequestFacts(
            TableProjectionContextRequest request)
        {
            IReadOnlyList<TableEventRecord> records = request.CommittedEvents;
            var wallBySeq = new Dictionary<ulong, uint>();
            long wall = request.Round.Tiles.WallRemaining;
            for (int i = records.Count - 1; i >= 0; i--)
            {
                if (records[i].Type == RoundEventType.TileDrawn)
                {
                    wallBySeq[records[i].Seq.Value] = unchecked((uint)wall);
                    wall++;
                }
                else if (records[i].Type == RoundEventType.RinshanDrawn)
                {
                    wall++;
                }
            }

            var used = new Dictionary<int, HashSet<int>>();
            var facts = new List<TableProjectionEventFact>(records.Count);
            for (int i = 0; i < records.Count; i++)
            {
                TableEventRecord record = records[i];
                TableEventProjectionInput? input = TableEventProjection.FromRoundEvent(record);
                if (input == null)
                {
                    continue;
                }

                uint turn = unchecked((uint)(i + 1));
                TableProjectionEventFacts values;
                switch (record.Type)
                {
                    case RoundEventType.TileDrawn:
                        values = new TableProjectionEventFacts(
                            turnNumber: turn,
                            wallRemaining: wallBySeq[record.Seq.Value]);
                        break;
                    case RoundEventType.TileDiscarded:
                        values = new TableProjectionEventFacts(
                            turnNumber: turn,
                            riichiDiscard: false,
                            tsumogiri: true,
                            discardTileId: unchecked((uint)record.Tile!.Value.Value));
                        break;
                    case RoundEventType.ChiDeclared:
                    case RoundEventType.PonDeclared:
                    case RoundEventType.KanDeclared:
                        MeldView meld = FindMeld(request.Round, record, used);
                        values = new TableProjectionEventFacts(
                            turnNumber: turn,
                            meld: meld,
                            fromSeat: record.Type == RoundEventType.KanDeclared
                                ? (int?)null
                                : meld.FromSeat);
                        break;
                    case RoundEventType.RonWin:
                        values = new TableProjectionEventFacts(
                            turnNumber: turn,
                            loserSeat: 1,
                            yaku: new[] { new YakuView("CHANKAN", 1U) },
                            han: 1U,
                            fu: 30U,
                            score: 1000L);
                        break;
                    case RoundEventType.RoundEnded:
                        values = new TableProjectionEventFacts(
                            turnNumber: turn,
                            roundWind: unchecked((uint)request.Round.RoundWind),
                            roundNumber: unchecked((uint)request.Round.RoundNumber));
                        break;
                    default:
                        values = new TableProjectionEventFacts(turnNumber: turn);
                        break;
                }

                facts.Add(new TableProjectionEventFact(
                    input.Type,
                    record.Seq,
                    record.CommandVersion,
                    unchecked((uint)record.Ordinal),
                    values));
            }

            return facts;
        }

        private static MeldView FindMeld(
            RoundStateMachine round,
            TableEventRecord record,
            Dictionary<int, HashSet<int>> used)
        {
            if (!record.Tile.HasValue)
            {
                throw new InvalidOperationException("A declared meld event requires a tile.");
            }

            if (!used.TryGetValue(record.Seat, out HashSet<int>? indices))
            {
                indices = new HashSet<int>();
                used[record.Seat] = indices;
            }

            for (int i = 0; i < round.Melds.Count; i++)
            {
                if (indices.Contains(i) || round.Melds[i].Seat != record.Seat)
                {
                    continue;
                }

                RoundMeld meld = round.Melds[i];
                bool kindMatches = record.Type == RoundEventType.ChiDeclared
                    ? meld.Kind == RoundMeldKind.Chi
                    : record.Type == RoundEventType.PonDeclared
                        ? meld.Kind == RoundMeldKind.Pon
                        : meld.Kind == RoundMeldKind.OpenKan ||
                          meld.Kind == RoundMeldKind.ClosedKan ||
                          meld.Kind == RoundMeldKind.AddedKan;
                if (!kindMatches || !meld.Tiles.Contains(record.Tile.Value))
                {
                    continue;
                }

                indices.Add(i);
                var tiles = new uint[meld.Tiles.Count];
                for (int t = 0; t < tiles.Length; t++)
                {
                    tiles[t] = unchecked((uint)meld.Tiles[t].Value);
                }

                bool closed = meld.Kind == RoundMeldKind.ClosedKan;
                ProtocolMeldType type = meld.Kind == RoundMeldKind.Chi
                    ? ProtocolMeldType.Chi
                    : meld.Kind == RoundMeldKind.Pon
                        ? ProtocolMeldType.Pon
                        : meld.Kind == RoundMeldKind.OpenKan
                            ? ProtocolMeldType.OpenKan
                            : closed
                                ? ProtocolMeldType.ClosedKan
                                : ProtocolMeldType.AddedKan;
                return new MeldView(
                    type,
                    tiles,
                    closed ? (uint?)null : unchecked((uint)meld.CalledTile.Value),
                    closed ? ProtocolConstants.NoSeat : meld.FromSeat);
            }

            throw new InvalidOperationException("No matching recovered meld for " + record.Seq + ".");
        }

        private static Evidence BuildEvidence(Script script)
        {
            ConservationReport report = TilePositionMap.Validate(script.Round.Tiles);
            Assert.True(report.IsValid, report.ToString());
            Assert.Equal(136, report.AssignedCount);
            Assert.Equal(0, report.UnassignedCount);

            var history = new List<TableEventRecord>(script.Drafts.Count);
            ulong seq = 1;
            int ordinal = 0;
            int version = 0;
            for (int i = 0; i < script.Drafts.Count; i++)
            {
                EventDraft draft = script.Drafts[i];
                if (draft.Version != version)
                {
                    version = draft.Version;
                    ordinal = 0;
                }

                int eventOrdinal = ordinal++;
                history.Add(new TableEventRecord(
                    new EventSeq(seq++),
                    new StateVersion((ulong)draft.Version),
                    eventOrdinal,
                    draft.Value.Type,
                    draft.Value.Seat,
                    draft.Value.Tile,
                    draft.Value.Detail,
                    1,
                    TableEventFactCodec.Encode(
                        new StateVersion((ulong)draft.Version),
                        eventOrdinal,
                        EvidenceFingerprint)));
            }

            var wallBySeq = new Dictionary<ulong, uint>();
            long wall = script.Round.Tiles.WallRemaining;
            for (int i = history.Count - 1; i >= 0; i--)
            {
                if (history[i].Type == RoundEventType.TileDrawn)
                {
                    wallBySeq[history[i].Seq.Value] = unchecked((uint)wall);
                    wall++;
                }
                else if (history[i].Type == RoundEventType.RinshanDrawn)
                {
                    wall++;
                }
            }

            var facts = new List<TableProjectionEventFact>(history.Count);
            for (int i = 0; i < history.Count; i++)
            {
                TableEventRecord record = history[i];
                TableEventProjectionInput? input = TableEventProjection.FromRoundEvent(record);
                if (input == null)
                {
                    continue;
                }

                uint turn = unchecked((uint)(i + 1));
                TableProjectionEventFacts values;
                switch (record.Type)
                {
                    case RoundEventType.TileDrawn:
                        values = new TableProjectionEventFacts(
                            turnNumber: turn,
                            wallRemaining: wallBySeq[record.Seq.Value]);
                        break;
                    case RoundEventType.TileDiscarded:
                        values = new TableProjectionEventFacts(
                            turnNumber: turn,
                            riichiDiscard: false,
                            tsumogiri: true,
                            discardTileId: unchecked((uint)record.Tile!.Value.Value));
                        break;
                    case RoundEventType.ChiDeclared:
                    case RoundEventType.PonDeclared:
                    case RoundEventType.KanDeclared:
                        MeldView meld = script.Drafts[i].Meld!;
                        values = new TableProjectionEventFacts(
                            turnNumber: turn,
                            meld: meld,
                            fromSeat: record.Type == RoundEventType.KanDeclared
                                ? (int?)null
                                : meld.FromSeat);
                        break;
                    case RoundEventType.RonWin:
                        values = new TableProjectionEventFacts(
                            turnNumber: turn,
                            loserSeat: 1,
                            yaku: new[] { new YakuView("CHANKAN", 1U) },
                            han: 1U,
                            fu: 30U,
                            score: 1000L);
                        break;
                    case RoundEventType.RoundEnded:
                        values = new TableProjectionEventFacts(
                            turnNumber: turn,
                            roundWind: unchecked((uint)script.Round.RoundWind),
                            roundNumber: unchecked((uint)script.Round.RoundNumber));
                        break;
                    default:
                        values = new TableProjectionEventFacts(turnNumber: turn);
                        break;
                }

                facts.Add(new TableProjectionEventFact(
                    input.Type,
                    record.Seq,
                    record.CommandVersion,
                    unchecked((uint)record.Ordinal),
                    values));
            }

            var policy = new TableProjectionPolicy(
                false,
                settlementBatch: true,
                revealSettlementUra: true);
            return new Evidence(history, facts, policy);
        }

        private static Script OpenKanScript()
        {
            RoundStateMachine round = BuildDealt(
                "open-kan",
                new Dictionary<int, TileId[]>
                {
                    [1] = new[] { Dragon(1, 0), Dragon(1, 1), Dragon(1, 2) }
                },
                Dragon(1, 3));
            var script = new Script(round);
            TileId target = Dragon(1, 3);
            Execute(script, RoundCommand.Discard(round.RoundId, 0, target, new[] { 1 }));
            Execute(
                script,
                RoundCommand.OpenKan(
                    round.RoundId,
                    1,
                    target,
                    Dragon(1, 0),
                    Dragon(1, 1),
                    Dragon(1, 2)),
                new MeldView(
                    ProtocolMeldType.OpenKan,
                    new uint[] { 124, 125, 126, 127 },
                    127,
                    0));
            Execute(script, RoundCommand.Discard(round.RoundId, 1, script.Round.Tiles.DrawnTile!.Value.Tile));
            return script;
        }

        private static Script ClosedKanScript()
        {
            RoundStateMachine round = BuildDealt(
                "closed-kan",
                new Dictionary<int, TileId[]>
                {
                    [0] = new[] { Dragon(2, 0), Dragon(2, 1), Dragon(2, 2), Dragon(2, 3) }
                },
                Man(9, 3));
            var script = new Script(round);
            Execute(
                script,
                RoundCommand.ClosedKan(
                    round.RoundId,
                    0,
                    Dragon(2, 0),
                    Dragon(2, 1),
                    Dragon(2, 2),
                    Dragon(2, 3)),
                new MeldView(
                    ProtocolMeldType.ClosedKan,
                    new uint[] { 128, 129, 130, 131 },
                    null,
                    ProtocolConstants.NoSeat));
            Execute(script, RoundCommand.Discard(round.RoundId, 0, script.Round.Tiles.DrawnTile!.Value.Tile));
            return script;
        }

        private static Script AddedKanScript(bool chankan)
        {
            RoundStateMachine round = BuildDealt(
                "added-kan-" + chankan,
                new Dictionary<int, TileId[]>
                {
                    [1] = new[] { Dragon(1, 0), Dragon(1, 1), Dragon(1, 2) }
                },
                Dragon(1, 3));
            var script = new Script(round);
            TileId target = Dragon(1, 3);
            Execute(script, RoundCommand.Discard(round.RoundId, 0, target, new[] { 1 }));
            Execute(
                script,
                RoundCommand.Pon(round.RoundId, 1, target, Dragon(1, 0), Dragon(1, 1)),
                new MeldView(
                    ProtocolMeldType.Pon,
                    new uint[] { 124, 125, 127 },
                    127,
                    0));
            var addedMeld = new MeldView(
                ProtocolMeldType.AddedKan,
                new uint[] { 124, 125, 127, 126 },
                127,
                0);
            if (chankan)
            {
                Execute(
                    script,
                    RoundCommand.AddedKan(round.RoundId, 1, Dragon(1, 2), new[] { 2 }),
                    addedMeld);
                Execute(script, RoundCommand.Pass(round.RoundId, 2), addedMeld);
            }
            else
            {
                Execute(script, RoundCommand.AddedKan(round.RoundId, 1, Dragon(1, 2)), addedMeld);
            }

            Execute(script, RoundCommand.Discard(round.RoundId, 1, script.Round.Tiles.DrawnTile!.Value.Tile));
            return script;
        }

        private static Script AddedKanRonScript()
        {
            RoundStateMachine round = BuildDealt(
                "added-kan-ron",
                new Dictionary<int, TileId[]>
                {
                    [1] = new[] { Dragon(1, 0), Dragon(1, 1), Dragon(1, 2) }
                },
                Dragon(1, 3));
            var script = new Script(round);
            TileId target = Dragon(1, 3);
            Execute(script, RoundCommand.Discard(round.RoundId, 0, target, new[] { 1 }));
            Execute(
                script,
                RoundCommand.Pon(round.RoundId, 1, target, Dragon(1, 0), Dragon(1, 1)),
                new MeldView(
                    ProtocolMeldType.Pon,
                    new uint[] { 124, 125, 127 },
                    127,
                    0));
            Execute(
                script,
                RoundCommand.AddedKan(round.RoundId, 1, Dragon(1, 2), new[] { 2 }),
                new MeldView(
                    ProtocolMeldType.AddedKan,
                    new uint[] { 124, 125, 127, 126 },
                    127,
                    0));
            var settlement = new RoundSettlement(
                "chankan-ron",
                RoundOutcome.Ron(2, 1),
                script.Round.Scores.ToArray(),
                script.Round.RiichiSticks,
                new PersistedUtcTime(DateTimeOffset.UnixEpoch),
                1000L);
            Execute(script, RoundCommand.Ron(script.Round.RoundId, 2, settlement));
            return script;
        }

        private static Script MultiKanScript()
        {
            RoundStateMachine round = BuildDealt(
                "multi-kan",
                new Dictionary<int, TileId[]>
                {
                    [0] = new[] { Dragon(2, 0), Dragon(2, 1), Dragon(2, 2), Dragon(2, 3) },
                    [1] = new[] { Dragon(1, 0), Dragon(1, 1), Dragon(1, 2) }
                },
                Dragon(1, 3));
            var script = new Script(round);
            TileId target = Dragon(1, 3);
            Execute(script, RoundCommand.Discard(round.RoundId, 0, target, new[] { 1 }));
            Execute(
                script,
                RoundCommand.OpenKan(
                    round.RoundId,
                    1,
                    target,
                    Dragon(1, 0),
                    Dragon(1, 1),
                    Dragon(1, 2)),
                new MeldView(
                    ProtocolMeldType.OpenKan,
                    new uint[] { 124, 125, 126, 127 },
                    127,
                    0));
            Execute(script, RoundCommand.Discard(round.RoundId, 1, script.Round.Tiles.DrawnTile!.Value.Tile));
            for (int guard = 0; script.Round.CurrentSeat != 0 && guard < 6; guard++)
            {
                int seat = script.Round.CurrentSeat;
                TileId tile = script.Round.Tiles.TilesInZone(TileZone.Hand, seat)[0];
                Execute(script, RoundCommand.Discard(script.Round.RoundId, seat, tile));
            }

            Assert.Equal(0, script.Round.CurrentSeat);
            Execute(
                script,
                RoundCommand.ClosedKan(
                    script.Round.RoundId,
                    0,
                    Dragon(2, 0),
                    Dragon(2, 1),
                    Dragon(2, 2),
                    Dragon(2, 3)),
                new MeldView(
                    ProtocolMeldType.ClosedKan,
                    new uint[] { 128, 129, 130, 131 },
                    null,
                    ProtocolConstants.NoSeat));
            Execute(script, RoundCommand.Discard(script.Round.RoundId, 0, script.Round.Tiles.DrawnTile!.Value.Tile));
            return script;
        }

        private static RoundStateMachine BuildDealt(
            string roundId,
            IReadOnlyDictionary<int, TileId[]> hands,
            TileId drawn)
        {
            var placements = new TilePlacement[TileId.Count];
            var used = new bool[TileId.Count];
            var placed = new bool[TileId.Count];

            void Place(TileId tile, TilePosition position)
            {
                if (used[tile.Value])
                {
                    throw new InvalidOperationException("tile " + tile + " specified twice");
                }

                used[tile.Value] = true;
                placed[tile.Value] = true;
                placements[tile.Value] = new TilePlacement(tile, position);
            }

            for (int seat = 0; seat < 4; seat++)
            {
                if (!hands.TryGetValue(seat, out TileId[]? hand))
                {
                    continue;
                }

                for (int slot = 0; slot < hand.Length; slot++)
                {
                    Place(hand[slot], TilePosition.Hand(seat, slot));
                }
            }

            Place(drawn, TilePosition.Hand(0, 13));

            int cursor = 0;
            TileId NextUnused()
            {
                while (cursor < TileId.Count && used[cursor])
                {
                    cursor++;
                }

                if (cursor >= TileId.Count)
                {
                    throw new InvalidOperationException("fixture ran out of physical tiles");
                }

                used[cursor] = true;
                return new TileId(cursor);
            }

            for (int seat = 0; seat < 4; seat++)
            {
                int specified = hands.TryGetValue(seat, out TileId[]? hand) ? hand.Length : 0;
                for (int slot = specified; slot < 13; slot++)
                {
                    TileId tile = NextUnused();
                    placements[tile.Value] = new TilePlacement(tile, TilePosition.Hand(seat, slot));
                    placed[tile.Value] = true;
                }
            }

            for (int slot = 0; slot < 14; slot++)
            {
                TileId tile = NextUnused();
                placements[tile.Value] = new TilePlacement(tile, TilePosition.DeadWall(slot));
                placed[tile.Value] = true;
            }

            int wallCount = 0;
            for (int i = 0; i < TileId.Count; i++)
            {
                if (used[i])
                {
                    continue;
                }

                placements[i] = new TilePlacement(new TileId(i), TilePosition.Wall(wallCount));
                placed[i] = true;
                wallCount++;
            }

            for (int i = 0; i < TileId.Count; i++)
            {
                if (!placed[i])
                {
                    throw new InvalidOperationException("fixture left tile " + i + " unplaced");
                }
            }

            TileId dora = new TileId(Array.FindIndex(
                placements,
                placement => placement.Position == TilePosition.DeadWall(4)));
            TilePositionMap map = TilePositionMap.FromPlacements(
                placements,
                wallPointer: 0,
                wallEndExclusive: wallCount,
                rinshanPointer: 0,
                lastDiscard: null,
                revision: 0,
                rinshanDraw: null,
                doraIndicators: new[] { dora },
                kanCount: 0,
                drawnTile: new DrawnTileEvidence(drawn, 0, DrawSource.Wall));
            var spec = new RoundSpec(
                roundId,
                0,
                1,
                0,
                0,
                new long[] { 25000, 25000, 25000, 25000 },
                0);
            return RoundStateMachine.CreateDealt(spec, map, RoundPhase.CheatWindow);
        }
    }
}
