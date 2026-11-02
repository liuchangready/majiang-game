using System;
using System.Collections.Generic;

namespace Mahjong.LocalHost
{
    public sealed class LocalTableSeatPlan
    {
        private readonly int[] _humanSeats;
        private static readonly LocalTableSeatPlan Single = new LocalTableSeatPlan(new[] { 0 });
        private static readonly LocalTableSeatPlan All = new LocalTableSeatPlan(new[] { 0, 1, 2, 3 });

        private LocalTableSeatPlan(int[] seats)
        {
            _humanSeats = seats;
        }

        public static LocalTableSeatPlan SingleHumanSeatZero => Single;

        public static LocalTableSeatPlan AllFourHumanSeats => All;

        public IReadOnlyList<int> HumanSeats => Array.AsReadOnly(_humanSeats);

        public static LocalTableSeatPlan FromHumanSeats(IReadOnlyList<int> humanSeats)
        {
            if (humanSeats == null) throw new ArgumentNullException(nameof(humanSeats));
            var copy = new int[humanSeats.Count];
            var seen = new bool[4];
            for (int i = 0; i < humanSeats.Count; i++)
            {
                int seat = humanSeats[i];
                if (seat < 0 || seat > 3 || seen[seat])
                    throw new LocalTableException(LocalTableErrorKind.InvalidOptions, "Human seats must be unique values from 0 through 3.");
                seen[seat] = true;
                copy[i] = seat;
            }
            Array.Sort(copy);
            return new LocalTableSeatPlan(copy);
        }

        internal bool Contains(int seat)
        {
            for (int i = 0; i < _humanSeats.Length; i++) if (_humanSeats[i] == seat) return true;
            return false;
        }
    }
}
