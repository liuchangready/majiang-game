using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Mahjong.Domain;
using Mahjong.Domain.Cheat;
using Xunit;

namespace Mahjong.Domain.Tests
{
    /// <summary>
    /// N3 cheat-v1 consumer binding. Unlike the original P1.4 structural replay, every case in
    /// this file is driven through the real C# Domain entry and every asserted value is read
    /// from <c>tests/v5-golden/cheat-v1.json</c> first. The exported file is the only oracle.
    ///
    /// The V4 table reconstruction below mirrors <c>engine/src/testFixtures/kotlin/com/majiang/
    /// testing/TestTableBuilder.kt</c>: reserve explicit hands/drawn/wall-prefix/dead-wall-prefix,
    /// fill each seat to 14 (current player in DISCARD/CHEAT_WINDOW) or 13, fill the dead wall to
    /// 14, then append the remaining tiles to the live wall. The control case proves the
    /// reconstruction against the M5 <c>inputPlacements</c> exported by V4.
    /// </summary>
    public class CheatGoldenBindingTests
    {
        private const string Schema = "v5-cheat-golden-v1";
        private const int WallSlotOffset = 53;
        private const string RoundId = "r-golden";
        private const int TurnNumber = 12;

        private static readonly string[] ActivationKeys =
        {
            "seat", "drawKind", "drawnTileId", "nextWallTileId", "nextDeadWallTileId",
            "wallPointer", "wallEndExclusive", "rinshanPointer", "cheatPoints", "cost"
        };

        private static readonly string[] ChoiceKeys =
        {
            "seat", "keep", "offerId", "turnNumber", "offerCreatedTurnNumber", "drawKind",
            "drawnTileId", "offerFirstTileId", "offerSecondTileId", "offerSecondWallIndex",
            "nextAvailableWallIndex", "wallPointer", "wallEndExclusive", "rinshanPointer", "cost"
        };

        private static readonly string[] OffTurnActivationKeys =
        {
            "seat", "currentPlayer", "phase", "windowId", "availableWallCount", "cheatPoints", "cost"
        };

        private static readonly string[] OffTurnChoiceKeys =
        {
            "seat", "keep", "offerId", "now", "deadlineMillis", "offerFirstInWall", "offerSecondInWall"
        };

        // ------------------------------------------------------------------
        // Golden loading. V5_CHEAT_GOLDEN_PATH lets the external mutation probe point the
        // consumer at an isolated TEMP copy without touching the repository fixture.
        // ------------------------------------------------------------------

        private static string RepoRoot()
        {
            DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return dir!.FullName;
        }

        private static string GoldenPath()
        {
            string? overridePath = Environment.GetEnvironmentVariable("V5_CHEAT_GOLDEN_PATH");
            return string.IsNullOrWhiteSpace(overridePath)
                ? Path.Combine(RepoRoot(), "tests", "v5-golden", "cheat-v1.json")
                : Path.GetFullPath(overridePath);
        }

        private static JsonElement Golden()
        {
            string path = GoldenPath();
            Assert.True(File.Exists(path), "golden fixture not found: " + path);
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(path));
            return doc.RootElement.Clone();
        }

        private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;

        private static int Int(JsonElement e, string name) => e.GetProperty(name).GetInt32();

        private static long Long(JsonElement e, string name) => e.GetProperty(name).GetInt64();

        private static bool Bool(JsonElement e, string name) => e.GetProperty(name).GetBoolean();
        private static void AssertKeys(JsonElement e, params string[] expected)
        {
            string[] actual = e.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            string[] wanted = expected.OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.Equal(wanted, actual);
        }

        private static string GoldenString(JsonElement e, string name)
        {
            JsonElement value = e.GetProperty(name);
            Assert.Equal(JsonValueKind.String, value.ValueKind);
            return value.GetString()!;
        }

        private static int GoldenInt(JsonElement e, string name)
        {
            JsonElement value = e.GetProperty(name);
            Assert.Equal(JsonValueKind.Number, value.ValueKind);
            return value.GetInt32();
        }

        private static long GoldenLong(JsonElement e, string name)
        {
            JsonElement value = e.GetProperty(name);
            Assert.Equal(JsonValueKind.Number, value.ValueKind);
            return value.GetInt64();
        }

        private static bool GoldenBool(JsonElement e, string name)
        {
            JsonElement value = e.GetProperty(name);
            Assert.Equal(JsonValueKind.True, value.ValueKind);
            return true;
        }

        private static int? GoldenNullableInt(JsonElement e, string name, bool expectNull)
        {
            JsonElement value = e.GetProperty(name);
            if (expectNull)
            {
                Assert.Equal(JsonValueKind.Null, value.ValueKind);
                return null;
            }

            Assert.Equal(JsonValueKind.Number, value.ValueKind);
            return value.GetInt32();
        }

        private static T GoldenEnum<T>(JsonElement e, string name) where T : struct, Enum
        {
            string text = GoldenString(e, name);
            string[] names = Enum.GetNames(typeof(T));
            string? canonical = names.FirstOrDefault(candidate =>
                string.Equals(candidate, text, StringComparison.OrdinalIgnoreCase));
            Assert.True(canonical != null,
                "unknown " + typeof(T).Name + " token: '" + text + "'; allowed names: " + string.Join(",", names));
            return (T)Enum.Parse(typeof(T), canonical!, false);
        }

        private static int[] Ints(JsonElement e, string name) =>
            e.GetProperty(name).EnumerateArray().Select(x => x.GetInt32()).ToArray();

        private static JsonElement[] Cases(JsonElement root, string section) =>
            root.GetProperty(section).EnumerateArray().ToArray();

        private static JsonElement Case(JsonElement root, string section, string caseId) =>
            Cases(root, section).Single(c => Str(c, "caseId") == caseId);

        private static string FixturePath(string section, string caseId) =>
            "$." + section + "[caseId=\"" + caseId + "\"]";

        // ------------------------------------------------------------------
        // V4 TestTableBuilder reconstruction (movement cases + M5 control).
        // ------------------------------------------------------------------

        private sealed class V4Spec
        {
            public Dictionary<int, List<int>> Hands { get; } = new Dictionary<int, List<int>>();

            public (int Seat, int Tile)? Drawn { get; set; }

            public List<int> WallPrefix { get; } = new List<int>();

            public List<int> DeadWallPrefix { get; } = new List<int>();

            public Dictionary<int, List<int>> Discards { get; } = new Dictionary<int, List<int>>();

            public RoundPhase Phase { get; set; } = RoundPhase.Discard;

            public int CurrentPlayer { get; set; }

            public int WallPointerV4 { get; set; }

            public int RinshanPointer { get; set; }

            public (int Tile, int Seat)? LastDiscard { get; set; }

            public (int Tile, int Seat, int DeadWallIndex)? LastRinshan { get; set; }

            public int[] Dora { get; set; } = Array.Empty<int>();

            public int KanCount { get; set; }

            public DrawSource DrawnSource { get; set; } = DrawSource.Wall;

            /// <summary>
            /// C# live-wall slots are 53..121 (69 slots). A V4 fixture with only 52 dealt
            /// hand tiles has a 70-entry wall before the next draw; one unexported filler is
            /// projected to a river tile so the canonical 136-position map stays valid.
            /// </summary>
            public int WallLimit { get; set; }
        }

        private static TilePositionMap BuildV4(V4Spec spec)
        {
            var reserved = new bool[TileId.Count];
            var hands = new List<int>[4];
            for (int seat = 0; seat < 4; seat++)
            {
                hands[seat] = spec.Hands.TryGetValue(seat, out List<int>? explicitHand)
                    ? new List<int>(explicitHand)
                    : new List<int>();
            }

            void Reserve(int tile)
            {
                if (tile < 0 || tile >= TileId.Count || reserved[tile])
                {
                    throw new InvalidOperationException("duplicate or invalid fixture tile: " + tile);
                }

                reserved[tile] = true;
            }

            for (int seat = 0; seat < 4; seat++)
            {
                for (int i = 0; i < hands[seat].Count; i++)
                {
                    Reserve(hands[seat][i]);
                }
            }

            if (spec.Drawn.HasValue)
            {
                Reserve(spec.Drawn.Value.Tile);
            }

            for (int i = 0; i < spec.WallPrefix.Count; i++)
            {
                Reserve(spec.WallPrefix[i]);
            }

            for (int i = 0; i < spec.DeadWallPrefix.Count; i++)
            {
                Reserve(spec.DeadWallPrefix[i]);
            }

            for (int seat = 0; seat < 4; seat++)
            {
                if (spec.Discards.TryGetValue(seat, out List<int>? discards))
                {
                    for (int i = 0; i < discards.Count; i++)
                    {
                        Reserve(discards[i]);
                    }
                }
            }

            int next = 0;
            int NextUnused()
            {
                while (next < TileId.Count && reserved[next])
                {
                    next++;
                }

                if (next >= TileId.Count)
                {
                    throw new InvalidOperationException("fixture exhausted the 136-tile set.");
                }

                int tile = next;
                reserved[tile] = true;
                next++;
                return tile;
            }

            for (int seat = 0; seat < 4; seat++)
            {
                bool current = seat == spec.CurrentPlayer &&
                    (spec.Phase == RoundPhase.Discard || spec.Phase == RoundPhase.CheatWindow);
                int drawnForSeat = spec.Drawn.HasValue && spec.Drawn.Value.Seat == seat ? 1 : 0;
                int target = (current ? 14 : 13) - drawnForSeat;
                while (hands[seat].Count < target)
                {
                    hands[seat].Add(NextUnused());
                }

                if (hands[seat].Count > target)
                {
                    throw new InvalidOperationException("seat " + seat + " fixture hand exceeds target " + target);
                }
            }

            var deadWall = new List<int>(spec.DeadWallPrefix);
            while (deadWall.Count < TilePositionMap.DeadWallSlotCount)
            {
                deadWall.Add(NextUnused());
            }

            var wall = new List<int>(spec.WallPrefix);
            while (next < TileId.Count)
            {
                wall.Add(NextUnused());
            }

            var extraRiver = new List<int>();
            if (spec.WallLimit > 0)
            {
                while (wall.Count > spec.WallLimit)
                {
                    int index = Math.Min(spec.WallPrefix.Count, wall.Count - 1);
                    int filler = wall[index];
                    wall.RemoveAt(index);
                    extraRiver.Add(filler);
                }
            }

            var placements = new List<TilePlacement>(TileId.Count);
            for (int seat = 0; seat < 4; seat++)
            {
                for (int i = 0; i < hands[seat].Count; i++)
                {
                    placements.Add(new TilePlacement(
                        new TileId(hands[seat][i]), TilePosition.Hand(seat, i)));
                }

                if (spec.Drawn.HasValue && spec.Drawn.Value.Seat == seat)
                {
                    placements.Add(new TilePlacement(
                        new TileId(spec.Drawn.Value.Tile),
                        TilePosition.Hand(seat, hands[seat].Count)));
                }

                if (spec.Discards.TryGetValue(seat, out List<int>? discards))
                {
                    for (int i = 0; i < discards.Count; i++)
                    {
                        placements.Add(new TilePlacement(
                            new TileId(discards[i]), TilePosition.Discard(seat, i)));
                    }
                }
            }

            int extraBase = spec.Discards.TryGetValue(3, out List<int>? seat3Explicit)
                ? seat3Explicit.Count
                : 0;
            for (int i = 0; i < extraRiver.Count; i++)
            {
                placements.Add(new TilePlacement(
                    new TileId(extraRiver[i]), TilePosition.Discard(3, extraBase + i)));
            }

            for (int i = 0; i < wall.Count; i++)
            {
                placements.Add(new TilePlacement(
                    new TileId(wall[i]), TilePosition.Wall(WallSlotOffset + i)));
            }

            for (int i = 0; i < deadWall.Count; i++)
            {
                placements.Add(new TilePlacement(
                    new TileId(deadWall[i]), TilePosition.DeadWall(i)));
            }

            LastDiscardEvidence? lastDiscard = null;
            if (spec.LastDiscard.HasValue)
            {
                int tile = spec.LastDiscard.Value.Tile;
                TilePlacement placement = placements.Single(p => p.Tile.Value == tile);
                lastDiscard = new LastDiscardEvidence(new TileId(tile), placement.Position);
            }
            else
            {
                for (int seat = 0; seat < 4; seat++)
                {
                    if (spec.Discards.TryGetValue(seat, out List<int>? d) && d.Count > 0)
                    {
                        int tile = d[d.Count - 1];
                        lastDiscard = new LastDiscardEvidence(
                            new TileId(tile), TilePosition.Discard(seat, d.Count - 1));
                    }
                }
            }

            RinshanDrawEvidence? rinshan = spec.LastRinshan.HasValue
                ? new RinshanDrawEvidence(
                    new TileId(spec.LastRinshan.Value.Tile),
                    spec.LastRinshan.Value.Seat,
                    spec.LastRinshan.Value.DeadWallIndex)
                : (RinshanDrawEvidence?)null;

            DrawnTileEvidence? drawn = spec.Drawn.HasValue
                ? new DrawnTileEvidence(
                    new TileId(spec.Drawn.Value.Tile), spec.Drawn.Value.Seat, spec.DrawnSource)
                : (DrawnTileEvidence?)null;

            return TilePositionMap.FromPlacements(
                placements,
                WallSlotOffset + spec.WallPointerV4,
                WallSlotOffset + wall.Count,
                spec.RinshanPointer,
                lastDiscard,
                0,
                rinshan,
                spec.Dora.Select(d => new TileId(d)).ToArray(),
                spec.KanCount,
                drawn);
        }

        private static string FactLabel(JsonElement fact)
        {
            string container = Str(fact, "container");
            string seat = fact.GetProperty("seat").ValueKind == JsonValueKind.Null
                ? "-"
                : Int(fact, "seat").ToString(CultureInfo.InvariantCulture);
            string index = fact.GetProperty("index").ValueKind == JsonValueKind.Null
                ? "-"
                : Int(fact, "index").ToString(CultureInfo.InvariantCulture);
            return container + "/seat=" + seat + "/index=" + index;
        }

        private enum FactSchema
        {
            FactsAfter,
            InputPlacements
        }

        private static void AssertFact(
            TilePositionMap map, JsonElement fact, string label, FactSchema schema)
        {
            int tileId = Int(fact, "tileId");
            string container = Str(fact, "container");
            bool seatless = container == "wall" || container == "deadWall" || container == "replacement";
            JsonElement seatElement = fact.GetProperty("seat");
            JsonElement indexElement = fact.GetProperty("index");
            if (schema == FactSchema.FactsAfter)
            {
                if (seatless)
                {
                    Assert.Equal(JsonValueKind.Null, seatElement.ValueKind);
                    Assert.Equal(JsonValueKind.Number, indexElement.ValueKind);
                }
                else
                {
                    Assert.Equal(JsonValueKind.Number, seatElement.ValueKind);
                    Assert.Equal(JsonValueKind.Null, indexElement.ValueKind);
                }
            }
            else
            {
                if (seatless)
                {
                    Assert.Equal(-1, seatElement.GetInt32());
                }
                else
                {
                    Assert.Equal(JsonValueKind.Number, seatElement.ValueKind);
                }

                Assert.Equal(JsonValueKind.Number, indexElement.ValueKind);
            }

            TilePosition position = map.PositionOf(new TileId(tileId));
            switch (container)
            {
                case "hand":
                    Assert.True(position.Zone == TileZone.Hand && position.Seat == Int(fact, "seat"),
                        label + ": tile " + tileId + " expected hand seat " + Int(fact, "seat") +
                        " but was " + position);
                    if (schema == FactSchema.InputPlacements)
                    {
                        Assert.Equal(Int(fact, "index"), position.Index);
                    }

                    break;
                case "discard":
                    Assert.True(position.Zone == TileZone.Discard && position.Seat == Int(fact, "seat"),
                        label + ": tile " + tileId + " expected discard seat " + Int(fact, "seat") +
                        " but was " + position);
                    if (schema == FactSchema.InputPlacements)
                    {
                        Assert.Equal(Int(fact, "index"), position.Index);
                    }

                    break;
                case "wall":
                    Assert.True(position.Zone == TileZone.Wall &&
                        position.Index == WallSlotOffset + Int(fact, "index"),
                        label + ": tile " + tileId + " expected wall[" + Int(fact, "index") +
                        "] (C# " + (WallSlotOffset + Int(fact, "index")) + ") but was " + position);
                    break;
                case "deadWall":
                    Assert.True(position.Zone == TileZone.DeadWall && position.Index == Int(fact, "index"),
                        label + ": tile " + tileId + " expected deadWall[" + Int(fact, "index") +
                        "] but was " + position);
                    break;
                case "replacement":
                    Assert.True(position.Zone == TileZone.DeadWallReplacement && position.Index == Int(fact, "index"),
                        label + ": tile " + tileId + " expected replacement[" + Int(fact, "index") +
                        "] but was " + position);
                    break;
                default:
                    throw new InvalidOperationException("unknown fact container: " + container);
            }
        }

        private static void AssertFacts(
            TilePositionMap map, JsonElement movementCase, string arrayName, FactSchema schema)
        {
            JsonElement[] facts = movementCase.GetProperty(arrayName).EnumerateArray().ToArray();
            Assert.NotEmpty(facts);
            for (int i = 0; i < facts.Length; i++)
            {
                AssertFact(map, facts[i], arrayName + "[" + i + "] " + FactLabel(facts[i]), schema);
            }
        }

        private static JsonElement MovementFact(JsonElement movementCase, string arrayName, int tileId) =>
            movementCase.GetProperty(arrayName).EnumerateArray()
                .Single(f => Int(f, "tileId") == tileId);

        // ------------------------------------------------------------------
        // Control: the reconstruction must reproduce the full M5 exported input table.
        // If this drifts, every movement binding that relies on BuildV4 is untrustworthy.
        // ------------------------------------------------------------------

        [Fact]
        public void V4TableReconstruction_MatchesExportedM5InputPlacements()
        {
            JsonElement m5 = Case(Golden(), "tileMovement", "mv-reservation-delivery-then-beam");
            var spec = new V4Spec();
            spec.Hands[0] = new List<int> { 0, 4, 8, 12, 20, 24, 28, 32, 1, 5, 9, 13, 2 };
            spec.Drawn = (0, 16);
            spec.WallPrefix.AddRange(new[] { 33, 17, 25 });
            spec.Phase = RoundPhase.Discard;
            spec.CurrentPlayer = 0;

            TilePositionMap pre = BuildV4(spec);
            TilePositionMap map = pre.Discard(0, new TileId(16)).RequireState();

            JsonElement[] placements = m5.GetProperty("inputPlacements").EnumerateArray().ToArray();
            Assert.Equal(136, placements.Length);
            Assert.Equal(136, placements.Select(p => Int(p, "tileId")).Distinct().Count());
            for (int i = 0; i < placements.Length; i++)
            {
                AssertFact(map, placements[i], "inputPlacements[" + i + "] " + FactLabel(placements[i]), FactSchema.InputPlacements);
            }

            Assert.Equal(WallSlotOffset + Int(m5, "inputWallPointer"), map.WallPointer);
            Assert.Equal(WallSlotOffset + Int(m5, "inputWallEndExclusive"), map.WallEndExclusive);
            Assert.Equal(Int(m5, "inputRinshanPointer"), map.RinshanPointer);
            Assert.Null(map.DrawnTile);
            Assert.Equal(Int(m5, "inputLastDiscardSeat"), map.LastDiscard!.Value.Position.Seat);
            Assert.Equal(Int(m5, "inputLastDiscardTileId"), map.LastDiscard.Value.Tile.Value);
            CheatTestBench.AssertConserved(map);
        }

