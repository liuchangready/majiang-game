using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Mahjong.Contracts.Wire;
using ProtocolConstants = Mahjong.Contracts.ProtocolConstants;

if (args.Length != 2 || args[0] is not ("verify" or "self-test"))
{
    Console.Error.WriteLine("usage: C04PrivacyMatrixVerifier <verify|self-test> <raw-frames.json|fixture-directory>");
    return 64;
}

if (args[0] == "self-test")
{
    try
    {
        if (Directory.Exists(args[1])) throw new IOException("self-test fixture directory exists; refusing to overwrite it");
        Directory.CreateDirectory(args[1]);
        FrameFile fixture = BuildLegalFixture();
        string path = Path.Combine(args[1], "valid.json");
        WriteFixture(path, fixture);
        var capture = ReadCapture(path);
        Frame[] fixtureFrames = capture.frames ?? [];
        List<string> errors = Verify(fixtureFrames);
        Require(errors.Count == 0, "WireCodec-generated valid fixture failed: " + string.Join("; ", errors));
        Require(fixtureFrames.Where(frame => frame.audience == "Player").All(frame => frame.privateLedgerEntryCount > 0 &&
                    frame.ownerSleeveTileCount > 0 && frame.privateImmediateSleeveOfferTileCount == 2 &&
                    frame.privateOffTurnSleeveOfferTileCount == 2 && frame.privateSleeveReservationCount == 1),
            "WireCodec-generated owner positives must include nonempty ledger, sleeve, and immediate/off-turn offer contents");
        Require(fixtureFrames.Where(frame => frame.audience == "Public").All(frame => frame.privateCheatPoints == 0 &&
                    frame.privateLedgerEntryCount == 0 && frame.ownerSleeveTileCount == 0 && !frame.hasPrivateCheatView),
            "WireCodec-generated public positive contains private owner fields");
        FrameFile genesisFixture = BuildEmptyGenesisPushFixture(fixture);
        string genesisPath = Path.Combine(args[1], "valid-empty-genesis-push.json");
        WriteFixture(genesisPath, genesisFixture);
        List<string> genesisErrors = Verify(ReadCapture(genesisPath).frames ?? []);
        Require(genesisErrors.Count == 0, "valid empty emitted genesis push was rejected: " + string.Join("; ", genesisErrors));
        foreach (var positive in new[] { BuildLegalOpenPonFixture(fixture), BuildLegalPublicRevealProgressFixture(fixture) })
            Require(Verify(ReadCapture(WriteFixtureAndReadback(args[1], positive)).frames ?? []).Count == 0,
                "legal post-call/public-reveal WireCodec fixture was rejected");
        var negative = BuildNegativeFixtures(fixture);
        foreach (var item in negative)
        {
            string casePath = Path.Combine(args[1], item.Key + ".json");
            WriteFixture(casePath, item.Value);
            Require(Verify(ReadCapture(casePath).frames ?? []).Count != 0, $"self-test mutation was accepted: {item.Key}");
        }
        Console.WriteLine($"SELF_TEST_PASS positive=4 negative={negative.Count} serializedFrames={capture.frames!.Length} source=WireCodec");
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"SELF_TEST_ERROR {exception.GetType().Name}: {exception.Message}");
        return 3;
    }
}

try
{
    FrameFile capture = ReadCapture(args[1]);
    Frame[] rows = capture.frames ?? [];
    List<string> errors = Verify(rows);
    foreach (string error in errors) Console.Error.WriteLine(error);
    if (errors.Count != 0) return 2;
    Console.WriteLine($"PRIVACY_MATRIX_PASS frames={rows.Length} sha256={Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[1]))).ToLowerInvariant()}");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"VERIFY_ERROR {exception.GetType().Name}: {exception.Message}");
    return 3;
}

static FrameFile ReadCapture(string path)
{
    var info = new FileInfo(path);
    if (!info.Exists || info.Length <= 0 || info.Length > 64L * 1024 * 1024) throw new InvalidDataException("raw capture is missing, empty or larger than the 64 MiB verifier input cap");
    var capture = JsonSerializer.Deserialize<FrameFile>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    if (capture is null || capture.schemaVersion != 2) throw new InvalidDataException("unsupported raw capture schema; Welcome-bound schema 2 is required");
    if (capture.frames is { Length: > 4096 }) throw new InvalidDataException("raw capture exceeds 4096 frame verifier cap");
    return capture;
}

