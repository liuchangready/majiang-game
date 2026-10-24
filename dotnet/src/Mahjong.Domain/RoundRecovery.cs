using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;

namespace Mahjong.Domain
{
    /// <summary>
    /// Immutable recovery capture of one exact <see cref="RoundStateMachine"/>.
    /// It is deliberately a value snapshot: the checkpoint stores encoded bytes, never the
    /// live object reference. <see cref="Encode"/> is deterministic and <see cref="Decode"/>
    /// reconstructs a new state machine through <see cref="RoundRecovery.Restore"/>.
    /// </summary>
    public sealed class RoundRecoveryState
    {
        private readonly ReadOnlyCollection<RoundEvent> _events;

        public RoundRecoveryState(
            RoundSpec spec,
            long version,
            RoundPhase phase,
            int currentSeat,
            TilePositionMap tiles,
            RoundResponseWindow? window,
            RoundResult? result,
            IReadOnlyList<RoundEvent> events,
            ulong sourceFingerprint)
        {
            Spec = spec ?? throw new ArgumentNullException(nameof(spec));
            Tiles = tiles ?? throw new ArgumentNullException(nameof(tiles));
            if (version < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(version), version, "Version must be non-negative.");
            }

            if (currentSeat < 0 || currentSeat > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(currentSeat), currentSeat, "Seat must be 0..3.");
            }

            if (events == null || events.Count == 0)
            {
                // A freshly created checkpoint may legitimately have no event yet. Recovery of an
                // accepted version must still have an event; that is enforced by the replay
                // validator, not by this capture type.
                _events = new ReadOnlyCollection<RoundEvent>(new List<RoundEvent>());
            }
            else
            {
                var copy = new List<RoundEvent>(events.Count);
                for (int i = 0; i < events.Count; i++)
                {
                    RoundEvent value = events[i] ?? throw new ArgumentException("Event cannot be null.", nameof(events));
                    if (value.Ordinal != i)
                    {
                        throw new ArgumentException("Events must be contiguous from ordinal zero.", nameof(events));
                    }

                    copy.Add(value);
                }

                _events = new ReadOnlyCollection<RoundEvent>(copy);
            }

            if ((phase == RoundPhase.RoundEnd) != (result != null))
            {
                throw new ArgumentException("ROUND_END and a result must be present together.");
            }

