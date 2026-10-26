using System;
using System.Threading;
using System.Threading.Tasks;

namespace Mahjong.Client
{
    /// <summary>
    /// Everything a platform transport needs before the client sends HELLO.
    /// The ticket is short-lived and must never be written to logs or the
    /// persistent outbox.
    /// </summary>
    public sealed class TableChannelConnectRequest
    {
        public TableChannelConnectRequest(string ticket, string clientBuild, string? resumeSessionId)
        {
            if (ticket == null)
            {
                throw new ArgumentNullException(nameof(ticket));
            }

            if (clientBuild == null)
            {
                throw new ArgumentNullException(nameof(clientBuild));
            }

            Ticket = ticket;
            ClientBuild = clientBuild;
            ResumeSessionId = resumeSessionId;
        }

        public string Ticket { get; }

        public string ClientBuild { get; }

        public string? ResumeSessionId { get; }
    }

    /// <summary>
    /// Transport boundary used by <see cref="TableSession"/>.
    ///
    /// Implementations move opaque wire envelopes only. They must not decode
    /// business messages, reorder frames, retry commands, or persist receipts;
    /// those are session/state-machine responsibilities. The Unity main-thread
    /// dispatcher also belongs to the platform layer, not to this interface.
    /// </summary>
    public interface ITableChannel
    {
        bool IsOpen { get; }

        Task OpenAsync(TableChannelConnectRequest request, CancellationToken cancellationToken);

        Task SendAsync(ReadOnlyMemory<byte> wireEnvelope, CancellationToken cancellationToken);

        /// <summary>
        /// Returns exactly one wire envelope. A transport that receives stream
        /// fragments must reassemble them before returning.
        /// </summary>
        Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken);

        Task CloseAsync(TableChannelCloseReason reason, CancellationToken cancellationToken);
    }

    /// <summary>Stable client-visible reason for closing a channel.</summary>
    public sealed class TableChannelCloseReason
    {
        public TableChannelCloseReason(string code, bool permanent)
        {
            if (string.IsNullOrEmpty(code))
            {
                throw new ArgumentException("Close reason code must not be empty.", nameof(code));
            }

            Code = code;
            Permanent = permanent;
        }

        public string Code { get; }

        public bool Permanent { get; }
    }

    /// <summary>
    /// Transport-level close. It carries no business receipt and never proves
    /// whether an already-sent command committed.
    /// </summary>
    public sealed class TableChannelClosedException : Exception
    {
        public TableChannelClosedException(string code, bool permanent)
            : base("Table channel closed: " + code + (permanent ? " (permanent)" : " (transient)"))
        {
            Code = code ?? string.Empty;
            Permanent = permanent;
        }

        public TableChannelClosedException(string code, bool permanent, Exception inner)
            : base("Table channel closed: " + code + (permanent ? " (permanent)" : " (transient)"), inner)
        {
            Code = code ?? string.Empty;
            Permanent = permanent;
        }

        public string Code { get; }

        public bool Permanent { get; }
    }
}
