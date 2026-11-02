using System;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Xunit;

namespace Mahjong.LocalHost.Tests
{
    public sealed class C04PrivacyStreamMatrixTests
    {
        [Fact]
        public async Task Public_and_player_snapshots_are_typed_and_seat_owned_on_real_channels()
        {
            await using var harness = new P41Harness();
            harness.Options.SeatPlan = LocalTableSeatPlan.AllFourHumanSeats;
            await using LocalTableRoom room = await LocalTableHost.OpenRoomAsync(harness.Options, TestContext.Current.CancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var tickets = new LocalTableConnectionTicket[4];
            var channels = new ITableChannel[4];
            var sessions = new TableSession[4];
            try
            {
                for (int seat = 0; seat < 4; seat++)
                {
                    tickets[seat] = room.CreatePlayerTicket(seat);
                    channels[seat] = await room.ConnectAsync(tickets[seat], timeout.Token);
                    sessions[seat] = new TableSession(channels[seat], new InMemoryOutboxStore());
                    await sessions[seat].OpenAsync(new TableSessionOpenRequest(tickets[seat].Value, "privacy-seat-" + seat), timeout.Token);
                }
                foreach (TableSession session in sessions)
                {
                    for (int i = 0; i < 16 && session.State != TableConnectionState.Ready; i++)
                        await session.PumpOnceAsync(timeout.Token);
                    Assert.Equal(TableConnectionState.Ready, session.State);
                }
                for (int owner = 0; owner < 4; owner++)
                {
                    var snapshot = Assert.IsType<Mahjong.Contracts.Wire.PlayerSnapshot>(sessions[owner].PlayerSnapshot);
                    Assert.Equal(owner, snapshot.PrivateView.Seat);
                    Assert.Equal(owner, sessions[owner].Binding!.SeatId);
                    Assert.NotEmpty(snapshot.PrivateView.ClosedTiles);
                    Assert.InRange(snapshot.PrivateView.ClosedTiles.Count + (snapshot.PrivateView.HasDrawnTileId ? 1 : 0), 13, 14);
                    for (int foreign = 0; foreign < 4; foreign++)
                        if (foreign != owner) Assert.NotEqual(foreign, snapshot.PrivateView.Seat);
                }
                LocalTableConnectionTicket spectatorTicket = room.CreateSpectatorTicket();
                ITableChannel spectatorChannel = await room.ConnectAsync(spectatorTicket, timeout.Token);
                var spectator = new TableSession(spectatorChannel, new InMemoryOutboxStore());
                await spectator.OpenAsync(new TableSessionOpenRequest(spectatorTicket.Value, "privacy-spectator"), timeout.Token);
                for (int i = 0; i < 16 && spectator.State != TableConnectionState.Ready; i++)
                    await spectator.PumpOnceAsync(timeout.Token);
                Assert.Equal(TableConnectionState.Ready, spectator.State);
                Assert.Null(spectator.PlayerSnapshot);
                var publicSnapshot = Assert.IsType<Mahjong.Contracts.Wire.PublicSnapshot>(spectator.PublicSnapshot);
                Assert.NotEmpty(publicSnapshot.RoundId);
                Assert.NotNull(publicSnapshot.View);
                await spectator.CloseAsync("TEST_DONE", CancellationToken.None);
                await spectatorChannel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
            }
            finally
            {
                for (int i = 0; i < sessions.Length; i++)
                {
                    if (sessions[i] != null) try { await sessions[i].CloseAsync("TEST_DONE", CancellationToken.None); } catch { }
                    if (channels[i] != null) try { await channels[i].CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None); } catch { }
                }
            }
        }
    }
}
