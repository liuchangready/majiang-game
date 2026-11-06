using System;

namespace Mahjong.LocalHost
{
    public enum LocalViewerRole : byte
    {
        Player = 0,
        Spectator = 1
    }

    internal enum LocalTicketState : byte
    {
        Issued = 0,
        Reserved = 1,
        Attached = 2,
        Detached = 3,
        Failed = 4,
        Expired = 5
    }

    public sealed class LocalTableConnectionTicket
    {
        internal LocalTableConnectionTicket(string value, LocalViewerRole role, int seatId, string accountId)
        {
            Value = value;
            Role = role;
            SeatId = seatId;
            AccountId = accountId;
        }

        public string Value { get; }
        public LocalViewerRole Role { get; }
        public int SeatId { get; }
        public string AccountId { get; }
    }
}
