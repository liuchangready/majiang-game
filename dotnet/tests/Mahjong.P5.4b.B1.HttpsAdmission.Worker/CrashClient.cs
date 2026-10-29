using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using WireDecisionView = Mahjong.Contracts.Wire.DecisionView;
using WireLegalActionView = Mahjong.Contracts.Wire.LegalActionView;
using WireReceiptStatus = Mahjong.Contracts.Wire.ReceiptStatus;

namespace Mahjong.P5_4b.B1.HttpsAdmission.Worker
{
    /// <summary>
    /// The independent OS client that performs a real player command, loses the
    /// final receipt at the documented transport decorator, resends the EXACT
    /// original bytes once, and remains alive holding its unresolved disk outbox
    /// until the runner/parent kills it with a real process kill.
    /// </summary>
    public static class CrashClient
    {
        public static async Task<int> RunAsync(Program.Options options)
        {
            string lobbyUrl = options.Require("lobby-url");
            int tablePort = int.Parse(options.Require("table-port"), CultureInfo.InvariantCulture);
            string caRootPath = options.Require("ca-root");
            string secretPath = options.Require("secret");
            string outboxDirectory = options.Require("outbox");
            string outputPath = options.Require("out");
            string startedPath = options.Require("started");

            GuestState guest = Program.ReadJson<GuestState>(secretPath);
            using var root = new X509Certificate2(caRootPath);
            using var lobby = new B1LobbyClient(new Uri(lobbyUrl), root);
            B1Flows.IssuedTicket ticket = await B1Flows.IssueTicketAsync(
                lobby,
                guest,
                "crash-ticket-" + Guid.NewGuid().ToString("N"),
                CancellationToken.None).ConfigureAwait(false);
            if (ticket.AssignmentId != guest.AssignmentId ||
                ticket.SeatId != guest.Seat ||
                !string.Equals(ticket.RoomId, guest.RoomId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The lobby ticket does not bind the crash client's assignment.");
            }

            string room = guest.RoomId!;
            var endpoint = new Uri("wss://127.0.0.1:" + tablePort + "/v1/table?room=" + Uri.EscapeDataString(room));
            await using var handle = new ClientHandle(endpoint, root, outboxDirectory);
            handle.AccountId = guest.AccountId;
            handle.AssignmentId = guest.AssignmentId!;
            handle.Seat = guest.Seat;
            await handle.OpenAsync(ticket.Ticket, TimeSpan.FromSeconds(40), CancellationToken.None)
                .ConfigureAwait(false);
            if (handle.Binding == null ||
                handle.Binding.Role != ProtocolRole.Player ||
                handle.Binding.SeatId != guest.Seat)
            {
                throw new InvalidOperationException("The real client binding is not the crash seat.");
            }

            Program.WriteJson(startedPath, new
            {
                pid = Environment.ProcessId,
                bootId = Guid.NewGuid().ToString("N"),
                baselineVersion = handle.LastBaselineVersion,
                role = handle.Binding.Role.ToString(),
                seat = handle.Binding.SeatId
            });
            Console.WriteLine("B1-CRASH-ADMITTED pid=" + Environment.ProcessId);

            (PlayerSnapshot snapshot, WireDecisionView decision, WireLegalActionView discard) =
                await B1Actions.WaitForDiscardAsync(handle, TimeSpan.FromSeconds(60), CancellationToken.None)
                    .ConfigureAwait(false);
            string requestId = "req-crash-" + Guid.NewGuid().ToString("N");
            ProtocolCommand command = B1Actions.BuildCommand(handle, snapshot, decision, discard, requestId);
            string scopeId = handle.Binding.Scope.ScopeId.Value;
            handle.Loss.TargetRequestId = requestId;
            handle.Loss.DropTargetReceipts = true;

            CommandSubmission submission = await handle.SubmitAsync(command, CancellationToken.None)
                .ConfigureAwait(false);
            if (!submission.Sent)
            {
                throw new InvalidOperationException("The crash command never reached the real transport.");
            }

            await WaitTaskAsync(handle.Loss.FirstDropped, TimeSpan.FromSeconds(30), "first dropped receipt")
                .ConfigureAwait(false);
            IReadOnlyList<ReceiptLossChannel.ReceiptObservation> firstObservations = handle.Loss.ObservedReceipts;
            ReceiptLossChannel.ReceiptObservation original = firstObservations[0];
            if (original.Status != WireReceiptStatus.Applied || !original.CommandVersion.HasValue)
            {
                throw new InvalidOperationException(
                    "The original crash action was not APPLIED (status=" + original.Status + ").");
            }

            IReadOnlyList<byte[]> captured = handle.Loss.CapturedCommandFrames;
            if (captured.Count != 1)
            {
                throw new InvalidOperationException("Expected exactly one captured COMMAND frame.");
            }

            // EXACT same request id, scope and canonical business bytes, over the
            // same real TLS WebSocket. The replay receipt is dropped too; the
            // assertion is the receipt identity (same command version, replayed).
            await handle.Transport.SendAsync(captured[0], CancellationToken.None).ConfigureAwait(false);
            await WaitTaskAsync(handle.Loss.SecondDropped, TimeSpan.FromSeconds(30), "second dropped receipt")
                .ConfigureAwait(false);
            ReceiptLossChannel.ReceiptObservation replay = handle.Loss.ObservedReceipts[1];
            if (replay.Status != original.Status ||
                replay.CommandVersion != original.CommandVersion ||
                !replay.Replayed)
            {
                throw new InvalidOperationException(
                    "The exact-byte replay did not return the original receipt identity (status=" +
                    replay.Status + " version=" + replay.CommandVersion + " replayed=" + replay.Replayed + ").");
            }

            IReadOnlyList<PendingCommandSnapshot> pending = await handle.PendingAsync(CancellationToken.None)
                .ConfigureAwait(false);
            PendingCommandSnapshot? unresolved = pending.FirstOrDefault(
                item => string.Equals(item.RequestId, requestId, StringComparison.Ordinal));
            if (unresolved == null)
            {
                throw new InvalidOperationException("The real Client outbox lost the unresolved crash command.");
            }

            var evidence = new CrashClientEvidence
            {
                Pid = Environment.ProcessId,
                BootId = Guid.NewGuid().ToString("N"),
                ProcessStartedUtc = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                AccountId = guest.AccountId,
                AssignmentId = guest.AssignmentId!,
                Seat = guest.Seat,
                RoomId = room,
                RequestIdHash = Program.Sha256Hex(requestId),
                ScopeIdHash = Program.Sha256Hex(scopeId),
                BaselineStateVersion = handle.LastBaselineVersion,
                ExpectedStateVersion = snapshot.StateVersion,
                OriginalReceiptStatus = original.Status.ToString(),
                ReplayReceiptStatus = replay.Status.ToString(),
                ReplayReplayed = replay.Replayed,
                OriginalCommandVersion = original.CommandVersion!.Value,
                ReplayCommandVersion = replay.CommandVersion!.Value,
                DiscardTileId = discard.TileIds[0],
                RoundId = snapshot.RoundId,
                CapturedCommandFrames = captured.Count,
                DroppedReceiptCount = handle.Loss.DroppedReceiptCount,
                OutboxDirectory = outboxDirectory,
                OutboxRawSha256 = HashDirectory(outboxDirectory),
                PendingBeforeKill = pending.Count,
                HoldArmed = true
            };
            Program.WriteJson(outputPath, evidence);
            Console.WriteLine(
                "B1-CRASH-READY pid=" + evidence.Pid +
                " version=" + evidence.OriginalCommandVersion +
                " dropped=" + evidence.DroppedReceiptCount +
                " outbox=" + evidence.PendingBeforeKill);
            Console.Out.Flush();
            await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None).ConfigureAwait(false);
            return Program.ExitOk;
        }

        private static async Task WaitTaskAsync(Task task, TimeSpan timeout, string what)
        {
            Task completed = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
            if (completed != task)
            {
                throw new TimeoutException("Timed out waiting for " + what + ".");
            }
        }

        private static string HashDirectory(string directory)
        {
            if (!Directory.Exists(directory))
            {
                return Program.Sha256Hex("<missing>");
            }

            var builder = new StringBuilder();
            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                         .OrderBy(item => item, StringComparer.Ordinal))
            {
                builder.Append(Path.GetFileName(file));
                builder.Append(':');
                builder.Append(Program.Sha256File(file));
                builder.Append('\n');
            }

            return Program.Sha256Hex(builder.ToString());
        }
    }
}
