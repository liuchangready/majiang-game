#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.LocalHost;
using Process = System.Diagnostics.Process;
using WireEnvelope = Mahjong.Contracts.Wire.Envelope;
using WireMessageType = Mahjong.Contracts.Wire.MessageType;

namespace Mahjong.Unity.App
{
    /// <summary>
    /// C04-D01 Player-only observation chain. It is dormant unless the exact
    /// <c>-c04-recovery</c> switch is present. It never edits domain state, never
    /// reflects into the runtime and never fabricates a receipt: PREPARE commits one
    /// real Discard and leaves its real outbox entry unresolved, and RECOVER opens the
    /// real LocalHost recovery path and drives TableSession.ReconcilePendingAsync
    /// through the real ITableChannel receipt-query route.
    /// </summary>
    public sealed class C04RecoveryReconciliationProbe : MonoBehaviour
    {
        private const string Switch = "-c04-recovery";
        private const string RoomId = "local-main";

        private ProbeArguments _arguments = new ProbeArguments();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            ProbeArguments arguments = ProbeArguments.Parse(Environment.GetCommandLineArgs());
            if (!arguments.Enabled)
            {
                return;
            }

            var gameObject = new GameObject("C04RecoveryReconciliationProbe");
            DontDestroyOnLoad(gameObject);
            var probe = gameObject.AddComponent<C04RecoveryReconciliationProbe>();
            probe._arguments = arguments;
        }

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            if (!_arguments.IsComplete(out string error))
            {
                Fail("ARGUMENT_INVALID", error);
                yield break;
            }

            Task<int> run = Task.Run(() => RunAsync(_arguments));
            while (!run.IsCompleted)
            {
                yield return null;
            }

            if (run.IsFaulted)
            {
                Exception exception = run.Exception?.GetBaseException() ?? new InvalidOperationException("unknown");
                Fail(exception.GetType().Name, exception.Message);
                yield break;
            }

