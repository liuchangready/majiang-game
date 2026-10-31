using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Mahjong.LocalHost;
using Mahjong.TableRuntime;
using PlayerSnapshot = Mahjong.Contracts.Wire.PlayerSnapshot;
using WireCodec = Mahjong.Contracts.Wire.WireCodec;
using WireCommand = Mahjong.Contracts.Wire.Command;
using WireCommandType = Mahjong.Contracts.Wire.CommandType;
using WireEnvelope = Mahjong.Contracts.Wire.Envelope;
using WireHello = Mahjong.Contracts.Wire.Hello;
using WireMessageType = Mahjong.Contracts.Wire.MessageType;
using WireReceiptStatus = Mahjong.Contracts.Wire.ReceiptStatus;
using WireLookupStatus = Mahjong.Contracts.Wire.LookupStatus;
using WireScope = Mahjong.Contracts.Wire.RequestScope;

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

            if (mode == "backup-recovery-read")
            {
                // Cold-process evidence for the missing-primary/valid-previous-copy decision.
                // The child opens the real runtime (which performs the production recovery and
                // publication path) and reports the durable state, round identity, 136-tile
                // hash, and the complete receipt set. It never submits an action or edits a save.
                string primaryPath = Path.Combine(saveDir, roomId + ".save");
                string previousPath = primaryPath + ".prev";
                bool primaryPresentBefore = File.Exists(primaryPath);
                bool previousPresentBefore = File.Exists(previousPath);
                var backupLines = new List<string>
                {
                    "mode=backup-recovery-read",
                    "primary_present_before=" + primaryPresentBefore,
                    "backup_present_before=" + previousPresentBefore,
                    "backup_sha256_before=" + FileSha256OrAbsent(previousPath)
                };
                await using (LocalTableRuntime backupRuntime = await LocalTableRuntime.OpenAsync(
                    options,
                    CancellationToken.None))
                {
                    LocalTableStore backupStore = backupRuntime.StoreForTest;
                    TableStateRow backupState = (await backupStore.LoadStateAsync(
                        roomId,
                        CancellationToken.None))!;
                    RoundStateMachine backupRound = backupRuntime.ActorForTest.GetRoundStateForHost();
                    ConservationReport backupConservation = TilePositionMap.Validate(backupRound.Tiles);
                    var backupSaveField = typeof(LocalTableStore).GetField(
                        "_save",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    var backupSave = backupSaveField?.GetValue(backupStore) as LocalSave;
                    if (backupSave == null)
                    {
                        Console.Error.WriteLine("backup-recovery-read loaded store has no LocalSave.");
                        return 6;
                    }

                    backupLines.Add("state_version=" + backupState.StateVersion.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    backupLines.Add("last_event_seq=" + backupState.LastEventSeq.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    backupLines.Add("state_fingerprint=" + backupState.StateFingerprint.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    backupLines.Add("state_hash=" + backupState.StateHash.ToHex());
                    backupLines.Add("round_id=" + backupRound.RoundId);
                    backupLines.Add("tiles_hash=" + backupConservation.StateHash.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    backupLines.Add("tiles_assigned=" + backupConservation.AssignedCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    backupLines.Add("receipt_count=" + backupSave.Receipts.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    backupLines.Add("receipt_set_sha256=" + ReceiptSetDigest(backupSave.Receipts));
                }

                backupLines.Add("primary_present_after=" + File.Exists(primaryPath));
                backupLines.Add("backup_present_after=" + File.Exists(previousPath));
                backupLines.Add("primary_sha256=" + FileSha256OrAbsent(primaryPath));
                backupLines.Add("backup_sha256=" + FileSha256OrAbsent(previousPath));
                File.WriteAllLines(marker, backupLines);
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

            if (mode == "rejected-receipt-cold-session")
            {
                // Cold-process evidence for the durable stale-decision rejection
                // (D-LH-GATE-02): a separate OS process (1) reads the committed
                // save through the real store, (2) recovers the real LocalHost
                // runtime from that save, (3) queries the persisted rejection over
                // the real wire for the pre-restart scope, (4) replays the same
                // request id with the same business bytes and kind through the
                // real wire under the freshly issued scope and observes
                // Replayed=true with a single durable record, and (5) reconciles
                // the real client FileOutboxStore with a real TableSession until
                // PendingCommands and LoadUnresolvedAsync are empty. The autonomous
                // driver stays disabled (AutoPlay=false) and the worker clock is
                // frozen, so every durable movement in this process is attributable
                // to the rejection/replay/reconciliation path under test rather
                // than to autonomous round advancement.
                if (args.Length < 11)
                {
                    Console.Error.WriteLine(
                        "rejected-receipt-cold-session requires <account> <scopeId> <requestId> <decisionId> <outboxPath> after the clock.");
                    return 2;
                }

                string rejectionAccount = args[6];
                string rejectionScope = args[7];
                string rejectionRequest = args[8];
                string retiredDecisionId = args[9];
                string outboxPath = args[10];
                ITableClock rejectionClock = options.Environment?.ResolveClock() ?? SystemTableClock.Instance;
                var failures = new List<string>();
                // Per-step durable state trace: records where the cold runtime's
                // own committed cut stands after each phase, so an exactness
                // failure pinpoints the step that moved state.
                var traceState = new List<string>();
                var movedEventLines = new List<string>();
                var movedCommandLines = new List<string>();

                // (1) Cold-start disk truth, read through the real store before any
                // runtime exists in this process.
                ulong coldEvaluated;
                ulong coldCreated;
                ulong coldExpires;
                ulong startStateVersion;
                ulong startLastEventSeq;
                string startDigest;
                bool coldCommandVersionPresent;
                bool coldEventSeqPresent;
                using (LocalTableStore rejectionStore = await LocalTableStore.OpenAsync(
                    options,
                    rejectionClock,
                    null,
                    CancellationToken.None))
                {
                    TableDurableReceipt? rejection = await rejectionStore.TryGetAsync(
                        rejectionAccount,
                        new RequestScopeId(rejectionScope),
                        new RequestId(rejectionRequest),
                        CancellationToken.None);
                    if (rejection == null)
                    {
                        Console.Error.WriteLine("rejected-receipt-cold-session receipt not found");
                        return 5;
                    }

                    TableStateRow? rejectionState = await rejectionStore.LoadStateAsync(
                        roomId,
                        CancellationToken.None);
                    LocalControlRecord? rejectionControl = rejectionStore.GetControlResult(
                        rejectionAccount,
                        new RequestScopeId(rejectionScope),
                        new RequestId(rejectionRequest));
                    startDigest = rejectionStore.GetReceiptDigest(
                        rejectionAccount,
                        new RequestScopeId(rejectionScope),
                        new RequestId(rejectionRequest)) ?? string.Empty;
                    coldEvaluated = rejection.Receipt.EvaluatedStateVersion.Value;
                    coldCreated = rejection.CreatedAtUtcMs;
                    coldExpires = rejection.ExpiresAtUtcMs;
                    coldCommandVersionPresent = rejection.Receipt.CommandVersion.HasValue;
                    coldEventSeqPresent = rejection.Receipt.EventSeq.HasValue;
                    startStateVersion = rejectionState?.StateVersion.Value ?? 0UL;
                    startLastEventSeq = rejectionState?.LastEventSeq.Value ?? 0UL;
                    if (rejectionControl != null)
                    {
                        failures.Add("the original rejection must not carry a LOCAL_CONTROL record");
                    }

                    if (rejectionStore.ControlVersionForTest != 0L)
                    {
                        failures.Add("control version must stay zero at the cold start");
                    }
                }

                string coldScopeId = string.Empty;
                string coldRoundId = string.Empty;
                string coldPendingDecision = string.Empty;
                long recoveryGeneration = 0;
                string queryStatus = string.Empty;
                string queryReceiptStatus = string.Empty;
                string queryReceiptReason = string.Empty;
                string queryDigest = string.Empty;
                string newKey1Status = string.Empty;
                string newKey1Reason = string.Empty;
                string newKey1Digest = string.Empty;
                string newKey1Evaluated = string.Empty;
                bool newKey1Replayed = false;
                string newKey2Reason = string.Empty;
                string newKey2Digest = string.Empty;
                string newKey2Evaluated = string.Empty;
                bool newKey2Replayed = false;
                ulong newKeyRecordCreated = 0UL;
                ulong newKeyRecordExpires = 0UL;
                ulong originalCreatedAfter = 0UL;
                string originalStatusAfter = string.Empty;
                string originalReasonAfter = string.Empty;
                string originalDigestAfter = string.Empty;
                string originalEvaluatedAfter = string.Empty;
                string query2Status = string.Empty;
                string query2ReceiptStatus = string.Empty;
                string query2Reason = string.Empty;
                string query2Digest = string.Empty;
                string query2Evaluated = string.Empty;
                ulong finalStateVersion = 0UL;
                ulong finalLastEventSeq = 0UL;
                long finalControlVersion = -1L;
                bool finalControlRecordPresent = true;
                string clientDisposition = string.Empty;
                string clientReason = string.Empty;
                string clientDigest = string.Empty;
                string clientKind = string.Empty;
                bool clientFromLookup = false;
                int clientPending = -1;
                int clientUnresolved = -1;

                ITableChannel? coldChannel = null;
                TableSession? coldSession = null;
                try
                {
                    // (2) Real LocalHost recovery from the committed save. The
                    // channel exposes its runtime only after the open handshake,
                    // so the recovery checks run once the connection is up.
                    coldChannel = await LocalTableHost.CreateChannelAsync(options, CancellationToken.None);
                    string coldChannelTicket = LocalTableHost.IssueTicket(options);
                    await coldChannel.OpenAsync(
                        new TableChannelConnectRequest(coldChannelTicket, "crash-worker", null),
                        CancellationToken.None);
                    if (coldChannel is not LocalTableChannel coldLocalChannel || coldLocalChannel.RuntimeForMatch == null)
                    {
                        Console.Error.WriteLine("rejected-receipt-cold-session runtime is not exposed by the channel");
                        return 4;
                    }

                    LocalTableRuntime coldRuntime = coldLocalChannel.RuntimeForMatch;
                    recoveryGeneration = coldRuntime.ActorForTest.RecoveryGeneration;
                    if (recoveryGeneration <= 0)
                    {
                        failures.Add("the worker runtime must be a cold recovery, not a fresh room");
                    }

                    async Task TraceStateAsync(string step)
                    {
                        TableStateRow? traceRow = await coldRuntime.StoreForTest.LoadStateAsync(
                            roomId,
                            CancellationToken.None);
                        traceState.Add(
                            step + ":v" +
                            (traceRow?.StateVersion.Value ?? 0UL).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                            ":e" +
                            (traceRow?.LastEventSeq.Value ?? 0UL).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }

                    await TraceStateAsync("open");

                    // Raw HELLO binds this connection; the freshly issued scope is
                    // the only scope command envelopes may carry (a pre-restart
                    // scope is accepted only by explicit receipt queries).
                    var hello = new WireEnvelope
                    {
                        ProtocolMajor = ProtocolConstants.Major,
                        ProtocolMinor = ProtocolConstants.Minor,
                        RulesetVersion = ProtocolConstants.RulesetVersion,
                        ProjectionVersion = ProtocolConstants.ProjectionVersion,
                        MessageType = WireMessageType.Hello,
                        BindingEpoch = 1UL,
                        CorrelationId = "cold-hello",
                        Hello = new WireHello
                        {
                            Ticket = coldChannelTicket,
                            ProtocolMajor = ProtocolConstants.Major,
                            ProtocolMinor = ProtocolConstants.Minor,
                            RulesetVersion = ProtocolConstants.RulesetVersion,
                            ProjectionVersion = ProtocolConstants.ProjectionVersion,
                            ClientBuild = "crash-worker",
                            ResumeRequested = false
                        }
                    };
                    await coldChannel.SendAsync(WireCodec.Encode(hello), CancellationToken.None);
                    WireEnvelope welcome = await WorkerReceiveUntilAsync(
                        coldChannel,
                        envelope => envelope.Welcome != null,
                        "the cold WELCOME");
                    coldScopeId = welcome.Welcome!.Scope.ScopeId;
                    coldRoundId = coldRuntime.ActorForTest.GetRoundStateForHost().RoundId;
                    coldPendingDecision = coldRuntime.ActorForTest.GetPendingDecisionId(0) ?? string.Empty;
                    if (string.Equals(coldPendingDecision, retiredDecisionId, StringComparison.Ordinal))
                    {
                        failures.Add("the recovered pending decision must differ from the retired decision id");
                    }

                    await TraceStateAsync("hello");

                    // (3) Real wire receipt query for the PRE-RESTART scope: the
                    // persisted rejection must answer with its original outcome.
                    var query = new WireEnvelope
                    {
                        ProtocolMajor = ProtocolConstants.Major,
                        ProtocolMinor = ProtocolConstants.Minor,
                        RulesetVersion = ProtocolConstants.RulesetVersion,
                        ProjectionVersion = ProtocolConstants.ProjectionVersion,
                        MessageType = WireMessageType.ReceiptQuery,
                        SessionId = welcome.SessionId,
                        RoomId = roomId,
                        SeatId = welcome.SeatId,
                        BindingEpoch = welcome.BindingEpoch,
                        CorrelationId = "cold-receipt-query",
                        ReceiptQuery = new Mahjong.Contracts.Wire.ReceiptQuery
                        {
                            RequestScopeId = rejectionScope,
                            RequestId = rejectionRequest
                        }
                    };
                    await coldChannel.SendAsync(WireCodec.Encode(query), CancellationToken.None);
                    WireEnvelope lookup = await WorkerReceiveUntilAsync(
                        coldChannel,
                        envelope => envelope.ReceiptLookup != null &&
                                    string.Equals(envelope.CorrelationId, "cold-receipt-query", StringComparison.Ordinal),
                        "the cold receipt lookup");
                    queryStatus = lookup.ReceiptLookup!.Status.ToString();
                    if (lookup.ReceiptLookup.Receipt != null)
                    {
                        queryReceiptStatus = lookup.ReceiptLookup.Receipt.Status.ToString();
                        queryReceiptReason = lookup.ReceiptLookup.Receipt.ReasonCode;
                        queryDigest = lookup.ReceiptLookup.CanonicalPayloadSha256;
                    }

                    if (!string.Equals(queryStatus, WireLookupStatus.Found.ToString(), StringComparison.Ordinal) ||
                        !string.Equals(queryReceiptStatus, WireReceiptStatus.Rejected.ToString(), StringComparison.Ordinal) ||
                        !string.Equals(queryReceiptReason, "DECISION_MISMATCH", StringComparison.Ordinal))
                    {
                        failures.Add("the cold wire receipt query must find the persisted Rejected/DECISION_MISMATCH receipt");
                    }

                    if (!string.Equals(queryDigest, startDigest, StringComparison.Ordinal))
                    {
                        failures.Add("the cold wire receipt query must return the persisted canonical digest");
                    }

                    await TraceStateAsync("query1");

                    // (4) Cold-scope new-key submissions — an explicit ISOLATION
                    // negative, not an original-key replay. The idempotency key is
                    // (account, scope, request id): every Hello issues a fresh
                    // scope (HandleHelloAsync resolves a new scope id per
                    // connection and binds the connection to it), and the binding
                    // gate rejects any other scope pre-linearization, so an
                    // original-key COMMAND replay over the cold wire is
                    // unreachable by contract. The original key is covered on the
                    // cold wire by the explicit receipt query above (read side)
                    // and by the client outbox reconciliation below (which
                    // resolves the ORIGINAL outbox entry from that query); the
                    // write-side replay semantics (first replay Replayed=true on
                    // the same key) are covered warm by the parent's
                    // exact-replay test. What these two submissions prove is key
                    // isolation: the cold-issued scope under the same request id
                    // gets its own record and must not rewrite the original.
                    BusinessPayload replayPayload = CommandPayloadFactory.ClaimControl(coldRoundId, 0UL);
                    byte[] BuildNewKeyBytes()
                    {
                        var envelope = new WireEnvelope
                        {
                            ProtocolMajor = ProtocolConstants.Major,
                            ProtocolMinor = ProtocolConstants.Minor,
                            RulesetVersion = ProtocolConstants.RulesetVersion,
                            ProjectionVersion = ProtocolConstants.ProjectionVersion,
                            MessageType = WireMessageType.Command,
                            SessionId = welcome.SessionId,
                            RoomId = roomId,
                            SeatId = welcome.SeatId,
                            BindingEpoch = welcome.BindingEpoch,
                            RequestId = rejectionRequest,
                            Scope = welcome.Welcome!.Scope.Clone(),
                            ControlEpoch = 0UL,
                            OwnerEpoch = welcome.Welcome.OwnerEpoch,
                            Command = new WireCommand
                            {
                                Type = WireCommandType.ClaimControl,
                                CanonicalPayload = ByteString.CopyFrom(replayPayload.CopyBytes()),
                                PayloadSchemaVersion = 1U,
                                ExpectedStateVersion = 0UL,
                                ExpectedRoundId = coldRoundId,
                                ExpectedDecisionId = retiredDecisionId
                            }
                        };
                        return WireCodec.Encode(envelope);
                    }

                    byte[] newKeyBytes = BuildNewKeyBytes();
                    await coldChannel.SendAsync(newKeyBytes, CancellationToken.None);
                    WireEnvelope newKey1 = await WorkerReceiveUntilAsync(
                        coldChannel,
                        envelope => envelope.Receipt != null &&
                                    string.Equals(envelope.RequestId, rejectionRequest, StringComparison.Ordinal),
                        "the cold new-key first submission receipt");
                    newKey1Status = newKey1.Receipt!.Status.ToString();
                    newKey1Reason = newKey1.Receipt.ReasonCode;
                    newKey1Digest = newKey1.Receipt.CanonicalPayloadSha256;
                    newKey1Evaluated = newKey1.Receipt.EvaluatedStateVersion.ToString(
                        System.Globalization.CultureInfo.InvariantCulture);
                    newKey1Replayed = newKey1.Receipt.Replayed;
                    if (!string.Equals(newKey1Status, WireReceiptStatus.Rejected.ToString(), StringComparison.Ordinal) ||
                        !string.Equals(newKey1Reason, "DECISION_MISMATCH", StringComparison.Ordinal) ||
                        newKey1Replayed)
                    {
                        failures.Add("the cold-scope new-key submission must create its own fresh Rejected/DECISION_MISMATCH record");
                    }

                    if (!string.Equals(newKey1Digest, startDigest, StringComparison.Ordinal))
                    {
                        failures.Add("the cold-scope new-key submission must carry the same canonical digest as the persisted rejection");
                    }

                    await coldChannel.SendAsync(BuildNewKeyBytes(), CancellationToken.None);
                    WireEnvelope newKey2 = await WorkerReceiveUntilAsync(
                        coldChannel,
                        envelope => envelope.Receipt != null &&
                                    string.Equals(envelope.RequestId, rejectionRequest, StringComparison.Ordinal),
                        "the cold new-key second submission receipt");
                    newKey2Reason = newKey2.Receipt!.ReasonCode;
                    newKey2Digest = newKey2.Receipt.CanonicalPayloadSha256;
                    newKey2Evaluated = newKey2.Receipt.EvaluatedStateVersion.ToString(
                        System.Globalization.CultureInfo.InvariantCulture);
                    newKey2Replayed = newKey2.Receipt.Replayed;
                    if (!newKey2Replayed ||
                        !string.Equals(newKey2Reason, newKey1Reason, StringComparison.Ordinal) ||
                        !string.Equals(newKey2Digest, newKey1Digest, StringComparison.Ordinal) ||
                        !string.Equals(newKey2Evaluated, newKey1Evaluated, StringComparison.Ordinal))
                    {
                        failures.Add("the cold new-key second submission must replay the first new-key record without rewriting it");
                    }

                    await TraceStateAsync("newkey2");

                    // The original key must still answer its ORIGINAL outcome on
                    // the wire after the cold-scope writes: Found / Rejected /
                    // DECISION_MISMATCH with the original digest and evaluated
                    // version, proving the new-key records did not touch it.
                    var requery = new WireEnvelope
                    {
                        ProtocolMajor = ProtocolConstants.Major,
                        ProtocolMinor = ProtocolConstants.Minor,
                        RulesetVersion = ProtocolConstants.RulesetVersion,
                        ProjectionVersion = ProtocolConstants.ProjectionVersion,
                        MessageType = WireMessageType.ReceiptQuery,
                        SessionId = welcome.SessionId,
                        RoomId = roomId,
                        SeatId = welcome.SeatId,
                        BindingEpoch = welcome.BindingEpoch,
                        CorrelationId = "cold-original-requery",
                        ReceiptQuery = new Mahjong.Contracts.Wire.ReceiptQuery
                        {
                            RequestScopeId = rejectionScope,
                            RequestId = rejectionRequest
                        }
                    };
                    await coldChannel.SendAsync(WireCodec.Encode(requery), CancellationToken.None);
                    WireEnvelope relookup = await WorkerReceiveUntilAsync(
                        coldChannel,
                        envelope => envelope.ReceiptLookup != null &&
                                    string.Equals(envelope.CorrelationId, "cold-original-requery", StringComparison.Ordinal),
                        "the cold original-scope requery");
                    query2Status = relookup.ReceiptLookup!.Status.ToString();
                    if (relookup.ReceiptLookup.Receipt != null)
                    {
                        query2ReceiptStatus = relookup.ReceiptLookup.Receipt.Status.ToString();
                        query2Reason = relookup.ReceiptLookup.Receipt.ReasonCode;
                        query2Digest = relookup.ReceiptLookup.CanonicalPayloadSha256;
                        query2Evaluated = relookup.ReceiptLookup.Receipt.EvaluatedStateVersion.ToString(
                            System.Globalization.CultureInfo.InvariantCulture);
                    }

                    if (!string.Equals(query2Status, WireLookupStatus.Found.ToString(), StringComparison.Ordinal) ||
                        !string.Equals(query2ReceiptStatus, WireReceiptStatus.Rejected.ToString(), StringComparison.Ordinal) ||
                        !string.Equals(query2Reason, "DECISION_MISMATCH", StringComparison.Ordinal) ||
                        !string.Equals(query2Digest, startDigest, StringComparison.Ordinal))
                    {
                        failures.Add("the original key must still answer its original outcome after the cold-scope writes");
                    }

                    if (!string.Equals(query2Evaluated, coldEvaluated.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))
                    {
                        failures.Add("the original key must keep its evaluated version after the cold-scope writes");
                    }

                    await TraceStateAsync("query2");

                    // Store truth between the new-key submissions and the client
                    // phase: the cold-issued key holds exactly one own record and
                    // the original record is untouched in every field. Read
                    // through the recovered runtime's own store so only one store
                    // handle exists while the channel is live.
                    {
                        TableDurableReceipt? newKeyRecord = await coldRuntime.StoreForTest.TryGetAsync(
                            rejectionAccount,
                            new RequestScopeId(coldScopeId),
                            new RequestId(rejectionRequest),
                            CancellationToken.None);
                        TableDurableReceipt? originalRecord = await coldRuntime.StoreForTest.TryGetAsync(
                            rejectionAccount,
                            new RequestScopeId(rejectionScope),
                            new RequestId(rejectionRequest),
                            CancellationToken.None);
                        if (newKeyRecord == null || originalRecord == null)
                        {
                            failures.Add("both the cold-scope record and the original record must be readable after the new-key submissions");
                        }
                        else
                        {
                            newKeyRecordCreated = newKeyRecord.CreatedAtUtcMs;
                            newKeyRecordExpires = newKeyRecord.ExpiresAtUtcMs;
                            originalCreatedAfter = originalRecord.CreatedAtUtcMs;
                            originalStatusAfter = originalRecord.Receipt.Status.ToString();
                            originalReasonAfter = originalRecord.Receipt.ReasonCode;
                            originalDigestAfter = coldRuntime.StoreForTest.GetReceiptDigest(
                                rejectionAccount,
                                new RequestScopeId(rejectionScope),
                                new RequestId(rejectionRequest)) ?? string.Empty;
                            originalEvaluatedAfter = originalRecord.Receipt.EvaluatedStateVersion.Value.ToString(
                                System.Globalization.CultureInfo.InvariantCulture);
                            if (originalCreatedAfter != coldCreated)
                            {
                                failures.Add("the original persisted rejection must keep its CreatedAtUtcMs");
                            }

                            if (!string.Equals(originalStatusAfter, WireReceiptStatus.Rejected.ToString(), StringComparison.Ordinal) ||
                                !string.Equals(originalReasonAfter, "DECISION_MISMATCH", StringComparison.Ordinal))
                            {
                                failures.Add("the original record must keep its outcome after the cold-scope writes");
                            }

                            if (!string.Equals(originalDigestAfter, startDigest, StringComparison.Ordinal))
                            {
                                failures.Add("the original record must keep its canonical digest after the cold-scope writes");
                            }

                            if (!string.Equals(
                                    originalEvaluatedAfter,
                                    coldEvaluated.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                    StringComparison.Ordinal))
                            {
                                failures.Add("the original record must keep its evaluated version after the cold-scope writes");
                            }

                            // The two records are distinct by KEY: the cold-scope
                            // record lives under the freshly issued scope while
                            // the original keeps its own scope, and both are
                            // readable at their own keys at the same moment
                            // (checked above). Their creation timestamps may
                            // legitimately coincide because this worker clock is
                            // frozen at the parent's pinned value, so a timestamp
                            // comparison must not be used as the distinctness
                            // proof.
                        }
                    }

                    await TraceStateAsync("records");

                    // (5) Real cold client: the same FileOutboxStore the parent's
                    // live session used, reconciled by a real TableSession until
                    // the pending entry resolves from the durable receipt.
                    var resolutions = new List<TableReceiptResolvedEventArgs>();
                    FileOutboxStore clientOutbox = new FileOutboxStore(outboxPath);
                    coldSession = new TableSession(coldChannel, clientOutbox);
                    coldSession.ReceiptResolved += (_, args2) =>
                    {
                        lock (resolutions)
                        {
                            resolutions.Add(args2);
                        }
                    };
                    string coldSessionTicket = LocalTableHost.IssueTicket(options);
                    await coldSession.OpenAsync(
                        new TableSessionOpenRequest(coldSessionTicket, "crash-worker"),
                        CancellationToken.None);
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                    {
                        while (!timeout.IsCancellationRequested)
                        {
                            bool done;
                            lock (resolutions)
                            {
                                done = coldSession.State == TableConnectionState.Ready &&
                                       coldSession.PendingCommands.Count == 0 &&
                                       resolutions.Count > 0;
                            }

                            if (done)
                            {
                                break;
                            }

                            try
                            {
                                await coldSession.PumpOnceAsync(timeout.Token);
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

                    TableReceiptResolvedEventArgs? resolved;
                    lock (resolutions)
                    {
                        resolved = resolutions.FirstOrDefault(args2 =>
                            string.Equals(args2.Command.RequestId, rejectionRequest, StringComparison.Ordinal));
                    }

                    if (resolved != null)
                    {
                        clientDisposition = resolved.Resolution.Disposition.ToString();
                        clientReason = resolved.Resolution.ReasonCode;
                        clientDigest = resolved.Resolution.CanonicalPayloadSha256;
                        clientKind = resolved.Command.Kind.ToString();
                        clientFromLookup = resolved.Resolution.FromReceiptLookup;
                    }

                    clientPending = coldSession.PendingCommands.Count;
                    IReadOnlyList<OutboxEntry> unresolved = await clientOutbox.LoadUnresolvedAsync(
                        CancellationToken.None);
                    clientUnresolved = unresolved.Count;
                    if (resolved == null ||
                        !string.Equals(clientDisposition, OutboxDisposition.Rejected.ToString(), StringComparison.Ordinal) ||
                        !string.Equals(clientReason, "DECISION_MISMATCH", StringComparison.Ordinal) ||
                        !clientFromLookup ||
                        !string.Equals(clientDigest, startDigest, StringComparison.Ordinal) ||
                        clientPending != 0 ||
                        clientUnresolved != 0)
                    {
                        failures.Add("the cold client must reconcile the entry to Rejected with pending and unresolved empty");
                    }

                    await TraceStateAsync("reconcile");
                }
                finally
                {
                    if (coldSession != null)
                    {
                        try
                        {
                            await coldSession.CloseAsync("WORKER_DONE", CancellationToken.None);
                        }
                        catch
                        {
                        }
                    }

                    if (coldChannel != null)
                    {
                        try
                        {
                            await coldChannel.CloseAsync(
                                new TableChannelCloseReason("WORKER_DONE", true),
                                CancellationToken.None);
                        }
                        catch
                        {
                        }
                    }
                }

                // Final disk truth after every socket/session in this process is
                // closed: the cold activity must have left state/event and control
                // exactly where the cold start left them.
                using (LocalTableStore finalStore = await LocalTableStore.OpenAsync(
                    options,
                    rejectionClock,
                    null,
                    CancellationToken.None))
                {
                    TableStateRow? finalState = await finalStore.LoadStateAsync(roomId, CancellationToken.None);
                    TableDurableReceipt? newKeyFinal = await finalStore.TryGetAsync(
                        rejectionAccount,
                        new RequestScopeId(coldScopeId),
                        new RequestId(rejectionRequest),
                        CancellationToken.None);
                    TableDurableReceipt? originalFinal = await finalStore.TryGetAsync(
                        rejectionAccount,
                        new RequestScopeId(rejectionScope),
                        new RequestId(rejectionRequest),
                        CancellationToken.None);
                    finalStateVersion = finalState?.StateVersion.Value ?? 0UL;
                    finalLastEventSeq = finalState?.LastEventSeq.Value ?? 0UL;
                    IReadOnlyList<TableEventRecord> movedEvents = await finalStore.LoadEventsAfterAsync(
                        roomId,
                        new EventSeq(startLastEventSeq),
                        CancellationToken.None);
                    foreach (TableEventRecord movedEvent in movedEvents)
                    {
                        movedEventLines.Add(
                            movedEvent.Type + "/seat" + movedEvent.Seat + "/seq" + movedEvent.Seq.Value);
                    }

                    IReadOnlyList<TableCommandJournalRecord> movedCommands = await finalStore.LoadCommandsAfterAsync(
                        roomId,
                        new StateVersion(startStateVersion),
                        CancellationToken.None);
                    foreach (TableCommandJournalRecord movedCommand in movedCommands)
                    {
                        movedCommandLines.Add(
                            movedCommand.Api + "/acct=" + movedCommand.AccountId +
                            "/req=" + movedCommand.RequestId.Value +
                            "/v" + movedCommand.CommandVersion.Value);
                    }

                    finalControlVersion = finalStore.ControlVersionForTest;
                    finalControlRecordPresent =
                        finalStore.GetControlResult(
                            rejectionAccount,
                            new RequestScopeId(rejectionScope),
                            new RequestId(rejectionRequest)) != null ||
                        finalStore.GetControlResult(
                            rejectionAccount,
                            new RequestScopeId(coldScopeId),
                            new RequestId(rejectionRequest)) != null;
                    if (finalStateVersion != startStateVersion || finalLastEventSeq != startLastEventSeq)
                    {
                        failures.Add(
                            "the rejection/replay/reconciliation activity must not move state or events (state " +
                            startStateVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) + "->" +
                            finalStateVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                            ", events " +
                            startLastEventSeq.ToString(System.Globalization.CultureInfo.InvariantCulture) + "->" +
                            finalLastEventSeq.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                            "; moved: " + string.Join(",", movedEventLines) + ")");
                    }

                    if (finalControlVersion != 0L || finalControlRecordPresent)
                    {
                        failures.Add("the rejection/replay/reconciliation activity must not create LOCAL_CONTROL records");
                    }

                    if (newKeyFinal == null || newKeyFinal.CreatedAtUtcMs != newKeyRecordCreated)
                    {
                        failures.Add("the cold-scope record must keep the CreatedAtUtcMs of the first new-key submission");
                    }

                    if (originalFinal == null || originalFinal.CreatedAtUtcMs != coldCreated ||
                        !string.Equals(
                            finalStore.GetReceiptDigest(
                                rejectionAccount,
                                new RequestScopeId(rejectionScope),
                                new RequestId(rejectionRequest)) ?? string.Empty,
                            startDigest,
                            StringComparison.Ordinal) ||
                        !string.Equals(originalFinal.Receipt.Status.ToString(), WireReceiptStatus.Rejected.ToString(), StringComparison.Ordinal) ||
                        !string.Equals(originalFinal.Receipt.EvaluatedStateVersion.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), coldEvaluated.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))
                    {
                        failures.Add("the original record must stay field-identical at the final disk read");
                    }
                }

                var coldLines = new List<string>
                {
                    "mode=rejected-receipt-cold-session",
                    "recovery_generation=" + recoveryGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "receipt_found=1",
                    "receipt_status=Rejected",
                    "receipt_reason=DECISION_MISMATCH",
                    "receipt_api=CLAIM_CONTROL",
                    "receipt_evaluated=" + coldEvaluated.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "receipt_created=" + coldCreated.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "receipt_expires=" + coldExpires.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "receipt_command_version_present=" + (coldCommandVersionPresent ? "1" : "0"),
                    "receipt_event_seq_present=" + (coldEventSeqPresent ? "1" : "0"),
                    "digest_hex=" + startDigest,
                    "state_version=" + startStateVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "last_event_seq=" + startLastEventSeq.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "cold_scope=" + coldScopeId,
                    "cold_round_id=" + coldRoundId,
                    "cold_pending_decision=" + coldPendingDecision,
                    "query_status=" + queryStatus,
                    "query_receipt_status=" + queryReceiptStatus,
                    "query_receipt_reason=" + queryReceiptReason,
                    "query_digest=" + queryDigest,
                    "replay_key_scope=cold-issued",
                    "original_key_cold_command_replay=unreachable",
                    "newkey1_replayed=" + (newKey1Replayed ? "1" : "0"),
                    "newkey1_status=" + newKey1Status,
                    "newkey1_reason=" + newKey1Reason,
                    "newkey1_digest=" + newKey1Digest,
                    "newkey1_evaluated=" + newKey1Evaluated,
                    "newkey2_replayed=" + (newKey2Replayed ? "1" : "0"),
                    "newkey2_reason=" + newKey2Reason,
                    "newkey2_digest=" + newKey2Digest,
                    "newkey2_evaluated=" + newKey2Evaluated,
                    "newkey_record_created=" + newKeyRecordCreated.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "newkey_record_expires=" + newKeyRecordExpires.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "query2_status=" + query2Status,
                    "query2_receipt_status=" + query2ReceiptStatus,
                    "query2_reason=" + query2Reason,
                    "query2_digest=" + query2Digest,
                    "query2_evaluated=" + query2Evaluated,
                    "original_created_after=" + originalCreatedAfter.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "original_status_after=" + originalStatusAfter,
                    "original_reason_after=" + originalReasonAfter,
                    "original_digest_after=" + originalDigestAfter,
                    "original_evaluated_after=" + originalEvaluatedAfter,
                    "final_state_version=" + finalStateVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "final_last_event_seq=" + finalLastEventSeq.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "final_control_version=" + finalControlVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "final_control_record_present=" + (finalControlRecordPresent ? "1" : "0"),
                    "client_disposition=" + clientDisposition,
                    "client_reason=" + clientReason,
                    "client_digest=" + clientDigest,
                    "client_kind=" + clientKind,
                    "client_from_lookup=" + (clientFromLookup ? "1" : "0"),
                    "client_pending=" + clientPending.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "client_unresolved=" + clientUnresolved.ToString(System.Globalization.CultureInfo.InvariantCulture)
                };
                coldLines.AddRange(traceState.Select(trace => "trace_state=" + trace));
                coldLines.AddRange(movedEventLines.Select(moved => "moved_event=" + moved));
                coldLines.AddRange(movedCommandLines.Select(moved => "moved_command=" + moved));
                File.WriteAllLines(marker, coldLines);
                if (failures.Count > 0)
                {
                    foreach (string moved in movedEventLines)
                    {
                        Console.Error.WriteLine("cold-session moved_event " + moved);
                    }

                    foreach (string moved in movedCommandLines)
                    {
                        Console.Error.WriteLine("cold-session moved_command " + moved);
                    }

                    foreach (string trace in traceState)
                    {
                        Console.Error.WriteLine("cold-session trace " + trace);
                    }

                    foreach (string failure in failures)
                    {
                        Console.Error.WriteLine("cold-session check failed: " + failure);
                    }

                    return 3;
                }

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

        private static async Task<WireEnvelope> WorkerReceiveUntilAsync(
            ITableChannel channel,
            Func<WireEnvelope, bool> predicate,
            string description)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (true)
            {
                ReadOnlyMemory<byte> bytes = await channel.ReceiveAsync(timeout.Token);
                WireEnvelope envelope = WireEnvelope.Parser.ParseFrom(bytes.ToArray());
                if (envelope.Error != null)
                {
                    throw new InvalidDataException(
                        description + " was interrupted by ERROR " + envelope.Error.Code + ".");
                }

                if (predicate(envelope))
                {
                    return envelope;
                }
            }
        }

        private static string ReceiptSetDigest(Dictionary<string, TableDurableReceipt> receipts)
        {
            var text = new StringBuilder();
            foreach (KeyValuePair<string, TableDurableReceipt> pair in receipts.OrderBy(
                p => p.Key,
                StringComparer.Ordinal))
            {
                TableDurableReceipt receipt = pair.Value;
                text.Append(pair.Key).Append('|')
                    .Append(receipt.AccountId).Append('|')
                    .Append(receipt.ScopeId.Value).Append('|')
                    .Append(receipt.RequestId.Value).Append('|')
                    .Append(receipt.Api).Append('|')
                    .Append(((int)receipt.Receipt.Status).ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                    .Append(receipt.Receipt.ReasonCode).Append('|')
                    .Append(receipt.Receipt.EvaluatedStateVersion.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                    .Append((receipt.Receipt.CommandVersion?.Value ?? 0UL).ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                    .Append((receipt.Receipt.EventSeq?.Value ?? 0UL).ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                    .Append(receipt.Receipt.CanonicalPayloadSha256).Append('|')
                    .Append(receipt.PayloadSha256.ToHex()).Append('|')
                    .Append(receipt.CreatedAtUtcMs.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                    .Append(receipt.ExpiresAtUtcMs.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Append('\n');
            }

            return TablePayloadSha256.Compute(Encoding.UTF8.GetBytes(text.ToString())).ToHex();
        }

        private static string FileSha256OrAbsent(string path)
        {
            return File.Exists(path)
                ? TablePayloadSha256.Compute(File.ReadAllBytes(path)).ToHex()
                : "ABSENT";
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
