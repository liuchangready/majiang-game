using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.Domain;
using Mahjong.Infrastructure;
using Mahjong.TableRuntime;
using Mahjong.TableService;
using Mahjong.TableService.Tests;
using Xunit;

namespace Mahjong.TableService.DurableHost.Tests
{
    /// <summary>
    /// D-R1-01 acceptance: a cold-recovered room must derive its opening-discard tsumogiri
    /// identity from the room's OWN verified persisted state, never from the successor
    /// process's freshly drawn placeholder round. Every test pairs a first host (deal-extra
    /// tile X) with a second host over the SAME database whose round source deals a
    /// DIFFERENT hand and draws a different deal-extra tile — the exact shape under which
    /// the pre-fix registry silently rewrote the opening discard's fact from the new
    /// shuffle. The pairings are poisoned so the pre-fix behavior and the persisted-truth
    /// behavior are OPPOSITE values in every test:
    ///   - tier 1 (opening discard replays from checkpoint-0 + event tail): the
    ///     checkpoint payload's intact deal-extra evidence decides;
    ///   - tier 2 (a checkpoint captured AFTER the opening discard consumed the evidence,
    ///     driven through the actor's own accepted SaveCheckpointAsync): the checkpoint's
    ///     published river fact decides.
    /// There is no in-memory substitute and no skip: the database is the acceptance.
    /// </summary>
    [Collection(DurableHostCollection.Name)]
    public sealed class PostgresDurableHostRecoveryAnchorTests
    {
        private const string RoomBase = "p54b-anchor";
        private static readonly TimeSpan LeaseAgeOut = TimeSpan.FromSeconds(6.5);

        // ------------------------------------------------------------------
        // Tier 1: the opening discard replays from checkpoint-0 + event tail
        // ------------------------------------------------------------------

        [Fact]
        public async Task ColdRecovery_TsumogiriOpening_FactSurvivesDifferentSecondSource()
        {
            await DurableTableHostEnvironment.ResetAsync();
            var signer = new TableTestSigner("anchor-tsumogiri-key");
            string room = RoomBase + "-tsumo";
            TableServiceHostRuntime? first = null;
            TableServiceHostRuntime? second = null;
            try
            {
                DiscardFact before;
                {
                    first = await StartHostAsync(
                        PostgresOptions(), signer, AnchorRoundSource(handStart: 108, drawn: 127));
                    await using TableTestClient client = await ConnectPlayerAsync(
                        first, signer, room, "acct-anchor-tsumo", 0);
                    before = await DiscardTileAsync(client, tile: 127);
                    Assert.True(before.Discard.Tsumogiri, "pre-crash: the drawn tile's own discard is a tsumogiri");
                    await first.StopAsync();
                }

                await Task.Delay(LeaseAgeOut);

                // drawn=126: under the pre-fix registry the fresh anchor tile (126) misses
                // the committed discard (127) and rewrote the fact to FALSE across recovery.
                second = await StartHostAsync(
                    PostgresOptions(), signer, AnchorRoundSource(handStart: 108, drawn: 126));
                await using TableTestClient recovered = await ConnectPlayerAsync(
                    second, signer, room, "acct-anchor-tsumo", 0);
                DiscardFact after = await ReadOpeningDiscardAsync(recovered);

                Assert.Equal(before.Discard.TileId, after.Discard.TileId);
                Assert.True(after.Discard.Tsumogiri, "the recovered room must keep the persisted tsumogiri fact");
                Assert.Equal(before.Discard.TurnNumber, after.Discard.TurnNumber);
                AssertHistoryExtends("opening", before.History, after.History);
                Assert.True(
                    recovered.Welcome!.Baseline.StateVersion >= before.CommandVersion,
                    "the recovered baseline must include the pre-crash commit");

                await second.StopAsync();
                second = null;
            }
            finally
            {
                await StopQuietlyAsync(first);
                await StopQuietlyAsync(second);
            }
        }

        [Fact]
        public async Task ColdRecovery_HandDiscardOpening_NeverRewrittenToTsumogiri()
        {
            await DurableTableHostEnvironment.ResetAsync();
            var signer = new TableTestSigner("anchor-hand-key");
            string room = RoomBase + "-hand";
            TableServiceHostRuntime? first = null;
            TableServiceHostRuntime? second = null;
            try
            {
                DiscardFact before;
                {
                    first = await StartHostAsync(
                        PostgresOptions(), signer, AnchorRoundSource(handStart: 108, drawn: 127));
                    await using TableTestClient client = await ConnectPlayerAsync(
                        first, signer, room, "acct-anchor-hand", 0);
                    before = await DiscardTileAsync(client, tile: 108);
                    Assert.False(before.Discard.Tsumogiri, "pre-crash: a hand tile's discard is not a tsumogiri");
                    await first.StopAsync();
                }

                await Task.Delay(LeaseAgeOut);

                // The second source's deal-extra IS the committed hand discard's tile
                // (108, dealt outside its own 109-121 hand): under the pre-fix registry
                // the fresh anchor equals the committed discard and rewrote the fact to
                // TRUE across recovery — the exact inversion this test forbids forever.
                second = await StartHostAsync(
                    PostgresOptions(), signer, AnchorRoundSource(handStart: 109, drawn: 108));
                await using TableTestClient recovered = await ConnectPlayerAsync(
                    second, signer, room, "acct-anchor-hand", 0);
                DiscardFact after = await ReadOpeningDiscardAsync(recovered);

                Assert.Equal(before.Discard.TileId, after.Discard.TileId);
                Assert.False(after.Discard.Tsumogiri, "the recovered room must keep the hand-discard fact");
                Assert.Equal(before.Discard.TurnNumber, after.Discard.TurnNumber);
                AssertHistoryExtends("opening", before.History, after.History);

                await second.StopAsync();
                second = null;
            }
            finally
            {
                await StopQuietlyAsync(first);
                await StopQuietlyAsync(second);
            }
        }

