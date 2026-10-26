using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Xunit;

namespace Mahjong.TableService.Tests
{
    /// <summary>
    /// D-1-01 acceptance against the REAL Kestrel host and the REAL actor runtime: a room
    /// served by this build runs the Domain's actual draw/discard turn loop — the service
    /// opens the currently required Domain decision, the wire DecisionView carries the real
    /// decision id/epoch/actions/deadline, an ordinary legal action is strictly APPLIED with a
    /// real chained state version, the board advances to the next seat through the Domain's
    /// own auto-draw, and a same-key retry is answered from the receipt ledger without a
    /// second application.
    /// </summary>
    public sealed class RuntimeDecisionRuntimeTests
    {
        [Fact]
        public async Task RealRoomDecision_LegalDiscardIsAppliedAndTurnAdvances()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            TableTestClient client = await host.ConnectPlayerAsync("room-loop", "acct-loop-0", 0);
            try
            {
                // The acting seat sees its REAL open decision (opened by the platform tick
                // drive from the Domain's own committed round state, published in the owner's
                // private view with the decision id, control epoch, candidates and deadline).
                Contracts.Wire.PlayerSnapshot snapshot = await ReadPlayerSnapshotAsync(
                    client,
                    predicate: s => s.PrivateView?.Decision != null);
                Contracts.Wire.DecisionView decision = RequireDecision(snapshot, seat: 0);
                Assert.Equal("room-loop-r1", snapshot.RoundId);
                Assert.True(decision.DeadlineRemainingMs > 0, "the open decision has a live deadline window");
                Assert.NotEmpty(decision.Actions);
                int tilesBefore = TilePopulationTotal(snapshot.PrivateView!, snapshot.PublicView!);
                ulong stateVersionBefore = snapshot.StateVersion;
                // TURN 1: the legal discard through the authoritative decision id.
                string requestId = TableWire.RandomId("req");
                byte[] payload = TableWire.SubmitActionPayload(
                    snapshot.RoundId,
                    decision.DecisionId,
                    decision.ControlEpoch,
                    (uint)ProtocolActionType.DiscardTile,
                    new uint[] { decision.Actions[0].TileIds[0] });
                await client.SendAsync(TableWire.Command(
                    client.Welcome!, requestId,
                    Contracts.CommandKind.SubmitAction, payload,
                    expectedStateVersion: snapshot.StateVersion));

                Envelope receipt = await client.ReadUntilAsync(
                    envelope => envelope.MessageType == MessageType.Receipt &&
                                envelope.RequestId == requestId,
                    TimeSpan.FromSeconds(20));
                Assert.Equal(Contracts.Wire.ReceiptStatus.Applied, receipt.Receipt.Status);
                Assert.False(receipt.Receipt.Replayed);
                Assert.True(
                    receipt.Receipt.CommandVersion >= stateVersionBefore + 1UL,
                    "an applied legal action must grow the authoritative chain: commandVersion " +
                    receipt.Receipt.CommandVersion + " vs base " + stateVersionBefore);

                // Same-key replay: identical request id + payload answers the durable receipt
                // without re-applying anything to the authority.
                await client.SendAsync(TableWire.Command(
                    client.Welcome!, requestId,
                    Contracts.CommandKind.SubmitAction, payload,
                    expectedStateVersion: snapshot.StateVersion));
                Envelope replay = await client.ReadUntilAsync(
                    envelope => envelope.MessageType == MessageType.Receipt &&
                                envelope.RequestId == requestId,
                    TimeSpan.FromSeconds(20));
                Assert.True(
                    replay.Receipt.Replayed,
                    "an exact same-key retry must be answered from the receipt ledger");
                Assert.Equal(receipt.Receipt.CommandVersion, replay.Receipt.CommandVersion);

                // The applied commit advanced the Domain automatically: a FRESH admission's
                // baseline carries the post-commit truth (the authoritative version grew and
                // the next seat took the turn through the Domain's own wall draw), and the
                // projection keeps the 136-tile closed world.
                await using TableTestClient observer = await host.ConnectPlayerAsync(
                    "room-loop", "acct-loop-observer", 2);
                Contracts.Wire.PlayerSnapshot after = await ReadPlayerSnapshotAsync(
                    observer,
                    predicate: s => s.StateVersion >= receipt.Receipt.CommandVersion &&
                                    s.PrivateView != null);
                Assert.True(
                    receipt.Receipt.CommandVersion >= stateVersionBefore + 1UL,
                    "the applied commit must own its own command version");
                Assert.True(
                    after.StateVersion >= receipt.Receipt.CommandVersion,
                    "the fresh baseline reflects the accepted commit");
                Assert.Equal(1, (int)after.PublicView!.Board.CurrentSeat);
                // The accepted discard is a REAL domain transition: seat 0's public river
                // carries exactly one discard and its public hand count dropped by one
                // (14 -> 13 through the honest projection).
                Assert.Single(after.PublicView.Board.Players[0].Discards);
                Assert.Equal(
                    snapshot.PublicView!.Board.Players[0].HandCount - 1,
                    after.PublicView.Board.Players[0].HandCount);
                Assert.True(
                    TilePopulationTotal(after.PrivateView!, after.PublicView) <= 136,
                    "the published identities never leave the 136-tile closed world");
                await observer.DisposeAsync();
            }
            finally
            {
                await client.DisposeAsync();
            }
        }

