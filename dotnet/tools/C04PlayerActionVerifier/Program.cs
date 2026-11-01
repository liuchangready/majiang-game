using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Google.Protobuf;
using Mahjong.Contracts.Wire;
using Mahjong.LocalHost;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "selftest") return SelfTest();
            if (args.Length != 2 || args[0] != "verify") throw new ArgumentException("Usage: verify <action-evidence.json> or selftest");
            Verify(args[1]);
            Console.WriteLine("C04_PROTOBUF_AND_AUTHORITY_IMAGE_PASS");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.GetType().FullName + ": " + error.Message);
            return 1;
        }
    }

    private static int SelfTest()
    {
        var snapshot = new PlayerSnapshot
        {
            SessionId = "fixture-session", RoundId = "c04-fixture-round", StateVersion = 13, EventSeq = 41,
            Cursor = new StreamCursor { StreamVersion = 2, ProjectionSeq = 9 },
            PublicView = new PublicMatchView { Board = new PublicBoardView { RiichiSticks = 1 } },
            PrivateView = new PlayerPrivateView { Seat = 0, ClosedTiles = { 7, 8 } }
        };
        byte[] legal = snapshot.ToByteArray();
        PlayerSnapshot parsed = PlayerSnapshot.Parser.ParseFrom(legal);
        if (!parsed.Equals(snapshot)) throw new InvalidDataException("Generated protobuf fixture failed round-trip.");
        foreach (byte[] invalid in new[] { new byte[] { 1 }, System.Text.Encoding.ASCII.GetBytes("not protobuf") })
        {
            bool rejected = false;
            try { PlayerSnapshot.Parser.ParseFrom(invalid); } catch (Google.Protobuf.InvalidProtocolBufferException) { rejected = true; }
            if (!rejected) throw new InvalidDataException("Generated PlayerSnapshot.Parser accepted malformed bytes.");
        }
        Console.WriteLine("C04_PROTOBUF_FIXTURE_PASS valid=1 invalid=2");
        return 0;
    }

    private static void Verify(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement evidence = document.RootElement;
        PlayerSnapshot baseline = ParseRequired(evidence, "BaselinePlayerSnapshotBase64");
        PlayerSnapshot after = ParseRequired(evidence, "AfterPlayerSnapshotBase64");
        string baselineSave = RequiredString(evidence, "BaselineAuthoritySaveBase64");
        string baselineCheat = RequiredString(evidence, "BaselineAuthorityCheatStateBase64");
        string afterSave = RequiredString(evidence, "AfterAuthoritySaveBase64");
        string afterCheat = RequiredString(evidence, "AfterAuthorityCheatStateBase64");
        LocalTableRecoveryFacts baselineFacts = LocalTableRecoveryFactsReader.ReadPersistedFacts(Convert.FromBase64String(baselineSave), Convert.FromBase64String(baselineCheat));
        LocalTableRecoveryFacts afterFacts = LocalTableRecoveryFactsReader.ReadPersistedFacts(Convert.FromBase64String(afterSave), Convert.FromBase64String(afterCheat));

        Equal(RequiredString(evidence, "BaselineSessionId"), baseline.SessionId, "baseline session");
        Equal(RequiredString(evidence, "BaselineRoundId"), baseline.RoundId, "baseline round");
        Equal(RequiredString(evidence, "BaselineStateVersion"), baseline.StateVersion.ToString(CultureInfo.InvariantCulture), "baseline version");
        Equal(RequiredString(evidence, "BaselineEventSeq"), baseline.EventSeq.ToString(CultureInfo.InvariantCulture), "baseline event sequence");
        Equal(RequiredString(evidence, "BaselineCursorStream"), baseline.Cursor.StreamVersion.ToString(CultureInfo.InvariantCulture), "baseline stream cursor");
        Equal(RequiredString(evidence, "BaselineCursor"), baseline.Cursor.ProjectionSeq.ToString(CultureInfo.InvariantCulture), "baseline projection cursor");
        Equal(RequiredString(evidence, "AfterSessionId"), after.SessionId, "after session");
        Equal(RequiredString(evidence, "AfterRoundId"), after.RoundId, "after round");
        Equal(RequiredString(evidence, "AfterStateVersion"), after.StateVersion.ToString(CultureInfo.InvariantCulture), "after version");
        Equal(RequiredString(evidence, "AfterEventSeq"), after.EventSeq.ToString(CultureInfo.InvariantCulture), "after event sequence");
        Equal(RequiredString(evidence, "AfterCursorStream"), after.Cursor.StreamVersion.ToString(CultureInfo.InvariantCulture), "after stream cursor");
        Equal(RequiredString(evidence, "AfterCursor"), after.Cursor.ProjectionSeq.ToString(CultureInfo.InvariantCulture), "after projection cursor");

        Equal(RequiredString(evidence, "BaselineRoundId"), baselineFacts.RoundId, "baseline authority round");
        Equal(RequiredString(evidence, "BaselineStateVersion"), baselineFacts.StateVersion, "baseline authority version");
        Equal(RequiredString(evidence, "BaselineEventSeq"), baselineFacts.EventSeq, "baseline authority event sequence");
        Equal(RequiredString(evidence, "AfterRoundId"), afterFacts.RoundId, "after authority round");
        Equal(RequiredString(evidence, "AfterStateVersion"), afterFacts.StateVersion, "after authority version");
        Equal(RequiredString(evidence, "AfterEventSeq"), afterFacts.EventSeq, "after authority event sequence");
        VerifyPhysical(evidence, "Baseline", baselineFacts.Physical);
        VerifyPhysical(evidence, "After", afterFacts.Physical);
        VerifyPlayerView(evidence, baseline, "Baseline", true);
        VerifyPlayerView(evidence, after, "After", false);
        VerifyActionEffects(evidence, baseline, after, baselineFacts.Physical, afterFacts.Physical);
    }

    private static void VerifyPlayerView(JsonElement evidence, PlayerSnapshot snapshot, string prefix, bool baseline)
    {
        PlayerPrivateView own = snapshot.PrivateView ?? throw new InvalidDataException(prefix + " private view is absent.");
        if (own.Seat != 0) throw new InvalidDataException(prefix + " protobuf private seat is not 0.");
        string closed = string.Join(",", own.ClosedTiles.Select(value => value.ToString(CultureInfo.InvariantCulture)));
        string meld = own.OwnerMelds.Count == 0 ? string.Empty : string.Join(",", own.OwnerMelds[0].TileIds.Select(value => value.ToString(CultureInfo.InvariantCulture)));
        string drawn = own.HasDrawnTileId ? own.DrawnTileId.ToString(CultureInfo.InvariantCulture) : string.Empty;
        if (!baseline)
        {
            Equal(RequiredString(evidence, "HandTileIds"), closed, "protobuf closed hand");
            Equal(RequiredString(evidence, "MeldTileIds"), meld, "protobuf owner meld");
            Equal(RequiredString(evidence, "DrawnTileId"), drawn, "protobuf drawn tile");
            PublicBoardView board = snapshot.PublicView?.Board ?? throw new InvalidDataException("After protobuf public board is absent.");
            long score = board.Scores.Single(value => value.Seat == 0).Score;
            Equal(RequiredString(evidence, "SeatScoreAfter"), score.ToString(CultureInfo.InvariantCulture), "protobuf seat score");
            Equal(RequiredString(evidence, "RiichiSticksAfter"), board.RiichiSticks.ToString(CultureInfo.InvariantCulture), "protobuf riichi sticks");
        }
        else
        {
            string expectedCase = RequiredString(evidence, "CaseId");
            int[] expected = expectedCase switch
            {
                "C04-G2-01" => new[] { 0, 1, 2 },
                "C04-G2-02" => new[] { 0, 4, 8 },
                "C04-G2-03" => Array.Empty<int>(),
                "C04-G2-04" => new[] { 0, 1, 2, 3 },
                _ => throw new InvalidDataException("Unknown fixed C04 case.")
            };
            foreach (int tile in expected)
                if (!own.ClosedTiles.Contains((uint)tile)) throw new InvalidDataException("Baseline protobuf hand lacks required fixed tile " + tile + ".");
        }
    }

    private static void VerifyActionEffects(JsonElement evidence, PlayerSnapshot baseline, PlayerSnapshot after,
        LocalTableRecoveryPhysicalFact beforePhysical, LocalTableRecoveryPhysicalFact afterPhysical)
    {
        string caseId = RequiredString(evidence, "CaseId");
        PlayerPrivateView beforeOwn = baseline.PrivateView!;
        PlayerPrivateView afterOwn = after.PrivateView!;
        string[] expected = caseId switch
        {
            "C04-G2-01" => new[] { "0", "1", "2" },
            "C04-G2-02" => new[] { "0", "4", "8" },
            "C04-G2-03" => Array.Empty<string>(),
            "C04-G2-04" => new[] { "0", "1", "2", "3" },
            _ => throw new InvalidDataException("Unknown fixed C04 case.")
        };
        if (caseId == "C04-G2-03")
        {
            PublicBoardView beforeBoard = baseline.PublicView?.Board ?? throw new InvalidDataException("Baseline public board is absent.");
            PublicBoardView afterBoard = after.PublicView?.Board ?? throw new InvalidDataException("After public board is absent.");
            long beforeScore = beforeBoard.Scores.Single(value => value.Seat == 0).Score;
            long afterScore = afterBoard.Scores.Single(value => value.Seat == 0).Score;
            if (beforeScore != 25000 || afterScore != 24000 || afterBoard.RiichiSticks != beforeBoard.RiichiSticks + 1 ||
                !afterBoard.Players.Single(value => value.Seat == 0).Riichi)
                throw new InvalidDataException("Parsed PlayerSnapshot does not prove the exact 1,000-point Riichi payment and one added stick.");
            return;
        }
        uint[] ids = expected.Select(value => uint.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        if (!ids.All(beforeOwn.ClosedTiles.Contains)) throw new InvalidDataException("Parsed baseline hand is missing a fixed selected entity.");
        MeldView meld = afterOwn.OwnerMelds.SingleOrDefault(value => value.TileIds.OrderBy(tile => tile).SequenceEqual(ids.OrderBy(tile => tile)))
            ?? throw new InvalidDataException("Parsed after owner snapshot lacks the exact fixed meld entities.");
        if (ids.Any(afterOwn.ClosedTiles.Contains)) throw new InvalidDataException("Parsed after hand still contains a meld entity.");
        string expectedKind = caseId switch { "C04-G2-01" => "Pon", "C04-G2-02" => "Chi", _ => "ClosedKan" };
        Equal(expectedKind, meld.Type.ToString(), "protobuf owner meld kind");
        if (caseId == "C04-G2-04")
        {
            if (!afterOwn.HasDrawnTileId || afterOwn.DrawnTileId != 73 || afterPhysical.DeadWallCount != beforePhysical.DeadWallCount - 1)
                throw new InvalidDataException("Parsed PlayerSnapshot and authority image do not prove dead-wall entity 73 as the closed-kan replacement draw.");
        }
    }

    private static void VerifyPhysical(JsonElement evidence, string prefix, LocalTableRecoveryPhysicalFact facts)
    {
        if (facts.TileCount != 136 || facts.UniqueCount != 136) throw new InvalidDataException(prefix + " authority image is not 136 unique entities.");
        Equal(RequiredString(evidence, prefix + "PhysicalTilesSha256"), facts.TilesSha256, prefix + " re-derived physical image hash");
        Equal(RequiredInt(evidence, prefix + "PhysicalTileCount").ToString(CultureInfo.InvariantCulture), facts.TileCount.ToString(CultureInfo.InvariantCulture), prefix + " physical count");
        Equal(RequiredInt(evidence, prefix + "PhysicalUniqueCount").ToString(CultureInfo.InvariantCulture), facts.UniqueCount.ToString(CultureInfo.InvariantCulture), prefix + " unique count");
        Equal(RequiredInt(evidence, prefix + "DeadWallCount").ToString(CultureInfo.InvariantCulture), facts.DeadWallCount.ToString(CultureInfo.InvariantCulture), prefix + " dead wall count");
    }

    private static PlayerSnapshot ParseRequired(JsonElement evidence, string name)
    {
        string value = RequiredString(evidence, name);
        byte[] bytes = Convert.FromBase64String(value);
        if (bytes.Length == 0) throw new InvalidDataException(name + " is empty.");
        return PlayerSnapshot.Parser.ParseFrom(bytes);
    }

    private static string RequiredString(JsonElement element, string name)
    {
        if (!TryProperty(element, name, out JsonElement value) || (value.ValueKind != JsonValueKind.String && value.ValueKind != JsonValueKind.Number))
            throw new InvalidDataException("Missing/empty evidence field " + name + ".");
        string result = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
        if (string.IsNullOrWhiteSpace(result)) throw new InvalidDataException("Missing/empty evidence field " + name + ".");
        return result;
    }

    private static int RequiredInt(JsonElement element, string name)
    {
        if (!TryProperty(element, name, out JsonElement value) || !value.TryGetInt32(out int result)) throw new InvalidDataException("Missing/noninteger evidence field " + name + ".");
        return result;
    }

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (JsonProperty property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        value = default;
        return false;
    }

    private static void Equal(string expected, string actual, string label)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal)) throw new InvalidDataException(label + " does not match the generated protobuf or authority image.");
    }
}