        // ------------------------------------------------------------------
        // Tier 2: a checkpoint captured AFTER the opening discard consumed
        // the pending deal-extra evidence (the actor's own accepted API)
        // ------------------------------------------------------------------

        [Fact]
        public async Task ColdRecovery_AfterCheckpoint_TsumogiriVerdictSurvives()
        {
            await DurableTableHostEnvironment.ResetAsync();
            var signer = new TableTestSigner("anchor-ckpt-tsumo-key");
            string room = RoomBase + "-ckpt-tsumo";
            TableServiceHostRuntime? first = null;
            TableServiceHostRuntime? second = null;
            try
            {
                DiscardFact before;
                {
                    first = await StartHostAsync(
                        PostgresOptions(), signer, AnchorRoundSource(handStart: 108, drawn: 127));
                    await using TableTestClient client = await ConnectPlayerAsync(
                        first, signer, room, "acct-anchor-ckpt-tsumo", 0);
                    before = await DiscardTileAsync(client, tile: 127);
                    Assert.True(before.Discard.Tsumogiri);
                    // A checkpoint captured at or above the discard's commit can no longer
                    // carry the pending deal-extra evidence (the Domain consumed it with
                    // the discard), so recovery must decide from the checkpoint's published
                    // river fact — the tier-2 anchor.
                    await CheckpointAsync(first, room, before.CommandVersion);
                    await first.StopAsync();
                }

                await Task.Delay(LeaseAgeOut);

                second = await StartHostAsync(
                    PostgresOptions(), signer, AnchorRoundSource(handStart: 109, drawn: 108));
                await using TableTestClient recovered = await ConnectPlayerAsync(
                    second, signer, room, "acct-anchor-ckpt-tsumo", 0);
                DiscardFact after = await ReadOpeningDiscardAsync(recovered);

                Assert.Equal(before.Discard.TileId, after.Discard.TileId);
                Assert.True(after.Discard.Tsumogiri, "the tier-2 anchor must carry the published tsumogiri verdict");
                Assert.Equal(before.Discard.TurnNumber, after.Discard.TurnNumber);
                AssertHistoryExtends("opening", before.History, after.History);

                await second.StopAsync();
                second = null;
            }
            finally
            {
                await StopQuietlyAsync(first);
                await StopQuietlyAsync(second);
            }
        }

        [Fact]
        public async Task ColdRecovery_AfterCheckpoint_HandDiscardStaysHandDiscard()
        {
            await DurableTableHostEnvironment.ResetAsync();
            var signer = new TableTestSigner("anchor-ckpt-hand-key");
            string room = RoomBase + "-ckpt-hand";
            TableServiceHostRuntime? first = null;
            TableServiceHostRuntime? second = null;
            try
            {
                DiscardFact before;
                {
                    first = await StartHostAsync(
                        PostgresOptions(), signer, AnchorRoundSource(handStart: 108, drawn: 127));
                    await using TableTestClient client = await ConnectPlayerAsync(
                        first, signer, room, "acct-anchor-ckpt-hand", 0);
                    before = await DiscardTileAsync(client, tile: 108);
                    Assert.False(before.Discard.Tsumogiri);
                    await CheckpointAsync(first, room, before.CommandVersion);
                    await first.StopAsync();
                }

                await Task.Delay(LeaseAgeOut);

                // drawn=108: the pre-fix fresh anchor would equal the committed discard
                // and flip the fact to TRUE; the published verdict (tsumogiri=false) must
                // win.
                second = await StartHostAsync(
                    PostgresOptions(), signer, AnchorRoundSource(handStart: 109, drawn: 108));
                await using TableTestClient recovered = await ConnectPlayerAsync(
                    second, signer, room, "acct-anchor-ckpt-hand", 0);
                DiscardFact after = await ReadOpeningDiscardAsync(recovered);

                Assert.Equal(before.Discard.TileId, after.Discard.TileId);
                Assert.False(after.Discard.Tsumogiri, "the tier-2 anchor must carry the published hand-discard verdict");
                Assert.Equal(before.Discard.TurnNumber, after.Discard.TurnNumber);
                AssertHistoryExtends("opening", before.History, after.History);

                await second.StopAsync();
                second = null;
            }
            finally
            {
                await StopQuietlyAsync(first);
                await StopQuietlyAsync(second);
            }
        }

