using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Mahjong.Contracts.Wire;
using Mahjong.LocalHost;
using BusinessPayload = Mahjong.Contracts.BusinessPayload;
using CanonicalPayloadDigest = Mahjong.Contracts.CanonicalPayloadDigest;
using CanonicalPayloadWriter = Mahjong.Contracts.CanonicalPayloadWriter;
using CommandKind = Mahjong.Contracts.CommandKind;
using CanonicalPayloadParser = Mahjong.Contracts.CanonicalPayloadParser;
using CommandPayloadFields = Mahjong.Contracts.CommandPayloadFields;
using ProtocolActionType = Mahjong.Contracts.ProtocolActionType;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "selftest") return SelfTest();
            if (args.Length == 3 && args[0] == "make-mutations") return MakeMutationFixtures(args[1], args[2]);
            if (args.Length == 3 && args[0] == "verify-payload")
            {
                ValidateCanonicalPayload(Convert.FromBase64String(args[1]), args[2]);
                Console.WriteLine("C04_CANONICAL_PAYLOAD_DIGEST_PASS");
                return 0;
            }
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
        BusinessPayload payload = new CanonicalPayloadWriter((ushort)CommandKind.SubmitAction)
            .WriteString(1, "c04-fixture-round")
            .WriteString(2, "c04-fixture-decision")
            .WriteUInt64(3, 1)
            .WriteUInt32(4, 6)
            .WriteString(5, "pon-0-1")
            .WriteRepeatedUInt32(6, new uint[] { 0, 1, 2 })
            .Build();
        ValidateCanonicalPayload(payload.CopyBytes(), payload.Digest.ToHex());
        string wrongDigest = Convert.ToHexString(SHA256.HashData(payload.CopyBytes())).ToLowerInvariant();
        bool wrongDigestRejected = false;
        try { ValidateCanonicalPayload(payload.CopyBytes(), wrongDigest); } catch (InvalidDataException) { wrongDigestRejected = true; }
        if (!wrongDigestRejected) throw new InvalidDataException("Canonical payload digest verifier accepted the unprefixed raw SHA-256.");
        Console.WriteLine("C04_PROTOBUF_FIXTURE_PASS valid=1 invalid=2 canonicalDigest=PASS wrongDigestRejected=1");
        return 0;
    }

    private static int MakeMutationFixtures(string sourcePath, string outputDirectory)
    {
        string original = File.ReadAllText(sourcePath);
        Directory.CreateDirectory(outputDirectory);
        string[] names = { "missing-evidence-source", "after-hand-omission", "after-hand-duplicate", "baseline-hand-omission", "baseline-hand-duplicate", "public-hand-count", "raw-binding-role", "raw-batch-role", "raw-batch-kind", "raw-game-role", "raw-acceptance-mismatch", "game-source-missing-acceptance", "coherent-after-stream-cursor", "counter-distinct-from-cursor-model" };
        foreach (string name in names)
        {
            JsonObject evidence = JsonNode.Parse(original)!.AsObject();
            PlayerSnapshot baseline = ParseSnapshot(evidence, "BaselinePlayerSnapshotBase64");
            PlayerSnapshot after = ParseSnapshot(evidence, "AfterPlayerSnapshotBase64");
            if (name == "after-hand-omission" && !after.PrivateView.ClosedTiles.Remove(20U)) throw new InvalidDataException("Mutation fixture lacks after hand entity 20.");
            if (name == "after-hand-duplicate") after.PrivateView.ClosedTiles.Add(20U);
            if (name == "baseline-hand-omission" && !baseline.PrivateView.ClosedTiles.Remove(20U)) throw new InvalidDataException("Mutation fixture lacks baseline hand entity 20.");
            if (name == "baseline-hand-duplicate") baseline.PrivateView.ClosedTiles.Add(20U);
            if (name == "public-hand-count") after.PublicView.Board.Players.Single(player => player.Seat == 0).HandCount = 99;
            if (name == "coherent-after-stream-cursor") { after.Cursor.StreamVersion = 999; evidence["AfterCursorStream"] = 999; }
            evidence["BaselinePlayerSnapshotBase64"] = Convert.ToBase64String(baseline.ToByteArray());
            evidence["AfterPlayerSnapshotBase64"] = Convert.ToBase64String(after.ToByteArray());
            evidence["HandTileIds"] = string.Join(",", after.PrivateView.ClosedTiles);
            JsonObject rawBatch = ParseEmbeddedObject(evidence, "RawEventBatchJsonBase64");
            if (name == "missing-evidence-source") evidence.Remove("EvidenceSource");
            if (name == "raw-binding-role")
                foreach (string key in new[] { "BaselineBindingJsonBase64", "AfterBindingJsonBase64" })
                {
                    JsonObject binding = ParseEmbeddedObject(evidence, key);
                    binding["Role"] = "Spectator";
                    evidence[key] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(binding.ToJsonString()));
                }
            if (name == "raw-batch-role") rawBatch["Role"] = 999;
            if (name == "raw-batch-kind") rawBatch["Kind"] = 999;
            if (name == "raw-game-role") { evidence["EvidenceSource"] = "GAME"; rawBatch["Role"] = "Spectator"; rawBatch["Kind"] = "Incremental"; }
            if (name == "raw-acceptance-mismatch") { evidence["EvidenceSource"] = "GAME"; rawBatch["AcceptanceSequence"] = 999; }
            if (name == "game-source-missing-acceptance") { evidence["EvidenceSource"] = "GAME"; rawBatch.Remove("AcceptanceSequence"); }
            if (name == "counter-distinct-from-cursor-model")
            {
                rawBatch["AcceptanceSequence"] = 42; evidence["AcceptanceSequence"] = 42; evidence["AfterAcceptanceSequence"] = 42;
            }
            evidence["RawEventBatchJsonBase64"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(rawBatch.ToJsonString()));
            string path = Path.Combine(outputDirectory, name + ".json");
            File.WriteAllText(path, evidence.ToJsonString() + Environment.NewLine);
        }
        Console.WriteLine("C04_MUTATION_FIXTURES_CREATED invalid=12 positiveCounterDistinct=1 source=TOOL_FIXTURE (GAME tags are schema probes only)");
        return 0;
    }

    private static PlayerSnapshot ParseSnapshot(JsonObject evidence, string property) =>
        PlayerSnapshot.Parser.ParseFrom(Convert.FromBase64String(evidence[property]?.GetValue<string>() ?? throw new InvalidDataException("Missing mutation snapshot " + property + ".")));

    private static JsonObject ParseEmbeddedObject(JsonObject evidence, string property) =>
        JsonNode.Parse(Convert.FromBase64String(evidence[property]?.GetValue<string>() ?? throw new InvalidDataException("Missing mutation raw JSON " + property + ".")))!.AsObject();

    private static void Verify(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement evidence = document.RootElement;
        string evidenceSource = RequiredString(evidence, "EvidenceSource");
        if (evidenceSource != "GAME" && evidenceSource != "TOOL_FIXTURE_LOCALHOST_CHANNEL")
            throw new InvalidDataException("Action evidence has an unknown source classification.");
        byte[] canonicalPayload = Convert.FromBase64String(RequiredString(evidence, "PayloadBase64"));
        string payloadDigest = RequiredString(evidence, "PayloadSha256");
        ValidateCanonicalPayload(canonicalPayload, payloadDigest);
        VerifyTypedActionPayload(evidence, canonicalPayload);
        Equal(payloadDigest, RequiredString(evidence, "ReceiptDigest"), "typed Applied receipt canonical payload digest");
        Equal(RequiredString(evidence, "RequestId"), RequiredString(evidence, "ReceiptRequestId"), "typed Applied receipt request id");
        Equal(RequiredString(evidence, "ScopeId"), RequiredString(evidence, "ReceiptScopeId"), "typed Applied receipt scope id");
        Equal("Applied", RequiredString(evidence, "ReceiptDisposition"), "typed receipt disposition");
        Equal("Applied", RequiredString(evidence, "ReceiptStatus"), "typed receipt status");
        PlayerSnapshot baseline = ParseRequired(evidence, "BaselinePlayerSnapshotBase64");
        PlayerSnapshot after = ParseRequired(evidence, "AfterPlayerSnapshotBase64");
        string baselineSave = RequiredString(evidence, "BaselineAuthoritySaveBase64");
        string baselineCheat = RequiredString(evidence, "BaselineAuthorityCheatStateBase64");
        string afterSave = RequiredString(evidence, "AfterAuthoritySaveBase64");
        string afterCheat = RequiredString(evidence, "AfterAuthorityCheatStateBase64");
        byte[] baselineSaveBytes = Convert.FromBase64String(baselineSave);
        byte[] baselineCheatBytes = Convert.FromBase64String(baselineCheat);
        byte[] afterSaveBytes = Convert.FromBase64String(afterSave);
        byte[] afterCheatBytes = Convert.FromBase64String(afterCheat);
        LocalTableRecoveryFacts baselineFacts = LocalTableRecoveryFactsReader.ReadPersistedFacts(baselineSaveBytes, baselineCheatBytes);
        LocalTableRecoveryFacts afterFacts = LocalTableRecoveryFactsReader.ReadPersistedFacts(afterSaveBytes, afterCheatBytes);
        Dictionary<int, string> baselinePositions = ReadPhysicalPositions(LocalPlayerActionScenario.ReadPhysicalAudit(baselineSaveBytes, baselineCheatBytes));
        string exactActionAudit = LocalPlayerActionScenario.ReadCommittedActionPhysicalAudit(
            afterSaveBytes,
            RequiredString(evidence, "RequestId"),
            RequiredString(evidence, "ScopeId"),
            baseline.RoundId,
            baseline.StateVersion,
            RequiredUInt64(evidence, "EventCommandVersion"),
            RequiredUInt64(evidence, "EventSeq"),
            RequiredString(evidence, "EventType"),
            RequiredInt(evidence, "EventSeat"));
        Dictionary<int, string> afterPositions = ReadPhysicalPositions(exactActionAudit);
        int afterActionDeadWallCount = afterPositions.Values.Count(value => value.StartsWith("DEADWALL/", StringComparison.Ordinal));

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
        Equal(RequiredString(evidence, "BaselineEventSeq"), baselineFacts.EventSeq, "baseline authority event sequence");
        Equal(RequiredString(evidence, "AfterRoundId"), afterFacts.RoundId, "after authority round");
        if (ulong.Parse(afterFacts.EventSeq, CultureInfo.InvariantCulture) < ulong.Parse(RequiredString(evidence, "ReceiptEventSeq"), CultureInfo.InvariantCulture))
            throw new InvalidDataException("After authority event high-water predates the matched accepted receipt.");
        Equal(RequiredString(evidence, "BaselineRoomId"), baselineFacts.RoomId, "baseline authority room");
        Equal(RequiredString(evidence, "AfterRoomId"), afterFacts.RoomId, "after authority room");
        Equal(RequiredString(evidence, "BaselineRoundId"), baselineFacts.RoundId, "baseline authority round identity");
        Equal(RequiredString(evidence, "AfterRoundId"), afterFacts.RoundId, "after authority round identity");
        int seat = RequiredInt(evidence, "BaselineSeat");
        if (seat < 0 || seat > 3 || RequiredInt(evidence, "AfterSeat") != seat)
            throw new InvalidDataException("Authority evidence is not bound to one player seat.");
        Equal(RequiredString(evidence, "BaselineControlEpoch"), baselineFacts.ControlEpochs[seat].ToString(CultureInfo.InvariantCulture), "baseline authority control epoch");
        Equal(RequiredString(evidence, "AfterControlEpoch"), afterFacts.ControlEpochs[seat].ToString(CultureInfo.InvariantCulture), "after authority control epoch");
        Equal(RequiredString(evidence, "BaselineAccountId"), RequiredString(evidence, "AccountId"), "captured baseline account");
        Equal(RequiredString(evidence, "AfterAccountId"), RequiredString(evidence, "AccountId"), "captured accepted account");
        Equal(RequiredString(evidence, "BaselineScopeId"), RequiredString(evidence, "ScopeId"), "captured baseline scope");
        Equal(RequiredString(evidence, "AfterScopeId"), RequiredString(evidence, "ScopeId"), "captured accepted scope");
        Equal(RequiredString(evidence, "BaselineBindingEpoch"), RequiredString(evidence, "AfterBindingEpoch"), "stable binding epoch across action");
        Equal(RequiredString(evidence, "BaselineConnectionEpoch"), RequiredString(evidence, "AfterConnectionEpoch"), "stable connection epoch across action");
        VerifyAuthorityReceipt(evidence, baselineFacts, afterFacts, canonicalPayload);
        VerifyRawBindingAndAcceptedEvent(evidence, baseline, after);
        VerifyPhysical(evidence, "Baseline", baselineFacts.Physical);
        VerifyPhysical(evidence, "After", afterFacts.Physical);
        VerifyPlayerView(evidence, baseline, "Baseline", true, baselinePositions);
        VerifyPlayerView(evidence, after, "After", false, afterPositions);
        VerifyActionEffects(evidence, baseline, after, baselineFacts.Physical.DeadWallCount,
            afterActionDeadWallCount, baselinePositions, afterPositions);
    }

    private static void ValidateCanonicalPayload(byte[] canonicalPayload, string expectedDigest)
    {
        if (canonicalPayload.Length == 0) throw new InvalidDataException("Canonical business payload bytes are empty.");
        string actual = CanonicalPayloadDigest.Compute(canonicalPayload).ToHex();
        if (expectedDigest.Length != 64 || !string.Equals(actual, expectedDigest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Canonical payload bytes do not match the existing contract digest.");
    }

    private static void VerifyTypedActionPayload(JsonElement evidence, byte[] bytes)
    {
        var payload = CanonicalPayloadParser.Parse(bytes);
        if (payload.CommandKind != (ushort)CommandKind.SubmitAction)
            throw new InvalidDataException("Raw canonical payload is not a SubmitAction command.");
        Equal(RequiredString(evidence, "BaselineRoundId"),
            ReadCanonicalString(payload, CommandPayloadFields.RoundIdField, "roundId"), "canonical request round");
        Equal(RequiredString(evidence, "BaselineDecisionId"),
            ReadCanonicalString(payload, CommandPayloadFields.DecisionIdField, "decisionId"), "canonical request decision");
        Equal(RequiredString(evidence, "BaselineControlEpoch"),
            payload.RequireUInt64(CommandPayloadFields.ControlEpochField, "controlEpoch").ToString(CultureInfo.InvariantCulture), "canonical request control epoch");
        uint expectedAction = RequiredString(evidence, "CaseId") switch
        {
            "C04-G2-01" => (uint)ProtocolActionType.DeclarePon,
            "C04-G2-02" => (uint)ProtocolActionType.DeclareChi,
            "C04-G2-03" => (uint)ProtocolActionType.DeclareRiichi,
            "C04-G2-04" => (uint)ProtocolActionType.DeclareKan,
            _ => throw new InvalidDataException("Unknown fixed C04 action case.")
        };
        if (payload.RequireUInt32(CommandPayloadFields.ActionType, "actionType") != expectedAction)
            throw new InvalidDataException("Canonical action kind differs from the fixed C04 case.");
        uint[] expectedTiles = RequiredString(evidence, "CaseId") switch
        {
            "C04-G2-01" => new uint[] { 0, 1, 2 },
            "C04-G2-02" => new uint[] { 0, 4, 8 },
            "C04-G2-03" => new uint[] { 124 },
            "C04-G2-04" => new uint[] { 0, 1, 2, 3 },
            _ => throw new InvalidDataException("Unknown fixed C04 action case.")
        };
        uint[] actualTiles = payload.HasField(CommandPayloadFields.TileIds)
            ? payload.RequireRepeatedUInt32(CommandPayloadFields.TileIds, "tileIds").ToArray()
            : Array.Empty<uint>();
        if (!actualTiles.OrderBy(value => value).SequenceEqual(expectedTiles.OrderBy(value => value)))
            throw new InvalidDataException("Canonical action physical selection differs from the frozen case. expected=" +
                string.Join(",", expectedTiles) + " actual=" + string.Join(",", actualTiles) + ".");
        string optionId = payload.HasField(CommandPayloadFields.OptionId)
            ? ReadCanonicalString(payload, CommandPayloadFields.OptionId, "optionId") : string.Empty;
        Equal(RequiredString(evidence, "OptionId", true), optionId, "canonical legal candidate option");
    }

    private static string ReadCanonicalString(Mahjong.Contracts.CanonicalPayloadDocument payload, ushort fieldNumber, string fieldName)
    {
        byte[] encoded = System.Text.Encoding.UTF8.GetBytes(payload.RequireString(fieldNumber, fieldName));
        if (encoded.Length < 4) throw new InvalidDataException("Canonical string field has no inner length prefix: " + fieldName + ".");
        uint count = BitConverter.ToUInt32(encoded, 0);
        if (count != encoded.Length - 4) throw new InvalidDataException("Canonical string field has an invalid inner length: " + fieldName + ".");
        return new System.Text.UTF8Encoding(false, true).GetString(encoded, 4, encoded.Length - 4);
    }

    private static void VerifyPlayerView(JsonElement evidence, PlayerSnapshot snapshot, string prefix, bool baseline, Dictionary<int, string> positions)
    {
        PlayerPrivateView own = snapshot.PrivateView ?? throw new InvalidDataException(prefix + " private view is absent.");
        if (own.Seat != 0) throw new InvalidDataException(prefix + " protobuf private seat is not 0.");
        var publicMeldTiles = new HashSet<uint>();
        PublicBoardView physicalBoard = snapshot.PublicView?.Board ?? throw new InvalidDataException(prefix + " public board is absent.");
        foreach (PlayerPublicView player in physicalBoard.Players)
        {
            foreach (MeldView publicMeld in player.Melds)
            foreach (uint tile in publicMeld.TileIds)
            {
                if (!publicMeldTiles.Add(tile)) throw new InvalidDataException(prefix + " protobuf repeats a physical entity across public melds.");
                RequirePosition(positions, checked((int)tile), "MELD/" + player.Seat.ToString(CultureInfo.InvariantCulture) + "/", prefix + " public meld tile");
            }
            foreach (DiscardView discard in player.Discards)
            {
                string location = positions.TryGetValue(checked((int)discard.TileId), out string? actual) ? actual : string.Empty;
                if (!location.StartsWith("DISCARD/" + player.Seat.ToString(CultureInfo.InvariantCulture) + "/", StringComparison.Ordinal) &&
                    !(publicMeldTiles.Contains(discard.TileId) && location.StartsWith("MELD/", StringComparison.Ordinal)))
                    throw new InvalidDataException(prefix + " protobuf discard is not in its authoritative river or claimed meld history.");
            }
        }
        var closedSet = new HashSet<uint>();
        foreach (uint tile in own.ClosedTiles)
        {
            if (!closedSet.Add(tile)) throw new InvalidDataException(prefix + " protobuf repeats an own closed-hand physical entity.");
            RequirePosition(positions, checked((int)tile), "HAND/0/", prefix + " protobuf own closed tile");
        }
        if (own.HasDrawnTileId) RequirePosition(positions, checked((int)own.DrawnTileId), "HAND/0/", prefix + " protobuf own drawn tile");
        var privateHand = closedSet.Select(value => checked((int)value)).ToHashSet();
        if (own.HasDrawnTileId) privateHand.Add(checked((int)own.DrawnTileId)); // A drawn marker may name an entity already listed in ClosedTiles.
        var authorityHand = PositionsForSeat(positions, "HAND", 0);
        if (!privateHand.SetEquals(authorityHand)) throw new InvalidDataException(prefix + " protobuf private hand is not the complete unique authoritative hand entity set.");

        var authorityMeldsBySeat = Enumerable.Range(0, 4).ToDictionary(seat => seat, seat => PositionsForSeat(positions, "MELD", seat));
        var publicSeats = new HashSet<int>();
        string closed = string.Join(",", own.ClosedTiles.Select(value => value.ToString(CultureInfo.InvariantCulture)));
        string meld = own.OwnerMelds.Count == 0 ? string.Empty : string.Join(",", own.OwnerMelds[0].TileIds.Select(value => value.ToString(CultureInfo.InvariantCulture)));
        string drawn = own.HasDrawnTileId ? own.DrawnTileId.ToString(CultureInfo.InvariantCulture) : string.Empty;
        if (!baseline)
        {
            Equal(RequiredString(evidence, "HandTileIds"), closed, "protobuf closed hand");
            Equal(RequiredString(evidence, "MeldTileIds", true), meld, "protobuf owner meld");
            Equal(RequiredString(evidence, "DrawnTileId", true), drawn, "protobuf drawn tile");
            PublicBoardView board = snapshot.PublicView?.Board ?? throw new InvalidDataException("After protobuf public board is absent.");
            long score = board.Scores.Single(value => value.Seat == 0).Score;
            Equal(RequiredString(evidence, "SeatScoreAfter"), score.ToString(CultureInfo.InvariantCulture), "protobuf seat score");
            Equal(RequiredString(evidence, "RiichiSticksAfter"), board.RiichiSticks.ToString(CultureInfo.InvariantCulture), "protobuf riichi sticks");
            VerifyPublicHandAndMelds(board, authorityMeldsBySeat, positions, prefix, publicSeats);
        }
        else
        {
            VerifyPublicHandAndMelds(physicalBoard, authorityMeldsBySeat, positions, prefix, publicSeats);
            string expectedCase = RequiredString(evidence, "CaseId");
            int[] owned = expectedCase switch
            {
                "C04-G2-01" => new[] { 0, 1 },
                "C04-G2-02" => new[] { 0, 8 },
                "C04-G2-03" => Array.Empty<int>(),
                "C04-G2-04" => new[] { 0, 1, 2, 3 },
                _ => throw new InvalidDataException("Unknown fixed C04 case.")
            };
            foreach (int tile in owned)
                if (!own.ClosedTiles.Contains((uint)tile)) throw new InvalidDataException("Baseline protobuf hand lacks required fixed tile " + tile + ".");
            if (expectedCase == "C04-G2-01" && own.ClosedTiles.Contains(2U) ||
                expectedCase == "C04-G2-02" && own.ClosedTiles.Contains(4U))
                throw new InvalidDataException("Claimed call tile must originate from the opponent's latest discard, not the caller hand.");
        }
        var ownerMeldIds = new HashSet<int>();
        foreach (MeldView ownerMeld in own.OwnerMelds)
            foreach (uint tile in ownerMeld.TileIds)
            {
                if (!ownerMeldIds.Add(checked((int)tile))) throw new InvalidDataException(prefix + " protobuf repeats an own meld entity.");
                RequirePosition(positions, checked((int)tile), "MELD/0/", prefix + " protobuf own meld tile");
            }
        if (!ownerMeldIds.SetEquals(authorityMeldsBySeat[0])) throw new InvalidDataException(prefix + " protobuf owner melds are not the complete authoritative meld entity set.");
    }

    private static HashSet<int> PositionsForSeat(Dictionary<int, string> positions, string zone, int seat)
    {
        string prefix = zone + "/" + seat.ToString(CultureInfo.InvariantCulture) + "/";
        return positions.Where(pair => pair.Value.StartsWith(prefix, StringComparison.Ordinal)).Select(pair => pair.Key).ToHashSet();
    }

    private static void VerifyPublicHandAndMelds(PublicBoardView board,
        Dictionary<int, HashSet<int>> authorityMeldsBySeat, Dictionary<int, string> positions, string prefix,
        HashSet<int> publicSeats)
    {
        foreach (PlayerPublicView player in board.Players)
        {
            if (player.Seat < 0 || player.Seat > 3 || !publicSeats.Add(player.Seat)) throw new InvalidDataException(prefix + " protobuf public board has a missing/duplicate/invalid seat.");
            HashSet<int> hand = PositionsForSeat(positions, "HAND", player.Seat);
            if (player.HandCount != hand.Count) throw new InvalidDataException(prefix + " protobuf public hand count differs from authoritative physical hand count for seat " + player.Seat.ToString(CultureInfo.InvariantCulture) + ".");
            var meldTiles = new HashSet<int>();
            foreach (MeldView meld in player.Melds)
            {
                string meldType = meld.Type.ToString();
                int expectedTiles = meldType == "Chi" || meldType == "Pon" ? 3 : 4;
                if (meld.TileIds.Count != expectedTiles) throw new InvalidDataException(prefix + " protobuf public meld has an incomplete physical tile list.");
                foreach (uint tile in meld.TileIds)
                {
                    int id = checked((int)tile);
                    if (!meldTiles.Add(id)) throw new InvalidDataException(prefix + " protobuf repeats a physical entity in public melds.");
                    RequirePosition(positions, id, "MELD/" + player.Seat.ToString(CultureInfo.InvariantCulture) + "/", prefix + " protobuf public meld tile");
                }
            }
            if (!meldTiles.IsSubsetOf(authorityMeldsBySeat[player.Seat])) throw new InvalidDataException(prefix + " protobuf public melds contain an entity outside the authoritative meld set for seat " + player.Seat.ToString(CultureInfo.InvariantCulture) + ".");
        }
        if (publicSeats.Count != 4) throw new InvalidDataException(prefix + " protobuf public board does not contain all four seats.");
    }

    private static void VerifyActionEffects(JsonElement evidence, PlayerSnapshot baseline, PlayerSnapshot after,
        int beforeDeadWallCount, int afterActionDeadWallCount,
        Dictionary<int, string> beforePositions, Dictionary<int, string> afterPositions)
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
            if (afterOwn.OwnerMelds.Count != beforeOwn.OwnerMelds.Count || afterOwn.HasDrawnTileId)
                throw new InvalidDataException("Riichi evidence must not invent a meld or replacement draw.");
            PublicBoardView beforeBoard = baseline.PublicView?.Board ?? throw new InvalidDataException("Baseline public board is absent.");
            PublicBoardView afterBoard = after.PublicView?.Board ?? throw new InvalidDataException("After public board is absent.");
            long beforeScore = beforeBoard.Scores.Single(value => value.Seat == 0).Score;
            long afterScore = afterBoard.Scores.Single(value => value.Seat == 0).Score;
            if (beforeScore != 25000 || afterScore != 24000 || afterBoard.RiichiSticks != beforeBoard.RiichiSticks + 1 ||
                !afterBoard.Players.Single(value => value.Seat == 0).Riichi)
                throw new InvalidDataException("Parsed PlayerSnapshot does not prove the exact 1,000-point Riichi payment and one added stick.");
            if (afterOwn.ClosedTiles.Contains(124U) || afterOwn.HasDrawnTileId)
                throw new InvalidDataException("Parsed PlayerSnapshot does not bind the Riichi discard entity 124 to its real hand-to-river transfer.");
            RequirePosition(beforePositions, 124, "HAND/0/", "baseline Riichi discarded entity");
            RequirePosition(afterPositions, 124, "DISCARD/0/", "after Riichi river entity");
            return;
        }
        uint[] ids = expected.Select(value => uint.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        uint called = caseId == "C04-G2-01" ? 2U : caseId == "C04-G2-02" ? 4U : uint.MaxValue;
        uint[] ownTiles = called == uint.MaxValue ? ids : ids.Where(value => value != called).ToArray();
        if (!ownTiles.All(beforeOwn.ClosedTiles.Contains)) throw new InvalidDataException("Parsed baseline hand is missing a fixed owned entity.");
        if (called != uint.MaxValue)
        {
            if (beforeOwn.ClosedTiles.Contains(called) || baseline.PublicView!.Board.LastDiscard == null || baseline.PublicView.Board.LastDiscard.TileId != called)
                throw new InvalidDataException("Parsed baseline snapshot does not expose the expected opponent call discard.");
        }
        MeldView meld = afterOwn.OwnerMelds.SingleOrDefault(value => value.TileIds.OrderBy(tile => tile).SequenceEqual(ids.OrderBy(tile => tile)))
            ?? throw new InvalidDataException("Parsed after owner snapshot lacks the exact fixed meld entities.");
        if (ownTiles.Any(afterOwn.ClosedTiles.Contains)) throw new InvalidDataException("Parsed after hand still contains a claimed caller entity.");
        string expectedKind = caseId switch { "C04-G2-01" => "Pon", "C04-G2-02" => "Chi", _ => "ClosedKan" };
        Equal(expectedKind, meld.Type.ToString(), "protobuf owner meld kind");
        foreach (uint tile in ownTiles)
        {
            RequirePosition(beforePositions, (int)tile, "HAND/0/", "baseline owned tile");
            RequirePosition(afterPositions, (int)tile, "MELD/0/", "after owned meld tile");
        }
        if (ids.Any(afterOwn.ClosedTiles.Contains)) throw new InvalidDataException("Parsed after hand still contains an entity transferred to the meld.");
        if (called != uint.MaxValue)
        {
            RequirePosition(beforePositions, (int)called, "DISCARD/3/", "baseline called discard");
            RequirePosition(afterPositions, (int)called, "MELD/0/", "after claimed discard");
            if (afterOwn.HasDrawnTileId) throw new InvalidDataException("Pon/Chi snapshot must not invent a replacement draw.");
        }
        if (caseId == "C04-G2-04")
        {
            foreach (uint tile in ids) RequirePosition(beforePositions, (int)tile, "HAND/0/", "baseline closed-kan tile");
            foreach (uint tile in ids) RequirePosition(afterPositions, (int)tile, "MELD/0/", "after closed-kan tile");
        }
        if (caseId == "C04-G2-04")
        {
            if (!afterOwn.HasDrawnTileId || afterOwn.DrawnTileId != 73 || afterActionDeadWallCount != beforeDeadWallCount - 1 ||
                !"DEADWALL".Equals(PositionZone(beforePositions, 73), StringComparison.Ordinal) ||
                !"HAND".Equals(PositionZone(afterPositions, 73), StringComparison.Ordinal) ||
                !afterPositions[73].StartsWith("HAND/0/", StringComparison.Ordinal))
                throw new InvalidDataException("Parsed PlayerSnapshot and authority image do not prove dead-wall entity 73 as the closed-kan replacement draw.");
        }
    }

    private static void VerifyAuthorityReceipt(JsonElement evidence, LocalTableRecoveryFacts baselineFacts, LocalTableRecoveryFacts afterFacts, byte[] canonicalPayload)
    {
        string account = RequiredString(evidence, "AccountId");
        string scope = RequiredString(evidence, "ScopeId");
        string request = RequiredString(evidence, "RequestId");
        string digest = RequiredString(evidence, "PayloadSha256");
        string rawPayloadSha256 = Convert.ToHexString(SHA256.HashData(canonicalPayload));
        string commandVersion = RequiredString(evidence, "ReceiptCommandVersion");
        string eventSeq = RequiredString(evidence, "ReceiptEventSeq");
        if (baselineFacts.Requests.Any(value => value.AccountId == account && value.ScopeId == scope && value.RequestId == request))
            throw new InvalidDataException("The submitted request already exists in the baseline authority image.");
        LocalTableRecoveryRequestFact fact = afterFacts.Requests.SingleOrDefault(value =>
            value.AccountId == account && value.ScopeId == scope && value.RequestId == request)
            ?? throw new InvalidDataException("Raw authority save has no receipt for the exact account/scope/request identity.");
        if (!fact.Confirmed || fact.Status != "APPLIED" || !string.Equals(fact.PayloadSha256, rawPayloadSha256, StringComparison.OrdinalIgnoreCase) ||
            fact.CommandVersion != commandVersion || fact.EventSeq != eventSeq || fact.EvaluatedStateVersion != baselineFacts.StateVersion ||
            ulong.Parse(afterFacts.StateVersion, CultureInfo.InvariantCulture) <= ulong.Parse(baselineFacts.StateVersion, CultureInfo.InvariantCulture))
            throw new InvalidDataException("Raw authority receipt differs from the typed Player receipt/request/event facts. status=" + fact.Status +
                " confirmed=" + fact.Confirmed + " digest=" + fact.PayloadSha256 + " evaluated=" + fact.EvaluatedStateVersion +
                " command=" + fact.CommandVersion + " event=" + fact.EventSeq + " baseline=" + baselineFacts.StateVersion + ".");
        Equal(commandVersion, RequiredString(evidence, "EventCommandVersion"), "authority receipt event command version");
        if (ulong.Parse(afterFacts.StateVersion, CultureInfo.InvariantCulture) < ulong.Parse(commandVersion, CultureInfo.InvariantCulture))
            throw new InvalidDataException("Latest authority state predates the accepted action command version.");
        if (ulong.Parse(afterFacts.EventSeq, CultureInfo.InvariantCulture) < ulong.Parse(eventSeq, CultureInfo.InvariantCulture))
            throw new InvalidDataException("Authority event high-water predates the accepted receipt: receipt=" + eventSeq + " authority=" + afterFacts.EventSeq + ".");
        if (ulong.Parse(RequiredString(evidence, "AfterStateVersion"), CultureInfo.InvariantCulture) <=
            ulong.Parse(RequiredString(evidence, "BaselineStateVersion"), CultureInfo.InvariantCulture))
            throw new InvalidDataException("Player snapshot state version did not advance after the submitted action.");
        Equal(commandVersion, RequiredString(evidence, "AfterStateVersion"), "same-publication accepted action command version");
    }

    private static void VerifyRawBindingAndAcceptedEvent(JsonElement evidence, PlayerSnapshot baseline, PlayerSnapshot after)
    {
        Equal("Player", RequiredString(evidence, "EventRole"), "captured flat Player role");
        Equal("Incremental", RequiredString(evidence, "EventBatchKind"), "captured flat batch kind");
        if (JsonRequired(evidence, "EventFullBaseline").ValueKind != JsonValueKind.False)
            throw new InvalidDataException("Captured flat action event is not an incremental Player publication.");
        using JsonDocument baselineBinding = ParseEmbeddedJson(evidence, "BaselineBindingJsonBase64");
        using JsonDocument afterBinding = ParseEmbeddedJson(evidence, "AfterBindingJsonBase64");
        using JsonDocument rawReceipt = ParseEmbeddedJson(evidence, "RawReceiptJsonBase64");
        Equal(RequiredString(evidence, "ReceiptDisposition"), JsonString(rawReceipt.RootElement, "Disposition"), "raw typed receipt disposition");
        Equal(RequiredString(evidence, "ReceiptStatus"), JsonString(rawReceipt.RootElement, "Status"), "raw typed receipt status");
        Equal(RequiredString(evidence, "ReceiptRequestId"), JsonString(rawReceipt.RootElement, "RequestId"), "raw typed receipt request");
        Equal(RequiredString(evidence, "ReceiptScopeId"), JsonString(rawReceipt.RootElement, "ScopeId"), "raw typed receipt scope");
        Equal(RequiredString(evidence, "ReceiptDigest"), JsonString(rawReceipt.RootElement, "CanonicalPayloadSha256"), "raw typed receipt canonical digest");
        Equal(RequiredString(evidence, "ReceiptCommandVersion"), JsonNumber(rawReceipt.RootElement, "CommandVersion"), "raw typed receipt command version");
        Equal(RequiredString(evidence, "ReceiptEventSeq"), JsonNumber(rawReceipt.RootElement, "EventSeq"), "raw typed receipt event high-water");
        string session = RequiredString(evidence, "BaselineSessionId");
        string room = RequiredString(evidence, "BaselineRoomId");
        string account = RequiredString(evidence, "AccountId");
        string scope = RequiredString(evidence, "ScopeId");
        foreach (JsonElement binding in new[] { baselineBinding.RootElement, afterBinding.RootElement })
        {
            string bindingRole = JsonEnumName(JsonRequired(binding, "Role"), typeof(Mahjong.Contracts.ProtocolRole));
            if (bindingRole != RequiredString(evidence, "EventRole"))
                throw new InvalidDataException("Raw Player binding role is not Player.");
            Equal(session, JsonString(binding, "SessionId"), "raw binding session");
            Equal(room, JsonString(binding, "RoomId"), "raw binding room");
            Equal(room, JsonString(binding, "TableId"), "raw binding table");
            Equal(account, JsonString(binding, "AccountId"), "raw binding account");
            Equal(scope, JsonString(binding, "ScopeId"), "raw binding request scope");
            Equal(RequiredString(evidence, "BaselineSeat"), JsonNumber(binding, "SeatId"), "raw binding seat");
            Equal(RequiredString(evidence, "BaselineBindingEpoch"), JsonNumber(binding, "BindingEpoch"), "raw binding epoch");
            Equal(RequiredString(evidence, "BaselineConnectionEpoch"), JsonNumber(binding, "ConnectionEpoch"), "raw connection epoch");
            Equal(RequiredString(evidence, "BaselineOwnerEpoch"), JsonNumber(binding, "OwnerEpoch"), "raw owner epoch");
        }

        using JsonDocument acceptedBatch = ParseEmbeddedJson(evidence, "RawEventBatchJsonBase64");
        JsonElement batch = acceptedBatch.RootElement;
        if (JsonEnumName(JsonRequired(batch, "Role"), typeof(Mahjong.Contracts.ProtocolRole)) != RequiredString(evidence, "EventRole"))
            throw new InvalidDataException("Raw accepted batch role is not Player.");
        if (JsonEnumName(JsonRequired(batch, "Kind"), typeof(Mahjong.Client.TableVisibleEventBatchKind)) != RequiredString(evidence, "EventBatchKind"))
            throw new InvalidDataException("Raw accepted batch kind is not Incremental.");
        Equal(room, JsonString(batch, "RoomId"), "accepted publication room");
        Equal(RequiredString(evidence, "BaselineTableId"), JsonString(batch, "TableId"), "accepted publication table");
        Equal(session, JsonString(batch, "SessionId"), "accepted publication session");
        Equal(RequiredString(evidence, "BaselineRoundId"), JsonString(batch, "RoundId"), "accepted publication round");
        Equal(scope, JsonString(batch, "ScopeId"), "accepted publication scope");
        Equal(RequiredString(evidence, "BaselineSeat"), JsonNumber(batch, "Seat"), "accepted publication seat");
        Equal(RequiredString(evidence, "BaselineBindingEpoch"), JsonNumber(batch, "BindingEpoch"), "accepted publication binding epoch");
        Equal(RequiredString(evidence, "BaselineConnectionEpoch"), JsonNumber(batch, "ConnectionEpoch"), "accepted publication connection epoch");
        Equal(RequiredString(evidence, "BaselineOwnerEpoch"), JsonNumber(batch, "OwnerEpoch"), "accepted publication owner epoch");
        if (JsonBoolean(batch, "IsFullBaseline")) throw new InvalidDataException("Action evidence event batch is a baseline, not an incremental accepted publication.");
        JsonElement events = JsonRequired(batch, "Events");
        if (events.ValueKind != JsonValueKind.Array || events.GetArrayLength() == 0)
            throw new InvalidDataException("Raw accepted publication has no incremental events.");
        JsonElement[] matching = events.EnumerateArray().Where(value =>
            JsonNumber(value, "EventSeq") == RequiredString(evidence, "EventSeq") &&
            JsonNumber(value, "CommandVersion") == RequiredString(evidence, "EventCommandVersion") &&
            JsonNumber(value, "Seat") == RequiredString(evidence, "EventSeat") &&
            Enum.GetName(typeof(Mahjong.Contracts.ProtocolEventType), checked((int)JsonUInt64(value, "Type"))) == RequiredString(evidence, "EventType"))
            .ToArray();
        if (matching.Length != 1) throw new InvalidDataException("Raw accepted publication does not contain exactly one matched action event.");
        JsonElement action = matching[0];
        Equal(RequiredString(evidence, "EventSeq"), JsonNumber(action, "EventSeq"), "raw event sequence");
        Equal(RequiredString(evidence, "EventCommandVersion"), JsonNumber(action, "CommandVersion"), "raw event command version");
        Equal(RequiredString(evidence, "EventSeat"), JsonNumber(action, "Seat"), "raw event seat");
        Equal(RequiredString(evidence, "EventType"), Enum.GetName(typeof(Mahjong.Contracts.ProtocolEventType), checked((int)JsonUInt64(action, "Type"))) ?? string.Empty, "raw typed event kind");
        string tileIds = string.Join(",", JsonRequired(action, "TileIds").EnumerateArray().Select(value => value.GetUInt32().ToString(CultureInfo.InvariantCulture)));
        Equal(RequiredString(evidence, "EventTileIds", true), tileIds, "raw event physical entity list");
        Equal(RequiredString(evidence, "ReceiptCommandVersion"), JsonNumber(action, "CommandVersion"), "raw event receipt command version");
        Equal(RequiredString(evidence, "ReceiptEventSeq"), JsonNumber(batch, "EventSeq"), "accepted publication receipt event high-water");
        Equal(RequiredString(evidence, "AcceptanceSequence"), RequiredString(evidence, "AfterAcceptanceSequence"), "accepted publication/after snapshot acceptance sequence");
        Equal(RequiredString(evidence, "AfterCursor"), JsonNumber(batch, "ProjectionSeq"), "accepted publication projection cursor");
        Equal(RequiredString(evidence, "AfterCursorStream"), JsonNumber(batch, "StreamVersion"), "accepted publication stream cursor");
        Equal(RequiredString(evidence, "AcceptanceSequence"), JsonNumber(batch, "AcceptanceSequence"), "raw accepted publication acceptance sequence");
        Equal(RequiredString(evidence, "AcceptanceSequence"), RequiredString(evidence, "AfterAcceptanceSequence"), "accepted publication/after snapshot acceptance sequence");
        Equal(RequiredString(evidence, "ReceiptCommandVersion"), JsonNumber(batch, "StateVersion"), "accepted publication command version");
        if (RequiredString(evidence, "CaseId") == "C04-G2-03")
        {
            if (JsonUInt64(action, "TileId") != 124U || JsonUInt64(JsonRequired(action, "Payload"), "DiscardTileId") != 124U)
                throw new InvalidDataException("Raw RiichiDeclared event does not carry the exact discarded physical entity 124.");
        }
        if (baseline.SessionId != after.SessionId || baseline.RoundId != after.RoundId)
            throw new InvalidDataException("Accepted publication changed the captured Player session/round identity.");
    }

    private static JsonDocument ParseEmbeddedJson(JsonElement evidence, string name)
    {
        byte[] bytes = Convert.FromBase64String(RequiredString(evidence, name));
        if (bytes.Length == 0) throw new InvalidDataException("Raw JSON capture is empty: " + name + ".");
        return JsonDocument.Parse(bytes);
    }

    private static JsonElement JsonRequired(JsonElement value, string name)
    {
        foreach (JsonProperty property in value.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        throw new InvalidDataException("Raw typed record is missing " + name + ".");
    }

    private static string JsonString(JsonElement value, string name) => JsonRequired(value, name).GetString() ?? string.Empty;
    private static string JsonEnumName(JsonElement value, Type enumType)
    {
        if (!enumType.IsEnum) throw new ArgumentException("Expected enum type.", nameof(enumType));
        if (value.ValueKind == JsonValueKind.String)
        {
            string? name = value.GetString();
            if (string.IsNullOrEmpty(name) || !Enum.TryParse(enumType, name, false, out object? parsed) ||
                !Enum.IsDefined(enumType, parsed!)) throw new InvalidDataException("Raw enum field has an unknown string value.");
            return Enum.GetName(enumType, parsed!)!;
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) && Enum.IsDefined(enumType, number))
            return Enum.GetName(enumType, number)!;
        throw new InvalidDataException("Raw enum field has a missing or unknown value/type.");
    }
    private static string JsonNumber(JsonElement value, string name) => JsonRequired(value, name).GetRawText();
    private static ulong JsonUInt64(JsonElement value, string name) => JsonRequired(value, name).GetUInt64();
    private static bool JsonBoolean(JsonElement value, string name) => JsonRequired(value, name).GetBoolean();

    private static string NestedString(JsonElement value, params string[] path)
    {
        foreach (string segment in path) value = JsonRequired(value, segment);
        return value.GetString() ?? string.Empty;
    }

    private static string NestedNumber(JsonElement value, params string[] path)
    {
        foreach (string segment in path) value = JsonRequired(value, segment);
        return value.GetRawText();
    }

    private static Dictionary<int, string> ReadPhysicalPositions(string canonical)
    {
        var result = new Dictionary<int, string>(136);
        foreach (string line in canonical.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = line.Split('|');
            if (fields.Length != 5 || fields[0] != "tile" || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out int id) ||
                id < 0 || id >= 136 || result.ContainsKey(id)) throw new InvalidDataException("LocalHost physical audit contains a malformed/duplicate entity row.");
            result.Add(id, fields[2] + "/" + fields[3] + "/" + fields[4]);
        }
        if (result.Count != 136) throw new InvalidDataException("LocalHost physical audit does not contain all 136 entities.");
        return result;
    }

    private static void RequirePosition(Dictionary<int, string> positions, int tile, string prefix, string label)
    {
        if (!positions.TryGetValue(tile, out string? value) || !value.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidDataException(label + " is not at the authoritative expected location: tile " + tile.ToString(CultureInfo.InvariantCulture) + ".");
    }

    private static string PositionZone(Dictionary<int, string> positions, int tile) =>
        positions.TryGetValue(tile, out string? value) ? value.Split('/')[0] : string.Empty;

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

    private static string RequiredString(JsonElement element, string name, bool allowEmpty = false)
    {
        if (!TryProperty(element, name, out JsonElement value) || (value.ValueKind != JsonValueKind.String && value.ValueKind != JsonValueKind.Number))
            throw new InvalidDataException("Missing/empty evidence field " + name + ".");
        string result = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
        if (!allowEmpty && string.IsNullOrWhiteSpace(result)) throw new InvalidDataException("Missing/empty evidence field " + name + ".");
        return result;
    }

    private static int RequiredInt(JsonElement element, string name)
    {
        if (!TryProperty(element, name, out JsonElement value) || !value.TryGetInt32(out int result)) throw new InvalidDataException("Missing/noninteger evidence field " + name + ".");
        return result;
    }

    private static ulong RequiredUInt64(JsonElement element, string name)
    {
        if (!TryProperty(element, name, out JsonElement value) || !value.TryGetUInt64(out ulong result))
            throw new InvalidDataException("Missing/non-UInt64 evidence field " + name + ".");
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
