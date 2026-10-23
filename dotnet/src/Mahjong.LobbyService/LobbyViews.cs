using System;
using System.Collections.Generic;

namespace Mahjong.LobbyService
{
    public sealed record LobbyCapabilitiesView(
        string AuthMode,
        bool GuestOnly,
        bool RankedMatchAllowed,
        bool SpectateAllowed,
        string MatchRule,
        int SpectatorDelaySeconds,
        string ProductDecision,
        string DataRetention,
        IReadOnlyList<string> LaunchBlocked);

    public sealed record GuestSessionView(
        string AccountId,
        bool Guest,
        string DisplayName,
        string SessionToken,
        DateTimeOffset ExpiresAt,
        LobbyCapabilitiesView Capabilities);

    public sealed record CurrentSessionView(
        string AccountId,
        bool Guest,
        string DisplayName,
        DateTimeOffset AccountExpiresAt,
        DateTimeOffset SessionExpiresAt,
        LobbyCapabilitiesView Capabilities);

    public sealed record RoomSummaryView(
        string RoomId,
        string Title,
        string Visibility,
        string State,
        bool AllowSpectators,
        int MaxPlayers,
        int SeatsTaken,
        int SeatsAvailable,
        int SpectatorCount,
        uint RulesetVersion,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        bool ProjectionStale);

    public sealed record RoomCatalogView(IReadOnlyList<RoomSummaryView> Items, string? NextCursor);

    public sealed record AssignmentView(
        string AssignmentId,
        string RoomId,
        string Role,
        int? SeatId,
        string Status,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        DateTimeOffset? ExpiresAt,
        DateTimeOffset? ConfirmedAt);

    public sealed record CreateRoomView(RoomSummaryView Room, AssignmentView Assignment);

    public sealed record JoinRoomView(AssignmentView Assignment);

    public sealed record MatchTicketView(
        string MatchTicketId,
        string Mode,
        string Status,
        uint RulesetVersion,
        string Region,
        string? RoomId,
        string? AssignmentId,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt,
        DateTimeOffset? ReservedAt,
        DateTimeOffset? ConfirmedAt,
        DateTimeOffset? CancelledAt);

    public sealed record TableTicketView(
        string Ticket,
        string Jti,
        string Kid,
        string RoomId,
        string AssignmentId,
        string Role,
        int? SeatId,
        DateTimeOffset IssuedAt,
        DateTimeOffset ExpiresAt,
        uint ProtocolMajor,
        uint RulesetVersion,
        uint ProjectionVersion);

    public sealed record LobbyErrorView(string Code, string Message);

    public sealed record HealthView(string Status, string Storage, bool Ready);
}