        // ------------------------------------------------------------------
        // Composite: full public history equality + continued play + receipt
        // ------------------------------------------------------------------

        [Fact]
        public async Task ColdRecovery_FullPublicHistoryEqual_ContinuedPlayConsistent_ReceiptPreserved()
        {
            await DurableTableHostEnvironment.ResetAsync();
            var signer = new TableTestSigner("anchor-history-key");
            string room = RoomBase + "-history";
            TableServiceHostRuntime? first = null;
            TableServiceHostRuntime? second = null;
            try
            {
                string requestId;
                string scopeId;
                ulong commandVersion;
                DiscardFact before;
                {
                    // This test rides the V4 deadline cadence (absent seats auto-resolve
                    // on their own schedule) and waits up to 90s per read; the default
                    // 60s idle-dead would kill the recovered room while the cadence is
                    // still turning under suite load. Nothing here relaxes an assertion —
                    // it only stops the harness from closing the room mid-wait.
                    first = await StartHostAsync(
                        PostgresOptions(o => o.IdleDeadMs = 240_000),
                        signer,
                        AnchorRoundSource(handStart: 108, drawn: 127));
                    await using TableTestClient client = await ConnectPlayerAsync(
                        first, signer, room, "acct-anchor-history", 0);
                    before = await DiscardTileAsync(client, tile: 127);
                    requestId = before.RequestId;
                    scopeId = before.ScopeId;
                    commandVersion = before.CommandVersion;
                    await first.StopAsync();
                }

                await Task.Delay(LeaseAgeOut);

                second = await StartHostAsync(
                    PostgresOptions(o => o.IdleDeadMs = 240_000),
                    signer,
                    AnchorRoundSource(handStart: 108, drawn: 126));
                await using TableTestClient recovered = await ConnectPlayerAsync(
                    second, signer, room, "acct-anchor-history", 0);
                DiscardFact after = await ReadOpeningDiscardAsync(recovered);
                Assert.True(after.Discard.Tsumogiri);
                Assert.Equal(127u, after.Discard.TileId);
                AssertHistoryExtends("opening", before.History, after.History);

                // The pre-crash receipt survives recovery through the durable ledger.
                await recovered.SendAsync(TableWire.ReceiptQuery(recovered.Welcome!, scopeId, requestId));
                Envelope lookup = await recovered.ReadUntilAsync(
                    envelope => envelope.MessageType == MessageType.ReceiptLookup,
                    TimeSpan.FromSeconds(30));
                Assert.Equal(Contracts.Wire.LookupStatus.Found, lookup.ReceiptLookup.Status);
                Assert.Equal(commandVersion, lookup.ReceiptLookup.Receipt.CommandVersion);

                // The recovered room keeps playing. The journal of earlier runs pins the V4
                // deadline cadence at ~23s per turn, and deadline auto-play applies whether
                // or not the seat is connected — seat 1's stale pre-crash decision resolves
                // itself right on schedule. So the test rides that cadence instead of
                // fighting it: waits are sized in multiples of it, and the moment a client's
                // decision opens the submission lands (milliseconds, versus a ~23s deadline).
                // Turn order from the recovered baseline: seat 3 resolves on its deadline,
                // then seat 0 plays a REAL client discard, which opens seat 1's decision in
                // the same commit wave; the loop then lands seat 1's own real discard. Only
                // a DISCARD action advances the river, so every submission targets it.
                await using TableTestClient seat1 = await ConnectPlayerAsync(
                    second, signer, room, "acct-anchor-history-1", 1);

                Envelope seat0Decision = await recovered.ReadUntilAsync(
                    message => message.PlayerUpdate?.Snapshot?.PrivateView?.Decision != null,
                    TimeSpan.FromSeconds(90));
                Contracts.Wire.PlayerSnapshot seat0Snapshot = seat0Decision.PlayerUpdate!.Snapshot;
                Contracts.Wire.DecisionView seat0View = seat0Snapshot.PrivateView!.Decision!;
                Contracts.Wire.LegalActionView? seat0Discard = seat0View.Actions.FirstOrDefault(
                    candidate => candidate.ActionType == (uint)ProtocolActionType.DiscardTile);
                Assert.NotNull(seat0Discard);
                string seat0Request = TableWire.RandomId("req");
                await recovered.SendAsync(TableWire.Command(
                    recovered.Welcome!,
                    seat0Request,
                    Contracts.CommandKind.SubmitAction,
                    TableWire.SubmitActionPayload(
                        seat0Snapshot.RoundId,
                        seat0View.DecisionId,
                        seat0View.ControlEpoch,
                        seat0Discard.ActionType,
                        seat0Discard.TileIds.ToArray()),
                    expectedStateVersion: seat0Snapshot.StateVersion));
                Envelope seat0Receipt = await recovered.ReadUntilAsync(
                    candidate => candidate.MessageType == MessageType.Receipt &&
                                 candidate.RequestId == seat0Request,
                    TimeSpan.FromSeconds(90));
                Assert.Equal(Contracts.Wire.ReceiptStatus.Applied, seat0Receipt.Receipt.Status);

                bool applied = false;
                Envelope seat1Receipt = null!;
                for (int attempt = 0; attempt < 5 && !applied; attempt++)
                {
                    Envelope seat1Decision = await seat1.ReadUntilAsync(
                        message => message.PlayerUpdate?.Snapshot?.PrivateView?.Decision != null,
                        TimeSpan.FromSeconds(90));
                    Contracts.Wire.PlayerSnapshot seat1Snapshot = seat1Decision.PlayerUpdate!.Snapshot;
                    Contracts.Wire.DecisionView seat1View = seat1Snapshot.PrivateView!.Decision!;
                    Contracts.Wire.LegalActionView? discard = seat1View.Actions.FirstOrDefault(
                        candidate => candidate.ActionType == (uint)ProtocolActionType.DiscardTile);
                    if (discard == null)
                    {
                        continue;
                    }

                    string seat1Request = TableWire.RandomId("req");
                    await seat1.SendAsync(TableWire.Command(
                        seat1.Welcome!,
                        seat1Request,
                        Contracts.CommandKind.SubmitAction,
                        TableWire.SubmitActionPayload(
                            seat1Snapshot.RoundId,
                            seat1View.DecisionId,
                            seat1View.ControlEpoch,
                            discard.ActionType,
                            discard.TileIds.ToArray()),
                        expectedStateVersion: seat1Snapshot.StateVersion));
                    seat1Receipt = await seat1.ReadUntilAsync(
                        candidate => candidate.MessageType == MessageType.Receipt &&
                                     candidate.RequestId == seat1Request,
                        TimeSpan.FromSeconds(90));
                    applied = seat1Receipt.Receipt.Status == Contracts.Wire.ReceiptStatus.Applied;
                }

                Assert.True(applied, "seat 1 could not land a real post-recovery discard in five decision cycles");

                // The committed history stays exactly what recovery served — the opening
                // discard is neither rewritten nor duplicated by the continued play. The
                // post-play read anchors on the receipt's committed command version (the
                // replay queue legitimately still carries older snapshots, including
                // seat 1's deadline auto-play), so only a board at or past the submitted
                // command can satisfy it.
                Envelope advanced = await seat1.ReadUntilAsync(
                    message => message.PlayerUpdate?.Snapshot is Contracts.Wire.PlayerSnapshot snapshot &&
                               snapshot.StateVersion >= seat1Receipt.Receipt.CommandVersion &&
                               snapshot.PublicView?.Board.Players
                                   .Any(p => p.Seat == 1 && p.Discards.Count > 0) == true,
                    TimeSpan.FromSeconds(90));
                PublicHistory played = PublicHistoryCapture(advanced.PlayerUpdate!.Snapshot);
                Contracts.Wire.PlayerPublicView seat0 = advanced.PlayerUpdate!.Snapshot.PublicView!.Board.Players[0];
                Assert.True(seat0.Discards[0].Tsumogiri);
                Assert.Equal(127u, seat0.Discards[0].TileId);
                AssertHistoryExtends("post-play", after.History, played);

                await second.StopAsync();
                second = null;
            }
            finally
            {
                await StopQuietlyAsync(first);
                await StopQuietlyAsync(second);
            }
        }

