#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Mahjong.Contracts.Wire;
using UnityEngine;

namespace Mahjong.Unity.App
{
    /// <summary>Opt-in raw inbound capture and typed privacy audit for real LocalHost channels.</summary>
    public static class C04PrivacyProbe
    {
        [Serializable]
        private sealed class FrameRecord
        {
            public int ordinal;
            public string wireSha256 = string.Empty;
            public string rawBase64 = string.Empty;
            public string decodeStatus = string.Empty;
            public string errorCode = string.Empty;
            public string messageType = string.Empty;
            public bool isFullSnapshot;
            public string sessionId = string.Empty;
            public string roomId = string.Empty;
            public string scopeId = string.Empty;
            public string accountId = string.Empty;
            public string connectionRole = string.Empty;
            public int boundSeat = -1;
            public int seatId = -1;
            public ulong bindingEpoch;
            public ulong cursorStreamVersion;
            public ulong cursorProjectionSeq;
            public int visibleEventCount;
            public ulong stateVersion;
            public ulong eventSeq;
            public string audience = string.Empty;
            public int ownerSeat = -1;
            public int privateClosedTileCount;
            public bool hasPrivateCheatView;
            public long privateCheatPoints;
            public int privateLedgerEntryCount;
            public int privateMarkedTileCount;
            public int privateImmediateSleeveOfferTileCount;
            public int privateOffTurnSleeveOfferTileCount;
            public int privateSleeveReservationCount;
            public int ownerSleeveTileCount;
            public int ownerMeldCount;
            public bool hasPrivateControlView;
            public ulong controlEpoch;
            public ulong controlVersion;
            public bool hasPublicSnapshot;
            public bool unknownFieldsZero;
        }

        [Serializable]
        private sealed class FrameFile
        {
            public int schemaVersion = 1;
            public FrameRecord[] frames = Array.Empty<FrameRecord>();
        }

        private static readonly object Gate = new object();
        private static readonly List<FrameRecord> Frames = new List<FrameRecord>();
        private static readonly Dictionary<string, ConnectionFacts> Connections = new Dictionary<string, ConnectionFacts>(StringComparer.Ordinal);
        private static string? _root;
        private static bool _sealed;

        private sealed class ConnectionFacts
        {
            public string ScopeId = string.Empty;
            public string AccountId = string.Empty;
            public string Role = string.Empty;
            public int Seat = -1;
        }

        public static bool IsEnabled
        {
            get { lock (Gate) return !string.IsNullOrEmpty(_root) && !_sealed; }
        }

        public static void Begin(string outputDirectory)
        {
            if (string.IsNullOrWhiteSpace(outputDirectory)) throw new ArgumentException("Evidence directory is required.", nameof(outputDirectory));
            string root = Path.GetFullPath(outputDirectory);
            lock (Gate)
            {
                if (_root != null && !_sealed) throw new InvalidOperationException("A C04 privacy capture is already active.");
                if (Directory.Exists(root)) throw new IOException("C04 privacy evidence directory already exists.");
                Directory.CreateDirectory(root);
                Frames.Clear();
                Connections.Clear();
                _root = root;
                _sealed = false;
            }
        }

