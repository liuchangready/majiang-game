using System;
using System.Collections.Generic;

namespace Mahjong.TableRuntime
{
    internal enum TableMailboxEnqueueStatus : byte
    {
        Enqueued = 0,
        Full = 1,
        Closed = 2
    }

    internal abstract class TableMailboxMessage
    {
    }

    /// <summary>
    /// Bounded FIFO mailbox for one table. There is no consumer thread inside the mailbox: the
    /// actor schedules a short drain task on the shared thread pool and only one drain task can
    /// run at a time (<c>TableActor._pumpRunning</c>). Locks protect only queue metadata; no lock
    /// is ever held across an await.
    ///
    /// Business messages (client commands/AI results) use the business capacity. Authority
    /// control messages use a small additional reserve so a full business queue cannot drop a
    /// deadline tick, lease-loss, recovery, close marker or snapshot read. The total is still
    /// bounded by business capacity + control reserve.
    /// </summary>
    internal sealed class TableMailbox
    {
        private readonly object _gate = new object();
        private readonly Queue<TableMailboxMessage> _queue = new Queue<TableMailboxMessage>();
        private readonly int _businessCapacity;
        private readonly int _controlReserve;
        private bool _accepting = true;

        public TableMailbox(int businessCapacity, int controlReserve)
        {
            if (businessCapacity < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(businessCapacity), businessCapacity, "Capacity must be positive.");
            }

            if (controlReserve < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(controlReserve), controlReserve, "Control reserve must be positive.");
            }

            _businessCapacity = businessCapacity;
            _controlReserve = controlReserve;
        }

        public TableMailboxEnqueueStatus TryEnqueue(TableMailboxMessage message)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            lock (_gate)
            {
                if (!_accepting)
                {
                    return TableMailboxEnqueueStatus.Closed;
                }

                if (_queue.Count >= _businessCapacity)
                {
                    return TableMailboxEnqueueStatus.Full;
                }

                _queue.Enqueue(message);
                return TableMailboxEnqueueStatus.Enqueued;
            }
        }

        public TableMailboxEnqueueStatus TryEnqueueControl(TableMailboxMessage message)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            lock (_gate)
            {
                if (!_accepting)
                {
                    return TableMailboxEnqueueStatus.Closed;
                }

                if (_queue.Count >= _businessCapacity + _controlReserve)
                {
                    return TableMailboxEnqueueStatus.Full;
                }

                _queue.Enqueue(message);
                return TableMailboxEnqueueStatus.Enqueued;
            }
        }

        /// <summary>
        /// Atomically stops accepting business/control messages and appends the close marker.
        /// Because the flag and the enqueue happen under the same lock, every previously
        /// admitted message is ahead of the close marker and no new message can follow it.
        /// </summary>
        public TableMailboxEnqueueStatus TryEnqueueClose(TableMailboxMessage message)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            lock (_gate)
            {
                if (!_accepting)
                {
                    return TableMailboxEnqueueStatus.Closed;
                }

                _accepting = false;
                _queue.Enqueue(message);
                return TableMailboxEnqueueStatus.Enqueued;
            }
        }

        public bool TryDequeue(out TableMailboxMessage message)
        {
            lock (_gate)
            {
                if (_queue.Count > 0)
                {
                    message = _queue.Dequeue();
                    return true;
                }
            }

            message = null!;
            return false;
        }

        public void StopAccepting()
        {
            lock (_gate)
            {
                _accepting = false;
            }
        }

        public bool HasItems
        {
            get
            {
                lock (_gate)
                {
                    return _queue.Count > 0;
                }
            }
        }

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _queue.Count;
                }
            }
        }
    }
}