        // ------------------------------------------------------------------
        // The bounded cold-recovery retry. The frozen install path claims the lease
        // first, rebuilds the room on the serial queue and re-verifies at two points
        // — nothing renews the lease in between, so on a cold or loaded process that
        // window can outlive a minimum-sized lease TTL (measured at 7431ms against a
        // 5s TTL in the run that motivated this pair) and the fence refuses its own
        // claim: RECOVERY_STATE_CHANGED at the verification fence or
        // STALE_OWNER_EPOCH at the installation fence. The registry retries ONCE
        // after either. These tests drive the refusals deterministically by expiring
        // the lease ROW in PostgreSQL the moment the recovery attempt claims it —
        // the exact arithmetic the fences check, no fake clocks, no production hooks.
        // ------------------------------------------------------------------

        [Fact]
        public async Task ColdRecovery_TransientFenceRefusal_RecoversOnBoundedRetry()
        {
            await DurableTableHostEnvironment.ResetAsync();
            var signer = new TableTestSigner("anchor-retry-key");
            string room = RoomBase + "-retry";
            TableServiceHostRuntime? first = null;
            TableServiceHostRuntime? second = null;
            using var flipperCts = new CancellationTokenSource();
            try
            {
                first = await StartHostAsync(
                    PostgresOptions(), signer, AnchorRoundSource(handStart: 108, drawn: 127));
                await using (TableTestClient client = await ConnectPlayerAsync(
                    first, signer, room, "acct-anchor-retry", 0))
                {
                    await DiscardTileAsync(client, tile: 127);
                }

                await first.StopAsync();
                await Task.Delay(LeaseAgeOut);
                long baseEpoch = await LeaseEpochAsync(room);
                Assert.True(baseEpoch >= 1, "the first host must leave a claimed lease row behind");

                // Expire EXACTLY the next epoch's lease the moment attempt one claims
                // it, then stand down: attempt two's fresh claim must stay valid. Which
                // of the fence's two install-time checkpoints reads the flipped row
                // first is a race (verification fence → RECOVERY_STATE_CHANGED,
                // installation fence → STALE_OWNER_EPOCH) — the registry must retry
                // either, because both mean "the claimed lease expired inside the
                // unrenewed recovery window".
                Task flipper = FlipLeaseOnceForEpochAsync(room, baseEpoch + 1, flipperCts.Token);

                second = await StartHostAsync(
                    PostgresOptions(), signer, AnchorRoundSource(handStart: 108, drawn: 126));
                await using TableTestClient recovered = await ConnectPlayerAsync(
                    second, signer, room, "acct-anchor-retry", 0);
                DiscardFact after = await ReadOpeningDiscardAsync(recovered);
                Assert.True(
                    after.Discard.Tsumogiri,
                    "the retried recovery must still serve the persisted tsumogiri fact");

                // Two claims prove attempt one was refused and the retry recovered:
                // a single-attempt recovery would still sit at baseEpoch + 1.
                Task done = await Task.WhenAny(flipper, Task.Delay(30_000));
                Assert.True(done == flipper, "the lease flipper did not finish within 30s");
                long finalEpoch = await LeaseEpochAsync(room);
                Assert.Equal(baseEpoch + 2, finalEpoch);

                await second.StopAsync();
                second = null;
            }
            finally
            {
                flipperCts.Cancel();
                await StopQuietlyAsync(first);
                await StopQuietlyAsync(second);
            }
        }