static List<string> Verify(IEnumerable<Frame> frames)
{
    var errors = new List<string>();
    var owners = new HashSet<int>();
    var visibility = new List<(string room, string round, ulong version, int owner, HashSet<uint> hidden, HashSet<uint> publicTiles)>();
    var scopeSessions = new Dictionary<string, string>(StringComparer.Ordinal);
    var sessionScopes = new Dictionary<string, string>(StringComparer.Ordinal);
    var sessionBindings = new Dictionary<string, (string role, int seat, string account, string room)>(StringComparer.Ordinal);
    var roomOwnerEpochs = new Dictionary<string, ulong>(StringComparer.Ordinal);
    int count = 0;
    foreach (Frame row in frames)
    {
        count++;
        try
        {
            byte[] raw = DecodeBase64(row.rawBase64, "projection raw");
            byte[] welcomeRaw = DecodeBase64(row.welcomeRawBase64, "Welcome raw");
            EqualSha(raw, row.wireSha256, $"frame[{row.ordinal}] projection");
            EqualSha(welcomeRaw, row.welcomeWireSha256, $"frame[{row.ordinal}] Welcome");

            WireDecodeResult welcomeDecoded = WireCodec.Decode(welcomeRaw);
            Require(welcomeDecoded.Status == WireDecodeStatus.Ok && welcomeDecoded.Envelope != null, "Welcome raw did not decode as a valid current wire envelope");
            Require(welcomeDecoded.UnknownFieldCount == 0 && CountUnknownFields(welcomeDecoded.Envelope!) == 0, "Welcome contains unknown protobuf fields");
            Envelope welcomeEnvelope = welcomeDecoded.Envelope!;
            Require(welcomeEnvelope.MessageType == MessageType.Welcome && welcomeEnvelope.BodyCase == Envelope.BodyOneofCase.Welcome,
                "Welcome raw is not a typed Welcome body");
            Welcome welcome = welcomeEnvelope.Welcome ?? throw new InvalidDataException("typed Welcome body is absent");
            Require(!string.IsNullOrWhiteSpace(welcome.SessionId) && !string.IsNullOrWhiteSpace(welcome.RoomId) &&
                    !string.IsNullOrWhiteSpace(welcome.TableId) && welcome.TableId == welcome.RoomId,
                "typed Welcome lacks session/room/table/scope identity");
            RequestScope scope = welcome.Scope ?? throw new InvalidDataException("typed Welcome scope is absent");
            Require(welcome.ProtocolMajor == ProtocolConstants.Major && welcome.ProtocolMinor <= ProtocolConstants.Minor &&
                    welcome.RulesetVersion == ProtocolConstants.RulesetVersion && welcome.ProjectionVersion == ProtocolConstants.ProjectionVersion,
                "typed Welcome protocol/ruleset/projection version is not current");
            Require(welcomeEnvelope.ProtocolMajor == ProtocolConstants.Major && welcomeEnvelope.RulesetVersion == ProtocolConstants.RulesetVersion &&
                    welcomeEnvelope.ProjectionVersion == ProtocolConstants.ProjectionVersion,
                "Welcome envelope version is not current");
            Require(welcomeEnvelope.ProtocolMinor == welcome.ProtocolMinor && welcomeEnvelope.ProtocolMajor == welcome.ProtocolMajor &&
                    welcomeEnvelope.RulesetVersion == welcome.RulesetVersion && welcomeEnvelope.ProjectionVersion == welcome.ProjectionVersion,
                "Welcome envelope version fields differ from typed Welcome version fields");
            Require(welcomeEnvelope.RoomId == welcome.RoomId && welcomeEnvelope.SessionId == welcome.SessionId &&
                    welcomeEnvelope.SeatId == welcome.SeatId && welcomeEnvelope.BindingEpoch == welcome.BindingEpoch &&
                    (welcomeEnvelope.OwnerEpoch == 0 || welcomeEnvelope.OwnerEpoch == welcome.OwnerEpoch),
                "Welcome envelope header identity differs from typed Welcome identity");
            Require(scope.Role == welcome.Role && scope.RoomId == welcome.RoomId && !string.IsNullOrWhiteSpace(scope.ScopeId) &&
                    !string.IsNullOrWhiteSpace(scope.AccountId) && scope.ExpiresAtMs > scope.IssuedAtMs,
                "Welcome scope does not bind role, room, scope id/account or a valid lifetime");
            string role = welcome.Role switch
            {
                Role.Player => "Player",
                Role.Spectator => "Spectator",
                _ => throw new InvalidDataException($"unsupported Welcome role {welcome.Role}")
            };
            int seat = role == "Player" ? welcome.SeatId : -1;
            Require(role == "Player" ? seat is >= 0 and <= 3 : welcome.SeatId == -1, "Welcome role has an invalid seat binding");
            Require(welcome.ConnectionEpoch > 0 && welcome.BindingEpoch > 0 && welcome.OwnerEpoch > 0,
                "Welcome lacks positive connection/binding/owner epoch");
            if (roomOwnerEpochs.TryGetValue(welcome.RoomId, out ulong knownOwnerEpoch) && knownOwnerEpoch != welcome.OwnerEpoch)
                throw new InvalidDataException("typed Welcome owner epoch differs across connections for the same room");
            roomOwnerEpochs[welcome.RoomId] = welcome.OwnerEpoch;
            Require(welcome.Baseline != null && welcome.Baseline.Cursor != null, "Welcome lacks typed stream baseline/cursor");

            WireDecodeResult decoded = WireCodec.Decode(raw);
            Require(decoded.Status == WireDecodeStatus.Ok && decoded.Envelope != null, "projection raw is malformed or rejected by WireCodec");
            Envelope envelope = decoded.Envelope!;
            Require(decoded.UnknownFieldCount == 0 && CountUnknownFields(envelope) == 0, "projection contains unknown protobuf fields");
            Require(envelope.ProtocolMajor == ProtocolConstants.Major && envelope.ProtocolMinor <= ProtocolConstants.Minor &&
                    envelope.RulesetVersion == ProtocolConstants.RulesetVersion && envelope.ProjectionVersion == ProtocolConstants.ProjectionVersion,
                "projection envelope version is not current");
            Require(envelope.RoomId == welcome.RoomId && envelope.SessionId == welcome.SessionId && envelope.SeatId == welcome.SeatId &&
                    envelope.BindingEpoch == welcome.BindingEpoch && (envelope.OwnerEpoch == 0 || envelope.OwnerEpoch == welcome.OwnerEpoch),
                "projection envelope identity differs from the raw Welcome binding");
            Compare(row.messageType, envelope.MessageType.ToString(), "messageType");
            Compare(row.bindingEpoch, envelope.BindingEpoch, "bindingEpoch");
            Require(StringComparer.Ordinal.Equals(row.decodeStatus, decoded.Status.ToString()), "decodeStatus summary differs from actual WireCodec result");
            Require(row.sessionId == welcome.SessionId && row.roomId == welcome.RoomId && row.scopeId == scope.ScopeId &&
                    row.accountId == scope.AccountId && row.connectionRole == role && row.boundSeat == seat,
                "identity summaries differ from decoded Welcome");
            var binding = (role, seat, scope.AccountId, welcome.RoomId);
            if (scopeSessions.TryGetValue(scope.ScopeId, out string? priorSession) && priorSession != welcome.SessionId)
                throw new InvalidDataException("one Welcome scope is shared across connection sessions");
            if (sessionScopes.TryGetValue(welcome.SessionId, out string? priorScope) && priorScope != welcome.Scope.ScopeId)
                throw new InvalidDataException("one Welcome session changed scope");
            if (sessionBindings.TryGetValue(welcome.SessionId, out var priorBinding) && priorBinding != binding)
                throw new InvalidDataException("one Welcome session changed role/seat/account/room binding");
            scopeSessions[scope.ScopeId] = welcome.SessionId;
            sessionScopes[welcome.SessionId] = scope.ScopeId;
            sessionBindings[welcome.SessionId] = binding;

            ProjectionFacts facts = ReadProjection(envelope, role, seat);
            CompareSummary(row, facts);
            ValidateRedundantContract(envelope, facts);
            visibility.Add((welcome.RoomId, facts.RoundId, facts.StateVersion, facts.OwnerSeat, facts.HiddenTileIds, facts.PublicTileIds));
            bool fullGenesis = facts.IsFull && facts.StateVersion == 0 && facts.EventSeq == 0;
            bool emptyGenesisPush = !facts.IsFull && facts.StateVersion == 0 && facts.EventSeq == 0 &&
                facts.CursorStreamVersion == 0 && facts.CursorProjectionSeq > 0 && facts.VisibleEventCount == 0;
            Require(facts.StateVersion != 0 || fullGenesis || emptyGenesisPush,
                "zero state version lacks a full baseline or empty emitted genesis push");
            Require(facts.EventSeq != 0 || fullGenesis || emptyGenesisPush,
                "zero event sequence lacks a full baseline or empty emitted genesis push");
            Require(facts.IsFull || facts.VisibleEventCount == 0 || facts.EventSeq != 0,
                "visible events have no event sequence");
            Require(welcome.BindingEpoch > 0, "missing positive Welcome binding epoch");
            if (facts.Audience is "Player" or "PlayerUpdate") owners.Add(facts.SeatId);
        }
        catch (Exception exception)
        {
            errors.Add($"frame[{row.ordinal}] {exception.Message}");
        }
    }
    if (count == 0) errors.Add("no real inbound frames were captured");
    if (!Enumerable.Range(0, 4).All(owners.Contains)) errors.Add("one or more Welcome-bound seats lack a nonempty private positive control");
    foreach (var group in visibility.Where(x => !string.IsNullOrWhiteSpace(x.room) && !string.IsNullOrWhiteSpace(x.round)).GroupBy(x => (x.room, x.round, x.version)))
    {
        var rows = group.ToArray();
        for (int i = 0; i < rows.Length; i++)
        for (int j = i + 1; j < rows.Length; j++)
        {
            if (rows[i].owner >= 0 && rows[j].owner >= 0 && rows[i].owner != rows[j].owner && rows[i].hidden.Overlaps(rows[j].hidden))
                errors.Add($"same-state private tile content overlaps between seat {rows[i].owner} and seat {rows[j].owner}");
        }
        HashSet<uint> publicTiles = rows.SelectMany(x => x.publicTiles).ToHashSet();
        foreach (var row in rows.Where(x => x.owner >= 0))
            if (row.hidden.Overlaps(publicTiles)) errors.Add($"same-state hidden private tile is present in the public board for seat {row.owner}");
    }
    return errors;
}

