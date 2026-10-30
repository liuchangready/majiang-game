#nullable enable
using Mahjong.Unity.Table;

namespace Mahjong.Unity.App
{
    /// <summary>
    /// Explicit action-semantics classification for the local smoke.
    ///
    /// An empty accepted publication is not, by itself, evidence that an action
    /// is allowed to be invisible. Only a response Pass intentionally declines
    /// a response without creating a protocol-visible fact; window internals are
    /// not protocol events. Every other submittable action has a required
    /// visible fact:
    ///   DiscardTile          -> TileDiscarded
    ///   DrawTile             -> TileDrawn (owner-private)
    ///   DeclareChi           -> ChiDeclared
    ///   DeclarePon           -> PonDeclared
    ///   DeclareKan           -> KanDeclared
    ///   DeclareRiichi        -> RiichiDeclared / TileDiscarded
    ///   DeclareTsumo         -> TsumoWin
    ///   DeclareRon           -> RonWin
    ///   DeclareAbortiveDraw  -> DrawGame
    ///   DeclareAgariYame     -> no round event is assumed here; it is not
    ///                           treated as a legal invisible action.
    ///
    /// If one of those actions is Applied with an empty publication, the smoke
    /// must fail with the observed batch/receipt chain. It must not skip the
    /// action and let a later discard turn the whole run into success.
    /// </summary>
    public static class SmokeActionEventPolicy
    {
        public static bool AllowsEmptyPublication(PlayerActionKind action)
        {
            // The only verified legal state-only response action in this path.
            // A Pass closes/skips this seat's response; it does not publish a
            // round event of its own.
            return action == PlayerActionKind.Pass;
        }

        public static string DescribeRequirement(PlayerActionKind action)
        {
            switch (action)
            {
                case PlayerActionKind.Pass:
                    return "state_only_response_pass";
                case PlayerActionKind.DiscardTile:
                    return "requires: TileDiscarded";
                case PlayerActionKind.DrawTile:
                    return "requires: TileDrawn";
                case PlayerActionKind.DeclareChi:
                    return "requires: ChiDeclared";
                case PlayerActionKind.DeclarePon:
                    return "requires: PonDeclared";
                case PlayerActionKind.DeclareKan:
                    return "requires: KanDeclared";
                case PlayerActionKind.DeclareRiichi:
                    return "requires: RiichiDeclared/TileDiscarded";
                case PlayerActionKind.DeclareTsumo:
                    return "requires: TsumoWin";
                case PlayerActionKind.DeclareRon:
                    return "requires: RonWin";
                case PlayerActionKind.DeclareAbortiveDraw:
                    return "requires: DrawGame";
                default:
                    return "not_a_legal_invisible_action";
            }
        }
    }
}
