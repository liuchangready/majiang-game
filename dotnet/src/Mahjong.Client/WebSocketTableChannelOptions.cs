using System;
using Mahjong.Contracts.Wire;

namespace Mahjong.Client
{
    /// <summary>
    /// Transport configuration. Production endpoints must be <c>wss</c>; the
    /// plaintext <c>ws</c> scheme is accepted only for an explicit loopback
    /// test configuration. The ticket is never part of this object: it is
    /// carried inside the HELLO envelope by <see cref="TableSession"/>.
    /// </summary>
    public sealed class WebSocketTableChannelOptions
    {
        public const int DefaultMaxPendingSends = 8;

        public WebSocketTableChannelOptions()
        {
        }

        public WebSocketTableChannelOptions(Uri endpoint)
        {
            Endpoint = endpoint;
        }

        /// <summary>
        /// Absolute <c>wss://</c> URL (or <c>ws://</c> for an explicit loopback
        /// test). Userinfo, query and fragment are rejected so a ticket cannot
        /// travel in the URL.
        /// </summary>
        public Uri? Endpoint { get; set; }

        /// <summary>
        /// Explicit test-only opt-in for plaintext <c>ws://</c>. Even when set,
        /// only loopback hosts are accepted; it can never enable public
        /// plaintext traffic.
        /// </summary>
        public bool AllowInsecureLocalWebSocket { get; set; }

        /// <summary>Hard cap for one complete WebSocket message (not one fragment).</summary>
        public int MaxMessageBytes { get; set; } = WireLimits.MaxInboundMessageBytes;

        /// <summary>Maximum send calls admitted but not yet completed; extra callers wait (backpressure).</summary>
        public int MaxPendingSends { get; set; } = DefaultMaxPendingSends;

        public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>Bound applied to admission plus the actual WebSocket write.</summary>
        public TimeSpan SendTimeout { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Bound for receiving one complete message. A stalled or trickling
        /// message is closed instead of accumulating forever.
        /// </summary>
        public TimeSpan ReceiveMessageTimeout { get; set; } = TimeSpan.FromSeconds(60);

        public TimeSpan CloseTimeout { get; set; } = TimeSpan.FromSeconds(5);

        internal void Validate()
        {
            if (Endpoint == null)
            {
                throw new ArgumentException("A WebSocket endpoint is required.", nameof(Endpoint));
            }

            if (!Endpoint.IsAbsoluteUri)
            {
                throw new ArgumentException("The WebSocket endpoint must be absolute.", nameof(Endpoint));
            }

            if (!string.IsNullOrEmpty(Endpoint.UserInfo))
            {
                throw new ArgumentException("Credentials must never be embedded in the WebSocket URL.", nameof(Endpoint));
            }

            if (!string.IsNullOrEmpty(Endpoint.Query) || !string.IsNullOrEmpty(Endpoint.Fragment))
            {
                throw new ArgumentException("The WebSocket URL must not carry a query or fragment (no tickets in URLs).", nameof(Endpoint));
            }

            bool secure = string.Equals(Endpoint.Scheme, "wss", StringComparison.OrdinalIgnoreCase);
            bool insecure = string.Equals(Endpoint.Scheme, "ws", StringComparison.OrdinalIgnoreCase);
            if (!secure && !insecure)
            {
                throw new ArgumentException("Only ws or wss endpoints are supported.", nameof(Endpoint));
            }

            if (insecure)
            {
                if (!AllowInsecureLocalWebSocket)
                {
                    throw new ArgumentException(
                        "Plaintext ws is rejected unless AllowInsecureLocalWebSocket is explicitly enabled for a test.",
                        nameof(Endpoint));
                }

                if (!Endpoint.IsLoopback)
                {
                    throw new ArgumentException(
                        "Plaintext ws is accepted only for loopback test endpoints, never for a remote host.",
                        nameof(Endpoint));
                }
            }

            if (MaxMessageBytes <= 0 || MaxMessageBytes > WireLimits.MaxInboundMessageBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(MaxMessageBytes),
                    MaxMessageBytes,
                    "The message cap must be between 1 and " + WireLimits.MaxInboundMessageBytes + " bytes.");
            }

            if (MaxPendingSends <= 0 || MaxPendingSends > 1024)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxPendingSends), MaxPendingSends, "Pending send limit must be 1..1024.");
            }

            RequirePositive(ConnectTimeout, nameof(ConnectTimeout));
            RequirePositive(SendTimeout, nameof(SendTimeout));
            RequirePositive(CloseTimeout, nameof(CloseTimeout));
            if (ReceiveMessageTimeout <= TimeSpan.Zero && ReceiveMessageTimeout != System.Threading.Timeout.InfiniteTimeSpan)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(ReceiveMessageTimeout),
                    ReceiveMessageTimeout,
                    "Receive timeout must be positive or Timeout.InfiniteTimeSpan.");
            }
        }

        private static void RequirePositive(TimeSpan value, string name)
        {
            if (value <= TimeSpan.Zero || value == System.Threading.Timeout.InfiniteTimeSpan)
            {
                throw new ArgumentOutOfRangeException(name, value, "Timeout must be finite and positive.");
            }
        }
    }
}