static ProjectionFacts ReadProjection(Envelope envelope, string role, int welcomeSeat)
{
    PlayerSnapshot? player = null;
    PublicSnapshot? publicSnapshot = null;
    string audience;
    int visibleEvents = 0;
    bool full;
    switch (envelope.BodyCase)
    {
        case Envelope.BodyOneofCase.SyncResponse:
            SyncResponse response = envelope.SyncResponse;
            full = response.IsFullSnapshot;
            if (response.Target == SyncTarget.PlayerStream && response.BaselineCase == SyncResponse.BaselineOneofCase.Player)
            { audience = "Player"; player = response.Player; Require(response.StateVersion == response.Player.StateVersion && response.EventSeq == response.Player.EventSeq, "SyncResponse redundant state/event version differs from typed player snapshot"); }
            else if (response.Target == SyncTarget.PublicStream && response.BaselineCase == SyncResponse.BaselineOneofCase.Public)
            { audience = "Public"; publicSnapshot = response.Public; Require(response.StateVersion == response.Public.StateVersion && response.EventSeq == response.Public.EventSeq, "SyncResponse redundant state/event version differs from typed public snapshot"); }
            else throw new InvalidDataException("SyncResponse target and typed baseline do not match a player/public projection");
            break;
        case Envelope.BodyOneofCase.PlayerUpdate:
            audience = "PlayerUpdate"; player = envelope.PlayerUpdate.Snapshot; full = envelope.PlayerUpdate.IsFullSnapshot;
            visibleEvents = envelope.PlayerUpdate.VisibleEvents.Count;
            Require(envelope.PlayerUpdate.Cursor != null && envelope.PlayerUpdate.Cursor.Equals(envelope.PlayerUpdate.Snapshot.Cursor), "PlayerUpdate cursor differs from typed snapshot cursor");
            break;
        case Envelope.BodyOneofCase.PublicUpdate:
            audience = "PublicUpdate"; publicSnapshot = envelope.PublicUpdate.Snapshot; full = envelope.PublicUpdate.IsFullSnapshot;
            visibleEvents = envelope.PublicUpdate.VisibleEvents.Count;
            Require(envelope.PublicUpdate.Cursor != null && envelope.PublicUpdate.Cursor.Equals(envelope.PublicUpdate.Snapshot.Cursor), "PublicUpdate cursor differs from typed snapshot cursor");
            break;
        default: throw new InvalidDataException($"unsupported projection body {envelope.BodyCase}");
    }
    MessageType expectedType = envelope.BodyCase switch
    {
        Envelope.BodyOneofCase.SyncResponse => MessageType.SyncResponse,
        Envelope.BodyOneofCase.PlayerUpdate => MessageType.PlayerUpdate,
        Envelope.BodyOneofCase.PublicUpdate => MessageType.PublicUpdate,
        _ => throw new InvalidDataException($"unsupported projection body {envelope.BodyCase}")
    };
    Require(envelope.MessageType == expectedType, "projection envelope message type differs from typed body oneof");
    bool isPlayer = audience is "Player" or "PlayerUpdate";
    Require(isPlayer == (role == "Player"), "projection audience is inconsistent with the raw Welcome role");
    Require(isPlayer ? envelope.SeatId == welcomeSeat : envelope.SeatId == -1, "projection seat differs from decoded Welcome role/seat");
    if (isPlayer)
    {
        PlayerSnapshot actualPlayer = player ?? throw new InvalidDataException("player projection is absent");
        PlayerPrivateView privateView = actualPlayer.PrivateView ?? throw new InvalidDataException("player private projection is absent");
        Require(actualPlayer.PublicView != null, "player projection lacks typed public snapshot");
        Require(actualPlayer.Cursor != null, "player projection lacks typed stream cursor");
        int owner = privateView.Seat;
        Require(owner == welcomeSeat, "typed private owner differs from decoded Welcome seat");
        Require(privateView.ClosedTiles.Count > 0, "typed owner hand has no closed tile positive control");
        var ownedIds = new HashSet<uint>();
        var hiddenIds = new HashSet<uint>();
        foreach (uint tile in privateView.ClosedTiles) { Require(tile < 136 && ownedIds.Add(tile), "typed owner hand contains an invalid or duplicate physical tile id"); hiddenIds.Add(tile); }
        if (privateView.HasDrawnTileId) { Require(privateView.DrawnTileId < 136 && ownedIds.Add(privateView.DrawnTileId), "typed drawn tile is invalid or duplicates an owned hand tile"); hiddenIds.Add(privateView.DrawnTileId); }
        foreach (uint tile in privateView.SleeveTiles) { Require(tile < 136 && ownedIds.Add(tile), "typed owner sleeve contains an invalid or duplicate private physical tile id"); hiddenIds.Add(tile); }
        foreach (MeldView meld in privateView.OwnerMelds)
            foreach (uint tile in meld.TileIds) { Require(tile < 136 && ownedIds.Add(tile), "typed owner meld contains an invalid or duplicate private physical tile id"); if (meld.Type == MeldType.ClosedKan) hiddenIds.Add(tile); }
        int logicalHandCount = privateView.ClosedTiles.Count + (privateView.HasDrawnTileId ? 1 : 0);
        Require(logicalHandCount == 13 - 3 * privateView.OwnerMelds.Count || logicalHandCount == 14 - 3 * privateView.OwnerMelds.Count,
            $"typed owner concealed hand size {logicalHandCount} is inconsistent with owner meld count {privateView.OwnerMelds.Count}");
        Require(privateView.Cheat != null && privateView.Control != null, "typed owner private cheat/control view is absent");
        Require(actualPlayer.SessionId == envelope.SessionId, "typed player snapshot session differs from envelope");
        Require(CountUnknownFields(actualPlayer) == 0, "player snapshot tree contains unknown fields");
        PrivateCheatView cheat = privateView.Cheat ?? throw new InvalidDataException("player private cheat view is absent");
        PlayerControlView control = privateView.Control ?? throw new InvalidDataException("player private control view is absent");
        return new ProjectionFacts(audience, full, actualPlayer.Cursor?.StreamVersion ?? 0, actualPlayer.Cursor?.ProjectionSeq ?? 0,
            visibleEvents, actualPlayer.SessionId, actualPlayer.RoundId, actualPlayer.StateVersion, actualPlayer.EventSeq, owner, owner, privateView.ClosedTiles.Count,
            true, cheat.CheatPoints, cheat.LedgerEntries.Count, cheat.TileMark?.Groups.Count ?? 0,
            CountSleeveOffer(cheat.SleeveBloom), CountSleeveOffer(cheat.OffTurnSleeveBloom), cheat.SleeveBloomReservation == null ? 0 : 1,
            privateView.SleeveTiles.Count, privateView.OwnerMelds.Count, true, control.ControlEpoch,
            control.ControlVersion, false, hiddenIds, ExtractPublicTiles(actualPlayer.PublicView));
    }
    PublicSnapshot actualPublic = publicSnapshot ?? throw new InvalidDataException("public projection is absent");
    Require(actualPublic.View != null, "public projection lacks typed public view");
    Require(actualPublic.Cursor != null, "public projection lacks typed stream cursor");
    Require(actualPublic.SessionId == envelope.SessionId, "typed public snapshot session differs from envelope");
    Require(CountUnknownFields(actualPublic) == 0, "public snapshot tree contains unknown fields");
    return new ProjectionFacts(audience, full, actualPublic.Cursor?.StreamVersion ?? 0, actualPublic.Cursor?.ProjectionSeq ?? 0,
        visibleEvents, actualPublic.SessionId, actualPublic.RoundId, actualPublic.StateVersion, actualPublic.EventSeq, -1, -1, 0, false, 0, 0, 0, 0, 0, 0,
        0, 0, false, 0, 0, true, new HashSet<uint>(), ExtractPublicTiles(actualPublic.View));
}

