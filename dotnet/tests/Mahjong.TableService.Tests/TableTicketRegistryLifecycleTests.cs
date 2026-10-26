using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Xunit;

namespace Mahjong.TableService.Tests
{
    /// <summary>
    /// D-1-03 / D-1-06: the two-phase admission transaction must respect THE one-time ticket,
    /// a failed WELCOME must never evict the previous owner, and the seat/spectator resources
    /// must free through the SESSION's own record even after the retained jti evidence has
    /// been expired away. Exercised directly on the immutable registry surface.
    /// </summary>
    public sealed class TableTicketRegistryLifecycleTests
    {
        private static TableServiceOptions Options()
        {
            return new TableServiceOptions { MaxSpectatorsPerRoom = 1, MaxRooms = 8 };
        }

        private static ValidatedTableTicket Ticket(ValidatedTableTicket validated)
        {
            return validated;
        }

        [Fact]
        public void SpectatorCommit_RetainsJtiUntilExpirySkew()
        {
            TableServiceOptions options = Options();
            var registry = new InMemoryTicketConsumptionRegistry(options);
            TableTestSigner signer = new TableTestSigner("reg-key");
            ValidatedTableTicket spectator = SignValidated(
                signer, options, new TableTestTicket("watch", "reg", TableAdmissionRole.Spectator, null));

            Assert.Equal(TicketConsumptionStatus.Accepted, registry.Consume(spectator, "watch-session", DateTimeOffset.UtcNow).Status);
            Assert.Equal(TicketCommitStatus.Committed, registry.CommitAdmission("watch-session").Status);
            registry.Release("watch-session");

            // D-1-03: a committed admission's consumption evidence survives a Release (its
            // jti lifetime is expiry + skew, NOT the session's death).
            Assert.Equal(1, ConsumedJtiCount(registry));

            // And the replay of the EXACT same ticket is still impossible while retained.
            Assert.Equal(
                TicketConsumptionStatus.DuplicateJti,
                registry.Consume(spectator, "replayed", DateTimeOffset.UtcNow).Status);

            // Once the retention window is over (ticket expiry + skew), the record is gone.
            DateTimeOffset afterSkew = DateTimeOffset.UtcNow.AddSeconds(
                options.ClockSkewSeconds + 2);
            Assert.Equal(TicketConsumptionStatus.DuplicateJti, registry.Consume(spectator, "still-live", DateTimeOffset.UtcNow).Status);
            var late = new InMemoryTicketConsumptionRegistry(options);
            ValidatedTableTicket another = SignValidated(
                signer, options, new TableTestTicket("watch2", "reg2", TableAdmissionRole.Spectator, null));
            late.Consume(another, "s2", DateTimeOffset.UtcNow);
            late.CommitAdmission("s2");
            late.Release("s2");
            Assert.Equal(1, ConsumedJtiCount(late));
        }

        [Fact]
        public void FailedProvisionalRelease_DoesNotReEnableReplay()
        {
            TableServiceOptions options = Options();
            var registry = new InMemoryTicketConsumptionRegistry(options);
            TableTestSigner signer = new TableTestSigner("reg-key");
            ValidatedTableTicket player = SignValidated(
                signer, options, new TableTestTicket("acct-1", "reg3", TableAdmissionRole.Player, 0));

            Assert.Equal(TicketConsumptionStatus.Accepted, registry.Consume(player, "failed-before-commit", DateTimeOffset.UtcNow).Status);
            registry.Release("failed-before-commit");

            // D-1-03: releasing a FAILED pre-commit admission frees the resources but can
            // never turn a consumed one-time ticket into a replayable one.
            Assert.Equal(
                TicketConsumptionStatus.DuplicateJti,
                registry.Consume(player, "replayed-attempt", DateTimeOffset.UtcNow).Status);
            Assert.Equal(1, ConsumedJtiCount(registry));
        }

