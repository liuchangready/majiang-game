using System;
using Xunit;

namespace Mahjong.LocalHost.Tests
{
    public sealed class LocalTicketTests
    {
        [Fact]
        public void TicketIsSingleUseAndRoomBound()
        {
            string ticket = LocalTicketIssuer.Issue("room-a");
            Assert.True(LocalTicketIssuer.TryConsume(ticket, "room-a"));
            Assert.False(LocalTicketIssuer.TryConsume(ticket, "room-a"));

            string other = LocalTicketIssuer.Issue("room-b");
            Assert.False(LocalTicketIssuer.TryConsume(other, "room-a"));
            Assert.False(LocalTicketIssuer.TryConsume(null, "room-a"));
        }
    }
}
