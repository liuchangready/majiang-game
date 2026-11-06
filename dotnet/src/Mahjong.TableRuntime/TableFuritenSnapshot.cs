using System;
using System.Collections.Generic;

namespace Mahjong.TableRuntime
{
    /// <summary>
    /// Immutable per-round furiten facts owned by the actor. A snapshot is advanced only while
    /// the concrete command is being decided on the actor mailbox and is installed only after
    /// its durable commit succeeds, so it can be encoded into the same projection-facts payload
    /// as the state/timing/receipt transaction. Temporary and riichi furiten are kept apart:
    /// a draw clears only the temporary bit while riichi furiten survives for the round.
    /// </summary>
    public sealed class TableFuritenSnapshot
    {
        private const int AllSeatsMask = 0x0F;

        public TableFuritenSnapshot(string roundId, int temporaryMask, int riichiMask)
        {
            if (string.IsNullOrWhiteSpace(roundId))
            {
                throw new ArgumentException("RoundId cannot be blank.", nameof(roundId));
            }

            if ((temporaryMask & ~AllSeatsMask) != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(temporaryMask), temporaryMask, "Temporary furiten mask is outside four seats.");
            }

            if ((riichiMask & ~AllSeatsMask) != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(riichiMask), riichiMask, "Riichi furiten mask is outside four seats.");
            }

            RoundId = roundId;
            TemporaryMask = temporaryMask;
            RiichiMask = riichiMask;
        }

        public string RoundId { get; }

        public int TemporaryMask { get; }

        public int RiichiMask { get; }

        public bool IsTemporary(int seat)
        {
            CheckSeat(seat);
            return (TemporaryMask & (1 << seat)) != 0;
        }

        public bool IsRiichi(int seat)
        {
            CheckSeat(seat);
            return (RiichiMask & (1 << seat)) != 0;
        }

        public bool IsFuriten(int seat)
        {
            return IsTemporary(seat) || IsRiichi(seat);
        }

        public static TableFuritenSnapshot Empty(string roundId)
        {
            return new TableFuritenSnapshot(roundId, 0, 0);
        }

        public static TableFuritenSnapshot FromSeats(
            string roundId,
            IReadOnlyList<bool>? temporary,
            IReadOnlyList<bool>? riichi)
        {
            if (temporary != null && temporary.Count != 4)
            {
                throw new ArgumentException("Temporary furiten must have four seats.", nameof(temporary));
            }

            if (riichi != null && riichi.Count != 4)
            {
                throw new ArgumentException("Riichi furiten must have four seats.", nameof(riichi));
            }

            int temporaryMask = 0;
            int riichiMask = 0;
            for (int seat = 0; seat < 4; seat++)
            {
                if (temporary != null && temporary[seat])
                {
                    temporaryMask |= 1 << seat;
                }

                if (riichi != null && riichi[seat])
                {
                    riichiMask |= 1 << seat;
                }
            }

            return new TableFuritenSnapshot(roundId, temporaryMask, riichiMask);
        }

        internal TableFuritenSnapshot WithTemporary(int seat, bool value)
        {
            CheckSeat(seat);
            int mask = value ? TemporaryMask | (1 << seat) : TemporaryMask & ~(1 << seat);
            return new TableFuritenSnapshot(RoundId, mask, RiichiMask);
        }

        internal TableFuritenSnapshot WithRiichi(int seat, bool value)
        {
            CheckSeat(seat);
            int mask = value ? RiichiMask | (1 << seat) : RiichiMask & ~(1 << seat);
            return new TableFuritenSnapshot(RoundId, TemporaryMask, mask);
        }

        private static void CheckSeat(int seat)
        {
            if (seat < 0 || seat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(seat), seat, "Seat must be 0..3.");
            }
        }
    }
}
