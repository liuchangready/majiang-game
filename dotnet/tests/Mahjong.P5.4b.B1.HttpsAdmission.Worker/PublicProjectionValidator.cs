using System;
using System.Collections.Generic;
using System.Linq;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using WirePublicMatchView = Mahjong.Contracts.Wire.PublicMatchView;
using WirePublicBoardView = Mahjong.Contracts.Wire.PublicBoardView;
using WirePlayerPublicView = Mahjong.Contracts.Wire.PlayerPublicView;
using WireMeldView = Mahjong.Contracts.Wire.MeldView;
using WireConcealedMeldView = Mahjong.Contracts.Wire.ConcealedMeldView;
using WireDiscardView = Mahjong.Contracts.Wire.DiscardView;

namespace Mahjong.P5_4b.B1.HttpsAdmission.Worker
{
    /// <summary>
    /// Typed/descriptor public-projection privacy gate. It validates both the
    /// schema surface (field numbers) and the semantic public shape (closed-kan
    /// must be identity-free ConcealedMeldView, no private event visibility or
    /// foreign owner, matching session/room/role/seat). It never inspects or
    /// logs private tiles.
    /// </summary>
    public static class PublicProjectionValidator
    {
        public static bool Validate(
            string expectedSessionId,
            string expectedRoomId,
            string role,
            int seat,
            PlayerSnapshot? player,
            PublicSnapshot? publicSnapshot,
            TableVisibleEventBatch? events,
            out string? reason)
        {
            reason = null;
            if (publicSnapshot != null)
            {
                if (!FieldsAllowed(publicSnapshot, 1, 2, 3, 4, 5, 6, 7, 8) ||
                    !PublicMatchViewSafe(publicSnapshot.View) ||
                    !string.Equals(publicSnapshot.SessionId, expectedSessionId, StringComparison.Ordinal))
                {
                    reason = "public_snapshot_schema_or_session";
                    return false;
                }
            }

            if (player != null)
            {
                if (player.PrivateView == null ||
                    player.PrivateView.Seat != seat ||
                    !PublicMatchViewSafe(player.PublicView) ||
                    !string.Equals(player.SessionId, expectedSessionId, StringComparison.Ordinal))
                {
                    reason = "player_public_snapshot_schema_session_or_private_seat";
                    return false;
                }
            }

            if (events != null)
            {
                if (!string.IsNullOrEmpty(events.SessionId) &&
                    !string.Equals(events.SessionId, expectedSessionId, StringComparison.Ordinal))
                {
                    reason = "event_batch_session_mismatch";
                    return false;
                }

                if (!string.IsNullOrEmpty(events.RoomId) &&
                    !string.Equals(events.RoomId, expectedRoomId, StringComparison.Ordinal))
                {
                    reason = "event_batch_room_mismatch";
                    return false;
                }

                foreach (TableVisibleEvent visible in events.Events)
                {
                    if (visible.Visibility == ProtocolEventVisibility.Unspecified)
                    {
                        reason = "event_visibility_unspecified";
                        return false;
                    }

                    if (visible.IsPrivate)
                    {
                        if (!string.Equals(role, "PLAYER", StringComparison.Ordinal) ||
                            visible.OwnerSeat != seat)
                        {
                            reason = "private_event_not_owned_by_this_seat";
                            return false;
                        }
                    }
                    else if (visible.OwnerSeat != -1)
                    {
                        reason = "public_event_has_owner_seat";
                        return false;
                    }
                }
            }

            return true;
        }

        public static bool FieldsAllowed(IMessage message, params int[] allowed)
        {
            foreach (FieldDescriptor field in message.Descriptor.Fields.InFieldNumberOrder())
            {
                if (Array.IndexOf(allowed, field.FieldNumber) < 0)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool PublicMatchViewSafe(WirePublicMatchView? view)
        {
            if (view == null)
            {
                return true;
            }

            if (!FieldsAllowed(view, 1, 2, 3, 4, 5, 6) || !PublicBoardSafe(view.Board))
            {
                return false;
            }

            foreach (PublicEventEnvelope settlement in view.SettlementEvents)
            {
                if (settlement.Visibility == EventVisibility.Private ||
                    settlement.Visibility == EventVisibility.Unspecified)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool PublicBoardSafe(WirePublicBoardView? board)
        {
            if (board == null)
            {
                return true;
            }

            if (!FieldsAllowed(board, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13))
            {
                return false;
            }

            var seats = new HashSet<int>();
            foreach (WirePlayerPublicView player in board.Players)
            {
                if (!FieldsAllowed(player, 1, 2, 3, 4, 5, 13) ||
                    player.Seat < 0 || player.Seat > 3 || !seats.Add(player.Seat))
                {
                    return false;
                }

                foreach (WireMeldView meld in player.Melds)
                {
                    // A closed kan must be identity-free on the public stream.
                    if (!FieldsAllowed(meld, 1, 2, 3, 4) || meld.Type == MeldType.ClosedKan)
                    {
                        return false;
                    }
                }

                foreach (WireConcealedMeldView concealed in player.ConcealedMelds)
                {
                    if (!FieldsAllowed(concealed, 1, 2, 3) ||
                        concealed.Type != MeldType.ClosedKan ||
                        concealed.TileCount != 4 ||
                        concealed.FromSeat != -1)
                    {
                        return false;
                    }
                }

                foreach (WireDiscardView discard in player.Discards)
                {
                    if (!FieldsAllowed(discard, 1, 2, 3, 4))
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Sticky per-client privacy audit. Every actually received baseline/update
    /// and visible-event batch is observed immediately; a later safe batch can
    /// never erase an earlier violation.
    /// </summary>
    public sealed class PrivacyAuditState
    {
        private readonly object _gate = new object();
        private bool _violated;
        private string? _reason;
        private long _batches;

        public bool Violated
        {
            get
            {
                lock (_gate)
                {
                    return _violated;
                }
            }
        }

        public string? Reason
        {
            get
            {
                lock (_gate)
                {
                    return _reason;
                }
            }
        }

        public long BatchesObserved
        {
            get
            {
                lock (_gate)
                {
                    return _batches;
                }
            }
        }

        public void Observe(
            string expectedSessionId,
            string expectedRoomId,
            string role,
            int seat,
            PlayerSnapshot? player,
            PublicSnapshot? publicSnapshot,
            TableVisibleEventBatch? events)
        {
            bool safe = PublicProjectionValidator.Validate(
                expectedSessionId,
                expectedRoomId,
                role,
                seat,
                player,
                publicSnapshot,
                events,
                out string? reason);
            lock (_gate)
            {
                _batches++;
                if (!safe && !_violated)
                {
                    _violated = true;
                    _reason = reason;
                }
            }
        }
    }
}