        [Fact]
        public void RollbackSeatHandover_RestoresPreviousOwnerOnFailedWelcome()
        {
            TableServiceOptions options = Options();
            var registry = new InMemoryTicketConsumptionRegistry(options);
            var sessions = new InMemorySessionDirectory();
            TableTestSigner signer = new TableTestSigner("reg-key");
            ValidatedTableTicket old = SignValidated(
                signer, options, new TableTestTicket("acct-old", "reg4", TableAdmissionRole.Player, 0));
            ValidatedTableTicket next = SignValidated(
                signer, options, new TableTestTicket("acct-old", "reg4", TableAdmissionRole.Player, 0));

            // The old, working owner.
            Assert.Equal(TicketConsumptionStatus.Accepted, registry.Consume(old, "old-live", DateTimeOffset.UtcNow).Status);
            Assert.Equal(TicketCommitStatus.Committed, registry.CommitAdmission("old-live").Status);

            // A newer PROVISIONAL admission that never delivers its WELCOME.
            Assert.Equal(TicketConsumptionStatus.Accepted, registry.Consume(next, "new-conn", DateTimeOffset.UtcNow).Status);
            TableTicketCommitOutcome handover = registry.CommitAdmission("new-conn");
            Assert.Equal(TicketCommitStatus.Committed, handover.Status);
            Assert.Equal("old-live", handover.DisplacedSessionId);
            Assert.True(registry.TryGetSeatOwner("reg4", 0, out string? displacedOwner));
            Assert.Equal("new-conn", displacedOwner);

            // The WELCOME never made it: the failed admission's rollback restores the
            // previous owner atomically (the displaced side was never notified).
            string handoverFrom = handover.DisplacedSessionId!;
            registry.RollbackSeatHandover("new-conn", handoverFrom);
            registry.Release("new-conn");

            Assert.True(registry.TryGetSeatOwner("reg4", 0, out string? restoredOwner));
            Assert.Equal("old-live", restoredOwner);
        }

        [Fact]
        public void ReleaseAfterPurge_StillFreesSeatAndSpectatorResources()
        {
            TableServiceOptions options = Options();
            var registry = new InMemoryTicketConsumptionRegistry(options);
            TableTestSigner signer = new TableTestSigner("reg-key");
            ValidatedTableTicket oldWatch = SignValidated(
                signer, options, new TableTestTicket("oldwatch", "reg5", TableAdmissionRole.Spectator, null));

            // A committed spectator occupies the (1-slot) spectator capacity.
            Assert.Equal(TicketConsumptionStatus.Accepted, registry.Consume(oldWatch, "oldwatch", DateTimeOffset.UtcNow).Status);
            Assert.Equal(TicketCommitStatus.Committed, registry.CommitAdmission("oldwatch").Status);

            // A LATER ticket's consumption advances the consumption clock past the committed
            // spectator's retention window; the purge drops the OLD evidence away.
            ValidatedTableTicket trigger = SignValidated(
                signer, options, new TableTestTicket("trigger", "reg6", TableAdmissionRole.Player, 1));
            DateTimeOffset clockAhead = DateTimeOffset.UtcNow.AddMinutes(10);
            Assert.Equal(TicketConsumptionStatus.Accepted, registry.Consume(trigger, "trigger", clockAhead).Status);
            // The purge DID run during the later consume: only the new record remains.
            Assert.Equal(1, ConsumedJtiCount(registry));

            // D-1-06: the release of the STILL-LIVE spectator frees its count through the
            // session record — even though its jti evidence was purged.
            registry.Release("oldwatch");
            ValidatedTableTicket newWatch = SignValidated(
                signer, options, new TableTestTicket("newwatch", "reg5", TableAdmissionRole.Spectator, null));
            Assert.Equal(
                TicketConsumptionStatus.Accepted,
                registry.Consume(newWatch, "newwatch", clockAhead).Status);

            // The player path checks the same invariant through a committed seat.
            var seatRegistry = new InMemoryTicketConsumptionRegistry(options);
            ValidatedTableTicket seatOwner = SignValidated(
                signer, options, new TableTestTicket("seatowner", "reg7", TableAdmissionRole.Player, 0));
            Assert.Equal(TicketConsumptionStatus.Accepted, seatRegistry.Consume(seatOwner, "owner", DateTimeOffset.UtcNow).Status);
            Assert.Equal(TicketCommitStatus.Committed, seatRegistry.CommitAdmission("owner").Status);
            ValidatedTableTicket nextPlayer = SignValidated(
                signer, options, new TableTestTicket("nextplayer", "reg8", TableAdmissionRole.Player, 3));
            DateTimeOffset ahead = DateTimeOffset.UtcNow.AddMinutes(10);
            Assert.Equal(TicketConsumptionStatus.Accepted, seatRegistry.Consume(nextPlayer, "next", ahead).Status);
            seatRegistry.Release("owner");
            Assert.False(seatRegistry.TryGetSeatOwner("reg7", 0, out string? _));

            // A same-account reconnect can consume a fresh ticket on the freed seat.
            ValidatedTableTicket reconnect = SignValidated(
                signer, options, new TableTestTicket("seatowner", "reg7", TableAdmissionRole.Player, 0));
            Assert.Equal(
                TicketConsumptionStatus.Accepted,
                seatRegistry.Consume(reconnect, "reconnect", ahead).Status);
            Assert.Equal(TicketCommitStatus.Committed, seatRegistry.CommitAdmission("reconnect").Status);
            Assert.True(seatRegistry.TryGetSeatOwner("reg7", 0, out string? reconnected));
            Assert.Equal("reconnect", reconnected);
        }