static HashSet<uint> ExtractPublicTiles(PublicMatchView? view)
{
    var result = new HashSet<uint>();
    if (view?.Board == null) return result;
    void Add(uint id) { Require(id < 136, "public board contains an invalid physical tile id"); result.Add(id); }
    foreach (PlayerPublicView p in view.Board.Players)
    {
        foreach (DiscardView d in p.Discards) Add(d.TileId);
        foreach (MeldView m in p.Melds) foreach (uint id in m.TileIds) Add(id);
    }
    if (view.Board.LastDiscard != null) Add(view.Board.LastDiscard.TileId);
    foreach (uint id in view.Board.DoraIndicators) Add(id);
    return result;
}

static void ValidateRedundantContract(Envelope envelope, ProjectionFacts facts)
{
    if (envelope.Cursor != null)
        Require(envelope.Cursor.StreamVersion == facts.CursorStreamVersion && envelope.Cursor.ProjectionSeq == facts.CursorProjectionSeq,
            "envelope cursor differs from typed projection snapshot cursor");
}

static int CountSleeveOffer(SleeveBloomView? offer) => offer == null ? 0 :
    (offer.HasFirstTileId ? 1 : 0) + (offer.HasSecondTileId ? 1 : 0) + (offer.HasReservedTileId ? 1 : 0);

static void CompareSummary(Frame row, ProjectionFacts f)
{
    Compare(row.audience, f.Audience, "audience"); Compare(row.isFullSnapshot, f.IsFull, "isFullSnapshot");
    Compare(row.cursorStreamVersion, f.CursorStreamVersion, "cursorStreamVersion"); Compare(row.cursorProjectionSeq, f.CursorProjectionSeq, "cursorProjectionSeq");
    Compare(row.visibleEventCount, f.VisibleEventCount, "visibleEventCount"); Compare(row.seatId, f.SeatId, "seatId"); Compare(row.ownerSeat, f.OwnerSeat, "ownerSeat");
    Compare(row.privateClosedTileCount, f.PrivateClosedTileCount, "privateClosedTileCount"); Compare(row.hasPrivateCheatView, f.HasPrivateCheatView, "hasPrivateCheatView");
    Compare(row.privateCheatPoints, f.PrivateCheatPoints, "privateCheatPoints"); Compare(row.privateLedgerEntryCount, f.PrivateLedgerEntryCount, "privateLedgerEntryCount");
    Compare(row.privateMarkedTileCount, f.PrivateMarkedTileCount, "privateMarkedTileCount"); Compare(row.privateImmediateSleeveOfferTileCount, f.PrivateImmediateSleeveOfferTileCount, "privateImmediateSleeveOfferTileCount");
    Compare(row.privateOffTurnSleeveOfferTileCount, f.PrivateOffTurnSleeveOfferTileCount, "privateOffTurnSleeveOfferTileCount");
    Compare(row.privateSleeveReservationCount, f.PrivateSleeveReservationCount, "privateSleeveReservationCount"); Compare(row.ownerSleeveTileCount, f.OwnerSleeveTileCount, "ownerSleeveTileCount");
    Compare(row.ownerMeldCount, f.OwnerMeldCount, "ownerMeldCount"); Compare(row.hasPrivateControlView, f.HasPrivateControlView, "hasPrivateControlView");
    Compare(row.controlEpoch, f.ControlEpoch, "controlEpoch"); Compare(row.controlVersion, f.ControlVersion, "controlVersion"); Compare(row.hasPublicSnapshot, f.HasPublicSnapshot, "hasPublicSnapshot");
    Compare(row.stateVersion, f.StateVersion, "stateVersion"); Compare(row.eventSeq, f.EventSeq, "eventSeq");
    Require(row.unknownFieldsZero, "unknownFieldsZero summary is false");
}

static FrameFile BuildLegalFixture()
{
    var rows = new List<Frame>();
    for (int seat = 0; seat < 4; seat++)
    {
        var welcome = CreateWelcome(Role.Player, seat, $"p{seat}");
        var snapshot = CreatePlayerSnapshot(seat, welcome.Welcome.SessionId);
        Envelope projection = CreateEnvelope(MessageType.SyncResponse, welcome.Welcome, seat,
            new SyncResponse { Target = SyncTarget.PlayerStream, IsFullSnapshot = true, Player = snapshot, StateVersion = 1, EventSeq = 1 });
        rows.Add(CreateRow(welcome, projection));
    }
    var publicWelcome = CreateWelcome(Role.Spectator, -1, "viewer");
    var publicSnapshot = new PublicSnapshot { SessionId = publicWelcome.Welcome.SessionId, RoundId = "round", Cursor = new StreamCursor { StreamVersion = 1, ProjectionSeq = 1 }, StateVersion = 1, EventSeq = 1, View = new PublicMatchView { Board = FixturePublicBoard() } };
    Envelope publicProjection = CreateEnvelope(MessageType.SyncResponse, publicWelcome.Welcome, -1,
        new SyncResponse { Target = SyncTarget.PublicStream, IsFullSnapshot = true, Public = publicSnapshot, StateVersion = 1, EventSeq = 1 });
    rows.Add(CreateRow(publicWelcome, publicProjection));
    return new FrameFile { schemaVersion = 2, frames = rows.ToArray() };
}

