#nullable enable
using Mahjong.Contracts;
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
    ///   DeclareRiichi        -> RiichiDeclared / TileDiscarded with riichi flag
    ///   DeclareTsumo         -> TsumoWin
    ///   DeclareRon           -> RonWin
    ///   DeclareAbortiveDraw  -> DrawGame (table-level settlement)
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

        /// <summary>
        /// True only for an event that is the selected action's own required
        /// visible fact, under the acting seat. A matching command_version alone
        /// is never enough: another event type, another seat, or a window
        /// settlement carried by the same command must not confirm this action.
        /// </summary>
        public static bool IsRequiredEvent(PlayerActionKind action, int seat, ObservedVisibleEvent ev)
        {
            if (ev == null) return false;
            switch (action)
            {
                case PlayerActionKind.DiscardTile:
                    return ev.Type == ProtocolEventType.TileDiscarded && ev.Seat == seat;
                case PlayerActionKind.DrawTile:
                    return ev.Type == ProtocolEventType.TileDrawn && ev.Seat == seat;
                case PlayerActionKind.DeclareChi:
                    return ev.Type == ProtocolEventType.ChiDeclared && ev.Seat == seat;
                case PlayerActionKind.DeclarePon:
                    return ev.Type == ProtocolEventType.PonDeclared && ev.Seat == seat;
                case PlayerActionKind.DeclareKan:
                    return ev.Type == ProtocolEventType.KanDeclared && ev.Seat == seat;
                case PlayerActionKind.DeclareRiichi:
                    return (ev.Type == ProtocolEventType.RiichiDeclared && ev.Seat == seat) ||
                           (ev.Type == ProtocolEventType.TileDiscarded &&
                            ev.Seat == seat &&
                            ev.Payload.RiichiDiscard);
                case PlayerActionKind.DeclareTsumo:
                    return ev.Type == ProtocolEventType.TsumoWin && ev.Seat == seat;
                case PlayerActionKind.DeclareRon:
                    return ev.Type == ProtocolEventType.RonWin && ev.Seat == seat;
                case PlayerActionKind.DeclareAbortiveDraw:
                    // DrawGame is a table-level settlement fact. It is not owned
                    // by a single seat; accept the table sentinel or this seat,
                    // but never another player as the acting seat.
                    return ev.Type == ProtocolEventType.DrawGame &&
                           (ev.Seat == seat || ev.Seat == ProtocolConstants.NoSeat);
                case PlayerActionKind.Pass:
                    // A Pass has no own playable event. Window/settlement events
                    // that happen to share the command_version are not ours.
                    return false;
                default:
                    return false;
            }
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
