using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.TableRuntime;

namespace Mahjong.LocalHost
{
    public enum LocalTableErrorKind
    {
        Unknown = 0,
        Unavailable = 1,
        LeaseConflict = 2,
        CorruptState = 3,
        InvalidOptions = 4,
        ChannelClosed = 5
    }

    public class LocalTableException : Exception
    {
        public LocalTableException(LocalTableErrorKind kind, string message) : base(message) => ErrorKind = kind;

        public LocalTableException(LocalTableErrorKind kind, string message, Exception inner) : base(message, inner) =>
            ErrorKind = kind;

        public LocalTableErrorKind ErrorKind { get; }
    }

    /// <summary>
    /// Minimal local host configuration. The Unity App injects only the persistent absolute
    /// directory string; this assembly never calls a Unity API.
    /// </summary>
    public sealed class LocalTableHostOptions
    {
        public string SaveDirectory { get; set; } = string.Empty;

        public string RoomId { get; set; } = string.Empty;

        public int RulesetVersion { get; set; } = 1;

        public string OwnerInstanceId { get; set; } = Guid.NewGuid().ToString("N");

        public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(10);

        public TimeSpan AutoFlushInterval { get; set; } = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// Optional injected clock/random/id source. Unity/offline hosts must provide their own
        /// deterministic source when replaying; the default reads the OS clock and a local RNG.
        /// </summary>
        public LocalHostEnvironment? Environment { get; set; }

        /// <summary>Test seam only: replaces the file primitives. Production leaves this null.</summary>
        internal ILocalFileSystem? FileSystemForTest { get; set; }

        internal void Validate()
        {
            if (string.IsNullOrWhiteSpace(SaveDirectory))
            {
                throw new LocalTableException(LocalTableErrorKind.InvalidOptions, "SaveDirectory must not be empty.");
            }

            if (!IsSafeRoomId(RoomId))
            {
                throw new LocalTableException(
                    LocalTableErrorKind.InvalidOptions,
                    "RoomId must be 1..64 characters of letters, digits, '-', '_' or '.' and must not be '.' or '..'.");
            }

            if (RulesetVersion < 1)
            {
                throw new LocalTableException(LocalTableErrorKind.InvalidOptions, "RulesetVersion must be positive.");
            }

            if (string.IsNullOrWhiteSpace(OwnerInstanceId) || OwnerInstanceId.Length > 128)
            {
                throw new LocalTableException(
                    LocalTableErrorKind.InvalidOptions,
                    "OwnerInstanceId must be 1..128 non-empty characters.");
            }

            if (LeaseDuration <= TimeSpan.Zero || LeaseDuration > TimeSpan.FromMinutes(10))
            {
                throw new LocalTableException(
                    LocalTableErrorKind.InvalidOptions,
                    "LeaseDuration must be positive and at most ten minutes.");
            }

            if (AutoFlushInterval < TimeSpan.Zero || AutoFlushInterval > TimeSpan.FromMinutes(1))
            {
                throw new LocalTableException(
                    LocalTableErrorKind.InvalidOptions,
                    "AutoFlushInterval must be between zero and one minute.");
            }

            LocalTableHostPaths.ResolveSaveDirectory(SaveDirectory);
        }

        private static bool IsSafeRoomId(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 64 || value == "." || value == "..")
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool valid = (c >= 'a' && c <= 'z') ||
                             (c >= 'A' && c <= 'Z') ||
                             (c >= '0' && c <= '9') ||
                             c == '-' || c == '_' || c == '.';
                if (!valid)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>Injected local clock/random/id source. It is never read by Domain.</summary>
    public sealed class LocalHostEnvironment
    {
        public ITableClock? Clock { get; set; }

        /// <summary>Returns a value in [0,bound); bound is always positive.</summary>
        public Func<int, int>? NextInt { get; set; }

        public Func<double>? NextDouble { get; set; }

        public Func<string>? NewId { get; set; }

        public static LocalHostEnvironment System { get; } = new LocalHostEnvironment();

        internal ITableClock ResolveClock() => Clock ?? SystemTableClock.Instance;

        internal int ResolveInt(int bound)
        {
            if (bound <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(bound));
            }