        // ------------------------------------------------------------------
        // Explicit map builder for the handler cases. Only fields that the exporter or the
        // V4 source constructor actually fixes are asserted; unused physical tiles are placed
        // as filler river tiles so the strict 136-tile validator still runs.
        // ------------------------------------------------------------------

        private sealed class MapSpec
        {
            public Dictionary<int, List<int>> Hands { get; } = new Dictionary<int, List<int>>();

            public (int Seat, int Tile)? Drawn { get; set; }

            public DrawSource DrawnSource { get; set; } = DrawSource.Wall;

            public List<int?> Wall { get; } = new List<int?>();

            public List<int> WallPrefix { get; } = new List<int>();

            public int AutoWallCount { get; set; }

            public List<int?> DeadWall { get; } = new List<int?>();

            public bool AutoFillDeadWall { get; set; } = true;

            public List<int> Replacements { get; } = new List<int>();

            public Dictionary<int, List<int>> Discards { get; } = new Dictionary<int, List<int>>();

            public int WallPointerV4 { get; set; }

            public int RinshanPointer { get; set; }

            public (int Tile, int Seat, int DeadWallIndex)? LastRinshan { get; set; }

            public int[] Dora { get; set; } = Array.Empty<int>();

            public int KanCount { get; set; }
        }

        private static TilePositionMap BuildMap(MapSpec spec)
        {
            var assigned = new bool[TileId.Count];

            void Reserve(int tile)
            {
                if (tile < 0 || tile >= TileId.Count || assigned[tile])
                {
                    throw new InvalidOperationException("duplicate or invalid handler fixture tile: " + tile);
                }

                assigned[tile] = true;
            }

            for (int seat = 0; seat < 4; seat++)
            {
                if (spec.Hands.TryGetValue(seat, out List<int>? hand))
                {
                    for (int i = 0; i < hand.Count; i++)
                    {
                        Reserve(hand[i]);
                    }
                }

                if (spec.Discards.TryGetValue(seat, out List<int>? discards))
                {
                    for (int i = 0; i < discards.Count; i++)
                    {
                        Reserve(discards[i]);
                    }
                }
            }

            if (spec.Drawn.HasValue)
            {
                Reserve(spec.Drawn.Value.Tile);
            }

            for (int i = 0; i < spec.WallPrefix.Count; i++)
            {
                Reserve(spec.WallPrefix[i]);
            }

            for (int i = 0; i < spec.Wall.Count; i++)
            {
                if (spec.Wall[i].HasValue)
                {
                    Reserve(spec.Wall[i]!.Value);
                }
            }

            for (int i = 0; i < spec.DeadWall.Count; i++)
            {
                if (spec.DeadWall[i].HasValue)
                {
                    Reserve(spec.DeadWall[i]!.Value);
                }
            }

            for (int i = 0; i < spec.Replacements.Count; i++)
            {
                Reserve(spec.Replacements[i]);
            }

            int next = 0;
            int NextUnused()
            {
                while (next < TileId.Count && assigned[next])
                {
                    next++;
                }

                if (next >= TileId.Count)
                {
                    throw new InvalidOperationException("handler fixture exhausted the 136-tile set.");
                }

                int tile = next;
                assigned[tile] = true;
                next++;
                return tile;
            }

            var wall = new List<int?>(spec.Wall);
            if (wall.Count == 0)
            {
                for (int i = 0; i < spec.WallPrefix.Count; i++)
                {
                    wall.Add(spec.WallPrefix[i]);
                }

                while (spec.AutoWallCount > 0 && wall.Count < spec.AutoWallCount)
                {
                    wall.Add(NextUnused());
                }
            }

            var deadWall = new List<int?>(spec.DeadWall);
            if (spec.AutoFillDeadWall)
            {
                while (deadWall.Count < TilePositionMap.DeadWallSlotCount)
                {
                    deadWall.Add(NextUnused());
                }
            }

            while (deadWall.Count < TilePositionMap.DeadWallSlotCount)
            {
                deadWall.Add(null);
            }

            var fillerDiscards = spec.Discards.TryGetValue(3, out List<int>? seat3Discards)
                ? new List<int>(seat3Discards)
                : new List<int>();
            for (int tile = 0; tile < TileId.Count; tile++)
            {
                if (!assigned[tile])
                {
                    fillerDiscards.Add(tile);
                    assigned[tile] = true;
                }
            }

            var placements = new List<TilePlacement>(TileId.Count);
            for (int seat = 0; seat < 4; seat++)
            {
                if (spec.Hands.TryGetValue(seat, out List<int>? hand))
                {
                    for (int i = 0; i < hand.Count; i++)
                    {
                        placements.Add(new TilePlacement(new TileId(hand[i]), TilePosition.Hand(seat, i)));
                    }
                }

                if (spec.Drawn.HasValue && spec.Drawn.Value.Seat == seat)
                {
                    int handCount = spec.Hands.TryGetValue(seat, out List<int>? h) ? h.Count : 0;
                    placements.Add(new TilePlacement(
                        new TileId(spec.Drawn.Value.Tile), TilePosition.Hand(seat, handCount)));
                }

                if (spec.Discards.TryGetValue(seat, out List<int>? d))
                {
                    for (int i = 0; i < d.Count; i++)
                    {
                        placements.Add(new TilePlacement(new TileId(d[i]), TilePosition.Discard(seat, i)));
                    }
                }

                if (seat == 3)
                {
                    int explicitCount = spec.Discards.TryGetValue(3, out List<int>? explicitDiscards)
                        ? explicitDiscards.Count
                        : 0;
                    for (int i = 0; i < fillerDiscards.Count; i++)
                    {
                        if (i < explicitCount)
                        {
                            continue;
                        }

                        placements.Add(new TilePlacement(
                            new TileId(fillerDiscards[i]), TilePosition.Discard(3, i)));
                    }
                }
            }

            for (int i = 0; i < wall.Count; i++)
            {
                if (wall[i].HasValue)
                {
                    placements.Add(new TilePlacement(
                        new TileId(wall[i]!.Value), TilePosition.Wall(WallSlotOffset + i)));
                }
            }

            for (int i = 0; i < deadWall.Count; i++)
            {
                if (deadWall[i].HasValue)
                {
                    placements.Add(new TilePlacement(
                        new TileId(deadWall[i]!.Value), TilePosition.DeadWall(i)));
                }
            }

            for (int i = 0; i < spec.Replacements.Count; i++)
            {
                placements.Add(new TilePlacement(
                    new TileId(spec.Replacements[i]), TilePosition.DeadWallReplacement(i)));
            }

            LastDiscardEvidence? lastDiscard = null;
            for (int seat = 0; seat < 4; seat++)
            {
                if (spec.Discards.TryGetValue(seat, out List<int>? d) && d.Count > 0)
                {
                    lastDiscard = new LastDiscardEvidence(
                        new TileId(d[d.Count - 1]), TilePosition.Discard(seat, d.Count - 1));
                }
            }

            RinshanDrawEvidence? rinshan = spec.LastRinshan.HasValue
                ? new RinshanDrawEvidence(
                    new TileId(spec.LastRinshan.Value.Tile),
                    spec.LastRinshan.Value.Seat,
                    spec.LastRinshan.Value.DeadWallIndex)
                : (RinshanDrawEvidence?)null;

            DrawnTileEvidence? drawn = spec.Drawn.HasValue
                ? new DrawnTileEvidence(
                    new TileId(spec.Drawn.Value.Tile), spec.Drawn.Value.Seat, spec.DrawnSource)
                : (DrawnTileEvidence?)null;

            TilePositionMap map = TilePositionMap.FromPlacements(
                placements,
                WallSlotOffset + spec.WallPointerV4,
                WallSlotOffset + wall.Count,
                spec.RinshanPointer,
                lastDiscard,
                0,
                rinshan,
                spec.Dora.Select(d => new TileId(d)).ToArray(),
                spec.KanCount,
                drawn);
            CheatTestBench.AssertConserved(map);
            return map;
        }

        private static TilePositionMap TileMarkBaseMap()
        {
            var spec = new MapSpec();
            spec.Hands[0] = new List<int> { 0, 4, 8, 12, 20, 24, 28, 32, 1, 5, 9, 13, 2 };
            spec.Hands[1] = new List<int> { 52, 56, 60, 64, 3, 6, 7, 10, 11, 14, 15, 18, 19 };
            spec.Hands[2] = new List<int> { 27, 29, 30, 31, 34, 35, 38, 39, 42, 43, 46, 47, 50 };
            spec.Hands[3] = new List<int> { 51, 54, 55, 58, 59, 62, 63, 66, 67, 70, 71, 74, 75 };
            spec.Drawn = (0, 16);
            spec.AutoWallCount = 60;
            return BuildMap(spec);
        }

        private static TilePositionMap SleeveBaseMap(
            int drawnTile = 16,
            int[]? wallHead = null,
            int wallCount = 60,
            DrawSource drawnSource = DrawSource.Wall,
            bool includeDrawnTileInHand = false,
            int[]? reservedDiscards = null,
            bool withoutDrawn = false)
        {
            var spec = new MapSpec();
            var hand = new List<int> { 0, 4, 8, 12, 20, 24, 28, 36, 1, 5, 9, 13, 2 };
            if (includeDrawnTileInHand)
            {
                hand.Remove(drawnTile);
                if (!hand.Contains(16))
                {
                    hand.Add(16);
                }
            }

            spec.Hands[0] = hand;
            if (!withoutDrawn)
            {
                spec.Drawn = (0, drawnTile);
                spec.DrawnSource = drawnSource;
            }

            if (wallCount > 0)
            {
                spec.WallPrefix.AddRange(wallHead ?? new[] { 32 });
            }

            if (reservedDiscards != null)
            {
                spec.Discards[0] = new List<int>(reservedDiscards);
            }

            spec.AutoWallCount = wallCount;
            return BuildMap(spec);
        }

        private static TilePositionMap SleeveRinshanMap(int rinshanPointer = 1)
        {
            var spec = new MapSpec();
            spec.Hands[0] = new List<int> { 0, 4, 8, 12, 20, 36, 1, 5, 9, 13, 2, 16, 17 };
            spec.Drawn = (0, 24);
            spec.DrawnSource = DrawSource.Rinshan;
            spec.WallPrefix.Add(32);
            spec.AutoWallCount = 60;
            for (int i = 0; i < rinshanPointer; i++)
            {
                spec.DeadWall.Add(null);
            }

            if (rinshanPointer == 1)
            {
                spec.DeadWall.Add(28);
            }

            spec.AutoFillDeadWall = true;
            for (int i = 0; i < rinshanPointer; i++)
            {
                spec.Replacements.Add(135 - i);
            }

            spec.RinshanPointer = rinshanPointer;
            spec.LastRinshan = (24, 0, Math.Max(0, rinshanPointer - 1));
            return BuildMap(spec);
        }

        private static TilePositionMap BeamBaseMap(
            bool withoutDrawn = false,
            int drawnTile = 16,
            DrawSource drawnSource = DrawSource.Wall,
            int wallCount = 60)
        {
            var spec = new MapSpec();
            var hand = new List<int> { 0, 4, 8, 12, 20, 24, 28, 32, 1, 5, 9, 13, 2 };
            hand.Remove(drawnTile);
            spec.Hands[0] = hand;
            if (!withoutDrawn)
            {
                spec.Drawn = (0, drawnTile);
                spec.DrawnSource = drawnSource;
            }

            if (wallCount > 0)
            {
                spec.WallPrefix.AddRange(new[] { 56, 60, 64, 68, 72, 76, 80, 84 });
            }

            spec.AutoWallCount = wallCount;
            return BuildMap(spec);
        }

        private static TilePositionMap BeamDeadWallActivationMap()
        {
            var spec = new MapSpec();
            // V4 activation default: drawnTileId=16, deadWall=108..121, replacement=0.
            spec.Hands[0] = new List<int> { 4, 8, 12, 20, 24, 28, 32, 1, 5, 9, 13, 2 };
            spec.Drawn = (0, 16);
            spec.DrawnSource = DrawSource.Rinshan;
            spec.WallPrefix.AddRange(new[] { 56, 60, 64, 68 });
            spec.AutoWallCount = 60;
            spec.Dora = new[] { 112 };
            spec.RinshanPointer = 1;
            spec.DeadWall.Add(null);
            for (int tile = 109; tile <= 121; tile++)
            {
                spec.DeadWall.Add(tile);
            }

            spec.AutoFillDeadWall = false;
            spec.Replacements.Add(0);
            return BuildMap(spec);
        }

        private static TilePositionMap BeamDeadWallChoiceMap()
        {
            var spec = new MapSpec();
            // V4 choice table: drawRinshan gives drawn=108 and replacement=135.
            spec.Hands[0] = new List<int> { 0, 4, 8, 12, 16, 20, 24, 28, 32, 1, 5, 9, 13 };
            spec.Drawn = (0, 108);
            spec.DrawnSource = DrawSource.Rinshan;
            spec.WallPrefix.AddRange(new[] { 56, 60, 64, 68 });
            spec.AutoWallCount = 60;
            spec.Dora = new[] { 112 };
            spec.RinshanPointer = 1;
            spec.DeadWall.Add(null);
            for (int tile = 109; tile <= 121; tile++)
            {
                spec.DeadWall.Add(tile);
            }

            spec.AutoFillDeadWall = false;
            spec.Replacements.Add(135);
            return BuildMap(spec);
        }

        private static CheatTurnContext Ctx(
            int currentPlayer,
            RoundPhase phase,
            SleeveBloomDrawKind drawKind,
            int turn = TurnNumber,
            bool responseWindow = false,
            bool riichi = false,
            long now = 1_000)
        {
            return new CheatTurnContext(
                currentPlayer,
                phase,
                responseWindow,
                new[] { riichi, false, false, false },
                turn,
                RoundId,
                1,
                drawKind,
                now);
        }

        private static CheatState NewState(int points = 40)
        {
            return NewState(CheatTestBench.Economy(points));
        }

        private static CheatState NewState(CheatEconomyConfig economy)
        {
            CheatState state = CheatState.Initialize(economy);
            TileMarkTransition registered = state.TileMarks.RegisterDealtRound(RoundId);
            Assert.True(registered.Success, registered.Error.ToString());
            return state.WithTileMarks(registered.State);
        }

        private static CheatState StateWithWindow(CheatState state, int seat, string windowId)
        {
            var windows = new string?[4];
            windows[seat] = windowId;
            return state.WithSleeveBloom(SleeveBloomState.Restore(
                null, Array.Empty<SleeveBloomOffer>(), Array.Empty<SleeveBloomReservation>(),
                windows, 1, -1, 1));
        }