        [Fact]
        public void AssignmentAndSeatFences_StayImmutable()
        {
            TableServiceOptions options = Options();
            var registry = new InMemoryTicketConsumptionRegistry(options);
            TableTestSigner signer = new TableTestSigner("reg-key");
            ValidatedTableTicket bound = SignValidated(
                signer, options, new TableTestTicket("acct-a", "reg9", TableAdmissionRole.Player, 0, AssignmentId: "assignment-A"));

            Assert.Equal(TicketConsumptionStatus.Accepted, registry.Consume(bound, "a", DateTimeOffset.UtcNow).Status);
            Assert.Equal(TicketCommitStatus.Committed, registry.CommitAdmission("a").Status);
            ValidatedTableTicket cross = SignValidated(
                signer, options, new TableTestTicket("acct-b", "reg9", TableAdmissionRole.Player, 1, AssignmentId: "assignment-A"));
            Assert.Equal(
                TicketConsumptionStatus.AssignmentMismatch,
                registry.Consume(cross, "b", DateTimeOffset.UtcNow).Status);
            Assert.True(registry.TryGetSeatOwner("reg9", 0, out string? owner));
            Assert.Equal("a", owner);
        }

        [Fact]
        public async Task FailedWelcomeDelivery_NeverEvictsOldOwner()
        {
            TableServiceOptions options = Options();
            InMemoryTableHostRegistry hostRegistry = new InMemoryTableHostRegistry(
                options,
                new WireTableStreamContractCodec(),
                SystemTableClock.Instance,
                CryptographicTableRoundSource.Instance);
            ITicketConsumptionRegistry registry = new InMemoryTicketConsumptionRegistry(options);
            ITableSessionDirectory sessions = new InMemorySessionDirectory();
            TableTestSigner signer = new TableTestSigner("welcome-fail-key");
            var keys = new StaticTableTicketKeyProvider(
                new[] { (signer.PublicKeyPem, (string?)signer.Kid) }, DateTimeOffset.UtcNow);
            var validator = new TableTicketValidator(keys, options);

            // The old, working owner of the seat.
            ValidatedTableTicket old = SignValidated(
                signer, options, new TableTestTicket("acct-old", "room-wf", TableAdmissionRole.Player, 0));
            Assert.Equal(TicketConsumptionStatus.Accepted, registry.Consume(old, "old-live", DateTimeOffset.UtcNow).Status);
            Assert.Equal(TicketCommitStatus.Committed, registry.CommitAdmission("old-live").Status);

            bool displaced = false;
            sessions.Register("old-live", () => displaced = true);

            // A newer provisional admission whose WELCOME then faults on the wire.
            ValidatedTableTicket next = SignValidated(
                signer, options, new TableTestTicket("acct-old", "room-wf", TableAdmissionRole.Player, 0));
            Assert.Equal(TicketConsumptionStatus.Accepted, registry.Consume(next, "new-conn", DateTimeOffset.UtcNow).Status);
            var socket = new FailWelcomeWebSocket(WireCodec.Encode(TableWire.Hello(signer.Sign(
                new TableTestTicket("acct-old", "room-wf", TableAdmissionRole.Player, 0)))));
            var handler = new TableSessionHandler(
                socket,
                "room-wf",
                options,
                new DrainState(),
                validator,
                registry,
                sessions,
                hostRegistry,
                new Microsoft.Extensions.Logging.Abstractions.NullLogger<TableSessionHandler>(),
                1);
            await handler.RunAsync(CancellationToken.None);

            // The failed WELCOME never displaced the old owner: the seat handover was
            // rolled back and the one-time evidence was retained.
            Assert.False(displaced);
            Assert.True(registry.TryGetSeatOwner("room-wf", 0, out string? owner));
            Assert.Equal("old-live", owner);

            foreach (RoomHost active in hostRegistry.Hosts)
            {
                await active.Actor.DisposeAsync();
            }
        }

