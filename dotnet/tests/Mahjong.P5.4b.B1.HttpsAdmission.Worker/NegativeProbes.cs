using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using WireReceiptStatus = Mahjong.Contracts.Wire.ReceiptStatus;
using WireReceipt = Mahjong.Contracts.Wire.Receipt;

namespace Mahjong.P5_4b.B1.HttpsAdmission.Worker
{
    public static class NegativeProbes
    {
        public static async Task RunAsync(
            SuiteEvidence suite,
            B1LobbyClient lobby,
            X509Certificate2 goodRoot,
            int tablePort,
            string normalScopeId,
            string normalRequestId,
            string tlsDirectory)
        {
            await TlsNegativesAsync(suite, goodRoot, tlsDirectory).ConfigureAwait(false);

            GuestState conflictGuest = await B1Flows.CreateGuestAsync(lobby, "b1-conflict", CancellationToken.None)
                .ConfigureAwait(false);
            (string conflictRoom, _, _) = await B1Flows.CreateRoomAsync(
                lobby,
                conflictGuest,
                "B1 conflict " + Guid.NewGuid().ToString("N").Substring(0, 6),
                "conflict-create-" + Guid.NewGuid().ToString("N"),
                CancellationToken.None).ConfigureAwait(false);
            B1Flows.IssuedTicket conflictTicket = await B1Flows.IssueTicketAsync(
                lobby,
                conflictGuest,
                "conflict-ticket-" + Guid.NewGuid().ToString("N"),
                CancellationToken.None).ConfigureAwait(false);
            await using (var probe = new RawWssProbe(TableUri(tablePort, conflictRoom), goodRoot))
            {
                Welcome welcome = await probe.AdmitAsync(
                    conflictTicket.Ticket,
                    TimeSpan.FromSeconds(15),
                    CancellationToken.None).ConfigureAwait(false);
                string result = await RunSameKeyConflictAsync(probe, welcome).ConfigureAwait(false);
                suite.SameKeyDifferentBytesPreservedOriginal =
                    string.Equals(result, "PRESERVED", StringComparison.Ordinal);
                if (!suite.SameKeyDifferentBytesPreservedOriginal)
                {
                    throw new InvalidOperationException(
                        "Same-key different-bytes conflict did not preserve the original receipt: " + result);
                }

                suite.NegativeResults.Add("same_key_different_bytes=PRESERVED");
            }

            GuestState crossGuest = await B1Flows.CreateGuestAsync(lobby, "b1-cross", CancellationToken.None)
                .ConfigureAwait(false);
            (string crossRoom, _, _) = await B1Flows.CreateRoomAsync(
                lobby,
                crossGuest,
                "B1 cross " + Guid.NewGuid().ToString("N").Substring(0, 6),
                "cross-create-" + Guid.NewGuid().ToString("N"),
                CancellationToken.None).ConfigureAwait(false);
            B1Flows.IssuedTicket crossTicket = await B1Flows.IssueTicketAsync(
                lobby,
                crossGuest,
                "cross-ticket-" + Guid.NewGuid().ToString("N"),
                CancellationToken.None).ConfigureAwait(false);
            await using (var probe = new RawWssProbe(TableUri(tablePort, crossRoom), goodRoot))
            {
                await probe.AdmitAsync(crossTicket.Ticket, TimeSpan.FromSeconds(15), CancellationToken.None)
                    .ConfigureAwait(false);
                Envelope response = await probe.QueryReceiptAsync(
                    normalScopeId,
                    normalRequestId,
                    TimeSpan.FromSeconds(10),
                    CancellationToken.None).ConfigureAwait(false);
                suite.CrossAccountReceiptQueryRejected =
                    response.BodyCase == Envelope.BodyOneofCase.Error &&
                    response.Error != null &&
                    string.Equals(response.Error.DetailCode, "SCOPE_MISMATCH", StringComparison.Ordinal);
                if (!suite.CrossAccountReceiptQueryRejected)
                {
                    throw new InvalidOperationException("A foreign account receipt query was not refused with SCOPE_MISMATCH.");
                }

                suite.NegativeResults.Add("cross_account_query=SCOPE_MISMATCH");
            }

            GuestState specHost = await B1Flows.CreateGuestAsync(lobby, "b1-spec-host", CancellationToken.None)
                .ConfigureAwait(false);
            (string specRoom, _, _) = await B1Flows.CreateRoomAsync(
                lobby,
                specHost,
                "B1 spec " + Guid.NewGuid().ToString("N").Substring(0, 6),
                "spec-create-" + Guid.NewGuid().ToString("N"),
                CancellationToken.None).ConfigureAwait(false);
            GuestState specGuest = await B1Flows.CreateGuestAsync(lobby, "b1-spec-guest", CancellationToken.None)
                .ConfigureAwait(false);
            await B1Flows.SpectateAsync(
                lobby,
                specGuest,
                specRoom,
                "spec-join-" + Guid.NewGuid().ToString("N"),
                CancellationToken.None).ConfigureAwait(false);
            B1Flows.IssuedTicket specTicket = await B1Flows.IssueTicketAsync(
                lobby,
                specGuest,
                "spec-ticket-" + Guid.NewGuid().ToString("N"),
                CancellationToken.None).ConfigureAwait(false);
            await using (var probe = new RawWssProbe(TableUri(tablePort, specRoom), goodRoot))
            {
                Welcome welcome = await probe.AdmitAsync(
                    specTicket.Ticket,
                    TimeSpan.FromSeconds(15),
                    CancellationToken.None).ConfigureAwait(false);
                BusinessPayload payload = CommandPayloadFactory.SubmitAction(
                    "round-b1-spectator",
                    "missing-decision",
                    0,
                    ProtocolActionType.DiscardTile,
                    tileIds: new uint[] { 1 });
                Envelope response = await probe.SendCommandAsync(
                    "spectator-command-" + Guid.NewGuid().ToString("N"),
                    WireCommand(payload, welcome.Baseline.StateVersion, "round-b1-spectator", "missing-decision"),
                    0,
                    TimeSpan.FromSeconds(10),
                    CancellationToken.None).ConfigureAwait(false);
                suite.SpectatorPlayerCommandRejected =
                    (response.BodyCase == Envelope.BodyOneofCase.Error &&
                     response.Error != null &&
                     string.Equals(response.Error.DetailCode, "SCOPE_ROLE_NOT_AUTHORIZED", StringComparison.Ordinal)) ||
                    response.BodyCase == Envelope.BodyOneofCase.Close;
                if (!suite.SpectatorPlayerCommandRejected)
                {
                    throw new InvalidOperationException("A spectator player command was not refused by the service.");
                }

                suite.NegativeResults.Add("spectator_player_command=SCOPE_ROLE_NOT_AUTHORIZED");
            }

            GuestState expiring = await B1Flows.CreateGuestAsync(lobby, "b1-expire", CancellationToken.None)
                .ConfigureAwait(false);
            (string expireRoom, _, _) = await B1Flows.CreateRoomAsync(
                lobby,
                expiring,
                "B1 expire " + Guid.NewGuid().ToString("N").Substring(0, 6),
                "expire-create-" + Guid.NewGuid().ToString("N"),
                CancellationToken.None).ConfigureAwait(false);
            B1Flows.IssuedTicket expiringTicket = await B1Flows.IssueTicketAsync(
                lobby,
                expiring,
                "expire-ticket-" + Guid.NewGuid().ToString("N"),
                CancellationToken.None).ConfigureAwait(false);

            string tampered = Tamper(expiringTicket.Ticket);
            suite.TamperedTicketRejected = await AdmissionRejectedAsync(
                TableUri(tablePort, expireRoom),
                goodRoot,
                tampered,
                TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            if (!suite.TamperedTicketRejected)
            {
                throw new InvalidOperationException("A tampered ticket was not rejected.");
            }

            suite.ForeignRoomTicketRejected = await AdmissionRejectedAsync(
                TableUri(tablePort, "b1-other-room"),
                goodRoot,
                expiringTicket.Ticket,
                TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            if (!suite.ForeignRoomTicketRejected)
            {
                throw new InvalidOperationException("A ticket used against a foreign room was not rejected.");
            }

            DateTimeOffset expiresAt = DateTimeOffset.Parse(
                expiringTicket.ExpiresAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);
            TimeSpan remaining = expiresAt.AddSeconds(6) - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining).ConfigureAwait(false);
            }

            suite.ExpiredTicketRejected = await AdmissionRejectedAsync(
                TableUri(tablePort, expireRoom),
                goodRoot,
                expiringTicket.Ticket,
                TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            if (!suite.ExpiredTicketRejected)
            {
                throw new InvalidOperationException("A genuinely expired Lobby ticket was not rejected.");
            }

            suite.NegativeResults.Add("tampered_ticket=rejected");
            suite.NegativeResults.Add("foreign_room_ticket=rejected");
            suite.NegativeResults.Add("expired_lobby_ticket=rejected");
        }

        private static async Task TlsNegativesAsync(
            SuiteEvidence suite,
            X509Certificate2 goodRoot,
            string tlsDirectory)
        {
            string goodLeaf = Path.Combine(tlsDirectory, "leaf.pfx");
            string wrongNameLeaf = Path.Combine(tlsDirectory, "leaf-wrongname.pfx");
            string expiredLeaf = Path.Combine(tlsDirectory, "leaf-expired.pfx");
            string otherRootPem = Path.Combine(tlsDirectory, "root-other.pem");
            using var otherRoot = new X509Certificate2(otherRootPem);

            await using (TlsNegativeServer server = TlsNegativeServer.Start(goodLeaf))
            {
                suite.TlsHttpsUntrustedRootRejected = await HttpsRejectedAsync(server.Port, otherRoot).ConfigureAwait(false);
                suite.TlsWssUntrustedRootRejected = await WssRejectedAsync(server.Port, otherRoot).ConfigureAwait(false);
            }

            await using (TlsNegativeServer server = TlsNegativeServer.Start(wrongNameLeaf))
            {
                suite.TlsHttpsWrongNameRejected = await HttpsRejectedAsync(server.Port, goodRoot).ConfigureAwait(false);
                suite.TlsWssWrongNameRejected = await WssRejectedAsync(server.Port, goodRoot).ConfigureAwait(false);
            }

            await using (TlsNegativeServer server = TlsNegativeServer.Start(expiredLeaf))
            {
                suite.TlsHttpsExpiredRejected = await HttpsRejectedAsync(server.Port, goodRoot).ConfigureAwait(false);
                suite.TlsWssExpiredRejected = await WssRejectedAsync(server.Port, goodRoot).ConfigureAwait(false);
            }

            if (!suite.TlsHttpsUntrustedRootRejected || !suite.TlsHttpsWrongNameRejected ||
                !suite.TlsHttpsExpiredRejected || !suite.TlsWssUntrustedRootRejected ||
                !suite.TlsWssWrongNameRejected || !suite.TlsWssExpiredRejected)
            {
                throw new InvalidOperationException("At least one strict HTTPS/WSS certificate negative was accepted.");
            }

            suite.NegativeResults.Add("tls_untrusted_root=rejected_https_and_wss");
            suite.NegativeResults.Add("tls_wrong_san=rejected_https_and_wss");
            suite.NegativeResults.Add("tls_expired=rejected_https_and_wss");
        }

        private static async Task<bool> HttpsRejectedAsync(int port, X509Certificate2 trustRoot)
        {
            using var client = new B1LobbyClient(new Uri("https://127.0.0.1:" + port), trustRoot);
            try
            {
                await client.GetAsync("/health/live", CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            return client.LastRejectionReason != null;
        }

        private static async Task<bool> WssRejectedAsync(int port, X509Certificate2 trustRoot)
        {
            var endpoint = new Uri("wss://127.0.0.1:" + port + "/v1/table?room=b1-tls-negative");
            using var channel = new RealWssTableChannel(endpoint, trustRoot);
            try
            {
                await channel.OpenAsync(
                    new TableChannelConnectRequest("ticket-not-used", "b1-tls-negative", null),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            return channel.LastRejectionReason != null;
        }

        private static async Task<string> RunSameKeyConflictAsync(RawWssProbe probe, Welcome welcome)
        {
            string requestId = "conflict-" + Guid.NewGuid().ToString("N");
            string round = "round-b1-conflict";
            BusinessPayload firstPayload = CommandPayloadFactory.SubmitAction(
                round,
                "missing-decision-b1",
                0,
                ProtocolActionType.DiscardTile,
                tileIds: new uint[] { 1 });
            Envelope first = await probe.SendCommandAsync(
                requestId,
                WireCommand(firstPayload, welcome.Baseline.StateVersion, round, "missing-decision-b1"),
                0,
                TimeSpan.FromSeconds(10),
                CancellationToken.None).ConfigureAwait(false);
            if (first.BodyCase != Envelope.BodyOneofCase.Receipt || first.Receipt == null)
            {
                return "NO_FIRST_RECEIPT_" + first.BodyCase;
            }

            string originalStatus = first.Receipt.Status.ToString();
            ulong originalVersion = first.Receipt.HasCommandVersion ? first.Receipt.CommandVersion : 0;
            ulong evaluated = first.Receipt.EvaluatedStateVersion;

            BusinessPayload secondPayload = CommandPayloadFactory.SubmitAction(
                round,
                "missing-decision-b1",
                0,
                ProtocolActionType.Pass);
            Envelope second = await probe.SendCommandAsync(
                requestId,
                WireCommand(secondPayload, welcome.Baseline.StateVersion, round, "missing-decision-b1"),
                0,
                TimeSpan.FromSeconds(10),
                CancellationToken.None).ConfigureAwait(false);
            bool conflictReported =
                (second.BodyCase == Envelope.BodyOneofCase.Error &&
                 second.Error != null &&
                 (string.Equals(second.Error.DetailCode, "REQUEST_ID_CONFLICT", StringComparison.Ordinal) ||
                  string.Equals(second.Error.Code, "REQUEST_ID_CONFLICT", StringComparison.Ordinal) ||
                  second.Error.DetailCode.Contains("CONFLICT", StringComparison.OrdinalIgnoreCase) ||
                  second.Error.Code.Contains("CONFLICT", StringComparison.OrdinalIgnoreCase))) ||
                (second.BodyCase == Envelope.BodyOneofCase.Receipt &&
                 second.Receipt != null &&
                 second.Receipt.Status == WireReceiptStatus.Rejected);
            if (!conflictReported)
            {
                return "SECOND_NOT_CONFLICT_" + second.BodyCase + "_" +
                       (second.Error == null ? string.Empty : second.Error.Code + "/" + second.Error.DetailCode);
            }

            Envelope lookup = await probe.QueryReceiptAsync(
                welcome.Scope.ScopeId,
                requestId,
                TimeSpan.FromSeconds(10),
                CancellationToken.None).ConfigureAwait(false);
            if (lookup.BodyCase != Envelope.BodyOneofCase.ReceiptLookup ||
                lookup.ReceiptLookup == null ||
                lookup.ReceiptLookup.Status != LookupStatus.Found ||
                lookup.ReceiptLookup.Receipt == null)
            {
                return "ORIGINAL_NOT_FOUND";
            }

            WireReceipt original = lookup.ReceiptLookup.Receipt;
            bool preserved =
                original.Status.ToString() == originalStatus &&
                original.EvaluatedStateVersion == evaluated &&
                (original.HasCommandVersion ? original.CommandVersion : 0) == originalVersion;
            return preserved ? "PRESERVED" : "ORIGINAL_CHANGED";
        }

        private static Command WireCommand(
            BusinessPayload payload,
            ulong expectedStateVersion,
            string round,
            string decision)
        {
            return new Command
            {
                Type = CommandType.SubmitAction,
                CanonicalPayload = ByteString.CopyFrom(payload.CopyBytes()),
                PayloadSchemaVersion = CanonicalPayloadDigest.FormatVersion,
                ExpectedStateVersion = expectedStateVersion,
                ExpectedRoundId = round,
                ExpectedDecisionId = decision
            };
        }

        private static async Task<bool> AdmissionRejectedAsync(
            Uri endpoint,
            X509Certificate2 root,
            string ticket,
            TimeSpan timeout)
        {
            await using var probe = new RawWssProbe(endpoint, root);
            try
            {
                await probe.AdmitAsync(ticket, timeout, CancellationToken.None).ConfigureAwait(false);
                return false;
            }
            catch (RawWssProbe.ProbeRejectedException)
            {
                return true;
            }
            catch (TableChannelClosedException)
            {
                return true;
            }
        }

        private static Uri TableUri(int port, string room)
        {
            return new Uri("wss://127.0.0.1:" + port + "/v1/table?room=" + Uri.EscapeDataString(room));
        }

        private static string Tamper(string ticket)
        {
            char[] chars = ticket.ToCharArray();
            int index = chars.Length - 6;
            chars[index] = chars[index] == 'A' ? 'B' : 'A';
            return new string(chars);
        }
    }
}
