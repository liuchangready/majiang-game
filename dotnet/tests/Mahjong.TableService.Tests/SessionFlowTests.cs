using System;
using System.Threading.Tasks;
using Google.Protobuf;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Xunit;

namespace Mahjong.TableService.Tests
{
    /// <summary>
    /// Post-admission session flow: application heartbeats, the serialized command pipeline
    /// through the real actor (receipts, idempotent replay, durable receipt lookup), sync and
    /// the atomic baseline, spectator zero-side-effect gating and identity fences.
    /// </summary>
    public sealed class SessionFlowTests
    {
        [Fact]
        public async Task HeartbeatPing_EchoesExactNonce()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            TableTestClient client = await host.ConnectPlayerAsync("room-h", "acct-h", 1);
            string nonce = TableWire.RandomNonce();
            await client.SendAsync(TableWire.HeartbeatPing(nonce));
            Envelope pong = await client.ReadUntilAsync(
                envelope => envelope.MessageType == MessageType.Heartbeat &&
                            envelope.Heartbeat.BeatCase == Heartbeat.BeatOneofCase.Pong,
                TimeSpan.FromSeconds(10));
            Assert.Equal(nonce, pong.Heartbeat.Pong.Nonce);
            await client.DisposeAsync();
        }

        [Fact]
        public async Task MalformedHeartbeatNonce_IsAnErrorNotAClose()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            TableTestClient client = await host.ConnectPlayerAsync("room-h", "acct-h", 1);
            // The frozen codec refuses to encode a malformed nonce, so the wire bytes are
            // produced by the protobuf serializer directly — exactly what a hostile or
            // buggy client would put on the wire.
            await client.SendAsync(TableWire.HeartbeatPing("NOT-HEX-NONCE").ToByteArray());
            Envelope error = await client.ReadUntilAsync(
                envelope => envelope.MessageType == MessageType.Error,
                TimeSpan.FromSeconds(10));
            Assert.Equal(ProtocolErrorCode.MalformedMessage, error.Error.Code);
            Assert.Equal("NONCE_INVALID", error.Error.DetailCode);