static FrameFile BuildEmptyGenesisPushFixture(FrameFile source)
{
    Frame[] rows = (Frame[])source.frames!.Clone();
    Envelope prior = Envelope.Parser.ParseFrom(Convert.FromBase64String(rows[0].rawBase64));
    PlayerSnapshot snapshot = prior.SyncResponse.Player.Clone();
    snapshot.StateVersion = 0;
    snapshot.EventSeq = 0;
    snapshot.Cursor = new StreamCursor { StreamVersion = 0, ProjectionSeq = 2 };
    Envelope push = new()
    {
        ProtocolMajor = prior.ProtocolMajor,
        ProtocolMinor = prior.ProtocolMinor,
        RulesetVersion = prior.RulesetVersion,
        ProjectionVersion = prior.ProjectionVersion,
        MessageType = MessageType.PlayerUpdate,
        RoomId = prior.RoomId,
        SessionId = prior.SessionId,
        SeatId = prior.SeatId,
        BindingEpoch = prior.BindingEpoch,
        OwnerEpoch = prior.OwnerEpoch,
        PlayerUpdate = new PlayerUpdate
        {
            Snapshot = snapshot,
            FromCursor = new StreamCursor(),
            Cursor = new StreamCursor { StreamVersion = 0, ProjectionSeq = 2 },
            IsFullSnapshot = false
        }
    };
    Envelope welcome = Envelope.Parser.ParseFrom(Convert.FromBase64String(rows[0].welcomeRawBase64));
    rows[0] = CreateRow(welcome, push);
    return new FrameFile { schemaVersion = 2, frames = rows };
}

static string WriteFixtureAndReadback(string root, FrameFile fixture)
{
    string path = Path.Combine(root, $"positive-{Guid.NewGuid():N}.json");
    WriteFixture(path, fixture);
    return path;
}

static FrameFile BuildLegalOpenPonFixture(FrameFile source)
{
    Frame[] rows = (Frame[])source.frames!.Clone();
    Envelope welcome = WireCodec.Decode(Convert.FromBase64String(rows[0].welcomeRawBase64)).Envelope!;
    Envelope projection = WireCodec.Decode(Convert.FromBase64String(rows[0].rawBase64)).Envelope!;
    PlayerSnapshot snapshot = projection.SyncResponse.Player;
    for (int i = 0; i < 3; i++) snapshot.PrivateView.ClosedTiles.RemoveAt(10);
    snapshot.PrivateView.OwnerMelds.Add(new MeldView { Type = MeldType.Pon, TileIds = { 100, 101, 102 }, CalledTileId = 102, FromSeat = 1 });
    snapshot.PublicView.Board.Players[0].Melds.Add(new MeldView { Type = MeldType.Pon, TileIds = { 100, 101, 102 }, CalledTileId = 102, FromSeat = 1 });
    rows[0] = CreateRow(welcome, projection);
    return new FrameFile { schemaVersion = 2, frames = rows };
}

static FrameFile BuildLegalPublicRevealProgressFixture(FrameFile source)
{
    Frame[] rows = (Frame[])source.frames!.Clone();
    Envelope welcome = WireCodec.Decode(Convert.FromBase64String(rows[0].welcomeRawBase64)).Envelope!;
    Envelope projection = WireCodec.Decode(Convert.FromBase64String(rows[0].rawBase64)).Envelope!;
    PlayerSnapshot snapshot = projection.SyncResponse.Player;
    snapshot.StateVersion = 2; snapshot.EventSeq = 2;
    snapshot.Cursor = new StreamCursor { StreamVersion = 2, ProjectionSeq = 2 };
    projection.SyncResponse.StateVersion = 2; projection.SyncResponse.EventSeq = 2;
    snapshot.PrivateView.ClosedTiles.RemoveAt(0);
    snapshot.PrivateView.DrawnTileId = 70;
    snapshot.PublicView.Board.Players[0].Discards.Add(new DiscardView { TileId = 0, TurnNumber = 1 });
    rows[0] = CreateRow(welcome, projection);
    return new FrameFile { schemaVersion = 2, frames = rows };
}

