using System;
using System.Collections.Concurrent;

namespace Mahjong.TableService
{
    /// <summary>
    /// Server-level live-session directory used to deliver 顶号 displacement. The ticket
    /// consumption registry decides WHO is displaced (the superseded session id); this
    /// directory finds that live handler and asks it to close with
    /// DISPLACED_BY_NEW_CONNECTION. Registration happens during admission; a displaced
    /// session skips its own seat DisconnectAsync because the seat is being handed over to
    /// the newer connection, not abandoned.
    /// </summary>
    public interface ITableSessionDirectory
    {
        void Register(string sessionId, Action onDisplaced);

        void Unregister(string sessionId);

        /// <summary>True when a live session was found and asked to close.</summary>
        bool Displace(string sessionId);
    }

    public sealed class InMemorySessionDirectory : ITableSessionDirectory
    {
        private readonly ConcurrentDictionary<string, Action> _handlers =
            new ConcurrentDictionary<string, Action>(StringComparer.Ordinal);

        public void Register(string sessionId, Action onDisplaced)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                throw new ArgumentException("A session registration requires a session id.", nameof(sessionId));
            }

            _handlers[sessionId] = onDisplaced ?? throw new ArgumentNullException(nameof(onDisplaced));
        }

        public void Unregister(string sessionId)
        {
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                _handlers.TryRemove(sessionId, out _);
            }
        }

        public bool Displace(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return false;
            }

            if (_handlers.TryRemove(sessionId, out Action? onDisplaced))
            {
                onDisplaced();
                return true;
            }

            return false;
        }
    }
}