        private static CheatEconomyConfig EconomyWithCosts(int points, int tileMark, int sleeve, int beam)
        {
            var costs = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                { CheatEconomyConfig.TileMarkCostKey, tileMark },
                { CheatEconomyConfig.SleeveBloomCostKey, sleeve },
                { CheatEconomyConfig.BeamSwapCostKey, beam }
            };
            return new CheatEconomyConfig(true, points, costs);
        }

        private static CheatState WithImmediateOffer(CheatState state, int seat, TileId first, TileId second)
        {
            var offer = new SleeveBloomOffer(
                "bloom-" + seat + "-req-1", seat, SleeveBloomDrawKind.Normal,
                first, second, TurnNumber, SleeveBloomOfferMode.Immediate);
            return state.WithSleeveBloom(state.SleeveBloom.WithImmediateOffer(offer));
        }

        private static CheatState WithOffTurnOffer(CheatState state, int seat, TileId first, TileId second)
        {
            var offer = new SleeveBloomOffer(
                "bloom-offturn-" + seat + "-req-1", seat, SleeveBloomDrawKind.Normal,
                first, second, TurnNumber, SleeveBloomOfferMode.OffTurn,
                "bloom-window-" + seat + "-1", 11_000, 1_000);
            return state.WithSleeveBloom(state.SleeveBloom.WithOffTurnOffer(offer));
        }

        private static CheatState WithBeamOffer(CheatState state, TileId drawn, TileId candidate, TileId hand)
        {
            var offer = new BeamSwapOffer(
                "beam-0-req-1", 0, BeamSwapWindow.Wall,
                new[] { new BeamSwapCandidate(candidate, BeamSwapSlot.Wall(WallSlotOffset)) },
                new[] { hand }, drawn, TurnNumber);
            return state.WithBeamSwap(state.BeamSwap.WithOffer(offer));
        }

        // ------------------------------------------------------------------
        // Ledger: the snapshot round-trip must restore every entry field, in order, and the
        // restored balances must replay those entries exactly. A boolean entriesEqual is not
        // a consumer binding.
        // ------------------------------------------------------------------

        [Fact]
        public void Ledger_Snapshot_RestoresEveryEntryFieldInOrder()
        {
            JsonElement root = Golden();
            JsonElement ledgerSpec = root.GetProperty("ledger");
            JsonElement charge = ledgerSpec.GetProperty("chargeOnce");
            JsonElement flowSpec = ledgerSpec.GetProperty("flowSummary");
            JsonElement snapshot = ledgerSpec.GetProperty("snapshotRoundTrip");

            int[] initialPoints = Ints(ledgerSpec, "initialPoints");
            Assert.Equal(4, initialPoints.Length);
            for (int seat = 1; seat < initialPoints.Length; seat++)
            {
                Assert.Equal(initialPoints[0], initialPoints[seat]);
            }

            CheatLedger charged = CheatLedger.Initialize(CheatTestBench.Economy(initialPoints[0]));
            for (int seat = 0; seat < 4; seat++)
            {
                Assert.Equal(initialPoints[seat], charged.BalanceOf(seat));
            }

            Assert.Equal("skill-1", Str(charge, "entryId")); // V4 exporter source literal
            CheatLedgerApplyResult first = charged.Apply(
                Str(charge, "entryId"), Int(charge, "seat"), Int(charge, "amount"),
                Str(charge, "reason"));
            Assert.True(first.Success, first.Error.ToString());

            // Every field of the first exported entry.
            Assert.Equal("skill-1", first.Entry!.EntryId);
            Assert.Equal(Str(charge, "entryId"), first.Entry.EntryId);
            Assert.Equal(Int(charge, "seat"), first.Entry.Seat);
            Assert.Equal(Int(charge, "amount"), first.Entry.Amount);
            Assert.Equal(Int(charge, "balanceAfter"), first.Entry.BalanceAfter);
            Assert.Equal(Str(charge, "reason"), first.Entry.Reason);
            Assert.Equal("TILE_MARK", first.Entry.Reason); // V4 exporter source literal
            Assert.Equal(Int(charge, "entryCount"), first.Ledger!.Entries.Count);
            int[] balancesAfter = Ints(charge, "balancesAfter");
            for (int seat = 0; seat < 4; seat++)
            {
                Assert.Equal(balancesAfter[seat], first.Ledger.BalanceOf(seat));
            }

            // Same id and payload returns the original entry instance, no second row.
            CheatLedgerApplyResult replay = first.Ledger.Apply(
                Str(charge, "entryId"), Int(charge, "seat"), Int(charge, "amount"),
                Str(charge, "reason"));
            Assert.True(replay.Success);
            Assert.True(replay.Replayed);
            Assert.Same(first.Entry, replay.Entry);
            Assert.Equal(Int(ledgerSpec.GetProperty("replaySamePayload"), "entryCount"),
                replay.Ledger!.Entries.Count);

            // Every exported ledger rejection: outcome/exception are V4 evidence, translated
            // to the typed C# error; the requested payload and source constants are consumed.
            JsonElement conflict = ledgerSpec.GetProperty("conflictDifferentPayload");
            Assert.Equal("rejected", Str(conflict, "outcome"));
            Assert.Equal("IllegalArgumentException", Str(conflict, "exception"));
            Assert.Equal(0, Int(conflict.GetProperty("requested"), "seat"));
            Assert.Equal(6, Int(conflict.GetProperty("requested"), "amount"));
            CheatLedgerApplyResult conflicted = replay.Ledger.Apply(
                "skill-1", Int(conflict.GetProperty("requested"), "seat"),
                Int(conflict.GetProperty("requested"), "amount"), "TILE_MARK");
            Assert.False(conflicted.Success);
            Assert.Equal(CheatLedgerError.EntryConflict, conflicted.Error);
            Assert.Null(conflicted.Ledger);
            Assert.Equal(Int(conflict, "entryCount"), replay.Ledger.Entries.Count);
            Assert.Equal(Ints(conflict, "balancesAfter"), replay.Ledger.Balances.ToArray());

            JsonElement poorSpec = ledgerSpec.GetProperty("insufficientFunds");
            Assert.Equal("rejected", Str(poorSpec, "outcome"));
            Assert.Equal("IllegalStateException", Str(poorSpec, "exception"));
            Assert.Equal(10, Int(poorSpec, "initialPoints"));
            Assert.Equal(-11, Int(poorSpec, "amount"));
            CheatLedger poor = CheatLedger.Initialize(CheatTestBench.Economy(Int(poorSpec, "initialPoints")));
            CheatLedgerApplyResult overdraft = poor.Apply("over", 1, Int(poorSpec, "amount"), "TILE_MARK");
            Assert.Equal(CheatLedgerError.InsufficientFunds, overdraft.Error);

            JsonElement overflowSpec = ledgerSpec.GetProperty("overflow");
            Assert.Equal("rejected", Str(overflowSpec, "outcome"));
            Assert.Equal("ArithmeticException", Str(overflowSpec, "exception"));
            Assert.Equal(int.MaxValue, Int(overflowSpec, "amount"));
            CheatLedgerApplyResult overflow = poor.Apply("big", 2, Int(overflowSpec, "amount"), "TILE_MARK");
            Assert.Equal(CheatLedgerError.BalanceOverflow, overflow.Error);

            JsonElement seatSpec = ledgerSpec.GetProperty("invalidSeat");
            Assert.Equal("rejected", Str(seatSpec, "outcome"));
            Assert.Equal("IllegalArgumentException", Str(seatSpec, "exception"));
            Assert.Equal(4, Int(seatSpec, "seat"));
            CheatLedgerApplyResult badSeat = poor.Apply("seat", Int(seatSpec, "seat"), -1, "TILE_MARK");
            Assert.Equal(CheatLedgerError.InvalidSeat, badSeat.Error);
            Assert.Equal(Int(poorSpec, "initialPoints"), poor.BalanceOf(0));
            Assert.Empty(poor.Entries);

            // The flow ledger is the exported snapshot source: spend then gain, in this order.
            CheatLedger flow = CheatLedger.Initialize(CheatTestBench.Economy(Int(
                ledgerSpec.GetProperty("insufficientFunds"), "initialPoints")));
            JsonElement spend = flowSpec.GetProperty("spend");
            JsonElement gain = flowSpec.GetProperty("gain");
            flow = flow.Apply(
                "spend", Int(spend, "seat"), Int(spend, "amount"), "TILE_MARK").Ledger!;
            flow = flow.Apply(
                "gain", Int(gain, "seat"), Int(gain, "amount"), "ROUND_ACHIEVEMENT").Ledger!;
            Assert.Equal(2, flow.Entries.Count);

            CheatLedgerEntry spendEntry = flow.Entries[0];
            Assert.Equal("spend", spendEntry.EntryId);
            Assert.Equal(Int(spend, "seat"), spendEntry.Seat);
            Assert.Equal(Int(spend, "amount"), spendEntry.Amount);
            Assert.Equal(Int(ledgerSpec.GetProperty("insufficientFunds"), "initialPoints") + Int(spend, "amount"),
                spendEntry.BalanceAfter);
            Assert.Equal("TILE_MARK", spendEntry.Reason);

            CheatLedgerEntry gainEntry = flow.Entries[1];
            Assert.Equal("gain", gainEntry.EntryId);
            Assert.Equal(Int(gain, "seat"), gainEntry.Seat);
            Assert.Equal(Int(gain, "amount"), gainEntry.Amount);
            Assert.Equal(Int(ledgerSpec.GetProperty("insufficientFunds"), "initialPoints") + Int(gain, "amount"),
                gainEntry.BalanceAfter);
            Assert.Equal("ROUND_ACHIEVEMENT", gainEntry.Reason);
            Assert.Equal(Int(flowSpec, "seat0TotalSpent"),
                flow.Entries.Where(e => e.Seat == 0 && e.Amount < 0).Sum(e => -e.Amount));
            Assert.Equal(Int(flowSpec, "seat1TotalGained"),
                flow.Entries.Where(e => e.Seat == 1 && e.Amount > 0).Sum(e => e.Amount));
            Assert.Equal(Int(flowSpec, "seat1NetDelta"),
                flow.Entries.Where(e => e.Seat == 1).Sum(e => e.Amount));

            int[] finalBalances = Ints(flowSpec, "finalBalances");
            CheatLedger restored = CheatLedger.FromSnapshot(
                Int(ledgerSpec.GetProperty("insufficientFunds"), "initialPoints"), finalBalances, flow.Entries);

            Assert.True(Bool(snapshot, "balancesEqual"));
            Assert.True(Bool(snapshot, "entriesEqual"));
            Assert.Equal(flow.Entries.Count, restored.Entries.Count);
            Assert.Equal(2, restored.Version);
            for (int i = 0; i < flow.Entries.Count; i++)
            {
                CheatLedgerEntry expected = flow.Entries[i];
                CheatLedgerEntry actual = restored.Entries[i];
                Assert.Equal(expected.EntryId, actual.EntryId);
                Assert.Equal(expected.Seat, actual.Seat);
                Assert.Equal(expected.Amount, actual.Amount);
                Assert.Equal(expected.BalanceAfter, actual.BalanceAfter);
                Assert.Equal(expected.Reason, actual.Reason);
            }

            Assert.Equal(flow.Entries, restored.Entries);
            for (int seat = 0; seat < 4; seat++)
            {
                Assert.Equal(finalBalances[seat], restored.BalanceOf(seat));
            }

            // The restore path rejects a balance that does not replay from the ordered entries.
            var drifted = new[] { finalBalances[0] + 1, finalBalances[1], finalBalances[2], finalBalances[3] };
            Assert.Throws<ArgumentException>(() => CheatLedger.FromSnapshot(
                Int(ledgerSpec.GetProperty("insufficientFunds"), "initialPoints"), drifted, flow.Entries));
        }

        // ------------------------------------------------------------------
        // TileMarkState: every exported group field, capacity/overlap refusals with zero
        // side effects, cross-seat identity, expiry and the settled-round order.
        // ------------------------------------------------------------------

        [Fact]
        public void TileMarkState_BindsEveryExportedFieldAndZeroSideEffect()
        {
            JsonElement spec = Golden().GetProperty("tileMarkState");
            JsonElement register = spec.GetProperty("registerDealtRound");
            JsonElement add = spec.GetProperty("addGroup");
            JsonElement expiry = spec.GetProperty("expiry");
            int[] trail = Ints(add, "versionTrail");

            var initial = new TileMarkState();
            Assert.Equal(trail[0], initial.Version);
            Assert.Equal(0, initial.DealtRoundOrdinal);
            Assert.Empty(initial.DealtRoundIds);
            Assert.Empty(initial.SettledRoundIds);

            TileMarkTransition registered = initial.RegisterDealtRound("r1");
            Assert.True(registered.Success);
            Assert.Equal(Int(register, "ordinalAfterFirst"), registered.State!.DealtRoundOrdinal);
            Assert.Equal(Int(register, "versionAfterFirstRegister"), registered.State.Version);
            Assert.Equal(trail[1], registered.State.Version);
            Assert.Equal(new[] { "r1" }, registered.State.DealtRoundIds.ToArray());
            Assert.Equal("r1", registered.State.CurrentRoundId);
            Assert.Same(registered.State, registered.State.RegisterDealtRound("r1").State);
            Assert.True(Bool(register, "sameIdReplays"));

            var g1 = new TileMarkGroup("g1", 0, new[] { new TileId(0), new TileId(1), new TileId(2), new TileId(3) }, "r1", 1);
            TileMarkTransition added = registered.State.AddGroup(g1);
            Assert.True(added.Success);
            Assert.Single(added.State!.Groups);
            TileMarkGroup group = added.State.Groups[0];
            Assert.Equal("g1", group.GroupId);
            Assert.Equal(0, group.OwnerSeat);
            Assert.Equal(new[] { 0, 1, 2, 3 }, group.TileIds.Select(t => t.Value).ToArray());
            Assert.Equal("r1", group.CreatedRoundId);
            Assert.Equal(1, group.CreatedRoundOrdinal);
            Assert.Equal(Int(add, "expiresAfterRoundOrdinal"), group.ExpiresAfterRoundOrdinal);
            Assert.Equal(new[] { 0, 1, 2, 3 },
                added.State.MarkedTilesForSeat(0).Select(t => t.Value).ToArray());
            Assert.Equal(trail[2], added.State.Version);
            Assert.Same(added.State, added.State.AddGroup(g1).State);
            Assert.True(Bool(add, "sameIdSameContentReplays"));
            Assert.Equal(Int(expiry, "versionAfterFirstAdd"), added.State.Version);

            TileMarkTransition conflict = added.State.AddGroup(
                new TileMarkGroup("g1", 0, new[] { new TileId(4), new TileId(5), new TileId(6), new TileId(7) }, "r1", 1));
            Assert.Equal("IllegalArgumentException", Str(add, "sameIdDifferentContentException"));
            Assert.False(conflict.Success);
            Assert.Equal(TileMarkError.GroupIdConflict, conflict.Error);
            Assert.Same(added.State, conflict.State);
            Assert.Single(conflict.State.Groups);
            Assert.Equal(trail[2], conflict.State.Version);

            // Three groups per seat is the hard capacity; the fourth must not mutate anything.
            TileMarkState three = added.State
                .AddGroup(new TileMarkGroup("g2", 0, new[] { new TileId(4), new TileId(5), new TileId(6), new TileId(7) }, "r1", 1)).State!
                .AddGroup(new TileMarkGroup("g3", 0, new[] { new TileId(8), new TileId(9), new TileId(10), new TileId(11) }, "r1", 1)).State!;
            Assert.Equal(3, three.Groups.Count);
            Assert.True(Bool(add, "seatGroupLimitIsThree"));
            Assert.Equal("IllegalArgumentException", Str(add, "seatGroupLimitException"));
            TileMarkTransition capacity = three.AddGroup(
                new TileMarkGroup("g4", 0, new[] { new TileId(12), new TileId(13), new TileId(14), new TileId(15) }, "r1", 1));
            Assert.False(capacity.Success);
            Assert.Equal(TileMarkError.GroupLimitExceeded, capacity.Error);
            Assert.Same(three, capacity.State);
            Assert.Equal(3, capacity.State.Groups.Count);

            // Every group holds exactly four entities, so the three-group cap and the
            // twelve-tile cap coincide at the public boundary: 3 x 4 is accepted, the next
            // group is rejected without touching the source state.
            Assert.Equal(12, three.Groups.Sum(g => g.TileIds.Count));
            Assert.True(Bool(add, "seatTileLimitIsTwelve"));

            // Cross-seat reuse of the same physical entities is legal.
            TileMarkTransition cross = added.State.AddGroup(
                new TileMarkGroup("other", 1, new[] { new TileId(0), new TileId(1), new TileId(2), new TileId(3) }, "r1", 1));
            Assert.True(cross.Success);
            Assert.True(Bool(add, "crossSeatSameTilesAllowed"));
            Assert.Equal(Int(add, "crossSeatGroupCount"), cross.State!.Groups.Count);
            Assert.Equal(trail[3], cross.State.Version);

            // Expiry: created ordinal 1 -> expires after 4; completed r3 keeps it, r4 drops it.
            TileMarkState rolling = added.State;
            rolling = rolling.CompleteRound("r1").State!;
            rolling = rolling.RegisterDealtRound("r2").State!;
            rolling = rolling.CompleteRound("r2").State!;
            rolling = rolling.RegisterDealtRound("r3").State!;
            rolling = rolling.CompleteRound("r3").State!;
            Assert.Equal(Int(expiry, "groupsAfterCompletingRound3"), rolling.Groups.Count);
            Assert.Equal(Int(expiry, "versionAfterCompletingRound3"), rolling.Version);
            Assert.Equal(new[] { "r1", "r2", "r3" }, rolling.SettledRoundIds.ToArray());
            Assert.True(rolling.IsSettled("r3"));

            TileMarkState final = rolling.RegisterDealtRound("r4").State!.CompleteRound("r4").State!;
            Assert.Equal(Int(expiry, "groupsAfterCompletingRound4"), final.Groups.Count);
            Assert.Equal(Int(expiry, "versionAfterCompletingRound4"), final.Version);
            Assert.Equal(Int(expiry, "settledRoundCount"), final.SettledRoundIds.Count);
            Assert.Equal(new[] { "r1", "r2", "r3", "r4" }, final.SettledRoundIds.ToArray());
            Assert.Equal(4, final.DealtRoundOrdinal);
            Assert.Empty(final.Groups);
            Assert.Equal(Int(expiry, "createdRoundOrdinal"), group.CreatedRoundOrdinal);
        }

        // ------------------------------------------------------------------
        // Binding manifest. The denominator is frozen at 20 + 32 + 23 handler cases and six
        // movement cases. Every row names the exact JSON location and the real C# entry.
        // BLOCKED rows are not counted as behaviour-bound and carry the missing dependency.
        // ------------------------------------------------------------------

        private enum BindingKind
        {
            Bound,
            Refined,
            ActorLayerBlocked,
            UnrepresentableBlocked
        }

        private sealed class CaseSpec
        {
            public CaseSpec(
                string section,
                string caseId,
                string outcome,
                string? reason,
                CheatErrorCode? code,
                BindingKind kind,
                string entry,
                string note,
                string? dependency = null)
            {
                Section = section;
                CaseId = caseId;
                Outcome = outcome;
                Reason = reason;
                Code = code;
                Kind = kind;
                Entry = entry;
                Note = note;
                Dependency = dependency;
            }

            public string Section { get; }

            public string CaseId { get; }

            public string Outcome { get; }

            public string? Reason { get; }

            public CheatErrorCode? Code { get; }

            public BindingKind Kind { get; }

            public string Entry { get; }

            public string Note { get; }

            public string? Dependency { get; }

            public string JsonPath => "$." + Section + "[?(@.caseId=='" + CaseId + "')]";

            public bool IsBehaviourBound => Kind == BindingKind.Bound || Kind == BindingKind.Refined;
        }

        private static CaseSpec Bound(
            string section, string id, string outcome, string? reason, CheatErrorCode code,
            string entry, string note)
        {
            return new CaseSpec(section, id, outcome, reason, code, BindingKind.Bound, entry, note);
        }

        private static CaseSpec Refined(
            string section, string id, string outcome, string reason, CheatErrorCode code,
            string entry, string note)
        {
            return new CaseSpec(section, id, outcome, reason, code, BindingKind.Refined, entry, note);
        }

        private static CaseSpec Blocked(
            string section, string id, string outcome, string? reason, BindingKind kind,
            string entry, string note, string dependency)
        {
            return new CaseSpec(section, id, outcome, reason, null, kind, entry, note, dependency);
        }

        private static readonly CaseSpec[] TileMarkSpecs =
        {
            Bound("tileMarkHandler", "tm-allowed", "Allowed", null, CheatErrorCode.None,
                "CheatSubmission.ApplyTileMark", "own turn CHEAT_WINDOW, four owned unmarked tiles"),
            Bound("tileMarkHandler", "tm-not-your-turn", "Rejected", "NOT_YOUR_TURN", CheatErrorCode.NotYourTurn,
                "CheatSubmission.ApplyTileMark", "actor seat 1 while seat 0 acts"),
            Bound("tileMarkHandler", "tm-wrong-phase-preparation", "Rejected", "WRONG_PHASE", CheatErrorCode.WrongPhase,
                "CheatSubmission.ApplyTileMark", "preparation is projected to RoundPhase.Stacking (no trick window)"),
            Bound("tileMarkHandler", "tm-wrong-phase-draw", "Rejected", "WRONG_PHASE", CheatErrorCode.WrongPhase,
                "CheatSubmission.ApplyTileMark", "DRAW is not a mark phase"),
            Blocked("tileMarkHandler", "tm-wrong-phase-round-end", "Rejected", "DECISION_MISMATCH",
                BindingKind.ActorLayerBlocked, "RoundStateMachine terminal guard + actor decision binding",
                "V4 reports DECISION_MISMATCH after round end; CheatTurnContext has no round-ended/decision field",
                "N1b accepted: TableActor.SubmitCheatAsync -> ValidateAuthorityIdentity -> RoundCheatTransition.Prepare; round-ended/decision admission is not exposed by CheatTurnContext; next actor test N1bCheatExecutionTests.cs (no TableRuntime ref in Domain.Tests)"),
            Refined("tileMarkHandler", "tm-response-window", "Rejected", "WRONG_PHASE", CheatErrorCode.ResponseWindowOpen,
                "CheatSubmission.ApplyTileMark", "V4 folds response windows into WRONG_PHASE; C# refines to ResponseWindowOpen"),
            Bound("tileMarkHandler", "tm-riichi-locked", "Rejected", "TILE_MARK_RIICHI_LOCKED", CheatErrorCode.RiichiLocked,
                "CheatSubmission.ApplyTileMark", "actor riichi flag"),
            Bound("tileMarkHandler", "tm-invalid-selection-three", "Rejected", "TILE_MARK_INVALID_SELECTION", CheatErrorCode.InvalidSelection,
                "CheatSubmission.ApplyTileMark", "only three tiles"),
            Bound("tileMarkHandler", "tm-invalid-selection-duplicate", "Rejected", "TILE_MARK_INVALID_SELECTION", CheatErrorCode.InvalidSelection,
                "CheatSubmission.ApplyTileMark", "duplicate physical entity"),
            Bound("tileMarkHandler", "tm-tile-not-owned", "Rejected", "TILE_MARK_TILE_NOT_OWNED", CheatErrorCode.TileNotOwned,
                "CheatSubmission.ApplyTileMark", "tiles 52/56/60/64 sit in other seats' hands"),
            Bound("tileMarkHandler", "tm-already-marked", "Rejected", "TILE_MARK_ALREADY_MARKED", CheatErrorCode.AlreadyMarked,
                "CheatSubmission.ApplyTileMark", "8 and 12 already marked for the seat"),
            Bound("tileMarkHandler", "tm-capacity-groups", "Rejected", "TILE_MARK_CAPACITY_EXCEEDED", CheatErrorCode.MarkCapacityExceeded,
                "CheatSubmission.ApplyTileMark", "three existing groups"),
            Blocked("tileMarkHandler", "tm-capacity-tiles", "Rejected", "TILE_MARK_CAPACITY_EXCEEDED",
                BindingKind.UnrepresentableBlocked, "TileMarkGroup exactly-four invariant / tile-limit guard",
                "V4 input groups=2, marked=(16..27)=12, selected=0/4/8/12; C# groups hold exactly four tiles, so 12 marked requires 3 groups and the next add hits the group limit, not the tile-limit guard",
                "needs a state-level group-size input or an exported tile-limit case that is reachable through the existing four-tile group constructor"),
            Bound("tileMarkHandler", "tm-insufficient-funds", "Rejected", "INSUFFICIENT_FUNDS", CheatErrorCode.InsufficientFunds,
                "CheatSubmission.ApplyTileMark", "five points below the configured cost"),
            Refined("tileMarkHandler", "tm-sleeve-pending", "Rejected", "SLEEVE_BLOOM_CHOICE_PENDING", CheatErrorCode.OtherOfferPending,
                "CheatSubmission.ApplyTileMark", "own immediate sleeve offer is pending"),
            Refined("tileMarkHandler", "tm-offturn-sleeve-pending", "Rejected", "SLEEVE_BLOOM_CHOICE_PENDING", CheatErrorCode.OtherOfferPending,
                "CheatSubmission.ApplyTileMark", "own off-turn sleeve offer is pending"),
            Refined("tileMarkHandler", "tm-beam-pending", "Rejected", "BEAM_SWAP_CHOICE_PENDING", CheatErrorCode.OtherOfferPending,
                "CheatSubmission.ApplyTileMark", "own beam offer is pending"),
            Blocked("tileMarkHandler", "tm-no-active-decision", "Rejected", "NO_ACTIVE_DECISION",
                BindingKind.ActorLayerBlocked, "DecisionClock/actor decision admission",
                "V4 timing absence; CheatSubmission has no decision timing input",
                "N1b accepted; no-active-decision timing is an actor admission input. Next test: TableRuntime.Tests/N1bCheatExecutionTests.cs; Domain.Tests must not reference TableRuntime"),
            Blocked("tileMarkHandler", "tm-deadline-expired", "Rejected", "DEADLINE_EXPIRED",
                BindingKind.ActorLayerBlocked, "DecisionTiming.IsExpired(now == deadline)",
                "V4 deadline equality; CheatTurnContext.NowMillis is not compared to a decision deadline",
                "N1b accepted; CheatTurnContext has no deadline field. Actor must call DecisionTiming.IsExpired before dispatch; next test TableRuntime.Tests/N1bCheatExecutionTests.cs"),
            Blocked("tileMarkHandler", "tm-decision-mismatch", "Rejected", "DECISION_MISMATCH",
                BindingKind.ActorLayerBlocked, "actor round/decision identity binding",
                "V4 no active round; CheatSubmission takes no round/decision identity for admission",
                "N1b accepted; decision/round identity admission happens before ApplyTileMark in TableActor.ValidateAuthorityIdentity; next test TableRuntime.Tests/N1bCheatExecutionTests.cs")
        };

        private static readonly CaseSpec[] SleeveBloomSpecs =
        {
            Bound("sleeveBloomHandler", "sb-activate-allowed", "Allowed", null, CheatErrorCode.None,
                "CheatSubmission.ActivateSleeveBloom", "own normal draw, first available wall candidate"),
            Bound("sleeveBloomHandler", "sb-activate-dealer-extra", "Rejected", "SLEEVE_BLOOM_DEALER_EXTRA_EXCLUDED", CheatErrorCode.DealExtraDraw,
                "CheatSubmission.ActivateSleeveBloom", "dea-extra draw evidence"),
            Bound("sleeveBloomHandler", "sb-activate-already-used", "Rejected", "SLEEVE_BLOOM_ALREADY_USED_THIS_DRAW", CheatErrorCode.AlreadyUsedThisTurn,
                "CheatSubmission.ActivateSleeveBloom", "used-on-turn equals the current turn"),
            Refined("sleeveBloomHandler", "sb-activate-beam-pending", "Rejected", "BEAM_SWAP_ALREADY_PENDING", CheatErrorCode.OtherOfferPending,
                "CheatSubmission.ActivateSleeveBloom", "mutual exclusion, C# names the other offer"),
            Bound("sleeveBloomHandler", "sb-activate-sleeve-pending", "Rejected", "SLEEVE_BLOOM_ALREADY_PENDING", CheatErrorCode.OfferAlreadyPending,
                "CheatSubmission.ActivateSleeveBloom", "immediate offer already frozen"),
            Refined("sleeveBloomHandler", "sb-activate-wall-empty", "Rejected", "SLEEVE_BLOOM_WALL_EMPTY", CheatErrorCode.NoWallCandidate,
                "CheatSubmission.ActivateSleeveBloom", "no available live-wall candidate"),
            Blocked("sleeveBloomHandler", "sb-activate-wall-empty-drift", "Rejected", "SLEEVE_BLOOM_WALL_EMPTY",
                BindingKind.UnrepresentableBlocked, "CheatSubmission.ActivateSleeveBloom + frozen nextWallTileId input",
                "V4 freezes nextWallTileId=32 and rejects when that entity left the wall; C# has no such input and re-searches the first available tile",
                "missing API input: a frozen nextWallTileId/nextWallTileInWall parameter (or actor pre-check). A map-only projection cannot distinguish this from a legal other-candidate wall; readiness probe intentionally fails until the input exists."),
            Bound("sleeveBloomHandler", "sb-activate-no-drawn-tile", "Rejected", "SLEEVE_BLOOM_NO_DRAWN_TILE", CheatErrorCode.NoDrawnTile,
                "CheatSubmission.ActivateSleeveBloom", "no tracked draw"),
            Bound("sleeveBloomHandler", "sb-activate-not-your-turn", "Rejected", "NOT_YOUR_TURN", CheatErrorCode.NotYourTurn,
                "CheatSubmission.ActivateSleeveBloom", "seat 1 on seat 0 turn"),
            Bound("sleeveBloomHandler", "sb-activate-wrong-phase", "Rejected", "WRONG_PHASE", CheatErrorCode.WrongPhase,
                "CheatSubmission.ActivateSleeveBloom", "DISCARD is not CHEAT_WINDOW"),
            Refined("sleeveBloomHandler", "sb-activate-response-window", "Rejected", "WRONG_PHASE", CheatErrorCode.ResponseWindowOpen,
                "CheatSubmission.ActivateSleeveBloom", "response windows override tricks"),
            Refined("sleeveBloomHandler", "sb-activate-disabled", "Rejected", "SLEEVE_BLOOM_DISABLED", CheatErrorCode.CostMustBeTen,
                "CheatSubmission.ActivateSleeveBloom", "V4 input cost=5; C# splits the disabled family into CostMustBeTen"),
            Bound("sleeveBloomHandler", "sb-activate-insufficient", "Rejected", "INSUFFICIENT_FUNDS", CheatErrorCode.InsufficientFunds,
                "CheatSubmission.ActivateSleeveBloom", "five points below cost ten"),
            Bound("sleeveBloomHandler", "sb-activate-rinshan-allowed", "Allowed", null, CheatErrorCode.None,
                "CheatSubmission.ActivateSleeveBloom", "rinshan draw with next dead-wall candidate"),
            Refined("sleeveBloomHandler", "sb-activate-rinshan-empty", "Rejected", "SLEEVE_BLOOM_WALL_EMPTY", CheatErrorCode.NoWallCandidate,
                "CheatSubmission.ActivateSleeveBloom", "rinshan pointer four"),
            Bound("sleeveBloomHandler", "sb-choose-first", "Allowed", null, CheatErrorCode.None,
                "CheatSubmission.ChooseSleeveBloom", "consistent immediate offer, keep FIRST"),
            Bound("sleeveBloomHandler", "sb-choose-second", "Allowed", null, CheatErrorCode.None,
                "CheatSubmission.ChooseSleeveBloom", "consistent immediate offer, keep SECOND"),
            Bound("sleeveBloomHandler", "sb-choose-stale-turn", "StaleOffer", "SLEEVE_BLOOM_STALE_OFFER", CheatErrorCode.StaleOffer,
                "CheatSubmission.ChooseSleeveBloom", "current turn drifted from the frozen turn"),
            Bound("sleeveBloomHandler", "sb-choose-stale-drawn", "StaleOffer", "SLEEVE_BLOOM_STALE_OFFER", CheatErrorCode.StaleOffer,
                "CheatSubmission.ChooseSleeveBloom", "drawn tile drifted from the frozen first candidate"),
            Bound("sleeveBloomHandler", "sb-choose-stale-index", "StaleOffer", "SLEEVE_BLOOM_STALE_OFFER", CheatErrorCode.StaleOffer,
                "CheatSubmission.ChooseSleeveBloom", "second candidate slot drifted from the first available wall index"),
            Bound("sleeveBloomHandler", "sb-choose-offer-mismatch", "Rejected", "SLEEVE_BLOOM_OFFER_MISMATCH", CheatErrorCode.OfferMismatch,
                "CheatSubmission.ChooseSleeveBloom", "request offer id differs from the frozen offer"),
            Bound("sleeveBloomHandler", "sb-choose-no-offer", "Rejected", "SLEEVE_BLOOM_NO_PENDING_OFFER", CheatErrorCode.NoPendingOffer,
                "CheatSubmission.ChooseSleeveBloom", "nothing pending"),
            Bound("sleeveBloomHandler", "sb-choose-rinshan", "Allowed", null, CheatErrorCode.None,
                "CheatSubmission.ChooseSleeveBloom", "rinshan offer, keep SECOND"),
            Bound("sleeveBloomHandler", "sbo-activate-allowed", "Allowed", null, CheatErrorCode.None,
                "CheatSubmission.ActivateOffTurnSleeveBloom", "seat 2 off-turn window on seat 0 DISCARD"),
            Refined("sleeveBloomHandler", "sbo-activate-no-window", "Rejected", "SLEEVE_BLOOM_NO_WINDOW", CheatErrorCode.WindowMismatch,
                "CheatSubmission.ActivateOffTurnSleeveBloom", "V4 collapses mismatch to NO_WINDOW; C# splits NoWindow/WindowMismatch"),
            Bound("sleeveBloomHandler", "sbo-activate-reserved", "Rejected", "SLEEVE_BLOOM_RESERVED_PENDING", CheatErrorCode.ReservedPending,
                "CheatSubmission.ActivateOffTurnSleeveBloom", "reservation outstanding"),
            Bound("sleeveBloomHandler", "sbo-activate-own-turn", "Rejected", "NOT_YOUR_TURN", CheatErrorCode.NotYourTurn,
                "CheatSubmission.ActivateOffTurnSleeveBloom", "own turn uses the immediate path"),
            Bound("sleeveBloomHandler", "sbo-activate-wall-empty", "Rejected", "SLEEVE_BLOOM_WALL_EMPTY", CheatErrorCode.WallEmpty,
                "CheatSubmission.ActivateOffTurnSleeveBloom", "fewer than two live tiles"),
            Bound("sleeveBloomHandler", "sbo-choose-allowed", "Allowed", null, CheatErrorCode.None,
                "CheatSubmission.ChooseOffTurnSleeveBloom", "before deadline, both candidates in wall"),
            Bound("sleeveBloomHandler", "sbo-choose-expired", "Rejected", "DEADLINE_EXPIRED", CheatErrorCode.DeadlineExpired,
                "CheatSubmission.ChooseOffTurnSleeveBloom", "now equals the deadline; settles FIRST with state"),
            Bound("sleeveBloomHandler", "sbo-choose-stale-first", "Rejected", "SLEEVE_BLOOM_STALE_OFFER", CheatErrorCode.StaleOffer,
                "CheatSubmission.ChooseOffTurnSleeveBloom", "first candidate drawn away; plain rejection keeps the offer"),
            Bound("sleeveBloomHandler", "sbo-choose-stale-second", "Rejected", "SLEEVE_BLOOM_STALE_OFFER", CheatErrorCode.StaleOffer,
                "CheatSubmission.ChooseOffTurnSleeveBloom", "second candidate drawn away; plain rejection keeps the offer")
        };

        private static readonly CaseSpec[] BeamSwapSpecs =
        {
            Bound("beamSwapHandler", "bm-activate-allowed", "Allowed", null, CheatErrorCode.None,
                "CheatSubmission.ActivateBeamSwap", "own normal draw, live-wall candidates"),
            Refined("beamSwapHandler", "bm-activate-no-candidates", "Rejected", "BEAM_SWAP_NO_CANDIDATES", CheatErrorCode.NoWallCandidate,
                "CheatSubmission.ActivateBeamSwap", "wall exhausted"),
            Bound("beamSwapHandler", "bm-activate-dealer-extra", "Rejected", "BEAM_SWAP_DEALER_EXTRA_EXCLUDED", CheatErrorCode.DealExtraDraw,
                "CheatSubmission.ActivateBeamSwap", "deal-extra draw evidence"),
            Bound("beamSwapHandler", "bm-activate-riichi", "Rejected", "BEAM_SWAP_RIICHI_LOCKED", CheatErrorCode.RiichiLocked,
                "CheatSubmission.ActivateBeamSwap", "riichi declared"),
            Bound("beamSwapHandler", "bm-activate-no-drawn", "Rejected", "BEAM_SWAP_NO_DRAWN_TILE", CheatErrorCode.NoDrawnTile,
                "CheatSubmission.ActivateBeamSwap", "no tracked draw"),
            Bound("beamSwapHandler", "bm-activate-beam-pending", "Rejected", "BEAM_SWAP_ALREADY_PENDING", CheatErrorCode.OfferAlreadyPending,
                "CheatSubmission.ActivateBeamSwap", "offer already frozen"),
            Bound("beamSwapHandler", "bm-activate-sleeve-pending", "Rejected", "BEAM_SWAP_ALREADY_PENDING", CheatErrorCode.OfferAlreadyPending,
                "CheatSubmission.ActivateBeamSwap", "mutual exclusion"),
            Bound("beamSwapHandler", "bm-activate-already-used", "Rejected", "BEAM_SWAP_ALREADY_USED_THIS_DRAW", CheatErrorCode.AlreadyUsedThisTurn,
                "CheatSubmission.ActivateBeamSwap", "same turn number"),
            Refined("beamSwapHandler", "bm-activate-disabled", "Rejected", "BEAM_SWAP_DISABLED", CheatErrorCode.CostMustBeTen,
                "CheatSubmission.ActivateBeamSwap", "V4 input cost=5; C# splits the disabled family into CostMustBeTen"),
            Bound("beamSwapHandler", "bm-activate-insufficient", "Rejected", "INSUFFICIENT_FUNDS", CheatErrorCode.InsufficientFunds,
                "CheatSubmission.ActivateBeamSwap", "five points below cost ten"),
            Bound("beamSwapHandler", "bm-activate-deadwall-allowed", "Allowed", null, CheatErrorCode.None,
                "CheatSubmission.ActivateBeamSwap", "rinshan window with undrawn rinshan, back half and replacement"),
            Blocked("beamSwapHandler", "bm-activate-deadwall-empty", "Rejected", "BEAM_SWAP_NO_CANDIDATES",
                BindingKind.UnrepresentableBlocked, "physical dead-wall map with no eligible candidate",
                "V4 handler input allows deadWallTileIdsInDeadWall=empty while the 14 dead-wall tiles still exist elsewhere; C# requires exactly 14 effective dead-wall/replacement tiles",
                "needs a handler-level deadWallTileIdsInDeadWall input (or a production candidate-suppression surface); canonical TilePositionMap cannot encode this filter state"),
            Blocked("beamSwapHandler", "bm-activate-deadwall-dora-only", "Rejected", "BEAM_SWAP_NO_CANDIDATES",
                BindingKind.UnrepresentableBlocked, "physical dead-wall map with only the dora indicator located in the dead wall",
                "same non-canonical filter state: the other 13 dead-wall entities must still occupy slots/replacements under C# conservation",
                "needs the handler-level deadWallTileIdsInDeadWall input; the dora exclusion itself is separately bound by BeamSwap_DoraIndicator_IsNeverACandidate"),
            Bound("beamSwapHandler", "bm-choose-swap", "Allowed", null, CheatErrorCode.None,
                "CheatSubmission.ChooseBeamSwap", "wall slot candidate consistent"),
            Bound("beamSwapHandler", "bm-choose-keep", "Allowed", null, CheatErrorCode.None,
                "CheatSubmission.ChooseBeamSwap", "keep original takes no arguments"),
            Refined("beamSwapHandler", "bm-choose-invalid-slot", "Rejected", "BEAM_SWAP_INVALID_CHOICE", CheatErrorCode.CandidateNotFound,
                "CheatSubmission.ChooseBeamSwap", "slot outside the frozen candidates; C# refines to CandidateNotFound"),
            Bound("beamSwapHandler", "bm-choose-invalid-foreign-hand", "Rejected", "BEAM_SWAP_INVALID_CHOICE", CheatErrorCode.InvalidSelection,
                "CheatSubmission.ChooseBeamSwap", "drawn tile is not in the frozen eligible set"),
            Refined("beamSwapHandler", "bm-keep-original-args-forbidden-at-request", "Rejected", "BEAM_SWAP_INVALID_CHOICE", CheatErrorCode.InvalidSelection,
                "CheatSubmission.ChooseBeamSwap", "V4 rejects at request construction; C# rejects at the command entry"),
            Bound("beamSwapHandler", "bm-choose-stale-turn", "StaleOffer", "BEAM_SWAP_STALE_OFFER", CheatErrorCode.StaleOffer,
                "CheatSubmission.ChooseBeamSwap", "offer created on turn twelve"),
            Bound("beamSwapHandler", "bm-choose-stale-drawn", "StaleOffer", "BEAM_SWAP_STALE_OFFER", CheatErrorCode.StaleOffer,
                "CheatSubmission.ChooseBeamSwap", "drawn tile drifted from the frozen offer"),
            Bound("beamSwapHandler", "bm-choose-deadwall-allowed", "Allowed", null, CheatErrorCode.None,
                "CheatSubmission.ChooseBeamSwap", "undrawn rinshan slot candidate consistent"),
            Bound("beamSwapHandler", "bm-choose-deadwall-dora-stale", "StaleOffer", "BEAM_SWAP_STALE_OFFER", CheatErrorCode.StaleOffer,
                "CheatSubmission.ChooseBeamSwap", "dora indicator slot is never a valid candidate"),
            Bound("beamSwapHandler", "bm-choose-replacement-allowed", "Allowed", null, CheatErrorCode.None,
                "CheatSubmission.ChooseBeamSwap", "replacement slot candidate consistent")
        };

        private static readonly (string Section, string CaseId, BindingKind Kind, string Entry, string Note)[] MovementSpecs =
        {
            ("tileMovement", "mv-swap-hand-with-wall-direct", BindingKind.Bound,
                "TilePositionMap.SwapHandWithWall", "V4 wall[0] maps to C# Wall(53); exact reconstructed table"),
            ("tileMovement", "mv-swap-drawn-with-wall-backfill", BindingKind.Bound,
                "CheatSubmission.ChooseSleeveBloom(SECOND)", "drawn tile re-enters Wall(54), displaced wall tile becomes the new drawn evidence; V4 hole backfill is not materialised"),
            ("tileMovement", "mv-reservation-delivery", BindingKind.Bound,
                "CheatSubmission.DeliverSleeveBloomReservation", "reserved tile leaves the live wall by removeAt semantics"),
            ("tileMovement", "mv-reservation-delivery-then-beam", BindingKind.Bound,
                "DeliverSleeveBloomReservation -> ActivateBeamSwap -> ChooseBeamSwap", "full exported input placements and candidate slots"),
            ("tileMovement", "mv-rinshan-and-drawn-swap", BindingKind.Bound,
                "TilePositionMap.DrawReplacement + SwapDrawnWithNextRinshan", "rinshan pointer, dead-wall slot and replacement"),
            ("tileMovement", "mv-swap-hand-with-dead-wall", BindingKind.Bound,
                "DrawReplacement + SwapHandWithDeadWall + SwapHandWithReplacement", "dead-wall/replacement swap and dora/wall refusal")
        };

        private static IEnumerable<CaseSpec> AllSpecs =>
            TileMarkSpecs.Concat(SleeveBloomSpecs).Concat(BeamSwapSpecs);

        private static CaseSpec Spec(string section, string caseId) =>
            AllSpecs.Single(s => s.Section == section && s.CaseId == caseId);

        public static IEnumerable<object[]> TileMarkBoundCases =>
            TileMarkSpecs.Where(s => s.IsBehaviourBound).Select(s => new object[] { s.CaseId });

        public static IEnumerable<object[]> SleeveBloomBoundCases =>
            SleeveBloomSpecs.Where(s => s.IsBehaviourBound).Select(s => new object[] { s.CaseId });

        public static IEnumerable<object[]> BeamSwapBoundCases =>
            BeamSwapSpecs.Where(s => s.IsBehaviourBound).Select(s => new object[] { s.CaseId });

        // ------------------------------------------------------------------
        // Denominator and fixture agreement. A missing/extra row or a silently converted
        // blocker fails here before any behaviour test can claim coverage.
        // ------------------------------------------------------------------

        [Fact]
        public void BindingManifest_PreservesTheFullDenominator()
        {
            JsonElement root = Golden();
            Assert.Equal(Schema, Str(root, "schema"));
            Assert.Equal(20, TileMarkSpecs.Length);
            Assert.Equal(32, SleeveBloomSpecs.Length);
            Assert.Equal(23, BeamSwapSpecs.Length);
            Assert.Equal(6, MovementSpecs.Length);
            Assert.Equal(75, AllSpecs.Count());

            Assert.Equal(
                Cases(root, "tileMarkHandler").Select(c => Str(c, "caseId")).ToArray(),
                TileMarkSpecs.Select(s => s.CaseId).ToArray());
            Assert.Equal(
                Cases(root, "sleeveBloomHandler").Select(c => Str(c, "caseId")).ToArray(),
                SleeveBloomSpecs.Select(s => s.CaseId).ToArray());
            Assert.Equal(
                Cases(root, "beamSwapHandler").Select(c => Str(c, "caseId")).ToArray(),
                BeamSwapSpecs.Select(s => s.CaseId).ToArray());
            Assert.Equal(
                Cases(root, "tileMovement").Select(c => Str(c, "caseId")).ToArray(),
                MovementSpecs.Select(s => s.CaseId).ToArray());

            foreach (CaseSpec spec in AllSpecs)
            {
                JsonElement c = Case(root, spec.Section, spec.CaseId);
                Assert.Equal(spec.Outcome, Str(c, "outcome"));
                if (spec.Reason == null)
                {
                    Assert.Equal(JsonValueKind.Null, c.GetProperty("reason").ValueKind);
                }
                else
                {
                    Assert.Equal(spec.Reason, Str(c, "reason"));
                }

                Assert.False(string.IsNullOrWhiteSpace(spec.Entry));
                Assert.False(string.IsNullOrWhiteSpace(spec.Note));
                if (spec.IsBehaviourBound)
                {
                    Assert.True(spec.Code.HasValue, spec.CaseId + " must declare a C# code");
                }
                else
                {
                    Assert.False(string.IsNullOrWhiteSpace(spec.Dependency),
                        spec.CaseId + " blocked row must name the remaining dependency");
                }
            }

            string[] blocked = AllSpecs.Where(s => !s.IsBehaviourBound).Select(s => s.CaseId).ToArray();
            Assert.Equal(new[]
            {
                "tm-wrong-phase-round-end",
                "tm-capacity-tiles",
                "tm-no-active-decision",
                "tm-deadline-expired",
                "tm-decision-mismatch",
                "sb-activate-wall-empty-drift",
                "bm-activate-deadwall-empty",
                "bm-activate-deadwall-dora-only"
            }, blocked);
            Assert.Equal(67, AllSpecs.Count(s => s.IsBehaviourBound));
            Assert.Equal(4, AllSpecs.Count(s => s.Kind == BindingKind.ActorLayerBlocked));
            Assert.Equal(4, AllSpecs.Count(s => s.Kind == BindingKind.UnrepresentableBlocked));
        }

        // ------------------------------------------------------------------
        // Shared assertion helpers.
        // ------------------------------------------------------------------

        private sealed class Snapshot
        {
            public int[] Balances = Array.Empty<int>();
            public int EntryCount;
            public string Entries = string.Empty;
            public long MarkVersion;
            public int GroupCount;
            public string Marks = string.Empty;
            public long SleeveVersion;
            public int ImmediateOfferCount;
            public int OffTurnOfferCount;
            public int ReservationCount;
            public string Sleeve = string.Empty;
            public long BeamVersion;
            public bool BeamOffer;
            public string Beam = string.Empty;
            public ulong Fingerprint;
            public string Locations = string.Empty;

            public static Snapshot Capture(CheatState state, TilePositionMap map)
            {
                string entries = string.Join("|", state.Ledger.Entries.Select(e =>
                    e.EntryId + "/" + e.Seat + "/" + e.Amount + "/" + e.BalanceAfter + "/" + e.Reason));
                string marks = string.Join("|", state.TileMarks.Groups.Select(g =>
                    g.GroupId + "/" + g.OwnerSeat + "/" + string.Join(",", g.TileIds.Select(t => t.Value)) +
                    "/" + g.CreatedRoundId + "/" + g.CreatedRoundOrdinal + "/" + g.ExpiresAfterRoundOrdinal)) +
                    "#" + string.Join(",", state.TileMarks.DealtRoundIds) +
                    "#" + string.Join(",", state.TileMarks.SettledRoundIds);
                string sleeve = (state.SleeveBloom.ImmediateOffer?.ToString() ?? "-") + "#" +
                    string.Join("|", state.SleeveBloom.OffTurnOffers.Select(o => o.ToString())) + "#" +
                    string.Join("|", state.SleeveBloom.Reservations.Select(r => r.ToString())) + "#" +
                    state.SleeveBloom.UsedOnTurn;
                string beam = (state.BeamSwap.Offer?.ToString() ?? "-") + "#" + state.BeamSwap.UsedOnTurn;
                return new Snapshot
                {
                    Balances = state.Ledger.Balances.ToArray(),
                    EntryCount = state.Ledger.Entries.Count,
                    Entries = entries,
                    MarkVersion = state.TileMarks.Version,
                    GroupCount = state.TileMarks.Groups.Count,
                    Marks = marks,
                    SleeveVersion = state.SleeveBloom.Version,
                    ImmediateOfferCount = state.SleeveBloom.ImmediateOffer == null ? 0 : 1,
                    OffTurnOfferCount = state.SleeveBloom.OffTurnOffers.Count,
                    ReservationCount = state.SleeveBloom.Reservations.Count,
                    Sleeve = sleeve,
                    BeamVersion = state.BeamSwap.Version,
                    BeamOffer = state.BeamSwap.Offer != null,
                    Beam = beam,
                    Fingerprint = state.Fingerprint,
                    Locations = CheatTestBench.Locations(map)
                };
            }
        }

        private static void AssertUnchanged(Snapshot before, CheatState state, TilePositionMap map)
        {
            Assert.Equal(before.Balances, state.Ledger.Balances.ToArray());
            Assert.Equal(before.EntryCount, state.Ledger.Entries.Count);
            Assert.Equal(before.Entries, string.Join("|", state.Ledger.Entries.Select(e =>
                e.EntryId + "/" + e.Seat + "/" + e.Amount + "/" + e.BalanceAfter + "/" + e.Reason)));
            Assert.Equal(before.MarkVersion, state.TileMarks.Version);
            Assert.Equal(before.GroupCount, state.TileMarks.Groups.Count);
            Assert.Equal(before.Marks, string.Join("|", state.TileMarks.Groups.Select(g =>
                g.GroupId + "/" + g.OwnerSeat + "/" + string.Join(",", g.TileIds.Select(t => t.Value)) +
                "/" + g.CreatedRoundId + "/" + g.CreatedRoundOrdinal + "/" + g.ExpiresAfterRoundOrdinal)) +
                "#" + string.Join(",", state.TileMarks.DealtRoundIds) +
                "#" + string.Join(",", state.TileMarks.SettledRoundIds));
            Assert.Equal(before.SleeveVersion, state.SleeveBloom.Version);
            Assert.Equal(before.ImmediateOfferCount, state.SleeveBloom.ImmediateOffer == null ? 0 : 1);
            Assert.Equal(before.OffTurnOfferCount, state.SleeveBloom.OffTurnOffers.Count);
            Assert.Equal(before.ReservationCount, state.SleeveBloom.Reservations.Count);
            Assert.Equal(before.Sleeve, (state.SleeveBloom.ImmediateOffer?.ToString() ?? "-") + "#" +
                string.Join("|", state.SleeveBloom.OffTurnOffers.Select(o => o.ToString())) + "#" +
                string.Join("|", state.SleeveBloom.Reservations.Select(r => r.ToString())) + "#" +
                state.SleeveBloom.UsedOnTurn);
            Assert.Equal(before.BeamVersion, state.BeamSwap.Version);
            Assert.Equal(before.BeamOffer, state.BeamSwap.Offer != null);
            Assert.Equal(before.Beam, (state.BeamSwap.Offer?.ToString() ?? "-") + "#" + state.BeamSwap.UsedOnTurn);
            Assert.Equal(before.Fingerprint, state.Fingerprint);
            Assert.Equal(before.Locations, CheatTestBench.Locations(map));
            CheatTestBench.AssertConserved(map);
        }

        private static void AssertFixture(CaseSpec spec)
        {
            JsonElement c = Case(Golden(), spec.Section, spec.CaseId);
            Assert.Equal(spec.Outcome, Str(c, "outcome"));
            if (spec.Reason == null)
            {
                Assert.Equal(JsonValueKind.Null, c.GetProperty("reason").ValueKind);
            }
            else
            {
                Assert.Equal(spec.Reason, Str(c, "reason"));
            }
        }

        private static void AssertRejected(
            CaseSpec spec, CheatTransition actual, Snapshot before, CheatState state, TilePositionMap map)
        {
            AssertFixture(spec);
            Assert.False(actual.Success, spec.CaseId + " must reject: " + actual.Message);
            Assert.Equal(spec.Code, actual.Error);
            Assert.False(actual.Changed, spec.CaseId + " must not change state");
            Assert.Null(actual.State);
            Assert.Null(actual.Map);
            Assert.Empty(actual.Events);
            AssertUnchanged(before, state, map);
        }

        private static void AssertStaleWithState(
            CaseSpec spec,
            CheatTransition actual,
            Snapshot before,
            CheatState state,
            TilePositionMap map,
            CheatState expectedState,
            IReadOnlyList<CheatEvent> expectedEvents)
        {
            AssertFixture(spec);
            Assert.False(actual.Success, spec.CaseId + " must be stale: " + actual.Message);
            Assert.Equal(CheatErrorCode.StaleOffer, actual.Error);
            Assert.True(actual.Changed);
            Assert.NotNull(actual.State);
            Assert.Null(actual.Map);
            AssertUnchanged(before, state, map);
            AssertUnchanged(Snapshot.Capture(expectedState, map), actual.State!, map);
            Assert.Equal(expectedEvents.ToArray(), actual.Events.ToArray());
            CheatTestBench.AssertConserved(map);
        }

        private static void AssertExpiredWithState(
            CaseSpec spec,
            CheatTransition actual,
            Snapshot before,
            CheatState state,
            TilePositionMap map,
            CheatState expectedState,
            IReadOnlyList<CheatEvent> expectedEvents)
        {
            AssertFixture(spec);
            Assert.False(actual.Success, spec.CaseId + " must settle expired: " + actual.Message);
            Assert.Equal(CheatErrorCode.DeadlineExpired, actual.Error);
            Assert.True(actual.Changed);
            Assert.NotNull(actual.State);
            Assert.Null(actual.Map);
            AssertUnchanged(before, state, map);
            AssertUnchanged(Snapshot.Capture(expectedState, map), actual.State!, map);
            Assert.Equal(expectedEvents.ToArray(), actual.Events.ToArray());
            CheatTestBench.AssertConserved(map);
        }

        private static void AssertCharged(
            CheatTransition actual, int seat, string entryId, string reason, int balanceBefore)
        {
            Assert.True(actual.Success, actual.Message);
            Assert.True(actual.Changed);
            Assert.NotNull(actual.State);
            Assert.NotEmpty(actual.Events);
            CheatEvent charge = actual.Events[0];
            Assert.Equal(CheatEventKind.CheatPointsChanged, charge.Kind);
            Assert.Equal(seat, charge.Seat);
            Assert.Equal(entryId, charge.EntryId);
            Assert.Equal(-10, charge.Amount);
            Assert.Equal(balanceBefore - 10, charge.NewBalance);
            CheatLedgerEntry entry = actual.State!.Ledger.Entries.Last();
            Assert.Equal(entryId, entry.EntryId);
            Assert.Equal(seat, entry.Seat);
            Assert.Equal(-10, entry.Amount);
            Assert.Equal(balanceBefore - 10, entry.BalanceAfter);
            Assert.Equal(reason, entry.Reason);
        }

        // ------------------------------------------------------------------
        // Tile mark: every behaviour-bound case through ApplyTileMark.
        // ------------------------------------------------------------------

        [Theory]
        [MemberData(nameof(TileMarkBoundCases))]
        public void TileMark_BoundCase_ReplaysThroughApplyTileMark(string caseId)
        {
            CaseSpec spec = Spec("tileMarkHandler", caseId);
            CheatState state = NewState();
            TilePositionMap map = TileMarkBaseMap();
            CheatTurnContext ctx = Ctx(0, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal);
            int seat = 0;
            string requestId = "req-1";
            string groupId = "g1";
            TileId[] tiles = { new TileId(0), new TileId(4), new TileId(8), new TileId(12) };

            switch (caseId)
            {
                case "tm-allowed":
                    break;
                case "tm-not-your-turn":
                    seat = 1;
                    break;
                case "tm-wrong-phase-preparation":
                    ctx = Ctx(0, RoundPhase.Stacking, SleeveBloomDrawKind.Normal);
                    break;
                case "tm-wrong-phase-draw":
                    ctx = Ctx(0, RoundPhase.Draw, SleeveBloomDrawKind.Normal);
                    break;
                case "tm-response-window":
                    ctx = Ctx(0, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal, responseWindow: true);
                    break;
                case "tm-riichi-locked":
                    ctx = Ctx(0, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal, riichi: true);
                    break;
                case "tm-invalid-selection-three":
                    tiles = new[] { new TileId(0), new TileId(4), new TileId(8) };
                    break;
                case "tm-invalid-selection-duplicate":
                    tiles = new[] { new TileId(0), new TileId(0), new TileId(4), new TileId(8) };
                    break;
                case "tm-tile-not-owned":
                    tiles = new[] { new TileId(52), new TileId(56), new TileId(60), new TileId(64) };
                    break;
                case "tm-already-marked":
                    state = WithMarks(state, new[]
                    {
                        new TileMarkGroup("m1", 0, new[] { new TileId(8), new TileId(12), new TileId(20), new TileId(24) }, RoundId, 1)
                    });
                    break;
                case "tm-capacity-groups":
                    state = WithMarks(state, new[]
                    {
                        new TileMarkGroup("m1", 0, new[] { new TileId(0), new TileId(1), new TileId(2), new TileId(3) }, RoundId, 1),
                        new TileMarkGroup("m2", 0, new[] { new TileId(4), new TileId(5), new TileId(6), new TileId(7) }, RoundId, 1),
                        new TileMarkGroup("m3", 0, new[] { new TileId(8), new TileId(9), new TileId(10), new TileId(11) }, RoundId, 1)
                    });
                    tiles = new[] { new TileId(12), new TileId(16), new TileId(20), new TileId(24) };
                    break;
                case "tm-insufficient-funds":
                    state = NewState(5);
                    break;
                case "tm-sleeve-pending":
                    state = WithImmediateOffer(state, 0, new TileId(16), new TileId(36));
                    break;
                case "tm-offturn-sleeve-pending":
                    state = WithOffTurnOffer(state, 0, new TileId(16), new TileId(36));
                    break;
                case "tm-beam-pending":
                    state = WithBeamOffer(
                        state, new TileId(16), map.TileAt(TilePosition.Wall(WallSlotOffset)), new TileId(0));
                    break;
                default:
                    throw new InvalidOperationException("no tile-mark binding for " + caseId);
            }

            Snapshot before = Snapshot.Capture(state, map);
            CheatTransition actual = CheatSubmission.ApplyTileMark(
                state, map, ctx, CheatTestBench.Economy(), seat, requestId, groupId, tiles);

            if (caseId == "tm-allowed")
            {
                AssertFixture(spec);
                AssertCharged(actual, 0, "mark-0-req-1", "TILE_MARK", 40);
                Assert.Equal(2, actual.Events.Count);
                Assert.Equal(CheatEventKind.TileMarkApplied, actual.Events[1].Kind);
                Assert.Equal(seat, actual.Events[1].Seat);
                Assert.Equal(groupId, actual.Events[1].GroupId);
                Assert.Null(actual.Map);
                Assert.Single(actual.State!.TileMarks.Groups);
                TileMarkGroup group = actual.State.TileMarks.Groups[0];
                Assert.Equal(groupId, group.GroupId);
                Assert.Equal(seat, group.OwnerSeat);
                Assert.Equal(tiles.Select(t => t.Value).ToArray(), group.TileIds.Select(t => t.Value).ToArray());
                Assert.Equal(RoundId, group.CreatedRoundId);
                Assert.Equal(1, group.CreatedRoundOrdinal);
                Assert.Equal(4, group.ExpiresAfterRoundOrdinal);
                Assert.Equal(30, actual.State.Ledger.BalanceOf(seat));
                AssertUnchanged(before, state, map);
            }
            else
            {
                AssertRejected(spec, actual, before, state, map);
            }
        }

        private static void AssertSleeveActivation(
            CaseSpec spec,
            CheatTransition actual,
            CheatState input,
            TilePositionMap map,
            CheatEconomyConfig economy,
            int seat,
            SleeveBloomDrawKind drawKind,
            TileId first,
            TileId second,
            int points)
        {
            AssertFixture(spec);
            AssertCharged(actual, seat, "bloom-" + seat + "-req-1", "SLEEVE_BLOOM", points);
            Assert.Null(actual.Map);
            SleeveBloomOffer offer = actual.State!.SleeveBloom.ImmediateOffer!;
            Assert.Equal("bloom-" + seat + "-req-1", offer.OfferId);
            Assert.Equal(seat, offer.Seat);
            Assert.Equal(drawKind, offer.DrawKind);
            Assert.Equal(first, offer.FirstTile);
            Assert.Equal(second, offer.SecondTile);
            Assert.Equal(TurnNumber, offer.CreatedTurnNumber);
            Assert.Equal(SleeveBloomOfferMode.Immediate, offer.Mode);
            Assert.Equal(points - 10, actual.State.Ledger.BalanceOf(seat));
            Assert.Equal(points, input.Ledger.BalanceOf(seat));
            Assert.Equal(10, economy.Costs[CheatEconomyConfig.SleeveBloomCostKey]);
            CheatTestBench.AssertConserved(map);
        }

        [Theory]
        [MemberData(nameof(SleeveBloomBoundCases))]
        public void SleeveBloom_BoundCase_ReplaysThroughDomainEntry(string caseId)
        {
            CaseSpec spec = Spec("sleeveBloomHandler", caseId);
            CheatState state = NewState();
            TilePositionMap map = SleeveBaseMap();
            CheatTurnContext ctx = Ctx(0, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal);
            CheatEconomyConfig economy = CheatTestBench.Economy();
            int seat = 0;
            string requestId = "req-1";

            switch (caseId)
            {
                case "sb-activate-allowed":
                case "sb-activate-dealer-extra":
                case "sb-activate-already-used":
                case "sb-activate-beam-pending":
                case "sb-activate-sleeve-pending":
                case "sb-activate-wall-empty":
                case "sb-activate-no-drawn-tile":
                case "sb-activate-not-your-turn":
                case "sb-activate-wrong-phase":
                case "sb-activate-response-window":
                case "sb-activate-disabled":
                case "sb-activate-insufficient":
                case "sb-activate-rinshan-allowed":
                case "sb-activate-rinshan-empty":
                    break;
                default:
                    break;
            }

            if (caseId == "sb-activate-dealer-extra")
            {
                map = SleeveBaseMap(drawnSource: DrawSource.DealExtra);
            }
            else if (caseId == "sb-activate-already-used")
            {
                state = state.WithSleeveBloom(state.SleeveBloom.WithUsedOnTurn(TurnNumber));
            }
            else if (caseId == "sb-activate-beam-pending")
            {
                state = WithBeamOffer(
                    state, new TileId(16), map.TileAt(TilePosition.Wall(WallSlotOffset)), new TileId(0));
            }
            else if (caseId == "sb-activate-sleeve-pending")
            {
                state = WithImmediateOffer(state, 0, new TileId(16), new TileId(36));
            }
            else if (caseId == "sb-activate-wall-empty")
            {
                map = SleeveBaseMap(wallCount: 0);
            }
            else if (caseId == "sb-activate-no-drawn-tile")
            {
                map = SleeveBaseMap(withoutDrawn: true);
            }
            else if (caseId == "sb-activate-not-your-turn")
            {
                seat = 1;
            }
            else if (caseId == "sb-activate-wrong-phase")
            {
                ctx = Ctx(0, RoundPhase.Discard, SleeveBloomDrawKind.Normal);
            }
            else if (caseId == "sb-activate-response-window")
            {
                ctx = Ctx(0, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal, responseWindow: true);
            }
            else if (caseId == "sb-activate-disabled")
            {
                economy = EconomyWithCosts(40, 10, 5, 10);
                state = NewState(economy);
            }
            else if (caseId == "sb-activate-insufficient")
            {
                state = NewState(5);
            }
            else if (caseId == "sb-activate-rinshan-allowed")
            {
                map = SleeveRinshanMap();
                ctx = Ctx(0, RoundPhase.CheatWindow, SleeveBloomDrawKind.Rinshan);
            }
            else if (caseId == "sb-activate-rinshan-empty")
            {
                map = SleeveRinshanMap(4);
                ctx = Ctx(0, RoundPhase.CheatWindow, SleeveBloomDrawKind.Rinshan);
            }

            if (caseId.StartsWith("sb-activate-", StringComparison.Ordinal))
            {
                Snapshot before = Snapshot.Capture(state, map);
                CheatTransition actual = CheatSubmission.ActivateSleeveBloom(
                    state, map, ctx, economy, CheatTestBench.Sleeve(), seat, requestId);

                if (caseId == "sb-activate-allowed")
                {
                    JsonElement golden = Case(Golden(), "sleeveBloomHandler", caseId).GetProperty("goldenInputs");
                    AssertKeys(golden, ActivationKeys);
                    AssertSleeveActivation(spec, actual, state, map, economy, 0,
                        SleeveBloomDrawKind.Normal, new TileId(16), new TileId(32), 40);
                    SleeveBloomOffer offer = actual.State!.SleeveBloom.ImmediateOffer!;
                    Assert.Equal(GoldenInt(golden, "seat"), offer.Seat);
                    Assert.Equal(SleeveBloomDrawKind.Normal, GoldenEnum<SleeveBloomDrawKind>(golden, "drawKind"));
                    Assert.Equal(GoldenInt(golden, "drawnTileId"), offer.FirstTile.Value);
                    Assert.Equal(GoldenInt(golden, "nextWallTileId"), offer.SecondTile.Value);
                    Assert.Equal(GoldenInt(golden, "nextWallTileId"),
                        map.TileAt(TilePosition.Wall(map.WallPointer)).Value);
                    Assert.Null(GoldenNullableInt(golden, "nextDeadWallTileId", expectNull: true));
                    Assert.Equal(GoldenInt(golden, "wallPointer"), map.WallPointer);
                    Assert.Equal(GoldenInt(golden, "wallEndExclusive"), map.WallEndExclusive);
                    Assert.Equal(GoldenInt(golden, "rinshanPointer"), map.RinshanPointer);
                    Assert.Equal(GoldenInt(golden, "cheatPoints"), state.Ledger.BalanceOf(0));
                    Assert.Equal(GoldenInt(golden, "cost"), economy.Costs[CheatEconomyConfig.SleeveBloomCostKey]);
                    AssertUnchanged(before, state, map);
                }
                else if (caseId == "sb-activate-rinshan-allowed")
                {
                    JsonElement golden = Case(Golden(), "sleeveBloomHandler", caseId).GetProperty("goldenInputs");
                    AssertKeys(golden, ActivationKeys);
                    AssertSleeveActivation(spec, actual, state, map, economy, 0,
                        SleeveBloomDrawKind.Rinshan, new TileId(24), new TileId(28), 40);
                    SleeveBloomOffer offer = actual.State!.SleeveBloom.ImmediateOffer!;
                    Assert.Equal(GoldenInt(golden, "seat"), offer.Seat);
                    Assert.Equal(SleeveBloomDrawKind.Rinshan, GoldenEnum<SleeveBloomDrawKind>(golden, "drawKind"));
                    Assert.Equal(GoldenInt(golden, "drawnTileId"), offer.FirstTile.Value);
                    Assert.Equal(GoldenInt(golden, "nextWallTileId"), map.TileAt(TilePosition.Wall(map.WallPointer)).Value);
                    Assert.Equal(GoldenInt(golden, "nextDeadWallTileId"), offer.SecondTile.Value);
                    Assert.Equal(GoldenInt(golden, "wallPointer"), map.WallPointer);
                    Assert.Equal(GoldenInt(golden, "wallEndExclusive"), map.WallEndExclusive);
                    Assert.Equal(GoldenInt(golden, "rinshanPointer"), map.RinshanPointer);
                    Assert.Equal(GoldenInt(golden, "cheatPoints"), state.Ledger.BalanceOf(0));
                    Assert.Equal(GoldenInt(golden, "cost"), economy.Costs[CheatEconomyConfig.SleeveBloomCostKey]);
                    AssertUnchanged(before, state, map);
                }
                else
                {
                    AssertRejected(spec, actual, before, state, map);
                }
            }
            else if (caseId == "sb-choose-first" || caseId == "sb-choose-second")
            {
                CheatTransition activated = CheatSubmission.ActivateSleeveBloom(
                    state, map, ctx, economy, CheatTestBench.Sleeve(), 0, requestId);
                Assert.True(activated.Success, activated.Message);
                SleeveBloomOffer pending = activated.State!.SleeveBloom.ImmediateOffer!;
                string offerId = pending.OfferId;
                JsonElement golden = Case(Golden(), "sleeveBloomHandler", caseId).GetProperty("goldenInputs");
                AssertKeys(golden, ChoiceKeys);
                Assert.Equal(GoldenInt(golden, "seat"), pending.Seat);
                SleeveBloomKeep keep = GoldenEnum<SleeveBloomKeep>(golden, "keep");
                Assert.Equal(caseId == "sb-choose-first" ? SleeveBloomKeep.First : SleeveBloomKeep.Second, keep);
                Assert.Equal(GoldenString(golden, "offerId"), offerId);
                Assert.Equal(GoldenInt(golden, "turnNumber"), ctx.TurnNumber);
                Assert.Equal(GoldenInt(golden, "offerCreatedTurnNumber"), pending.CreatedTurnNumber);
                Assert.Equal(SleeveBloomDrawKind.Normal, GoldenEnum<SleeveBloomDrawKind>(golden, "drawKind"));
                Assert.Equal(GoldenInt(golden, "drawnTileId"), map.DrawnTile!.Value.Tile.Value);
                Assert.Equal(GoldenInt(golden, "offerFirstTileId"), pending.FirstTile.Value);
                Assert.Equal(GoldenInt(golden, "offerSecondTileId"), pending.SecondTile.Value);
                Assert.Equal(GoldenInt(golden, "offerSecondWallIndex"), map.PositionOf(pending.SecondTile).Index);
                Assert.Equal(GoldenInt(golden, "nextAvailableWallIndex"), map.WallPointer);
                Assert.Equal(GoldenInt(golden, "wallPointer"), map.WallPointer);
                Assert.Equal(GoldenInt(golden, "wallEndExclusive"), map.WallEndExclusive);
                Assert.Equal(GoldenInt(golden, "rinshanPointer"), map.RinshanPointer);
                Assert.Equal(GoldenInt(golden, "cost"), economy.Costs[CheatEconomyConfig.SleeveBloomCostKey]);
                string locationsBefore = CheatTestBench.Locations(map);
                CheatTransition chosen = CheatSubmission.ChooseSleeveBloom(
                    activated.State, map, ctx, 0, offerId, keep);
                AssertFixture(spec);
                Assert.True(chosen.Success, chosen.Message);
                Assert.True(chosen.Changed);
                Assert.NotNull(chosen.State);
                Assert.Null(chosen.State!.SleeveBloom.ImmediateOffer);
                Assert.Equal(30, chosen.State.Ledger.BalanceOf(0));
                Assert.Single(chosen.Events);
                Assert.Equal(CheatEventKind.SleeveBloomResolved, chosen.Events[0].Kind);
                Assert.Equal(keep, chosen.Events[0].Keep);
                if (keep == SleeveBloomKeep.First)
                {
                    Assert.Null(chosen.Map);
                    Assert.Equal(locationsBefore, CheatTestBench.Locations(map));
                }
                else
                {
                    TilePositionMap swapped = chosen.Map!;
                    Assert.Equal(TileZone.Wall, swapped.PositionOf(new TileId(16)).Zone);
                    Assert.Equal(WallSlotOffset, swapped.PositionOf(new TileId(16)).Index);
                    Assert.Equal(TileZone.Hand, swapped.PositionOf(new TileId(32)).Zone);
                    Assert.Equal(0, swapped.PositionOf(new TileId(32)).Seat);
                    Assert.Equal(map.WallPointer, swapped.WallPointer);
                    Assert.Equal(new TileId(32), swapped.DrawnTile!.Value.Tile);
                    CheatTestBench.AssertConserved(swapped);
                }
            }
            else if (caseId == "sb-choose-rinshan")
            {
                map = SleeveRinshanMap();
                ctx = Ctx(0, RoundPhase.CheatWindow, SleeveBloomDrawKind.Rinshan);
                CheatTransition activated = CheatSubmission.ActivateSleeveBloom(
                    state, map, ctx, economy, CheatTestBench.Sleeve(), 0, requestId);
                Assert.True(activated.Success, activated.Message);
                SleeveBloomOffer offer = activated.State!.SleeveBloom.ImmediateOffer!;
                JsonElement golden = Case(Golden(), "sleeveBloomHandler", caseId).GetProperty("goldenInputs");
                AssertKeys(golden, ChoiceKeys);
                Assert.Equal(GoldenInt(golden, "seat"), offer.Seat);
                Assert.Equal(SleeveBloomKeep.Second, GoldenEnum<SleeveBloomKeep>(golden, "keep"));
                Assert.Equal(GoldenString(golden, "offerId"), offer.OfferId);
                Assert.Equal(GoldenInt(golden, "turnNumber"), ctx.TurnNumber);
                Assert.Equal(GoldenInt(golden, "offerCreatedTurnNumber"), offer.CreatedTurnNumber);
                Assert.Equal(SleeveBloomDrawKind.Rinshan, GoldenEnum<SleeveBloomDrawKind>(golden, "drawKind"));
                Assert.Equal(GoldenInt(golden, "drawnTileId"), map.DrawnTile!.Value.Tile.Value);
                Assert.Equal(GoldenInt(golden, "offerFirstTileId"), offer.FirstTile.Value);
                Assert.Equal(GoldenInt(golden, "offerSecondTileId"), offer.SecondTile.Value);
                Assert.Null(GoldenNullableInt(golden, "offerSecondWallIndex", expectNull: true));
                Assert.Equal(GoldenInt(golden, "nextAvailableWallIndex"), map.WallPointer);
                Assert.Equal(GoldenInt(golden, "wallPointer"), map.WallPointer);
                Assert.Equal(GoldenInt(golden, "wallEndExclusive"), map.WallEndExclusive);
                Assert.Equal(GoldenInt(golden, "rinshanPointer"), map.RinshanPointer);
                Assert.Equal(GoldenInt(golden, "cost"), economy.Costs[CheatEconomyConfig.SleeveBloomCostKey]);
                CheatTransition chosen = CheatSubmission.ChooseSleeveBloom(
                    activated.State, map, ctx, 0, offer.OfferId, SleeveBloomKeep.Second);
                AssertFixture(spec);
                Assert.True(chosen.Success, chosen.Message);
                TilePositionMap swapped = chosen.Map!;
                Assert.True(swapped.PositionOf(new TileId(24)).Equals(TilePosition.DeadWall(1)));
                Assert.Equal(TileZone.Hand, swapped.PositionOf(new TileId(28)).Zone);
                Assert.Equal(new TileId(28), swapped.DrawnTile!.Value.Tile);
                Assert.Equal(1, swapped.RinshanPointer);
                CheatTestBench.AssertConserved(swapped);
            }
            else if (caseId == "sb-choose-stale-turn")
            {
                CheatTransition activated = CheatSubmission.ActivateSleeveBloom(
                    state, map, ctx, economy, CheatTestBench.Sleeve(), 0, requestId);
                SleeveBloomOffer offer = activated.State!.SleeveBloom.ImmediateOffer!;
                Snapshot before = Snapshot.Capture(activated.State, map);
                CheatTransition actual = CheatSubmission.ChooseSleeveBloom(
                    activated.State, map, Ctx(0, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal, turn: 13),
                    0, offer.OfferId, SleeveBloomKeep.First);
                CheatState expected = activated.State.WithSleeveBloom(
                    activated.State.SleeveBloom.WithClearedImmediateOffer());
                AssertStaleWithState(spec, actual, before, activated.State, map, expected,
                    new[] { CheatEvent.SleeveBloomOfferCleared(0, offer.OfferId, "Stale") });
                Assert.Null(actual.State!.SleeveBloom.ImmediateOffer);
            }
            else if (caseId == "sb-choose-stale-drawn")
            {
                CheatTransition activated = CheatSubmission.ActivateSleeveBloom(
                    state, map, ctx, economy, CheatTestBench.Sleeve(), 0, requestId);
                TilePositionMap drifted = SleeveBaseMap(drawnTile: 20, includeDrawnTileInHand: true);
                SleeveBloomOffer offer = activated.State!.SleeveBloom.ImmediateOffer!;
                Snapshot before = Snapshot.Capture(activated.State, drifted);
                CheatTransition actual = CheatSubmission.ChooseSleeveBloom(
                    activated.State, drifted, ctx, 0, offer.OfferId, SleeveBloomKeep.First);
                CheatState expected = activated.State.WithSleeveBloom(
                    activated.State.SleeveBloom.WithClearedImmediateOffer());
                AssertStaleWithState(spec, actual, before, activated.State, drifted, expected,
                    new[] { CheatEvent.SleeveBloomOfferCleared(0, offer.OfferId, "Stale") });
                Assert.Null(actual.State!.SleeveBloom.ImmediateOffer);
            }
            else if (caseId == "sb-choose-stale-index")
            {
                CheatTransition activated = CheatSubmission.ActivateSleeveBloom(
                    state, map, ctx, economy, CheatTestBench.Sleeve(), 0, requestId);
                SleeveBloomOffer offer = activated.State!.SleeveBloom.ImmediateOffer!;
                Assert.Equal(53, map.PositionOf(offer.SecondTile).Index);
                Assert.Equal(32, offer.SecondTile.Value);
                // V4 input: offerSecondWallIndex=53, nextAvailableWallIndex=54. Keep the
                // frozen candidate at 53, but lock it with a seat-1 reservation so the
                // first *available* wall index becomes 54; do not move the candidate.
                CheatState protectedState = activated.State.WithSleeveBloom(
                    activated.State.SleeveBloom.WithReservation(
                        new SleeveBloomReservation(1, "protect-32", offer.SecondTile, "protect-window", 1_000)));
                Snapshot before = Snapshot.Capture(protectedState, map);
                CheatTransition actual = CheatSubmission.ChooseSleeveBloom(
                    protectedState, map, ctx, 0, offer.OfferId, SleeveBloomKeep.Second);
                CheatState expected = protectedState.WithSleeveBloom(
                    protectedState.SleeveBloom.WithClearedImmediateOffer());
                AssertStaleWithState(spec, actual, before, protectedState, map, expected,
                    new[] { CheatEvent.SleeveBloomOfferCleared(0, offer.OfferId, "Stale") });
                Assert.Null(actual.State!.SleeveBloom.ImmediateOffer);
            }
            else if (caseId == "sb-choose-offer-mismatch")
            {
                CheatTransition activated = CheatSubmission.ActivateSleeveBloom(
                    state, map, ctx, economy, CheatTestBench.Sleeve(), 0, requestId);
                Snapshot before = Snapshot.Capture(activated.State!, map);
                CheatTransition actual = CheatSubmission.ChooseSleeveBloom(
                    activated.State!, map, ctx, 0, "bloom-0-other", SleeveBloomKeep.First);
                AssertRejected(spec, actual, before, activated.State!, map);
                Assert.Equal(before.ImmediateOfferCount, activated.State!.SleeveBloom.ImmediateOffer == null ? 0 : 1);
            }
            else if (caseId == "sb-choose-no-offer")
            {
                Snapshot before = Snapshot.Capture(state, map);
                CheatTransition actual = CheatSubmission.ChooseSleeveBloom(
                    state, map, ctx, 0, "bloom-0-req-1", SleeveBloomKeep.First);
                AssertRejected(spec, actual, before, state, map);
            }
            else if (caseId == "sbo-activate-allowed" || caseId == "sbo-activate-no-window"
                || caseId == "sbo-activate-reserved" || caseId == "sbo-activate-own-turn"
                || caseId == "sbo-activate-wall-empty")
            {
                state = StateWithWindow(state, 2, "bloom-window-2-1");
                ctx = Ctx(0, RoundPhase.Discard, SleeveBloomDrawKind.Normal);
                string requestWindow = "bloom-window-2-1";
                if (caseId == "sbo-activate-no-window")
                {
                    state = StateWithWindow(NewState(), 2, "bloom-window-2-9");
                }
                else if (caseId == "sbo-activate-reserved")
                {
                    state = state.WithSleeveBloom(state.SleeveBloom.WithReservation(
                        new SleeveBloomReservation(2, "bloom-offturn-2-req-1", new TileId(32),
                            "bloom-window-2-1", 1_000)));
                }
                else if (caseId == "sbo-activate-own-turn")
                {
                    ctx = Ctx(2, RoundPhase.Discard, SleeveBloomDrawKind.Normal);
                }
                else if (caseId == "sbo-activate-wall-empty")
                {
                    map = SleeveBaseMap(wallHead: new[] { 32 }, wallCount: 1);
                }

                Snapshot before = Snapshot.Capture(state, map);
                CheatTransition actual = CheatSubmission.ActivateOffTurnSleeveBloom(
                    state, map, ctx, economy, CheatTestBench.Sleeve(), 2, requestId, requestWindow);
                if (caseId == "sbo-activate-allowed")
                {
                    JsonElement golden = Case(Golden(), "sleeveBloomHandler", caseId).GetProperty("goldenInputs");
                    AssertKeys(golden, OffTurnActivationKeys);
                    AssertFixture(spec);
                    AssertCharged(actual, 2, "bloom-offturn-2-req-1", "SLEEVE_BLOOM", 40);
                    Assert.Null(actual.Map);
                    SleeveBloomOffer offer = actual.State!.SleeveBloom.OffTurnOfferFor(2)!;
                    Assert.Equal(GoldenInt(golden, "seat"), offer.Seat);
                    Assert.Equal(GoldenInt(golden, "currentPlayer"), ctx.CurrentPlayer);
                    Assert.Equal(RoundPhase.Discard, GoldenEnum<RoundPhase>(golden, "phase"));
                    Assert.Equal(GoldenString(golden, "windowId"), offer.WindowId);
                    Assert.Equal("bloom-offturn-2-req-1", offer.OfferId);
                    Assert.Equal(SleeveBloomOfferMode.OffTurn, offer.Mode);
                    Assert.Equal(new TileId(32), offer.FirstTile);
                    Assert.Equal(GoldenInt(golden, "availableWallCount"), map.WallEndExclusive - map.WallPointer);
                    Assert.Equal(GoldenInt(golden, "cheatPoints"), state.Ledger.BalanceOf(2));
                    Assert.Equal(GoldenInt(golden, "cost"), economy.Costs[CheatEconomyConfig.SleeveBloomCostKey]);
                    Assert.Equal(11_000, offer.DeadlineMillis);
                }
                else
                {
                    AssertRejected(spec, actual, before, state, map);
                }
            }
            else if (caseId == "sbo-choose-allowed" || caseId == "sbo-choose-expired")
            {
                state = StateWithWindow(state, 2, "bloom-window-2-1");
                CheatTransition activated = CheatSubmission.ActivateOffTurnSleeveBloom(
                    state, map, Ctx(0, RoundPhase.Discard, SleeveBloomDrawKind.Normal),
                    economy, CheatTestBench.Sleeve(), 2, "req-1", "bloom-window-2-1");
                Assert.True(activated.Success, activated.Message);
                SleeveBloomOffer offer = activated.State!.SleeveBloom.OffTurnOfferFor(2)!;
                if (caseId == "sbo-choose-allowed")
                {
                    JsonElement golden = Case(Golden(), "sleeveBloomHandler", caseId).GetProperty("goldenInputs");
                    AssertKeys(golden, OffTurnChoiceKeys);
                    Assert.Equal(GoldenInt(golden, "seat"), offer.Seat);
                    Assert.Equal(SleeveBloomKeep.Second, GoldenEnum<SleeveBloomKeep>(golden, "keep"));
                    Assert.Equal(GoldenString(golden, "offerId"), offer.OfferId);
                    Assert.Equal(1_000, GoldenLong(golden, "now"));
                    Assert.Equal(GoldenLong(golden, "deadlineMillis"), offer.DeadlineMillis);
                    Assert.Equal(GoldenBool(golden, "offerFirstInWall"),
                        map.PositionOf(offer.FirstTile).Zone == TileZone.Wall);
                    Assert.Equal(GoldenBool(golden, "offerSecondInWall"),
                        map.PositionOf(offer.SecondTile).Zone == TileZone.Wall);
                }
                else
                {
                    // The expired case exports only outcome/reason; the offer input is the
                    // same sbo-activate-allowed input (now=1000, deadline=11000).
                    Assert.Equal(1_000, offer.CreatedAtMillis);
                    Assert.Equal(11_000, offer.DeadlineMillis);
                }

                long now = caseId == "sbo-choose-expired" ? offer.DeadlineMillis : 1_000;
                Snapshot before = Snapshot.Capture(activated.State, map);
                CheatTransition chosen = CheatSubmission.ChooseOffTurnSleeveBloom(
                    activated.State, map,
                    Ctx(0, RoundPhase.Discard, SleeveBloomDrawKind.Normal, now: now),
                    2, offer.OfferId, SleeveBloomKeep.Second);
                AssertFixture(spec);
                if (caseId == "sbo-choose-allowed")
                {
                    Assert.True(chosen.Success, chosen.Message);
                    Assert.True(chosen.Changed);
                    Assert.Null(chosen.Map);
                    SleeveBloomReservation reservation = chosen.State!.SleeveBloom.ReservationFor(2)!;
                    Assert.Equal(offer.SecondTile, reservation.Tile);
                    Assert.Equal(offer.OfferId, reservation.OfferId);
                    Assert.Equal(offer.WindowId, reservation.WindowId);
                    Assert.Equal(30, chosen.State.Ledger.BalanceOf(2));
                    Assert.Single(chosen.Events);
                    Assert.Equal(CheatEventKind.SleeveBloomResolved, chosen.Events[0].Kind);
                    Assert.Equal(SleeveBloomKeep.Second, chosen.Events[0].Keep);
                }
                else
                {
                    Assert.True(activated.State.SleeveBloom.TryResolveOffTurnOffer(
                        2, SleeveBloomKeep.First, out SleeveBloomState expired, out SleeveBloomReservation? expectedReservation));
                    Assert.NotNull(expectedReservation);
                    Assert.Equal(offer.FirstTile, expectedReservation!.Tile);
                    CheatState expected = activated.State.WithSleeveBloom(expired);
                    AssertExpiredWithState(spec, chosen, before, activated.State, map, expected,
                        new[] { CheatEvent.SleeveBloomResolved(2, offer.OfferId, SleeveBloomKeep.First) });
                }
            }
            else if (caseId == "sbo-choose-stale-first" || caseId == "sbo-choose-stale-second")
            {
                state = StateWithWindow(state, 2, "bloom-window-2-1");
                TileId first = caseId == "sbo-choose-stale-first" ? new TileId(20) : new TileId(32);
                TileId second = caseId == "sbo-choose-stale-first" ? new TileId(32) : new TileId(20);
                state = WithOffTurnOffer(state, 2, first, second);
                Snapshot before = Snapshot.Capture(state, map);
                CheatTransition actual = CheatSubmission.ChooseOffTurnSleeveBloom(
                    state, map, Ctx(0, RoundPhase.Discard, SleeveBloomDrawKind.Normal),
                    2, "bloom-offturn-2-req-1", SleeveBloomKeep.Second);
                AssertRejected(spec, actual, before, state, map);
                Assert.NotNull(state.SleeveBloom.OffTurnOfferFor(2));
            }
            else
            {
                throw new InvalidOperationException("no sleeve bloom binding for " + caseId);
            }
        }

        // ------------------------------------------------------------------
        // Six physical movements. The V4 constructor inputs are reconstructed exactly; the
        // entity/container/seat/slot/pointer facts are read from the same fixture entry.
        // ------------------------------------------------------------------

        [Fact]
        public void Movement_SwapHandWithWall_BindsFactsBeforeAndAfter()
        {
            JsonElement m = Case(Golden(), "tileMovement", "mv-swap-hand-with-wall-direct");
            var spec = new V4Spec();
            spec.Hands[0] = new List<int> { 0, 4, 8, 12, 20, 24, 28, 32, 1, 5, 9, 13 };
            spec.Drawn = (0, 16);
            spec.WallPrefix.AddRange(new[] { 33, 29, 25 });
            spec.Phase = RoundPhase.CheatWindow;
            spec.CurrentPlayer = 0;
            TilePositionMap map = BuildV4(spec);

            AssertFacts(map, m, "factsBefore", FactSchema.FactsAfter);
            Assert.Equal(WallSlotOffset + Int(m, "wallPointerBefore"), map.WallPointer);
            TileId handTile = new TileId(Int(m, "handTileId"));
            TileId displaced = new TileId(Int(m, "displacedTileId"));
            TilePosition handPosition = map.PositionOf(handTile);
            TransitionResult moved = map.SwapHandWithWall(0, handTile, WallSlotOffset + Int(m, "wallIndex"));

            Assert.True(moved.IsSuccess, moved.Error?.ToString());
            Assert.Equal(displaced, moved.ResultTile!.Value);
            Assert.Equal(TilePosition.Wall(WallSlotOffset + Int(m, "wallIndex")), moved.State!.PositionOf(handTile));
            Assert.Equal(handPosition, moved.State.PositionOf(displaced));
            Assert.Equal(WallSlotOffset + Int(m, "wallPointerAfter"), moved.State.WallPointer);
            Assert.Equal(map.WallEndExclusive, moved.State.WallEndExclusive);
            AssertFacts(moved.State, m, "factsAfter", FactSchema.FactsAfter);
            CheatTestBench.AssertConserved(moved.State);
        }

        [Fact]
        public void Movement_DrawnBackfill_ReplaysThroughSleeveBloomSecondChoice()
        {
            JsonElement m = Case(Golden(), "tileMovement", "mv-swap-drawn-with-wall-backfill");
            var spec = new V4Spec();
            spec.Hands[0] = new List<int> { 0, 4, 8, 12, 20, 24, 28, 32, 1, 5, 9, 13 };
            spec.WallPrefix.AddRange(new[] { 33, 29, 25 });
            spec.Phase = RoundPhase.Draw;
            spec.CurrentPlayer = 0;
            spec.WallLimit = TilePositionMap.LiveWallSlotCount - WallSlotOffset;
            TilePositionMap map = BuildV4(spec);
            Assert.Equal(WallSlotOffset + Int(m, "wallPointerBeforeDraw"), map.WallPointer);

            TilePositionMap drawnMap = map.DrawFromWall(0).RequireState();
            Assert.Equal(Int(m, "drawnTileId"), drawnMap.DrawnTile!.Value.Tile.Value);
            Assert.Equal(WallSlotOffset + Int(m, "wallPointerAfterDraw"), drawnMap.WallPointer);
            CheatTestBench.AssertConserved(drawnMap);

            CheatTurnContext ctx = Ctx(0, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal);
            CheatTransition activated = CheatSubmission.ActivateSleeveBloom(
                NewState(), drawnMap, ctx, CheatTestBench.Economy(), CheatTestBench.Sleeve(), 0, "req-1");
            Assert.True(activated.Success, activated.Message);
            SleeveBloomOffer offer = activated.State!.SleeveBloom.ImmediateOffer!;
            Assert.Equal(new TileId(Int(m, "drawnTileId")), offer.FirstTile);
            Assert.Equal(new TileId(Int(m, "displacedTileId")), offer.SecondTile);
            Assert.Equal(WallSlotOffset + Int(m, "resolvedIndex"),
                drawnMap.PositionOf(offer.SecondTile).Index);

            CheatTransition chosen = CheatSubmission.ChooseSleeveBloom(
                activated.State, drawnMap, ctx, 0, offer.OfferId, SleeveBloomKeep.Second);
            Assert.True(chosen.Success, chosen.Message);
            TilePositionMap moved = chosen.Map!;
            Assert.Equal(TilePosition.Wall(WallSlotOffset + Int(m, "resolvedIndex")),
                moved.PositionOf(new TileId(Int(m, "drawnTileId"))));
            Assert.Equal(drawnMap.PositionOf(new TileId(Int(m, "drawnTileId"))),
                moved.PositionOf(new TileId(Int(m, "displacedTileId"))));
            Assert.Equal(new TileId(Int(m, "backfilledTileId")), moved.DrawnTile!.Value.Tile);
            Assert.True(moved.IsPositionFree(TilePosition.Wall(WallSlotOffset + Int(m, "backfilledIndex"))),
                "the canonical C# map keeps the drawn tile's old wall slot empty; V4 backfills that hole with the displaced tile");
            Assert.Equal(WallSlotOffset + Int(m, "wallPointerAfterSwap"), moved.WallPointer);
            AssertFacts(moved, m, "factsAfter", FactSchema.FactsAfter);
            CheatTestBench.AssertConserved(moved);
        }

        [Fact]
        public void Movement_ReservationDelivery_BindsFactsAndRemoveAt()
        {
            JsonElement m = Case(Golden(), "tileMovement", "mv-reservation-delivery");
            var spec = new V4Spec();
            spec.Hands[0] = new List<int> { 0, 4, 8, 12, 20, 24, 28, 32, 1, 5, 9, 13 };
            spec.Drawn = (0, 16);
            spec.WallPrefix.AddRange(new[] { 33, 17, 25 });
            spec.Phase = RoundPhase.Discard;
            spec.CurrentPlayer = 0;
            TilePositionMap beforeDiscard = BuildV4(spec);
            TilePositionMap map = beforeDiscard.Discard(0, new TileId(16)).RequireState();
            Assert.Equal(WallSlotOffset + Int(m, "wallIndex"),
                map.PositionOf(new TileId(Int(m, "reservedTileId"))).Index);
            Assert.Equal(WallSlotOffset + Int(m, "wallPointerBefore"), map.WallPointer);
            Assert.Equal(WallSlotOffset + Int(m, "wallEndExclusiveBefore"), map.WallEndExclusive);

            CheatState state = NewState().WithSleeveBloom(NewState().SleeveBloom.WithReservation(
                new SleeveBloomReservation(0, "bloom-offturn-0-req-1",
                    new TileId(Int(m, "reservedTileId")), "bloom-window-0-1", 1_000)));
            CheatTransition delivered = CheatSubmission.DeliverSleeveBloomReservation(
                state, map, Ctx(0, RoundPhase.Draw, SleeveBloomDrawKind.Normal), 0);
            Assert.True(delivered.Success, delivered.Message);
            TilePositionMap moved = delivered.Map!;
            Assert.Equal(WallSlotOffset + Int(m, "wallPointerAfter"), moved.WallPointer);
            Assert.Equal(WallSlotOffset + Int(m, "wallEndExclusiveAfter"), moved.WallEndExclusive);
            AssertFacts(moved, m, "factsAfter", FactSchema.FactsAfter);
            Assert.Null(delivered.State!.SleeveBloom.ReservationFor(0));
            Assert.Single(delivered.Events);
            Assert.Equal(CheatEventKind.SleeveBloomReservationDelivered, delivered.Events[0].Kind);
            CheatTestBench.AssertConserved(moved);
        }

        [Fact]
        public void Movement_RinshanAndDrawnSwap_BindsDeadWallSlotAndReplacement()
        {
            JsonElement m = Case(Golden(), "tileMovement", "mv-rinshan-and-drawn-swap");
            var spec = new V4Spec();
            spec.Hands[0] = new List<int>
            {
                0, 4, 8, 12, 20, 24, 28, 32, 1, 5, 9, 13, 17
            };
            spec.WallPrefix.AddRange(new[] { 56, 60, 64, 68 });
            spec.DeadWallPrefix.AddRange(Enumerable.Range(108, 14));
            spec.Phase = RoundPhase.Draw;
            spec.CurrentPlayer = 0;
            spec.WallLimit = TilePositionMap.LiveWallSlotCount - WallSlotOffset;
            TilePositionMap map = BuildV4(spec);
            Assert.Equal(WallSlotOffset + Int(m, "wallEndExclusiveBefore"), map.WallEndExclusive);

            TilePositionMap afterDraw = map.DrawReplacement(0).RequireState();
            Assert.Equal(Int(m, "rinshanTileId"), afterDraw.DrawnTile!.Value.Tile.Value);
            Assert.Equal(Int(m, "rinshanPointerAfterDraw"), afterDraw.RinshanPointer);
            Assert.Equal(WallSlotOffset + Int(m, "wallEndExclusiveAfter"), afterDraw.WallEndExclusive);
            Assert.Equal(Int(m, "replacementTileId"),
                afterDraw.TileAt(TilePosition.DeadWallReplacement(0)).Value);

            TransitionResult swapped = afterDraw.SwapDrawnWithNextRinshan(
                0, new TileId(Int(m, "rinshanTileId")));
            Assert.True(swapped.IsSuccess, swapped.Error?.ToString());
            TilePositionMap moved = swapped.State!;
            Assert.Equal(Int(m, "deadWallSlot1AfterSwap"),
                moved.TileAt(TilePosition.DeadWall(1)).Value);
            Assert.Equal(TileZone.Hand, moved.PositionOf(new TileId(Int(m, "nextRinshanTileId"))).Zone);
            Assert.Equal(TileZone.DeadWallReplacement,
                moved.PositionOf(new TileId(Int(m, "replacementTileId"))).Zone);

            // V4 keeps a duplicate deadWall list entry for the moved rinshan tile, so its
            // factsJson indexOf reports the stale slot 0. Consume the exported artifact leaf
            // explicitly and map it to the authoritative slot field; C# has exactly one 108.
            JsonElement fact108 = MovementFact(m, "factsAfter", Int(m, "rinshanTileId"));
            Assert.Equal("deadWall", Str(fact108, "container"));
            Assert.Equal(JsonValueKind.Null, fact108.GetProperty("seat").ValueKind);
            Assert.Equal(0, Int(fact108, "index")); // V4 stale list index, not the live slot
            Assert.Equal(TilePosition.DeadWall(1),
                moved.PositionOf(new TileId(Int(m, "rinshanTileId"))));
            AssertFact(moved, MovementFact(m, "factsAfter", Int(m, "nextRinshanTileId")), "m3 next rinshan", FactSchema.FactsAfter);
            AssertFact(moved, MovementFact(m, "factsAfter", Int(m, "replacementTileId")), "m3 replacement", FactSchema.FactsAfter);
            CheatTestBench.AssertConserved(moved);
        }

        [Fact]
        public void Movement_DeadWallSwap_BindsCompositeFactsAndRefusals()
        {
            JsonElement m = Case(Golden(), "tileMovement", "mv-swap-hand-with-dead-wall");
            var spec = new V4Spec();
            spec.Hands[0] = new List<int>
            {
                0, 4, 8, 12, 20, 24, 28, 32, 1, 5, 9, 13, 17
            };
            spec.WallPrefix.AddRange(new[] { 56, 60, 64, 68 });
            spec.DeadWallPrefix.AddRange(Enumerable.Range(108, 14));
            spec.Phase = RoundPhase.Draw;
            spec.CurrentPlayer = 0;
            spec.Dora = new[] { 112 };
            spec.WallLimit = TilePositionMap.LiveWallSlotCount - WallSlotOffset;
            TilePositionMap map = BuildV4(spec).DrawReplacement(0).RequireState();
            Assert.Equal(Int(m, "rinshanTileId"), map.DrawnTile!.Value.Tile.Value);
            TileId handTile = new TileId(Int(m, "handTileId"));
            TilePosition handPosition = map.PositionOf(handTile);

            TransitionResult first = map.SwapHandWithDeadWall(0, handTile, 1);
            Assert.True(first.IsSuccess, first.Error?.ToString());
            Assert.Equal(Int(m, "gotTileId"), first.ResultTile!.Value.Value);
            Assert.Equal(Int(m, "deadWallSlot1AfterSwap"),
                first.State!.TileAt(TilePosition.DeadWall(1)).Value);
            Assert.Equal(TileZone.Hand, first.State.PositionOf(new TileId(Int(m, "gotTileId"))).Zone);

            Assert.Equal("IllegalArgumentException", Str(m, "doraRefusalException"));
            TransitionResult doraRefusal = first.State.SwapHandWithDeadWall(0, new TileId(4), Int(m, "doraSlotIndex"));
            Assert.False(doraRefusal.IsSuccess);
            Assert.Equal(TransitionErrorCode.InvalidPosition, doraRefusal.Error!.Code);
            Assert.Null(doraRefusal.State);
            CheatTestBench.AssertConserved(first.State);

            // The V4 primitive refuses WallSlot by type; the C# command entry rejects the
            // same target at candidate lookup instead of letting a dead-wall window swap a wall.
            CheatState beamState = NewState();
            CheatTransition activated = CheatSubmission.ActivateBeamSwap(
                beamState, first.State,
                Ctx(0, RoundPhase.CheatWindow, SleeveBloomDrawKind.Rinshan),
                CheatTestBench.Economy(), CheatTestBench.Sleeve(), "req-wall-refusal", 0);
            Assert.True(activated.Success, activated.Message);
            Assert.Equal("IllegalArgumentException", Str(m, "wallSlotRefusalException"));
            CheatTransition wallRefusal = CheatSubmission.ChooseBeamSwap(
                activated.State!, first.State,
                Ctx(0, RoundPhase.CheatWindow, SleeveBloomDrawKind.Rinshan),
                0, activated.State!.BeamSwap.Offer!.OfferId, BeamSwapAction.Swap,
                new TileId(4), BeamSwapSlot.Wall(54));
            Assert.False(wallRefusal.Success);
            Assert.Equal(CheatErrorCode.CandidateNotFound, wallRefusal.Error);
            Assert.Null(wallRefusal.Map);

            TransitionResult replacement = first.State.SwapHandWithReplacement(0, new TileId(4), 0);
            Assert.True(replacement.IsSuccess, replacement.Error?.ToString());
            Assert.Equal(Int(m, "replacementSlot0AfterSwap"),
                replacement.State!.TileAt(TilePosition.DeadWallReplacement(0)).Value);
            Assert.Equal(TileZone.Hand,
                replacement.State.PositionOf(new TileId(Int(m, "replacementTileIdBefore"))).Zone);
            AssertFacts(replacement.State, m, "factsAfter", FactSchema.FactsAfter);
            Assert.Equal(handPosition, first.State.PositionOf(new TileId(Int(m, "gotTileId"))));
            CheatTestBench.AssertConserved(replacement.State);
        }

        [Theory]
        [MemberData(nameof(BeamSwapBoundCases))]
        public void BeamSwap_BoundCase_ReplaysThroughDomainEntry(string caseId)
        {
            CaseSpec spec = Spec("beamSwapHandler", caseId);
            CheatState state = NewState();
            TilePositionMap map = BeamBaseMap();
            CheatTurnContext ctx = Ctx(0, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal);
            CheatEconomyConfig economy = CheatTestBench.Economy();
            string requestId = "req-1";

            if (caseId == "bm-activate-no-candidates")
            {
                map = BeamBaseMap(wallCount: 0);
            }
            else if (caseId == "bm-activate-dealer-extra")
            {
                map = BeamBaseMap(drawnTile: 16, drawnSource: DrawSource.DealExtra);
            }
            else if (caseId == "bm-activate-riichi")
            {
                ctx = Ctx(0, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal, riichi: true);
            }
            else if (caseId == "bm-activate-no-drawn")
            {
                map = BeamBaseMap(withoutDrawn: true);
            }
            else if (caseId == "bm-activate-beam-pending")
            {
                state = WithBeamOffer(
                    state, new TileId(16), map.TileAt(TilePosition.Wall(WallSlotOffset)), new TileId(4));
            }
            else if (caseId == "bm-activate-sleeve-pending")
            {
                state = WithImmediateOffer(state, 0, new TileId(16), new TileId(56));
            }
            else if (caseId == "bm-activate-already-used")
            {
                state = state.WithBeamSwap(state.BeamSwap.WithUsedOnTurn(TurnNumber));
            }
            else if (caseId == "bm-activate-disabled")
            {
                economy = EconomyWithCosts(40, 10, 10, 5);
                state = NewState(economy);
            }
            else if (caseId == "bm-activate-insufficient")
            {
                state = NewState(5);
            }
            else if (caseId == "bm-activate-deadwall-allowed")
            {
                map = BeamDeadWallActivationMap();
                ctx = Ctx(0, RoundPhase.CheatWindow, SleeveBloomDrawKind.Rinshan);
            }

            if (caseId.StartsWith("bm-activate-", StringComparison.Ordinal))
            {
                Snapshot before = Snapshot.Capture(state, map);
                CheatTransition actual = CheatSubmission.ActivateBeamSwap(
                    state, map, ctx, economy, CheatTestBench.Sleeve(), requestId, 0);

                if (caseId == "bm-activate-allowed")
                {
                    AssertFixture(spec);
                    AssertCharged(actual, 0, "beam-0-req-1", "BEAM_SWAP", 40);
                    Assert.Null(actual.Map);
                    BeamSwapOffer offer = actual.State!.BeamSwap.Offer!;
                    Assert.Equal("beam-0-req-1", offer.OfferId);
                    Assert.Equal(0, offer.Seat);
                    Assert.Equal(BeamSwapWindow.Wall, offer.Window);
                    Assert.Equal(8, offer.Candidates.Count);
                    Assert.Equal(new TileId(16), offer.DrawnTileId);
                    Assert.Equal(TurnNumber, offer.CreatedTurnNumber);
                    for (int i = 0; i < 8; i++)
                    {
                        Assert.Equal(WallSlotOffset + i, offer.Candidates[i].Slot.Index);
                        Assert.Equal(map.TileAt(TilePosition.Wall(WallSlotOffset + i)), offer.Candidates[i].Tile);
                    }

                    Assert.Equal(30, actual.State.Ledger.BalanceOf(0));
                    AssertUnchanged(before, state, map);
                }
                else if (caseId == "bm-activate-deadwall-allowed")
                {
                    AssertFixture(spec);
                    AssertCharged(actual, 0, "beam-0-req-1", "BEAM_SWAP", 40);
                    BeamSwapOffer offer = actual.State!.BeamSwap.Offer!;
                    Assert.Equal(BeamSwapWindow.DeadWall, offer.Window);
                    Assert.Equal(13, offer.Candidates.Count);
                    Assert.Contains(offer.Candidates, c => c.Slot == BeamSwapSlot.DeadWall(1) && c.Tile.Value == 109);
                    Assert.Contains(offer.Candidates, c => c.Slot == BeamSwapSlot.DeadWall(3) && c.Tile.Value == 111);
                    Assert.Contains(offer.Candidates, c => c.Slot == BeamSwapSlot.DeadWall(5) && c.Tile.Value == 113);
                    Assert.Contains(offer.Candidates, c => c.Slot == BeamSwapSlot.Replacement(0) && c.Tile.Value == 0);
                    Assert.DoesNotContain(offer.Candidates, c => c.Slot == BeamSwapSlot.DeadWall(4));
                    Assert.Equal(new TileId(16), offer.DrawnTileId);
                }
                else
                {
                    AssertRejected(spec, actual, before, state, map);
                }
            }
            else if (caseId == "bm-choose-swap" || caseId == "bm-choose-keep"
                || caseId == "bm-choose-invalid-slot" || caseId == "bm-choose-invalid-foreign-hand"
                || caseId == "bm-keep-original-args-forbidden-at-request"
                || caseId == "bm-choose-stale-turn" || caseId == "bm-choose-stale-drawn")
            {
                // V4 choice table is beamTable() with drawn=52, not the activation default 16.
                map = BeamBaseMap(drawnTile: 52);
                CheatTransition activated = CheatSubmission.ActivateBeamSwap(
                    state, map, ctx, economy, CheatTestBench.Sleeve(), requestId, 0);
                Assert.True(activated.Success, activated.Message);
                BeamSwapOffer offer = activated.State!.BeamSwap.Offer!;
                TileId hand = new TileId(4);
                BeamSwapSlot slot = BeamSwapSlot.Wall(WallSlotOffset);
                CheatTurnContext choiceCtx = ctx;
                BeamSwapAction action = BeamSwapAction.Swap;

                if (caseId == "bm-choose-keep")
                {
                    action = BeamSwapAction.KeepOriginal;
                    hand = default;
                    slot = default;
                }
                else if (caseId == "bm-choose-invalid-slot")
                {
                    slot = BeamSwapSlot.Wall(99);
                }
                else if (caseId == "bm-choose-invalid-foreign-hand")
                {
                    hand = new TileId(52);
                }
                else if (caseId == "bm-keep-original-args-forbidden-at-request")
                {
                    action = BeamSwapAction.KeepOriginal;
                }
                else if (caseId == "bm-choose-stale-turn")
                {
                    choiceCtx = Ctx(0, RoundPhase.CheatWindow, SleeveBloomDrawKind.Normal, turn: 13);
                }
                else if (caseId == "bm-choose-stale-drawn")
                {
                    map = BeamBaseMap(drawnTile: 20);
                }

                TileId? handArg = action == BeamSwapAction.KeepOriginal && caseId != "bm-keep-original-args-forbidden-at-request"
                    ? (TileId?)null
                    : hand;
                BeamSwapSlot? slotArg = action == BeamSwapAction.KeepOriginal && caseId != "bm-keep-original-args-forbidden-at-request"
                    ? (BeamSwapSlot?)null
                    : slot;
                if (caseId == "bm-choose-keep")
                {
                    handArg = null;
                    slotArg = null;
                }

                Snapshot before = Snapshot.Capture(activated.State, map);
                CheatTransition chosen = CheatSubmission.ChooseBeamSwap(
                    activated.State, map, choiceCtx, 0, offer.OfferId, action, handArg, slotArg);

                if (caseId == "bm-choose-swap")
                {
                    AssertFixture(spec);
                    Assert.True(chosen.Success, chosen.Message);
                    Assert.NotNull(chosen.Map);
                    Assert.Equal(TileZone.Wall, chosen.Map!.PositionOf(new TileId(4)).Zone);
                    Assert.Equal(WallSlotOffset, chosen.Map.PositionOf(new TileId(4)).Index);
                    Assert.Equal(TileZone.Hand, chosen.Map.PositionOf(new TileId(56)).Zone);
                    Assert.Equal(0, chosen.Map.PositionOf(new TileId(56)).Seat);
                    Assert.Equal(map.WallPointer, chosen.Map.WallPointer);
                    Assert.Equal(30, chosen.State!.Ledger.BalanceOf(0));
                    Assert.Null(chosen.State.BeamSwap.Offer);
                    CheatTestBench.AssertConserved(chosen.Map);
                }
                else if (caseId == "bm-choose-keep")
                {
                    AssertFixture(spec);
                    Assert.True(chosen.Success, chosen.Message);
                    Assert.Null(chosen.Map);
                    Assert.Null(chosen.State!.BeamSwap.Offer);
                    Assert.Equal(30, chosen.State.Ledger.BalanceOf(0));
                    Assert.Single(chosen.Events);
                    Assert.Equal(CheatEventKind.BeamSwapResolved, chosen.Events[0].Kind);
                    Assert.Equal(BeamSwapAction.KeepOriginal, chosen.Events[0].Action);
                }
                else if (caseId == "bm-choose-stale-turn" || caseId == "bm-choose-stale-drawn")
                {
                    CheatState expected = activated.State!.WithBeamSwap(activated.State.BeamSwap.WithoutOffer());
                    AssertStaleWithState(spec, chosen, before, activated.State, map, expected,
                        new[] { CheatEvent.BeamSwapOfferCleared(0, offer.OfferId, "Stale") });
                    Assert.Null(chosen.State!.BeamSwap.Offer);
                }
                else
                {
                    if (caseId == "bm-keep-original-args-forbidden-at-request")
                    {
                        Assert.Equal("IllegalArgumentException",
                            Str(Case(Golden(), "beamSwapHandler", caseId), "requestException"));
                    }

                    AssertRejected(spec, chosen, before, activated.State!, map);
                }
            }
            else if (caseId == "bm-choose-deadwall-allowed"
                || caseId == "bm-choose-deadwall-dora-stale"
                || caseId == "bm-choose-replacement-allowed")
            {
                map = BeamDeadWallChoiceMap();
                ctx = Ctx(0, RoundPhase.CheatWindow, SleeveBloomDrawKind.Rinshan);
                CheatTransition activated;
                if (caseId == "bm-choose-deadwall-dora-stale")
                {
                    var doraOffer = new BeamSwapOffer(
                        "beam-0-req-1", 0, BeamSwapWindow.DeadWall,
                        new[] { new BeamSwapCandidate(new TileId(112), BeamSwapSlot.DeadWall(4)) },
                        new[] { new TileId(4) }, new TileId(108), TurnNumber);
                    state = state.WithBeamSwap(BeamSwapState.Restore(doraOffer, TurnNumber, 1));
                    activated = CheatTransition.Replayed(state, Array.Empty<CheatEvent>());
                }
                else
                {
                    activated = CheatSubmission.ActivateBeamSwap(
                        state, map, ctx, economy, CheatTestBench.Sleeve(), requestId, 0);
                    Assert.True(activated.Success, activated.Message);
                }

                BeamSwapSlot target = caseId == "bm-choose-deadwall-dora-stale"
                    ? BeamSwapSlot.DeadWall(4)
                    : caseId == "bm-choose-replacement-allowed"
                        ? BeamSwapSlot.Replacement(0)
                        : BeamSwapSlot.DeadWall(1);
                Snapshot before = Snapshot.Capture(activated.State!, map);
                CheatTransition chosen = CheatSubmission.ChooseBeamSwap(
                    activated.State!, map, ctx, 0, "beam-0-req-1", BeamSwapAction.Swap, new TileId(4), target);

                if (caseId == "bm-choose-deadwall-dora-stale")
                {
                    CheatState expected = activated.State!.WithBeamSwap(activated.State.BeamSwap.WithoutOffer());
                    AssertStaleWithState(spec, chosen, before, activated.State, map, expected,
                        new[] { CheatEvent.BeamSwapOfferCleared(0, "beam-0-req-1", "Stale") });
                    Assert.Null(chosen.State!.BeamSwap.Offer);
                }
                else if (caseId == "bm-choose-deadwall-allowed")
                {
                    AssertFixture(spec);
                    Assert.True(chosen.Success, chosen.Message);
                    Assert.Equal(TileZone.DeadWall, chosen.Map!.PositionOf(new TileId(4)).Zone);
                    Assert.Equal(1, chosen.Map.PositionOf(new TileId(4)).Index);
                    Assert.Equal(TileZone.Hand, chosen.Map.PositionOf(new TileId(109)).Zone);
                    Assert.Equal(30, chosen.State!.Ledger.BalanceOf(0));
                    CheatTestBench.AssertConserved(chosen.Map);
                }
                else
                {
                    AssertFixture(spec);
                    Assert.True(chosen.Success, chosen.Message);
                    Assert.Equal(TileZone.DeadWallReplacement, chosen.Map!.PositionOf(new TileId(4)).Zone);
                    Assert.Equal(0, chosen.Map.PositionOf(new TileId(4)).Index);
                    Assert.Equal(TileZone.Hand, chosen.Map.PositionOf(new TileId(135)).Zone);
                    Assert.Equal(30, chosen.State!.Ledger.BalanceOf(0));
                    CheatTestBench.AssertConserved(chosen.Map);
                }
            }
            else
            {
                throw new InvalidOperationException("no beam swap binding for " + caseId);
            }
        }

        [Fact]
        public void BeamDeadWallFilterStates_AreNotCanonicalTilePositionMaps()
        {
            var emptySpec = new MapSpec();
            emptySpec.Hands[0] = new List<int> { 0, 4, 8, 12, 16, 20, 24, 28, 32, 1, 5, 9, 13 };
            emptySpec.Drawn = (0, 108);
            emptySpec.DrawnSource = DrawSource.Rinshan;
            emptySpec.AutoWallCount = 60;
            emptySpec.RinshanPointer = 4;
            emptySpec.AutoFillDeadWall = false;
            for (int i = 0; i < TilePositionMap.DeadWallSlotCount; i++)
            {
                emptySpec.DeadWall.Add(null);
            }

            Assert.Throws<ConservationException>(() => BuildMap(emptySpec));

            var onlyDoraSpec = new MapSpec();
            onlyDoraSpec.Hands[0] = new List<int> { 0, 4, 8, 12, 16, 20, 24, 28, 32, 1, 5, 9, 13 };
            onlyDoraSpec.Drawn = (0, 108);
            onlyDoraSpec.DrawnSource = DrawSource.Rinshan;
            onlyDoraSpec.AutoWallCount = 60;
            onlyDoraSpec.Dora = new[] { 112 };
            onlyDoraSpec.RinshanPointer = 1;
            onlyDoraSpec.AutoFillDeadWall = false;
            onlyDoraSpec.DeadWall.Add(null);
            onlyDoraSpec.DeadWall.Add(null);
            onlyDoraSpec.DeadWall.Add(null);
            onlyDoraSpec.DeadWall.Add(null);
            onlyDoraSpec.DeadWall.Add(112);
            for (int i = 5; i < TilePositionMap.DeadWallSlotCount; i++)
            {
                onlyDoraSpec.DeadWall.Add(null);
            }

            Assert.Throws<ConservationException>(() => BuildMap(onlyDoraSpec));
        }

        [Fact]
        public void BeamSwap_DoraIndicator_IsNeverACandidate()
        {
            TilePositionMap map = BeamDeadWallChoiceMap();
            CheatTransition actual = CheatSubmission.ActivateBeamSwap(
                NewState(), map,
                Ctx(0, RoundPhase.CheatWindow, SleeveBloomDrawKind.Rinshan),
                CheatTestBench.Economy(), CheatTestBench.Sleeve(), "req-1", 0);
            Assert.True(actual.Success, actual.Message);
            BeamSwapOffer offer = actual.State!.BeamSwap.Offer!;
            Assert.DoesNotContain(offer.Candidates, c => c.Slot == BeamSwapSlot.DeadWall(4));
            Assert.Contains(offer.Candidates, c => c.Slot == BeamSwapSlot.DeadWall(5));
        }

        private static CheatState WithMarks(CheatState state, IEnumerable<TileMarkGroup> groups)
        {
            TileMarkState marks = state.TileMarks;
            foreach (TileMarkGroup group in groups)
            {
                TileMarkTransition transition = marks.AddGroup(group);
                Assert.True(transition.Success, transition.Error + " for " + group.GroupId);
                marks = transition.State!;
            }

            return state.WithTileMarks(marks);
        }
    }
}
