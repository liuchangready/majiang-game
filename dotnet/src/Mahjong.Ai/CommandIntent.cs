using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Mahjong.Domain;

namespace Mahjong.Ai
{
    /// <summary>
    /// The immutable, recorded result of one AI decision. It is the only thing the table
    /// runtime needs for deterministic replay: the authoritative decision identity, the
    /// selected legal candidate and the algorithm identity.
    ///
    /// The canonical byte stream has a fixed field order and explicitly excludes every
    /// floating-point heuristic value. <see cref="AuthorityHash"/> is therefore stable across
    /// runs, cultures and runtimes and cannot change when only a score/ranking changes.
    /// </summary>
    public sealed class CommandIntent
    {
        public const int EncodingVersion = 1;

        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private readonly byte[] _canonicalBytes;

        public CommandIntent(
            AiObservation observation,
            LegalActionOption action,
            string algorithmId,
            int algorithmVersion)
        {
            if (observation == null)
            {
                throw new ArgumentNullException(nameof(observation));
            }

            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (!observation.ContainsLegalAction(action))
            {
                throw new ArgumentException(
                    "A CommandIntent can only select a candidate from its own observation.",
                    nameof(action));
            }

            if (algorithmVersion < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(algorithmVersion),
                    algorithmVersion,
                    "Algorithm version must be positive.");
            }

            DecisionKey = observation.DecisionKey;
            ControlEpoch = observation.ControlEpoch;
            ObservationVersion = observation.Version;
            ObservationStateVersion = observation.StateVersion;
            Action = action;
            AlgorithmId = AiGuard.RequireText(algorithmId, nameof(algorithmId));
            AlgorithmVersion = algorithmVersion;
            _canonicalBytes = Encode();
            AuthorityHash = Hash(_canonicalBytes);
        }

        public DecisionKey DecisionKey { get; }

        public string RoundId => DecisionKey.RoundId;

        public int Seat => DecisionKey.Seat;

        public string DecisionId => DecisionKey.DecisionId;

        public long ControlEpoch { get; }

        public int ObservationVersion { get; }

        public long ObservationStateVersion { get; }

        public LegalActionOption Action { get; }

        public string AlgorithmId { get; }

        public int AlgorithmVersion { get; }

        /// <summary>Lower-case hexadecimal SHA-256 of <see cref="CanonicalBytes"/>.</summary>
        public string AuthorityHash { get; }

        /// <summary>Copy of the canonical intent bytes. No heuristic score is encoded.</summary>
        public byte[] CanonicalBytes()
        {
            var copy = new byte[_canonicalBytes.Length];
            Array.Copy(_canonicalBytes, copy, _canonicalBytes.Length);
            return copy;
        }

        public bool HasSameAuthorityIdentity(CommandIntent other)
        {
            return other != null &&
                   string.Equals(AuthorityHash, other.AuthorityHash, StringComparison.Ordinal);
        }

        public override string ToString() =>
            DecisionKey + "/epoch" + ControlEpoch.ToString(CultureInfo.InvariantCulture) + "/" + Action;

        private byte[] Encode()
        {
            var bytes = new List<byte>(160);
            AddBytes(bytes, StrictUtf8.GetBytes("MJAI-INTENT"));
            AddByte(bytes, EncodingVersion);
            AddByte(bytes, unchecked((byte)ObservationVersion));
            AddString(bytes, DecisionKey.RoundId);
            AddByte(bytes, unchecked((byte)DecisionKey.Seat));
            AddString(bytes, DecisionKey.DecisionId);
            AddInt64(bytes, ControlEpoch);
            AddInt64(bytes, ObservationStateVersion);
            AddByte(bytes, (byte)Action.Kind);
            AddByte(bytes, unchecked((byte)Action.Seat));
            if (Action.Tile.HasValue)
            {
                AddByte(bytes, 1);
                AddByte(bytes, unchecked((byte)Action.Tile.Value.Value));
            }
            else
            {
                AddByte(bytes, 0);
            }

            if (Action.OptionId != null)
            {
                AddByte(bytes, 1);
                AddString(bytes, Action.OptionId);
            }
            else
            {
                AddByte(bytes, 0);
            }

            AddString(bytes, AlgorithmId);
            AddInt32(bytes, AlgorithmVersion);
            return bytes.ToArray();
        }

        private static string Hash(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(bytes);
                var text = new StringBuilder(digest.Length * 2);
                for (int i = 0; i < digest.Length; i++)
                {
                    text.Append(digest[i].ToString("x2", CultureInfo.InvariantCulture));
                }

                return text.ToString();
            }
        }

        private static void AddString(List<byte> bytes, string value)
        {
            byte[] payload = StrictUtf8.GetBytes(value);
            AddInt32(bytes, payload.Length);
            AddBytes(bytes, payload);
        }

        private static void AddInt32(List<byte> bytes, int value)
        {
            unchecked
            {
                uint bits = (uint)value;
                AddByte(bytes, (byte)(bits >> 24));
                AddByte(bytes, (byte)(bits >> 16));
                AddByte(bytes, (byte)(bits >> 8));
                AddByte(bytes, (byte)bits);
            }
        }

        private static void AddInt64(List<byte> bytes, long value)
        {
            unchecked
            {
                ulong bits = (ulong)value;
                AddByte(bytes, (byte)(bits >> 56));
                AddByte(bytes, (byte)(bits >> 48));
                AddByte(bytes, (byte)(bits >> 40));
                AddByte(bytes, (byte)(bits >> 32));
                AddByte(bytes, (byte)(bits >> 24));
                AddByte(bytes, (byte)(bits >> 16));
                AddByte(bytes, (byte)(bits >> 8));
                AddByte(bytes, (byte)bits);
            }
        }

        private static void AddByte(List<byte> bytes, byte value)
        {
            bytes.Add(value);
        }

        private static void AddBytes(List<byte> bytes, byte[] value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                bytes.Add(value[i]);
            }
        }
    }
}