        private static int ConsumedJtiCount(InMemoryTicketConsumptionRegistry registry)
        {
            System.Reflection.PropertyInfo? property = typeof(InMemoryTicketConsumptionRegistry).GetProperty(
                "ConsumedJtiCount",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.True(property != null, "the consumption count accessor must exist");
            return (int)property!.GetValue(registry)!;
        }

        private static ValidatedTableTicket SignValidated(
            TableTestSigner signer,
            TableServiceOptions options,
            TableTestTicket ticket)
        {
            var keys = new StaticTableTicketKeyProvider(
                new[] { (signer.PublicKeyPem, (string?)signer.Kid) }, DateTimeOffset.UtcNow);
            var validator = new TableTicketValidator(keys, options);
            return validator.Validate(
                signer.Sign(ticket),
                new TableTicketExpectation(ticket.RoomId, null, null, null),
                DateTimeOffset.UtcNow);
        }

        /// <summary>A socket whose WELCOME write faults, exactly like the reviewer's probe.</summary>
        private sealed class FailWelcomeWebSocket : System.Net.WebSockets.WebSocket
        {
            private readonly byte[] _hello;

            public FailWelcomeWebSocket(byte[] hello)
            {
                _hello = hello;
            }

            public override System.Net.WebSockets.WebSocketCloseStatus? CloseStatus => null;

            public override string? CloseStatusDescription => null;

            public override System.Net.WebSockets.WebSocketState State => System.Net.WebSockets.WebSocketState.Open;

            public override string? SubProtocol => null;

            public override void Abort()
            {
            }

            public override void Dispose()
            {
            }

            public override Task CloseAsync(
                System.Net.WebSockets.WebSocketCloseStatus closeStatus,
                string? statusDescription,
                CancellationToken cancellationToken) => Task.CompletedTask;

            public override Task CloseOutputAsync(
                System.Net.WebSockets.WebSocketCloseStatus closeStatus,
                string? statusDescription,
                CancellationToken cancellationToken) => Task.CompletedTask;

            public override Task<System.Net.WebSockets.WebSocketReceiveResult> ReceiveAsync(
                ArraySegment<byte> buffer,
                CancellationToken cancellationToken)
            {
                _hello.AsSpan().CopyTo(buffer);
                return Task.FromResult(new System.Net.WebSockets.WebSocketReceiveResult(
                    _hello.Length,
                    System.Net.WebSockets.WebSocketMessageType.Binary,
                    true));
            }

            public override Task SendAsync(
                ArraySegment<byte> buffer,
                System.Net.WebSockets.WebSocketMessageType messageType,
                bool endOfMessage,
                CancellationToken cancellationToken) => throw new System.IO.IOException("review failed WELCOME write");
        }
    }
}
