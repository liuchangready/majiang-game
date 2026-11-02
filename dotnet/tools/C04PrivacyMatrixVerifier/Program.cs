using System.Security.Cryptography;
using System.Text.Json;

if (args.Length != 2 || args[0] is not ("verify" or "self-test"))
{
    Console.Error.WriteLine("usage: C04PrivacyMatrixVerifier <verify|self-test> <raw-frames.json>");
    return 64;
}

if (args[0] == "self-test")
{
    var valid = new[]
    {
        CreateFrame("player", 0, "Player", 13, true, 0, 1) with { eventSeq = 0 },
        CreateFrame("player", 1, "Player", 13, true, 4, 1),
        CreateFrame("player", 2, "Player", 13, true, 4, 1),
        CreateFrame("player", 3, "Player", 13, true, 4, 1),
        CreateFrame("spectator", -1, "Public", 0, false, 4, 1)
    };
    Require(Verify(valid).Count == 0, "valid owner/audience fixtures rejected");
    Require(Verify(new[] { valid[0] with { ownerSeat = 1 } }).Count != 0, "wrong owner accepted");
    Require(Verify(new[] { valid[0] with { audience = "Public" } }).Count != 0, "private fields in public audience accepted");
    Require(Verify(new[] { valid[4] with { privateClosedTileCount = 2, ownerSeat = 2, hasPrivateCheatView = true } }).Count != 0, "spectator private payload accepted");
    Require(Verify(new[] { valid[0] with { isFullSnapshot = false } }).Count != 0, "zero genesis accepted outside full snapshot");
    Require(Verify(new[] { valid[0] with { unknownFieldsZero = false } }).Count != 0, "unknown wire fields accepted");
    Console.WriteLine("SELF_TEST_PASS positive=1 negative=5");
    return 0;
}

