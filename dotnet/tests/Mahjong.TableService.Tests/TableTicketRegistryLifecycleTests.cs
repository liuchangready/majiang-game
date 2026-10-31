using System;
using System.Collections.Generic;
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
            Assert.True(registry.EvictOnWelcome("old-live").Kind == TableEvictionKind.None,
                "the first welcomed owner has nobody to evict");

            // A newer PROVISIONAL admission that never delivers its WELCOME.
            Assert.Equal(TicketConsumptionStatus.Accepted, registry.Consume(next, "new-conn", DateTimeOffset.UtcNow).Status);
            TableTicketCommitOutcome handover = registry.CommitAdmission("new-conn");
            Assert.Equal(TicketCommitStatus.Committed, handover.Status);
            Assert.Equal("old-live", handover.DisplacedSessionId);
            Assert.True(registry.EvictOnWelcome("old-live").Kind == TableEvictionKind.None,
                "an older owner never evicts through its own (already-delivered) welcome");

            // The WELCOME never made it: the failed admission's rollback removes its own
            // chain entry, so the seat reverts to the last WELCOME-successful owner.
            registry.RollbackSeatHandover("new-conn");
            registry.Release("new-conn");

            Assert.True(registry.TryGetSeatOwner("reg4", 0, out string? restoredOwner));
            Assert.Equal("old-live", restoredOwner);
            Assert.True(registry.EvictOnWelcome("old-live").Kind == TableEvictionKind.None,
                "the restored owner has nobody further to evict");
        }

        /// <summary>
        /// D-1-03 (round 3): the exact A/B/C failure-chain order the review pinned — B commits
        /// its handover, C commits another handover over it, THEN B's WELCOME fails and then
        /// C's WELCOME fails. No eviction ever fired, and the last committed handover chain
        /// must end at the still-working original owner A with its exact account identity —
        /// even though the two failures happened after a newer commit superseded them.
        /// </summary>
        [Fact]
        public void FailureChain_UnwelcomedCommitsRevertToLastWelcome_OwnerIdentityNeverLost()
        {
            TableServiceOptions options = Options();
            var registry = new InMemoryTicketConsumptionRegistry(options);
            var sessions = new InMemorySessionDirectory();
            TableTestSigner signer = new TableTestSigner("chain-key");

            ValidatedTableTicket a = SignValidated(
                signer, options, new TableTestTicket("acct-alpha", "chain", TableAdmissionRole.Player, 0));
            ValidatedTableTicket b = SignValidated(
                signer, options, new TableTestTicket("acct-alpha", "chain", TableAdmissionRole.Player, 0));
            ValidatedTableTicket c = SignValidated(
                signer, options, new TableTestTicket("acct-alpha", "chain", TableAdmissionRole.Player, 0));

            Assert.Equal(TicketConsumptionStatus.Accepted, registry.Consume(a, "old-live", DateTimeOffset.UtcNow).Status);
            Assert.Equal(TicketCommitStatus.Committed, registry.CommitAdmission("old-live").Status);
            // A's WELCOME was delivered (the real handler's post-WELCOME step): recorded
            // welcomed on the chain, nobody to evict.
            Assert.Equal(TableEvictionKind.None, registry.EvictOnWelcome("old-live").Kind);
            sessions.Register("old-live", () => throw new InvalidOperationException("A must never be displaced"));

            Assert.Equal(TicketConsumptionStatus.Accepted, registry.Consume(b, "conn-b", DateTimeOffset.UtcNow).Status);
            Assert.Equal(TicketCommitStatus.Committed, registry.CommitAdmission("conn-b").Status);
            Assert.Equal(TicketConsumptionStatus.Accepted, registry.Consume(c, "conn-c", DateTimeOffset.UtcNow).Status);
            Assert.Equal(TicketCommitStatus.Committed, registry.CommitAdmission("conn-c").Status);

            // Both Welcomes fail, current owner never notified, newest-first restoration; the
            // removal order is deliberately REVERSED (C first, then B): the rollback of a
            // superseded failed admission must stay effective regardless of removal order.
            registry.RollbackSeatHandover("conn-c");
            registry.Release("conn-c");
            registry.RollbackSeatHandover("conn-b");
            registry.Release("conn-b");

            Assert.True(registry.TryGetSeatOwner("chain", 0, out string? owner));
            Assert.Equal("old-live", owner);
            Assert.Equal(TicketConsumptionStatus.Accepted,
                registry.Consume(c, "acceptable-again", DateTimeOffset.UtcNow.AddHours(1)).Status);
        }

        /// <summary>D-1-03 (round 3): seat identity lives on the seat lease entry itself — a
        /// rollback restores the EXACT account even when the jti/assignment retention rows of
        /// the previous owner were already purged by expiry.</summary>
        [Fact]
        public void RollbackAfterExpiryPurge_RestoresExactAccountId_SameAccountReconnectAccepted()
        {
            TableServiceOptions options = Options();
            var registry = new InMemoryTicketConsumptionRegistry(options);
            TableTestSigner signer = new TableTestSigner("purge-key");

            ValidatedTableTicket old = SignValidated(
                signer, options, new TableTestTicket("acct-online", "purge", TableAdmissionRole.Player, 0));
            ValidatedTableTicket handover = SignValidated(
                signer, options, new TableTestTicket("acct-online", "purge", TableAdmissionRole.Player, 0));

            Assert.Equal(TicketConsumptionStatus.Accepted, registry.Consume(old, "old-live", DateTimeOffset.UtcNow).Status);
            Assert.Equal(TicketCommitStatus.Committed, registry.CommitAdmission("old-live").Status);
            // The old owner's WELCOME was delivered (the real handler's post-WELCOME step).
            Assert.Equal(TableEvictionKind.None, registry.EvictOnWelcome("old-live").Kind);

            // Advance the retention clock: the previous owner's retained records are purged,
            // while the LIVE session keeps owning the resource.
            DateTimeOffset purgedClock = DateTimeOffset.UtcNow.AddMinutes(10);
            Assert.Equal(TicketConsumptionStatus.Accepted,
                registry.Consume(handover, "new-conn", purgedClock).Status);
            Assert.Equal(1, ConsumedJtiCount(registry)); // the old retained rows are gone

            Assert.Equal(TicketCommitStatus.Committed, registry.CommitAdmission("new-conn").Status);
            registry.RollbackSeatHandover("new-conn");
            registry.Release("new-conn");

            // The restored owner is the LIVE session with its EXACT account — not an empty
            // string derived from a purged jti row — so a fresh ticket of the SAME account is
            // accepted again and a foreign account is refused.
            Assert.True(registry.TryGetSeatOwner("purge", 0, out string? live));
            Assert.Equal("old-live", live);
            ValidatedTableTicket reconnect = SignValidated(
                signer, options, new TableTestTicket("acct-online", "purge", TableAdmissionRole.Player, 0));
            Assert.Equal(
                TicketConsumptionStatus.Accepted,
                registry.Consume(reconnect, "reconnect", purgedClock).Status);

            var foreignRegistry = new InMemoryTicketConsumptionRegistry(options);
            ValidatedTableTicket foreignOld = SignValidated(
                signer, options, new TableTestTicket("acct-foreign", "purge2", TableAdmissionRole.Player, 0));
            foreignRegistry.Consume(foreignOld, "foreign-old", DateTimeOffset.UtcNow);
            foreignRegistry.CommitAdmission("foreign-old");
            Assert.Equal(TableEvictionKind.None, foreignRegistry.EvictOnWelcome("foreign-old").Kind);
            ValidatedTableTicket foreignPurged = SignValidated(
                signer, options, new TableTestTicket("other", "purge2", TableAdmissionRole.Player, 0));
            foreignRegistry.Consume(foreignPurged, "foreign-new", DateTimeOffset.UtcNow.AddMinutes(10));
            foreignRegistry.CommitAdmission("foreign-new");
            foreignRegistry.RollbackSeatHandover("foreign-new");
            Assert.Equal(
                TicketConsumptionStatus.SeatOccupiedByOtherAccount,
                foreignRegistry.Consume(foreignPurged, "foreign-takeover", DateTimeOffset.UtcNow.AddMinutes(10)).Status);
        }

        /// <summary>D-1-03 (round 3): a WELCOME that succeeds out of commit order can never
        /// displace a seat its session no longer owns (the loser welcome never evicts).</summary>
        [Fact]
        public void OutOfOrderWelcome_LosingWelcomeNeverDisplacesAnybody()
        {
            TableServiceOptions options = Options();
            var registry = new InMemoryTicketConsumptionRegistry(options);
            var sessions = new InMemorySessionDirectory();
            TableTestSigner signer = new TableTestSigner("racer-key");

            ValidatedTableTicket a = SignValidated(
                signer, options, new TableTestTicket("acct-a", "racer", TableAdmissionRole.Player, 0));
            Assert.Equal(TicketConsumptionStatus.Accepted, registry.Consume(a, "live-a", DateTimeOffset.UtcNow).Status);
            Assert.Equal(TicketCommitStatus.Committed, registry.CommitAdmission("live-a").Status);
            // live-a's WELCOME was delivered (the real handler's post-WELCOME step).
            Assert.Equal(TableEvictionKind.None, registry.EvictOnWelcome("live-a").Kind);

            bool displaced = false;
            sessions.Register("live-a", () => displaced = true);

            ValidatedTableTicket b = SignValidated(
                signer, options, new TableTestTicket("acct-a", "racer", TableAdmissionRole.Player, 0));
            Assert.Equal(TicketConsumptionStatus.Accepted, registry.Consume(b, "conn-b", DateTimeOffset.UtcNow).Status);
            Assert.Equal(TicketCommitStatus.Committed, registry.CommitAdmission("conn-b").Status);
            ValidatedTableTicket c = SignValidated(
                signer, options, new TableTestTicket("acct-a", "racer", TableAdmissionRole.Player, 0));
            Assert.Equal(TicketConsumptionStatus.Accepted, registry.Consume(c, "conn-c", DateTimeOffset.UtcNow).Status);
            Assert.Equal(TicketCommitStatus.Committed, registry.CommitAdmission("conn-c").Status);

            // The NEWEST handover (C) delivers its WELCOME first: it evicts the closest
            // earlier welcomed owner (A). The LATE welcome B finds a newer welcomed owner
            // already on the chain: it LOST the seat — SelfSuperseded removes its own entry
            // so the caller closes it and no second live owner can ever appear (round 5).
            TableEvictionDecision cEviction = registry.EvictOnWelcome("conn-c");
            Assert.Equal(TableEvictionKind.DisplacedOther, cEviction.Kind);
            Assert.Equal("live-a", cEviction.SessionId);
            TableEvictionDecision bEviction = registry.EvictOnWelcome("conn-b");
            Assert.Equal(TableEvictionKind.SelfSuperseded, bEviction.Kind);
            Assert.Null(bEviction.SessionId);
            Assert.False(displaced, "only the winning welcome drives the actual displacement");

            // ...and B's own eventual failure releases its already-removed stale entry
            // without touching the live winner's seat (idempotent no-ops now).
            registry.RollbackSeatHandover("conn-b");
            registry.Release("conn-b");
            Assert.True(registry.TryGetSeatOwner("racer", 0, out string? owner));
            Assert.Equal("conn-c", owner);
            (string RacerEndSession, bool RacerEndWelcomed) = Assert.Single(SeatChain(registry, "racer", 0));
            Assert.Equal("conn-c", RacerEndSession);
            Assert.True(RacerEndWelcomed);
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
        public void SameAssignmentFreshJti_AfterRelease_IsAccepted_AndOldJtiStaysReplayProof()
        {
            TableServiceOptions options = Options();
            var registry = new InMemoryTicketConsumptionRegistry(options);
            TableTestSigner signer = new TableTestSigner("reissue-key");

            ValidatedTableTicket old = SignValidated(
                signer,
                options,
                new TableTestTicket(
                    "acct-reissue",
                    "room-reissue",
                    TableAdmissionRole.Player,
                    0,
                    Jti: "jti-old-reissue",
                    AssignmentId: "assignment-reissue"));
            ValidatedTableTicket fresh = SignValidated(
                signer,
                options,
                new TableTestTicket(
                    "acct-reissue",
                    "room-reissue",
                    TableAdmissionRole.Player,
                    0,
                    Jti: "jti-fresh-reissue",
                    AssignmentId: "assignment-reissue"));

            Assert.Equal(TicketConsumptionStatus.Accepted, registry.Consume(old, "old-live", DateTimeOffset.UtcNow).Status);
            Assert.Equal(TicketCommitStatus.Committed, registry.CommitAdmission("old-live").Status);
            Assert.Equal(TableEvictionKind.None, registry.EvictOnWelcome("old-live").Kind);
            registry.Release("old-live");
            Assert.False(registry.TryGetSeatOwner("room-reissue", 0, out string? _));

            // B1-BLOCKER-02: the SAME assignment with the SAME tuple and a FRESH jti is the
            // legitimate reconnect path, even while the old assignment evidence is retained.
            Assert.Equal(
                TicketConsumptionStatus.Accepted,
                registry.Consume(fresh, "fresh-live", DateTimeOffset.UtcNow).Status);
            Assert.Equal(TicketCommitStatus.Committed, registry.CommitAdmission("fresh-live").Status);
            Assert.Equal(TableEvictionKind.None, registry.EvictOnWelcome("fresh-live").Kind);
            Assert.True(registry.TryGetSeatOwner("room-reissue", 0, out string? owner));
            Assert.Equal("fresh-live", owner);

            // Each consumed jti stays one-time until its own expiry + skew, including the
            // disconnected old jti and the just-consumed fresh jti.
            Assert.Equal(
                TicketConsumptionStatus.DuplicateJti,
                registry.Consume(old, "old-replay", DateTimeOffset.UtcNow).Status);
            Assert.Equal(
                TicketConsumptionStatus.DuplicateJti,
                registry.Consume(fresh, "fresh-replay", DateTimeOffset.UtcNow).Status);
        }

        [Fact]
        public void SameAssignmentFreshJti_WhileLive_WelcomesAndDisplacesOldOwner()
        {
            TableServiceOptions options = Options();
            var registry = new InMemoryTicketConsumptionRegistry(options);
            TableTestSigner signer = new TableTestSigner("handover-key");

            ValidatedTableTicket old = SignValidated(
                signer,
                options,
                new TableTestTicket(
                    "acct-handover",
                    "room-handover",
                    TableAdmissionRole.Player,
                    1,
                    Jti: "jti-old-handover",
                    AssignmentId: "assignment-handover"));
            ValidatedTableTicket fresh = SignValidated(
                signer,
                options,
                new TableTestTicket(
                    "acct-handover",
                    "room-handover",
                    TableAdmissionRole.Player,
                    1,
                    Jti: "jti-fresh-handover",
                    AssignmentId: "assignment-handover"));

            Assert.Equal(TicketConsumptionStatus.Accepted, registry.Consume(old, "old-live", DateTimeOffset.UtcNow).Status);
            Assert.Equal(TicketCommitStatus.Committed, registry.CommitAdmission("old-live").Status);
            Assert.Equal(TableEvictionKind.None, registry.EvictOnWelcome("old-live").Kind);

            Assert.Equal(
                TicketConsumptionStatus.Accepted,
                registry.Consume(fresh, "fresh-live", DateTimeOffset.UtcNow).Status);
            TableTicketCommitOutcome commit = registry.CommitAdmission("fresh-live");
            Assert.Equal(TicketCommitStatus.Committed, commit.Status);
            Assert.Equal("old-live", commit.DisplacedSessionId);
            TableEvictionDecision eviction = registry.EvictOnWelcome("fresh-live");
            Assert.Equal(TableEvictionKind.DisplacedOther, eviction.Kind);
            Assert.Equal("old-live", eviction.SessionId);

            // Exactly one authoritative live owner remains; a late/duplicate welcome of the
            // displaced connection cannot resurrect it.
            Assert.True(registry.TryGetSeatOwner("room-handover", 1, out string? owner));
            Assert.Equal("fresh-live", owner);
            Assert.Equal(TableEvictionKind.None, registry.EvictOnWelcome("old-live").Kind);
            (string OnlySession, bool OnlyWelcomed) = Assert.Single(SeatChain(registry, "room-handover", 1));
            Assert.Equal("fresh-live", OnlySession);
            Assert.True(OnlyWelcomed);
            Assert.Equal(
                TicketConsumptionStatus.DuplicateJti,
                registry.Consume(old, "old-replay", DateTimeOffset.UtcNow).Status);
        }

        [Fact]
        public void SameAssignmentFreshJti_AfterFailedHandover_KeepsOldOwner_AndBothJtisSpent()
        {
            TableServiceOptions options = Options();
            var registry = new InMemoryTicketConsumptionRegistry(options);
            TableTestSigner signer = new TableTestSigner("rollback-reissue-key");

            ValidatedTableTicket old = SignValidated(
                signer,
                options,
                new TableTestTicket(
                    "acct-rollback",
                    "room-rollback",
                    TableAdmissionRole.Player,
                    0,
                    Jti: "jti-old-rollback",
                    AssignmentId: "assignment-rollback"));
            ValidatedTableTicket failed = SignValidated(
                signer,
                options,
                new TableTestTicket(
                    "acct-rollback",
                    "room-rollback",
                    TableAdmissionRole.Player,
                    0,
                    Jti: "jti-failed-rollback",
                    AssignmentId: "assignment-rollback"));
            ValidatedTableTicket reconnect = SignValidated(
                signer,
                options,
                new TableTestTicket(
                    "acct-rollback",
                    "room-rollback",
                    TableAdmissionRole.Player,
                    0,
                    Jti: "jti-reconnect-rollback",
                    AssignmentId: "assignment-rollback"));

            Assert.Equal(TicketConsumptionStatus.Accepted,
                registry.Consume(old, "old-live", DateTimeOffset.UtcNow).Status);
            Assert.Equal(TicketCommitStatus.Committed, registry.CommitAdmission("old-live").Status);
            Assert.Equal(TableEvictionKind.None, registry.EvictOnWelcome("old-live").Kind);

            // The fresh same-assignment ticket commits its handover, then its WELCOME fails.
            // Rollback must restore the old owner and must NOT re-enable either ticket.
            Assert.Equal(TicketConsumptionStatus.Accepted,
                registry.Consume(failed, "failed-live", DateTimeOffset.UtcNow).Status);
            Assert.Equal(TicketCommitStatus.Committed, registry.CommitAdmission("failed-live").Status);
            registry.RollbackSeatHandover("failed-live");
            registry.Release("failed-live");

            Assert.True(registry.TryGetSeatOwner("room-rollback", 0, out string? owner));
            Assert.Equal("old-live", owner);
            Assert.Equal(
                TicketConsumptionStatus.DuplicateJti,
                registry.Consume(old, "old-replay", DateTimeOffset.UtcNow).Status);
            Assert.Equal(
                TicketConsumptionStatus.DuplicateJti,
                registry.Consume(failed, "failed-replay", DateTimeOffset.UtcNow).Status);

            // A genuinely fresh ticket for the same tuple is still the legal reconnect path.
            Assert.Equal(
                TicketConsumptionStatus.Accepted,
                registry.Consume(reconnect, "reconnect-live", DateTimeOffset.UtcNow).Status);
        }

        [Fact]
        public void ShorterLivedReissue_DoesNotShortenTheAssignmentsBindingWindow()
        {
            TableServiceOptions options = Options();
            var registry = new InMemoryTicketConsumptionRegistry(options);
            TableTestSigner signer = new TableTestSigner("assignment-window-key");

            ValidatedTableTicket longLived = SignValidated(
                signer,
                options,
                new TableTestTicket(
                    "acct-window",
                    "room-window",
                    TableAdmissionRole.Player,
                    0,
                    Jti: "jti-long-window",
                    AssignmentId: "assignment-window",
                    Ttl: TimeSpan.FromSeconds(240)));
            ValidatedTableTicket shortLived = SignValidated(
                signer,
                options,
                new TableTestTicket(
                    "acct-window",
                    "room-window",
                    TableAdmissionRole.Player,
                    0,
                    Jti: "jti-short-window",
                    AssignmentId: "assignment-window",
                    Ttl: TimeSpan.FromSeconds(60)));
            ValidatedTableTicket crossTuple = SignValidated(
                signer,
                options,
                new TableTestTicket(
                    "acct-foreign-window",
                    "room-window",
                    TableAdmissionRole.Player,
                    1,
                    Jti: "jti-cross-window",
                    AssignmentId: "assignment-window",
                    Ttl: TimeSpan.FromSeconds(240)));

            Assert.Equal(TicketConsumptionStatus.Accepted,
                registry.Consume(longLived, "long-live", DateTimeOffset.UtcNow).Status);
            Assert.Equal(TicketCommitStatus.Committed, registry.CommitAdmission("long-live").Status);
            registry.Release("long-live");

            // The newer re-ticket is valid for a SHORTER window; consuming it must not
            // truncate the assignment binding established by the longer-lived first ticket.
            Assert.Equal(TicketConsumptionStatus.Accepted,
                registry.Consume(shortLived, "short-live", DateTimeOffset.UtcNow).Status);
            registry.Release("short-live");

            DateTimeOffset afterShortExpiry = DateTimeOffset.UtcNow.AddSeconds(90);
            Assert.Equal(
                TicketConsumptionStatus.DuplicateJti,
                registry.Consume(longLived, "long-replay", afterShortExpiry).Status);
            Assert.Equal(
                TicketConsumptionStatus.AssignmentMismatch,
                registry.Consume(crossTuple, "cross-live", afterShortExpiry).Status);
        }

        [Fact]
        public void AssignmentMismatch_DoesNotLeakSpectatorCapacity()
        {
            TableServiceOptions options = Options();
            var registry = new InMemoryTicketConsumptionRegistry(options);
            TableTestSigner signer = new TableTestSigner("capacity-fence-key");

            ValidatedTableTicket player = SignValidated(
                signer,
                options,
                new TableTestTicket(
                    "acct-capacity",
                    "room-capacity",
                    TableAdmissionRole.Player,
                    0,
                    Jti: "jti-capacity-player",
                    AssignmentId: "assignment-capacity"));
            ValidatedTableTicket roleMismatch = SignValidated(
                signer,
                options,
                new TableTestTicket(
                    "acct-capacity",
                    "room-capacity",
                    TableAdmissionRole.Spectator,
                    null,
                    Jti: "jti-capacity-role",
                    AssignmentId: "assignment-capacity"));
            ValidatedTableTicket unrelatedWatch = SignValidated(
                signer,
                options,
                new TableTestTicket(
                    "acct-watch",
                    "room-capacity",
                    TableAdmissionRole.Spectator,
                    null,
                    Jti: "jti-capacity-watch",
                    AssignmentId: "assignment-capacity-watch"));

            Assert.Equal(TicketConsumptionStatus.Accepted,
                registry.Consume(player, "player-live", DateTimeOffset.UtcNow).Status);
            Assert.Equal(TicketCommitStatus.Committed, registry.CommitAdmission("player-live").Status);
            Assert.Equal(TableEvictionKind.None, registry.EvictOnWelcome("player-live").Kind);

            Assert.Equal(
                TicketConsumptionStatus.AssignmentMismatch,
                registry.Consume(roleMismatch, "role-mismatch", DateTimeOffset.UtcNow).Status);

            // MaxSpectatorsPerRoom = 1 and no spectator was admitted: if the refused role
            // mismatch had consumed capacity, this legitimate unrelated spectator would fail.
            Assert.Equal(
                TicketConsumptionStatus.Accepted,
                registry.Consume(unrelatedWatch, "watch-live", DateTimeOffset.UtcNow).Status);
            Assert.True(registry.TryGetSeatOwner("room-capacity", 0, out string? owner));
            Assert.Equal("player-live", owner);
        }

        [Fact]
        public void RetainedTicketStore_IsBounded_AndExpiryPurgeReclaimsCapacity()
        {
            TableServiceOptions options = new TableServiceOptions
            {
                MaxSpectatorsPerRoom = 1,
                MaxRetainedConsumedTickets = 1024
            };
            var registry = new InMemoryTicketConsumptionRegistry(options);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            DateTimeOffset expires = now.AddSeconds(60);

            for (int i = 0; i < 1024; i++)
            {
                ValidatedTableTicket ticket = DirectTicket(
                    "jti-bound-" + i, "assignment-bound-" + i, now, expires);
                string session = "session-bound-" + i;
                Assert.Equal(TicketConsumptionStatus.Accepted,
                    registry.Consume(ticket, session, now).Status);
                registry.Release(session);
            }

            ValidatedTableTicket overCapacity = DirectTicket(
                "jti-over-capacity", "assignment-over-capacity", now, expires);
            Assert.Equal(
                TicketConsumptionStatus.ConsumptionStoreExhausted,
                registry.Consume(overCapacity, "session-over", now).Status);

            // Past every retained ticket's expiry + skew, the bounded store is reclaimed by
            // the next Consume instead of staying exhausted forever.
            DateTimeOffset afterExpiry = expires.AddSeconds(options.ClockSkewSeconds + 1);
            Assert.Equal(
                TicketConsumptionStatus.Accepted,
                registry.Consume(overCapacity, "session-after-purge", afterExpiry).Status);
        }

        [Theory]
        [InlineData("account")]
        [InlineData("role")]
        [InlineData("room")]
        [InlineData("seat")]
        public void SameAssignmentDifferentTuple_FailsClosed_AndLeavesCurrentOwner(string changedField)
        {
            TableServiceOptions options = Options();
            var registry = new InMemoryTicketConsumptionRegistry(options);
            TableTestSigner signer = new TableTestSigner("tuple-key");

            ValidatedTableTicket bound = SignValidated(
                signer,
                options,
                new TableTestTicket(
                    "acct-tuple",
                    "room-tuple",
                    TableAdmissionRole.Player,
                    0,
                    Jti: "jti-bound",
                    AssignmentId: "assignment-tuple"));
            Assert.Equal(TicketConsumptionStatus.Accepted, registry.Consume(bound, "bound-live", DateTimeOffset.UtcNow).Status);
            Assert.Equal(TicketCommitStatus.Committed, registry.CommitAdmission("bound-live").Status);
            Assert.Equal(TableEvictionKind.None, registry.EvictOnWelcome("bound-live").Kind);

            TableTestTicket mismatch = changedField switch
            {
                "account" => new TableTestTicket(
                    "acct-other",
                    "room-tuple",
                    TableAdmissionRole.Player,
                    0,
                    Jti: "jti-mismatch",
                    AssignmentId: "assignment-tuple"),
                "role" => new TableTestTicket(
                    "acct-tuple",
                    "room-tuple",
                    TableAdmissionRole.Spectator,
                    null,
                    Jti: "jti-mismatch",
                    AssignmentId: "assignment-tuple"),
                "room" => new TableTestTicket(
                    "acct-tuple",
                    "room-other",
                    TableAdmissionRole.Player,
                    0,
                    Jti: "jti-mismatch",
                    AssignmentId: "assignment-tuple"),
                _ => new TableTestTicket(
                    "acct-tuple",
                    "room-tuple",
                    TableAdmissionRole.Player,
                    1,
                    Jti: "jti-mismatch",
                    AssignmentId: "assignment-tuple")
            };
            ValidatedTableTicket cross = SignValidated(signer, options, mismatch);
            TicketConsumptionOutcome refused = registry.Consume(cross, "mismatch-live", DateTimeOffset.UtcNow);
            Assert.Equal(TicketConsumptionStatus.AssignmentMismatch, refused.Status);
            Assert.Equal("acct-tuple", refused.ExistingAccountId);
            Assert.Equal(1, ConsumedJtiCount(registry));
            Assert.True(registry.TryGetSeatOwner("room-tuple", 0, out string? owner));
            Assert.Equal("bound-live", owner);

            // The refusal has zero resource/side effects: the unchanged tuple can still
            // consume a fresh jti, and the same assignment still refuses the mismatched one.
            ValidatedTableTicket sameTuple = SignValidated(
                signer,
                options,
                new TableTestTicket(
                    "acct-tuple",
                    "room-tuple",
                    TableAdmissionRole.Player,
                    0,
                    Jti: "jti-after-mismatch",
                    AssignmentId: "assignment-tuple"));
            Assert.Equal(
                TicketConsumptionStatus.Accepted,
                registry.Consume(sameTuple, "after-mismatch", DateTimeOffset.UtcNow).Status);
        }

        [Fact]
        public void ConcurrentFreshJtis_SameAssignment_AtMostOneCommitAndOneLiveOwner()
        {
            TableServiceOptions options = Options();
            var registry = new InMemoryTicketConsumptionRegistry(options);
            TableTestSigner signer = new TableTestSigner("race-key");

            ValidatedTableTicket old = SignValidated(
                signer,
                options,
                new TableTestTicket(
                    "acct-race",
                    "room-race",
                    TableAdmissionRole.Player,
                    2,
                    Jti: "jti-old-race",
                    AssignmentId: "assignment-race"));
            ValidatedTableTicket b = SignValidated(
                signer,
                options,
                new TableTestTicket(
                    "acct-race",
                    "room-race",
                    TableAdmissionRole.Player,
                    2,
                    Jti: "jti-b-race",
                    AssignmentId: "assignment-race"));
            ValidatedTableTicket c = SignValidated(
                signer,
                options,
                new TableTestTicket(
                    "acct-race",
                    "room-race",
                    TableAdmissionRole.Player,
                    2,
                    Jti: "jti-c-race",
                    AssignmentId: "assignment-race"));

            Assert.Equal(TicketConsumptionStatus.Accepted, registry.Consume(old, "live-old", DateTimeOffset.UtcNow).Status);
            Assert.Equal(TicketCommitStatus.Committed, registry.CommitAdmission("live-old").Status);
            Assert.Equal(TableEvictionKind.None, registry.EvictOnWelcome("live-old").Kind);

            // Deterministic interleave of two fresh admissions racing for the same assignment:
            // B reserves, C supersedes the provisional reservation, then B commits first.
            Assert.Equal(TicketConsumptionStatus.Accepted, registry.Consume(b, "conn-b", DateTimeOffset.UtcNow).Status);
            Assert.Equal(TicketConsumptionStatus.Accepted, registry.Consume(c, "conn-c", DateTimeOffset.UtcNow).Status);
            Assert.Equal(TicketCommitStatus.Superseded, registry.CommitAdmission("conn-b").Status);
            Assert.Equal(TicketCommitStatus.Committed, registry.CommitAdmission("conn-c").Status);
            Assert.Equal(TableEvictionKind.None, registry.EvictOnWelcome("conn-b").Kind);
            Assert.Equal(TableEvictionKind.DisplacedOther, registry.EvictOnWelcome("conn-c").Kind);

            Assert.True(registry.TryGetSeatOwner("room-race", 2, out string? owner));
            Assert.Equal("conn-c", owner);
            (string OnlySession, bool OnlyWelcomed) = Assert.Single(SeatChain(registry, "room-race", 2));
            Assert.Equal("conn-c", OnlySession);
            Assert.True(OnlyWelcomed);

            // The loser's exact jti is still spent, and the old/winner jtis are spent too.
            Assert.Equal(
                TicketConsumptionStatus.DuplicateJti,
                registry.Consume(b, "b-replay", DateTimeOffset.UtcNow).Status);
            Assert.Equal(
                TicketConsumptionStatus.DuplicateJti,
                registry.Consume(c, "c-replay", DateTimeOffset.UtcNow).Status);
            Assert.Equal(
                TicketConsumptionStatus.DuplicateJti,
                registry.Consume(old, "old-replay", DateTimeOffset.UtcNow).Status);
        }

        [Fact]
        public void SpectatorCapacity_DoesNotLeakAcrossSameAssignmentRefresh()
        {
            TableServiceOptions options = Options();
            var registry = new InMemoryTicketConsumptionRegistry(options);
            TableTestSigner signer = new TableTestSigner("watch-reissue-key");

            ValidatedTableTicket old = SignValidated(
                signer,
                options,
                new TableTestTicket(
                    "acct-watch",
                    "room-watch",
                    TableAdmissionRole.Spectator,
                    null,
                    Jti: "jti-watch-old",
                    AssignmentId: "assignment-watch"));
            ValidatedTableTicket fresh = SignValidated(
                signer,
                options,
                new TableTestTicket(
                    "acct-watch",
                    "room-watch",
                    TableAdmissionRole.Spectator,
                    null,
                    Jti: "jti-watch-fresh",
                    AssignmentId: "assignment-watch"));
            ValidatedTableTicket later = SignValidated(
                signer,
                options,
                new TableTestTicket(
                    "acct-watch",
                    "room-watch",
                    TableAdmissionRole.Spectator,
                    null,
                    Jti: "jti-watch-later",
                    AssignmentId: "assignment-watch"));

            Assert.Equal(TicketConsumptionStatus.Accepted, registry.Consume(old, "watch-old", DateTimeOffset.UtcNow).Status);
            Assert.Equal(TicketCommitStatus.Committed, registry.CommitAdmission("watch-old").Status);
            // The old spectator is still live and the room max is 1: a second live admission
            // is refused by capacity, not by the assignment tuple.
            Assert.Equal(
                TicketConsumptionStatus.SpectatorCapacityReached,
                registry.Consume(fresh, "watch-fresh-live", DateTimeOffset.UtcNow).Status);

            registry.Release("watch-old");
            Assert.Equal(
                TicketConsumptionStatus.Accepted,
                registry.Consume(fresh, "watch-fresh", DateTimeOffset.UtcNow).Status);
            Assert.Equal(TicketCommitStatus.Committed, registry.CommitAdmission("watch-fresh").Status);
            registry.Release("watch-fresh");

            // Capacity was released exactly once per session: the next refresh is accepted
            // and no phantom count from the earlier capacity refusal remains.
            Assert.Equal(
                TicketConsumptionStatus.Accepted,
                registry.Consume(later, "watch-later", DateTimeOffset.UtcNow).Status);
        }

        [Fact]
        public async Task RealKestrel_SameAssignmentFreshJti_DisplacesOld4403_AndOldJtiReplayIsRejected()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync(options =>
            {
                options.RoomTickIntervalMs = 250;
                options.HeartbeatIntervalMs = 1000;
                options.IdleDeadMs = 60_000;
            });

            const string Room = "room-reissue-live";
            const string Account = "acct-reissue-live";
            var oldTicket = new TableTestTicket(
                Account,
                Room,
                TableAdmissionRole.Player,
                0,
                Jti: "jti-old-live",
                AssignmentId: "assignment-live");
            var freshTicket = new TableTestTicket(
                Account,
                Room,
                TableAdmissionRole.Player,
                0,
                Jti: "jti-fresh-live",
                AssignmentId: "assignment-live");

            TableTestClient oldClient = await host.ConnectPlayerAsync(Room, Account, 0, oldTicket);
            try
            {
                Assert.NotNull(oldClient.Welcome);
                TableTestClient freshClient = await host.ConnectPlayerAsync(Room, Account, 0, freshTicket);
                try
                {
                    Assert.NotNull(freshClient.Welcome);

                    // The legal fresh ticket's WELCOME is the displacement point: the old live
                    // connection must close with the frozen 4403, not linger as a second owner.
                    (int oldCode, string oldReason, TableWireMessage[] oldDrained) =
                        await oldClient.ExpectCloseAsync(TimeSpan.FromSeconds(20));
                    Assert.Equal(4403, oldCode);
                    Assert.Equal(ProtocolErrorCode.DisplacedByNewConnection, oldReason);
                    Assert.Contains(
                        oldDrained,
                        message => message.Envelope != null &&
                                   message.Envelope.MessageType == MessageType.Close &&
                                   !string.IsNullOrEmpty(message.Envelope.Close.Code));

                    // The exact old jti remains spent: a replay is refused with 4401 even
                    // though the same assignment was legitimately re-ticketed.
                    TableTestClient replayClient = await TableTestClient.ConnectAsync(
                        host.WsUrl + "?room=" + Uri.EscapeDataString(Room));
                    try
                    {
                        await replayClient.SendAsync(TableWire.Hello(host.Signer.Sign(oldTicket)));
                        (int replayCode, string replayReason, _) =
                            await replayClient.ExpectCloseAsync(TimeSpan.FromSeconds(15));
                        Assert.Equal(4401, replayCode);
                        Assert.Equal(ProtocolErrorCode.InvalidToken, replayReason);
                    }
                    finally
                    {
                        await replayClient.DisposeAsync();
                    }

                    // The new owner is still alive and receives its own heartbeat answer; the
                    // displaced connection was not resurrected by the replay attempt.
                    string nonce = TableWire.RandomNonce();
                    await freshClient.SendAsync(TableWire.HeartbeatPing(nonce));
                    Envelope pong = await freshClient.ReadUntilAsync(
                        envelope => envelope.MessageType == MessageType.Heartbeat &&
                                    envelope.Heartbeat.BeatCase == Heartbeat.BeatOneofCase.Pong,
                        TimeSpan.FromSeconds(15));
                    Assert.Equal(nonce, pong.Heartbeat.Pong.Nonce);
                }
                finally
                {
                    await freshClient.DisposeAsync();
                }
            }
            finally
            {
                await oldClient.DisposeAsync();
            }
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
            // The old owner's WELCOME was delivered (the real handler's post-WELCOME step).
            Assert.Equal(TableEvictionKind.None, registry.EvictOnWelcome("old-live").Kind);

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

        /// <summary>
        /// D-1-03 (round 3): the review's REAL-HANDLER interleave — B and C both commit their
        /// seat handovers while parked inside their WELCOME write (TCS-gated sockets, no
        /// sleeps), then B's WELCOME faults first and C's second. No failed welcome may evict
        /// or hide the still-working original owner A, in that exact commit/failure order, and
        /// the seat identity must survive with its exact account for a same-account reconnect.
        /// </summary>
        [Fact]
        public async Task GatedRealHandlers_TwoFailedWelcomes_OriginalOwnerNeverLost()
        {
            TableServiceOptions options = Options();
            InMemoryTableHostRegistry hostRegistry = new InMemoryTableHostRegistry(
                options,
                new WireTableStreamContractCodec(),
                SystemTableClock.Instance,
                CryptographicTableRoundSource.Instance);
            ITicketConsumptionRegistry registry = new InMemoryTicketConsumptionRegistry(options);
            ITableSessionDirectory sessions = new InMemorySessionDirectory();
            TableTestSigner signer = new TableTestSigner("abc-gate-key");
            var keys = new StaticTableTicketKeyProvider(
                new[] { (signer.PublicKeyPem, (string?)signer.Kid) }, DateTimeOffset.UtcNow);
            var validator = new TableTicketValidator(keys, options);

            // A: the live, welcomed owner of the seat.
            ValidatedTableTicket a = SignValidated(
                signer, options, new TableTestTicket("acct-abc", "room-abc", TableAdmissionRole.Player, 0));
            Assert.Equal(TicketConsumptionStatus.Accepted, registry.Consume(a, "old-live", DateTimeOffset.UtcNow).Status);
            Assert.Equal(TicketCommitStatus.Committed, registry.CommitAdmission("old-live").Status);
            Assert.Equal(TableEvictionKind.None, registry.EvictOnWelcome("old-live").Kind);
            bool displaced = false;
            sessions.Register("old-live", () => displaced = true);

            // B runs the REAL handler: it consumes, commits its handover and parks INSIDE its
            // WELCOME write (the socket gates the first send). The ticket is presented only
            // through the handler's own admission — the jti is never pre-consumed here.
            var socketB = new GatedFailWelcomeWebSocket(WireCodec.Encode(TableWire.Hello(signer.Sign(
                new TableTestTicket("acct-abc", "room-abc", TableAdmissionRole.Player, 0)))));
            Task handlerB = new TableSessionHandler(
                socketB, "room-abc", options, new DrainState(), validator, registry, sessions,
                hostRegistry, new Microsoft.Extensions.Logging.Abstractions.NullLogger<TableSessionHandler>(), 2)
                .RunAsync(CancellationToken.None);
            await socketB.WelcomeAttempted.Task.WaitAsync(TimeSpan.FromSeconds(30)); // B parked inside its (first) WELCOME write
            List<(string SessionId, bool Welcomed)> chainAtB = SeatChain(registry, "room-abc", 0);
            Assert.Equal(2, chainAtB.Count);
            Assert.Equal(("old-live", true), (chainAtB[0].SessionId, chainAtB[0].Welcomed));
            Assert.NotEqual("old-live", chainAtB[1].SessionId);
            Assert.False(chainAtB[1].Welcomed, "B's handover commit is durable before its WELCOME write begins");
            string sessionB = chainAtB[1].SessionId;

            // C commits its handover OVER the parked B (same real-handler path).
            var socketC = new GatedFailWelcomeWebSocket(WireCodec.Encode(TableWire.Hello(signer.Sign(
                new TableTestTicket("acct-abc", "room-abc", TableAdmissionRole.Player, 0)))));
            Task handlerC = new TableSessionHandler(
                socketC, "room-abc", options, new DrainState(), validator, registry, sessions,
                hostRegistry, new Microsoft.Extensions.Logging.Abstractions.NullLogger<TableSessionHandler>(), 3)
                .RunAsync(CancellationToken.None);
            await socketC.WelcomeAttempted.Task.WaitAsync(TimeSpan.FromSeconds(30)); // C committed, parked inside its WELCOME write
            List<(string SessionId, bool Welcomed)> chainAtC = SeatChain(registry, "room-abc", 0);
            Assert.Equal(3, chainAtC.Count);
            Assert.False(chainAtC[2].Welcomed);
            string sessionC = chainAtC[2].SessionId;
            Assert.NotEqual(sessionB, sessionC);

            // B's WELCOME faults first, then C's — the review's exact failure order.
            socketB.Gate.Release();
            await Task.WhenAny(handlerB, Task.Delay(TimeSpan.FromSeconds(30)));
            Assert.True(handlerB.IsCompleted, "the failed welcome B must end its handler");
            Assert.False(displaced, "no failed welcome ever displaces the working owner");

            // After B's rollback the seat STILL belongs to A (C's entry is its own business).
            Assert.True(registry.TryGetSeatOwner("room-abc", 0, out string? ownerAfterB));
            Assert.Equal("old-live", ownerAfterB);
            List<(string SessionId, bool Welcomed)> chainAfterBRollback = SeatChain(registry, "room-abc", 0);
            Assert.Equal(2, chainAfterBRollback.Count);
            Assert.Equal(sessionC, chainAfterBRollback[1].SessionId);

            socketC.Gate.Release();
            await Task.WhenAny(handlerC, Task.Delay(TimeSpan.FromSeconds(30)));
            Assert.True(handlerC.IsCompleted, "the failed welcome C must end its handler");

            // The full failure chain reverted to the last welcome-successful owner — never
            // ownerless, never a newer failed admission.
            Assert.True(registry.TryGetSeatOwner("room-abc", 0, out string? ownerAfterC));
            Assert.Equal("old-live", ownerAfterC);
            (string EndSession, bool EndWelcomed) = Assert.Single(SeatChain(registry, "room-abc", 0));
            Assert.Equal("old-live", EndSession);
            Assert.True(EndWelcomed);
            Assert.False(displaced);

            // The seat identity survived with its EXACT account: a fresh same-account ticket
            // is admitted again (the exact review positive after the purged-rollback fix).
            ValidatedTableTicket reconnect = SignValidated(
                signer, options, new TableTestTicket("acct-abc", "room-abc", TableAdmissionRole.Player, 0));
            Assert.Equal(
                TicketConsumptionStatus.Accepted,
                registry.Consume(reconnect, "reconnect", DateTimeOffset.UtcNow).Status);

            foreach (RoomHost active in hostRegistry.Hosts)
            {
                await active.Actor.DisposeAsync();
            }
        }

        /// <summary>D-1-03 (round 5): the review's A/B/C probe matrix — B and C both commit
        /// their handovers while A is live and welcomed; B's welcome (the MIDDLE commit) MUST
        /// displace A even though the newer commit C is still unwelcomed on the chain. Every
        /// successful promotion to live owner evicts exactly the previous live session; an
        /// unwelcomed newer commit is skipped — it never displaces and never blocks the
        /// handover — and its own failure path then removes exactly its own entry.</summary>
        [Fact]
        public void WelcomeMatrix_MiddleWelcomeDisplacesPreviousLive_UnwelcomedNewerCommitNeverBlocks()
        {
            TableServiceOptions options = Options();
            var registry = new InMemoryTicketConsumptionRegistry(options);
            TableTestSigner signer = new TableTestSigner("matrix-key");

            ValidatedTableTicket a = SignValidated(
                signer, options, new TableTestTicket("acct-matrix", "matrix", TableAdmissionRole.Player, 0));
            ValidatedTableTicket b = SignValidated(
                signer, options, new TableTestTicket("acct-matrix", "matrix", TableAdmissionRole.Player, 0));
            ValidatedTableTicket c = SignValidated(
                signer, options, new TableTestTicket("acct-matrix", "matrix", TableAdmissionRole.Player, 0));

            Assert.Equal(TicketConsumptionStatus.Accepted, registry.Consume(a, "live-a", DateTimeOffset.UtcNow).Status);
            Assert.Equal(TicketCommitStatus.Committed, registry.CommitAdmission("live-a").Status);
            Assert.Equal(TableEvictionKind.None, registry.EvictOnWelcome("live-a").Kind);

            Assert.Equal(TicketConsumptionStatus.Accepted, registry.Consume(b, "conn-b", DateTimeOffset.UtcNow).Status);
            Assert.Equal(TicketCommitStatus.Committed, registry.CommitAdmission("conn-b").Status);
            Assert.Equal(TicketConsumptionStatus.Accepted, registry.Consume(c, "conn-c", DateTimeOffset.UtcNow).Status);
            Assert.Equal(TicketCommitStatus.Committed, registry.CommitAdmission("conn-c").Status);

            // B's welcome: the previous LIVE owner A is displaced even with C pending above.
            TableEvictionDecision bEviction = registry.EvictOnWelcome("conn-b");
            Assert.Equal(TableEvictionKind.DisplacedOther, bEviction.Kind);
            Assert.Equal("live-a", bEviction.SessionId);

            // C's handover then FAILS pre-welcome: its rollback/release remove exactly its
            // own entry, leaving exactly ONE welcomed owner (B) — A is never resurrected.
            registry.RollbackSeatHandover("conn-c");
            registry.Release("conn-c");

            Assert.True(registry.TryGetSeatOwner("matrix", 0, out string? owner));
            Assert.Equal("conn-b", owner);
            (string MatrixEndSession, bool MatrixEndWelcomed) = Assert.Single(SeatChain(registry, "matrix", 0));
            Assert.Equal("conn-b", MatrixEndSession);
            Assert.True(MatrixEndWelcomed);

            // The consumed jti evidence of the failed C survives (no replay re-entry).
            Assert.Equal(
                TicketConsumptionStatus.DuplicateJti,
                registry.Consume(c, "replayed-c", DateTimeOffset.UtcNow).Status);
        }

        /// <summary>D-1-03 (round 5): BOTH late commits welcome — the displacements chain
        /// one-per-welcome (A evicted by B's welcome, B evicted by C's welcome) and the chain
        /// ends with exactly one welcomed live owner.</summary>
        [Fact]
        public void WelcomeMatrix_BothWelcomesSucceed_DisplacementsChainExactlyOnceEach()
        {
            TableServiceOptions options = Options();
            var registry = new InMemoryTicketConsumptionRegistry(options);
            TableTestSigner signer = new TableTestSigner("chain2-key");

            ValidatedTableTicket a = SignValidated(
                signer, options, new TableTestTicket("acct-chain2", "chain2", TableAdmissionRole.Player, 0));
            ValidatedTableTicket b = SignValidated(
                signer, options, new TableTestTicket("acct-chain2", "chain2", TableAdmissionRole.Player, 0));
            ValidatedTableTicket c = SignValidated(
                signer, options, new TableTestTicket("acct-chain2", "chain2", TableAdmissionRole.Player, 0));

            Assert.Equal(TicketConsumptionStatus.Accepted, registry.Consume(a, "live-a", DateTimeOffset.UtcNow).Status);
            Assert.Equal(TicketCommitStatus.Committed, registry.CommitAdmission("live-a").Status);
            Assert.Equal(TableEvictionKind.None, registry.EvictOnWelcome("live-a").Kind);
            registry.Consume(b, "conn-b", DateTimeOffset.UtcNow);
            registry.CommitAdmission("conn-b");
            registry.Consume(c, "conn-c", DateTimeOffset.UtcNow);
            registry.CommitAdmission("conn-c");

            TableEvictionDecision bEviction = registry.EvictOnWelcome("conn-b");
            Assert.Equal(TableEvictionKind.DisplacedOther, bEviction.Kind);
            Assert.Equal("live-a", bEviction.SessionId);
            TableEvictionDecision cEviction = registry.EvictOnWelcome("conn-c");
            Assert.Equal(TableEvictionKind.DisplacedOther, cEviction.Kind);
            Assert.Equal("conn-b", cEviction.SessionId);

            Assert.True(registry.TryGetSeatOwner("chain2", 0, out string? owner));
            Assert.Equal("conn-c", owner);
            (string Chain2EndSession, bool Chain2EndWelcomed) = Assert.Single(SeatChain(registry, "chain2", 0));
            Assert.Equal("conn-c", Chain2EndSession);
            Assert.True(Chain2EndWelcomed);
        }

        /// <summary>D-1-03 (round 5): the review's reverse order through REAL TCS-gated
        /// handlers — C's WELCOME completes first (it REALLY displaces the working owner A
        /// through the session directory), and B's LATE welcome is closed by B's OWN handler
        /// with the frozen 4403 instead of being registered as a second live owner; exactly
        /// one live owner survives on the whole chain.</summary>
        [Fact]
        public async Task GatedRealHandlers_LateWelcomeAfterNewerHandover_SelfCloses4403_SingleLiveOwner()
        {
            TableServiceOptions options = Options();
            InMemoryTableHostRegistry hostRegistry = new InMemoryTableHostRegistry(
                options,
                new WireTableStreamContractCodec(),
                SystemTableClock.Instance,
                CryptographicTableRoundSource.Instance);
            ITicketConsumptionRegistry registry = new InMemoryTicketConsumptionRegistry(options);
            ITableSessionDirectory sessions = new InMemorySessionDirectory();
            TableTestSigner signer = new TableTestSigner("late-key");
            var keys = new StaticTableTicketKeyProvider(
                new[] { (signer.PublicKeyPem, (string?)signer.Kid) }, DateTimeOffset.UtcNow);
            var validator = new TableTicketValidator(keys, options);

            // A: the live, welcomed owner of the seat, with a REAL displacement callback.
            ValidatedTableTicket a = SignValidated(
                signer, options, new TableTestTicket("acct-late", "room-late", TableAdmissionRole.Player, 0));
            Assert.Equal(TicketConsumptionStatus.Accepted, registry.Consume(a, "live-a", DateTimeOffset.UtcNow).Status);
            Assert.Equal(TicketCommitStatus.Committed, registry.CommitAdmission("live-a").Status);
            Assert.Equal(TableEvictionKind.None, registry.EvictOnWelcome("live-a").Kind);
            var aDisplaced = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            sessions.Register("live-a", () => aDisplaced.TrySetResult(true));

            // B: real handler parked INSIDE its WELCOME write (its handover commit durable).
            var socketB = new GatedPassWelcomeWebSocket(WireCodec.Encode(TableWire.Hello(signer.Sign(
                new TableTestTicket("acct-late", "room-late", TableAdmissionRole.Player, 0)))));
            Task handlerB = new TableSessionHandler(
                socketB, "room-late", options, new DrainState(), validator, registry, sessions,
                hostRegistry, new Microsoft.Extensions.Logging.Abstractions.NullLogger<TableSessionHandler>(), 2)
                .RunAsync(CancellationToken.None);
            await socketB.WelcomeAttempted.Task.WaitAsync(TimeSpan.FromSeconds(30));
            List<(string SessionId, bool Welcomed)> chainAtB = SeatChain(registry, "room-late", 0);
            Assert.Equal(2, chainAtB.Count);
            Assert.Equal(("live-a", true), (chainAtB[0].SessionId, chainAtB[0].Welcomed));
            Assert.False(chainAtB[1].Welcomed);
            string sessionB = chainAtB[1].SessionId;

            // C commits its handover OVER the parked B, then parks inside its own WELCOME.
            var socketC = new GatedPassWelcomeWebSocket(WireCodec.Encode(TableWire.Hello(signer.Sign(
                new TableTestTicket("acct-late", "room-late", TableAdmissionRole.Player, 0)))));
            Task handlerC = new TableSessionHandler(
                socketC, "room-late", options, new DrainState(), validator, registry, sessions,
                hostRegistry, new Microsoft.Extensions.Logging.Abstractions.NullLogger<TableSessionHandler>(), 3)
                .RunAsync(CancellationToken.None);
            await socketC.WelcomeAttempted.Task.WaitAsync(TimeSpan.FromSeconds(30));
            List<(string SessionId, bool Welcomed)> chainAtC = SeatChain(registry, "room-late", 0);
            Assert.Equal(3, chainAtC.Count);
            Assert.False(chainAtC[2].Welcomed);
            string sessionC = chainAtC[2].SessionId;
            Assert.NotEqual(sessionB, sessionC);

            // C's WELCOME completes FIRST: the newest live owner REALLY displaces A.
            socketC.Gate.Release();
            await aDisplaced.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(registry.TryGetSeatOwner("room-late", 0, out string? ownerAfterC));
            Assert.Equal(sessionC, ownerAfterC);

            // B's WELCOME completes LATE: a newer handover already won, so B's OWN handler
            // closes B with the frozen 4403 instead of registering a second live owner.
            socketB.Gate.Release();
            await handlerB.WaitAsync(TimeSpan.FromSeconds(30));
            (int CloseCode, string Reason) = Assert.Single(socketB.Closes);
            Assert.Equal(4403, CloseCode);
            Assert.Equal(ProtocolErrorCode.DisplacedByNewConnection, Reason);

            // Exactly ONE live owner survives the whole interleave: C. The loser's entry is
            // gone from the chain (no resurrection if C later fails) and A was displaced
            // exactly once — B was never displaced by the directory, it closed itself.
            Assert.True(registry.TryGetSeatOwner("room-late", 0, out string? finalOwner));
            Assert.Equal(sessionC, finalOwner);
            (string LateEndSession, bool LateEndWelcomed) = Assert.Single(SeatChain(registry, "room-late", 0));
            Assert.Equal(sessionC, LateEndSession);
            Assert.True(LateEndWelcomed);

            // Let the still-running winner session exit cleanly (no leaked handler task).
            socketC.AllowClose();
            await handlerC.WaitAsync(TimeSpan.FromSeconds(30));

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

        /// <summary>The seat's committed handover chain as (session, welcomed) pairs, in
        /// commit order — the deterministic observation for the gated interleave test
        /// (no sleeps, no guessed session ids).</summary>
        private static List<(string SessionId, bool Welcomed)> SeatChain(
            ITicketConsumptionRegistry registry,
            string roomId,
            int seat)
        {
            System.Reflection.FieldInfo? bindings = typeof(InMemoryTicketConsumptionRegistry).GetField(
                "_seatBindings",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.True(bindings != null, "the seat binding map accessor must exist");
            var map = (System.Collections.IDictionary)bindings!.GetValue(registry)!;
            var chain = new List<(string SessionId, bool Welcomed)>();
            if (!map.Contains(roomId + "#" + seat))
            {
                return chain;
            }

            object lease = map[roomId + "#" + seat]!;
            System.Reflection.FieldInfo? commits = lease.GetType().GetField(
                "Commits",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Instance);
            Assert.True(commits != null, "the seat lease chain accessor must exist");
            foreach (object entry in (System.Collections.IEnumerable)commits!.GetValue(lease)!)
            {
                string? entrySession = (string?)entry.GetType().GetProperty("SessionId")!.GetValue(entry);
                bool welcomed = (bool)entry.GetType().GetProperty("Welcomed")!.GetValue(entry)!;
                chain.Add((entrySession!, welcomed));
            }

            return chain;
        }

        private static ValidatedTableTicket DirectTicket(
            string jti,
            string assignmentId,
            DateTimeOffset issuedAt,
            DateTimeOffset expiresAt)
        {
            return new ValidatedTableTicket(
                "mahjong-lobby",
                "acct-bound",
                "mahjong-table",
                "room-bound",
                assignmentId,
                TableAdmissionRole.Player,
                0,
                jti,
                issuedAt,
                expiresAt,
                ProtocolConstants.Major,
                ProtocolConstants.RulesetVersion,
                ProtocolConstants.ProjectionVersion,
                "kid-bound");
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

        /// <summary>A socket whose FIRST write (the WELCOME) parks on a deterministic gate and
        /// then SUCCEEDS: the handover commit is provably durable while a newer admission
        /// interleaves, and every close this socket records is the handler's OWN verdict
        /// (the round-5 late-welcome self-close 4403 evidence).</summary>
        private sealed class GatedPassWelcomeWebSocket : System.Net.WebSockets.WebSocket
        {
            private readonly byte[] _hello;
            private int _sends;
            private int _received;

            public GatedPassWelcomeWebSocket(byte[] hello)
            {
                _hello = hello;
                WelcomeAttempted = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                Gate = new SemaphoreSlim(0, 1);
                CloseAllowed = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            /// <summary>Set when the WELCOME send began (the commit is already durable).</summary>
            public TaskCompletionSource<bool> WelcomeAttempted { get; }

            /// <summary>Released by the test to let the WELCOME write complete.</summary>
            public SemaphoreSlim Gate { get; }

            /// <summary>Every terminal close the handler delivered to this socket.</summary>
            public List<(int CloseCode, string Reason)> Closes { get; } = new List<(int, string)>();

            private TaskCompletionSource<bool> CloseAllowed { get; }

            /// <summary>Lets a still-running admitted session's receive loop observe a close and exit.</summary>
            public void AllowClose()
            {
                CloseAllowed.TrySetResult(true);
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
                Gate.Dispose();
            }

            public override Task CloseAsync(
                System.Net.WebSockets.WebSocketCloseStatus closeStatus,
                string? statusDescription,
                CancellationToken cancellationToken) => Task.CompletedTask;

            public override Task CloseOutputAsync(
                System.Net.WebSockets.WebSocketCloseStatus closeStatus,
                string? statusDescription,
                CancellationToken cancellationToken)
            {
                lock (Closes)
                {
                    Closes.Add(((int)closeStatus, statusDescription ?? string.Empty));
                }

                return Task.CompletedTask;
            }

            public override Task<System.Net.WebSockets.WebSocketReceiveResult> ReceiveAsync(
                ArraySegment<byte> buffer,
                CancellationToken cancellationToken)
            {
                if (Interlocked.Exchange(ref _received, 1) == 0)
                {
                    _hello.AsSpan().CopyTo(buffer);
                    return Task.FromResult(new System.Net.WebSockets.WebSocketReceiveResult(
                        _hello.Length,
                        System.Net.WebSockets.WebSocketMessageType.Binary,
                        true));
                }

                // The admitted session stays open (never a teardown race with the test's
                // assertions) until the test explicitly lets it observe the close.
                return CloseAllowed.Task.ContinueWith(
                    _ => new System.Net.WebSockets.WebSocketReceiveResult(
                        0,
                        System.Net.WebSockets.WebSocketMessageType.Binary,
                        true),
                    TaskScheduler.Default);
            }

            public override async Task SendAsync(
                ArraySegment<byte> buffer,
                System.Net.WebSockets.WebSocketMessageType messageType,
                bool endOfMessage,
                CancellationToken cancellationToken)
            {
                if (Interlocked.Increment(ref _sends) == 1)
                {
                    // The FIRST write is the WELCOME: signal it, then park until released.
                    WelcomeAttempted.TrySetResult(true);
                    await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                // Every write (the gated WELCOME and any later close envelope) succeeds.
            }
        }

        /// <summary>A socket whose WELCOME write parks on a deterministic gate and THEN faults:
        /// the commit is provably done (the send runs strictly after CommitAdmission) while a
        /// newer admission interleaves, reproducing the review's A/B/C ordering with zero
        /// sleeps.</summary>
        private sealed class GatedFailWelcomeWebSocket : System.Net.WebSockets.WebSocket
        {
            private readonly byte[] _hello;

            public GatedFailWelcomeWebSocket(byte[] hello)
            {
                _hello = hello;
                WelcomeAttempted = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                Gate = new SemaphoreSlim(0, 1);
            }

            /// <summary>Set when the WELCOME send began (the commit is already durable).</summary>
            public TaskCompletionSource<bool> WelcomeAttempted { get; }

            /// <summary>Released by the test to let the WELCOME write fault.</summary>
            public SemaphoreSlim Gate { get; }

            public override System.Net.WebSockets.WebSocketCloseStatus? CloseStatus => null;

            public override string? CloseStatusDescription => null;

            public override System.Net.WebSockets.WebSocketState State => System.Net.WebSockets.WebSocketState.Open;

            public override string? SubProtocol => null;

            public override void Abort()
            {
            }

            public override void Dispose()
            {
                Gate.Dispose();
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

            public override async Task SendAsync(
                ArraySegment<byte> buffer,
                System.Net.WebSockets.WebSocketMessageType messageType,
                bool endOfMessage,
                CancellationToken cancellationToken)
            {
                WelcomeAttempted.TrySetResult(true);
                if (Interlocked.Exchange(ref _gatedUsed, 1) == 1)
                {
                    // Every send AFTER the gated WELCOME faults immediately: the transport is
                    // dead exactly like a real failed write, and a close-path retry can never
                    // park on an already-consumed gate.
                    throw new System.IO.IOException("post-failure writes are dead too");
                }

                await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                throw new System.IO.IOException("gated failed WELCOME write");
            }

            private int _gatedUsed;
        }
    }
}