        [Fact]
        public async Task ColdRecovery_PersistentFenceRefusal_FailsClosed()
        {
            await DurableTableHostEnvironment.ResetAsync();
            var signer = new TableTestSigner("anchor-refusal-key");
            string room = RoomBase + "-refusal";
            TableServiceHostRuntime? first = null;
            TableServiceHostRuntime? second = null;
            using var flipperCts = new CancellationTokenSource();
            try
            {
                first = await StartHostAsync(
                    PostgresOptions(), signer, AnchorRoundSource(handStart: 108, drawn: 127));
                await using (TableTestClient client = await ConnectPlayerAsync(
                    first, signer, room, "acct-anchor-refusal", 0))
                {
                    await DiscardTileAsync(client, tile: 127);
                }

                await first.StopAsync();
                await Task.Delay(LeaseAgeOut);
                long baseEpoch = await LeaseEpochAsync(room);
                Assert.True(baseEpoch >= 1, "the first host must leave a claimed lease row behind");

                // Expire EVERY claim from the next epoch upward, for as long as the
                // admission runs: both attempts must trip a fence (either install-time
                // checkpoint — see the retry test above), and the second refusal must
                // be final for that admission.
                Task flipper = FlipLeaseFromEpochOnwardAsync(room, baseEpoch + 1, flipperCts.Token);

                second = await StartHostAsync(
                    PostgresOptions(), signer, AnchorRoundSource(handStart: 108, drawn: 126));
                await using TableTestClient refused = await TableTestClient.ConnectAsync(WsUrl(second, room));
                Exception admitFailure = await Assert.ThrowsAnyAsync<Exception>(() =>
                    refused.AdmitAsync(signer.Sign(
                        new TableTestTicket("acct-anchor-refusal", room, TableAdmissionRole.Player, 0))));
                Assert.Contains(
                    "4429",
                    admitFailure.Message,
                    StringComparison.Ordinal);

                PostgresTableHostRegistry registry =
                    Assert.IsAssignableFrom<PostgresTableHostRegistry>(second.HostRegistry);
                Assert.True(
                    registry.FailedRooms.TryGetValue(room, out string? code) &&
                    (code.Contains("RECOVERY_STATE_CHANGED", StringComparison.Ordinal) ||
                     code.Contains("STALE_OWNER_EPOCH", StringComparison.Ordinal)),
                    "the final refusal must register the room with the fence's reason code: " +
                    (code ?? "<none>"));
                Assert.False(registry.TryGet(room, out _), "no room host may be served after the final refusal");

                // Both attempts claimed a lease (the refusal is the SECOND one's).
                long finalEpoch = await LeaseEpochAsync(room);
                Assert.Equal(baseEpoch + 2, finalEpoch);

                flipperCts.Cancel();
                await flipper;
                await second.StopAsync();
                second = null;
            }
            finally
            {
                flipperCts.Cancel();
                await StopQuietlyAsync(first);
                await StopQuietlyAsync(second);
            }
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        /// <summary>The room's current lease owner epoch (0 when no row exists).</summary>
        private static async Task<long> LeaseEpochAsync(string room)
        {
            object? value = await DurableTableHostEnvironment.ScalarAsync(
                "SELECT owner_epoch::bigint FROM table_store.table_lease WHERE room_id = @room",
                ("room", room));
            return value == null || value is DBNull
                ? 0L
                : Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Expires the lease row for EXACTLY one owner epoch the moment it appears, then
        /// stops. This is the fence's own arithmetic (IsExpiredAt over the persisted row)
        /// driven through real PostgreSQL — attempt one is refused with the same
        /// RECOVERY_STATE_CHANGED the unrenewed-window race produces, and attempt two's
        /// fresh claim is never touched.
        /// </summary>
        private static async Task FlipLeaseOnceForEpochAsync(
            string room,
            long targetEpoch,
            CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int flipped = await DurableTableHostEnvironment.ExecuteAsync(
                    "UPDATE table_store.table_lease " +
                    "   SET lease_expires_at = clock_timestamp() - interval '1 second' " +
                    " WHERE room_id = @room AND owner_epoch = @epoch" +
                    "   AND lease_expires_at > clock_timestamp()",
                    ("room", room),
                    ("epoch", targetEpoch));
                if (flipped > 0)
                {
                    return;
                }

                // Attempt two already claimed a newer epoch (the flip lost the race):
                // stand down without touching it.
                if (await LeaseEpochAsync(room) > targetEpoch)
                {
                    return;
                }

                try
                {
                    await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        /// <summary>
        /// Expires EVERY claim from <paramref name="minEpoch"/> upward until cancelled —
        /// both recovery attempts trip the fence, so the admission ends in the final,
        /// fail-closed refusal.
        /// </summary>
        private static async Task FlipLeaseFromEpochOnwardAsync(
            string room,
            long minEpoch,
            CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await DurableTableHostEnvironment.ExecuteAsync(
                    "UPDATE table_store.table_lease " +
                    "   SET lease_expires_at = clock_timestamp() - interval '1 second' " +
                    " WHERE room_id = @room AND owner_epoch >= @epoch" +
                    "   AND lease_expires_at > clock_timestamp()",
                    ("room", room),
                    ("epoch", minEpoch));
                try
                {
                    await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        private readonly record struct DiscardFact(
            string RequestId,
            string ScopeId,
            ulong CommandVersion,
            Mahjong.Contracts.Wire.DiscardView Discard,
            PublicHistory History);

        /// <summary>One seat's committed public history, entry by entry.</summary>
        private sealed class SeatHistory
        {
            public bool Riichi;
            public List<string> Melds = new List<string>();
            public List<string> Concealed = new List<string>();
            public List<string> Discards = new List<string>();
        }

        /// <summary>
        /// The canonical public-history capture: every fact whose truth must survive
        /// recovery (immutable board facts, scores, dora, and each seat's complete public
        /// river, melds and riichi register). A still-live room may legitimately ADVANCE
        /// the history between two reads (the service-account driver keeps playing for
        /// absent seats), so the acceptance comparison is <see cref="AssertHistoryExtends"/>:
        /// the recovered history must extend — never contradict — the pre-crash history.
        /// </summary>
        private sealed class PublicHistory
        {
            public string BoardConstants = string.Empty;
            public SeatHistory[] Seats = Array.Empty<SeatHistory>();
        }

        private static PublicHistory PublicHistoryCapture(Contracts.Wire.PlayerSnapshot snapshot)
        {
            Mahjong.Contracts.Wire.PublicBoardView board = snapshot.PublicView!.Board;
            var history = new PublicHistory
            {
                BoardConstants =
                    "phase" + board.Phase +
                    " wind" + board.RoundWind +
                    " number" + board.RoundNumber +
                    " honba" + board.Honba +
                    " dealer" + board.DealerSeat +
                    " dora[" + string.Join(",", board.DoraIndicators) + "]" +
                    " scores[" + string.Join(";", board.Scores.Select(score =>
                        score.Seat + "=" + score.Score)) + "]",
                Seats = board.Players.OrderBy(player => player.Seat).Select(player => new SeatHistory
                {
                    Riichi = player.Riichi,
                    Melds = player.Melds.Select(meld =>
                        (int)meld.Type + ":" + string.Join("+", meld.TileIds)).ToList(),
                    Concealed = player.ConcealedMelds.Select(meld =>
                        (int)meld.Type + "x" + meld.TileCount).ToList(),
                    Discards = player.Discards.Select(discard =>
                        discard.TileId + ":" + discard.Tsumogiri + ":" +
                        discard.RiichiDiscard + ":" + discard.TurnNumber).ToList()
                }).ToArray()
            };
            return history;
        }

        /// <summary>
        /// The recovered history must EQUAL or EXTEND the pre-crash history: identical
        /// immutable board facts, and every seat's committed entries either identical or
        /// appended in order (a new entry may only JOIN the end of a list; a riichi
        /// register may only flip false to true). A recovery that rewrote, reordered or
        /// dropped ANY committed entry fails here.
        /// </summary>
        private static void AssertHistoryExtends(string stage, PublicHistory before, PublicHistory after)
        {
            Assert.True(
                string.Equals(before.BoardConstants, after.BoardConstants, StringComparison.Ordinal),
                stage + ": immutable board facts changed across recovery:" + Environment.NewLine +
                "  before: " + before.BoardConstants + Environment.NewLine +
                "  after:  " + after.BoardConstants);
            Assert.Equal(before.Seats.Length, after.Seats.Length);
            for (int seat = 0; seat < before.Seats.Length; seat++)
            {
                Assert.True(
                    !before.Seats[seat].Riichi || after.Seats[seat].Riichi,
                    stage + ": seat " + seat + " riichi register was reset across recovery");
                AssertListExtends(stage, "seat " + seat + " melds", before.Seats[seat].Melds, after.Seats[seat].Melds);
                AssertListExtends(stage, "seat " + seat + " concealed", before.Seats[seat].Concealed, after.Seats[seat].Concealed);
                AssertListExtends(stage, "seat " + seat + " discards", before.Seats[seat].Discards, after.Seats[seat].Discards);
            }
        }

        private static void AssertListExtends(string stage, string what, List<string> before, List<string> after)
        {
            Assert.True(
                after.Count >= before.Count,
                stage + ": " + what + " shrank across recovery (" + before.Count + " -> " + after.Count + ")");
            for (int i = 0; i < before.Count; i++)
            {
                Assert.True(
                    string.Equals(before[i], after[i], StringComparison.Ordinal),
                    stage + ": " + what + " entry " + i + " was rewritten across recovery:" +
                    Environment.NewLine +
                    "  before: " + before[i] + Environment.NewLine +
                    "  after:  " + after[i]);
            }
        }

        private static async Task StopQuietlyAsync(TableServiceHostRuntime? runtime)
        {
            if (runtime == null)
            {
                return;
            }

            try
            {
                await runtime.StopAsync();
            }
            catch (Exception)
            {
                // A best-effort final stop: an assert-failed test must not leak live host
                // sessions into the next test's TRUNCATE (they deadlock against it).
            }
        }

        private static TableServiceOptions PostgresOptions(Action<TableServiceOptions>? configure = null)
        {
            var options = new TableServiceOptions
            {
                StoreMode = TableHostStoreMode.Postgres,
                PostgresConnectionString = DurableTableHostEnvironment.RuntimeConnectionString,
                PostgresPayloadKeyBase64 = DurableTableHostEnvironment.PayloadKey,
                PostgresApplyMigrations = false,
                HandshakeTimeoutMs = 2000,
                HeartbeatIntervalMs = 500,
                IdleDeadMs = 60_000,
                RoomTickIntervalMs = 50,
                LeaseTtlSeconds = 5
            };
            configure?.Invoke(options);
            return options;
        }

        private static async Task<TableServiceHostRuntime> StartHostAsync(
            TableServiceOptions options,
            TableTestSigner signer,
            ITableRoundSource roundSource)
        {
            TableServiceHostRuntime runtime = TableServiceWebHost.Build(
                options,
                new StaticTableTicketKeyProvider(
                    new[] { (signer.PublicKeyPem, (string?)signer.Kid) },
                    DateTimeOffset.UtcNow),
                System.Net.IPAddress.Loopback,
                0,
                roundSource);
            await runtime.StartAsync();
            return runtime;
        }

        private static async Task CheckpointAsync(
            TableServiceHostRuntime runtime,
            string room,
            ulong atLeastVersion)
        {
            var registry = Assert.IsAssignableFrom<PostgresTableHostRegistry>(runtime.HostRegistry);
            Assert.True(registry.TryGet(room, out RoomHost? host), "the serving room must be registered");
            TableOperationResult result = await host!.Actor.SaveCheckpointAsync();
            Assert.True(result.Admitted, "the checkpoint must be admitted: " + result.ReasonCode);

            // The accepted API completes through the actor's own mailbox checkpoint path —
            // the durable row must exist by the time the call returns.
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < deadline)
            {
                long rows = await DurableTableHostEnvironment.CountAsync(
                    "SELECT count(*) FROM table_store.table_checkpoint " +
                    " WHERE room_id = @room AND state_version >= @version",
                    ("room", room),
                    ("version", (long)atLeastVersion));
                if (rows >= 1)
                {
                    return;
                }

                await Task.Delay(100);
            }

            throw new TimeoutException(
                "no checkpoint row at or above version " + atLeastVersion + " appeared within 10s");
        }

        private static async Task<TableTestClient> ConnectPlayerAsync(
            TableServiceHostRuntime runtime,
            TableTestSigner signer,
            string room,
            string account,
            int seat)
        {
            TableTestClient client = await TableTestClient.ConnectAsync(WsUrl(runtime, room));
            await client.AdmitAsync(signer.Sign(
                new TableTestTicket(account, room, TableAdmissionRole.Player, seat)));
            return client;
        }

        private static string WsUrl(TableServiceHostRuntime runtime, string room)
        {
            return "ws://127.0.0.1:" + runtime.GetBoundPort() + TableServiceWebHost.TablePath +
                   "?room=" + Uri.EscapeDataString(room);
        }

        /// <summary>
        /// The anchor probe source: seat 0 holds the claim-free honour hand starting at
        /// <paramref name="handStart"/> (handStart..handStart+12) and draws
        /// <paramref name="drawn"/>, which must lie OUTSIDE that hand. Successive hosts use
        /// DIFFERENT deals so the pre-fix fresh-shuffle anchor and the persisted truth are
        /// opposite values.
        /// </summary>
        private static ITableRoundSource AnchorRoundSource(int handStart, int drawn)
        {
            return new FixedRoundSource(roomId =>
            {
                var fixture = new N1aTable { Dealer = 0 };
                fixture.Hand(0, Enumerable.Range(handStart, 13).Select(t => new TileId(t)).ToArray());
                fixture.Drawn(0, new TileId(drawn));
                return fixture.Build(roomId + "-r1");
            });
        }

        /// <summary>
        /// Waits for the seat's real opening decision, discards exactly
        /// <paramref name="tile"/>, and returns the applied receipt identity plus the
        /// projected public fact and the room's full public history capture.
        /// </summary>
        private static async Task<DiscardFact> DiscardTileAsync(TableTestClient client, int tile)
        {
            Envelope decisionEnvelope = await client.ReadUntilAsync(
                message => message.PlayerUpdate?.Snapshot?.PrivateView?.Decision != null,
                TimeSpan.FromSeconds(30));
            Contracts.Wire.PlayerSnapshot snapshot = decisionEnvelope.PlayerUpdate!.Snapshot;
            Contracts.Wire.DecisionView decision = snapshot.PrivateView!.Decision!;
            Contracts.Wire.LegalActionView action = decision.Actions.FirstOrDefault(candidate =>
                candidate.ActionType == (uint)ProtocolActionType.DiscardTile &&
                candidate.TileIds.Contains((uint)tile))
                ?? throw new InvalidOperationException(
                    "the opening decision offers no discard of tile " + tile);

            string requestId = TableWire.RandomId("req");
            byte[] payload = TableWire.SubmitActionPayload(
                snapshot.RoundId,
                decision.DecisionId,
                decision.ControlEpoch,
                action.ActionType,
                action.TileIds.ToArray());
            await client.SendAsync(TableWire.Command(
                client.Welcome!,
                requestId,
                Contracts.CommandKind.SubmitAction,
                payload,
                expectedStateVersion: snapshot.StateVersion));
            Envelope receipt = await client.ReadUntilAsync(
                candidate => candidate.MessageType == MessageType.Receipt &&
                             candidate.RequestId == requestId,
                TimeSpan.FromSeconds(30));
            Assert.Equal(Contracts.Wire.ReceiptStatus.Applied, receipt.Receipt.Status);
            Assert.False(receipt.Receipt.Replayed);

            Envelope published = await client.ReadUntilAsync(
                message => message.PlayerUpdate?.Snapshot?.PublicView?.Board.Players[0].Discards.Count > 0,
                TimeSpan.FromSeconds(30));
            Contracts.Wire.PlayerSnapshot publishedSnapshot = published.PlayerUpdate!.Snapshot;
            Mahjong.Contracts.Wire.DiscardView discard = publishedSnapshot.PublicView!.Board.Players[0].Discards[0];
            Assert.Equal((uint)tile, discard.TileId);
            return new DiscardFact(
                requestId,
                client.Welcome!.Scope.ScopeId,
                receipt.Receipt.CommandVersion,
                discard,
                PublicHistoryCapture(publishedSnapshot));
        }

        /// <summary>Reads the opening discard's public fact from a (recovered) room.</summary>
        private static async Task<DiscardFact> ReadOpeningDiscardAsync(TableTestClient client)
        {
            Envelope published = await client.ReadUntilAsync(
                message => message.PlayerUpdate?.Snapshot?.PublicView?.Board.Players[0].Discards.Count > 0,
                TimeSpan.FromSeconds(30));
            Contracts.Wire.PlayerSnapshot snapshot = published.PlayerUpdate!.Snapshot;
            return new DiscardFact(
                string.Empty,
                string.Empty,
                0UL,
                snapshot.PublicView!.Board.Players[0].Discards[0],
                PublicHistoryCapture(snapshot));
        }
    }
}