static Dictionary<string, FrameFile> BuildNegativeFixtures(FrameFile source)
{
    Frame[] rows = source.frames!;
    var cases = new Dictionary<string, FrameFile>(StringComparer.Ordinal);
    void One(string name, int index, Frame changed) { var copy = (Frame[])rows.Clone(); copy[index] = changed; cases.Add(name, new FrameFile { schemaVersion = 2, frames = copy }); }
    One("raw-empty", 0, rows[0] with { rawBase64 = Convert.ToBase64String(Array.Empty<byte>()), wireSha256 = Sha(Array.Empty<byte>()) });
    One("raw-invalid-one-byte", 0, rows[0] with { rawBase64 = Convert.ToBase64String(new byte[] { 1 }), wireSha256 = Sha(new byte[] { 1 }) });
    One("cross-seat-raw-summary-unchanged", 0, rows[0] with { rawBase64 = rows[1].rawBase64, wireSha256 = rows[1].wireSha256 });
    One("hand-count-summary-rewritten", 0, rows[0] with { privateClosedTileCount = 14 });
    One("zero-version-without-valid-genesis", 0, MutateProjection(rows[0], envelope =>
    {
        envelope.SyncResponse.IsFullSnapshot = false;
        envelope.SyncResponse.Player.StateVersion = 0;
        envelope.SyncResponse.Player.EventSeq = 0;
        envelope.SyncResponse.Player.Cursor.StreamVersion = 0;
        envelope.SyncResponse.Player.Cursor.ProjectionSeq = 0;
    }));
    One("public-private-summary", 4, rows[4] with { privateCheatPoints = 42 });
    One("wrong-sync-state-version", 0, MutateProjection(rows[0], envelope => envelope.SyncResponse.StateVersion = 999));
    One("wrong-envelope-cursor", 0, MutateProjection(rows[0], envelope => envelope.Cursor = new StreamCursor { StreamVersion = 999, ProjectionSeq = 999 }));
    One("wrong-projection-message-type-summary-rewritten", 0, MutateProjection(rows[0], envelope => envelope.MessageType = MessageType.PublicSnapshot) with { messageType = MessageType.PublicSnapshot.ToString() });
    One("wrong-welcome-owner-epoch-header-unset", 0, MutateWelcomeOwnerEpochWithoutHeader(rows[0]));
    One("foreign-closed-hand-content", 0, MutateProjection(rows[0], envelope =>
    {
        envelope.SyncResponse.Player.PrivateView.ClosedTiles.Clear();
        envelope.SyncResponse.Player.PrivateView.ClosedTiles.Add(Enumerable.Range(13, 13).Select(i => (uint)i));
    }));
    One("public-current-hidden-tile-as-discard", 0, MutateProjection(rows[0], envelope =>
        envelope.SyncResponse.Player.PublicView.Board.Players[0].Discards.Add(new DiscardView { TileId = 0, TurnNumber = 1 })));
    One("unknown-field-summary-false", 0, rows[0] with { unknownFieldsZero = false });
    One("missing-welcome", 0, rows[0] with { welcomeRawBase64 = string.Empty, welcomeWireSha256 = string.Empty });
    One("welcome-record-is-not-projection", 0, rows[0] with { rawBase64 = rows[0].welcomeRawBase64, wireSha256 = rows[0].welcomeWireSha256 });
    One("truncated-projection-wire", 0, rows[0] with { rawBase64 = Convert.ToBase64String(new byte[] { 0x0A, 0x80 }), wireSha256 = Sha(new byte[] { 0x0A, 0x80 }) });
    One("wrong-welcome-session", 0, MutateWelcome(rows[0], welcome => welcome.SessionId += "-forged"));
    One("wrong-welcome-role", 0, MutateWelcome(rows[0], welcome => welcome.Role = Role.Spectator));
    One("wrong-welcome-seat", 0, MutateWelcome(rows[0], welcome => welcome.SeatId = 2));
    One("wrong-welcome-envelope-session", 0, MutateWelcomeEnvelope(rows[0], envelope => envelope.SessionId += "-header"));
    One("wrong-welcome-envelope-binding", 0, MutateWelcomeEnvelope(rows[0], envelope => envelope.BindingEpoch++));
    One("wrong-welcome-scope-id", 0, MutateWelcome(rows[0], welcome => welcome.Scope.ScopeId = ""));
    One("wrong-welcome-scope-role", 0, MutateWelcome(rows[0], welcome => welcome.Scope.Role = Role.Spectator));
    One("wrong-welcome-scope-room", 0, MutateWelcome(rows[0], welcome => welcome.Scope.RoomId = "foreign-room"));
    One("wrong-welcome-version", 0, MutateWelcome(rows[0], welcome => welcome.ProjectionVersion++));
    One("wrong-welcome-owner-epoch", 0, MutateWelcome(rows[0], welcome => welcome.OwnerEpoch++));
    One("wrong-welcome-binding-epoch", 0, MutateWelcome(rows[0], welcome => welcome.BindingEpoch++));
    One("wrong-projection-owner", 0, MutateProjection(rows[0], envelope => envelope.SyncResponse.Player.PrivateView.Seat = 1));
    One("wrong-projection-audience", 0, MutateProjection(rows[0], envelope => envelope.SyncResponse.Target = SyncTarget.PublicStream));
    One("wrong-projection-message-type", 0, MutateProjection(rows[0], envelope => envelope.MessageType = MessageType.PublicSnapshot));
    One("unknown-projection-message-type", 0, MutateProjection(rows[0], envelope => envelope.MessageType = (MessageType)999));
    One("wrong-projection-version", 0, MutateProjection(rows[0], envelope => envelope.ProjectionVersion++));
    One("projection-unknown-field", 0, AppendUnknownProjectionField(rows[0]));
    One("welcome-unknown-field", 0, AppendUnknownWelcomeField(rows[0]));
    One("public-nested-unknown-field", 4, AppendUnknownPublicSnapshotField(rows[4]));
    return cases;
}

static Frame MutateProjection(Frame frame, Action<Envelope> mutation)
{
    Envelope envelope = Envelope.Parser.ParseFrom(Convert.FromBase64String(frame.rawBase64));
    mutation(envelope);
    return frame with { rawBase64 = Convert.ToBase64String(envelope.ToByteArray()), wireSha256 = Sha(envelope.ToByteArray()) };
}

static Frame MutateWelcome(Frame frame, Action<Welcome> mutation)
{
    Envelope envelope = Envelope.Parser.ParseFrom(Convert.FromBase64String(frame.welcomeRawBase64));
    mutation(envelope.Welcome);
    byte[] raw = envelope.ToByteArray();
    return frame with { welcomeRawBase64 = Convert.ToBase64String(raw), welcomeWireSha256 = Sha(raw) };
}

static Frame MutateWelcomeEnvelope(Frame frame, Action<Envelope> mutation)
{
    Envelope envelope = Envelope.Parser.ParseFrom(Convert.FromBase64String(frame.welcomeRawBase64));
    mutation(envelope);
    byte[] raw = envelope.ToByteArray();
    return frame with { welcomeRawBase64 = Convert.ToBase64String(raw), welcomeWireSha256 = Sha(raw) };
}

static Frame MutateWelcomeOwnerEpochWithoutHeader(Frame frame)
{
    Envelope envelope = Envelope.Parser.ParseFrom(Convert.FromBase64String(frame.welcomeRawBase64));
    envelope.OwnerEpoch = 0;
    envelope.Welcome.OwnerEpoch++;
    byte[] raw = envelope.ToByteArray();
    return frame with { welcomeRawBase64 = Convert.ToBase64String(raw), welcomeWireSha256 = Sha(raw) };
}

static Frame AppendUnknownProjectionField(Frame frame)
{
    byte[] raw = Convert.FromBase64String(frame.rawBase64).Concat(new byte[] { 0x98, 0x06, 0x01 }).ToArray();
    return frame with { rawBase64 = Convert.ToBase64String(raw), wireSha256 = Sha(raw) };
}

static Frame AppendUnknownWelcomeField(Frame frame)
{
    byte[] raw = Convert.FromBase64String(frame.welcomeRawBase64).Concat(new byte[] { 0x98, 0x06, 0x01 }).ToArray();
    return frame with { welcomeRawBase64 = Convert.ToBase64String(raw), welcomeWireSha256 = Sha(raw) };
}

static Frame AppendUnknownPublicSnapshotField(Frame frame)
{
    Envelope envelope = Envelope.Parser.ParseFrom(Convert.FromBase64String(frame.rawBase64));
    envelope.SyncResponse.Public.MergeFrom(new byte[] { 0xA0, 0x06, 0x01 });
    byte[] raw = envelope.ToByteArray();
    return frame with { rawBase64 = Convert.ToBase64String(raw), wireSha256 = Sha(raw) };
}

static void WriteFixture(string path, FrameFile fixture)
{
    File.WriteAllText(path, JsonSerializer.Serialize(fixture, new JsonSerializerOptions { WriteIndented = true }));
}