            if (NextInt != null)
            {
                int value = NextInt(bound);
                if (value < 0 || value >= bound)
                {
                    throw new InvalidOperationException("Injected NextInt returned a value outside [0,bound).");
                }

                return value;
            }

            return SystemRandom.Next(bound);
        }

        internal double ResolveDouble()
        {
            if (NextDouble != null)
            {
                double value = NextDouble();
                if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0 || value >= 1.0)
                {
                    throw new InvalidOperationException("Injected NextDouble returned a value outside [0,1).");
                }

                return value;
            }

            return SystemRandom.NextDouble();
        }

        internal string ResolveId()
        {
            string value = NewId != null ? NewId() : Guid.NewGuid().ToString("N");
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException("Injected NewId returned an empty id.");
            }

            return value;
        }

        private static class SystemRandom
        {
            private static readonly Random Shared = new Random(unchecked(Environment.TickCount * 397));
            private static readonly object Gate = new object();

            public static int Next(int bound)
            {
                lock (Gate)
                {
                    return Shared.Next(bound);
                }
            }

            public static double NextDouble()
            {
                lock (Gate)
                {
                    return Shared.NextDouble();
                }
            }
        }
    }

    internal sealed class SystemTableClock : ITableClock
    {
        public static SystemTableClock Instance { get; } = new SystemTableClock();

        private readonly Stopwatch _watch = Stopwatch.StartNew();

        public long MonotonicMillis => _watch.ElapsedMilliseconds;

        public long UtcNowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    /// <summary>
    /// Static local host facade. Creating a channel allocates no file handle, lease or actor;
    /// the first OpenAsync is the only resource acquisition point.
    /// </summary>
    public static class LocalTableHost
    {
        public static ValueTask<ITableChannel> CreateChannelAsync(
            LocalTableHostOptions options,
            CancellationToken cancellationToken = default)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return new ValueTask<ITableChannel>(Task.FromCanceled<ITableChannel>(cancellationToken));
            }

            try
            {
                options.Validate();
            }
            catch (LocalTableException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new LocalTableException(
                    LocalTableErrorKind.InvalidOptions,
                    "Local table options are invalid: " + exception.Message,
                    exception);
            }

            ITableChannel channel = new LocalTableChannel(options);
            return new ValueTask<ITableChannel>(channel);
        }

        /// <summary>
        /// Issues a one-process, one-use local connection ticket. It is an opaque handle, never a
        /// JWT/wallet credential, and is invalid after consumption or 30 seconds.
        /// </summary>
        public static string IssueTicket(LocalTableHostOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            options.Validate();
            return LocalTicketIssuer.Issue(options.RoomId);
        }
    }

    /// <summary>Process-local opaque ticket registry. Nothing is written or uploaded.</summary>
    internal static class LocalTicketIssuer
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Ticket> Tickets = new Dictionary<string, Ticket>(StringComparer.Ordinal);

        private sealed class Ticket
        {
            public Ticket(string roomId, long expiresAtUtcMs)
            {
                RoomId = roomId;
                ExpiresAtUtcMs = expiresAtUtcMs;
            }

            public string RoomId { get; }

            public long ExpiresAtUtcMs { get; }
        }

        public static string Issue(string roomId)
        {
            string value = "local-ticket-" + Guid.NewGuid().ToString("N");
            long expires = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 30_000L;
            lock (Gate)
            {
                Prune();
                Tickets[value] = new Ticket(roomId, expires);
            }

            return value;
        }

        public static bool TryConsume(string? value, string roomId)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            lock (Gate)
            {
                Prune();
                if (!Tickets.TryGetValue(value!, out Ticket? ticket))
                {
                    return false;
                }

                Tickets.Remove(value!);
                return string.Equals(ticket.RoomId, roomId, StringComparison.Ordinal);
            }
        }

        private static void Prune()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var stale = new List<string>();
            foreach (KeyValuePair<string, Ticket> pair in Tickets)
            {
                if (pair.Value.ExpiresAtUtcMs <= now)
                {
                    stale.Add(pair.Key);
                }
            }

            for (int i = 0; i < stale.Count; i++)
            {
                Tickets.Remove(stale[i]);
            }
        }
    }
}
