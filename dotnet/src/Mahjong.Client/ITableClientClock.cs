using System;
using System.Diagnostics;

namespace Mahjong.Client
{
    /// <summary>
    /// Clock port for client-side liveness decisions. Heartbeat/timer code must
    /// consume this instead of reading the system clock directly so tests can
    /// drive time deterministically.
    /// </summary>
    public interface ITableClientClock
    {
        /// <summary>Monotonic milliseconds; never moves backwards within a process.</summary>
        long MonotonicMilliseconds { get; }

        DateTimeOffset UtcNow { get; }
    }

    /// <summary>Default clock: <see cref="Stopwatch"/> monotonic time plus UTC wall clock.</summary>
    public sealed class SystemTableClientClock : ITableClientClock
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

        public long MonotonicMilliseconds => _stopwatch.ElapsedMilliseconds;

        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