static Envelope CreateWelcome(Role role, int seat, string suffix)
{
    uint v = ProtocolConstants.Major;
    var welcome = new Welcome
    {
        SessionId = "session-" + suffix, RoomId = "room-real-wire", TableId = "room-real-wire", Role = role, SeatId = seat,
        ConnectionEpoch = 1, OwnerEpoch = 1, BindingEpoch = 1,
        Baseline = new StreamBaseline { Cursor = new StreamCursor { StreamVersion = 1, ProjectionSeq = 1 } },
        Scope = new RequestScope { ScopeId = "scope-" + suffix, AccountId = "account-" + suffix, Role = role, RoomId = "room-real-wire", IssuedAtMs = 1, ExpiresAtMs = 2 },
        ProtocolMajor = v, ProtocolMinor = ProtocolConstants.Minor, RulesetVersion = ProtocolConstants.RulesetVersion, ProjectionVersion = ProtocolConstants.ProjectionVersion
    };
    return CreateEnvelope(MessageType.Welcome, welcome, seat, welcomeBody: welcome);
}

static Envelope CreateEnvelope(MessageType type, Welcome welcome, int seat, SyncResponse? sync = null, Welcome? welcomeBody = null)
{
    var e = new Envelope { ProtocolMajor = ProtocolConstants.Major, ProtocolMinor = ProtocolConstants.Minor,
        RulesetVersion = ProtocolConstants.RulesetVersion, ProjectionVersion = ProtocolConstants.ProjectionVersion,
        MessageType = type, RoomId = welcome.RoomId, SessionId = welcome.SessionId, SeatId = seat,
        BindingEpoch = welcome.BindingEpoch, OwnerEpoch = welcome.OwnerEpoch };
    if (type == MessageType.Welcome) e.Welcome = welcomeBody ?? welcome;
    else e.SyncResponse = sync ?? throw new InvalidOperationException("sync response is required");
    return e;
}

static PlayerSnapshot CreatePlayerSnapshot(int seat, string session) => new()
{
    SessionId = session, RoundId = "round", Cursor = new StreamCursor { StreamVersion = 1, ProjectionSeq = 1 }, StateVersion = 1, EventSeq = 1,
    PublicView = new PublicMatchView { Board = FixturePublicBoard() },
    PrivateView = new PlayerPrivateView
    {
        Seat = seat,
        ClosedTiles = { Enumerable.Range(0, 13).Select(i => (uint)(seat * 13 + i)) },
        SleeveTiles = { (uint)(80 + seat) },
        Cheat = new PrivateCheatView
        {
            CheatPoints = 10,
            LedgerEntries = { new CheatEntryView { EntryId = $"ledger-{seat}", Amount = -1, BalanceAfter = 10, ReasonCode = "SLEEVE_BLOOM" } },
            SleeveBloom = new SleeveBloomView
            {
                OfferId = $"immediate-{seat}", Mode = "IMMEDIATE", FirstTileId = 60, SecondTileId = 61,
                DrawKind = SleeveBloomDrawKind.Normal
            },
            OffTurnSleeveBloom = new SleeveBloomView
            {
                OfferId = $"off-turn-{seat}", Mode = "OFF_TURN", WindowId = $"window-{seat}",
                FirstTileId = 62, SecondTileId = 63, DrawKind = SleeveBloomDrawKind.Normal
            },
            SleeveBloomReservation = new SleeveBloomReservationView
            {
                OfferId = $"reservation-{seat}", TileId = 64, WindowId = $"window-{seat}"
            }
        },
        Control = new PlayerControlView { ControlEpoch = 1, ControlVersion = 1 }
    }
};

static PublicBoardView FixturePublicBoard() => new()
{
    Phase = GamePhase.Draw, DealerSeat = 0, CurrentSeat = 0,
    Scores = { new ScoreView { Seat = 0, Score = 25000 } },
    Players = { new PlayerPublicView { Seat = 0, HandCount = 13 }, new PlayerPublicView { Seat = 1, HandCount = 13 },
        new PlayerPublicView { Seat = 2, HandCount = 13 }, new PlayerPublicView { Seat = 3, HandCount = 13 } }
};

static Frame CreateRow(Envelope welcome, Envelope projection)
{
    byte[] w = WireCodec.Encode(welcome), p = WireCodec.Encode(projection);
    Envelope parsedWelcome = WireCodec.Decode(w).Envelope!;
    Envelope parsedProjection = WireCodec.Decode(p).Envelope!;
    string role = parsedWelcome.Welcome.Role == Role.Player ? "Player" : "Spectator";
    bool player = parsedProjection.BodyCase == Envelope.BodyOneofCase.PlayerUpdate ||
        (parsedProjection.BodyCase == Envelope.BodyOneofCase.SyncResponse && parsedProjection.SyncResponse.Target == SyncTarget.PlayerStream);
    PlayerSnapshot? ps = parsedProjection.BodyCase == Envelope.BodyOneofCase.PlayerUpdate ? parsedProjection.PlayerUpdate.Snapshot :
        player ? parsedProjection.SyncResponse.Player : null;
    PublicSnapshot? pubs = parsedProjection.BodyCase == Envelope.BodyOneofCase.PublicUpdate ? parsedProjection.PublicUpdate.Snapshot :
        player ? null : parsedProjection.SyncResponse.Public;
    bool isFull = parsedProjection.BodyCase == Envelope.BodyOneofCase.PlayerUpdate ? parsedProjection.PlayerUpdate.IsFullSnapshot :
        parsedProjection.BodyCase == Envelope.BodyOneofCase.PublicUpdate ? parsedProjection.PublicUpdate.IsFullSnapshot : parsedProjection.SyncResponse.IsFullSnapshot;
    int visibleEventCount = parsedProjection.BodyCase == Envelope.BodyOneofCase.PlayerUpdate ? parsedProjection.PlayerUpdate.VisibleEvents.Count :
        parsedProjection.BodyCase == Envelope.BodyOneofCase.PublicUpdate ? parsedProjection.PublicUpdate.VisibleEvents.Count : 0;
    PrivateCheatView? cheat = ps?.PrivateView.Cheat;
    return new Frame { ordinal = parsedProjection.SeatId + 2, rawBase64 = Convert.ToBase64String(p), wireSha256 = Sha(p),
        welcomeRawBase64 = Convert.ToBase64String(w), welcomeWireSha256 = Sha(w), decodeStatus = "Ok",
        messageType = parsedProjection.MessageType.ToString(), bindingEpoch = parsedProjection.BindingEpoch,
        sessionId = parsedWelcome.Welcome.SessionId, roomId = parsedWelcome.Welcome.RoomId, scopeId = parsedWelcome.Welcome.Scope.ScopeId,
        accountId = parsedWelcome.Welcome.Scope.AccountId, connectionRole = role, boundSeat = role == "Player" ? parsedWelcome.Welcome.SeatId : -1,
        audience = player ? (parsedProjection.BodyCase == Envelope.BodyOneofCase.PlayerUpdate ? "PlayerUpdate" : "Player") :
            (parsedProjection.BodyCase == Envelope.BodyOneofCase.PublicUpdate ? "PublicUpdate" : "Public"),
        seatId = parsedProjection.SeatId, ownerSeat = ps?.PrivateView.Seat ?? -1,
        isFullSnapshot = isFull, cursorStreamVersion = player ? ps!.Cursor.StreamVersion : pubs!.Cursor.StreamVersion,
        cursorProjectionSeq = player ? ps!.Cursor.ProjectionSeq : pubs!.Cursor.ProjectionSeq, visibleEventCount = visibleEventCount,
        privateClosedTileCount = ps?.PrivateView.ClosedTiles.Count ?? 0, hasPrivateCheatView = cheat != null, privateCheatPoints = cheat?.CheatPoints ?? 0,
        privateLedgerEntryCount = cheat?.LedgerEntries.Count ?? 0, privateMarkedTileCount = cheat?.TileMark?.Groups.Count ?? 0,
        privateImmediateSleeveOfferTileCount = CountSleeveOffer(cheat?.SleeveBloom), privateOffTurnSleeveOfferTileCount = CountSleeveOffer(cheat?.OffTurnSleeveBloom),
        privateSleeveReservationCount = cheat?.SleeveBloomReservation == null ? 0 : 1, ownerSleeveTileCount = ps?.PrivateView.SleeveTiles.Count ?? 0,
        ownerMeldCount = ps?.PrivateView.OwnerMelds.Count ?? 0, hasPrivateControlView = ps?.PrivateView.Control != null,
        controlEpoch = ps?.PrivateView.Control?.ControlEpoch ?? 0, controlVersion = ps?.PrivateView.Control?.ControlVersion ?? 0,
        hasPublicSnapshot = pubs != null, stateVersion = player ? ps!.StateVersion : pubs!.StateVersion, eventSeq = player ? ps!.EventSeq : pubs!.EventSeq,
        unknownFieldsZero = true };
}