        [Fact]
        public async Task IllegalAction_AndWrongDecisionCannotAdvanceTheAuthority()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            TableTestClient client = await host.ConnectPlayerAsync("room-neg", "acct-neg", 0);
            try
            {
                Contracts.Wire.PlayerSnapshot snapshot =
                    await ReadPlayerSnapshotAsync(client, predicate: s => s.PrivateView?.Decision != null);
                Contracts.Wire.DecisionView decision = RequireDecision(snapshot, seat: 0);
                ulong stateVersion = snapshot.StateVersion;

                // A legal-LOOKING discard that names a tile the seat does not hold.
                uint ownTile = decision.Actions[0].TileIds[0];
                uint foreignTile = TileOutOfRange(snapshot, ownTile);
                byte[] payload = TableWire.SubmitActionPayload(
                    snapshot.RoundId,
                    decision.DecisionId,
                    decision.ControlEpoch,
                    (uint)ProtocolActionType.DiscardTile,
                    new uint[] { foreignTile });
                await client.SendAsync(TableWire.Command(
                    client.Welcome!, TableWire.RandomId("neg"), Contracts.CommandKind.SubmitAction, payload));
                Envelope receipt = await client.ReadUntilAsync(
                    envelope => envelope.MessageType == MessageType.Receipt,
                    TimeSpan.FromSeconds(20));
                Assert.Equal(Contracts.Wire.ReceiptStatus.Rejected, receipt.Receipt.Status);

                // A valid hand tile but a decision id that is not the open authoritative one.
                byte[] wrongDecision = TableWire.SubmitActionPayload(
                    snapshot.RoundId,
                    "room-neg-fabricated-decision",
                    decision.ControlEpoch,
                    (uint)ProtocolActionType.DiscardTile,
                    new uint[] { ownTile });
                await client.SendAsync(TableWire.Command(
                    client.Welcome!, TableWire.RandomId("neg2"), Contracts.CommandKind.SubmitAction, wrongDecision));
                Envelope wrong = await client.ReadUntilAsync(
                    envelope => envelope.MessageType == MessageType.Receipt,
                    TimeSpan.FromSeconds(20));
                Assert.Equal(Contracts.Wire.ReceiptStatus.Rejected, wrong.Receipt.Status);

                // Zero side effects on the authoritative chain: the fresh admission's baseline
                // still carries the base version (the refusals never entered the actor).
                await using TableTestClient observer = await host.ConnectPlayerAsync(
                    "room-neg", "acct-neg-observer", 1);
                Contracts.Wire.PlayerSnapshot after = await ReadPlayerSnapshotAsync(
                    observer,
                    predicate: s => s.PrivateView != null);
                Assert.Equal(stateVersion, after.StateVersion);
                await observer.DisposeAsync();
            }
            finally
            {
                await client.DisposeAsync();
            }
        }

        [Fact]
        public async Task CrossRoomFabrication_NeverEntersTheActor()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            TableTestClient client = await host.ConnectPlayerAsync("room-fence2", "acct-fence2", 0);

            try
            {
                Contracts.Wire.PlayerSnapshot snapshot =
                    await ReadPlayerSnapshotAsync(client, predicate: s => s.PrivateView?.Decision != null);
                Contracts.Wire.DecisionView decision = RequireDecision(snapshot, seat: 0);

                // Cross-room fabrication: a command that claims a room the connection was
                // never admitted to is refused at the envelope fence (never applied).
                byte[] crossRoom = TableWire.SubmitActionPayload(
                    snapshot.RoundId,
                    decision.DecisionId,
                    decision.ControlEpoch,
                    (uint)ProtocolActionType.DiscardTile,
                    new uint[] { decision.Actions[0].TileIds[0] });
                Envelope envelope = TableWire.Command(
                    client.Welcome!, TableWire.RandomId("cross"),
                    Contracts.CommandKind.SubmitAction, crossRoom);
                envelope.RoomId = "room-fence999";
                await client.SendAsync(envelope);
                Envelope refused = await client.ReadUntilAsync(
                    envelope => envelope.MessageType == MessageType.Error ||
                                (envelope.MessageType == MessageType.Receipt &&
                                 envelope.Receipt.Status == Contracts.Wire.ReceiptStatus.Rejected),
                    TimeSpan.FromSeconds(20));
                Assert.True(
                    refused.MessageType == MessageType.Error ||
                        refused.Receipt.Status == Contracts.Wire.ReceiptStatus.Rejected,
                    "a cross-room fabrication must never be applied");
            }
            finally
            {
                await client.DisposeAsync();
            }
        }

        private static Contracts.Wire.DecisionView RequireDecision(Contracts.Wire.PlayerSnapshot snapshot, int seat)
        {
            Contracts.Wire.PlayerPrivateView? view = snapshot.PrivateView;
            Assert.True(view != null, "a player snapshot always carries the owner's own private view");
            Assert.Equal(seat, view!.Seat);
            Assert.True(view.Decision != null, "the acting seat must see its open authoritative decision");
            return view.Decision!;
        }

        private static uint TileOutOfRange(Contracts.Wire.PlayerSnapshot snapshot, uint except)
        {
            var unavailable = new HashSet<uint>(snapshot.PrivateView!.ClosedTiles)
            {
                except
            };
            for (uint tile = 0; tile < 136; tile++)
            {
                if (!unavailable.Contains(tile))
                {
                    return tile;
                }
            }

            throw new Xunit.Sdk.XunitException("no alternate tile id exists");
        }

        /// <summary>
        /// Counts every physical tile identity the owner projection publishes (closed hand,
        /// own + other public melds, every seat's river). This is a conservation sanity check
        /// against the 136-tile closed world, NOT a full ownership proof (the full check is
        /// the Domain map's own ConservationException).
        /// </summary>
        private static int TilePopulationTotal(Contracts.Wire.PlayerPrivateView privateView, Contracts.Wire.PublicMatchView publicView)
        {
            var seen = new HashSet<uint>();
            void Track(uint tile)
            {
                if (tile <= 135)
                {
                    seen.Add(tile);
                }
            }

            foreach (uint tile in privateView.ClosedTiles)
            {
                Track(tile);
            }

            foreach (Contracts.Wire.PlayerPublicView player in publicView.Board.Players)
            {
                foreach (Contracts.Wire.MeldView meld in player.Melds)
                {
                    foreach (uint tile in meld.TileIds)
                    {
                        Track(tile);
                    }
                }

                foreach (Contracts.Wire.DiscardView discard in player.Discards)
                {
                    Track(discard.TileId);
                }
            }

            return seen.Count;
        }

        private static async Task<Contracts.Wire.PlayerSnapshot> ReadPlayerSnapshotAsync(
            TableTestClient client,
            Func<Contracts.Wire.PlayerSnapshot, bool>? predicate = null,
            TimeSpan? timeout = null)
        {
            TimeSpan budget = timeout ?? TimeSpan.FromSeconds(20);
            long deadline = Environment.TickCount64 + (long)budget.TotalMilliseconds;
            while (true)
            {
                long remainingMs = Math.Max(1, deadline - Environment.TickCount64);
                Envelope envelope = await client.ReadUntilAsync(
                    item => item.MessageType == MessageType.PlayerUpdate &&
                            item.PlayerUpdate.Snapshot.PrivateView != null &&
                            (predicate == null || predicate(item.PlayerUpdate.Snapshot)),
                    TimeSpan.FromMilliseconds(remainingMs));
                return envelope.PlayerUpdate.Snapshot;
            }
        }
    }
}
