using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;

namespace Mahjong.Unity.Tests.EditMode
{
    public sealed class ScriptedTableChannel : ITableChannel, IDisposable
    {
        private readonly Queue<ReadOnlyMemory<byte>> _inbound = new Queue<ReadOnlyMemory<byte>>();
        private readonly List<byte[]> _outbound = new List<byte[]>();
        private bool _isOpen;
        private bool _isDisposed;

        public bool IsOpen => _isOpen;
        public bool IsDisposed => _isDisposed;
        public IReadOnlyList<byte[]> OutboundMessages => _outbound;

        public void EnqueueInbound(byte[] bytes)
        {
            _inbound.Enqueue(bytes);
        }

        public Task OpenAsync(TableChannelConnectRequest request, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled(cancellationToken);
            }
            _isOpen = true;
            return Task.CompletedTask;
        }

        public Task SendAsync(ReadOnlyMemory<byte> wireEnvelope, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled(cancellationToken);
            }
            _outbound.Add(wireEnvelope.ToArray());
            return Task.CompletedTask;
        }

        public Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled<ReadOnlyMemory<byte>>(cancellationToken);
            }

            if (_inbound.Count > 0)
            {
                return Task.FromResult(_inbound.Dequeue());
            }

            // Return empty or wait
            var tcs = new TaskCompletionSource<ReadOnlyMemory<byte>>();
            cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            return tcs.Task;
        }

        public Task CloseAsync(TableChannelCloseReason reason, CancellationToken cancellationToken)
        {
            _isOpen = false;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _isDisposed = true;
            _isOpen = false;
        }
    }
}
