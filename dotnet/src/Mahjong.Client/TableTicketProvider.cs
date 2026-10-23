using System;
using System.Threading;
using System.Threading.Tasks;

namespace Mahjong.Client
{
    /// <summary>
    /// Input for a ticket request. A ticket is short-lived and may be
    /// single-use; the runner asks the provider on every connection attempt and
    /// never caches or reuses a previous value.
    /// </summary>
    public sealed class TableTicketRequest
    {
        public TableTicketRequest(int attempt, string? resumeSessionId, string? lastFailureCode)
        {
            if (attempt <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(attempt), attempt, "Attempt numbers start at 1.");
            }

            Attempt = attempt;
            ResumeSessionId = resumeSessionId;
            LastFailureCode = lastFailureCode;
        }

        /// <summary>1-based connection attempt counter for this run.</summary>
        public int Attempt { get; }

        public string? ResumeSessionId { get; }

        public string? LastFailureCode { get; }
    }

    /// <summary>
    /// Obtains a currently applicable table ticket. Implementations may call a
    /// lobby service, refresh an expiring ticket or fail permanently when the
    /// account/seat is no longer entitled. The returned value must not be
    /// logged or persisted by the caller.
    /// </summary>
    public interface ITableTicketProvider
    {
        Task<string> GetTicketAsync(TableTicketRequest request, CancellationToken cancellationToken);
    }

    /// <summary>Typed ticket failure so the runner can distinguish retry from stop.</summary>
    public sealed class TableTicketException : Exception
    {
        public TableTicketException(string code, bool permanent, string message)
            : base(message)
        {
            Code = code ?? string.Empty;
            Permanent = permanent;
        }

        public string Code { get; }

        public bool Permanent { get; }
    }

    /// <summary>Convenience adapter for tests and simple hosts.</summary>
    public sealed class DelegateTableTicketProvider : ITableTicketProvider
    {
        private readonly Func<TableTicketRequest, CancellationToken, Task<string>> _getTicket;

        public DelegateTableTicketProvider(Func<TableTicketRequest, CancellationToken, Task<string>> getTicket)
        {
            _getTicket = getTicket ?? throw new ArgumentNullException(nameof(getTicket));
        }

        public Task<string> GetTicketAsync(TableTicketRequest request, CancellationToken cancellationToken)
        {
            return _getTicket(request, cancellationToken);
        }
    }
}
