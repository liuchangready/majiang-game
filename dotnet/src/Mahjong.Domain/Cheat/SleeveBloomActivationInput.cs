using System;

namespace Mahjong.Domain.Cheat
{
    /// <summary>
    /// Immutable normal-wall evidence frozen for one immediate sleeve bloom activation.
    /// This is the typed input contract that closes the V4 <c>ActivationInput.nextWallTileId</c>
    /// / <c>nextWallTileInWall</c> projection: a caller can replay the exact candidate the
    /// authoritative decision surface observed, instead of letting the domain silently search
    /// the live wall again and pick a different tile.
    ///
    /// The type deliberately distinguishes two states that a nullable argument cannot express:
    /// <see cref="Unspecified"/> means "legacy caller did not provide frozen evidence" and keeps
    /// the map-only control (legal alternate candidates stay Allowed); <see cref="Frozen"/>
    /// means "this exact candidate was observed" and must be replayed, including an explicit
    /// <c>null</c> or an already-drifted candidate, which must reject rather than re-search.
    ///
    /// The domain never trusts the two frozen booleans on their own. <see cref="NextWallTileInWall"/>
    /// must still agree with the authoritative map; a caller that claims <c>true</c> for a tile
    /// that is not in the live wall is rejected, and a caller that claims <c>false</c> for a tile
    /// that is still in the wall is rejected too (the decision surface already saw it leave).
    /// </summary>
    public sealed class SleeveBloomActivationInput
    {
        private readonly bool _provided;
        private readonly TileId? _nextWallTileId;
        private readonly bool _nextWallTileInWall;

        private SleeveBloomActivationInput(bool provided, TileId? nextWallTileId, bool nextWallTileInWall)
        {
            _provided = provided;
            _nextWallTileId = nextWallTileId;
            _nextWallTileInWall = nextWallTileInWall;
        }

        /// <summary>
        /// Legacy map-only activation: no frozen normal-wall evidence was observed. The domain
        /// derives the second candidate from the live map exactly as before. This is not the same
        /// as <see cref="Frozen"/> with a null id.
        /// </summary>
        public static SleeveBloomActivationInput Unspecified { get; } =
            new SleeveBloomActivationInput(false, null, false);

        /// <summary>
        /// Frozen normal-wall evidence. <paramref name="nextWallTileId"/> may be null and
        /// <paramref name="nextWallTileInWall"/> may be false; both are authoritative observations
        /// and a missing/drifted candidate must reject with no automatic substitute.
        /// </summary>
        public static SleeveBloomActivationInput Frozen(TileId? nextWallTileId, bool nextWallTileInWall) =>
            new SleeveBloomActivationInput(true, nextWallTileId, nextWallTileInWall);

        /// <summary>
        /// Builds frozen evidence from the authoritative read-only round map and cheat state.
        /// The candidate is the first available live-wall slot (V4 <c>nextAvailableWallTile()</c>:
        /// scan from slot 0, skip tiles whose physical location is not WALL, skip off-turn offer
        /// and reservation tiles). An absent candidate is represented as a frozen null, not as
        /// <see cref="Unspecified"/>, so a later domain call cannot fall back to a re-search.
        /// </summary>
        public static SleeveBloomActivationInput FromAuthoritativeState(
            TilePositionMap map,
            SleeveBloomState sleeve)
        {
            if (map == null) throw new ArgumentNullException(nameof(map));
            if (sleeve == null) throw new ArgumentNullException(nameof(sleeve));

            return TryResolveFirstAvailableWallCandidate(map, sleeve, out TileId candidate, out _)
                ? Frozen(candidate, true)
                : Frozen(null, false);
        }

        /// <summary>
        /// Resolves V4's next available live-wall tile. Scans from slot 0 (not from the pointer) so
        /// unlocked holes before the pointer re-enter the draw queue, rejects tiles whose physical
        /// location left the wall, and skips protected off-turn offer/reservation tiles.
        /// </summary>
        public static bool TryResolveFirstAvailableWallCandidate(
            TilePositionMap map,
            SleeveBloomState sleeve,
            out TileId candidate,
            out int wallIndex)
        {
            if (map == null) throw new ArgumentNullException(nameof(map));
            if (sleeve == null) throw new ArgumentNullException(nameof(sleeve));

            for (int i = 0; i < map.WallEndExclusive; i++)
            {
                if (!map.TryGetTileAt(TilePosition.Wall(i), out TileId tile))
                {
                    continue;
                }

                if (map.PositionOf(tile).Zone != TileZone.Wall)
                {
                    continue;
                }

                if (sleeve.IsProtectedTile(tile))
                {
                    continue;
                }

                candidate = tile;
                wallIndex = i;
                return true;
            }

            candidate = default;
            wallIndex = -1;
            return false;
        }

        /// <summary>True when the caller supplied frozen evidence (including a frozen null).</summary>
        public bool IsFrozen => _provided;

        /// <summary>The observed next-wall tile, or null when the candidate was already absent.</summary>
        public TileId? NextWallTileId => _nextWallTileId;

        /// <summary>The observed in-wall flag; never sufficient on its own to allow activation.</summary>
        public bool NextWallTileInWall => _nextWallTileInWall;
    }
}
