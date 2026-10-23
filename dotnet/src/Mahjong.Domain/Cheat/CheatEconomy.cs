using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Mahjong.Domain.Cheat
{
    /// <summary>
    /// Caller-supplied cheat economy configuration (V4 <c>CheatEconomyConfig</c>).
    ///
    /// Costs are keyed by the frozen rule constants. Sleeve bloom and beam swap costs are
    /// pinned to exactly 10 points at the transition layer (RULE-CONTRACT §7, D-08); the
    /// tile mark cost is configurable but must be positive. An unconfigured cost makes the
    /// corresponding trick unavailable rather than free.
    /// </summary>
    public sealed class CheatEconomyConfig
    {
        public const string TileMarkCostKey = "TILE_MARK";
        public const string SleeveBloomCostKey = "SLEEVE_BLOOM";
        public const string BeamSwapCostKey = "BEAM_SWAP";

        private readonly ReadOnlyDictionary<string, int> _costs;

        public CheatEconomyConfig(bool enabled, int initialPoints, IReadOnlyDictionary<string, int> costs)
        {
            if (initialPoints < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(initialPoints), initialPoints, "Initial points must be non-negative.");
            }

            if (costs == null)
            {
                throw new ArgumentNullException(nameof(costs));
            }

            var copy = new Dictionary<string, int>(costs.Count, StringComparer.Ordinal);
            foreach (KeyValuePair<string, int> pair in costs)
            {
                if (string.IsNullOrWhiteSpace(pair.Key))
                {
                    throw new ArgumentException("Cost keys cannot be blank.", nameof(costs));
                }

                if (pair.Value < 0)
                {
                    throw new ArgumentException("Cost '" + pair.Key + "' must be non-negative.", nameof(costs));
                }

                copy.Add(pair.Key, pair.Value);
            }

            Enabled = enabled;
            InitialPoints = initialPoints;
            _costs = new ReadOnlyDictionary<string, int>(copy);
        }

        public bool Enabled { get; }

        public int InitialPoints { get; }

        public IReadOnlyDictionary<string, int> Costs => _costs;

        public bool TryGetCost(string key, out int cost) => _costs.TryGetValue(key, out cost);
    }
}