static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
static byte[] DecodeBase64(string? value, string label)
{
    if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException(label + " is missing");
    try { byte[] bytes = Convert.FromBase64String(value); if (bytes.Length == 0) throw new InvalidDataException(label + " is empty"); if (bytes.Length > WireLimits.MaxInboundMessageBytes) throw new InvalidDataException(label + " exceeds the production wire size cap"); return bytes; }
    catch (FormatException ex) { throw new InvalidDataException(label + " is invalid base64", ex); }
}
static void EqualSha(byte[] bytes, string? expected, string label)
{
    Require(expected is { Length: 64 } && expected.All(Uri.IsHexDigit), label + " SHA-256 summary is invalid");
    Require(StringComparer.OrdinalIgnoreCase.Equals(Sha(bytes), expected), label + " raw bytes do not match SHA-256 summary");
}
static int CountUnknownFields(IMessage message)
{
    int count = 0;
    var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
    void Visit(IMessage current)
    {
        if (!visited.Add(current)) return;
        FieldInfo? unknownField = current.GetType().GetField("_unknownFields", BindingFlags.Instance | BindingFlags.NonPublic);
        object? unknown = unknownField?.GetValue(current);
        if (unknown != null)
        {
            PropertyInfo? n = unknown.GetType().GetProperty("Count", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (n?.GetValue(unknown) is int number) count += number;
            else count++;
        }
        foreach (Google.Protobuf.Reflection.FieldDescriptor field in current.Descriptor.Fields.InFieldNumberOrder())
        {
            if (field.FieldType is not (Google.Protobuf.Reflection.FieldType.Message or Google.Protobuf.Reflection.FieldType.Group)) continue;
            object? value = field.Accessor.GetValue(current);
            if (field.IsRepeated)
            {
                if (value is System.Collections.IEnumerable items)
                    foreach (object? item in items) if (item is IMessage child) Visit(child);
            }
            else if (value is IMessage child) Visit(child);
        }
    }
    Visit(message);
    return count;
}
static void Compare<T>(T actual, T expected, string name)
{
    if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new InvalidDataException($"{name} summary differs from decoded typed field");
}
static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }

sealed record ProjectionFacts(string Audience, bool IsFull, ulong CursorStreamVersion, ulong CursorProjectionSeq, int VisibleEventCount,
    string SessionId, string RoundId, ulong StateVersion, ulong EventSeq, int SeatId, int OwnerSeat, int PrivateClosedTileCount, bool HasPrivateCheatView,
    long PrivateCheatPoints, int PrivateLedgerEntryCount, int PrivateMarkedTileCount, int PrivateImmediateSleeveOfferTileCount,
    int PrivateOffTurnSleeveOfferTileCount, int PrivateSleeveReservationCount, int OwnerSleeveTileCount, int OwnerMeldCount,
    bool HasPrivateControlView, ulong ControlEpoch, ulong ControlVersion, bool HasPublicSnapshot, HashSet<uint> HiddenTileIds, HashSet<uint> PublicTileIds);

sealed record Frame
{
    public int ordinal { get; init; }
    public string wireSha256 { get; init; } = "";
    public string rawBase64 { get; init; } = "";
    public string welcomeWireSha256 { get; init; } = "";
    public string welcomeRawBase64 { get; init; } = "";
    public string decodeStatus { get; init; } = "";
    public string messageType { get; init; } = "";
    public ulong bindingEpoch { get; init; }
    public string audience { get; init; } = "";
    public bool isFullSnapshot { get; init; }
    public ulong cursorStreamVersion { get; init; }
    public ulong cursorProjectionSeq { get; init; }
    public int visibleEventCount { get; init; }
    public string sessionId { get; init; } = "";
    public string roomId { get; init; } = "";
    public string scopeId { get; init; } = "";
    public string accountId { get; init; } = "";
    public string connectionRole { get; init; } = "";
    public int boundSeat { get; init; } = -1;
    public int seatId { get; init; } = -1;
    public int ownerSeat { get; init; } = -1;
    public int privateClosedTileCount { get; init; }
    public bool hasPrivateCheatView { get; init; }
    public long privateCheatPoints { get; init; }
    public int privateLedgerEntryCount { get; init; }
    public int privateMarkedTileCount { get; init; }
    public int privateImmediateSleeveOfferTileCount { get; init; }
    public int privateOffTurnSleeveOfferTileCount { get; init; }
    public int privateSleeveReservationCount { get; init; }
    public int ownerSleeveTileCount { get; init; }
    public int ownerMeldCount { get; init; }
    public bool hasPrivateControlView { get; init; }
    public ulong controlEpoch { get; init; }
    public ulong controlVersion { get; init; }
    public bool hasPublicSnapshot { get; init; }
    public ulong stateVersion { get; init; }
    public ulong eventSeq { get; init; }
    public bool unknownFieldsZero { get; init; }
}

sealed record FrameFile { public int schemaVersion { get; init; } public Frame[]? frames { get; init; } }