        internal static void RecordInbound(ReadOnlyMemory<byte> wire)
        {
            lock (Gate)
            {
                if (_root == null || _sealed) return;
                byte[] bytes = wire.ToArray();
                WireDecodeResult decoded = WireCodec.Decode(bytes);
                Envelope? envelope = decoded.Envelope;
                if (decoded.Status == WireDecodeStatus.Ok && envelope?.Welcome != null)
                {
                    Welcome welcome = envelope.Welcome;
                    Connections[welcome.SessionId] = new ConnectionFacts
                    {
                        ScopeId = welcome.Scope?.ScopeId ?? string.Empty,
                        AccountId = welcome.Scope?.AccountId ?? string.Empty,
                        Role = welcome.Role == Mahjong.Contracts.Wire.Role.Player ? "Player" :
                            welcome.Role == Mahjong.Contracts.Wire.Role.Spectator ? "Spectator" : "Unsupported",
                        Seat = welcome.SeatId
                    };
                    return;
                }
                bool hasTypedSnapshot = envelope?.SyncResponse != null || envelope?.PlayerUpdate != null || envelope?.PublicUpdate != null;
                if (decoded.Status == WireDecodeStatus.Ok && !hasTypedSnapshot) return;
                var record = new FrameRecord
                {
                    ordinal = Frames.Count + 1,
                    wireSha256 = Sha256(bytes),
                    rawBase64 = Convert.ToBase64String(bytes),
                    decodeStatus = decoded.Status.ToString(),
                    errorCode = decoded.ErrorCode ?? string.Empty,
                    messageType = envelope?.MessageType.ToString() ?? string.Empty,
                    isFullSnapshot = envelope?.SyncResponse?.IsFullSnapshot ??
                        envelope?.PlayerUpdate?.IsFullSnapshot ?? envelope?.PublicUpdate?.IsFullSnapshot ?? false,
                    sessionId = envelope?.SessionId ?? string.Empty,
                    roomId = envelope?.RoomId ?? string.Empty,
                    seatId = envelope?.SeatId ?? -1,
                    bindingEpoch = envelope?.BindingEpoch ?? 0UL,
                    cursorStreamVersion = envelope?.Cursor?.StreamVersion ?? 0UL,
                    cursorProjectionSeq = envelope?.Cursor?.ProjectionSeq ?? 0UL,
                    unknownFieldsZero = decoded.UnknownFieldCount == 0
                };
                if (envelope != null && Connections.TryGetValue(envelope.SessionId, out ConnectionFacts? connection))
                {
                    record.scopeId = connection.ScopeId;
                    record.accountId = connection.AccountId;
                    record.connectionRole = connection.Role;
                    record.boundSeat = connection.Seat;
                }
                if (envelope?.SyncResponse != null)
                {
                    record.audience = envelope.SyncResponse.Target == SyncTarget.PlayerStream ? "Player" :
                        envelope.SyncResponse.Target == SyncTarget.PublicStream ? "Public" : "Unsupported";
                    record.stateVersion = envelope.SyncResponse.StateVersion;
                    record.eventSeq = envelope.SyncResponse.EventSeq;
                    if (envelope.SyncResponse.BaselineCase == SyncResponse.BaselineOneofCase.Player)
                    {
                        record.stateVersion = envelope.SyncResponse.Player.StateVersion;
                        record.eventSeq = envelope.SyncResponse.Player.EventSeq;
                        record.ownerSeat = envelope.SyncResponse.Player.PrivateView.Seat;
                        record.privateClosedTileCount = envelope.SyncResponse.Player.PrivateView.ClosedTiles.Count;
                        RecordPrivateFacts(record, envelope.SyncResponse.Player.PrivateView);
                    }
                    else if (envelope.SyncResponse.BaselineCase == SyncResponse.BaselineOneofCase.Public)
                    {
                        record.stateVersion = envelope.SyncResponse.Public.StateVersion;
                        record.eventSeq = envelope.SyncResponse.Public.EventSeq;
                        record.hasPublicSnapshot = envelope.SyncResponse.Public != null;
                    }
                }
                else if (envelope?.PlayerUpdate != null)
                {
                    record.audience = "PlayerUpdate";
                    record.visibleEventCount = envelope.PlayerUpdate.VisibleEvents.Count;
                    record.stateVersion = envelope.PlayerUpdate.Snapshot.StateVersion;
                    record.eventSeq = envelope.PlayerUpdate.Snapshot.EventSeq;
                    record.ownerSeat = envelope.PlayerUpdate.Snapshot.PrivateView.Seat;
                    record.privateClosedTileCount = envelope.PlayerUpdate.Snapshot.PrivateView.ClosedTiles.Count;
                    RecordPrivateFacts(record, envelope.PlayerUpdate.Snapshot.PrivateView);
                }
                else if (envelope?.PublicUpdate != null)
                {
                    record.audience = "PublicUpdate";
                    record.visibleEventCount = envelope.PublicUpdate.VisibleEvents.Count;
                    record.stateVersion = envelope.PublicUpdate.Snapshot.StateVersion;
                    record.eventSeq = envelope.PublicUpdate.Snapshot.EventSeq;
                    record.hasPublicSnapshot = true;
                }
                Frames.Add(record);
            }
        }

        public static string Seal()
        {
            lock (Gate)
            {
                if (_root == null || _sealed) throw new InvalidOperationException("No active C04 privacy capture exists.");
                string path = Path.Combine(_root, "raw-frames.json");
                string json = JsonUtility.ToJson(new FrameFile { frames = Frames.ToArray() }, true);
                File.WriteAllText(path, json, new UTF8Encoding(false));
                string hash;
                using (SHA256 sha = SHA256.Create())
                    hash = Hex(sha.ComputeHash(File.ReadAllBytes(path)));
                _sealed = true;
                return hash;
            }
        }

        public static int CapturedFrameCount
        {
            get { lock (Gate) return Frames.Count; }
        }

        private static void RecordPrivateFacts(FrameRecord record, PlayerPrivateView view)
        {
            record.ownerSeat = view.Seat;
            record.privateClosedTileCount = view.ClosedTiles.Count;
            record.hasPrivateCheatView = view.Cheat != null;
            if (view.Cheat != null)
            {
                record.privateCheatPoints = view.Cheat.CheatPoints;
                record.privateLedgerEntryCount = view.Cheat.LedgerEntries.Count;
                record.privateMarkedTileCount = view.Cheat.TileMark?.Groups.Count ?? 0;
                record.privateImmediateSleeveOfferTileCount = SleeveOfferFactCount(view.Cheat.SleeveBloom);
                record.privateOffTurnSleeveOfferTileCount = SleeveOfferFactCount(view.Cheat.OffTurnSleeveBloom);
                record.privateSleeveReservationCount = view.Cheat.SleeveBloomReservation != null ? 1 : 0;
            }
            record.ownerSleeveTileCount = view.SleeveTiles.Count;
            record.ownerMeldCount = view.OwnerMelds.Count;
            if (view.Control != null)
            {
                record.hasPrivateControlView = true;
                record.controlEpoch = view.Control.ControlEpoch;
                record.controlVersion = view.Control.ControlVersion;
            }
        }

        private static int SleeveOfferFactCount(SleeveBloomView? offer)
        {
            if (offer == null) return 0;
            return (offer.HasFirstTileId ? 1 : 0) + (offer.HasSecondTileId ? 1 : 0) +
                   (offer.HasReservedTileId ? 1 : 0);
        }

        public static void ResetForTest()
        {
            lock (Gate)
            {
                _root = null;
                _sealed = false;
                Frames.Clear();
                Connections.Clear();
            }
        }

        private static string Sha256(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create()) return Hex(sha.ComputeHash(bytes));
        }

        private static string Hex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (byte value in bytes) builder.Append(value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            return builder.ToString();
        }
    }
}
