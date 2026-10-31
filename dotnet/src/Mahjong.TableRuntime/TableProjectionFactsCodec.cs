using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using Mahjong.Contracts;
using Mahjong.Domain;

namespace Mahjong.TableRuntime
{
    /// <summary>
    /// Strict binary codec for the P3.2 projection facts that Domain does not model. The
    /// envelope binds round/state version; every nested optional field has an explicit presence
    /// tag. Unknown schema/truncation/trailing bytes are rejected. Cheat is encoded separately by
    /// the checkpoint so this codec never duplicates private wallet state.
    /// </summary>
    public static class TableProjectionFactsCodec
    {
        public const int FormatVersion = 2;
        private const int LegacyFormatVersion = 1;
        private const int Magic = 0x4D4A5046; // MJPF
        private const int MaxItems = 200;

        public static byte[] Encode(TableProjectionContextData data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(data.RoundId);
                writer.Write(data.StateVersion.Value);
                writer.Write(data.SessionVersion);

                if (data.PublicFacts.Count != 4)
                {
                    throw new InvalidOperationException("Projection facts require four public seat facts.");
                }

                writer.Write(data.PublicFacts.Count);
                for (int i = 0; i < data.PublicFacts.Count; i++)
                {
                    WritePublicFacts(writer, data.PublicFacts[i]);
                }

                writer.Write(data.PrivateFacts.Count);
                for (int i = 0; i < data.PrivateFacts.Count; i++)
                {
                    WritePrivateFacts(writer, data.PrivateFacts[i]);
                }

                WriteSession(writer, data.Session);
                WritePolicy(writer, data.Policy);

                writer.Write(data.EventFacts.Count);
                for (int i = 0; i < data.EventFacts.Count; i++)
                {
                    WriteEventFact(writer, data.EventFacts[i]);
                }

                writer.Write(data.ConcealedMeldFacts.Count);
                for (int i = 0; i < data.ConcealedMeldFacts.Count; i++)
                {
                    TablePublicConcealedMeldFact value = data.ConcealedMeldFacts[i];
                    writer.Write(value.Seat);
                    writer.Write(value.TileCount);
                }

                writer.Flush();
                return stream.ToArray();
            }
        }

