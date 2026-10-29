using System;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Contracts;
using Mahjong.TableRuntime;

namespace Mahjong.TableService
{
    /// <summary>
    /// Narrow host-side scope write/read port (P5.4b-A). Admission, failed-admission
    /// rollback and receipt reconciliation authorize through THIS port only — never through
    /// a concrete in-memory authority — so a durable host issues and revokes its request
    /// scopes in the SAME database that stores the events and receipts those scopes
    /// authorize. There is no memory shadow acting as the sole authorization source in
    /// persistent mode: the durable commit path re-reads the scope row inside its own
    /// store transaction (<see cref="ITableTransactionalScopeReader"/>), and this port's
    /// writes own the rows it reads.
    /// <para>
    /// <see cref="IRequestScopeAuthority.LookupAsync"/> is the frozen actor-shared read;
    /// the two write methods carry a full pre-built <see cref="TableScopeRecord"/> (the
    /// caller owns scope-id generation) so the PostgreSQL backend can insert the exact
    /// record it will later serve.
    /// </para>
    /// </summary>
    public interface ITableHostScopeAuthority : IRequestScopeAuthority
    {
        /// <summary>Inserts the scope row; a duplicate scope id fails the call (never overwrites).</summary>
        Task<TableScopeRecord> IssueScopeAsync(
            TableScopeRecord scope,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Marks the scope revoked at the given instant. Unknown ids return false; an
        /// already-revoked scope keeps its FIRST revocation instant (COALESCE semantics).
        /// </summary>
        Task<bool> RevokeScopeAsync(
            RequestScopeId scopeId,
            ulong revokedAtUtcMs,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Memory-mode adapter over the accepted P3.4 in-memory authority. The wrapped
    /// instance stays the durable context's scope port so the in-process actor keeps its
    /// exact accepted behavior; this adapter only widens the host-side writes to the port.
    /// </summary>
    public sealed class InMemoryTableHostScopeAuthority : ITableHostScopeAuthority
    {
        private readonly InMemoryRequestScopeAuthority _inner;

        public InMemoryTableHostScopeAuthority(InMemoryRequestScopeAuthority inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public InMemoryRequestScopeAuthority Inner => _inner;

        public Task<TableScopeRecord> IssueScopeAsync(
            TableScopeRecord scope,
            CancellationToken cancellationToken = default)
        {
            if (scope == null)
            {
                throw new ArgumentNullException(nameof(scope));
            }

            TableScopeRecord issued = _inner.Issue(
                scope.AccountId,
                scope.Role,
                scope.RoomId,
                scope.IssuedAtUtcMs,
                scope.ExpiresAtUtcMs,
                scope.ScopeId.Value);
            return Task.FromResult(issued);
        }

        public Task<bool> RevokeScopeAsync(
            RequestScopeId scopeId,
            ulong revokedAtUtcMs,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_inner.Revoke(scopeId, revokedAtUtcMs));
        }

        public ValueTask<TableScopeLookup> LookupAsync(
            RequestScopeId scopeId,
            CancellationToken cancellationToken)
        {
            return _inner.LookupAsync(scopeId, cancellationToken);
        }
    }

    /// <summary>
    /// Durable adapter over the P5.2 PostgreSQL table store. Every scope write lands in
    /// the <c>table_store.request_scope</c> row that the durable commit path and the
    /// receipt reconciliation re-read — the same database, the same row, no shadow copy.
    /// </summary>
    public sealed class PostgresTableHostScopeAuthority : ITableHostScopeAuthority
    {
        private readonly Infrastructure.PostgresTableStore _store;

        public PostgresTableHostScopeAuthority(Infrastructure.PostgresTableStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        public Task<TableScopeRecord> IssueScopeAsync(
            TableScopeRecord scope,
            CancellationToken cancellationToken = default)
        {
            return _store.IssueScopeAsync(scope, cancellationToken);
        }

        public Task<bool> RevokeScopeAsync(
            RequestScopeId scopeId,
            ulong revokedAtUtcMs,
            CancellationToken cancellationToken = default)
        {
            return _store.RevokeScopeAsync(scopeId, revokedAtUtcMs, cancellationToken);
        }

        public ValueTask<TableScopeLookup> LookupAsync(
            RequestScopeId scopeId,
            CancellationToken cancellationToken)
        {
            return _store.LookupAsync(scopeId, cancellationToken);
        }
    }
}