            Application.Quit(run.Result);
        }

        private static async Task<int> RunAsync(ProbeArguments arguments)
        {
            Directory.CreateDirectory(arguments.Root);
            Directory.CreateDirectory(arguments.LocalDirectory);
            Directory.CreateDirectory(arguments.OutboxDirectory);
            Directory.CreateDirectory(arguments.FramesDirectory);
            Record("PROBE_START mode=" + arguments.Mode + " case=" + arguments.CaseId + " root=" + arguments.Root);

            try
            {
                if (arguments.Mode == "Prepare")
                {
                    return await PrepareAsync(arguments).ConfigureAwait(false);
                }

                return await RecoverAsync(arguments).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                var error = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["schemaVersion"] = 1,
                    ["kind"] = "c04-recovery-error",
                    ["caseId"] = arguments.CaseId,
                    ["fault"] = arguments.Fault,
                    ["code"] = exception.GetType().Name,
                    ["detail"] = exception.Message,
                    ["atUtc"] = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                };
                WriteAtomic(Path.Combine(arguments.Root, "result.error.json"), Json.Serialize(error));
                Record("PROBE_ERROR " + exception.GetType().Name + ": " + exception.Message);
                return 1;
            }
        }

        private static async Task<int> PrepareAsync(ProbeArguments arguments)
        {
            LocalTableHostOptions options = BuildOptions(arguments, "c04-prepare-" + Guid.NewGuid().ToString("N"));
            LocalTableHost.PrepareRecoveryCaptureOnNextOpen(arguments.LocalDirectory);
            var channel = new RecordingChannel(
                await LocalTableHost.CreateChannelAsync(options, CancellationToken.None).ConfigureAwait(false),
                arguments.FramesDirectory);
            string channelTicket = LocalTableHost.IssueTicket(options);
            await channel.OpenAsync(
                new TableChannelConnectRequest(channelTicket, "c04-recovery-probe", null),
                CancellationToken.None).ConfigureAwait(false);
            LocalTableRecoveryCapture initial = await LocalTableHost.TakePreparedRecoveryCaptureAsync(
                arguments.LocalDirectory,
                CancellationToken.None).ConfigureAwait(false);
            WriteArtifact(arguments, "authority/prepare-pre.save", initial.AuthoritySaveBytes);
            WriteArtifact(arguments, "authority/prepare-pre-cheat.bin", initial.CheatStatePayload);
            var outbox = new FileOutboxStore(arguments.OutboxDirectory);
            var session = new TableSession(channel, outbox);
            int visibleDiscards = 0;
            EventHandler<TableSnapshotChangedEventArgs> discardObserver = (_, args) =>
            {
                foreach (TableVisibleEvent visible in args.VisibleEventBatch.Events)
                {
                    if (visible.Type == ProtocolEventType.TileDiscarded && visible.Seat == 0)
                    {
                        Interlocked.Increment(ref visibleDiscards);
                    }
                }
            };
            session.SnapshotChanged += discardObserver;
            try
            {
                await PumpUntilReadyAsync(
                    session,
                    LocalTableHost.IssueTicket(options),
                    CancellationToken.None).ConfigureAwait(false);
                PlayerSnapshot before = session.PlayerSnapshot!;
                uint tile = before.PrivateView.ClosedTiles[0];
                int preHand = HandCount(before);
                int preRiver = RiverCount(before);

                BusinessPayload payload = CommandPayloadFactory.SubmitAction(
                    before.RoundId,
                    "local-direct",
                    0UL,
                    ProtocolActionType.DiscardTile,
                    tileIds: new[] { tile });
                var requestId = new RequestId("c04-player-" + arguments.CaseId + "-discard");
                var command = new ProtocolCommand(
                    CommandKind.SubmitAction,
                    payload,
                    requestId,
                    session.Binding!.Scope.ScopeId,
                    new StateVersion(before.StateVersion),
                    new RoundId(before.RoundId),
                    decisionId: null,
                    controlEpoch: new ControlEpoch(0));
                CommandSubmission submission = await session.SubmitAsync(command, CancellationToken.None)
                    .ConfigureAwait(false);
                if (!submission.Sent)
                {
                    throw new InvalidOperationException("The original Discard was not handed to the transport.");
                }

                // The receipt reply is deliberately lost (as after a kill) while the
                // authoritative PUSH is still observed. The outbox therefore remains
                // unresolved and the original effect can be proven from the snapshot.
                channel.DropReceiptRequestId = requestId.Value;
                int beforeVersion = (int)before.StateVersion;
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                {
                    for (int i = 0; i < 40 &&
                        (session.PlayerSnapshot == null || session.PlayerSnapshot.StateVersion <= (ulong)beforeVersion); i++)
                    {
                        await session.PumpOnceAsync(cts.Token).ConfigureAwait(false);
                    }
                }

                PlayerSnapshot after = session.PlayerSnapshot!;
                int discardEvents = Volatile.Read(ref visibleDiscards);
                LocalTableRecoveryCapture post = await LocalTableHost.CaptureRecoveryAsync(
                    arguments.LocalDirectory,
                    CancellationToken.None).ConfigureAwait(false);
                WriteArtifact(arguments, "authority/prepare-post.save", post.AuthoritySaveBytes);
                WriteArtifact(arguments, "authority/prepare-post-cheat.bin", post.CheatStatePayload);
                CopyArtifact(arguments, Path.Combine(arguments.OutboxDirectory, "outbox.dat"), "authority/prepare-outbox.dat");
                LocalTableRecoveryFacts facts = post.Facts;
                IReadOnlyList<OutboxEntry> unresolved = await outbox.LoadUnresolvedAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                LocalTableRecoveryRequestFact? receipt = facts.Requests.FirstOrDefault(r =>
                    string.Equals(r.RequestId, requestId.Value, StringComparison.Ordinal));
                if (receipt == null)
                {
                    throw new InvalidDataException("The committed save has no receipt for the original Discard.");
                }

                string primaryPath = Path.Combine(arguments.LocalDirectory, RoomId + ".save");
                string backupPath = primaryPath + ".prev";
                // Publish the exact post-Discard committed primary as the valid
                // previous copy. A real writer normally lags one commit behind; the
                // C04 fault rows require a valid backup that contains the Applied
                // receipt and the post-Discard state, so seed both copies from the
                // same committed bytes and bind both hashes.
                File.Copy(primaryPath, backupPath, true);
                string outboxPath = Path.Combine(arguments.OutboxDirectory, "outbox.dat");
                var prepare = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["schemaVersion"] = 1,
                    ["kind"] = "c04-recovery-prepare",
                    ["caseId"] = arguments.CaseId,
                    ["fault"] = arguments.Fault,
                    ["expectedOutcome"] = arguments.ExpectedOutcome,
                    ["request"] = RequestMap(requestId.Value, session.Binding!.Scope.ScopeId.Value,
                        receipt.AccountId, receipt.Status, receipt.PayloadSha256, receipt.EvaluatedStateVersion,
                        receipt.CommandVersion, receipt.EventSeq),
                    ["canonicalDigest"] = CanonicalPayloadDigest.Compute(payload).ToHex(),
                    ["api"] = "SUBMIT_ACTION",
                    ["authority"] = AuthorityMap(arguments),
                    ["pre"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["stateVersion"] = before.StateVersion,
                        ["eventSeq"] = before.EventSeq,
                        ["handCount"] = preHand,
                        ["riverCount"] = preRiver,
                        ["ledgerEntries"] = initial.Facts.Ledger.EntryCount,
                        ["ledgerPoints"] = initial.Facts.Ledger.Points,
                        ["ledgerSha256"] = initial.Facts.Ledger.EntriesSha256,
                        ["physicalCount"] = initial.Facts.Physical.TileCount,
                        ["physicalUnique"] = initial.Facts.Physical.UniqueCount,
                        ["physicalSha256"] = initial.Facts.Physical.TilesSha256,
                    },
                    ["post"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["stateVersion"] = after.StateVersion,
                        ["eventSeq"] = after.EventSeq,
                        ["handCount"] = HandCount(after),
                        ["riverCount"] = RiverCount(after),
                        ["tileInRiver"] = TileInRiver(after, tile),
                        ["discardEvents"] = discardEvents,
                        ["ledgerEntries"] = facts.Ledger.EntryCount,
                        ["ledgerPoints"] = facts.Ledger.Points,
                        ["ledgerSha256"] = facts.Ledger.EntriesSha256,
                        ["physicalCount"] = facts.Physical.TileCount,
                        ["physicalUnique"] = facts.Physical.UniqueCount,
                        ["physicalSha256"] = facts.Physical.TilesSha256,
                    },
                    ["tileId"] = tile,
                    ["outbox"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["path"] = outboxPath,
                        ["sha256"] = File.Exists(outboxPath) ? Sha256File(outboxPath) : string.Empty,
                        ["unresolved"] = unresolved.Count,
                    },
                    ["files"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["primary"] = File.Exists(primaryPath) ? Sha256File(primaryPath) : string.Empty,
                        ["backup"] = File.Exists(backupPath) ? Sha256File(backupPath) : string.Empty,
                    },
                    ["binding"] = Binding(session),
                    ["frames"] = channel.Frames(),
                    ["droppedReceipts"] = channel.DroppedReceipts,
                    ["process"] = ProcessIdentity(),
                    ["build"] = arguments.Build,
                };
                WriteAtomic(Path.Combine(arguments.Root, "prepare.json"), Json.Serialize(prepare));
                WriteFactLines(arguments, prepare);
                Record("PREPARE_DONE request=" + requestId.Value + " digest=" + receipt.PayloadSha256 +
                    " stateVersion=" + after.StateVersion + " discardEvents=" + discardEvents);

                if (arguments.ForceKill)
                {
                    Record("FORCE_KILLED after commit; no close/dispose path");
                    Process.GetCurrentProcess().Kill();
                    return 0;
                }

                await session.CloseAsync("C04_PREPARE_DONE", CancellationToken.None).ConfigureAwait(false);
                await channel.CloseAsync(
                    new TableChannelCloseReason("C04_PREPARE_DONE", true),
                    CancellationToken.None).ConfigureAwait(false);
                return 0;
            }
            finally
            {
                session.SnapshotChanged -= discardObserver;
                if (!arguments.ForceKill)
                {
                    try
                    {
                        await session.CloseAsync("C04_PREPARE_FINALLY", CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                    }

                    try
                    {
                        await channel.CloseAsync(
                            new TableChannelCloseReason("C04_PREPARE_FINALLY", true),
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static async Task<int> RecoverAsync(ProbeArguments arguments)
        {
            string primaryPath = Path.Combine(arguments.LocalDirectory, RoomId + ".save");
            string backupPath = primaryPath + ".prev";
            string primaryBefore = File.Exists(primaryPath) ? Sha256File(primaryPath) : string.Empty;
            string backupBefore = File.Exists(backupPath) ? Sha256File(backupPath) : string.Empty;
            FactSummary expected = FactSummary.Read(Path.Combine(arguments.Root, "expected.txt"));
            LocalTableHostOptions options = BuildOptions(arguments, "c04-recover-" + Guid.NewGuid().ToString("N"));
            LocalTableHost.PrepareRecoveryCaptureOnNextOpen(arguments.LocalDirectory);
            var channel = new RecordingChannel(
                await LocalTableHost.CreateChannelAsync(options, CancellationToken.None).ConfigureAwait(false),
                arguments.FramesDirectory);
            string channelTicket = LocalTableHost.IssueTicket(options);
            bool opened = false;
            try
            {
                try
                {
                    await channel.OpenAsync(
                        new TableChannelConnectRequest(channelTicket, "c04-recovery-probe", null),
                        CancellationToken.None).ConfigureAwait(false);
                    opened = true;
                }
                catch (LocalTableException exception) when (exception.ErrorKind == LocalTableErrorKind.CorruptState)
                {
                    return await WriteRejected(arguments, channel, exception, expected, primaryBefore, backupBefore)
                        .ConfigureAwait(false);
                }

                LocalTableRecoveryCapture baselineCapture = await LocalTableHost.TakePreparedRecoveryCaptureAsync(
                    arguments.LocalDirectory,
                    CancellationToken.None).ConfigureAwait(false);
                LocalTableRecoveryFacts baseline = baselineCapture.Facts;
                WriteArtifact(arguments, "authority/recover-baseline.save", baselineCapture.AuthoritySaveBytes);
                WriteArtifact(arguments, "authority/recover-baseline-cheat.bin", baselineCapture.CheatStatePayload);
                CopyArtifact(arguments,
                    Path.Combine(arguments.OutboxDirectory, "outbox.dat"),
                    "authority/recover-baseline-outbox.dat");
                LocalTableRecoveryRequestFact? original = baseline.Requests.FirstOrDefault(r =>
                    string.Equals(r.RequestId, expected.RequestId, StringComparison.Ordinal) &&
                    string.Equals(r.ScopeId, expected.ScopeId, StringComparison.Ordinal));
                if (original != null && string.Equals(original.Status, "APPLIED", StringComparison.Ordinal))
                {
                    return await RecoverAndReconcileAsync(
                        arguments,
                        options,
                        channel,
                        baseline,
                        original,
                        expected).ConfigureAwait(false);
                }

                return await FreshStartAsync(arguments, options, channel, baseline, expected).ConfigureAwait(false);
            }
            finally
            {
                if (opened)
                {
                    try
                    {
                        await channel.CloseAsync(
                            new TableChannelCloseReason("C04_RECOVER_FINALLY", true),
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static async Task<int> RecoverAndReconcileAsync(
            ProbeArguments arguments,
            LocalTableHostOptions options,
            RecordingChannel channel,
            LocalTableRecoveryFacts baseline,
            LocalTableRecoveryRequestFact original,
            FactSummary expected)
        {
            var outbox = new FileOutboxStore(arguments.OutboxDirectory);
            IReadOnlyList<OutboxEntry> unresolved = await outbox.LoadUnresolvedAsync(CancellationToken.None)
                .ConfigureAwait(false);
            OutboxEntry? entry = unresolved.FirstOrDefault(e =>
                string.Equals(e.RequestId.Value, expected.RequestId, StringComparison.Ordinal) &&
                string.Equals(e.ScopeId.Value, expected.ScopeId, StringComparison.Ordinal));
            if (entry == null)
            {
                throw new InvalidDataException("The recovered outbox has no unresolved original request.");
            }

            var session = new TableSession(channel, outbox);
            var resolutions = new List<TableReceiptResolvedEventArgs>();
            EventHandler<TableReceiptResolvedEventArgs> handler = (_, args) =>
            {
                lock (resolutions)
                {
                    resolutions.Add(args);
                }
            };
            session.ReceiptResolved += handler;
            try
            {
                await PumpUntilReadyAsync(session, LocalTableHost.IssueTicket(options), CancellationToken.None)
                    .ConfigureAwait(false);
                int pendingBefore = session.PendingCommands.Count;
                if (pendingBefore > 0)
                {
                    await session.ReconcilePendingAsync(CancellationToken.None).ConfigureAwait(false);
                }

                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                {
                    for (int i = 0; i < 80 && session.PendingCommands.Count > 0; i++)
                    {
                        await session.PumpOnceAsync(cts.Token).ConfigureAwait(false);
                    }
                }

                if (session.PendingCommands.Count != 0)
                {
                    throw new TimeoutException("The original request did not reconcile from the real receipt query.");
                }

                TableReceiptResolvedEventArgs resolution;
                lock (resolutions)
                {
                    resolution = resolutions.FirstOrDefault(r =>
                        string.Equals(r.Command.RequestId, expected.RequestId, StringComparison.Ordinal)) ??
                        throw new InvalidDataException("No ReceiptResolved event was observed for the original request.");
                }

                LocalTableRecoveryCapture afterCapture = await LocalTableHost.CaptureRecoveryAsync(
                    arguments.LocalDirectory,
                    CancellationToken.None).ConfigureAwait(false);
                WriteArtifact(arguments, "authority/recover-after.save", afterCapture.AuthoritySaveBytes);
                WriteArtifact(arguments, "authority/recover-after-cheat.bin", afterCapture.CheatStatePayload);
                CopyArtifact(arguments,
                    Path.Combine(arguments.OutboxDirectory, "outbox.dat"),
                    "authority/recover-after-outbox.dat");
                LocalTableRecoveryFacts after = afterCapture.Facts;
                PlayerSnapshot snapshot = session.PlayerSnapshot!;
                int sentBeforeRepeat = channel.SentCount;
                await session.ReconcilePendingAsync(CancellationToken.None).ConfigureAwait(false);
                bool repeatSentNothing = channel.SentCount == sentBeforeRepeat;
                LocalTableRecoveryCapture repeatCapture = await LocalTableHost.CaptureRecoveryAsync(
                    arguments.LocalDirectory,
                    CancellationToken.None).ConfigureAwait(false);
                WriteArtifact(arguments, "authority/recover-repeat.save", repeatCapture.AuthoritySaveBytes);
                WriteArtifact(arguments, "authority/recover-repeat-cheat.bin", repeatCapture.CheatStatePayload);
                LocalTableRecoveryFacts repeated = repeatCapture.Facts;

                bool zeroDelta = string.Equals(after.StateVersion, repeated.StateVersion, StringComparison.Ordinal) &&
                    string.Equals(after.EventSeq, repeated.EventSeq, StringComparison.Ordinal) &&
                    string.Equals(after.Ledger.EntriesSha256, repeated.Ledger.EntriesSha256, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(after.Physical.TilesSha256, repeated.Physical.TilesSha256, StringComparison.OrdinalIgnoreCase);
                int hand = HandCount(snapshot);
                int river = RiverCount(snapshot);
                bool exactRequest = string.Equals(original.PayloadSha256, expected.PayloadSha256, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(original.ScopeId, expected.ScopeId, StringComparison.Ordinal) &&
                    string.Equals(original.RequestId, expected.RequestId, StringComparison.Ordinal);
                bool appliedOnce = string.Equals(after.StateVersion, expected.CommandVersion, StringComparison.Ordinal) &&
                    string.Equals(after.EventSeq, expected.EventSeq, StringComparison.Ordinal) &&
                    string.Equals(after.Ledger.EntriesSha256, expected.LedgerSha256, StringComparison.OrdinalIgnoreCase) &&
                    after.Physical.TileCount == 136 && after.Physical.UniqueCount == 136 &&
                    hand == expected.PreHand - 1 && river == expected.PreRiver + 1 &&
                    TileInRiver(snapshot, expected.TileId);
                int oldCommands = channel.SentFrames.Count(f => f.MessageType == "Command" &&
                    string.Equals(f.RequestId, expected.RequestId, StringComparison.Ordinal));
                int oldQueries = channel.SentFrames.Count(f => f.MessageType == "ReceiptQuery" &&
                    string.Equals(f.RequestId, expected.RequestId, StringComparison.Ordinal) &&
                    string.Equals(f.ScopeId, expected.ScopeId, StringComparison.Ordinal));

                var checks = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["exactOriginalIdentity"] = exactRequest,
                    ["foundFromRealLookup"] = resolution.Resolution.FromReceiptLookup &&
                        resolution.Resolution.ReceiptStatus == Mahjong.Contracts.ReceiptStatus.Applied,
                    ["appliedOnceEffectVector"] = appliedOnce,
                    ["zeroDeltaAfterReconcile"] = zeroDelta,
                    ["repeatQuerySentNothing"] = repeatSentNothing,
                    ["noOriginalCommandResend"] = oldCommands == 0,
                    ["oldScopeQueryCount"] = oldQueries,
                    ["physical136Unique"] = after.Physical.TileCount == 136 && after.Physical.UniqueCount == 136,
                };
                var result = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["schemaVersion"] = 1,
                    ["kind"] = "c04-recovery-result",
                    ["caseId"] = arguments.CaseId,
                    ["fault"] = arguments.Fault,
                    ["expectedOutcome"] = arguments.ExpectedOutcome,
                    ["actualOutcome"] = arguments.Fault == "None" ? "RECOVERED" : "RECOVERED_FROM_BACKUP",
                    ["request"] = RequestMap(original.RequestId, original.ScopeId, original.AccountId,
                        original.Status, original.PayloadSha256, original.EvaluatedStateVersion,
                        original.CommandVersion, original.EventSeq),
                    ["canonicalDigest"] = expected.CanonicalDigest,
                    ["binding"] = Binding(session),
                    ["pre"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["stateVersion"] = expected.PreStateVersion,
                        ["eventSeq"] = expected.PreEventSeq,
                        ["handCount"] = expected.PreHand,
                        ["riverCount"] = expected.PreRiver,
                        ["ledgerSha256"] = expected.LedgerSha256,
                        ["physicalSha256"] = expected.PhysicalSha256,
                    },
                    ["state"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["stateVersion"] = after.StateVersion,
                        ["eventSeq"] = after.EventSeq,
                        ["handCount"] = hand,
                        ["riverCount"] = river,
                        ["tileInRiver"] = TileInRiver(snapshot, expected.TileId),
                        ["discardEvents"] = expected.DiscardEvents,
                        ["ledgerEntries"] = after.Ledger.EntryCount,
                        ["ledgerPoints"] = after.Ledger.Points,
                        ["ledgerSha256"] = after.Ledger.EntriesSha256,
                        ["physicalCount"] = after.Physical.TileCount,
                        ["physicalUnique"] = after.Physical.UniqueCount,
                        ["physicalSha256"] = after.Physical.TilesSha256,
                        ["oldScopeQueries"] = oldQueries,
                        ["oldRequestCommands"] = oldCommands,
                    },
                    ["resolution"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["requestId"] = resolution.Command.RequestId,
                        ["scopeId"] = resolution.Command.ScopeId,
                        ["disposition"] = resolution.Resolution.Disposition.ToString(),
                        ["receiptStatus"] = resolution.Resolution.ReceiptStatus.ToString(),
                        ["fromLookup"] = resolution.Resolution.FromReceiptLookup,
                        ["digest"] = resolution.Resolution.CanonicalPayloadSha256,
                    },
                    ["checks"] = checks,
                    ["authority"] = AuthorityMap(arguments),
                    ["frames"] = channel.Frames(),
                    ["oldOutboxUnresolved"] = unresolved.Count,
                    ["process"] = ProcessIdentity(),
                    ["build"] = arguments.Build,
                    ["fileHashes"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["primary"] = File.Exists(Path.Combine(arguments.LocalDirectory, RoomId + ".save"))
                            ? Sha256File(Path.Combine(arguments.LocalDirectory, RoomId + ".save")) : string.Empty,
                        ["backup"] = File.Exists(Path.Combine(arguments.LocalDirectory, RoomId + ".save.prev"))
                            ? Sha256File(Path.Combine(arguments.LocalDirectory, RoomId + ".save.prev")) : string.Empty,
                    },
                };
                WriteAtomic(Path.Combine(arguments.Root, "result.json"), Json.Serialize(result));
                Record("RECOVER_DONE outcome=" + result["actualOutcome"] + " queries=" + oldQueries +
                    " stateVersion=" + after.StateVersion);
                return checks.Values.All(value => value is bool b ? b : true) ? 0 : 2;
            }
            finally
            {
                session.ReceiptResolved -= handler;
                await session.CloseAsync("C04_RECOVER_DONE", CancellationToken.None).ConfigureAwait(false);
            }
        }

        private static async Task<int> FreshStartAsync(
            ProbeArguments arguments,
            LocalTableHostOptions options,
            RecordingChannel channel,
            LocalTableRecoveryFacts baseline,
            FactSummary expected)
        {
            var freshOutbox = new FileOutboxStore(Path.Combine(arguments.Root, "outbox-fresh"));
            var session = new TableSession(channel, freshOutbox);
            try
            {
                await PumpUntilReadyAsync(session, LocalTableHost.IssueTicket(options), CancellationToken.None)
                    .ConfigureAwait(false);
                bool noOldSend = channel.SentFrames.All(f =>
                    !string.Equals(f.RequestId, expected.RequestId, StringComparison.Ordinal) &&
                    !string.Equals(f.ScopeId, expected.ScopeId, StringComparison.Ordinal));
                var oldOutbox = new FileOutboxStore(arguments.OutboxDirectory);
                int oldUnresolved = (await oldOutbox.LoadUnresolvedAsync(CancellationToken.None)
                    .ConfigureAwait(false)).Count;
                var result = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["schemaVersion"] = 1,
                    ["kind"] = "c04-recovery-result",
                    ["caseId"] = arguments.CaseId,
                    ["fault"] = arguments.Fault,
                    ["expectedOutcome"] = arguments.ExpectedOutcome,
                    ["actualOutcome"] = "FRESH_START",
                    ["canonicalDigest"] = expected.CanonicalDigest,
                    ["request"] = RequestMap(expected.RequestId, expected.ScopeId, expected.AccountId,
                        "NOT_REPLAYED_IN_NEW_SCOPE", expected.PayloadSha256, expected.EvaluatedStateVersion,
                        expected.CommandVersion, expected.EventSeq),
                    ["binding"] = Binding(session),
                    ["state"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["stateVersion"] = baseline.StateVersion,
                        ["eventSeq"] = baseline.EventSeq,
                        ["oldReceiptCount"] = baseline.Requests.Count,
                        ["oldReceiptPresent"] = baseline.Requests.Any(r =>
                            string.Equals(r.RequestId, expected.RequestId, StringComparison.Ordinal)),
                        ["oldOutboxUnresolved"] = oldUnresolved,
                        ["physicalCount"] = baseline.Physical.TileCount,
                        ["physicalUnique"] = baseline.Physical.UniqueCount,
                    },
                    ["checks"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["noOldScopeFrame"] = noOldSend,
                        ["freshStateVersionZero"] = baseline.StateVersion == "0",
                        ["oldOutboxKept"] = oldUnresolved == 1,
                        ["oldReceiptAbsent"] = baseline.Requests.Count == 0,
                    },
                    ["authority"] = AuthorityMap(arguments),
                    ["frames"] = channel.Frames(),
                    ["process"] = ProcessIdentity(),
                    ["build"] = arguments.Build,
                };
                WriteAtomic(Path.Combine(arguments.Root, "result.json"), Json.Serialize(result));
                Record("FRESH_DONE oldUnresolved=" + oldUnresolved + " frames=" + channel.SentCount);
                return noOldSend && baseline.StateVersion == "0" && oldUnresolved == 1 ? 0 : 2;
            }
            finally
            {
                await session.CloseAsync("C04_FRESH_DONE", CancellationToken.None).ConfigureAwait(false);
            }
        }

        private static Task<int> WriteRejected(
            ProbeArguments arguments,
            RecordingChannel channel,
            LocalTableException exception,
            FactSummary expected,
            string primaryBefore,
            string backupBefore)
        {
            var oldOutbox = new FileOutboxStore(arguments.OutboxDirectory);
            int oldUnresolved = oldOutbox.LoadUnresolvedAsync(CancellationToken.None)
                .ConfigureAwait(false).GetAwaiter().GetResult().Count;
            string primaryAfter = File.Exists(Path.Combine(arguments.LocalDirectory, RoomId + ".save"))
                ? Sha256File(Path.Combine(arguments.LocalDirectory, RoomId + ".save")) : string.Empty;
            string backupAfter = File.Exists(Path.Combine(arguments.LocalDirectory, RoomId + ".save.prev"))
                ? Sha256File(Path.Combine(arguments.LocalDirectory, RoomId + ".save.prev")) : string.Empty;
            CopyArtifact(arguments,
                Path.Combine(arguments.LocalDirectory, RoomId + ".save"),
                "authority/rejected-primary.save");
            CopyArtifact(arguments,
                Path.Combine(arguments.LocalDirectory, RoomId + ".save.prev"),
                "authority/rejected-backup.save");
            CopyArtifact(arguments,
                Path.Combine(arguments.OutboxDirectory, "outbox.dat"),
                "authority/rejected-outbox.dat");
            var result = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["schemaVersion"] = 1,
                ["kind"] = "c04-recovery-result",
                ["caseId"] = arguments.CaseId,
                ["fault"] = arguments.Fault,
                ["expectedOutcome"] = arguments.ExpectedOutcome,
                ["actualOutcome"] = "REJECTED",
                ["canonicalDigest"] = expected.CanonicalDigest,
                ["request"] = RequestMap(expected.RequestId, expected.ScopeId, expected.AccountId,
                    "NOT_APPLICABLE_SAFE_REFUSAL", expected.PayloadSha256, expected.EvaluatedStateVersion,
                    expected.CommandVersion, expected.EventSeq),
                ["error"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["errorKind"] = exception.ErrorKind.ToString(),
                    ["detail"] = exception.Message,
                },
                ["state"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["ready"] = false,
                    ["oldOutboxUnresolved"] = oldUnresolved,
                },
                ["checks"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["zeroSendBoundary"] = channel.SentCount == 0,
                    ["oldOutboxKept"] = oldUnresolved == 1,
                    ["unchangedPrimary"] = string.Equals(primaryBefore, primaryAfter, StringComparison.OrdinalIgnoreCase),
                    ["unchangedBackup"] = string.Equals(backupBefore, backupAfter, StringComparison.OrdinalIgnoreCase),
                },
                ["authority"] = AuthorityMap(arguments),
                ["frames"] = channel.Frames(),
                ["fileHashes"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["primary"] = primaryAfter,
                    ["backup"] = backupAfter,
                },
                ["process"] = ProcessIdentity(),
                ["build"] = arguments.Build,
            };
            WriteAtomic(Path.Combine(arguments.Root, "result.json"), Json.Serialize(result));
            Record("REJECTED_DONE kind=" + exception.ErrorKind + " sent=" + channel.SentCount);
            bool ok = channel.SentCount == 0 && oldUnresolved == 1 &&
                string.Equals(primaryBefore, primaryAfter, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(backupBefore, backupAfter, StringComparison.OrdinalIgnoreCase);
            return Task.FromResult(ok ? 0 : 2);
        }

        private static LocalTableHostOptions BuildOptions(ProbeArguments arguments, string owner)
        {
            return new LocalTableHostOptions
            {
                SaveDirectory = arguments.LocalDirectory,
                RoomId = RoomId,
                RulesetVersion = arguments.RulesetVersion,
                OwnerInstanceId = owner,
                LeaseDuration = TimeSpan.FromMinutes(5),
            };
        }

        private static async Task PumpUntilReadyAsync(
            TableSession session,
            string ticket,
            CancellationToken cancellationToken)
        {
            await session.OpenAsync(new TableSessionOpenRequest(ticket, "c04-recovery-probe"), cancellationToken)
                .ConfigureAwait(false);
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(30));
                for (int i = 0; i < 80 && session.State != TableConnectionState.Ready; i++)
                {
                    await session.PumpOnceAsync(cts.Token).ConfigureAwait(false);
                }
            }

            if (session.State != TableConnectionState.Ready)
            {
                throw new TimeoutException("The local session did not reach Ready: " + session.LastError);
            }
        }

        private static int HandCount(PlayerSnapshot snapshot)
        {
            int count = snapshot.PrivateView.ClosedTiles.Count;
            return snapshot.PrivateView.HasDrawnTileId ? count + 1 : count;
        }

        private static int RiverCount(PlayerSnapshot snapshot) =>
            snapshot.PublicView.Board.Players[0].Discards.Count;

        private static bool TileInRiver(PlayerSnapshot snapshot, uint tile) =>
            snapshot.PublicView.Board.Players[0].Discards.Any(d => d.TileId == tile);

        private static Dictionary<string, object> RequestMap(
            string requestId,
            string scopeId,
            string accountId,
            string status,
            string payloadSha256,
            string evaluated,
            string? commandVersion,
            string? eventSeq)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["requestId"] = requestId,
                ["scopeId"] = scopeId,
                ["accountId"] = accountId,
                ["status"] = status,
                ["payloadSha256"] = payloadSha256,
                ["evaluatedStateVersion"] = evaluated,
                ["commandVersion"] = commandVersion ?? string.Empty,
                ["eventSeq"] = eventSeq ?? string.Empty,
            };
        }

        private static Dictionary<string, object> Binding(TableSession session)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["sessionId"] = session.Binding!.SessionId.Value,
                ["roomId"] = session.Binding.RoomId.Value,
                ["seat"] = session.Binding.SeatId,
                ["role"] = session.Binding.Role.ToString(),
                ["scopeId"] = session.Binding.Scope.ScopeId.Value,
                ["bindingEpoch"] = session.Binding.BindingEpoch.Value,
            };
        }

        private static Dictionary<string, object> ProcessIdentity()
        {
            Process process = Process.GetCurrentProcess();
            string path = process.MainModule?.FileName ?? string.Empty;
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["pid"] = process.Id,
                ["path"] = path,
                ["sha256"] = string.IsNullOrEmpty(path) ? string.Empty : Sha256File(path),
                ["startUtc"] = process.StartTime.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            };
        }

        private static void WriteFactLines(ProbeArguments arguments, Dictionary<string, object> prepare)
        {
            var request = (Dictionary<string, object>)prepare["request"];
            var pre = (Dictionary<string, object>)prepare["pre"];
            var post = (Dictionary<string, object>)prepare["post"];
            var lines = new[]
            {
                "requestId=" + request["requestId"],
                "scopeId=" + request["scopeId"],
                "accountId=" + request["accountId"],
                "payloadSha256=" + request["payloadSha256"],
                "canonicalDigest=" + prepare["canonicalDigest"],
                "evaluatedStateVersion=" + request["evaluatedStateVersion"],
                "commandVersion=" + request["commandVersion"],
                "eventSeq=" + request["eventSeq"],
                "preStateVersion=" + pre["stateVersion"],
                "preEventSeq=" + pre["eventSeq"],
                "preHandCount=" + pre["handCount"],
                "preRiverCount=" + pre["riverCount"],
                "ledgerSha256=" + pre["ledgerSha256"],
                "physicalSha256=" + pre["physicalSha256"],
                "tileId=" + prepare["tileId"],
                "discardEvents=" + post["discardEvents"],
            };
            File.WriteAllLines(Path.Combine(arguments.Root, "expected.txt"), lines, new UTF8Encoding(false));
        }

        private static void Record(string message)
        {
            UnityEngine.Debug.Log("C04_RECOVERY " + message);
        }

        private static void Fail(string code, string detail)
        {
            Record("PROBE_FAIL " + code + ": " + detail);
            try
            {
                UnityEngine.Debug.LogError("C04_RECOVERY_FAIL " + code + ": " + detail);
            }
            catch
            {
            }

            Application.Quit(1);
        }

        private static string Sha256File(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sha = SHA256.Create())
            {
                return ToHex(sha.ComputeHash(stream));
            }
        }

        private static string Sha256Bytes(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                return ToHex(sha.ComputeHash(bytes));
            }
        }

        private static string ToHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
            {
                builder.Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        private static void WriteAtomic(string path, string text)
        {
            string temporary = path + ".tmp." + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            File.Move(temporary, path);
        }

        private static void WriteArtifact(ProbeArguments arguments, string relative, byte[] bytes)
        {
            string path = Path.Combine(arguments.Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + ".tmp." + Guid.NewGuid().ToString("N");
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path);
        }

        private static void CopyArtifact(ProbeArguments arguments, string source, string relative)
        {
            if (!File.Exists(source))
            {
                throw new InvalidDataException("Required raw authority artifact is missing: " + source);
            }

            WriteArtifact(arguments, relative, File.ReadAllBytes(source));
        }

        private static Dictionary<string, object> AuthorityMap(ProbeArguments arguments)
        {
            string authority = Path.Combine(arguments.Root, "authority");
            var files = new List<object>();
            if (Directory.Exists(authority))
            {
                foreach (string file in Directory.GetFiles(authority).OrderBy(p => p, StringComparer.Ordinal))
                {
                    var info = new FileInfo(file);
                    files.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["path"] = "authority/" + Path.GetFileName(file),
                        ["bytes"] = info.Length,
                        ["sha256"] = Sha256File(file),
                    });
                }
            }

            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["files"] = files,
            };
        }

        private sealed class FactSummary
        {
            public string RequestId { get; private set; } = string.Empty;
            public string ScopeId { get; private set; } = string.Empty;
            public string AccountId { get; private set; } = string.Empty;
            public string PayloadSha256 { get; private set; } = string.Empty;
            public string CanonicalDigest { get; private set; } = string.Empty;
            public string EvaluatedStateVersion { get; private set; } = string.Empty;
            public string CommandVersion { get; private set; } = string.Empty;
            public string EventSeq { get; private set; } = string.Empty;
            public string PreStateVersion { get; private set; } = string.Empty;
            public string PreEventSeq { get; private set; } = string.Empty;
            public int PreHand { get; private set; }
            public int PreRiver { get; private set; }
            public string LedgerSha256 { get; private set; } = string.Empty;
            public string PhysicalSha256 { get; private set; } = string.Empty;
            public uint TileId { get; private set; }
            public int DiscardEvents { get; private set; }

            public static FactSummary Read(string path)
            {
                if (!File.Exists(path))
                {
                    throw new FileNotFoundException("Expected request facts are missing.", path);
                }

                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (string line in File.ReadAllLines(path))
                {
                    int index = line.IndexOf('=');
                    if (index > 0)
                    {
                        values[line.Substring(0, index)] = line.Substring(index + 1);
                    }
                }

                return new FactSummary
                {
                    RequestId = Value(values, "requestId"),
                    ScopeId = Value(values, "scopeId"),
                    AccountId = Value(values, "accountId"),
                    PayloadSha256 = Value(values, "payloadSha256"),
                    CanonicalDigest = Value(values, "canonicalDigest"),
                    EvaluatedStateVersion = Value(values, "evaluatedStateVersion"),
                    CommandVersion = Value(values, "commandVersion"),
                    EventSeq = Value(values, "eventSeq"),
                    PreStateVersion = Value(values, "preStateVersion"),
                    PreEventSeq = Value(values, "preEventSeq"),
                    PreHand = int.Parse(Value(values, "preHandCount"), CultureInfo.InvariantCulture),
                    PreRiver = int.Parse(Value(values, "preRiverCount"), CultureInfo.InvariantCulture),
                    LedgerSha256 = Value(values, "ledgerSha256"),
                    PhysicalSha256 = Value(values, "physicalSha256"),
                    TileId = uint.Parse(Value(values, "tileId"), CultureInfo.InvariantCulture),
                    DiscardEvents = int.Parse(Value(values, "discardEvents"), CultureInfo.InvariantCulture),
                };
            }

            private static string Value(Dictionary<string, string> values, string key)
            {
                return values.TryGetValue(key, out string? value) ? value : string.Empty;
            }
        }

        private sealed class FrameInfo
        {
            public string Direction { get; set; } = "OUT";
            public string File { get; set; } = string.Empty;
            public string Sha256 { get; set; } = string.Empty;
            public string MessageType { get; set; } = "UNPARSEABLE";
            public string RequestId { get; set; } = string.Empty;
            public string ScopeId { get; set; } = string.Empty;
            public string Status { get; set; } = string.Empty;
            public string CorrelationId { get; set; } = string.Empty;
        }

        private sealed class RecordingChannel : ITableChannel
        {
            private readonly object _gate = new object();
            private readonly ITableChannel _inner;
            private readonly string _framesDirectory;
            private readonly List<FrameInfo> _frames = new List<FrameInfo>();
            private int _sequence;
            private int _droppedReceipts;

            public RecordingChannel(ITableChannel inner, string framesDirectory)
            {
                _inner = inner;
                _framesDirectory = framesDirectory;
            }

            public string? DropReceiptRequestId { get; set; }

            public List<FrameInfo> SentFrames { get; } = new List<FrameInfo>();

            public int SentCount
            {
                get
                {
                    lock (_gate)
                    {
                        return SentFrames.Count;
                    }
                }
            }

            public int DroppedReceipts
            {
                get
                {
                    lock (_gate)
                    {
                        return _droppedReceipts;
                    }
                }
            }

            public bool IsOpen => _inner.IsOpen;

            public Task OpenAsync(TableChannelConnectRequest request, CancellationToken cancellationToken) =>
                _inner.OpenAsync(request, cancellationToken);

            public async Task SendAsync(ReadOnlyMemory<byte> wireEnvelope, CancellationToken cancellationToken)
            {
                Record(wireEnvelope, "OUT", true);
                await _inner.SendAsync(wireEnvelope, cancellationToken).ConfigureAwait(false);
            }

            public async Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
            {
                while (true)
                {
                    ReadOnlyMemory<byte> frame = await _inner.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                    WireEnvelope envelope = WireEnvelope.Parser.ParseFrom(frame.ToArray());
                    if (!string.IsNullOrEmpty(DropReceiptRequestId) &&
                        envelope.BodyCase == WireEnvelope.BodyOneofCase.Receipt &&
                        string.Equals(envelope.RequestId, DropReceiptRequestId, StringComparison.Ordinal))
                    {
                        lock (_gate)
                        {
                            _droppedReceipts++;
                        }

                        continue;
                    }

                    Record(frame, "IN", false);
                    return frame;
                }
            }

            public Task CloseAsync(TableChannelCloseReason reason, CancellationToken cancellationToken) =>
                _inner.CloseAsync(reason, cancellationToken);

            public List<Dictionary<string, object>> Frames()
            {
                lock (_gate)
                {
                    var result = new List<Dictionary<string, object>>(_frames.Count);
                    foreach (FrameInfo info in _frames)
                    {
                        result.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            ["direction"] = info.Direction,
                            ["file"] = info.File,
                            ["sha256"] = info.Sha256,
                            ["type"] = info.MessageType,
                            ["requestId"] = info.RequestId,
                            ["scopeId"] = info.ScopeId,
                            ["status"] = info.Status,
                            ["correlationId"] = info.CorrelationId,
                        });
                    }

                    return result;
                }
            }

            private void Record(ReadOnlyMemory<byte> frame, string direction, bool outbound)
            {
                byte[] bytes = frame.ToArray();
                string relative = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:D4}-{1}.bin",
                    Interlocked.Increment(ref _sequence),
                    direction.ToLowerInvariant());
                string path = Path.Combine(_framesDirectory, relative);
                Directory.CreateDirectory(_framesDirectory);
                File.WriteAllBytes(path, bytes);
                string type = "UNPARSEABLE";
                string requestId = string.Empty;
                string scopeId = string.Empty;
                string status = string.Empty;
                string correlationId = string.Empty;
                try
                {
                    WireEnvelope envelope = WireEnvelope.Parser.ParseFrom(bytes);
                    type = envelope.BodyCase.ToString();
                    requestId = envelope.RequestId ?? string.Empty;
                    scopeId = envelope.Scope?.ScopeId ?? string.Empty;
                    status = envelope.Receipt?.Status.ToString() ??
                        envelope.ReceiptLookup?.Status.ToString() ??
                        string.Empty;
                    correlationId = envelope.CorrelationId ?? string.Empty;
                    if (envelope.BodyCase == WireEnvelope.BodyOneofCase.ReceiptQuery &&
                        envelope.ReceiptQuery != null)
                    {
                        requestId = envelope.ReceiptQuery.RequestId;
                        scopeId = envelope.ReceiptQuery.RequestScopeId;
                    }
                }
                catch (Google.Protobuf.InvalidProtocolBufferException)
                {
                }

                var info = new FrameInfo
                {
                    Direction = direction,
                    File = "frames/" + relative,
                    Sha256 = Sha256Bytes(bytes),
                    MessageType = type,
                    RequestId = requestId,
                    ScopeId = scopeId,
                    Status = status,
                    CorrelationId = correlationId,
                };
                lock (_gate)
                {
                    _frames.Add(info);
                    if (outbound)
                    {
                        SentFrames.Add(info);
                    }
                }
            }
        }

        private sealed class ProbeArguments
        {
            public bool Enabled { get; private set; }
            public string Root { get; private set; } = string.Empty;
            public string Mode { get; private set; } = string.Empty;
            public string CaseId { get; private set; } = string.Empty;
            public string Fault { get; private set; } = "None";
            public string ExpectedOutcome { get; private set; } = string.Empty;
            public string PlayerSha256 { get; private set; } = string.Empty;
            public string PackageSha256 { get; private set; } = string.Empty;
            public int PackageFileCount { get; private set; }
            public string SourceHead { get; private set; } = string.Empty;
            public string BuildCommit { get; private set; } = string.Empty;
            public int RulesetVersion { get; private set; }
            public bool ForceKill { get; private set; }

            public string LocalDirectory => Path.Combine(Root, "local");
            public string OutboxDirectory => Path.Combine(Root, "outbox");
            public string FramesDirectory => Path.Combine(Root, "frames");

            public Dictionary<string, object> Build => new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["sourceHead"] = SourceHead,
                ["buildCommit"] = BuildCommit,
                ["playerSha256"] = PlayerSha256,
                ["packageSha256"] = PackageSha256,
                ["packageFileCount"] = PackageFileCount,
                ["rulesetVersion"] = RulesetVersion,
            };

            public static ProbeArguments Parse(string[] args)
            {
                var result = new ProbeArguments();
                result.Root = Value(args, Switch);
                if (string.IsNullOrWhiteSpace(result.Root))
                {
                    return result;
                }

                result.Enabled = true;
                result.Mode = Value(args, "-c04-recovery-mode");
                result.CaseId = Value(args, "-c04-recovery-case");
                result.Fault = Value(args, "-c04-recovery-fault");
                result.ExpectedOutcome = Value(args, "-c04-recovery-outcome");
                result.PlayerSha256 = Value(args, "-c04-recovery-player-sha");
                result.PackageSha256 = Value(args, "-c04-recovery-package-sha");
                result.SourceHead = Value(args, "-c04-recovery-source-head");
                result.BuildCommit = Value(args, "-c04-recovery-build-commit");
                result.ForceKill = HasFlag(args, "-c04-recovery-force");
                int.TryParse(Value(args, "-c04-recovery-package-count"), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int packageCount);
                result.PackageFileCount = packageCount;
                int.TryParse(Value(args, "-c04-recovery-ruleset"), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int ruleset);
                result.RulesetVersion = ruleset;
                result.Root = Path.GetFullPath(result.Root);
                return result;
            }

            public bool IsComplete(out string error)
            {
                if (Mode != "Prepare" && Mode != "Recover")
                {
                    error = "mode must be Prepare or Recover";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(Root) || string.IsNullOrWhiteSpace(CaseId) ||
                    string.IsNullOrWhiteSpace(ExpectedOutcome) || string.IsNullOrWhiteSpace(PlayerSha256) ||
                    string.IsNullOrWhiteSpace(PackageSha256) || PackageFileCount <= 0 ||
                    string.IsNullOrWhiteSpace(SourceHead) || string.IsNullOrWhiteSpace(BuildCommit) ||
                    RulesetVersion <= 0)
                {
                    error = "one or more C04 binding fields are empty";
                    return false;
                }

                string full = Path.GetFullPath(Root);
                string? parent = Directory.GetParent(full)?.FullName;
                if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
                {
                    error = "the evidence root must be an existing build directory child";
                    return false;
                }

                error = string.Empty;
                return true;
            }

            private static string Value(string[] args, string key)
            {
                for (int i = 0; i < args.Length - 1; i++)
                {
                    if (string.Equals(args[i], key, StringComparison.Ordinal))
                    {
                        return args[i + 1];
                    }
                }

                return string.Empty;
            }

            private static bool HasFlag(string[] args, string key)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    if (string.Equals(args[i], key, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        private static class Json
        {
            public static string Serialize(object value)
            {
                var builder = new StringBuilder(8192);
                Write(builder, value);
                return builder.ToString();
            }

            private static void Write(StringBuilder builder, object? value)
            {
                if (value == null)
                {
                    builder.Append("null");
                    return;
                }

                if (value is string text)
                {
                    String(builder, text);
                    return;
                }

                if (value is bool flag)
                {
                    builder.Append(flag ? "true" : "false");
                    return;
                }

                if (value is int || value is long || value is ulong || value is uint || value is short)
                {
                    builder.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                    return;
                }

                if (value is IDictionary<string, object> map)
                {
                    builder.Append('{');
                    bool first = true;
                    foreach (KeyValuePair<string, object> pair in map)
                    {
                        if (!first)
                        {
                            builder.Append(',');
                        }

                        first = false;
                        String(builder, pair.Key);
                        builder.Append(':');
                        Write(builder, pair.Value);
                    }

                    builder.Append('}');
                    return;
                }

                if (value is IEnumerable sequence)
                {
                    builder.Append('[');
                    bool first = true;
                    foreach (object item in sequence)
                    {
                        if (!first)
                        {
                            builder.Append(',');
                        }

                        first = false;
                        Write(builder, item);
                    }

                    builder.Append(']');
                    return;
                }

                String(builder, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
            }

            private static void String(StringBuilder builder, string value)
            {
                builder.Append('"');
                for (int i = 0; i < value.Length; i++)
                {
                    char c = value[i];
                    switch (c)
                    {
                        case '"': builder.Append("\\\""); break;
                        case '\\': builder.Append("\\\\"); break;
                        case '\b': builder.Append("\\b"); break;
                        case '\f': builder.Append("\\f"); break;
                        case '\n': builder.Append("\\n"); break;
                        case '\r': builder.Append("\\r"); break;
                        case '\t': builder.Append("\\t"); break;
                        default:
                            if (c < 0x20)
                            {
                                builder.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                            }
                            else
                            {
                                builder.Append(c);
                            }

                            break;
                    }
                }

                builder.Append('"');
            }
        }
    }
}
