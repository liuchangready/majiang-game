using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Mahjong.Unity.App
{
    public sealed class MainThreadDispatcher
    {
        public const int DefaultCapacity = 1024;
        public const int MaxDispatchPerFrame = 128;
        public const float MaxDispatchMsPerFrame = 4.0f;

        private struct QueuedAction
        {
            public long Generation;
            public Action Action;
        }

        private readonly Queue<QueuedAction> _queue = new Queue<QueuedAction>();
        private readonly object _gate = new object();
        private readonly int _capacity;
        private long _currentGeneration;
        private bool _hasOverflowed;
        private long _droppedOldGenerationCount;
        private long _handlerExceptionCount;
        private readonly Stopwatch _stopwatch = new Stopwatch();

        public event Action? Overflowed;

        public MainThreadDispatcher(int capacity = DefaultCapacity)
        {
            _capacity = capacity;
        }

        public long CurrentGeneration
        {
            get
            {
                lock (_gate) return _currentGeneration;
            }
            set
            {
                lock (_gate) _currentGeneration = value;
            }
        }

        public int PendingCount
        {
            get
            {
                lock (_gate) return _queue.Count;
            }
        }

        public bool HasOverflowed
        {
            get
            {
                lock (_gate) return _hasOverflowed;
            }
        }

        public long DroppedOldGenerationCount
        {
            get
            {
                lock (_gate) return _droppedOldGenerationCount;
            }
        }

        public long HandlerExceptionCount
        {
            get
            {
                lock (_gate) return _handlerExceptionCount;
            }
        }

        public bool Post(Action action, long generation = 0)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            lock (_gate)
            {
                if (_hasOverflowed)
                {
                    return false;
                }

                if (_queue.Count >= _capacity)
                {
                    _hasOverflowed = true;
                    Debug.LogError($"UI_DISPATCH_OVERFLOW: Dispatcher exceeded capacity {_capacity}.");
                    Overflowed?.Invoke();
                    return false;
                }

                _queue.Enqueue(new QueuedAction
                {
                    Generation = generation,
                    Action = action
                });
                return true;
            }
        }

        public int Update(int maxBatch = MaxDispatchPerFrame, float maxMs = MaxDispatchMsPerFrame)
        {
            _stopwatch.Restart();
            int processed = 0;

            while (processed < maxBatch && _stopwatch.Elapsed.TotalMilliseconds < maxMs)
            {
                QueuedAction item;
                lock (_gate)
                {
                    if (_queue.Count == 0) break;
                    item = _queue.Dequeue();
                }

                long curGen;
                lock (_gate) curGen = _currentGeneration;

                // Check generation: if item.Generation != 0 and != curGen, drop
                if (item.Generation != 0 && item.Generation != curGen)
                {
                    lock (_gate) _droppedOldGenerationCount++;
                    continue;
                }

                try
                {
                    item.Action.Invoke();
                }
                catch (Exception ex)
                {
                    lock (_gate) _handlerExceptionCount++;
                    Debug.LogError($"UI_DISPATCH_HANDLER_EXCEPTION: {ex}");
                }

                processed++;
            }

            _stopwatch.Stop();
            return processed;
        }

        public void Clear()
        {
            lock (_gate)
            {
                _queue.Clear();
                _hasOverflowed = false;
            }
        }
    }
}