try
{
    var capture = JsonSerializer.Deserialize<FrameFile>(File.ReadAllText(args[1]), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    if (capture is null || capture.schemaVersion != 1) throw new InvalidDataException("unsupported raw capture schema");
    var rows = capture.frames ?? [];
    var errors = Verify(rows);
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

static List<string> Verify(IEnumerable<Frame> frames)
{
    var errors = new List<string>();
    var owners = new HashSet<int>();
    var scopeOwners = new Dictionary<string, int>(StringComparer.Ordinal);
    var scopeSessions = new Dictionary<string, string>(StringComparer.Ordinal);
    var sessionScopes = new Dictionary<string, string>(StringComparer.Ordinal);
    var sessionBindings = new Dictionary<string, (string role, int seat, string account)>(StringComparer.Ordinal);
    int count = 0;
    foreach (Frame row in frames)
    {
        count++;
        if (!IsSha(row.wireSha256)) errors.Add($"frame[{row.ordinal}] invalid wire SHA-256");
        byte[] raw;
        try { raw = Convert.FromBase64String(row.rawBase64); }
        catch { errors.Add($"frame[{row.ordinal}] invalid raw base64"); continue; }
        string actual = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant();
        if (!StringComparer.OrdinalIgnoreCase.Equals(actual, row.wireSha256)) errors.Add($"frame[{row.ordinal}] raw wire hash mismatch");
        if (!StringComparer.Ordinal.Equals(row.decodeStatus, "Ok")) errors.Add($"frame[{row.ordinal}] wire decode status {row.decodeStatus}");
        if (!row.unknownFieldsZero) errors.Add($"frame[{row.ordinal}] contains unknown fields");
        if (String.IsNullOrWhiteSpace(row.sessionId) || String.IsNullOrWhiteSpace(row.roomId) ||
            String.IsNullOrWhiteSpace(row.scopeId) || String.IsNullOrWhiteSpace(row.accountId))
            errors.Add($"frame[{row.ordinal}] lacks session/room/scope/account identity bound from Welcome");
        if (scopeSessions.TryGetValue(row.scopeId, out string? priorScopeSession) && priorScopeSession != row.sessionId)
            errors.Add($"frame[{row.ordinal}] scope is shared across connection sessions");
        if (sessionScopes.TryGetValue(row.sessionId, out string? priorSessionScope) && priorSessionScope != row.scopeId)
            errors.Add($"frame[{row.ordinal}] one connection session changed scope");
        var binding = (row.connectionRole, row.boundSeat, row.accountId);
        if (sessionBindings.TryGetValue(row.sessionId, out var priorBinding) && priorBinding != binding)
            errors.Add($"frame[{row.ordinal}] one connection session changed its Welcome binding");
        scopeSessions[row.scopeId] = row.sessionId;
        sessionScopes[row.sessionId] = row.scopeId;
        sessionBindings[row.sessionId] = binding;
        if (row.stateVersion == 0 && !(row.isFullSnapshot && row.eventSeq == 0)) errors.Add($"frame[{row.ordinal}] zero state version is valid only for a full genesis snapshot");
        if (row.eventSeq == 0 && !(row.isFullSnapshot && row.stateVersion == 0)) errors.Add($"frame[{row.ordinal}] zero event cursor is valid only for a full genesis snapshot");
        if (row.bindingEpoch == 0) errors.Add($"frame[{row.ordinal}] missing binding epoch");
        if (row.audience is "Player" or "PlayerUpdate")
        {
            if (row.seatId is < 0 or > 3 || row.ownerSeat != row.seatId) errors.Add($"frame[{row.ordinal}] private owner does not match bound seat");
            if (row.connectionRole != "Player" || row.boundSeat != row.seatId) errors.Add($"frame[{row.ordinal}] player payload differs from Welcome role/seat");
            if (row.privateClosedTileCount == 0) errors.Add($"frame[{row.ordinal}] owner lacks a nonempty private hand");
            if (row.privateClosedTileCount is < 13 or > 14) errors.Add($"frame[{row.ordinal}] private hand count is outside 13–14 closed/drawn tiles");
            if (!row.hasPrivateCheatView) errors.Add($"frame[{row.ordinal}] owner lacks private cheat view evidence");
            if (!row.hasPrivateControlView) errors.Add($"frame[{row.ordinal}] owner lacks a private control view");
            if (row.hasPublicSnapshot) errors.Add($"frame[{row.ordinal}] player audience was classified as public baseline");
            owners.Add(row.seatId);
            if (scopeOwners.TryGetValue(row.scopeId, out int priorOwner) && priorOwner != row.seatId) errors.Add($"frame[{row.ordinal}] one scope is shared by multiple seat owners");
            scopeOwners[row.scopeId] = row.seatId;
        }
        else if (row.audience is "Public" or "PublicUpdate")
        {
            if (row.seatId != -1) errors.Add($"frame[{row.ordinal}] public flow has a player seat binding");
            if (row.connectionRole != "Spectator" || row.boundSeat != -1) errors.Add($"frame[{row.ordinal}] public payload differs from Welcome role/seat");
            if (row.ownerSeat != -1 || row.privateClosedTileCount != 0 || row.hasPrivateCheatView ||
                row.privateCheatPoints != 0 || row.privateLedgerEntryCount != 0 || row.privateMarkedTileCount != 0 ||
                row.privateImmediateSleeveOfferTileCount != 0 || row.privateOffTurnSleeveOfferTileCount != 0 ||
                row.privateSleeveReservationCount != 0 || row.ownerSleeveTileCount != 0 || row.ownerMeldCount != 0 ||
                row.hasPrivateControlView || row.controlEpoch != 0 || row.controlVersion != 0)
                errors.Add($"frame[{row.ordinal}] spectator/public flow contains private owner fields");
            if (!row.hasPublicSnapshot) errors.Add($"frame[{row.ordinal}] public flow lacks typed public snapshot evidence");
        }
        else errors.Add($"frame[{row.ordinal}] has unsupported audience '{row.audience}'");
    }
    if (count == 0) errors.Add("no real inbound frames were captured");
    if (!Enumerable.Range(0, 4).All(owners.Contains)) errors.Add("one or more seat owners lack a nonempty private positive control");
    return errors;
}

static bool IsSha(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static Frame CreateFrame(string role, int seat, string audience, int tileCount, bool cheat, ulong version, ulong epoch)
{
    byte[] raw = [1];
    return new() { ordinal = 1, isFullSnapshot = true, wireSha256 = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant(), rawBase64 = Convert.ToBase64String(raw), decodeStatus = "Ok", audience = audience, sessionId = role + "-session-" + seat, roomId = "room-positive", scopeId = role + "-scope-" + seat, accountId = role + "-account-" + seat, connectionRole = role == "player" ? "Player" : "Spectator", boundSeat = seat, seatId = seat, ownerSeat = role == "player" ? seat : -1, privateClosedTileCount = tileCount, hasPrivateCheatView = cheat, hasPublicSnapshot = audience == "Public", hasPrivateControlView = role == "player", controlEpoch = role == "player" ? 1UL : 0UL, controlVersion = role == "player" ? 1UL : 0UL, stateVersion = version, eventSeq = 1, bindingEpoch = epoch, unknownFieldsZero = true };
}

sealed record Frame
{
    public int ordinal { get; init; }
    public bool isFullSnapshot { get; init; }
    public string wireSha256 { get; init; } = "";
    public string rawBase64 { get; init; } = "";
    public string decodeStatus { get; init; } = "";
    public string audience { get; init; } = "";
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
    public ulong bindingEpoch { get; init; }
    public bool unknownFieldsZero { get; init; }
}

sealed record FrameFile
{
    public int schemaVersion { get; init; }
    public Frame[]? frames { get; init; }
}