            // The session survives; a good ping still answers.
            string nonce = TableWire.RandomNonce();
            await client.SendAsync(TableWire.HeartbeatPing(nonce));
            Envelope pong = await client.ReadUntilAsync(
                envelope => envelope.MessageType == MessageType.Heartbeat &&
                            envelope.Heartbeat.BeatCase == Heartbeat.BeatOneofCase.Pong,
                TimeSpan.FromSeconds(10));
            Assert.Equal(nonce, pong.Heartbeat.Pong.Nonce);
            await client.DisposeAsync();
        }

        [Fact]
        public async Task IdleClient_IsClosedWithHeartbeatTimeout()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync(
                options =>
                {
                    options.HeartbeatIntervalMs = 500;
                    options.IdleDeadMs = 1500;
                });
            TableTestClient client = await TableTestClient.ConnectAsync(host.WsUrl + "?room=room-idle");
            string ticket = host.Signer.Sign(new TableTestTicket("acct-idle", "room-idle", TableAdmissionRole.Player, 0));
            await client.SendAsync(TableWire.Hello(ticket));
            await client.ReceiveAsync(TimeSpan.FromSeconds(5)); // WELCOME

            // Read but never send: application-level liveness decays while transport
            // keep-alives (handled inside the stacks) stay green.
            (int code, string reason, _) = await client.ExpectCloseAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(4429, code);
            Assert.Equal(ProtocolErrorCode.HeartbeatTimeout, reason);
        }

        [Fact]
        public async Task CommandReceipt_ReplayAndDurableLookup()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            TableTestClient client = await host.ConnectPlayerAsync("room-cmd", "acct-cmd", 0);
            Welcome welcome = client.Welcome!;
            await DrainBaselineAsync(client);

            string requestId = TableWire.RandomId("req");
            byte[] payload = TableWire.SubmitActionPayload(
                "room-cmd-r1",
                "decision-does-not-exist-yet",
                0,
                (uint)ProtocolActionType.DrawTile);
            Envelope command = TableWire.Command(
                welcome, requestId, Contracts.CommandKind.SubmitAction, payload);
            command.CorrelationId = "corr-1";
            // B1-BLOCKER-01: send the exact retained wire bytes once, then replay those SAME
            // bytes below. This is the production outbox/retry shape, not a rebuilt envelope.
            byte[] commandBytes = command.ToByteArray();
            await client.SendAsync(commandBytes);

            Envelope receipt = await client.ReadUntilAsync(
                envelope => envelope.MessageType == MessageType.Receipt,
                TimeSpan.FromSeconds(15));
            Assert.Equal(requestId, receipt.RequestId);
            Assert.Equal("corr-1", receipt.CorrelationId);
            TableReceiptAssertions.AssertAuthoritativeScope(receipt, welcome);
            Contracts.Wire.Receipt body = receipt.Receipt;
            Assert.True(
                body.Status == Contracts.Wire.ReceiptStatus.Applied ||
                body.Status == Contracts.Wire.ReceiptStatus.Rejected,
                "receipt status must be authoritative: " + body.Status);
            Assert.False(string.IsNullOrEmpty(body.ReasonCode));
            // B1-BLOCKER-01: the wire digest is the Client's MJC1 idempotency digest, not a
            // bare SHA-256 of the bytes; a raw digest makes the Client resolve a Conflict.
            Assert.Equal(CanonicalPayloadDigest.Compute(payload).ToHex(), body.CanonicalPayloadSha256);
            Assert.False(body.Replayed);

            // Exact retry: the exact original wire bytes are answered from the receipt ledger
            // and retain the original request/correlation/scope/digest/version identity.
            await client.SendAsync(commandBytes);
            Envelope replay = await client.ReadUntilAsync(
                envelope => envelope.MessageType == MessageType.Receipt,
                TimeSpan.FromSeconds(15));
            Assert.True(replay.Receipt.Replayed, "an exact retry must be marked replayed");
            Assert.Equal(requestId, replay.RequestId);
            Assert.Equal("corr-1", replay.CorrelationId);
            TableReceiptAssertions.AssertAuthoritativeScope(replay, welcome);
            Assert.Equal(body.Status, replay.Receipt.Status);
            Assert.Equal(body.ReasonCode, replay.Receipt.ReasonCode);
            Assert.Equal(body.CanonicalPayloadSha256, replay.Receipt.CanonicalPayloadSha256);
            Assert.Equal(body.EvaluatedStateVersion, replay.Receipt.EvaluatedStateVersion);
            Assert.Equal(body.CommandVersion, replay.Receipt.CommandVersion);

            // Same key with different canonical bytes is a conflict with zero side effects;
            // it never overwrites the original receipt identity.
            byte[] otherPayload = TableWire.SubmitActionPayload(
                "room-cmd-r1",
                "decision-does-not-exist-yet",
                0,
                (uint)ProtocolActionType.Pass);
            await client.SendAsync(TableWire.Command(
                welcome, requestId, Contracts.CommandKind.SubmitAction, otherPayload));
            Envelope conflict = await client.ReadUntilAsync(
                envelope => envelope.MessageType == MessageType.Error &&
                            envelope.Error.Code == ProtocolErrorCode.RequestIdConflict,
                TimeSpan.FromSeconds(15));
            Assert.Equal("request_id", conflict.Error.FieldPath);

            // Durable lookup within the session scope. The frozen actor's QueryReceiptAsync
            // converts the stored TableDurableReceipt (whose PayloadSha256 is mandatory) into
            // a wire receipt via Receipt.AsReplay() WITHOUT forwarding the digest and without
            // ever passing one into Receipt.Applied/Rejected — so CanonicalPayloadSha256 is
            // always empty on actor-backed lookups (frozen-contract boundary, recorded in the
            // delivery report). Payload binding is verifiable on the immediate receipt above;
            // what is contractual on the lookup path: the same request id resolves as Found,
            // marked as a replay, with the authoritative status, reason and state version.
            Envelope query = TableWire.ReceiptQuery(
                welcome, welcome.Scope.ScopeId, requestId);
            query.CorrelationId = "corr-lookup";
            await client.SendAsync(query);
            Envelope lookup = await client.ReadUntilAsync(
                envelope => envelope.MessageType == MessageType.ReceiptLookup,
                TimeSpan.FromSeconds(15));
            Assert.Equal(LookupStatus.Found, lookup.ReceiptLookup.Status);
            Assert.Equal(requestId, lookup.RequestId);
            Assert.Equal("corr-lookup", lookup.CorrelationId);
            // A lookup may target a HISTORICAL scope: never fabricate this connection's
            // current scope onto the answer as if it owned the queried old scope.
            Assert.Null(lookup.Scope);
            // The durable receipt stores the actor's raw TablePayloadSha256, not the Client's
            // MJC1 idempotency digest. Forwarding that raw value would make the Client resolve
            // every lookup as a Conflict, so the lookup carries no digest and the Client binds
            // the answer to its own retained expected digest (the frozen history contract).
            Assert.True(
                string.IsNullOrEmpty(lookup.ReceiptLookup.Receipt.CanonicalPayloadSha256),
                "a lookup must not forward the actor's raw digest under the Client digest field");
            Assert.True(lookup.ReceiptLookup.Receipt.Replayed, "a durable lookup answers with the replayed receipt");
            Assert.Equal(body.Status, lookup.ReceiptLookup.Receipt.Status);
            Assert.Equal(body.ReasonCode, lookup.ReceiptLookup.Receipt.ReasonCode);
            Assert.Equal(body.EvaluatedStateVersion, lookup.ReceiptLookup.Receipt.EvaluatedStateVersion);
            await client.DisposeAsync();
        }

        [Fact]
        public async Task ReceiptQuery_ForeignScopeRefusedAndUnknownIdNotFound()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            TableTestClient client = await host.ConnectPlayerAsync("room-q", "acct-q", 2);
            Welcome welcome = client.Welcome!;
            await DrainBaselineAsync(client);

            await client.SendAsync(TableWire.ReceiptQuery(
                welcome, "scope-from-another-session", TableWire.RandomId("req")));
            Envelope mismatch = await client.ReadUntilAsync(
                envelope => envelope.MessageType == MessageType.Error,
                TimeSpan.FromSeconds(10));
            Assert.Equal(ProtocolErrorCode.MalformedMessage, mismatch.Error.Code);
            Assert.Equal("SCOPE_MISMATCH", mismatch.Error.DetailCode);

            await client.SendAsync(TableWire.ReceiptQuery(
                welcome, welcome.Scope.ScopeId, "req-never-seen-0001"));
            Envelope lookup = await client.ReadUntilAsync(
                envelope => envelope.MessageType == MessageType.ReceiptLookup,
                TimeSpan.FromSeconds(10));
            Assert.Equal(LookupStatus.NotFound, lookup.ReceiptLookup.Status);
            await client.DisposeAsync();
        }

        [Fact]
        public async Task CommandIdentityFences_AreErrorsBeforeTheActor()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            TableTestClient client = await host.ConnectPlayerAsync("room-fence", "acct-fence", 0);
            Welcome welcome = client.Welcome!;
            await DrainBaselineAsync(client);

            byte[] payload = TableWire.SubmitActionPayload(
                "room-fence-r1", "decision-x", 0, (uint)ProtocolActionType.Pass);

            // Lying session id
            await client.SendAsync(TableWire.Command(
                welcome, TableWire.RandomId("req"), Contracts.CommandKind.SubmitAction, payload,
                sessionId: "not-my-session"));
            Envelope sessionError = await client.ReadUntilAsync(
                envelope => envelope.MessageType == MessageType.Error,
                TimeSpan.FromSeconds(10));
            Assert.Equal(ProtocolErrorCode.ProtocolViolation, sessionError.Error.Code);
            Assert.Equal("SESSION_MISMATCH", sessionError.Error.DetailCode);

            // Lying seat
            await client.SendAsync(TableWire.Command(
                welcome, TableWire.RandomId("req"), Contracts.CommandKind.SubmitAction, payload,
                seatId: 3));
            Envelope seatError = await client.ReadUntilAsync(
                envelope => envelope.MessageType == MessageType.Error,
                TimeSpan.FromSeconds(10));
            Assert.Equal("SEAT_MISMATCH", seatError.Error.DetailCode);

            // Echoing a foreign request scope
            await client.SendAsync(TableWire.Command(
                welcome, TableWire.RandomId("req"), Contracts.CommandKind.SubmitAction, payload,
                scope: new Contracts.Wire.RequestScope
                {
                    ScopeId = "scope-from-someone-else",
                    AccountId = welcome.Scope.AccountId,
                    Role = Contracts.Wire.Role.Player,
                    RoomId = welcome.RoomId
                }));
            Envelope scopeError = await client.ReadUntilAsync(
                envelope => envelope.MessageType == MessageType.Error,
                TimeSpan.FromSeconds(10));
            Assert.Equal("SCOPE_MISMATCH", scopeError.Error.DetailCode);

            // The scope id matches but the client lies about every OTHER owner field. The
            // server fences by id only and must answer with its own admission identity, not
            // these forged fields (B1-BLOCKER-01). The command itself is an authoritative
            // rejection, still a real receipt from the actor path.
            string forgedRequest = TableWire.RandomId("req");
            await client.SendAsync(TableWire.Command(
                welcome, forgedRequest, Contracts.CommandKind.SubmitAction, payload,
                scope: new Contracts.Wire.RequestScope
                {
                    ScopeId = welcome.Scope.ScopeId,
                    AccountId = "forged-account",
                    Role = Contracts.Wire.Role.Spectator,
                    RoomId = "forged-room",
                    IssuedAtMs = 1,
                    ExpiresAtMs = 2
                }));
            Envelope forgedReceipt = await client.ReadUntilAsync(
                envelope => envelope.MessageType == MessageType.Receipt &&
                            envelope.RequestId == forgedRequest,
                TimeSpan.FromSeconds(10));
            Assert.True(
                forgedReceipt.Receipt.Status == Contracts.Wire.ReceiptStatus.Applied ||
                forgedReceipt.Receipt.Status == Contracts.Wire.ReceiptStatus.Rejected,
                "a forged owner-field command still gets an authoritative receipt: " +
                forgedReceipt.Receipt.Status);
            TableReceiptAssertions.AssertAuthoritativeScope(forgedReceipt, welcome);
            Assert.NotEqual("forged-account", forgedReceipt.Scope.AccountId);
            Assert.NotEqual("forged-room", forgedReceipt.Scope.RoomId);
            Assert.NotEqual(Contracts.Wire.Role.Spectator, forgedReceipt.Scope.Role);

            // The session is still alive after fenced attempts.
            string nonce = TableWire.RandomNonce();
            await client.SendAsync(TableWire.HeartbeatPing(nonce));
            Envelope pong = await client.ReadUntilAsync(
                envelope => envelope.MessageType == MessageType.Heartbeat &&
                            envelope.Heartbeat.BeatCase == Heartbeat.BeatOneofCase.Pong,
                TimeSpan.FromSeconds(10));
            Assert.Equal(nonce, pong.Heartbeat.Pong.Nonce);
            await client.DisposeAsync();
        }

        [Fact]
        public async Task SpectatorCommands_AreRefusedWithZeroSideEffects()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            TableTestClient spectator = await host.ConnectSpectatorAsync("room-scmd", "watcher-s");
            Welcome welcome = spectator.Welcome!;
            await spectator.ReadUntilAsync(
                envelope => envelope.MessageType == MessageType.PublicUpdate &&
                            envelope.PublicUpdate.IsFullSnapshot,
                TimeSpan.FromSeconds(15));

            byte[] payload = TableWire.SubmitActionPayload(
                "room-scmd-r1", "decision-s", 0, (uint)ProtocolActionType.Pass);
            string requestId = TableWire.RandomId("req");
            await spectator.SendAsync(TableWire.Command(
                welcome, requestId, Contracts.CommandKind.SubmitAction, payload));

            Envelope error = await spectator.ReadUntilAsync(
                envelope => envelope.MessageType == MessageType.Error,
                TimeSpan.FromSeconds(10));
            Assert.Equal(ProtocolErrorCode.Unauthenticated, error.Error.Code);
            Assert.Equal("SCOPE_ROLE_NOT_AUTHORIZED", error.Error.DetailCode);

            // Zero side effects: the refused request id has no durable receipt for the scope.
            await spectator.SendAsync(TableWire.ReceiptQuery(
                welcome, welcome.Scope.ScopeId, requestId));
            Envelope lookup = await spectator.ReadUntilAsync(
                envelope => envelope.MessageType == MessageType.ReceiptLookup,
                TimeSpan.FromSeconds(10));
            Assert.Equal(LookupStatus.NotFound, lookup.ReceiptLookup.Status);

            // And the spectator session stays open for observation.
            string nonce = TableWire.RandomNonce();
            await spectator.SendAsync(TableWire.HeartbeatPing(nonce));
            Envelope pong = await spectator.ReadUntilAsync(
                envelope => envelope.MessageType == MessageType.Heartbeat &&
                            envelope.Heartbeat.BeatCase == Heartbeat.BeatOneofCase.Pong,
                TimeSpan.FromSeconds(10));
            Assert.Equal(nonce, pong.Heartbeat.Pong.Nonce);
            await spectator.DisposeAsync();
        }

        [Fact]
        public async Task SyncRequest_AcksAndRebasesFromAnyCursor()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            TableTestClient client = await host.ConnectPlayerAsync("room-sync", "acct-sync", 0);
            Welcome welcome = client.Welcome!;

            Envelope baseline = await client.ReadUntilAsync(
                envelope => envelope.MessageType == MessageType.PlayerUpdate &&
                            envelope.PlayerUpdate.IsFullSnapshot,
                TimeSpan.FromSeconds(15));
            Contracts.Wire.StreamCursor current = baseline.PlayerUpdate.Cursor!;

            // Syncing from the current cursor: acknowledged, no forced rebaseline.
            await client.SendAsync(TableWire.SyncRequest(welcome, true, current.StreamVersion, current.ProjectionSeq));
            Envelope ack = await client.ReadUntilAsync(
                envelope => envelope.MessageType == MessageType.SyncResponse,
                TimeSpan.FromSeconds(10));
            Assert.Equal(SyncTarget.PlayerStream, ack.SyncResponse.Target);

            // Syncing from a zero cursor: the authoritative full baseline flows again. The
            // SYNC_RESPONSE carries the baseline itself; the actor's own PLAYER_UPDATE frame
            // flows on the stream channel with no contract-guaranteed order against the
            // reply (the frozen client validates the two independently), so the re-baseline
            // is asserted through the stream frame the client actually observed.
            await client.SendAsync(TableWire.SyncRequest(welcome, true, 0, 0));
            Envelope rebase = await client.ReadUntilAsync(
                envelope => envelope.MessageType == MessageType.SyncResponse,
                TimeSpan.FromSeconds(10));
            Assert.True(rebase.SyncResponse.IsFullSnapshot);
            Assert.Equal(SyncTarget.PlayerStream, rebase.SyncResponse.Target);
            Assert.True(
                client.ReadLastBaselineStateVersion >= baseline.PlayerUpdate.Cursor.StreamVersion,
                "the actor's own full baseline must flow on the stream after a zero-cursor sync; observed state version " +
                client.ReadLastBaselineStateVersion);
            await client.DisposeAsync();
        }

        [Fact]
        public async Task SyncTargetMustMatchAudience()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            TableTestClient spectator = await host.ConnectSpectatorAsync("room-st", "watcher-st");
            await spectator.ReadUntilAsync(
                envelope => envelope.MessageType == MessageType.PublicUpdate &&
                            envelope.PublicUpdate.IsFullSnapshot,
                TimeSpan.FromSeconds(15));
            await spectator.SendAsync(TableWire.SyncRequest(spectator.Welcome!, playerStream: true, 0, 0));
            Envelope error = await spectator.ReadUntilAsync(
                envelope => envelope.MessageType == MessageType.Error,
                TimeSpan.FromSeconds(10));
            Assert.Equal("SYNC_TARGET_MISMATCH", error.Error.DetailCode);
            await spectator.DisposeAsync();
        }

        [Fact]
        public async Task ControlCommandKind_IsGatedByTheFrozenFieldContract()
        {
            // The frozen field-number table gives ClaimControl.expectedControlEpoch and
            // roundId the same field number (1), so a wire-level CLAIM_CONTROL can never
            // satisfy the frozen validator: it is refused pre-actor as malformed. This test
            // pins that refusal (no crash, no actor entry) until the frozen schema resolves
            // the collision.
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            TableTestClient client = await host.ConnectPlayerAsync("room-ctl", "acct-ctl", 0);
            Welcome welcome = client.Welcome!;
            await DrainBaselineAsync(client);

            var writer = new CanonicalPayloadWriter((ushort)Contracts.CommandKind.ClaimControl);
            writer.WriteUInt64(CommandPayloadFields.ExpectedControlEpoch, 0);
            // Field 1 twice is impossible under the strict ascending canonical encoding —
            // exactly why a wire-level CLAIM_CONTROL can never carry both frozen fields.
            Assert.Throws<CanonicalEncodingException>(() =>
                writer.WriteString(CommandPayloadFields.RoundIdField, "room-ctl-r1"));

            // A single-field control payload puts exactly the bytes a hostile or
            // schema-ignorant client would put on the wire on the wire. The frozen codec
            // refuses to even encode this command client-side (its validator demands both
            // the UInt64 and the String form of field 1) and classifies the payload as a
            // wire-level violation at decode time — the same class as an oversize frame or
            // concatenated envelopes. The host therefore closes the session with the
            // malformed-message close: no Error envelope, no crash, and crucially no actor
            // entry for a command the frozen schema says can never be valid.
            var controlWriter = new CanonicalPayloadWriter((ushort)Contracts.CommandKind.ClaimControl);
            controlWriter.WriteUInt64(CommandPayloadFields.ExpectedControlEpoch, 0);
            byte[] payload = controlWriter.Build().CopyBytes();
            await client.SendAsync(TableWire.Command(
                    welcome, TableWire.RandomId("req"), Contracts.CommandKind.ClaimControl, payload)
                .ToByteArray());
            (int code, string reason, TableWireMessage[] drained) =
                await client.ExpectCloseAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(4400, code);
            Assert.Equal("MALFORMED_MESSAGE", reason);
            foreach (TableWireMessage frame in drained)
            {
                Assert.True(
                    frame.Envelope == null || frame.Envelope.MessageType != MessageType.Receipt,
                    "a wire-rejected CLAIM_CONTROL must never reach the actor");
            }

            await client.DisposeAsync();
        }

        private static async Task DrainBaselineAsync(TableTestClient client)
        {
            await client.ReadUntilAsync(
                envelope => (envelope.MessageType == MessageType.PlayerUpdate ||
                             envelope.MessageType == MessageType.PublicUpdate) &&
                            (envelope.PlayerUpdate != null || envelope.PublicUpdate != null),
                TimeSpan.FromSeconds(15));
        }

    }
}
