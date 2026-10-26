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
        private readonly ReadOnlyCollection<long> _scores;
        private readonly ReadOnlyCollection<bool> _riichiDeclared;
        private readonly ReadOnlyCollection<RoundMeld> _melds;

        public RoundRecoveryState(
            RoundSpec spec,
            long version,
            RoundPhase phase,
            int currentSeat,
            TilePositionMap tiles,
            RoundResponseWindow? window,
            RoundResult? result,
            IReadOnlyList<RoundEvent> events,
            ulong sourceFingerprint,
            IReadOnlyList<long>? scores = null,
            int? riichiSticks = null,
            IReadOnlyList<bool>? riichiDeclared = null,
            IReadOnlyList<RoundMeld>? melds = null,
            int pendingKanDoraCount = 0)
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

            var scoreCopy = new long[4];
            IReadOnlyList<long> scoreSource = scores ?? spec.Scores;
            if (scoreSource.Count != 4)
            {
                throw new ArgumentException("Scores must contain exactly four entries.", nameof(scores));
            }

            for (int i = 0; i < 4; i++)
            {
                scoreCopy[i] = scoreSource[i];
            }

            _scores = new ReadOnlyCollection<long>(scoreCopy);
            RiichiSticks = riichiSticks ?? spec.RiichiSticks;
            var flags = new bool[4];
            if (riichiDeclared != null)
            {
                if (riichiDeclared.Count != 4)
                {
                    throw new ArgumentException("Riichi flags must contain exactly four entries.", nameof(riichiDeclared));
                }

                for (int i = 0; i < 4; i++)
                {
                    flags[i] = riichiDeclared[i];
                }
            }

            _riichiDeclared = new ReadOnlyCollection<bool>(flags);
            var meldCopy = new List<RoundMeld>();
            if (melds != null)
            {
                for (int i = 0; i < melds.Count; i++)
                {
                    meldCopy.Add(melds[i] ?? throw new ArgumentException("Meld cannot be null.", nameof(melds)));
                }
            }

            _melds = new ReadOnlyCollection<RoundMeld>(meldCopy);
            if (pendingKanDoraCount < 0 || pendingKanDoraCount > 5)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(pendingKanDoraCount),
                    pendingKanDoraCount,
                    "Pending kan dora must be 0..5.");
            }

            PendingKanDoraCount = pendingKanDoraCount;
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

        public IReadOnlyList<long> Scores => _scores;

        public int RiichiSticks { get; }

        public IReadOnlyList<bool> RiichiDeclared => _riichiDeclared;

        public IReadOnlyList<RoundMeld> Melds => _melds;

        public int PendingKanDoraCount { get; }

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
                round.SourceFingerprint,
                round.Scores,
                round.RiichiSticks,
                round.RiichiDeclared,
                round.Melds,
                round.PendingKanDoraCount);
        }

        public static RoundStateMachine Restore(RoundRecoveryState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            ValidateSpec(state.Spec);
            ValidateEvents(state.Events);
            ValidateDecisionState(state);
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
                null,
                state.Scores,
                state.RiichiSticks,
                state.RiichiDeclared,
                state.Melds,
                state.PendingKanDoraCount);

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

        private static void ValidateDecisionState(RoundRecoveryState state)
        {
            if (state.Scores.Count != 4 || state.RiichiDeclared.Count != 4)
            {
                throw new InvalidDataException("Round decision state requires four scores and four riichi flags.");
            }

            long startingTotal = 0;
            long currentTotal = 0;
            for (int i = 0; i < 4; i++)
            {
                startingTotal += state.Spec.Scores[i];
                currentTotal += state.Scores[i];
            }

            startingTotal += (long)state.Spec.RiichiSticks * 1000L;
            currentTotal += (long)state.RiichiSticks * 1000L;
            if (startingTotal != currentTotal)
            {
                throw new InvalidDataException(
                    "Recovered scores/sticks do not conserve the round-start total.");
            }

            if (state.RiichiSticks < 0)
            {
                throw new InvalidDataException("Recovered riichi sticks are negative.");
            }

            if (state.PendingKanDoraCount < 0 || state.PendingKanDoraCount > 5)
            {
                throw new InvalidDataException("Recovered pending kan-dora count is invalid.");
            }

            var seenTiles = new HashSet<int>();
            var perSeat = new int[4];
            for (int i = 0; i < state.Melds.Count; i++)
            {
                RoundMeld meld = state.Melds[i];
                if (meld.Seat < 0 || meld.Seat > 3 || ++perSeat[meld.Seat] > 4)
                {
                    throw new InvalidDataException("Recovered meld seat/count is invalid.");
                }

                for (int t = 0; t < meld.Tiles.Count; t++)
                {
                    TileId tile = meld.Tiles[t];
                    if (!seenTiles.Add(tile.Value))
                    {
                        throw new InvalidDataException("A recovered physical tile belongs to two melds.");
                    }

                    TilePosition position = state.Tiles[tile];
                    if (position.Zone != TileZone.Meld || position.Seat != meld.Seat)
                    {
                        throw new InvalidDataException(
                            "Recovered meld tile " + tile + " is not in seat " + meld.Seat + "'s meld zone.");
                    }
                }

                bool hasEvent = false;
                for (int e = 0; e < state.Events.Count; e++)
                {
                    RoundEvent value = state.Events[e];
                    if (value.Seat == meld.Seat &&
                        (value.Type == RoundEventType.ChiDeclared ||
                         value.Type == RoundEventType.PonDeclared ||
                         value.Type == RoundEventType.KanDeclared))
                    {
                        hasEvent = true;
                        break;
                    }
                }

                if (!hasEvent)
                {
                    throw new InvalidDataException("Recovered meld has no matching declared event.");
                }
            }

            for (int seat = 0; seat < 4; seat++)
            {
                if (!state.RiichiDeclared[seat])
                {
                    continue;
                }

                bool declared = false;
                for (int e = 0; e < state.Events.Count; e++)
                {
                    if (state.Events[e].Type == RoundEventType.RiichiDeclared &&
                        state.Events[e].Seat == seat)
                    {
                        declared = true;
                        break;
                    }
                }

                if (!declared)
                {
                    throw new InvalidDataException("Recovered riichi flag has no RiichiDeclared event.");
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

                if (!Enum.IsDefined(typeof(RoundClaimKind), claim.Kind))
                {
                    throw new InvalidDataException("A response claim kind is invalid.");
                }

                int expectedTiles = claim.Kind == RoundClaimKind.Chi || claim.Kind == RoundClaimKind.Pon
                    ? 2
                    : claim.Kind == RoundClaimKind.OpenKan ? 3 : 0;
                if (claim.MeldTiles.Count != expectedTiles)
                {
                    throw new InvalidDataException("A response claim carries the wrong number of meld tiles.");
                }

                var claimTiles = new HashSet<int>();
                for (int t = 0; t < claim.MeldTiles.Count; t++)
                {
                    if (!claimTiles.Add(claim.MeldTiles[t].Value))
                    {
                        throw new InvalidDataException("A response claim repeats a physical tile.");
                    }
                }

                ValidateClaimShape(claim, window.TargetTile);

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

        private static void ValidateClaimShape(RoundResponseClaim claim, TileId target)
        {
            if (claim.Kind == RoundClaimKind.Chi)
            {
                var ordered = new List<TileId>
                {
                    claim.MeldTiles[0],
                    claim.MeldTiles[1],
                    target
                };
                ordered.Sort();
                if (!ordered[0].IsNumberTile ||
                    ordered[0].Suit != ordered[1].Suit ||
                    ordered[1].Suit != ordered[2].Suit ||
                    ordered[1].Rank != ordered[0].Rank + 1 ||
                    ordered[2].Rank != ordered[1].Rank + 1)
                {
                    throw new InvalidDataException("A recovered Chi claim is not a consecutive sequence.");
                }

                return;
            }

            if (claim.Kind == RoundClaimKind.Pon || claim.Kind == RoundClaimKind.OpenKan)
            {
                for (int i = 0; i < claim.MeldTiles.Count; i++)
                {
                    if (!claim.MeldTiles[i].SameType(target))
                    {
                        throw new InvalidDataException("A recovered Pon/OpenKan claim has a wrong tile type.");
                    }
                }
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

            if (result.StartingRiichiSticks != spec.RiichiSticks)
            {
                throw new InvalidDataException("A round result starting sticks do not match its spec.");
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

            startingTotal += (long)result.StartingRiichiSticks * 1000L;
            finalTotal += (long)result.RemainingRiichiSticks * 1000L;
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
        public const int FormatVersion = 2;
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
                WriteLongs(writer, state.Scores);
                writer.Write(state.RiichiSticks);
                for (int i = 0; i < 4; i++)
                {
                    writer.Write(state.RiichiDeclared[i]);
                }

                writer.Write(state.Melds.Count);
                for (int i = 0; i < state.Melds.Count; i++)
                {
                    RoundMeld meld = state.Melds[i];
                    writer.Write((byte)meld.Kind);
                    writer.Write(meld.Seat);
                    writer.Write(meld.FromSeat);
                    writer.Write(meld.CalledTile.Value);
                    writer.Write(meld.Tiles.Count);
                    for (int t = 0; t < meld.Tiles.Count; t++)
                    {
                        writer.Write(meld.Tiles[t].Value);
                    }
                }

                writer.Write(state.PendingKanDoraCount);
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
                IReadOnlyList<long> scores = ReadLongs(reader);
                int riichiSticks = reader.ReadInt32();
                var riichiDeclared = new bool[4];
                for (int i = 0; i < 4; i++)
                {
                    riichiDeclared[i] = reader.ReadBoolean();
                }

                int meldCount = reader.ReadInt32();
                if (meldCount < 0 || meldCount > 16)
                {
                    throw new InvalidDataException("Round recovery meld count is invalid.");
                }

                var melds = new List<RoundMeld>(meldCount);
                for (int i = 0; i < meldCount; i++)
                {
                    byte kind = reader.ReadByte();
                    if (!Enum.IsDefined(typeof(RoundMeldKind), kind))
                    {
                        throw new InvalidDataException("Round recovery meld kind is invalid.");
                    }

                    int meldSeat = reader.ReadInt32();
                    int fromSeat = reader.ReadInt32();
                    TileId called = ReadTile(reader);
                    int tileCount = reader.ReadInt32();
                    if (tileCount < 3 || tileCount > 4)
                    {
                        throw new InvalidDataException("Round recovery meld tile count is invalid.");
                    }

                    var tiles = new TileId[tileCount];
                    for (int t = 0; t < tileCount; t++)
                    {
                        tiles[t] = ReadTile(reader);
                    }

                    melds.Add(new RoundMeld((RoundMeldKind)kind, meldSeat, fromSeat, called, tiles));
                }

                int pendingKanDora = reader.ReadInt32();
                TilePositionMap tilesMap = ReadTiles(reader);
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
                    tilesMap,
                    window,
                    result,
                    events,
                    fingerprint,
                    scores,
                    riichiSticks,
                    riichiDeclared,
                    melds,
                    pendingKanDora);
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
                writer.Write((byte)claim.Kind);
                writer.Write(claim.Seat);
                if (claim.Kind == RoundClaimKind.Ron)
                {
                    writer.Write(claim.Settlement != null);
                    if (claim.Settlement != null)
                    {
                        WriteSettlement(writer, claim.Settlement);
                    }
                }
                else
                {
                    writer.Write(claim.MeldTiles.Count);
                    for (int t = 0; t < claim.MeldTiles.Count; t++)
                    {
                        writer.Write(claim.MeldTiles[t].Value);
                    }
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
                byte kindByte = reader.ReadByte();
                if (!Enum.IsDefined(typeof(RoundClaimKind), kindByte))
                {
                    throw new InvalidDataException("A response claim kind is invalid.");
                }

                RoundClaimKind kind = (RoundClaimKind)kindByte;
                int seat = reader.ReadInt32();
                if (kind == RoundClaimKind.Ron)
                {
                    if (!reader.ReadBoolean())
                    {
                        throw new InvalidDataException("A Ron claim requires a settlement.");
                    }

                    claims.Add(RoundResponseClaim.Ron(seat, ReadSettlement(reader)));
                }
                else
                {
                    int tileCount = reader.ReadInt32();
                    if (tileCount < 0 || tileCount > 4)
                    {
                        throw new InvalidDataException("A response claim tile count is invalid.");
                    }

                    var tiles = new TileId[tileCount];
                    for (int t = 0; t < tileCount; t++)
                    {
                        tiles[t] = ReadTile(reader);
                    }

                    switch (kind)
                    {
                        case RoundClaimKind.Pass:
                            if (tileCount != 0)
                            {
                                throw new InvalidDataException("A Pass claim cannot carry tiles.");
                            }

                            claims.Add(RoundResponseClaim.Pass(seat));
                            break;
                        case RoundClaimKind.Chi:
                            if (tileCount != 2)
                            {
                                throw new InvalidDataException("A Chi claim requires two tiles.");
                            }

                            claims.Add(RoundResponseClaim.Chi(seat, tiles[0], tiles[1]));
                            break;
                        case RoundClaimKind.Pon:
                            if (tileCount != 2)
                            {
                                throw new InvalidDataException("A Pon claim requires two tiles.");
                            }

                            claims.Add(RoundResponseClaim.Pon(seat, tiles[0], tiles[1]));
                            break;
                        case RoundClaimKind.OpenKan:
                            if (tileCount != 3)
                            {
                                throw new InvalidDataException("An OpenKan claim requires three tiles.");
                            }

                            claims.Add(RoundResponseClaim.OpenKan(seat, tiles[0], tiles[1], tiles[2]));
                            break;
                        default:
                            throw new InvalidDataException("Unknown response claim kind.");
                    }
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
        /// <summary>
        /// Command payloads remain schema 1. N-1a appends an optional meld-tile block after the
        /// original fields, so pre-N-1a journals still decode (with no meld tiles) and a new
        /// chi/pon/kan command without that block is rejected explicitly at Prepare time.
        /// </summary>
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

                WriteMeldTiles(writer, command.MeldTiles);

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
                IReadOnlyList<TileId>? meldTiles = stream.Position < payload.Length
                    ? ReadMeldTiles(reader)
                    : null;
                if (stream.Position != payload.Length)
                {
                    throw new InvalidDataException("Command journal payload has trailing bytes.");
                }

                return Create((RoundActionKind)action, roundId, seat, tile, eligible, chankan, settlement, meldTiles);
            }
        }

        private static RoundCommand Create(
            RoundActionKind action,
            string roundId,
            int seat,
            TileId? tile,
            IReadOnlyList<int>? eligible,
            IReadOnlyList<int>? chankan,
            RoundSettlement? settlement,
            IReadOnlyList<TileId>? meldTiles)
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
                case RoundActionKind.DeclareChi:
                    RequireMeldTiles(meldTiles, 2, "Chi");
                    if (!tile.HasValue)
                    {
                        throw new InvalidDataException("Chi command requires the called tile.");
                    }

                    return RoundCommand.Chi(roundId, seat, tile.Value, meldTiles![0], meldTiles[1]);
                case RoundActionKind.DeclarePon:
                    RequireMeldTiles(meldTiles, 2, "Pon");
                    if (!tile.HasValue)
                    {
                        throw new InvalidDataException("Pon command requires the called tile.");
                    }

                    return RoundCommand.Pon(roundId, seat, tile.Value, meldTiles![0], meldTiles[1]);
                case RoundActionKind.DeclareOpenKan:
                    RequireMeldTiles(meldTiles, 3, "Open kan");
                    if (!tile.HasValue)
                    {
                        throw new InvalidDataException("Open-kan command requires the called tile.");
                    }

                    return RoundCommand.OpenKan(
                        roundId,
                        seat,
                        tile.Value,
                        meldTiles![0],
                        meldTiles[1],
                        meldTiles[2]);
                case RoundActionKind.DeclareClosedKan:
                    RequireMeldTiles(meldTiles, 4, "Closed kan");
                    return RoundCommand.ClosedKan(
                        roundId,
                        seat,
                        meldTiles![0],
                        meldTiles[1],
                        meldTiles[2],
                        meldTiles[3]);
                case RoundActionKind.DeclareRiichi:
                    if (!tile.HasValue)
                    {
                        throw new InvalidDataException("Riichi command requires the discard tile.");
                    }

                    return RoundCommand.Riichi(roundId, seat, tile.Value, eligible);
                default:
                    throw new InvalidDataException("Command action is invalid.");
            }
        }

        private static void RequireMeldTiles(IReadOnlyList<TileId>? tiles, int expected, string action)
        {
            if (tiles == null || tiles.Count != expected)
            {
                throw new InvalidDataException(action + " command requires " + expected + " meld tiles.");
            }
        }

        private static RoundSettlement RequireSettlement(RoundSettlement? settlement)
        {
            return settlement ?? throw new InvalidDataException("Command requires a settlement.");
        }

        private static void WriteMeldTiles(BinaryWriter writer, IReadOnlyList<TileId>? tiles)
        {
            writer.Write(tiles != null);
            if (tiles == null)
            {
                return;
            }

            writer.Write(tiles.Count);
            for (int i = 0; i < tiles.Count; i++)
            {
                writer.Write(tiles[i].Value);
            }
        }

        private static IReadOnlyList<TileId>? ReadMeldTiles(BinaryReader reader)
        {
            if (!reader.ReadBoolean())
            {
                return null;
            }

            int count = reader.ReadInt32();
            if (count < 0 || count > 4)
            {
                throw new InvalidDataException("Command meld tile count is invalid.");
            }

            var tiles = new TileId[count];
            for (int i = 0; i < count; i++)
            {
                tiles[i] = ReadTile(reader);
            }

            return new ReadOnlyCollection<TileId>(tiles);
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