        /// <summary>
        /// Reads only the envelope identity of a projection-facts payload. Hosts that persist a
        /// whole commit candidate use it to avoid applying facts that belong to a different
        /// state version (for example a baseline checkpoint reused by a synthetic commit).
        /// </summary>
        public static TableProjectionFactsIdentity ReadIdentity(byte[] payload)
        {
            if (payload == null || payload.Length < 8)
            {
                throw new InvalidDataException("Projection facts payload is empty or truncated.");
            }

            using (var stream = new MemoryStream(payload, writable: false))
            using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true))
            {
                if (reader.ReadInt32() != Magic)
                {
                    throw new InvalidDataException("Projection facts magic is invalid.");
                }

                int schema = reader.ReadInt32();
                if (schema != LegacyFormatVersion && schema != FormatVersion)
                {
                    throw new InvalidDataException("Unsupported projection facts schema " + schema + ".");
                }

                return new TableProjectionFactsIdentity(reader.ReadString(), new StateVersion(reader.ReadUInt64()));
            }
        }

        public static TableProjectionContextData Decode(
            byte[] payload,
            string expectedRoundId,
            StateVersion expectedStateVersion)
        {
            if (payload == null || payload.Length < 8)
            {
                throw new InvalidDataException("Projection facts payload is empty or truncated.");
            }

            using (var stream = new MemoryStream(payload, writable: false))
            using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true))
            {
                if (reader.ReadInt32() != Magic)
                {
                    throw new InvalidDataException("Projection facts magic is invalid.");
                }

                int schema = reader.ReadInt32();
                if (schema != LegacyFormatVersion && schema != FormatVersion)
                {
                    throw new InvalidDataException("Unsupported projection facts schema " + schema + ".");
                }

                string roundId = reader.ReadString();
                ulong stateVersion = reader.ReadUInt64();
                ulong sessionVersion = reader.ReadUInt64();
                if (!string.Equals(roundId, expectedRoundId, StringComparison.Ordinal) ||
                    stateVersion != expectedStateVersion.Value)
                {
                    throw new InvalidDataException("Projection facts do not match the recovered round/state.");
                }

                int publicCount = reader.ReadInt32();
                if (publicCount != 4)
                {
                    throw new InvalidDataException("Projection facts require four public seat facts.");
                }

                var publicFacts = new List<TableSeatPublicFacts>(4);
                for (int i = 0; i < publicCount; i++)
                {
                    publicFacts.Add(ReadPublicFacts(reader, i));
                }

                int privateCount = reader.ReadInt32();
                if (privateCount != 4)
                {
                    throw new InvalidDataException("Projection facts require four private seat facts.");
                }

                var privateFacts = new List<TableSeatPrivateFacts>(privateCount);
                for (int i = 0; i < privateCount; i++)
                {
                    privateFacts.Add(ReadPrivateFacts(reader, schema));
                }

                TableSessionProjection? session = ReadSession(reader);
                TableProjectionPolicy policy = ReadPolicy(reader);

                int eventCount = reader.ReadInt32();
                if (eventCount < 0 || eventCount > 4096)
                {
                    throw new InvalidDataException("Projection event fact count is invalid.");
                }

                var eventFacts = new List<TableProjectionEventFact>(eventCount);
                for (int i = 0; i < eventCount; i++)
                {
                    eventFacts.Add(ReadEventFact(reader));
                }

                int concealedCount = reader.ReadInt32();
                if (concealedCount < 0 || concealedCount > 16)
                {
                    throw new InvalidDataException("Projection concealed meld count is invalid.");
                }

                var concealed = new List<TablePublicConcealedMeldFact>(concealedCount);
                for (int i = 0; i < concealedCount; i++)
                {
                    concealed.Add(new TablePublicConcealedMeldFact(reader.ReadInt32(), reader.ReadUInt32()));
                }

                if (stream.Position != payload.Length)
                {
                    throw new InvalidDataException("Projection facts payload has trailing bytes.");
                }

                return new TableProjectionContextData(
                    roundId,
                    new StateVersion(stateVersion),
                    sessionVersion,
                    null,
                    publicFacts,
                    privateFacts,
                    session,
                    policy,
                    eventFacts,
                    concealed);
            }
        }

        public readonly struct TableProjectionFactsIdentity
        {
            public TableProjectionFactsIdentity(string roundId, StateVersion stateVersion)
            {
                RoundId = roundId;
                StateVersion = stateVersion;
            }

            public string RoundId { get; }

            public StateVersion StateVersion { get; }
        }

        private static void WritePublicFacts(BinaryWriter writer, TableSeatPublicFacts facts)
        {
            writer.Write(facts.Seat);
            writer.Write(facts.Riichi);
            writer.Write(facts.Melds.Count);
            for (int i = 0; i < facts.Melds.Count; i++)
            {
                TableMeldFact meld = facts.Melds[i];
                writer.Write((byte)meld.Type);
                writer.Write(meld.TileIds.Count);
                for (int t = 0; t < meld.TileIds.Count; t++)
                {
                    writer.Write(meld.TileIds[t]);
                }

                writer.Write(meld.CalledTileId.HasValue);
                if (meld.CalledTileId.HasValue)
                {
                    writer.Write(meld.CalledTileId.Value);
                }

                writer.Write(meld.FromSeat);
            }

            writer.Write(facts.Discards.Count);
            for (int i = 0; i < facts.Discards.Count; i++)
            {
                DiscardView discard = facts.Discards[i];
                writer.Write(discard.TileId);
                writer.Write(discard.TurnNumber);
                writer.Write(discard.RiichiDiscard);
                writer.Write(discard.Tsumogiri);
            }
        }

        private static TableSeatPublicFacts ReadPublicFacts(BinaryReader reader, int expectedSeat)
        {
            int seat = reader.ReadInt32();
            if (seat != expectedSeat)
            {
                throw new InvalidDataException("Public facts are not ordered by seat.");
            }

            bool riichi = reader.ReadBoolean();
            int meldCount = reader.ReadInt32();
            if (meldCount < 0 || meldCount > 4)
            {
                throw new InvalidDataException("Meld fact count is invalid.");
            }

            var melds = new List<TableMeldFact>(meldCount);
            for (int i = 0; i < meldCount; i++)
            {
                byte type = reader.ReadByte();
                if (!Enum.IsDefined(typeof(ProtocolMeldType), (int)type))
                {
                    throw new InvalidDataException("Meld fact type is invalid.");
                }

                int tileCount = reader.ReadInt32();
                if (tileCount < 0 || tileCount > 4)
                {
                    throw new InvalidDataException("Meld fact tile count is invalid.");
                }

                var tiles = new uint[tileCount];
                for (int t = 0; t < tileCount; t++)
                {
                    tiles[t] = reader.ReadUInt32();
                }

                uint? called = reader.ReadBoolean() ? reader.ReadUInt32() : (uint?)null;
                melds.Add(new TableMeldFact((ProtocolMeldType)type, tiles, called, reader.ReadInt32()));
            }

            int discardCount = reader.ReadInt32();
            if (discardCount < 0 || discardCount > MaxItems)
            {
                throw new InvalidDataException("Discard fact count is invalid.");
            }

            var discards = new List<DiscardView>(discardCount);
            for (int i = 0; i < discardCount; i++)
            {
                discards.Add(new DiscardView(
                    reader.ReadUInt32(),
                    reader.ReadUInt32(),
                    reader.ReadBoolean(),
                    reader.ReadBoolean()));
            }

            return new TableSeatPublicFacts(seat, riichi, melds, discards);
        }

        private static void WritePrivateFacts(BinaryWriter writer, TableSeatPrivateFacts facts)
        {
            writer.Write(facts.Seat);
            writer.Write(facts.Furiten);
            writer.Write(facts.TemporaryFuriten);
            writer.Write(facts.RiichiFuriten);
            writer.Write(facts.HasPreparation);
            writer.Write(facts.DiscardHistory.Count);
            for (int i = 0; i < facts.DiscardHistory.Count; i++)
            {
                writer.Write(facts.DiscardHistory[i]);
            }
        }

        private static TableSeatPrivateFacts ReadPrivateFacts(BinaryReader reader, int schema)
        {
            int seat = reader.ReadInt32();
            bool furiten = reader.ReadBoolean();
            bool temporaryFuriten = schema >= 2 && reader.ReadBoolean();
            bool riichiFuriten = schema >= 2 && reader.ReadBoolean();
            bool preparation = reader.ReadBoolean();
            int count = reader.ReadInt32();
            if (count < 0 || count > MaxItems)
            {
                throw new InvalidDataException("Private discard history count is invalid.");
            }

            var history = new uint[count];
            for (int i = 0; i < count; i++)
            {
                history[i] = reader.ReadUInt32();
            }

            return new TableSeatPrivateFacts(
                seat,
                furiten,
                preparation,
                history,
                temporaryFuriten,
                riichiFuriten);
        }

        private static void WriteSession(BinaryWriter writer, TableSessionProjection? session)
        {
            writer.Write(session != null);
            if (session == null)
            {
                return;
            }

            writer.Write(session.Finished);
            writer.Write(session.FinishReason);
            writer.Write(session.FinalScores.Count);
            for (int i = 0; i < session.FinalScores.Count; i++)
            {
                writer.Write(session.FinalScores[i]);
            }

            writer.Write(session.Rankings.Count);
            for (int i = 0; i < session.Rankings.Count; i++)
            {
                writer.Write(session.Rankings[i]);
            }

            writer.Write(session.SettlementRoundId);
        }

        private static TableSessionProjection? ReadSession(BinaryReader reader)
        {
            if (!reader.ReadBoolean())
            {
                return null;
            }

            bool finished = reader.ReadBoolean();
            string reason = reader.ReadString();
            int scoreCount = reader.ReadInt32();
            var scores = new long[scoreCount];
            for (int i = 0; i < scoreCount; i++)
            {
                scores[i] = reader.ReadInt64();
            }

            int rankCount = reader.ReadInt32();
            var ranks = new int[rankCount];
            for (int i = 0; i < rankCount; i++)
            {
                ranks[i] = reader.ReadInt32();
            }

            return new TableSessionProjection(finished, reason, scores, ranks, reader.ReadString());
        }

        private static void WritePolicy(BinaryWriter writer, TableProjectionPolicy policy)
        {
            writer.Write(policy.Paused);
            writer.Write(policy.SettlementBatch);
            writer.Write(policy.RevealSettlementUra);
        }

        private static TableProjectionPolicy ReadPolicy(BinaryReader reader) =>
            new TableProjectionPolicy(reader.ReadBoolean(), reader.ReadBoolean(), reader.ReadBoolean());

        private static void WriteEventFact(BinaryWriter writer, TableProjectionEventFact fact)
        {
            writer.Write((byte)fact.Type);
            writer.Write(fact.EventSeq.Value);
            writer.Write(fact.CommandVersion.Value);
            writer.Write(fact.EventOrdinal);
            WriteEventFacts(writer, fact.Facts);
        }

        private static TableProjectionEventFact ReadEventFact(BinaryReader reader)
        {
            byte type = reader.ReadByte();
            if (!Enum.IsDefined(typeof(ProtocolEventType), (int)type))
            {
                throw new InvalidDataException("Event fact type is invalid.");
            }

            ulong seq = reader.ReadUInt64();
            ulong version = reader.ReadUInt64();
            uint ordinal = reader.ReadUInt32();
            return new TableProjectionEventFact(
                (ProtocolEventType)type,
                new EventSeq(seq),
                new StateVersion(version),
                ordinal,
                ReadEventFacts(reader));
        }

        private static void WriteEventFacts(BinaryWriter writer, TableProjectionEventFacts facts)
        {
            WriteNullableUInt(writer, facts.TurnNumber);
            WriteNullableUInt(writer, facts.WallSize);
            WriteNullableUInt(writer, facts.WallRemaining);
            WriteMeld(writer, facts.Meld);
            WriteNullableInt(writer, facts.FromSeat);
            WriteNullableBool(writer, facts.RiichiDiscard);
            WriteNullableBool(writer, facts.Tsumogiri);
            WriteNullableUInt(writer, facts.DiscardTileId);
            WriteNullableBool(writer, facts.DoubleRiichi);
            WriteNullableInt(writer, facts.LoserSeat);
            WriteYaku(writer, facts.Yaku);
            WriteNullableUInt(writer, facts.Han);
            WriteNullableUInt(writer, facts.Fu);
            WriteNullableLong(writer, facts.Score);
            WriteBreakdown(writer, facts.Breakdown);
            WriteIntList(writer, facts.TenpaiPlayers);
            WritePayments(writer, facts.Payments);
            WriteScoreList(writer, facts.NewScores);
            WriteScoreList(writer, facts.FinalScores);
            WriteNullableInt(writer, facts.WinnerSeat);
            WriteNullableUInt(writer, facts.RoundWind);
            WriteNullableUInt(writer, facts.RoundNumber);
            writer.Write(facts.OfferId != null);
            if (facts.OfferId != null)
            {
                writer.Write(facts.OfferId);
            }

            WriteNullableBool(writer, facts.KeptFirst);
            WriteNullableUInt(writer, facts.ResultTileId);
            writer.Write(facts.BeamSwapResolution.HasValue);
            if (facts.BeamSwapResolution.HasValue)
            {
                writer.Write((int)facts.BeamSwapResolution.Value);
            }

            WriteNullableUInt(writer, facts.HandTileId);
            WriteNullableUInt(writer, facts.TargetSlot);
        }

        private static TableProjectionEventFacts ReadEventFacts(BinaryReader reader)
        {
            uint? turn = ReadNullableUInt(reader);
            uint? wall = ReadNullableUInt(reader);
            uint? remaining = ReadNullableUInt(reader);
            MeldView? meld = ReadMeld(reader);
            int? fromSeat = ReadNullableInt(reader);
            bool? riichi = ReadNullableBool(reader);
            bool? tsumogiri = ReadNullableBool(reader);
            uint? discard = ReadNullableUInt(reader);
            bool? doubleRiichi = ReadNullableBool(reader);
            int? loser = ReadNullableInt(reader);
            IReadOnlyList<YakuView>? yaku = ReadYaku(reader);
            uint? han = ReadNullableUInt(reader);
            uint? fu = ReadNullableUInt(reader);
            long? score = ReadNullableLong(reader);
            ScoreBreakdownView? breakdown = ReadBreakdown(reader);
            IReadOnlyList<int>? tenpai = ReadIntList(reader);
            IReadOnlyList<PaymentView>? payments = ReadPayments(reader);
            IReadOnlyList<ScoreView>? newScores = ReadScoreList(reader);
            IReadOnlyList<ScoreView>? finalScores = ReadScoreList(reader);
            int? winner = ReadNullableInt(reader);
            uint? wind = ReadNullableUInt(reader);
            uint? roundNumber = ReadNullableUInt(reader);
            string? offer = reader.ReadBoolean() ? reader.ReadString() : null;
            bool? kept = ReadNullableBool(reader);
            uint? resultTile = ReadNullableUInt(reader);
            ProjectionBeamSwapResolution? resolution = null;
            if (reader.ReadBoolean())
            {
                int value = reader.ReadInt32();
                if (!Enum.IsDefined(typeof(ProjectionBeamSwapResolution), value))
                {
                    throw new InvalidDataException("Beam swap resolution is invalid.");
                }

                resolution = (ProjectionBeamSwapResolution)value;
            }

            uint? hand = ReadNullableUInt(reader);
            uint? target = ReadNullableUInt(reader);
            return new TableProjectionEventFacts(
                turn, wall, remaining, meld, fromSeat, riichi, tsumogiri, discard,
                doubleRiichi, loser, yaku, han, fu, score, breakdown, tenpai, payments,
                newScores, finalScores, winner, wind, roundNumber, offer, kept, resultTile,
                resolution, hand, target);
        }

        private static void WriteMeld(BinaryWriter writer, MeldView? meld)
        {
            writer.Write(meld != null);
            if (meld == null)
            {
                return;
            }

            writer.Write((byte)meld.Type);
            writer.Write(meld.TileIds.Count);
            for (int i = 0; i < meld.TileIds.Count; i++)
            {
                writer.Write(meld.TileIds[i]);
            }

            writer.Write(meld.CalledTileId.HasValue);
            if (meld.CalledTileId.HasValue)
            {
                writer.Write(meld.CalledTileId.Value);
            }

            writer.Write(meld.FromSeat);
        }

        private static MeldView? ReadMeld(BinaryReader reader)
        {
            if (!reader.ReadBoolean())
            {
                return null;
            }

            byte type = reader.ReadByte();
            int count = reader.ReadInt32();
            var tiles = new uint[count];
            for (int i = 0; i < count; i++)
            {
                tiles[i] = reader.ReadUInt32();
            }

            uint? called = reader.ReadBoolean() ? reader.ReadUInt32() : (uint?)null;
            return new MeldView((ProtocolMeldType)type, tiles, called, reader.ReadInt32());
        }

        private static void WriteYaku(BinaryWriter writer, IReadOnlyList<YakuView>? values)
        {
            writer.Write(values != null);
            if (values == null)
            {
                return;
            }

            writer.Write(values.Count);
            for (int i = 0; i < values.Count; i++)
            {
                writer.Write(values[i].Code);
                writer.Write(values[i].Han);
            }
        }

        private static IReadOnlyList<YakuView>? ReadYaku(BinaryReader reader)
        {
            if (!reader.ReadBoolean())
            {
                return null;
            }

            int count = reader.ReadInt32();
            var values = new List<YakuView>(count);
            for (int i = 0; i < count; i++)
            {
                values.Add(new YakuView(reader.ReadString(), reader.ReadUInt32()));
            }

            return values;
        }

        private static void WriteBreakdown(BinaryWriter writer, ScoreBreakdownView? value)
        {
            writer.Write(value != null);
            if (value == null)
            {
                return;
            }

            writer.Write(value.Han);
            writer.Write(value.Fu);
            writer.Write(value.TotalPoints);
            WriteYaku(writer, value.Yaku);
            writer.Write(value.DoraCount);
            writer.Write(value.UraCount);
            writer.Write(value.UraTiles.Count);
            for (int i = 0; i < value.UraTiles.Count; i++)
            {
                writer.Write(value.UraTiles[i]);
            }

            writer.Write(value.UraHits.Count);
            for (int i = 0; i < value.UraHits.Count; i++)
            {
                writer.Write(value.UraHits[i]);
            }

            writer.Write(value.UraAuthorized);
        }

        private static ScoreBreakdownView? ReadBreakdown(BinaryReader reader)
        {
            if (!reader.ReadBoolean())
            {
                return null;
            }

            uint han = reader.ReadUInt32();
            uint fu = reader.ReadUInt32();
            long total = reader.ReadInt64();
            IReadOnlyList<YakuView>? yaku = ReadYaku(reader);
            uint dora = reader.ReadUInt32();
            uint ura = reader.ReadUInt32();
            int uraTilesCount = reader.ReadInt32();
            var uraTiles = new uint[uraTilesCount];
            for (int i = 0; i < uraTilesCount; i++)
            {
                uraTiles[i] = reader.ReadUInt32();
            }

            int uraHitsCount = reader.ReadInt32();
            var uraHits = new uint[uraHitsCount];
            for (int i = 0; i < uraHitsCount; i++)
            {
                uraHits[i] = reader.ReadUInt32();
            }

            return new ScoreBreakdownView(han, fu, total, yaku, dora, ura, uraTiles, uraHits, reader.ReadBoolean());
        }

        private static void WritePayments(BinaryWriter writer, IReadOnlyList<PaymentView>? values)
        {
            writer.Write(values != null);
            if (values == null)
            {
                return;
            }

            writer.Write(values.Count);
            for (int i = 0; i < values.Count; i++)
            {
                writer.Write(values[i].FromSeat);
                writer.Write(values[i].ToSeat);
                writer.Write(values[i].Amount);
            }
        }

        private static IReadOnlyList<PaymentView>? ReadPayments(BinaryReader reader)
        {
            if (!reader.ReadBoolean())
            {
                return null;
            }

            int count = reader.ReadInt32();
            var values = new List<PaymentView>(count);
            for (int i = 0; i < count; i++)
            {
                values.Add(new PaymentView(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt64()));
            }

            return values;
        }

        private static void WriteScoreList(BinaryWriter writer, IReadOnlyList<ScoreView>? values)
        {
            writer.Write(values != null);
            if (values == null)
            {
                return;
            }

            writer.Write(values.Count);
            for (int i = 0; i < values.Count; i++)
            {
                writer.Write(values[i].Seat);
                writer.Write(values[i].Score);
            }
        }

        private static IReadOnlyList<ScoreView>? ReadScoreList(BinaryReader reader)
        {
            if (!reader.ReadBoolean())
            {
                return null;
            }

            int count = reader.ReadInt32();
            var values = new List<ScoreView>(count);
            for (int i = 0; i < count; i++)
            {
                values.Add(new ScoreView(reader.ReadInt32(), reader.ReadInt64()));
            }

            return values;
        }

        private static void WriteIntList(BinaryWriter writer, IReadOnlyList<int>? values)
        {
            writer.Write(values != null);
            if (values == null)
            {
                return;
            }

            writer.Write(values.Count);
            for (int i = 0; i < values.Count; i++)
            {
                writer.Write(values[i]);
            }
        }

        private static IReadOnlyList<int>? ReadIntList(BinaryReader reader)
        {
            if (!reader.ReadBoolean())
            {
                return null;
            }

            int count = reader.ReadInt32();
            var values = new int[count];
            for (int i = 0; i < count; i++)
            {
                values[i] = reader.ReadInt32();
            }

            return new ReadOnlyCollection<int>(values);
        }

        private static void WriteNullableUInt(BinaryWriter writer, uint? value)
        {
            writer.Write(value.HasValue);
            if (value.HasValue)
            {
                writer.Write(value.Value);
            }
        }

        private static uint? ReadNullableUInt(BinaryReader reader) =>
            reader.ReadBoolean() ? reader.ReadUInt32() : (uint?)null;

        private static void WriteNullableInt(BinaryWriter writer, int? value)
        {
            writer.Write(value.HasValue);
            if (value.HasValue)
            {
                writer.Write(value.Value);
            }
        }

        private static int? ReadNullableInt(BinaryReader reader) =>
            reader.ReadBoolean() ? reader.ReadInt32() : (int?)null;

        private static void WriteNullableLong(BinaryWriter writer, long? value)
        {
            writer.Write(value.HasValue);
            if (value.HasValue)
            {
                writer.Write(value.Value);
            }
        }

        private static long? ReadNullableLong(BinaryReader reader) =>
            reader.ReadBoolean() ? reader.ReadInt64() : (long?)null;

        private static void WriteNullableBool(BinaryWriter writer, bool? value)
        {
            writer.Write(value.HasValue);
            if (value.HasValue)
            {
                writer.Write(value.Value);
            }
        }

        private static bool? ReadNullableBool(BinaryReader reader) =>
            reader.ReadBoolean() ? reader.ReadBoolean() : (bool?)null;
    }
}
