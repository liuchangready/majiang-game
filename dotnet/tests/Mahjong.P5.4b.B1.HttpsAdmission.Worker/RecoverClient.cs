using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts.Wire;

namespace Mahjong.P5_4b.B1.HttpsAdmission.Worker
{
    /// <summary>
    /// New OS process after the crash: reissues a fresh Lobby ticket for the SAME
    /// account/assignment, installs a new full WSS baseline first, then issues
    /// RECEIPT_QUERY with the original request id/scope and clears the disk
    /// outbox only after a definite Found receipt.
    /// </summary>
    public static class RecoverClient
    {
        public static async Task<int> RunAsync(Program.Options options)
        {
            string lobbyUrl = options.Require("lobby-url");
            int tablePort = int.Parse(options.Require("table-port"), CultureInfo.InvariantCulture);
            string caRootPath = options.Require("ca-root");
            string secretPath = options.Require("secret");
            string outboxDirectory = options.Require("outbox");
            string outputPath = options.Require("out");
            string crashPath = options.Require("crash");
            CrashClientEvidence crash = Program.ReadJson<CrashClientEvidence>(crashPath);

            GuestState guest = Program.ReadJson<GuestState>(secretPath);
            using var root = new X509Certificate2(caRootPath);
            using var lobby = new B1LobbyClient(new Uri(lobbyUrl), root);
            B1Flows.IssuedTicket ticket = await B1Flows.IssueTicketAsync(
                lobby,
                guest,
                "recover-ticket-" + Guid.NewGuid().ToString("N"),
                CancellationToken.None).ConfigureAwait(false);
            if (ticket.AssignmentId != guest.AssignmentId || ticket.SeatId != guest.Seat)
            {
                throw new InvalidOperationException("The fresh Lobby ticket no longer matches the assignment.");
            }

            var endpoint = new Uri(
                "wss://127.0.0.1:" + tablePort + "/v1/table?room=" + Uri.EscapeDataString(guest.RoomId!));
            ClientHandle handle = new ClientHandle(endpoint, root, outboxDirectory);
            handle.AccountId = guest.AccountId;
            handle.AssignmentId = guest.AssignmentId!;
            handle.Seat = guest.Seat;
            try
            {
                await handle.OpenAsync(ticket.Ticket, TimeSpan.FromSeconds(40), CancellationToken.None)
                    .ConfigureAwait(false);
                if (handle.Binding == null ||
                    handle.Binding.Role != Mahjong.Contracts.ProtocolRole.Player ||
                    handle.Binding.SeatId != guest.Seat)
                {
                    throw new InvalidOperationException("The recovery binding is not the same seat.");
                }

                int baselineCount = (int)handle.BaselineCount;
                long baselineVersion = handle.LastBaselineVersion;
                if (baselineCount < 1)
                {
                    throw new InvalidOperationException("No full baseline was installed before reconciliation.");
                }

                IReadOnlyList<PendingCommandSnapshot> pendingBefore =
                    await handle.PendingAsync(CancellationToken.None).ConfigureAwait(false);
                PendingCommandSnapshot unresolved = pendingBefore.Single();
                if (!unresolved.DeliveryUnknown || !unresolved.NeedsReconciliation)
                {
                    throw new InvalidOperationException(
                        "The reloaded disk outbox was not marked as an unknown result.");
                }

                await handle.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
                await handle.WaitForAsync(
                    () => handle.LastResolution != null,
                    TimeSpan.FromSeconds(20),
                    "the RECEIPT_QUERY resolution").ConfigureAwait(false);
                await handle.WaitForAsync(
                    () => handle.LastResolution != null &&
                          string.Equals(
                              handle.LastResolution.Command.RequestId,
                              unresolved.RequestId,
                              StringComparison.Ordinal),
                    TimeSpan.FromSeconds(5),
                    "the resolution of the original request").ConfigureAwait(false);

                TableReceiptResolvedEventArgs resolved = handle.LastResolution!;
                IReadOnlyList<PendingCommandSnapshot> pendingAfter =
                    await handle.PendingAsync(CancellationToken.None).ConfigureAwait(false);
                if (pendingAfter.Count != 0)
                {
                    throw new InvalidOperationException("The outbox was not cleared after a Found receipt.");
                }

                if (!resolved.Resolution.FromReceiptLookup ||
                    resolved.Resolution.ReceiptStatus != Mahjong.Contracts.ReceiptStatus.Applied ||
                    !resolved.Resolution.CommandVersion.HasValue ||
                    resolved.Resolution.CommandVersion.Value.Value != crash.OriginalCommandVersion)
                {
                    throw new InvalidOperationException(
                        "The receipt lookup did not resolve to the original APPLIED command version.");
                }

                PlayerSnapshot? recoveredPlayer = handle.LatestPlayer;
                PublicSnapshot? recoveredPublic = handle.LatestPublic;
                long recoveredStateVersion = recoveredPlayer != null
                    ? (long)recoveredPlayer.StateVersion
                    : (long)(recoveredPublic?.StateVersion ?? 0);
                PublicBoardView? recoveredBoard = recoveredPlayer?.PublicView?.Board ?? recoveredPublic?.View?.Board;
                bool crashActionVisible = recoveredBoard != null &&
                                          recoveredBoard.Players.Any(player =>
                                              player.Seat == crash.Seat &&
                                              player.Discards.Any(discard =>
                                                  discard.TileId == crash.DiscardTileId));
                int recoveredPlayers = recoveredBoard?.Players.Count ?? 0;
                string recoveredSessionHash = Program.Sha256Hex(handle.Binding!.SessionId.Value);
                Mahjong.Contracts.ProtocolRole recoveredRole = handle.Binding.Role;
                int recoveredSeat = handle.Binding.SeatId;

                await handle.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                await handle.DisposeAsync().ConfigureAwait(false);
                handle = null!;

                FileOutboxStore fileOutbox = new FileOutboxStore(outboxDirectory);
                IReadOnlyList<OutboxEntry> remaining =
                    await fileOutbox.LoadUnresolvedAsync(CancellationToken.None).ConfigureAwait(false);

                var evidence = new RecoverClientEvidence
                {
                    NewPid = Environment.ProcessId,
                    NewBootId = Guid.NewGuid().ToString("N"),
                    ProcessStartedUtc = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    AccountId = guest.AccountId,
                    AssignmentId = guest.AssignmentId!,
                    BaselineCount = baselineCount,
                    BaselineStateVersion = baselineVersion,
                    BaselineBeforeQuery = true,
                    PendingBeforeQuery = pendingBefore.Count,
                    DeliveryUnknownBeforeQuery = unresolved.DeliveryUnknown,
                    NeedsReconciliationBeforeQuery = unresolved.NeedsReconciliation,
                    PendingAfterQuery = pendingAfter.Count,
                    ResolvedStatus = resolved.Resolution.ReceiptStatus.ToString(),
                    ResolvedReason = resolved.Resolution.ReasonCode,
                    ResolvedFromLookup = resolved.Resolution.FromReceiptLookup,
                    ResolvedCommandVersion = resolved.Resolution.CommandVersion.Value.Value,
                    QueryDigestHash = Program.Sha256Hex(resolved.Resolution.CanonicalPayloadSha256),
                    OutboxEntriesAfter = remaining.Count,
                    OutboxDirectory = outboxDirectory,
                    RecoveredStateVersion = recoveredStateVersion,
                    RecoveredCrashActionVisible = crashActionVisible,
                    RecoveredPublicDiscardTileId = crash.DiscardTileId,
                    RecoveredPublicDiscardSeat = crash.Seat,
                    RecoveredPublicPlayerCount = recoveredPlayers,
                    RecoveredSessionIdHash = recoveredSessionHash,
                    RecoveredRole = recoveredRole.ToString(),
                    RecoveredSeat = recoveredSeat
                };
                Program.WriteJson(outputPath, evidence);
                Console.WriteLine(
                    "B1-RECOVER-OK pid=" + evidence.NewPid +
                    " baseline=" + evidence.BaselineStateVersion +
                    " foundVersion=" + evidence.ResolvedCommandVersion +
                    " pendingAfter=" + evidence.PendingAfterQuery);
                return Program.ExitOk;
            }
            finally
            {
                if (handle != null)
                {
                    await handle.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }
}
