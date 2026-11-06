#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;

namespace Mahjong.Unity.UI.Session
{
    /// <summary>
    /// Command sink interface for Table UI components.
    /// Encapsulates command submission without exposing Domain, TableRuntime,
    /// LocalHost or internal session implementations to the presentation layer.
    /// </summary>
    public interface ITableCommandSink
    {
        bool CanSubmit { get; }
        long Generation { get; }
        Task<CommandSubmission> SubmitAsync(ProtocolCommand command, CancellationToken cancellationToken);
        event EventHandler<TableReceiptResolvedEventArgs>? ReceiptResolved;
    }
}
