using System;
using System.Collections.Generic;

namespace Mahjong.LobbyService
{
    /// <summary>
    /// P5.3 lobby vocabulary. These are lobby-owned read models, not table
    /// domain objects: they contain no hands, wall order, seeds, private cheat
    /// state, or table owner epochs.
    /// </summary>
    public enum LobbyRole
    {
        Unspecified = 0,
        Player = 1,
        Spectator = 2
    }

    public enum LobbyRoomVisibility
    {
        Unspecified = 0,
        Public = 1,
        Private = 2
    }

    /// <summary>Persisted room lifecycle. FULL/IN_GAME are derived public views.</summary>
    public enum LobbyRoomState
    {
        Unspecified = 0,
        Open = 1,
        Closed = 2
    }

    public enum LobbyAssignmentStatus
    {
        Unspecified = 0,
        Reserved = 1,
        Confirmed = 2,
        Cancelled = 3,
        Expired = 4
    }

    public enum LobbyMatchMode
    {
        Unspecified = 0,
        Casual = 1,
        Ranked = 2
    }

    public enum LobbyMatchStatus
    {
        Unspecified = 0,
        Queued = 1,
        Reserved = 2,
        Confirmed = 3,
        Cancelled = 4,
        Expired = 5
    }

    public sealed record LobbyAccount(
        string AccountId,
        bool IsGuest,
        string DisplayName,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt);

    public sealed record LobbySession(
        string TokenHash,
        string AccountId,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt,
        DateTimeOffset? RevokedAt);

    public sealed record LobbySessionInfo(LobbySession Session, LobbyAccount Account);

    public sealed record LobbyRoom(
        string RoomId,
        string Title,
        LobbyRoomVisibility Visibility,
        LobbyRoomState State,
        bool AllowSpectators,
        int MaxPlayers,
        uint RulesetVersion,
        string HostAccountId,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        DateTimeOffset? ClosedAt);

    public sealed record LobbyAssignment(
        string AssignmentId,
        string RoomId,
        string AccountId,
        LobbyRole Role,
        int? SeatId,
        LobbyAssignmentStatus Status,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        DateTimeOffset? ExpiresAt,
        DateTimeOffset? ConfirmedAt,
        DateTimeOffset? TableConfirmedAt,
        string? MatchTicketId);

    public sealed record LobbyMatchTicket(
        string MatchTicketId,
        string AccountId,
        LobbyMatchMode Mode,
        LobbyMatchStatus Status,
        uint RulesetVersion,
        string Region,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt,
        DateTimeOffset? ReservedAt,
        DateTimeOffset? ConfirmedAt,
        DateTimeOffset? CancelledAt,
        string? AssignmentId,
        string? RoomId);

    public sealed record LobbyTableTicketRecord(
        string Jti,
        string AssignmentId,
        string AccountId,
        string RoomId,
        LobbyRole Role,
        int? SeatId,
        DateTimeOffset IssuedAt,
        DateTimeOffset ExpiresAt,
        string Kid,
        DateTimeOffset? ConsumedAt,
        DateTimeOffset? SupersededAt);

    public sealed record LobbyTicketConsumption(
        string Jti,
        string AssignmentId,
        string AccountId,
        string RoomId,
        int? SeatId,
        DateTimeOffset ConsumedAt);

    public sealed record LobbyReceiptRecord(
        string Principal,
        string RequestId,
        string PayloadDigest,
        string Status,
        int HttpStatus,
        string ResultBody,
        DateTimeOffset CreatedAt,
        DateTimeOffset? CompletedAt);

    public sealed record LobbyIdempotencyKey(string Principal, string RequestId);

    /// <summary>A serialized lobby response, persisted verbatim for exact replay.</summary>
    public sealed record LobbyResponse(int StatusCode, string Json, bool Replayed = false);

    public sealed record LobbyRoomSnapshot(LobbyRoom Room, int SeatsTaken, int SpectatorCount);

    public sealed record LobbyRoomPage(IReadOnlyList<LobbyRoomSnapshot> Items, string? NextCursor);

    /// <summary>
    /// The only table-owned data the lobby may read. This is a deliberately
    /// small public projection; no private event payload is reachable here.
    /// </summary>
    public sealed record PublicRoomProjection(
        string RoomId,
        string Lifecycle,
        int SeatsOccupied,
        int SpectatorCount,
        ulong PublicVersion,
        uint RulesetVersion,
        DateTimeOffset UpdatedAt);

    public sealed record PublicRoomProjectionBatch(IReadOnlyList<PublicRoomProjection> Items)
    {
        public static PublicRoomProjectionBatch Empty { get; } =
            new PublicRoomProjectionBatch(Array.Empty<PublicRoomProjection>());
    }

    public sealed record GuestSessionCommand(string DeviceId, string DisplayName);

    public sealed record CreateRoomCommand(
        string Title,
        int MaxPlayers,
        LobbyRoomVisibility Visibility,
        bool AllowSpectators);

    public sealed record JoinRoomCommand(int? PreferSeat);

    public sealed record SpectateRoomCommand;

    public sealed record CreateMatchCommand(LobbyMatchMode Mode, uint RulesetVersion, string Region);

    public sealed record IssueTableTicketCommand(string AssignmentId);

    public sealed record TableTicketClaims(
        string Issuer,
        string AccountId,
        string Audience,
        string RoomId,
        string AssignmentId,
        LobbyRole Role,
        int? SeatId,
        string Jti,
        DateTimeOffset IssuedAt,
        DateTimeOffset ExpiresAt,
        uint ProtocolMajor,
        uint RulesetVersion,
        uint ProjectionVersion);

    public sealed record TableTicketExpectation(
        string RoomId,
        string AssignmentId,
        LobbyRole Role,
        int? SeatId,
        string? AccountId);

    public sealed record IssuedTableTicket(
        string Ticket,
        string Jti,
        string Kid,
        string AssignmentId,
        string RoomId,
        LobbyRole Role,
        int? SeatId,
        DateTimeOffset IssuedAt,
        DateTimeOffset ExpiresAt,
        uint ProtocolMajor,
        uint RulesetVersion,
        uint ProjectionVersion);

    public sealed record TicketConsumptionResult(
        string Jti,
        string AccountId,
        string RoomId,
        string AssignmentId,
        LobbyRole Role,
        int? SeatId,
        string? MatchTicketId,
        DateTimeOffset ConsumedAt);

    public sealed class LobbyException : Exception
    {
        public LobbyException(int statusCode, string code, string message) : base(message)
        {
            StatusCode = statusCode;
            Code = code;
        }

        public int StatusCode { get; }

        public string Code { get; }
    }

    public sealed class LobbyOptionsException : Exception
    {
        public LobbyOptionsException(string message) : base(message)
        {
        }
    }
}
