using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Mahjong.LobbyService
{
    /// <summary>
    /// Durable lobby state. Implementations must make the receipt, room/match
    /// assignment, seat reservation and ticket-consumption changes atomic in a
    /// single database transaction. The in-memory implementation exists for
    /// contract tests only; it is not persistence evidence.
    /// </summary>
    public interface ILobbyStore : IAsyncDisposable
    {
        string ProviderName { get; }

        Task InitializeAsync(CancellationToken cancellationToken);

        Task<bool> CheckReadyAsync(CancellationToken cancellationToken);

        Task<LobbySessionInfo?> FindSessionByTokenHashAsync(string tokenHash, CancellationToken cancellationToken);

        Task<LobbyAccount?> FindAccountAsync(string accountId, CancellationToken cancellationToken);

        Task<LobbyRoomPage> ListRoomsAsync(string? cursor, int limit, DateTimeOffset now, CancellationToken cancellationToken);

        Task<LobbyRoomSnapshot?> FindRoomSnapshotAsync(string roomId, DateTimeOffset now, CancellationToken cancellationToken);

        Task<LobbyAssignment?> FindAssignmentAsync(string assignmentId, CancellationToken cancellationToken);

        Task<LobbyMatchTicket?> FindMatchTicketAsync(string matchTicketId, CancellationToken cancellationToken);

        Task<LobbyResponse> ExecuteIdempotentAsync(
            LobbyIdempotencyKey key,
            string payloadDigest,
            DateTimeOffset now,
            Func<ILobbyTransaction, CancellationToken, Task<LobbyResponse>> operation,
            CancellationToken cancellationToken);

        Task<T> ExecuteAsync<T>(
            DateTimeOffset now,
            Func<ILobbyTransaction, CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken);
    }

    public interface ILobbyTransaction
    {
        /// <summary>
        /// Serializes lobby placement decisions for one account across room and
        /// match tables (for example join-vs-queue double booking).
        /// </summary>
        Task LockAccountAsync(string accountId, CancellationToken cancellationToken);

        Task<LobbyAccount?> FindAccountByBootstrapAsync(
            string deviceDigest,
            string bootstrapHash,
            CancellationToken cancellationToken);

        Task<LobbyAccount?> FindAccountAsync(string accountId, CancellationToken cancellationToken);

        Task InsertAccountAsync(
            LobbyAccount account,
            string deviceDigest,
            string bootstrapHash,
            CancellationToken cancellationToken);

        Task InsertSessionAsync(LobbySession session, CancellationToken cancellationToken);

        Task<LobbyRoom?> FindRoomForUpdateAsync(string roomId, CancellationToken cancellationToken);

        Task InsertRoomAsync(LobbyRoom room, CancellationToken cancellationToken);

        Task UpdateRoomAsync(LobbyRoom room, CancellationToken cancellationToken);

        Task<int> CancelActiveAssignmentsForRoomAsync(
            string roomId,
            DateTimeOffset now,
            CancellationToken cancellationToken);

        Task<LobbyAssignment?> FindAssignmentForUpdateAsync(string assignmentId, CancellationToken cancellationToken);

        Task<LobbyAssignment?> FindAnyActiveAssignmentForAccountAsync(string accountId, CancellationToken cancellationToken);

        Task<IReadOnlyList<LobbyAssignment>> ListActiveAssignmentsForAccountAsync(
            string accountId,
            CancellationToken cancellationToken);

        Task<IReadOnlyList<LobbyAssignment>> ListAssignmentsAsync(string roomId, CancellationToken cancellationToken);

        Task<int> CountActiveAssignmentsAsync(string roomId, LobbyRole role, CancellationToken cancellationToken);

        Task InsertAssignmentAsync(LobbyAssignment assignment, CancellationToken cancellationToken);

        Task UpdateAssignmentAsync(LobbyAssignment assignment, CancellationToken cancellationToken);

        Task<LobbyMatchTicket?> FindMatchTicketAsync(string matchTicketId, CancellationToken cancellationToken);

        Task<LobbyMatchTicket?> FindMatchTicketForUpdateAsync(string matchTicketId, CancellationToken cancellationToken);

        Task<LobbyMatchTicket?> FindActiveMatchTicketForAccountAsync(string accountId, CancellationToken cancellationToken);

        Task<IReadOnlyList<LobbyMatchTicket>> ListQueuedMatchTicketsForUpdateAsync(
            LobbyMatchMode mode,
            uint rulesetVersion,
            string? region,
            int limit,
            CancellationToken cancellationToken);

        Task InsertMatchTicketAsync(LobbyMatchTicket ticket, CancellationToken cancellationToken);

        Task UpdateMatchTicketAsync(LobbyMatchTicket ticket, CancellationToken cancellationToken);

        Task<LobbyTableTicketRecord?> FindTableTicketForUpdateAsync(string jti, CancellationToken cancellationToken);

        Task<LobbyTableTicketRecord?> FindActiveTableTicketForAssignmentAsync(string assignmentId, CancellationToken cancellationToken);

        Task InsertTableTicketAsync(LobbyTableTicketRecord ticket, CancellationToken cancellationToken);

        Task UpdateTableTicketAsync(LobbyTableTicketRecord ticket, CancellationToken cancellationToken);

        Task InsertTicketConsumptionAsync(LobbyTicketConsumption consumption, CancellationToken cancellationToken);

        Task<int> ExpireDueAsync(DateTimeOffset now, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Read-only public room projection owned by TableService. The lobby must
    /// never read table_event, player snapshots, hands, seeds or cheat state.
    /// Missing projections are allowed: the directory is eventually consistent.
    /// </summary>
    public interface IRoomProjectionReader
    {
        Task<PublicRoomProjectionBatch> ReadAsync(
            IReadOnlyList<string> roomIds,
            CancellationToken cancellationToken);
    }

    public sealed class EmptyRoomProjectionReader : IRoomProjectionReader
    {
        public Task<PublicRoomProjectionBatch> ReadAsync(
            IReadOnlyList<string> roomIds,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(PublicRoomProjectionBatch.Empty);
        }
    }
}
