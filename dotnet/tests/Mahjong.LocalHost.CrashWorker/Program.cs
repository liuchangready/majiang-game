using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Mahjong.LocalHost;
using Mahjong.TableRuntime;
using PlayerSnapshot = Mahjong.Contracts.Wire.PlayerSnapshot;

namespace Mahjong.LocalHost.CrashWorker
{
    internal sealed class FixedWorkerClock : ITableClock
    {
        public FixedWorkerClock(long monotonicMillis, long utcNowMs)
        {
            MonotonicMillis = monotonicMillis;
            UtcNowMs = utcNowMs;
        }

        public long MonotonicMillis { get; }

        public long UtcNowMs { get; }
    }

    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("usage: <saveDir> <roomId> <markerPath> [mode]");
                return 2;
            }

            string saveDir = args[0];
            string roomId = args[1];
            string marker = args[2];
            string mode = args.Length >= 4 ? args[3] : "action";

            var options = new LocalTableHostOptions
            {
                SaveDirectory = saveDir,
                RoomId = roomId,
                OwnerInstanceId = "crash-owner",
                AutoPlay = false
            };
            if (args.Length >= 6)
            {
                options.Environment = new LocalHostEnvironment
                {
                    Clock = new FixedWorkerClock(
                        long.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture),
                        long.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture))
                };
            }

            if (mode == "furiten-read")
            {
                // Cold-process evidence: open the existing committed save in a separate OS
                // process, run the real recovery path, and report the recovered snapshot.
                await using (LocalTableRuntime readRuntime = await LocalTableRuntime.OpenAsync(
                    options,
                    CancellationToken.None))
                {
                    TableFuritenSnapshot furiten = readRuntime.ActorForTest.FuritenForHost;
                    File.WriteAllLines(marker, new[]
                    {
                        furiten.RoundId,
                        readRuntime.ActorForTest.GetRoundStateForHost().Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        furiten.TemporaryMask.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        furiten.RiichiMask.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    });
                }

                return 0;
            }

            if (mode == "recovery-boundary-read")
            {
                // Cold-process evidence for the D-LH-GATE-04 authoritative boundary:
                // a separate OS process opens the same committed save through the real
                // LocalTableRuntime recovery path and reports the durable state/event
                // versions, round identity, 136-tile hash, ledger and receipt. The parent
                // test compares these lines byte-for-byte with its own store reading.
                if (args.Length < 9)
                {
                    Console.Error.WriteLine(
                        "recovery-boundary-read requires <account> <scopeId> <requestId> after the clock.");
                    return 2;
                }

                string boundaryAccount = args[6];
                string boundaryScope = args[7];
                string boundaryRequest = args[8];
                await using (LocalTableRuntime boundaryRuntime = await LocalTableRuntime.OpenAsync(
                    options,
                    CancellationToken.None))
                {
                    LocalTableStore boundaryStore = boundaryRuntime.StoreForTest;
                    TableStateRow boundaryState = (await boundaryStore.LoadStateAsync(
                        roomId,
                        CancellationToken.None))!;
                    TableCheckpoint boundaryCheckpoint = (await boundaryStore.LoadCheckpointAsync(
                        roomId,
                        CancellationToken.None))!;
                    IReadOnlyList<TableEventRecord> boundaryEvents = await boundaryStore.LoadEventsAfterAsync(
                        roomId,
                        new EventSeq(0),
                        CancellationToken.None);
                    TableDurableReceipt? boundaryReceipt = await boundaryStore.TryGetAsync(
                        boundaryAccount,
                        new RequestScopeId(boundaryScope),
                        new RequestId(boundaryRequest),
                        CancellationToken.None);
                    if (boundaryReceipt == null)
                    {
                        Console.Error.WriteLine("recovery-boundary-read receipt not found");
                        return 5;
                    }

                    RoundStateMachine boundaryRound = boundaryRuntime.ActorForTest.GetRoundStateForHost();
                    TableCheatLedgerCheckpoint boundaryCheat = TableCheatLedgerCheckpointCodec.Decode(
                        boundaryCheckpoint.CheatPayload!);
                    ConservationReport boundaryConservation = TilePositionMap.Validate(boundaryRound.Tiles);
                    var boundaryLines = new List<string>
                    {
                        "mode=recovery-boundary-read",
                        "state_version=" + boundaryState.StateVersion.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "last_event_seq=" + boundaryState.LastEventSeq.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "state_fingerprint=" + boundaryState.StateFingerprint.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "state_hash=" + boundaryState.StateHash.ToHex(),
                        "checkpoint_state_version=" + boundaryCheckpoint.StateVersion.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "checkpoint_last_event_seq=" + boundaryCheckpoint.LastEventSeq.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "round_id=" + boundaryRound.RoundId,
                        "tiles_hash=" + boundaryConservation.StateHash.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "ledger_balances=" + string.Join(",", boundaryCheat.Balances),
                        "ledger_entries=" + boundaryCheat.Entries.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "ledger_digest=" + BoundaryLedgerDigest(boundaryCheat),
                        "event_count=" + boundaryEvents.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "event_summary=" + BoundaryEventSummary(boundaryEvents),
                        "event_digest=" + BoundaryEventDigest(boundaryEvents),
                        "receipt_request_id=" + boundaryReceipt.RequestId.Value,
                        "receipt_scope_id=" + boundaryReceipt.ScopeId.Value,
                        "receipt_status=" + boundaryReceipt.Receipt.Status,
                        "receipt_evaluated=" + boundaryReceipt.Receipt.EvaluatedStateVersion.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "receipt_command_version=" + boundaryReceipt.Receipt.CommandVersion!.Value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "receipt_event_seq=" + boundaryReceipt.Receipt.EventSeq!.Value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "receipt_payload_sha256=" + boundaryReceipt.PayloadSha256.ToHex()
                    };
                    File.WriteAllLines(marker, boundaryLines);
                }

                return 0;
            }

            if (mode == "control-outbox-reconcile")
            {
                // Cold-process evidence: a separate OS process opens a fresh local runtime
                // from the committed save, loads the unresolved durable client outbox, runs
                // the real baseline+RECEIPT_QUERY reconciliation, and reports both the
                // resolution and the durable control record it reads back.
                if (args.Length < 10)
                {
                    Console.Error.WriteLine(
                        "control-outbox-reconcile requires <outboxPath> <account> <scopeId> <requestId>.");
                    return 2;
                }

                string outboxPath = args[6];
                string account = args[7];
                string scopeId = args[8];
                string requestId = args[9];
                var controlLines = new List<string>();
                ITableChannel? controlChannel = null;
                TableSession? controlSession = null;
                try
                {
                    controlChannel = await LocalTableHost.CreateChannelAsync(options, CancellationToken.None);
                    string controlChannelTicket = LocalTableHost.IssueTicket(options);
                    await controlChannel.OpenAsync(
                        new TableChannelConnectRequest(controlChannelTicket, "crash-worker", null),
                        CancellationToken.None);
                    controlSession = new TableSession(controlChannel, new FileOutboxStore(outboxPath));
                    var resolutions = new List<TableReceiptResolvedEventArgs>();
                    controlSession.ReceiptResolved += (_, args2) =>
                    {
                        lock (resolutions)
                        {
                            resolutions.Add(args2);
                        }
                    };
                    string controlSessionTicket = LocalTableHost.IssueTicket(options);
                    await controlSession.OpenAsync(
                        new TableSessionOpenRequest(controlSessionTicket, "crash-worker"),
                        CancellationToken.None);
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                    {
                        while (!timeout.IsCancellationRequested)
                        {
                            bool done;
                            lock (resolutions)
                            {
                                done = controlSession.State == TableConnectionState.Ready &&
                                       controlSession.PendingCommands.Count == 0 &&
                                       resolutions.Count > 0;
                            }

                            if (done)
                            {
                                break;
                            }

                            try
                            {
                                await controlSession.PumpOnceAsync(timeout.Token);
                            }
                            catch (OperationCanceledException)
                            {
                                break;
                            }
                            catch (TableChannelClosedException)
                            {
                                break;
                            }
                            catch (InvalidOperationException)
                            {
                                break;
                            }
                        }
                    }

                    lock (resolutions)
                    {
                        for (int i = 0; i < resolutions.Count; i++)
                        {
                            TableReceiptResolvedEventArgs resolved = resolutions[i];
                            controlLines.Add(
                                "RESOLUTION|" + resolved.Command.RequestId + "|" +
                                resolved.Resolution.Disposition + "|" +
                                resolved.Resolution.ReasonCode + "|" +
                                resolved.Resolution.CanonicalPayloadSha256 + "|" +
                                (resolved.Resolution.FromReceiptLookup ? "1" : "0") + "|" +
                                resolved.Command.Kind + "|" +
                                resolved.Resolution.EvaluatedStateVersion.Value.ToString(
                                    System.Globalization.CultureInfo.InvariantCulture));
                        }
                    }

                    controlLines.Add("STATE|" + controlSession.State);
                    controlLines.Add("PENDING|" + controlSession.PendingCommands.Count);
                }
                finally
                {
                    if (controlSession != null)
                    {
                        try
                        {
                            await controlSession.CloseAsync("WORKER_DONE", CancellationToken.None);
                        }
                        catch
                        {
                        }
                    }

                    if (controlChannel != null)
                    {
                        try
                        {
                            await controlChannel.CloseAsync(
                                new TableChannelCloseReason("WORKER_DONE", true),
                                CancellationToken.None);
                        }
                        catch
                        {
                        }
                    }
                }

                ITableClock recordClock = options.Environment?.ResolveClock() ?? SystemTableClock.Instance;
                using (LocalTableStore controlStore = await LocalTableStore.OpenAsync(
                    options,
                    recordClock,
                    null,
                    CancellationToken.None))
                {
                    LocalControlRecord? control = controlStore.GetControlResult(
                        account,
                        new RequestScopeId(scopeId),
                        new RequestId(requestId));
                    controlLines.Add(
                        "RECORD|" +
                        (control == null ? "<null>" : control.Digest) + "|" +
                        (control == null ? "<null>" : control.Kind) + "|" +
                        (control?.EvaluatedStateVersion ?? 0UL).ToString(
                            System.Globalization.CultureInfo.InvariantCulture));
                    controlLines.Add(
                        "CONTROLVERSION|" + controlStore.ControlVersionForTest.ToString(
                            System.Globalization.CultureInfo.InvariantCulture));
                }

                File.WriteAllLines(marker, controlLines);
                return 0;
            }

            if (mode == "settlement")
            {
                var clock = SystemTableClock.Instance;
                using (LocalTableStore settleStore = await LocalTableStore.OpenAsync(options, clock, null, CancellationToken.None))
                {
                    var facts = new LocalSnapshotFacts(LocalHostEnvironment.System, clock);
                    var settleRound = facts.CreateInitialRound(options);
                    TableCheckpoint checkpoint = facts.CreateInitialCheckpoint(options, settleRound);
                    await settleStore.CreateRoomAsync(checkpoint, CancellationToken.None);

                    byte[] outboxPayload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
                    var outboxHash = TablePayloadSha256.Compute(outboxPayload);
                    var outboxRecord = new TableSettlementOutboxRecord(
                        "match-settle-1",
                        1,
                        outboxPayload,
                        outboxHash,
                        (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        publishedAtUtcMs: null);

                    TableLeaseSnapshot? lease = await settleStore.GetAsync(roomId, CancellationToken.None);
                    TableStateRow? stateRow = await settleStore.LoadStateAsync(roomId, CancellationToken.None);

                    var timing = TableTimingCheckpointCodec.Decode(checkpoint.TimingPayload);
                    var cmdEvent = new TableEventRecord(
                        new EventSeq(1),
                        new StateVersion(1),
                        0,
                        RoundEventType.RoundEnded,
                        0,
                        null,
                        "settle",
                        1,
                        Array.Empty<byte>());

                    var cmdJournal = new TableCommandJournalRecord(
                        new StateVersion(1),
                        stateRow!.StateVersion,
                        new RequestId("crash-settle-req"),
                        "local-" + roomId + "-seat0",
                        new RequestScopeId("crash-settle-scope"),
                        "DECLARE_TSUMO",
                        outboxHash,
                        outboxPayload);

                    var commit = new TableDurableCommitRequest(
                        roomId,
                        options.OwnerInstanceId,
                        lease!.OwnerEpoch,
                        stateRow.StateVersion,
                        receipt: null,
                        internalCommand: true,
                        events: new[] { cmdEvent },
                        commandJournal: cmdJournal,
                        outbox: new[] { outboxRecord },
                        checkpoint: null,
                        nextStatePayload: checkpoint.StatePayload,
                        nextStateFingerprint: checkpoint.StateFingerprint == 0 ? 12345UL : checkpoint.StateFingerprint,
                        terminal: true,
                        timing: timing,
                        timingVersion: checkpoint.TimingVersion + 1L);

                    TableDurableCommitResult result = await settleStore.CommitAsync(commit, CancellationToken.None);
                    if (!result.IsCommitted)
                    {
                        Console.Error.WriteLine("Settlement commit failed: " + result.ReasonCode + " " + result.Detail);
                        return 6;
                    }

                    File.WriteAllLines(marker, new[]
                    {
                        "match-settle-1",
                        "1",
                        outboxHash.ToHex(),
                        Convert.ToHexString(outboxPayload),
                        "unpublished"
                    });

                    // Real kill without dispose
                    System.Diagnostics.Process.GetCurrentProcess().Kill();
                    return 0;
                }
            }

            if (mode == "cheat")
            {
                var clock = SystemTableClock.Instance;
                using (LocalTableStore cheatStore = await LocalTableStore.OpenAsync(options, clock, null, CancellationToken.None))
                {
                    var facts = new LocalSnapshotFacts(LocalHostEnvironment.System, clock);
                    var initialRound = facts.CreateInitialRound(options);
                    TableCheckpoint initialCheckpoint = facts.CreateInitialCheckpoint(options, initialRound);
                    await cheatStore.CreateRoomAsync(initialCheckpoint, CancellationToken.None);

                    long now = clock.UtcNowMs;
                    ulong nowUtc = (ulong)now;
                    string accountId = "local-" + roomId + "-seat0";
                    var scopeId = new RequestScopeId("crash-cheat-scope");
                    cheatStore.IssueScope(
                        accountId,
                        TableScopeRole.Player,
                        roomId,
                        issuedAtUtcMs: nowUtc - 1000UL,
                        expiresAtUtcMs: nowUtc + 3600000UL,
                        scopeId: scopeId.Value);

                    TableLeaseAcquireResult acquireRes = await cheatStore.AcquireAsync(roomId, options.OwnerInstanceId, TimeSpan.FromMinutes(5), CancellationToken.None);
                    TableLeaseSnapshot lease = acquireRes.Lease!;

                    // 1. Physical tile swap: seat 0 hand tile with live wall slot 53
                    TilePositionMap cwTiles = initialRound.Tiles;
                    IReadOnlyList<TileId> handTiles = cwTiles.TilesInZone(TileZone.Hand, 0);
                    TileId handTile = handTiles[0];
                    TransitionResult swapRes = cwTiles.SwapHandWithWall(0, handTile, 53);
                    if (!swapRes.IsSuccess)
                    {
                        Console.Error.WriteLine("SwapHandWithWall failed: " + swapRes.Error);
                        return 7;
                    }
                    TilePositionMap tilesSwapped = swapRes.RequireState();

                    // 2. CheatLedger deductions: -25 for sleeve, -15 for beam (seat 0: 100 - 40 = 60)
                    TableCheatLedgerCheckpoint initialCheatCp = TableCheatLedgerCheckpointCodec.Decode(initialCheckpoint.CheatPayload!);
                    CheatLedger ledger = initialCheatCp.Restore();
                    CheatLedgerApplyResult lr1 = ledger.Apply("entry-cw-sleeve", 0, -25, "cheat:sleeve-bloom");
                    ledger = lr1.Ledger!;
                    CheatLedgerApplyResult lr2 = ledger.Apply("entry-cw-beam", 0, -15, "cheat:beam-swap");
                    ledger = lr2.Ledger!;

                    // 3. Tricks mutations
                    var group1 = new TileMarkGroup("group-cw-1", 0, new[] { new TileId(10), new TileId(11), new TileId(12), new TileId(13) }, initialRound.Spec.RoundId, 1);
                    var marksState = TileMarkState.Restore(1L, 1, new[] { initialRound.Spec.RoundId }, Array.Empty<string>(), new[] { group1 });
                    var res2 = new SleeveBloomReservation(0, "sleeve-cw-1", new TileId(20), "win-cw", 1000L);
                    var sleeveState = SleeveBloomState.Restore(null, Array.Empty<SleeveBloomOffer>(), new[] { res2 }, new string?[4], 1, 0, 1L);
                    var cand3 = new BeamSwapCandidate(handTile, BeamSwapSlot.Wall(53));
                    var offer3 = new BeamSwapOffer("beam-cw-1", 0, BeamSwapWindow.Wall, new[] { cand3 }, new[] { new TileId(40) }, new TileId(50), 0);
                    var beamState = BeamSwapState.Restore(offer3, 0, 1L);
                    var cheatState = new CheatState(ledger, marksState, sleeveState, beamState);

                    BusinessPayload payloadMarks = CommandPayloadFactory.SubmitTileMark(initialRound.Spec.RoundId, "dec-cw-1", 0UL, new uint[] { 10, 11, 12, 13 });
                    BusinessPayload payloadSleeve = CommandPayloadFactory.SubmitSleeveBloom(initialRound.Spec.RoundId, "dec-cw-2", 0UL);
                    BusinessPayload payloadBeam = CommandPayloadFactory.SubmitBeamSwap(initialRound.Spec.RoundId, "dec-cw-3", 0UL);
                    byte[] marksBytes = payloadMarks.CopyBytes();
                    byte[] sleeveBytes = payloadSleeve.CopyBytes();
                    byte[] beamBytes = payloadBeam.CopyBytes();

                    // 4. Construct round and checkpoint
                    RoundStateMachine roundSwapped = CreateRoundWithEvents(
                        initialRound.Spec,
                        1L,
                        RoundPhase.CheatWindow,
                        0,
                        tilesSwapped,
                        initialRound.Events);

                    var cheatEvent = new TableCheatEventRecord(
                        CheatEvent.BeamSwapResolved(0, "beam-cw-1", BeamSwapAction.Swap),
                        "beam-cw-1",
                        null,
                        BeamSwapAction.Swap,
                        handTile,
                        handTile,
                        BeamSwapSlot.Wall(53),
                        1);

                    byte[] eventFactPayload = TableEventFactCodec.Encode(new StateVersion(1UL), 0, roundSwapped.SourceFingerprint);
                    var cmdEvent = new TableEventRecord(
                        new EventSeq(1UL),
                        new StateVersion(1UL),
                        0,
                        RoundEventType.CheatApplied,
                        0,
                        null,
                        "CHEAT_BEAM_SWAP",
                        1,
                        eventFactPayload,
                        cheatEvent);

                    var binding = new TableCommandBinding(1UL, 1, roundSwapped.SourceFingerprint);

                    TableTimingCheckpoint initialTiming = TableTimingCheckpointCodec.Decode(initialCheckpoint.TimingPayload);
                    TableTimingCheckpoint timing1 = new TableTimingCheckpoint(
                        initialTiming.RoundId,
                        initialTiming.SeatReservesMillis,
                        initialTiming.Decisions,
                        nowUtc,
                        initialTiming.CapturedMonotonicMillis,
                        initialTiming.BootId);

                    TableProjectionContextData factsData = facts.Capture(new TableProjectionContextRequest(
                        roomId,
                        roundSwapped,
                        new StateVersion(1UL),
                        new EventSeq(1UL),
                        lease.OwnerEpoch,
                        0L,
                        initialCheckpoint.TimingVersion + 1L,
                        0L,
                        now,
                        new[] { cmdEvent }));
                    var cheatFacts = new TableProjectionEventFacts(
                        turnNumber: 1U,
                        offerId: "beam-cw-1",
                        beamSwapResolution: ProjectionBeamSwapResolution.Swap,
                        handTileId: (uint)handTile.Value,
                        targetSlot: 53U);
                    var cheatInput = TableEventProjection.FromRoundEvent(cmdEvent)!;
                    var eventFact = new TableProjectionEventFact(
                        cheatInput.Type,
                        cmdEvent.Seq,
                        cmdEvent.CommandVersion,
                        (uint)cmdEvent.Ordinal,
                        cheatFacts);

                    var mergedFactsData = new TableProjectionContextData(
                        factsData.RoundId,
                        factsData.StateVersion,
                        factsData.SessionVersion,
                        cheatState,
                        factsData.PublicFacts,
                        factsData.PrivateFacts,
                        factsData.Session,
                        factsData.Policy,
                        new[] { eventFact });
                    byte[] factsPayload = TableProjectionFactsCodec.Encode(mergedFactsData);

                    TableCheatLedgerCheckpoint cheatCp1 = TableCheatLedgerCheckpoint.FromCheatState(cheatState);

                    TableCheckpoint cp1 = TableCheckpointFactory.Create(
                        roomId,
                        roundSwapped,
                        new StateVersion(1UL),
                        new EventSeq(1UL),
                        TableControlCheckpointCodec.Decode(initialCheckpoint.ControlPayload),
                        timing1,
                        options.RulesetVersion,
                        initialCheckpoint.BootId,
                        nowUtc,
                        cheatCp1,
                        factsPayload,
                        initialCheckpoint.TimingVersion + 1L,
                        0L,
                        new[] { cmdEvent },
                        new[] { binding });

                    var requestId = new RequestId("crash-cheat-req");
                    byte[] cheatCmdPayload = payloadBeam.CopyBytes();
                    var receiptDigest = TablePayloadSha256.Compute(cheatCmdPayload);
                    var receipt = new TableDurableReceipt(
                        accountId,
                        scopeId,
                        requestId,
                        "CHEAT_BEAM_SWAP",
                        receiptDigest,
                        Receipt.Applied(
                            new StateVersion(0UL),
                            new StateVersion(1UL),
                            new EventSeq(1UL),
                            null,
                            nowUtc),
                        string.Empty,
                        nowUtc,
                        nowUtc + 600000UL);

                    var cmdJournal = new TableCommandJournalRecord(
                        new StateVersion(1UL),
                        new StateVersion(0UL),
                        requestId,
                        accountId,
                        scopeId,
                        "CHEAT_BEAM_SWAP",
                        receiptDigest,
                        cheatCmdPayload);

                    var commitReq = new TableDurableCommitRequest(
                        roomId,
                        options.OwnerInstanceId,
                        lease.OwnerEpoch,
                        new StateVersion(0UL),
                        receipt,
                        false,
                        new[] { cmdEvent },
                        cmdJournal,
                        Array.Empty<TableSettlementOutboxRecord>(),
                        cp1,
                        cp1.StatePayload,
                        cp1.StateFingerprint,
                        false,
                        timing1,
                        initialCheckpoint.TimingVersion + 1L);

                    TableDurableCommitResult commitRes = await cheatStore.CommitAsync(commitReq, CancellationToken.None);
                    if (!commitRes.IsCommitted)
                    {
                        Console.Error.WriteLine("Cheat commit failed: " + commitRes.ReasonCode + " " + commitRes.Detail);
                        return 8;
                    }

                    // Export marker file
                    var markerLines = new List<string>
                    {
                        scopeId.Value,                                          // 0
                        requestId.Value,                                        // 1
                        "1",                                                    // 2: committed version
                        accountId,                                              // 3
                        receiptDigest.ToHex(),                                  // 4
                        $"{ledger.BalanceOf(0)},{ledger.BalanceOf(1)},{ledger.BalanceOf(2)},{ledger.BalanceOf(3)}", // 5
                        TilePositionMap.Validate(tilesSwapped).StateHash.ToString(System.Globalization.CultureInfo.InvariantCulture), // 6
                        receipt.Api,                                            // 7
                        receipt.Receipt.EvaluatedStateVersion.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), // 8
                        receipt.Receipt.CommandVersion!.Value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), // 9
                        receipt.Receipt.EventSeq!.Value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),        // 10
                        receipt.CreatedAtUtcMs.ToString(System.Globalization.CultureInfo.InvariantCulture),                        // 11
                        receipt.ExpiresAtUtcMs.ToString(System.Globalization.CultureInfo.InvariantCulture),                        // 12
                        Convert.ToHexString(marksBytes),                        // 13
                        Convert.ToHexString(sleeveBytes),                       // 14
                        Convert.ToHexString(beamBytes),                         // 15
                        ledger.Entries.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)                         // 16
                    };
                    for (int i = 0; i < 136; i++)
                    {
                        TilePosition pos = tilesSwapped[i];
                        markerLines.Add($"{i}:{(int)pos.Zone}:{pos.Index}");
                    }
                    File.WriteAllLines(marker, markerLines);

                    // Kill without dispose
                    System.Diagnostics.Process.GetCurrentProcess().Kill();
                    return 0;
                }
            }

            ITableChannel channel = await LocalTableHost.CreateChannelAsync(options);
            string channelTicket = LocalTableHost.IssueTicket(options);
            await channel.OpenAsync(new TableChannelConnectRequest(channelTicket, "crash-worker", null), CancellationToken.None);
            var session = new TableSession(channel, new InMemoryOutboxStore());
            string sessionTicket = LocalTableHost.IssueTicket(options);
            await session.OpenAsync(new TableSessionOpenRequest(sessionTicket, "crash-worker"), CancellationToken.None);
            for (int i = 0; i < 50 && session.State != TableConnectionState.Ready; i++)
            {
                await session.PumpOnceAsync(CancellationToken.None);
            }

            if (session.State != TableConnectionState.Ready)
            {
                Console.Error.WriteLine("worker not ready: " + session.LastError);
                return 3;
            }

            PlayerSnapshot baseline = session.PlayerSnapshot!;
            BusinessPayload payload = CommandPayloadFactory.SubmitAction(
                baseline.RoundId,
                "local-direct",
                0UL,
                ProtocolActionType.DiscardTile,
                tileIds: new[] { baseline.PrivateView.ClosedTiles[0] });
            var command = new ProtocolCommand(
                CommandKind.SubmitAction,
                payload,
                new RequestId("crash-request-1"),
                session.Binding!.Scope.ScopeId,
                new StateVersion(baseline.StateVersion),
                new RoundId(baseline.RoundId),
                decisionId: null,
                controlEpoch: new ControlEpoch(0));
            CommandSubmission submission = await session.SubmitAsync(command, CancellationToken.None);
            if (!submission.Sent)
            {
                Console.Error.WriteLine("worker command not sent");
                return 4;
            }

            // Wait until the commit is observable; then die without any dispose/close path.
            for (int i = 0; i < 200 && session.PlayerSnapshot!.StateVersion == baseline.StateVersion; i++)
            {
                await Task.Delay(10);
                if (i % 2 == 0 && session.State == TableConnectionState.Ready)
                {
                    try
                    {
                        await session.PumpOnceAsync(CancellationToken.None);
                    }
                    catch
                    {
                        break;
                    }
                }
            }

            if (session.PlayerSnapshot!.StateVersion == baseline.StateVersion)
            {
                Console.Error.WriteLine("worker commit not observed");
                return 5;
            }

            for (int i = 0; i < 500 && session.PendingCommands.Count > 0; i++)
            {
                if (session.State == TableConnectionState.Ready)
                {
                    try
                    {
                        await session.PumpOnceAsync(CancellationToken.None);
                    }
                    catch
                    {
                        break;
                    }
                }

                await Task.Delay(10);
            }

            var localChannel = channel as LocalTableChannel;
            var runtimeField = typeof(LocalTableChannel).GetField("_runtime", BindingFlags.NonPublic | BindingFlags.Instance);
            var runtime = runtimeField?.GetValue(localChannel);
            var driverField = runtime?.GetType().GetField("_driver", BindingFlags.NonPublic | BindingFlags.Instance);
            var driver = driverField?.GetValue(runtime) as LocalTableDriver;
            if (driver != null)
            {
                await driver.StopAsync();
            }

            var actorField = runtime?.GetType().GetField("_actor", BindingFlags.NonPublic | BindingFlags.Instance);
            var actor = actorField?.GetValue(runtime) as TableActor;
            var storeField = runtime?.GetType().GetField("_store", BindingFlags.NonPublic | BindingFlags.Instance);
            var store = storeField?.GetValue(runtime) as LocalTableStore;

            var saveField = typeof(LocalTableStore).GetField("_save", BindingFlags.NonPublic | BindingFlags.Instance);
            var save = saveField?.GetValue(store) as LocalSave;

            TilePositionMap? tiles = null;
            if (save != null)
            {
                for (int i = save.Operations.Count - 1; i >= 0; i--)
                {
                    if (save.Operations[i].NextStatePayload != null)
                    {
                        RoundRecoveryState roundState = RoundRecoveryCodec.Decode(save.Operations[i].NextStatePayload!);
                        tiles = roundState.Tiles;
                        break;
                    }
                }

                if (tiles == null && save.BaseCheckpointBlob != null)
                {
                    TableCheckpoint cpBase = LocalSaveCodec.DecodeCheckpoint(save.BaseCheckpointBlob);
                    RoundRecoveryState roundState = RoundRecoveryCodec.Decode(cpBase.StatePayload);
                    tiles = roundState.Tiles;
                }
            }

            if (tiles == null)
            {
                var round = actor?.GetRoundStateForHost();
                tiles = round?.Tiles;
            }

            var expectedDigest = TablePayloadSha256.Compute(payload.CopyBytes());

            TableCheckpoint? cp = store != null ? await store.LoadCheckpointAsync(roomId, CancellationToken.None) : null;
            TableCheatLedgerCheckpoint? cheatCp = cp?.CheatPayload != null ? TableCheatLedgerCheckpointCodec.Decode(cp.CheatPayload) : null;
            long b0 = cheatCp?.Balances.Count > 0 ? cheatCp.Balances[0] : 0L;
            long b1 = cheatCp?.Balances.Count > 1 ? cheatCp.Balances[1] : 0L;
            long b2 = cheatCp?.Balances.Count > 2 ? cheatCp.Balances[2] : 0L;
            long b3 = cheatCp?.Balances.Count > 3 ? cheatCp.Balances[3] : 0L;

            TableStateRow? authorState = store != null ? await store.LoadStateAsync(roomId, CancellationToken.None) : null;
            ulong authorVersion = authorState?.StateVersion.Value ?? session.PlayerSnapshot.StateVersion;

            string rKey = "local-" + roomId + "-seat0\u001f" + session.Binding!.Scope.ScopeId.Value + "\u001fcrash-request-1";
            LocalDurableOperation? lastOp = save?.Operations.LastOrDefault(o => o.Receipt != null && o.Receipt.RequestId.Value == "crash-request-1");
            TableDurableReceipt? actionReceipt = (save != null && save.Receipts.TryGetValue(rKey, out TableDurableReceipt? r)) ? r : lastOp?.Receipt;

            var lines = new List<string>
            {
                session.Binding!.Scope.ScopeId.Value,
                "crash-request-1",
                authorVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "local-" + roomId + "-seat0",
                expectedDigest.ToHex(),
                $"{b0},{b1},{b2},{b3}",
                tiles != null ? TilePositionMap.Validate(tiles).StateHash.ToString(System.Globalization.CultureInfo.InvariantCulture) : "0"
            };

            if (actionReceipt != null)
            {
                lines.Add(actionReceipt.Api);
                lines.Add(actionReceipt.Receipt.EvaluatedStateVersion.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                lines.Add(actionReceipt.Receipt.CommandVersion!.Value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                lines.Add(actionReceipt.Receipt.EventSeq!.Value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                lines.Add(actionReceipt.CreatedAtUtcMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
                lines.Add(actionReceipt.ExpiresAtUtcMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            if (tiles != null)
            {
                for (int i = 0; i < 136; i++)
                {
                    TilePosition pos = tiles[i];
                    lines.Add($"{i}:{(int)pos.Zone}:{pos.Index}");
                }
            }

            File.WriteAllLines(marker, lines);

            // Real crash: no actor close, no channel close, no store dispose.
            System.Diagnostics.Process.GetCurrentProcess().Kill();
            return 0;
        }

        private static string BoundaryLedgerDigest(TableCheatLedgerCheckpoint cheat)
        {
            if (cheat.Entries.Count == 0)
            {
                return "-";
            }

            var text = new System.Text.StringBuilder();
            for (int i = 0; i < cheat.Entries.Count; i++)
            {
                CheatLedgerEntry entry = cheat.Entries[i];
                text.Append(entry.EntryId).Append('|')
                    .Append(entry.Seat.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                    .Append(entry.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                    .Append(entry.BalanceAfter.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                    .Append(entry.Reason).Append('\n');
            }

            return TablePayloadSha256.Compute(System.Text.Encoding.UTF8.GetBytes(text.ToString())).ToHex();
        }

        private static string BoundaryEventSummary(IReadOnlyList<TableEventRecord> events)
        {
            return string.Join(
                ";",
                events
                    .GroupBy(e => e.CommandVersion.Value)
                    .OrderBy(g => g.Key)
                    .Select(g => "v" + g.Key.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" +
                                 string.Join(
                                     ",",
                                     g.OrderBy(e => e.Ordinal)
                                         .Select(e => e.Type + "/" + e.Seat.ToString(System.Globalization.CultureInfo.InvariantCulture)))));
        }

        private static string BoundaryEventDigest(IReadOnlyList<TableEventRecord> events)
        {
            var text = new System.Text.StringBuilder();
            foreach (TableEventRecord record in events.OrderBy(e => e.Seq.Value))
            {
                text.Append(record.Seq.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                    .Append(record.CommandVersion.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                    .Append(record.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                    .Append(((int)record.Type).ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                    .Append(record.Seat.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                    .Append((record.Tile?.Value ?? -1).ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                    .Append(record.Detail ?? string.Empty).Append('|')
                    .Append(record.EventSchema.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                    .Append(record.HasPayload
                        ? TablePayloadSha256.Compute(record.Payload!).ToHex()
                        : "-")
                    .Append('\n');
            }

            return TablePayloadSha256.Compute(System.Text.Encoding.UTF8.GetBytes(text.ToString())).ToHex();
        }

        private static RoundStateMachine CreateRoundWithEvents(
            RoundSpec spec,
            long version,
            RoundPhase phase,
            int currentSeat,
            TilePositionMap tiles,
            IReadOnlyList<RoundEvent> events,
            IReadOnlyList<long>? scores = null,
            int? riichiSticks = null,
            IReadOnlyList<bool>? riichiDeclared = null,
            IReadOnlyList<RoundMeld>? melds = null,
            int pendingKanDoraCount = 0)
        {
            var dummyState = new RoundRecoveryState(
                spec, version, phase, currentSeat, tiles, null, null, events,
                sourceFingerprint: 0UL,
                scores, riichiSticks, riichiDeclared, melds, pendingKanDoraCount);
            ulong realFingerprint = 0UL;
            try
            {
                RoundRecovery.Restore(dummyState);
            }
            catch (InvalidDataException ex) when (ex.Message.StartsWith("Recovered round fingerprint "))
            {
                string[] parts = ex.Message.Split(' ');
                realFingerprint = ulong.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture);
            }

            var realState = new RoundRecoveryState(
                spec, version, phase, currentSeat, tiles, null, null, events,
                realFingerprint,
                scores, riichiSticks, riichiDeclared, melds, pendingKanDoraCount);
            return RoundRecovery.Restore(realState);
        }
    }
}
