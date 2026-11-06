using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Xunit;

namespace Mahjong.TableService.Tests
{
    /// <summary>
    /// Four seats plus a spectator on one real table: each player stream carries exactly its
    /// own private view and the spectator stream carries public data only. The public stream
    /// is projected server-side (never serialized-whole-then-filtered), so a spectator
    /// connection must never even transport a PLAYER_UPDATE message.
    /// </summary>
    public sealed class StreamPrivacyTests
    {
        [Fact]
        public async Task FourSeatsPlusSpectator_ReceiveAtomicBaselines()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            string room = "room-priv";

            var players = new List<TableTestClient>();
            try
            {
                for (int seat = 0; seat < 4; seat++)
                {
                    players.Add(await host.ConnectPlayerAsync(room, "acct-seat-" + seat, seat));
                }

                TableTestClient spectator = await host.ConnectSpectatorAsync(room, "watcher-priv");

                ulong playerBaselineStateVersion = 0;
                for (int seat = 0; seat < 4; seat++)
                {
                    Welcome welcome = players[seat].Welcome!;
                    Assert.Equal(seat, welcome.SeatId);

                    // The subscription baseline is atomic: the first stream frame is a full
                    // snapshot at the WELCOME baseline state version.
                    Envelope update = await players[seat].ReadUntilAsync(
                        envelope => envelope.MessageType == MessageType.PlayerUpdate &&
                                    envelope.PlayerUpdate.IsFullSnapshot,
                        TimeSpan.FromSeconds(20));
                    PlayerSnapshot snapshot = update.PlayerUpdate.Snapshot;
                    Assert.NotNull(snapshot.PrivateView);
                    Assert.Equal(seat, snapshot.PrivateView.Seat);
                    Assert.Equal(welcome.Baseline.StateVersion, update.PlayerUpdate.Snapshot.StateVersion);
                    Assert.True(
                        snapshot.PrivateView.ClosedTiles.Count >= 1,
                        "a dealt hand must be private (seat " + seat + " got " +
                        snapshot.PrivateView.ClosedTiles.Count + " closed tiles at state version " +
                        snapshot.StateVersion + ")");
                    playerBaselineStateVersion = snapshot.StateVersion;
                }

                Envelope publicBaseline = await spectator.ReadUntilAsync(
                    envelope => envelope.MessageType == MessageType.PublicUpdate &&
                                envelope.PublicUpdate.IsFullSnapshot,
                    TimeSpan.FromSeconds(20));
                Assert.NotNull(publicBaseline.PublicUpdate.Snapshot.View);
                // Every audience projects the same authoritative state: the spectator's
                // baseline version must equal the players' (zero here — no command has
                // committed on this fresh table).
                Assert.Equal(
                    playerBaselineStateVersion,
                    publicBaseline.PublicUpdate.Snapshot.StateVersion);
            }
            finally
            {
                foreach (TableTestClient player in players)
                {
                    await player.DisposeAsync();
                }
            }
        }

        [Fact]
        public async Task SpectatorStream_NeverCarriesPrivateData()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            string room = "room-nopriv";

            var players = new List<TableTestClient>();
            TableTestClient spectator = await host.ConnectSpectatorAsync(room, "watcher-scan");
            try
            {
                // The spectator's own public baseline arrives first.
                Envelope baseline = await spectator.ReadUntilAsync(
                    envelope => envelope.MessageType == MessageType.PublicUpdate &&
                                envelope.PublicUpdate.IsFullSnapshot,
                    TimeSpan.FromSeconds(20));
                Assert.NotNull(baseline.PublicUpdate.Snapshot.View);

                for (int seat = 0; seat < 4; seat++)
                {
                    players.Add(await host.ConnectPlayerAsync(room, "acct-np-" + seat, seat));
                }

                // Drain everything the spectator connection transports for a window while
                // the players join. Whatever arrives must be public projection data only:
                // a PLAYER_UPDATE on this connection would already be a privacy breach.
                var spectatorFrames = new List<Envelope> { baseline };
                long deadline = Environment.TickCount64 + 2000;
                while (true)
                {
                    long remaining = deadline - Environment.TickCount64;
                    if (remaining <= 0)
                    {
                        break;
                    }

                    try
                    {
                        Envelope frame = await spectator.ReadUntilAsync(
                            envelope => envelope.MessageType is MessageType.PublicUpdate or MessageType.PlayerUpdate,
                            TimeSpan.FromMilliseconds(remaining));
                        spectatorFrames.Add(frame);
                    }
                    catch (OperationCanceledException)
                    {
                        // The total sampling deadline elapsed with no further frame. A read
                        // timeout only ends this wait; the spectator socket must still be
                        // open (asserted below) and a later frame must remain readable.
                        break;
                    }
                }

                Assert.Equal(System.Net.WebSockets.WebSocketState.Open, spectator.State);
                Assert.True(spectatorFrames.Count >= 1);
                foreach (Envelope frame in spectatorFrames)
                {
                    Assert.Equal(MessageType.PublicUpdate, frame.MessageType);
                    // The public snapshot type has no private_view field at all (the frozen
                    // proto reserves those names); a parsed PublicUpdate cannot carry hands.
                    Assert.NotNull(frame.PublicUpdate.Snapshot.View);
                }
            }
            finally
            {
                foreach (TableTestClient player in players)
                {
                    await player.DisposeAsync();
                }

                await spectator.DisposeAsync();
            }
        }

        [Fact]
        public async Task PlayerStreamCarriesOnlyOwnSeatPrivateView()
        {
            await using TableServiceTestHost host = await TableServiceTestHost.StartAsync();
            string room = "room-ownseat";

            TableTestClient seat0 = await host.ConnectPlayerAsync(room, "acct-os-0", 0);
            TableTestClient seat1 = await host.ConnectPlayerAsync(room, "acct-os-1", 1);
            try
            {
                Envelope update0 = await seat0.ReadUntilAsync(
                    envelope => envelope.MessageType == MessageType.PlayerUpdate &&
                                envelope.PlayerUpdate.IsFullSnapshot,
                    TimeSpan.FromSeconds(20));
                Envelope update1 = await seat1.ReadUntilAsync(
                    envelope => envelope.MessageType == MessageType.PlayerUpdate &&
                                envelope.PlayerUpdate.IsFullSnapshot,
                    TimeSpan.FromSeconds(20));

                Assert.Equal(0, update0.PlayerUpdate.Snapshot.PrivateView.Seat);
                Assert.Equal(1, update1.PlayerUpdate.Snapshot.PrivateView.Seat);

                // Each seat sees its own 13 dealt tiles; the private views are per-connection
                // projections of the same authoritative state version.
                Assert.Equal(
                    update0.PlayerUpdate.Snapshot.StateVersion,
                    update1.PlayerUpdate.Snapshot.StateVersion);
                Assert.NotEqual(
                    update0.PlayerUpdate.Snapshot.PrivateView.ClosedTiles,
                    update1.PlayerUpdate.Snapshot.PrivateView.ClosedTiles);
            }
            finally
            {
                await seat0.DisposeAsync();
                await seat1.DisposeAsync();
            }
        }
    }
}