            Version = version;
            Phase = phase;
            CurrentSeat = currentSeat;
            Window = window;
            Result = result;
            SourceFingerprint = sourceFingerprint;
        }

        public RoundSpec Spec { get; }

        public long Version { get; }

        public RoundPhase Phase { get; }

        public int CurrentSeat { get; }

        public TilePositionMap Tiles { get; }

        public RoundResponseWindow? Window { get; }

        public RoundResult? Result { get; }

        public IReadOnlyList<RoundEvent> Events => _events;

        public ulong SourceFingerprint { get; }

        public RoundStateMachine ToStateMachine() => RoundRecovery.Restore(this);
    }

    /// <summary>
    /// Strict Domain-only recovery helper. It reconstructs a state machine from durable data
    /// and verifies the recomputed fingerprint and tile hash. It does not relax any rule, RNG
    /// or scoring check; it calls the same validating constructors/factories as normal play.
    /// </summary>
    public static class RoundRecovery
    {
        public static RoundRecoveryState Capture(RoundStateMachine round)
        {
            if (round == null)
            {
                throw new ArgumentNullException(nameof(round));
            }

            return new RoundRecoveryState(
                round.Spec,
                round.Version,
                round.Phase,
                round.CurrentSeat,
                round.Tiles,
                round.Window,
                round.Result,
                round.Events,
                round.SourceFingerprint);
        }

        public static RoundStateMachine Restore(RoundRecoveryState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            ValidateSpec(state.Spec);
            ValidateEvents(state.Events);
            if (state.Window != null)
            {
                ValidateWindow(state.Window, state.Events.Count);
            }

            if (state.Result != null)
            {
                ValidateResult(state.Result, state.Spec, state.Tiles, state.Events);
            }

            var restored = new RoundStateMachine(
                state.Spec,
                state.Version,
                state.Phase,
                state.CurrentSeat,
                state.Tiles,
                state.Window,
                state.Result,
                state.Events,
                null);

            if (restored.SourceFingerprint != state.SourceFingerprint)
            {
                throw new InvalidDataException(
                    "Recovered round fingerprint " + restored.SourceFingerprint +
                    " does not match the captured fingerprint " + state.SourceFingerprint + ".");
            }

            return restored;
        }

        private static void ValidateSpec(RoundSpec spec)
        {
            if (spec.Scores.Count != 4)
            {
                throw new InvalidDataException("A round spec requires four scores.");
            }
        }

        private static void ValidateEvents(IReadOnlyList<RoundEvent> events)
        {
            for (int i = 0; i < events.Count; i++)
            {
                if (events[i].Ordinal != i)
                {
                    throw new InvalidDataException("Round events are not contiguous from zero.");
                }
            }
        }

        private static void ValidateWindow(RoundResponseWindow window, int eventCount)
        {
            if (window.EligibleSeats.Count != 4 && window.EligibleSeats.Count != 3)
            {
                // The Domain only opens windows for a subset of seats; do not invent a stricter
                // rule here than the state machine itself enforces, but reject impossible counts.
                if (window.EligibleSeats.Count == 0)
                {
                    throw new InvalidDataException("A response window requires at least one eligible seat.");
                }
            }

            var seen = new HashSet<int>();
            for (int i = 0; i < window.EligibleSeats.Count; i++)
            {
                int seat = window.EligibleSeats[i];
                if (seat < 0 || seat > 3 || !seen.Add(seat))
                {
                    throw new InvalidDataException("Response window eligible seats are invalid or duplicated.");
                }
            }

            for (int i = 0; i < window.Claims.Count; i++)
            {
                RoundResponseClaim claim = window.Claims[i];
                if (!seen.Contains(claim.Seat))
                {
                    throw new InvalidDataException("A response claim belongs to a non-eligible seat.");
                }

                if (claim.Settlement != null)
                {
                    ValidateSettlement(claim.Settlement);
                }
            }

            if (eventCount == 0)
            {
                throw new InvalidDataException("A response window cannot exist without an event.");
            }
        }

        private static void ValidateSettlement(RoundSettlement settlement)
        {
            if (settlement.FinalScores.Count != 4)
            {
                throw new InvalidDataException("A settlement requires four final scores.");
            }

            if (settlement.Outcome.Kind == RoundOutcomeKind.None)
            {
                throw new InvalidDataException("A settlement requires a concrete outcome.");
            }
        }

        private static void ValidateResult(
            RoundResult result,
            RoundSpec spec,
            TilePositionMap tiles,
            IReadOnlyList<RoundEvent> events)
        {
            if (!string.Equals(result.RoundId, spec.RoundId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("A round result belongs to another round.");
            }

            if (result.StartingScores.Count != 4 ||
                result.FinalScores.Count != 4 ||
                result.TotalDeltas.Count != 4)
            {
                throw new InvalidDataException("A round result requires four score vectors.");
            }

            long startingTotal = 0;
            long finalTotal = 0;
            for (int i = 0; i < 4; i++)
            {
                startingTotal += result.StartingScores[i];
                finalTotal += result.FinalScores[i];
                if (result.StartingScores[i] != spec.Scores[i])
                {
                    throw new InvalidDataException("A round result starting scores do not match its spec.");
                }
            }

            if (startingTotal != finalTotal)
            {
                throw new InvalidDataException("A round result does not conserve total score.");
            }

            if (result.Events.Count != events.Count)
            {
                throw new InvalidDataException("A round result event history does not match the round events.");
            }

            for (int i = 0; i < events.Count; i++)
            {
                RoundEvent left = result.Events[i];
                RoundEvent right = events[i];
                if (left.Ordinal != right.Ordinal ||
                    left.Type != right.Type ||
                    left.Seat != right.Seat ||
                    !Nullable.Equals(left.Tile, right.Tile) ||
                    !string.Equals(left.Detail, right.Detail, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("A round result event history diverges from the round events.");
                }
            }

            if (tiles.StateHash == 0)
            {
                throw new InvalidDataException("A recovered round has an empty tile hash.");
            }
        }
    }

    /// <summary>
    /// Deterministic binary encoding for cold recovery. This is a Domain helper only: no IO,
    /// network, JSON, reflection or optional fields. Unknown format versions are refused. The
    /// outer durable checkpoint hashes these bytes with SHA-256 and independently recomputes
    /// the fingerprint after decode.
    /// </summary>
    public static class RoundRecoveryCodec
    {
        public const int FormatVersion = 1;
        private const int Magic = 0x4D4A5231; // MJR1

        public static byte[] Encode(RoundRecoveryState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                WriteSpec(writer, state.Spec);
                writer.Write(state.Version);
                writer.Write((byte)state.Phase);
                writer.Write(state.CurrentSeat);
                WriteTiles(writer, state.Tiles);
                WriteWindow(writer, state.Window);
                WriteResult(writer, state.Result);
                writer.Write(state.Events.Count);
                for (int i = 0; i < state.Events.Count; i++)
                {
                    WriteEvent(writer, state.Events[i]);
                }

                writer.Write(state.SourceFingerprint);
                writer.Flush();
                return stream.ToArray();
            }
        }

        public static RoundRecoveryState Decode(byte[] payload)
        {
            if (payload == null || payload.Length < 8)
            {
                throw new InvalidDataException("Round recovery payload is empty or truncated.");
            }

            using (var stream = new MemoryStream(payload, writable: false))
            using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true))
            {
                if (reader.ReadInt32() != Magic)
                {
                    throw new InvalidDataException("Round recovery payload magic is invalid.");
                }

                int version = reader.ReadInt32();
                if (version != FormatVersion)
                {
                    throw new InvalidDataException("Unsupported round recovery schema " + version + ".");
                }

                RoundSpec spec = ReadSpec(reader);
                long roundVersion = reader.ReadInt64();
                byte phaseByte = reader.ReadByte();
                if (!Enum.IsDefined(typeof(RoundPhase), phaseByte))
                {
                    throw new InvalidDataException("Unknown round phase " + phaseByte + ".");
                }

                RoundPhase phase = (RoundPhase)phaseByte;
                int currentSeat = reader.ReadInt32();
                TilePositionMap tiles = ReadTiles(reader);
                RoundResponseWindow? window = ReadWindow(reader);
                RoundResult? result = ReadResult(reader);
                int eventCount = reader.ReadInt32();
                if (eventCount < 0 || eventCount > 100000)
                {
                    throw new InvalidDataException("Round recovery event count is invalid.");
                }

                var events = new List<RoundEvent>(eventCount);
                for (int i = 0; i < eventCount; i++)
                {
                    RoundEvent value = ReadEvent(reader);
                    if (value.Ordinal != i)
                    {
                        throw new InvalidDataException("Round recovery event ordinals are not contiguous.");
                    }

                    events.Add(value);
                }

                ulong fingerprint = reader.ReadUInt64();
                if (stream.Position != payload.Length)
                {
                    throw new InvalidDataException("Round recovery payload has trailing bytes.");
                }

                return new RoundRecoveryState(
                    spec,
                    roundVersion,
                    phase,
                    currentSeat,
                    tiles,
                    window,
                    result,
                    events,
                    fingerprint);
            }
        }

        private static void WriteSpec(BinaryWriter writer, RoundSpec spec)
        {
            writer.Write(spec.RoundId);
            writer.Write(spec.DealerSeat);
            writer.Write(spec.RoundWind);
            writer.Write(spec.RoundNumber);
            writer.Write(spec.HonbaSticks);
            writer.Write(spec.RiichiSticks);
            writer.Write(spec.Scores.Count);
            for (int i = 0; i < spec.Scores.Count; i++)
            {
                writer.Write(spec.Scores[i]);
            }
        }

        private static RoundSpec ReadSpec(BinaryReader reader)
        {
            string roundId = ReadString(reader);
            int dealerSeat = reader.ReadInt32();
            int roundWind = reader.ReadInt32();
            int roundNumber = reader.ReadInt32();
            int honba = reader.ReadInt32();
            int riichi = reader.ReadInt32();
            int scoreCount = reader.ReadInt32();
            if (scoreCount != 4)
            {
                throw new InvalidDataException("Round recovery spec requires four scores.");
            }

            var scores = new long[scoreCount];
            for (int i = 0; i < scores.Length; i++)
            {
                scores[i] = reader.ReadInt64();
            }

            return new RoundSpec(roundId, dealerSeat, roundWind, roundNumber, honba, scores, riichi);
        }

        private static void WriteTiles(BinaryWriter writer, TilePositionMap tiles)
        {
            IReadOnlyList<TilePlacement> placements = tiles.AsPlacements();
            writer.Write(placements.Count);
            for (int i = 0; i < placements.Count; i++)
            {
                TilePlacement placement = placements[i];
                writer.Write(placement.Tile.Value);
                writer.Write((byte)placement.Position.Zone);
                writer.Write((sbyte)placement.Position.Seat);
                writer.Write((short)placement.Position.Index);
            }

            writer.Write(tiles.WallPointer);
            writer.Write(tiles.WallEndExclusive);
            writer.Write(tiles.RinshanPointer);
            writer.Write(tiles.Revision);
            writer.Write(tiles.KanCount);
            writer.Write(tiles.DoraIndicators.Count);
            for (int i = 0; i < tiles.DoraIndicators.Count; i++)
            {
                writer.Write(tiles.DoraIndicators[i].Value);
            }

            WriteLastDiscard(writer, tiles.LastDiscard);
            WriteRinshan(writer, tiles.LastRinshanDraw);
            WriteDrawn(writer, tiles.DrawnTile);
            writer.Write(tiles.StateHash);
        }

        private static TilePositionMap ReadTiles(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            if (count != TileId.Count)
            {
                throw new InvalidDataException("Round recovery requires exactly 136 tile placements.");
            }

            var placements = new TilePlacement[count];
            for (int i = 0; i < count; i++)
            {
                int tile = reader.ReadInt32();
                byte zone = reader.ReadByte();
                sbyte seat = reader.ReadSByte();
                short index = reader.ReadInt16();
                if (tile < 0 || tile >= TileId.Count)
                {
                    throw new InvalidDataException("Tile id in recovery payload is out of range.");
                }

                if (!Enum.IsDefined(typeof(TileZone), zone))
                {
                    throw new InvalidDataException("Tile zone in recovery payload is invalid.");
                }

                placements[i] = new TilePlacement(
                    new TileId(tile),
                    CreatePosition((TileZone)zone, seat, index));
            }

            int wallPointer = reader.ReadInt32();
            int wallEnd = reader.ReadInt32();
            int rinshanPointer = reader.ReadInt32();
            int revision = reader.ReadInt32();
            int kanCount = reader.ReadInt32();
            int doraCount = reader.ReadInt32();
            if (doraCount < 0 || doraCount > TilePositionMap.DeadWallSlotCount)
            {
                throw new InvalidDataException("Dora indicator count is invalid.");
            }

            var dora = new TileId[doraCount];
            for (int i = 0; i < doraCount; i++)
            {
                int value = reader.ReadInt32();
                if (value < 0 || value >= TileId.Count)
                {
                    throw new InvalidDataException("Dora indicator id is invalid.");
                }

                dora[i] = new TileId(value);
            }

            LastDiscardEvidence? lastDiscard = ReadLastDiscard(reader);
            RinshanDrawEvidence? rinshan = ReadRinshan(reader);
            DrawnTileEvidence? drawn = ReadDrawn(reader);
            ulong expectedHash = reader.ReadUInt64();
            TilePositionMap map = TilePositionMap.FromPlacements(
                placements,
                wallPointer,
                wallEnd,
                rinshanPointer,
                lastDiscard,
                revision,
                rinshan,
                dora,
                kanCount,
                drawn);
            if (map.StateHash != expectedHash)
            {
                throw new InvalidDataException("Recovered tile hash does not match the checkpoint.");
            }

            return map;
        }

        private static void WriteLastDiscard(BinaryWriter writer, LastDiscardEvidence? evidence)
        {
            writer.Write(evidence.HasValue);
            if (!evidence.HasValue)
            {
                return;
            }

            writer.Write(evidence.Value.Tile.Value);
            writer.Write((byte)evidence.Value.Position.Zone);
            writer.Write((sbyte)evidence.Value.Position.Seat);
            writer.Write((short)evidence.Value.Position.Index);
        }

        private static LastDiscardEvidence? ReadLastDiscard(BinaryReader reader)
        {
            if (!reader.ReadBoolean())
            {
                return null;
            }

            return new LastDiscardEvidence(ReadTile(reader), ReadPosition(reader));
        }

        private static void WriteRinshan(BinaryWriter writer, RinshanDrawEvidence? evidence)
        {
            writer.Write(evidence.HasValue);
            if (!evidence.HasValue)
            {
                return;
            }

            writer.Write(evidence.Value.Tile.Value);
            writer.Write(evidence.Value.Seat);
            writer.Write(evidence.Value.DeadWallIndex);
        }

        private static RinshanDrawEvidence? ReadRinshan(BinaryReader reader)
        {
            if (!reader.ReadBoolean())
            {
                return null;
            }

            return new RinshanDrawEvidence(ReadTile(reader), reader.ReadInt32(), reader.ReadInt32());
        }

        private static void WriteDrawn(BinaryWriter writer, DrawnTileEvidence? evidence)
        {
            writer.Write(evidence.HasValue);
            if (!evidence.HasValue)
            {
                return;
            }

            writer.Write(evidence.Value.Tile.Value);
            writer.Write(evidence.Value.Seat);
            writer.Write((byte)evidence.Value.Source);
        }

        private static DrawnTileEvidence? ReadDrawn(BinaryReader reader)
        {
            if (!reader.ReadBoolean())
            {
                return null;
            }

            TileId tile = ReadTile(reader);
            int seat = reader.ReadInt32();
            byte source = reader.ReadByte();
            if (!Enum.IsDefined(typeof(DrawSource), source))
            {
                throw new InvalidDataException("Drawn tile source is invalid.");
            }

            return new DrawnTileEvidence(tile, seat, (DrawSource)source);
        }

        private static void WriteWindow(BinaryWriter writer, RoundResponseWindow? window)
        {
            writer.Write(window != null);
            if (window == null)
            {
                return;
            }

            writer.Write(window.FromSeat);
            writer.Write(window.TargetTile.Value);
            writer.Write(window.IsChankan);
            writer.Write(window.EligibleSeats.Count);
            for (int i = 0; i < window.EligibleSeats.Count; i++)
            {
                writer.Write(window.EligibleSeats[i]);
            }

            writer.Write(window.Claims.Count);
            for (int i = 0; i < window.Claims.Count; i++)
            {
                RoundResponseClaim claim = window.Claims[i];
                writer.Write(claim.Seat);
                writer.Write(claim.IsRon);
                writer.Write(claim.Settlement != null);
                if (claim.Settlement != null)
                {
                    WriteSettlement(writer, claim.Settlement);
                }
            }
        }

        private static RoundResponseWindow? ReadWindow(BinaryReader reader)
        {
            if (!reader.ReadBoolean())
            {
                return null;
            }

            int fromSeat = reader.ReadInt32();
            TileId target = ReadTile(reader);
            bool chankan = reader.ReadBoolean();
            int eligibleCount = reader.ReadInt32();
            if (eligibleCount < 0 || eligibleCount > 4)
            {
                throw new InvalidDataException("Response window eligible count is invalid.");
            }

            var eligible = new int[eligibleCount];
            for (int i = 0; i < eligibleCount; i++)
            {
                eligible[i] = reader.ReadInt32();
            }

            int claimCount = reader.ReadInt32();
            if (claimCount < 0 || claimCount > 4)
            {
                throw new InvalidDataException("Response window claim count is invalid.");
            }

            var claims = new List<RoundResponseClaim>(claimCount);
            for (int i = 0; i < claimCount; i++)
            {
                int seat = reader.ReadInt32();
                bool isRon = reader.ReadBoolean();
                if (!isRon)
                {
                    claims.Add(RoundResponseClaim.Pass(seat));
                }
                else
                {
                    if (!reader.ReadBoolean())
                    {
                        throw new InvalidDataException("A Ron claim requires a settlement.");
                    }

                    claims.Add(RoundResponseClaim.Ron(seat, ReadSettlement(reader)));
                }
            }

            return new RoundResponseWindow(fromSeat, target, chankan, eligible, claims);
        }

        private static void WriteResult(BinaryWriter writer, RoundResult? result)
        {
            writer.Write(result != null);
            if (result == null)
            {
                return;
            }

            writer.Write(result.ResultId);
            writer.Write(result.RoundId);
            WriteOutcome(writer, result.Outcome);
            WriteLongs(writer, result.StartingScores);
            WriteLongs(writer, result.FinalScores);
            WriteLongs(writer, result.TotalDeltas);
            writer.Write(result.StartingRiichiSticks);
            writer.Write(result.RemainingRiichiSticks);
            writer.Write((byte)result.EndReason);
            writer.Write(result.CompletedAtUtc.Value.UtcDateTime.Ticks);
            writer.Write(result.CompletedAtMonotonicMillis);
            writer.Write(result.Events.Count);
            for (int i = 0; i < result.Events.Count; i++)
            {
                WriteEvent(writer, result.Events[i]);
            }
        }

        private static RoundResult? ReadResult(BinaryReader reader)
        {
            if (!reader.ReadBoolean())
            {
                return null;
            }

            string resultId = ReadString(reader);
            string roundId = ReadString(reader);
            RoundOutcome outcome = ReadOutcome(reader);
            IReadOnlyList<long> starting = ReadLongs(reader);
            IReadOnlyList<long> final = ReadLongs(reader);
            IReadOnlyList<long> deltas = ReadLongs(reader);
            int startingRiichi = reader.ReadInt32();
            int remainingRiichi = reader.ReadInt32();
            byte endByte = reader.ReadByte();
            if (!Enum.IsDefined(typeof(RoundEndReason), endByte))
            {
                throw new InvalidDataException("Round end reason is invalid.");
            }

            long utcTicks = reader.ReadInt64();
            long monotonic = reader.ReadInt64();
            int eventCount = reader.ReadInt32();
            if (eventCount < 0 || eventCount > 100000)
            {
                throw new InvalidDataException("Round result event count is invalid.");
            }

            var events = new List<RoundEvent>(eventCount);
            for (int i = 0; i < eventCount; i++)
            {
                events.Add(ReadEvent(reader));
            }

            return new RoundResult(
                resultId,
                roundId,
                outcome,
                starting,
                final,
                deltas,
                startingRiichi,
                remainingRiichi,
                events,
                (RoundEndReason)endByte,
                new PersistedUtcTime(new DateTimeOffset(utcTicks, TimeSpan.Zero)),
                monotonic);
        }

        private static TilePosition CreatePosition(TileZone zone, int seat, int index)
        {
            switch (zone)
            {
                case TileZone.Unassigned:
                    return TilePosition.Unassigned;
                case TileZone.Wall:
                    return TilePosition.Wall(index);
                case TileZone.DeadWall:
                    return TilePosition.DeadWall(index);
                case TileZone.DeadWallReplacement:
                    return TilePosition.DeadWallReplacement(index);
                case TileZone.Hand:
                    return TilePosition.Hand(seat, index);
                case TileZone.Discard:
                    return TilePosition.Discard(seat, index);
                case TileZone.Meld:
                    return TilePosition.Meld(seat, index);
                case TileZone.Sleeve:
                    return TilePosition.Sleeve(seat, index);
                default:
                    throw new InvalidDataException("Tile position zone is invalid.");
            }
        }

        private static void WriteOutcome(BinaryWriter writer, RoundOutcome outcome)
        {
            writer.Write((byte)outcome.Kind);
            writer.Write(outcome.WinnerSeat);
            writer.Write(outcome.LoserSeat);
            writer.Write(outcome.TenpaiSeats.Count);
            for (int i = 0; i < outcome.TenpaiSeats.Count; i++)
            {
                writer.Write(outcome.TenpaiSeats[i]);
            }

            writer.Write(outcome.Reason);
        }

        private static RoundOutcome ReadOutcome(BinaryReader reader)
        {
            byte kind = reader.ReadByte();
            int winner = reader.ReadInt32();
            int loser = reader.ReadInt32();
            int tenpaiCount = reader.ReadInt32();
            if (tenpaiCount < 0 || tenpaiCount > 4)
            {
                throw new InvalidDataException("Outcome tenpai count is invalid.");
            }

            var tenpai = new int[tenpaiCount];
            for (int i = 0; i < tenpaiCount; i++)
            {
                tenpai[i] = reader.ReadInt32();
            }

            string reason = ReadString(reader);
            switch ((RoundOutcomeKind)kind)
            {
                case RoundOutcomeKind.DealerWin:
                    return RoundOutcome.DealerWin(winner);
                case RoundOutcomeKind.NonDealerWin:
                    return RoundOutcome.NonDealerWin(winner);
                case RoundOutcomeKind.Ron:
                    return RoundOutcome.Ron(winner, loser);
                case RoundOutcomeKind.Tsumo:
                    return RoundOutcome.Tsumo(winner);
                case RoundOutcomeKind.ExhaustiveDraw:
                    return RoundOutcome.ExhaustiveDraw(tenpai);
                case RoundOutcomeKind.AbortiveDraw:
                    return RoundOutcome.AbortiveDraw(string.IsNullOrWhiteSpace(reason) ? "abortive draw" : reason);
                default:
                    throw new InvalidDataException("Unknown outcome kind " + kind + ".");
            }
        }

        private static void WriteSettlement(BinaryWriter writer, RoundSettlement settlement)
        {
            writer.Write(settlement.ResultId);
            WriteOutcome(writer, settlement.Outcome);
            WriteLongs(writer, settlement.FinalScores);
            writer.Write(settlement.RemainingRiichiSticks);
            writer.Write(settlement.CompletedAtUtc.Value.UtcDateTime.Ticks);
            writer.Write(settlement.CompletedAtMonotonicMillis);
        }

        private static RoundSettlement ReadSettlement(BinaryReader reader)
        {
            string resultId = ReadString(reader);
            RoundOutcome outcome = ReadOutcome(reader);
            IReadOnlyList<long> final = ReadLongs(reader);
            int sticks = reader.ReadInt32();
            long ticks = reader.ReadInt64();
            long monotonic = reader.ReadInt64();
            return new RoundSettlement(
                resultId,
                outcome,
                final,
                sticks,
                new PersistedUtcTime(new DateTimeOffset(ticks, TimeSpan.Zero)),
                monotonic);
        }

        private static void WriteEvent(BinaryWriter writer, RoundEvent value)
        {
            writer.Write((byte)value.Type);
            writer.Write(value.Ordinal);
            writer.Write(value.Seat);
            writer.Write(value.Tile.HasValue);
            if (value.Tile.HasValue)
            {
                writer.Write(value.Tile.Value.Value);
            }

            writer.Write(value.Detail != null);
            if (value.Detail != null)
            {
                writer.Write(value.Detail);
            }
        }

        private static RoundEvent ReadEvent(BinaryReader reader)
        {
            byte type = reader.ReadByte();
            if (!Enum.IsDefined(typeof(RoundEventType), type))
            {
                throw new InvalidDataException("Round event type is invalid.");
            }

            int ordinal = reader.ReadInt32();
            int seat = reader.ReadInt32();
            TileId? tile = reader.ReadBoolean() ? ReadTile(reader) : (TileId?)null;
            string? detail = reader.ReadBoolean() ? ReadString(reader) : null;
            return new RoundEvent((RoundEventType)type, ordinal, seat, tile, detail);
        }

        private static void WriteLongs(BinaryWriter writer, IReadOnlyList<long> values)
        {
            writer.Write(values.Count);
            for (int i = 0; i < values.Count; i++)
            {
                writer.Write(values[i]);
            }
        }

        private static IReadOnlyList<long> ReadLongs(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > 4)
            {
                throw new InvalidDataException("Score vector length is invalid.");
            }

            var values = new long[count];
            for (int i = 0; i < count; i++)
            {
                values[i] = reader.ReadInt64();
            }

            return new ReadOnlyCollection<long>(values);
        }

        private static string ReadString(BinaryReader reader)
        {
            string value = reader.ReadString();
            if (value.Length > 4096)
            {
                throw new InvalidDataException("Recovery string exceeds the 4096-character limit.");
            }

            return value;
        }

        private static TileId ReadTile(BinaryReader reader)
        {
            int value = reader.ReadInt32();
            if (value < 0 || value >= TileId.Count)
            {
                throw new InvalidDataException("Tile id is out of range.");
            }

            return new TileId(value);
        }

        private static TilePosition ReadPosition(BinaryReader reader)
        {
            byte zone = reader.ReadByte();
            sbyte seat = reader.ReadSByte();
            short index = reader.ReadInt16();
            if (!Enum.IsDefined(typeof(TileZone), zone))
            {
                throw new InvalidDataException("Tile position zone is invalid.");
            }

            return CreatePosition((TileZone)zone, seat, index);
        }
    }

    /// <summary>
    /// Deterministic command journal encoding used by recovery replay. It records the exact
    /// caller-authorized command (including recorded settlement/eligible-seat inputs), not a
    /// fabricated candidate state.
    /// </summary>
    public static class RoundCommandCodec
    {
        public const int FormatVersion = 1;
        private const int Magic = 0x4D4A4331; // MJC1

        public static byte[] Encode(RoundCommand command)
        {
            if (command == null)
            {
                throw new ArgumentNullException(nameof(command));
            }

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(command.RoundId);
                writer.Write((byte)command.Action);
                writer.Write(command.Seat);
                writer.Write(command.Tile.HasValue);
                if (command.Tile.HasValue)
                {
                    writer.Write(command.Tile.Value.Value);
                }

                WriteSeats(writer, command.EligibleSeats);
                WriteSeats(writer, command.ChankanEligibleSeats);
                writer.Write(command.Settlement != null);
                if (command.Settlement != null)
                {
                    WriteSettlement(writer, command.Settlement);
                }

                writer.Flush();
                return stream.ToArray();
            }
        }

        public static RoundCommand Decode(byte[] payload)
        {
            if (payload == null || payload.Length < 8)
            {
                throw new InvalidDataException("Command journal payload is empty or truncated.");
            }

            using (var stream = new MemoryStream(payload, writable: false))
            using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true))
            {
                if (reader.ReadInt32() != Magic)
                {
                    throw new InvalidDataException("Command journal magic is invalid.");
                }

                int version = reader.ReadInt32();
                if (version != FormatVersion)
                {
                    throw new InvalidDataException("Unsupported command journal schema " + version + ".");
                }

                string roundId = reader.ReadString();
                byte action = reader.ReadByte();
                if (!Enum.IsDefined(typeof(RoundActionKind), action))
                {
                    throw new InvalidDataException("Command action is invalid.");
                }

                int seat = reader.ReadInt32();
                TileId? tile = reader.ReadBoolean() ? ReadTile(reader) : (TileId?)null;
                IReadOnlyList<int>? eligible = ReadSeats(reader);
                IReadOnlyList<int>? chankan = ReadSeats(reader);
                RoundSettlement? settlement = reader.ReadBoolean() ? ReadSettlement(reader) : null;
                if (stream.Position != payload.Length)
                {
                    throw new InvalidDataException("Command journal payload has trailing bytes.");
                }

                return Create((RoundActionKind)action, roundId, seat, tile, eligible, chankan, settlement);
            }
        }

        private static RoundCommand Create(
            RoundActionKind action,
            string roundId,
            int seat,
            TileId? tile,
            IReadOnlyList<int>? eligible,
            IReadOnlyList<int>? chankan,
            RoundSettlement? settlement)
        {
            switch (action)
            {
                case RoundActionKind.StartStacking:
                    return RoundCommand.StartStacking(roundId);
                case RoundActionKind.DrawTile:
                    return RoundCommand.Draw(roundId, seat);
                case RoundActionKind.DiscardTile:
                    if (!tile.HasValue)
                    {
                        throw new InvalidDataException("Discard command requires a tile.");
                    }

                    return RoundCommand.Discard(roundId, seat, tile.Value, eligible);
                case RoundActionKind.DeclareTsumo:
                    return RoundCommand.Tsumo(roundId, seat, RequireSettlement(settlement));
                case RoundActionKind.DeclareRon:
                    return RoundCommand.Ron(roundId, seat, RequireSettlement(settlement));
                case RoundActionKind.Pass:
                    return RoundCommand.Pass(roundId, seat);
                case RoundActionKind.DeclareAddedKan:
                    if (!tile.HasValue)
                    {
                        throw new InvalidDataException("Added-kan command requires a tile.");
                    }

                    return RoundCommand.AddedKan(roundId, seat, tile.Value, chankan);
                case RoundActionKind.DeclareExhaustiveDraw:
                    return RoundCommand.ExhaustiveDraw(roundId, RequireSettlement(settlement));
                case RoundActionKind.DeclareAbortiveDraw:
                    return RoundCommand.AbortiveDraw(roundId, RequireSettlement(settlement));
                default:
                    throw new InvalidDataException("Command action is invalid.");
            }
        }

        private static RoundSettlement RequireSettlement(RoundSettlement? settlement)
        {
            return settlement ?? throw new InvalidDataException("Command requires a settlement.");
        }

        private static void WriteSeats(BinaryWriter writer, IReadOnlyList<int>? seats)
        {
            writer.Write(seats != null);
            if (seats == null)
            {
                return;
            }

            writer.Write(seats.Count);
            for (int i = 0; i < seats.Count; i++)
            {
                writer.Write(seats[i]);
            }
        }

        private static IReadOnlyList<int>? ReadSeats(BinaryReader reader)
        {
            if (!reader.ReadBoolean())
            {
                return null;
            }

            int count = reader.ReadInt32();
            if (count < 0 || count > 4)
            {
                throw new InvalidDataException("Seat list length is invalid.");
            }

            var seats = new int[count];
            for (int i = 0; i < count; i++)
            {
                seats[i] = reader.ReadInt32();
            }

            return new ReadOnlyCollection<int>(seats);
        }

        private static void WriteSettlement(BinaryWriter writer, RoundSettlement settlement)
        {
            writer.Write(settlement.ResultId);
            writer.Write((byte)settlement.Outcome.Kind);
            writer.Write(settlement.Outcome.WinnerSeat);
            writer.Write(settlement.Outcome.LoserSeat);
            writer.Write(settlement.Outcome.TenpaiSeats.Count);
            for (int i = 0; i < settlement.Outcome.TenpaiSeats.Count; i++)
            {
                writer.Write(settlement.Outcome.TenpaiSeats[i]);
            }

            writer.Write(settlement.Outcome.Reason);
            writer.Write(settlement.FinalScores.Count);
            for (int i = 0; i < settlement.FinalScores.Count; i++)
            {
                writer.Write(settlement.FinalScores[i]);
            }

            writer.Write(settlement.RemainingRiichiSticks);
            writer.Write(settlement.CompletedAtUtc.Value.UtcDateTime.Ticks);
            writer.Write(settlement.CompletedAtMonotonicMillis);
        }

        private static RoundSettlement ReadSettlement(BinaryReader reader)
        {
            string resultId = reader.ReadString();
            byte kind = reader.ReadByte();
            int winner = reader.ReadInt32();
            int loser = reader.ReadInt32();
            int tenpaiCount = reader.ReadInt32();
            var tenpai = new int[tenpaiCount];
            for (int i = 0; i < tenpaiCount; i++)
            {
                tenpai[i] = reader.ReadInt32();
            }

            string reason = reader.ReadString();
            int scoreCount = reader.ReadInt32();
            var scores = new long[scoreCount];
            for (int i = 0; i < scoreCount; i++)
            {
                scores[i] = reader.ReadInt64();
            }

            int sticks = reader.ReadInt32();
            long ticks = reader.ReadInt64();
            long monotonic = reader.ReadInt64();
            RoundOutcome outcome;
            switch ((RoundOutcomeKind)kind)
            {
                case RoundOutcomeKind.DealerWin:
                    outcome = RoundOutcome.DealerWin(winner);
                    break;
                case RoundOutcomeKind.NonDealerWin:
                    outcome = RoundOutcome.NonDealerWin(winner);
                    break;
                case RoundOutcomeKind.Ron:
                    outcome = RoundOutcome.Ron(winner, loser);
                    break;
                case RoundOutcomeKind.Tsumo:
                    outcome = RoundOutcome.Tsumo(winner);
                    break;
                case RoundOutcomeKind.ExhaustiveDraw:
                    outcome = RoundOutcome.ExhaustiveDraw(tenpai);
                    break;
                case RoundOutcomeKind.AbortiveDraw:
                    outcome = RoundOutcome.AbortiveDraw(string.IsNullOrWhiteSpace(reason) ? "abortive draw" : reason);
                    break;
                default:
                    throw new InvalidDataException("Settlement outcome kind is invalid.");
            }

            return new RoundSettlement(
                resultId,
                outcome,
                scores,
                sticks,
                new PersistedUtcTime(new DateTimeOffset(ticks, TimeSpan.Zero)),
                monotonic);
        }

        private static TileId ReadTile(BinaryReader reader)
        {
            int value = reader.ReadInt32();
            if (value < 0 || value >= TileId.Count)
            {
                throw new InvalidDataException("Tile id is out of range.");
            }

            return new TileId(value);
        }
    }
}
