using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Xunit;
using DecisionView = Mahjong.Contracts.Wire.DecisionView;
using GamePhase = Mahjong.Contracts.Wire.GamePhase;
using PlayerSnapshot = Mahjong.Contracts.Wire.PlayerSnapshot;
using PrivateCheatView = Mahjong.Contracts.Wire.PrivateCheatView;
using PublicSnapshot = Mahjong.Contracts.Wire.PublicSnapshot;

namespace Mahjong.LocalHost.Tests
{
    /// <summary>
    /// C04-D05 acceptance-matrix scenarios. Every test uses the real local room,
    /// production channel and public TableSession entry points; no test writes
    /// authority state or substitutes a scripted channel.
    /// </summary>
    public sealed class C04PrivacyStreamMatrixTests
    {
        private static readonly TimeSpan MatrixTimeout = TimeSpan.FromSeconds(45);

        [Fact]
        public void Released_control_raw_is_decoded_and_bound_to_each_real_welcome()
        {
            string? rawPath = Environment.GetEnvironmentVariable("C04_RELEASED_CONTROL_RAW_PATH");
            string? fixtureRoot = Environment.GetEnvironmentVariable("C04_RELEASED_CONTROL_FIXTURE_ROOT");
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                if (string.IsNullOrWhiteSpace(fixtureRoot)) fixtureRoot = CreateDefaultWireControlFixtureRoot();
                Directory.CreateDirectory(fixtureRoot!);
                WireControlCapture fixture = BuildWireControlFixture();
                string generatedRaw = Path.Combine(fixtureRoot!, "raw-frames.json");
                WriteWireControlFixture(fixtureRoot!, fixture);
                WireControlCapture foreign = new WireControlCapture { FixtureOnly = true, SchemaVersion = 2 };
                foreign.Frames.Add(BuildWireControlFrame(1, "foreign-owner-session", "fixture-room", "fixture-account-seat1", 1, "foreign-owner-scope", 2, 1));
                File.WriteAllText(Path.Combine(fixtureRoot!, "foreign-raw.json"), JsonSerializer.Serialize(foreign, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                ValidateReleasedControlRaw(generatedRaw, Path.Combine(fixtureRoot!, "reconnect.json"),
                    Path.Combine(fixtureRoot!, "ready.json"), Path.Combine(fixtureRoot!, "release.json"));
                AssertWireControlNegatives(fixtureRoot!, fixture);
                Console.WriteLine("C04_CONTROL_WIRE_CONTRACT_PASS frames=" + fixture.Frames.Count + " negative=13 source=WireCodec fixtureOnly=true");
                return;
            }

            string reconnectPath = RequiredEnvironmentPath("C04_RELEASED_CONTROL_RECONNECT_PATH");
            string readyPath = RequiredEnvironmentPath("C04_RELEASED_CONTROL_READY_PATH");
            string? releasePath = Environment.GetEnvironmentVariable("C04_RELEASED_CONTROL_RELEASE_PATH");
            ValidateReleasedControlRaw(rawPath, reconnectPath, readyPath, releasePath);
            string? negativeRoot = Environment.GetEnvironmentVariable("C04_RELEASED_CONTROL_NEGATIVE_ROOT");
            string? foreignRawPath = Environment.GetEnvironmentVariable("C04_RELEASED_CONTROL_FOREIGN_RAW_PATH");
            if (!string.IsNullOrWhiteSpace(negativeRoot))
            {
                AssertCapturedControlNegatives(rawPath, reconnectPath, readyPath, releasePath, negativeRoot!, foreignRawPath);
            }
            Console.WriteLine("C04_CONTROL_WIRE_CAPTURE_PASS frames=" + ReadWireControlCapture(rawPath).Frames.Count +
                " rawSha256=" + Sha256(File.ReadAllBytes(rawPath)) + " negative=" + (string.IsNullOrWhiteSpace(negativeRoot) ? 0 : (string.IsNullOrWhiteSpace(foreignRawPath) ? 10 : 11)) + " source=WireCodec");
        }

        private static string CreateDefaultWireControlFixtureRoot()
        {
            DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md")) && Directory.Exists(Path.Combine(directory.FullName, "dotnet")))
                {
                    string buildRoot = Path.Combine(directory.FullName, "build");
                    Directory.CreateDirectory(buildRoot);
                    return Path.Combine(buildRoot, "c04-wire-default-" + Guid.NewGuid().ToString("N"));
                }
                directory = directory.Parent;
            }
            throw new DirectoryNotFoundException("Could not locate the repository root for an exclusive C04 WireCodec fixture.");
        }

        private static string RequiredEnvironmentPath(string name)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value) || !File.Exists(value))
                throw new InvalidDataException(name + " must name an existing evidence file.");
            return value;
        }

        private static void ValidateReleasedControlRaw(string rawPath, string reconnectPath, string readyPath, string? releasePath)
        {
            WireControlCapture capture = ReadWireControlCapture(rawPath);
            using JsonDocument reconnectDoc = JsonDocument.Parse(File.ReadAllText(reconnectPath));
            using JsonDocument readyDoc = JsonDocument.Parse(File.ReadAllText(readyPath));
            JsonElement reconnect = reconnectDoc.RootElement;
            JsonElement ready = readyDoc.RootElement;
            string mainSession = String(reconnect, "newSessionId");
            string roomId = String(reconnect, "newRoomId");
            string oldScope = String(reconnect, "oldScopeId");
            string newScope = String(reconnect, "newScopeId");
            ulong oldBinding = Number(reconnect, "oldBindingEpoch");
            ulong resumedBinding = Number(reconnect, "newBindingEpoch");
            string readySession = String(ready, "newSessionId");
            string readyRoom = String(ready, "newRoomId");
            string readyScope = String(ready, "newScopeId");
            ulong readyBinding = Number(ready, "bindingEpoch");
            string expectedAccount = String(ready, "newWelcomeAccountId");
            ulong readyControlEpoch = Number(ready, "controlEpochAfter");
            if (!Boolean(ready, "controlHuman") || readyControlEpoch == 0 ||
                readyControlEpoch != Number(reconnect, "newSnapshotControlEpoch"))
                throw new InvalidDataException("Reconnect and Ready evidence disagree about the resumed human control epoch.");
            if (capture.SchemaVersion != 2 || capture.Frames.Count < 4 || mainSession != readySession || roomId != readyRoom ||
                oldBinding == 0 || resumedBinding <= oldBinding || readyBinding < resumedBinding ||
                oldScope == newScope || oldScope == readyScope ||
                (readyBinding == resumedBinding && newScope != readyScope) ||
                (readyBinding > resumedBinding && newScope == readyScope))
                throw new InvalidDataException("Released-control expected WELCOME timeline is incomplete or contradictory.");

            if (!string.IsNullOrWhiteSpace(releasePath) && File.Exists(releasePath))
            {
                using JsonDocument releaseDoc = JsonDocument.Parse(File.ReadAllText(releasePath));
                JsonElement release = releaseDoc.RootElement;
                string releaseSession = String(release, "releasedPlayerSessionId");
                string releaseRoom = String(release, "releasedPlayerRoomId");
                string releaseAccount = String(release, "releasedPlayerAccountId");
                if (string.IsNullOrWhiteSpace(releaseSession) || releaseSession == mainSession || releaseRoom != roomId || releaseAccount != expectedAccount)
                    throw new InvalidDataException("Released player identity does not bind the first control phase to the same room/account.");
            }

            var sessionOrder = new List<string>();
            var bindings = new Dictionary<string, (string Scope, string Account, Role Role, int Seat)>();
            bool enteredMain = false;
            string initialSession = string.Empty;
            ulong previousMainBinding = 0;
            for (int index = 0; index < capture.Frames.Count; index++)
            {
                WireControlFrame frame = capture.Frames[index];
                if (frame.Ordinal != index + 1 || frame.DecodeStatus != "Ok" || frame.UnknownFieldsZero != true ||
                    frame.WireSha256 != Sha256(Convert.FromBase64String(frame.RawBase64)) ||
                    frame.WelcomeWireSha256 != Sha256(Convert.FromBase64String(frame.WelcomeRawBase64)))
                    throw new InvalidDataException("Raw frame ordinal, asserted decoder state, or hash is invalid at index " + index + ".");

                byte[] wireBytes = Convert.FromBase64String(frame.RawBase64);
                WireDecodeResult wireResult = WireCodec.Decode(wireBytes);
                if (!wireResult.IsOk || wireResult.Envelope == null || wireResult.UnknownFieldCount != 0)
                    throw new InvalidDataException("WireCodec rejected player raw frame " + frame.Ordinal + ": " + wireResult.ErrorCode);
                Envelope wire = wireResult.Envelope;
                WireCodec.EnsureStrict(wire);
                PlayerSnapshot? snapshot = null;
                bool playerStreamBody = wire.BodyCase == Envelope.BodyOneofCase.SyncResponse &&
                    wire.SyncResponse.Target == SyncTarget.PlayerStream && wire.SyncResponse.BaselineCase == SyncResponse.BaselineOneofCase.Player;
                if (playerStreamBody) snapshot = wire.SyncResponse.Player;
                else if (wire.BodyCase == Envelope.BodyOneofCase.PlayerUpdate) snapshot = wire.PlayerUpdate.Snapshot;
                if (snapshot == null || (wire.MessageType != MessageType.SyncResponse && wire.MessageType != MessageType.PlayerUpdate) ||
                    (wire.BodyCase == Envelope.BodyOneofCase.SyncResponse && wire.MessageType != MessageType.SyncResponse) ||
                    (wire.BodyCase == Envelope.BodyOneofCase.PlayerUpdate && wire.MessageType != MessageType.PlayerUpdate) ||
                    wire.SessionId != frame.SessionId || wire.RoomId != frame.RoomId || wire.SeatId != frame.SeatId ||
                    wire.BindingEpoch != frame.BindingEpoch || (wire.Scope != null &&
                        (wire.Scope.ScopeId != frame.ScopeId || wire.Scope.AccountId != frame.AccountId ||
                         wire.Scope.Role != Role.Player || wire.Scope.RoomId != frame.RoomId)) ||
                    wire.MessageType.ToString() != frame.MessageType)
                    throw new InvalidDataException("Decoded player envelope identity/body conflicts at frame " + frame.Ordinal +
                        ": type=" + wire.MessageType + "/" + frame.MessageType + " body=" + wire.BodyCase + " target=" + wire.SyncResponse?.Target +
                        " baseline=" + wire.SyncResponse?.BaselineCase + " session=" + wire.SessionId + "/" + frame.SessionId +
                        " room=" + wire.RoomId + "/" + frame.RoomId + " seat=" + wire.SeatId + "/" + frame.SeatId +
                        " binding=" + wire.BindingEpoch + "/" + frame.BindingEpoch + " scope=" + wire.Scope?.ScopeId + "/" + frame.ScopeId);

                if (snapshot.PrivateView == null || snapshot.PrivateView.Seat != frame.OwnerSeat || frame.OwnerSeat != frame.SeatId ||
                    snapshot.SessionId != wire.SessionId || snapshot.SessionId != frame.SessionId ||
                    snapshot.StateVersion != frame.StateVersion ||
                    (wire.BodyCase == Envelope.BodyOneofCase.SyncResponse && wire.SyncResponse.StateVersion != frame.StateVersion) ||
                    snapshot.PrivateView.ClosedTiles.Count == 0 || snapshot.PrivateView.ClosedTiles.Count != frame.PrivateClosedTileCount ||
                    snapshot.PrivateView.Cheat == null || snapshot.PrivateView.Cheat.CheatPoints != frame.PrivateCheatPoints ||
                    snapshot.PrivateView.Cheat.LedgerEntries.Count != frame.PrivateLedgerEntryCount ||
                    (snapshot.PrivateView.Control?.ControlEpoch ?? 0UL) != frame.ControlEpoch ||
                    (snapshot.PrivateView.Control?.ControlVersion ?? 0UL) != frame.ControlVersion)
                    throw new InvalidDataException("Decoded player snapshot does not prove the recorded nonempty owner-private content at frame " + frame.Ordinal + ".");

                byte[] welcomeBytes = Convert.FromBase64String(frame.WelcomeRawBase64);
                WireDecodeResult welcomeResult = WireCodec.Decode(welcomeBytes);
                if (!welcomeResult.IsOk || welcomeResult.Envelope == null || welcomeResult.UnknownFieldCount != 0)
                    throw new InvalidDataException("WireCodec rejected WELCOME raw at frame " + frame.Ordinal + ": " + welcomeResult.ErrorCode);
                Envelope welcomeEnvelope = welcomeResult.Envelope;
                WireCodec.EnsureStrict(welcomeEnvelope);
                if (welcomeEnvelope.MessageType != MessageType.Welcome || welcomeEnvelope.BodyCase != Envelope.BodyOneofCase.Welcome)
                    throw new InvalidDataException("Typed WELCOME body is missing at frame " + frame.Ordinal + ".");
                Welcome welcome = welcomeEnvelope.Welcome;
                if (welcomeEnvelope.SessionId != welcome.SessionId || welcomeEnvelope.RoomId != welcome.RoomId || welcomeEnvelope.SeatId != welcome.SeatId ||
                    welcomeEnvelope.BindingEpoch != welcome.BindingEpoch || welcome.SessionId != frame.SessionId || welcome.RoomId != frame.RoomId || welcome.Role != Role.Player || welcome.SeatId != frame.SeatId ||
                    welcome.BindingEpoch != frame.BindingEpoch || welcome.Scope == null || welcome.Scope.ScopeId != frame.ScopeId ||
                    welcome.Scope.AccountId != frame.AccountId || welcome.Scope.Role != Role.Player || welcome.Scope.RoomId != frame.RoomId ||
                    (welcomeEnvelope.Scope != null && (welcomeEnvelope.Scope.ScopeId != welcome.Scope.ScopeId || welcomeEnvelope.Scope.AccountId != welcome.Scope.AccountId ||
                        welcomeEnvelope.Scope.Role != welcome.Scope.Role || welcomeEnvelope.Scope.RoomId != welcome.Scope.RoomId)) ||
                    frame.ConnectionRole != "Player" || frame.BoundSeat != welcome.SeatId ||
                    frame.Audience != (wire.MessageType == MessageType.PlayerUpdate ? "PlayerUpdate" : "Player") || frame.OwnerSeat != welcome.SeatId)
                    throw new InvalidDataException("Decoded WELCOME identity conflicts at frame " + frame.Ordinal + ": session=" + welcome.SessionId + "/" + frame.SessionId +
                        " room=" + welcome.RoomId + "/" + frame.RoomId + " role=" + welcome.Role + " seat=" + welcome.SeatId + "/" + frame.SeatId +
                        " binding=" + welcome.BindingEpoch + "/" + frame.BindingEpoch + " scope=" + welcome.Scope?.ScopeId + "/" + frame.ScopeId +
                        " account=" + welcome.Scope?.AccountId + "/" + frame.AccountId + " scopeRole=" + welcome.Scope?.Role + " scopeRoom=" + welcome.Scope?.RoomId +
                        " envelopeType=" + welcomeEnvelope.MessageType + " body=" + welcomeEnvelope.BodyCase + " connRole=" + frame.ConnectionRole +
                        " boundSeat=" + frame.BoundSeat + " audience=" + frame.Audience + " owner=" + frame.OwnerSeat);

                string identityKey = frame.SessionId + "/" + frame.BindingEpoch;
                var identity = (frame.ScopeId, frame.AccountId, welcome.Role, welcome.SeatId);
                if (bindings.TryGetValue(identityKey, out var existing) && existing != identity)
                    throw new InvalidDataException("A session/binding epoch maps to conflicting decoded WELCOME identities.");
                bindings[identityKey] = identity;

                if (sessionOrder.Count == 0 || sessionOrder[sessionOrder.Count - 1] != frame.SessionId)
                    sessionOrder.Add(frame.SessionId);
                if (frame.SessionId == mainSession) enteredMain = true;
                else if (enteredMain) throw new InvalidDataException("A foreign/old session appears after the active claim session began.");
                else if (string.IsNullOrEmpty(initialSession)) initialSession = frame.SessionId;
                else if (initialSession != frame.SessionId) throw new InvalidDataException("Unexpected extra session appears in the release phase.");

                if (frame.SessionId == mainSession)
                {
                    if (frame.RoomId != roomId || frame.AccountId != expectedAccount || frame.SeatId != 0 || frame.ConnectionRole != "Player")
                        throw new InvalidDataException("Claimed session left the expected room/account/player seat.");
                    string expectedScope = frame.BindingEpoch == oldBinding ? oldScope :
                        frame.BindingEpoch == resumedBinding ? newScope :
                        frame.BindingEpoch == readyBinding && readyBinding > resumedBinding ? readyScope : string.Empty;
                    if (string.IsNullOrEmpty(expectedScope) || frame.ScopeId != expectedScope || frame.BindingEpoch < previousMainBinding)
                        throw new InvalidDataException("Claimed session binding/scope timeline is missing, reordered, or foreign.");
                    if (frame.BindingEpoch >= resumedBinding &&
                        (snapshot.PrivateView.Control == null || snapshot.PrivateView.Control.ControlledByComputer ||
                         snapshot.PrivateView.Control.ControlEpoch != Number(reconnect, "newSnapshotControlEpoch")))
                        throw new InvalidDataException("Resumed/Ready player snapshot does not match the human control epoch recorded by formal recovery.");
                    previousMainBinding = frame.BindingEpoch;
                }
                else if (frame.RoomId != roomId || frame.AccountId != expectedAccount || frame.SeatId != 0 || frame.ConnectionRole != "Player")
                    throw new InvalidDataException("Initial release session is not the same real owner identity.");
            }

            if (sessionOrder.Count != 2 || sessionOrder[0] != initialSession || sessionOrder[1] != mainSession ||
                initialSession == mainSession || previousMainBinding != Math.Max(resumedBinding, readyBinding) ||
                !bindings.ContainsKey(mainSession + "/" + oldBinding) || !bindings.ContainsKey(mainSession + "/" + resumedBinding) ||
                (readyBinding > resumedBinding && !bindings.ContainsKey(mainSession + "/" + readyBinding)))
                throw new InvalidDataException("Complete release/claim/reconnect/Ready session timeline is not present in the decoded raw frames.");

            if (!string.IsNullOrWhiteSpace(releasePath) && File.Exists(releasePath))
            {
                using JsonDocument releaseDoc = JsonDocument.Parse(File.ReadAllText(releasePath));
                JsonElement release = releaseDoc.RootElement;
                if (String(release, "releasedPlayerSessionId") != initialSession)
                    throw new InvalidDataException("Decoded initial session does not match the release producer's captured player session.");
                if (Boolean(release, "controlHuman") != true || Boolean(release, "claimEpochAdvanced") != true ||
                    DecimalString(release, "claimEpoch") != Number(reconnect, "newSnapshotControlEpoch") ||
                    DecimalString(release, "claimEpoch") <= DecimalString(release, "claimEpochBefore"))
                    throw new InvalidDataException("Release/claim phase evidence does not establish the resumed human owner and epoch.");
            }
        }

        private static WireControlCapture ReadWireControlCapture(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Control raw capture is missing.", path);
            WireControlCapture? capture = JsonSerializer.Deserialize<WireControlCapture>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return capture ?? throw new InvalidDataException("Control raw capture JSON is empty.");
        }

        private static string String(JsonElement value, string name)
        {
            if (!value.TryGetProperty(name, out JsonElement property) || property.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Expected nonempty JSON string " + name + ".");
            string result = property.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(result)) throw new InvalidDataException("Expected nonempty JSON string " + name + ".");
            return result;
        }

        private static ulong Number(JsonElement value, string name)
        {
            if (!value.TryGetProperty(name, out JsonElement property) || !property.TryGetUInt64(out ulong result))
                throw new InvalidDataException("Expected unsigned JSON number " + name + ".");
            return result;
        }

        private static bool Boolean(JsonElement value, string name)
        {
            if (!value.TryGetProperty(name, out JsonElement property) ||
                (property.ValueKind != JsonValueKind.True && property.ValueKind != JsonValueKind.False))
                throw new InvalidDataException("Expected JSON boolean " + name + ".");
            return property.GetBoolean();
        }

        private static ulong DecimalString(JsonElement value, string name)
        {
            string text = String(value, name);
            if (!ulong.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ulong result))
                throw new InvalidDataException("Expected unsigned decimal JSON string " + name + ".");
            return result;
        }

        private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

        private static WireControlCapture BuildWireControlFixture()
        {
            var capture = new WireControlCapture { FixtureOnly = true, SchemaVersion = 2 };
            string room = "fixture-room";
            string account = "fixture-account";
            capture.Frames.Add(BuildWireControlFrame(1, "release-session", room, account, 1, "release-scope", 0));
            capture.Frames.Add(BuildWireControlFrame(2, "claim-session", room, account, 1, "old-claim-scope", 1, computerControlled: true));
            capture.Frames.Add(BuildWireControlFrame(3, "claim-session", room, account, 2, "resumed-scope", 2));
            capture.Frames.Add(BuildWireControlFrame(4, "claim-session", room, account, 2, "resumed-scope", 2));
            return capture;
        }

        private static WireControlFrame BuildWireControlFrame(int ordinal, string session, string room, string account, ulong binding, string scope, ulong controlEpoch, int seat = 0, bool computerControlled = false)
        {
            var requestScope = new Mahjong.Contracts.Wire.RequestScope { ScopeId = scope, AccountId = account, Role = Role.Player, RoomId = room, IssuedAtMs = 1, ExpiresAtMs = 10_000 };
            var welcome = new Envelope
            {
                ProtocolMajor = ProtocolConstants.Major, ProtocolMinor = ProtocolConstants.Minor,
                RulesetVersion = ProtocolConstants.RulesetVersion, ProjectionVersion = ProtocolConstants.ProjectionVersion,
                MessageType = MessageType.Welcome, SessionId = session, RoomId = room, SeatId = seat, BindingEpoch = binding,
                Welcome = new Welcome { SessionId = session, RoomId = room, Role = Role.Player, SeatId = seat, BindingEpoch = binding,
                    Scope = requestScope.Clone(), ProtocolMajor = ProtocolConstants.Major, ProtocolMinor = ProtocolConstants.Minor,
                    RulesetVersion = ProtocolConstants.RulesetVersion, ProjectionVersion = ProtocolConstants.ProjectionVersion }
            };
            var snapshot = new PlayerSnapshot { SessionId = session, StateVersion = (ulong)ordinal, RoundId = "fixture-round", PublicView = new Mahjong.Contracts.Wire.PublicMatchView(),
                PrivateView = new PlayerPrivateView { Seat = seat, Cheat = new PrivateCheatView { CheatPoints = 120 },
                    Control = new Mahjong.Contracts.Wire.PlayerControlView { ControlEpoch = controlEpoch, ControlVersion = controlEpoch, ControlledByComputer = computerControlled } } };
            for (uint tile = 0; tile < 13; tile++) snapshot.PrivateView.ClosedTiles.Add(tile);
            var player = new Envelope
            {
                ProtocolMajor = ProtocolConstants.Major, ProtocolMinor = ProtocolConstants.Minor,
                RulesetVersion = ProtocolConstants.RulesetVersion, ProjectionVersion = ProtocolConstants.ProjectionVersion,
                MessageType = MessageType.SyncResponse, SessionId = session, RoomId = room, SeatId = seat, BindingEpoch = binding,
                Scope = requestScope.Clone(), SyncResponse = new SyncResponse { Target = SyncTarget.PlayerStream, IsFullSnapshot = true,
                    Player = snapshot, StateVersion = (ulong)ordinal, EventSeq = (ulong)ordinal }
            };
            byte[] raw = WireCodec.Encode(player);
            byte[] welcomeRaw = WireCodec.Encode(welcome);
            return new WireControlFrame { Ordinal = ordinal, RawBase64 = Convert.ToBase64String(raw), WireSha256 = Sha256(raw),
                WelcomeRawBase64 = Convert.ToBase64String(welcomeRaw), WelcomeWireSha256 = Sha256(welcomeRaw), DecodeStatus = "Ok",
                MessageType = "SyncResponse", SessionId = session, RoomId = room, ScopeId = scope, AccountId = account,
                ConnectionRole = "Player", Audience = "Player", BoundSeat = seat, SeatId = seat, OwnerSeat = seat, BindingEpoch = binding,
                PrivateClosedTileCount = 13, PrivateCheatPoints = 120, PrivateLedgerEntryCount = 0, ControlEpoch = controlEpoch,
                ControlVersion = controlEpoch, StateVersion = (ulong)ordinal, UnknownFieldsZero = true };
        }

        private static void WriteWireControlFixture(string root, WireControlCapture capture)
        {
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false };
            File.WriteAllText(Path.Combine(root, "raw-frames.json"), JsonSerializer.Serialize(capture, options));
            File.WriteAllText(Path.Combine(root, "reconnect.json"), JsonSerializer.Serialize(new { schemaVersion = 1, newSessionId = "claim-session", newRoomId = "fixture-room", oldScopeId = "old-claim-scope", newScopeId = "resumed-scope", oldBindingEpoch = 1UL, newBindingEpoch = 2UL, newSnapshotControlEpoch = 2UL }, options));
            File.WriteAllText(Path.Combine(root, "ready.json"), JsonSerializer.Serialize(new { schemaVersion = 1, newSessionId = "claim-session", newRoomId = "fixture-room", newScopeId = "resumed-scope", bindingEpoch = 2UL, newWelcomeAccountId = "fixture-account", controlEpochAfter = 2UL, controlHuman = true }, options));
            File.WriteAllText(Path.Combine(root, "release.json"), JsonSerializer.Serialize(new { schemaVersion = 1, releasedPlayerSessionId = "release-session", releasedPlayerRoomId = "fixture-room", releasedPlayerAccountId = "fixture-account", claimEpochBefore = "1", claimEpoch = "2", claimEpochAdvanced = true, controlHuman = true }, options));
        }

        private static void AssertWireControlNegatives(string root, WireControlCapture valid)
        {
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            string reconnect = Path.Combine(root, "reconnect.json"), ready = Path.Combine(root, "ready.json"), release = Path.Combine(root, "release.json");
            void Reject(string name, Action<WireControlCapture> mutate)
            {
                WireControlCapture copy = JsonSerializer.Deserialize<WireControlCapture>(JsonSerializer.Serialize(valid, options), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                mutate(copy);
                string badPath = Path.Combine(root, name + ".json");
                File.WriteAllText(badPath, JsonSerializer.Serialize(copy, options));
                Assert.ThrowsAny<Exception>(() => ValidateReleasedControlRaw(badPath, reconnect, ready, release));
            }
            Reject("bad-wire", copy => { byte[] bad = System.Text.Encoding.ASCII.GetBytes("NOT A PROTOBUF ENVELOPE"); copy.Frames[0].RawBase64 = Convert.ToBase64String(bad); copy.Frames[0].WireSha256 = Sha256(bad); });
            Reject("bad-welcome", copy => { byte[] bad = System.Text.Encoding.ASCII.GetBytes("NOT A PROTOBUF WELCOME"); copy.Frames[0].WelcomeRawBase64 = Convert.ToBase64String(bad); copy.Frames[0].WelcomeWireSha256 = Sha256(bad); });
            Reject("foreign-owner-wire", copy => { WireControlFrame foreign = BuildWireControlFrame(1, "release-session", "fixture-room", "fixture-owner-seat1", 1, "release-scope", 0, 1); copy.Frames[0].RawBase64 = foreign.RawBase64; copy.Frames[0].WireSha256 = foreign.WireSha256; });
            Reject("scope-metadata-conflict", copy => copy.Frames[0].ScopeId = "foreign-scope");
            Reject("unknown-session", copy => { WireControlFrame f = BuildWireControlFrame(1, "foreign-session", "fixture-room", "fixture-owner-seat0", 1, "release-scope", 0); copy.Frames[0].RawBase64 = f.RawBase64; copy.Frames[0].WireSha256 = f.WireSha256; copy.Frames[0].WelcomeRawBase64 = f.WelcomeRawBase64; copy.Frames[0].WelcomeWireSha256 = f.WelcomeWireSha256; copy.Frames[0].SessionId = "foreign-session"; });
            Reject("missing-recovery-phase", copy => { copy.Frames.RemoveAt(2); for (int i = 0; i < copy.Frames.Count; i++) copy.Frames[i].Ordinal = i + 1; });
            AddTypedWireNegatives(Reject, "claim-session", 2);
        }

        private static void AddTypedWireNegatives(Action<string, Action<WireControlCapture>> reject, string resumedSession, ulong resumedBinding)
        {
            reject("inner-snapshot-session", copy =>
            {
                WireControlFrame frame = copy.Frames[0];
                Envelope envelope = DecodeRawEnvelope(frame.RawBase64);
                GetPlayerSnapshot(envelope).SessionId = "wrong-inner-snapshot-session";
                ReplacePlayerEnvelope(frame, envelope);
            });
            reject("outer-welcome-session", copy =>
            {
                WireControlFrame frame = copy.Frames[0];
                Envelope envelope = DecodeRawEnvelope(frame.WelcomeRawBase64);
                envelope.SessionId = "wrong-welcome-envelope-session";
                ReplaceWelcomeEnvelope(frame, envelope);
            });
            reject("wire-scope-account", copy => MutateScope(copy.Frames[0], scope => scope.AccountId = "wrong-wire-scope-account"));
            reject("wire-scope-role", copy => MutateScope(copy.Frames[0], scope => scope.Role = Role.Spectator));
            reject("wire-scope-room", copy => MutateScope(copy.Frames[0], scope => scope.RoomId = "wrong-wire-scope-room"));
            reject("resumed-control-owner", copy =>
            {
                WireControlFrame frame = copy.Frames.FirstOrDefault(item => item.SessionId == resumedSession && item.BindingEpoch == resumedBinding)
                    ?? throw new InvalidDataException("No resumed binding frame is available for the control-owner mutation.");
                Envelope envelope = DecodeRawEnvelope(frame.RawBase64);
                Mahjong.Contracts.Wire.PlayerControlView control = GetPlayerSnapshot(envelope).PrivateView.Control
                    ?? throw new InvalidDataException("Resumed frame has no player control view.");
                control.ControlledByComputer = true;
                ReplacePlayerEnvelope(frame, envelope);
            });
            reject("recorded-state-version", copy =>
            {
                WireControlFrame frame = copy.Frames[0];
                Envelope envelope = DecodeRawEnvelope(frame.RawBase64);
                PlayerSnapshot snapshot = GetPlayerSnapshot(envelope);
                snapshot.StateVersion += 100;
                if (envelope.BodyCase == Envelope.BodyOneofCase.SyncResponse)
                    envelope.SyncResponse.StateVersion = snapshot.StateVersion;
                ReplacePlayerEnvelope(frame, envelope);
            });
        }

        private static void MutateScope(WireControlFrame frame, Action<Mahjong.Contracts.Wire.RequestScope> mutate)
        {
            Envelope envelope = DecodeRawEnvelope(frame.RawBase64);
            if (envelope.Scope == null)
            {
                Envelope welcome = DecodeRawEnvelope(frame.WelcomeRawBase64);
                envelope.Scope = (welcome.Welcome?.Scope)
                    ?? throw new InvalidDataException("WELCOME frame has no scope to use for the scope mutation.");
                envelope.Scope = envelope.Scope.Clone();
            }
            mutate(envelope.Scope);
            ReplacePlayerEnvelope(frame, envelope);
        }

        private static Envelope DecodeRawEnvelope(string base64)
        {
            WireDecodeResult decoded = WireCodec.Decode(Convert.FromBase64String(base64));
            if (!decoded.IsOk || decoded.Envelope == null) throw new InvalidDataException("Cannot mutate an invalid WireCodec envelope.");
            return decoded.Envelope;
        }

        private static PlayerSnapshot GetPlayerSnapshot(Envelope envelope)
        {
            if (envelope.BodyCase == Envelope.BodyOneofCase.SyncResponse && envelope.SyncResponse.BaselineCase == SyncResponse.BaselineOneofCase.Player)
                return envelope.SyncResponse.Player;
            if (envelope.BodyCase == Envelope.BodyOneofCase.PlayerUpdate) return envelope.PlayerUpdate.Snapshot;
            throw new InvalidDataException("Player raw frame does not contain a player snapshot.");
        }

        private static void ReplacePlayerEnvelope(WireControlFrame frame, Envelope envelope)
        {
            byte[] bytes = WireCodec.Encode(envelope);
            frame.RawBase64 = Convert.ToBase64String(bytes);
            frame.WireSha256 = Sha256(bytes);
        }

        private static void ReplaceWelcomeEnvelope(WireControlFrame frame, Envelope envelope)
        {
            byte[] bytes = WireCodec.Encode(envelope);
            frame.WelcomeRawBase64 = Convert.ToBase64String(bytes);
            frame.WelcomeWireSha256 = Sha256(bytes);
        }

        private static void AssertCapturedControlNegatives(string rawPath, string reconnectPath, string readyPath, string? releasePath, string negativeRoot, string? foreignRawPath)
        {
            Directory.CreateDirectory(negativeRoot);
            WireControlCapture valid = ReadWireControlCapture(rawPath);
            void Reject(string name, Action<WireControlCapture> mutate)
            {
                WireControlCapture copy = JsonSerializer.Deserialize<WireControlCapture>(JsonSerializer.Serialize(valid), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                mutate(copy);
                string path = Path.Combine(negativeRoot, name + ".json");
                File.WriteAllText(path, JsonSerializer.Serialize(copy, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                Assert.ThrowsAny<Exception>(() => ValidateReleasedControlRaw(path, reconnectPath, readyPath, releasePath));
            }

            Reject("bad-wire-rehashed", copy =>
            {
                byte[] bad = System.Text.Encoding.ASCII.GetBytes("NOT A PROTOBUF ENVELOPE");
                copy.Frames[0].RawBase64 = Convert.ToBase64String(bad);
                copy.Frames[0].WireSha256 = Sha256(bad);
            });
            Reject("bad-welcome-rehashed", copy =>
            {
                byte[] bad = System.Text.Encoding.ASCII.GetBytes("NOT A PROTOBUF WELCOME");
                copy.Frames[0].WelcomeRawBase64 = Convert.ToBase64String(bad);
                copy.Frames[0].WelcomeWireSha256 = Sha256(bad);
            });
            Reject("typed-owner-metadata-conflict", copy => copy.Frames[0].OwnerSeat = 1);
            if (!string.IsNullOrWhiteSpace(foreignRawPath))
            {
                WireControlCapture foreign = ReadWireControlCapture(foreignRawPath);
                WireControlFrame frame = foreign.Frames.FirstOrDefault(f => f.OwnerSeat == 1 && f.SeatId == 1)
                    ?? throw new InvalidDataException("Foreign raw capture has no actual seat-1 owner frame.");
                Reject("real-foreign-owner-wire", copy =>
                {
                    copy.Frames[0].RawBase64 = frame.RawBase64;
                    copy.Frames[0].WireSha256 = frame.WireSha256;
                });
            }
            using JsonDocument reconnectDoc = JsonDocument.Parse(File.ReadAllText(reconnectPath));
            AddTypedWireNegatives(Reject, String(reconnectDoc.RootElement, "newSessionId"), Number(reconnectDoc.RootElement, "newBindingEpoch"));
        }

        private sealed class WireControlCapture
        {
            public int SchemaVersion { get; set; }
            public bool FixtureOnly { get; set; }
            public List<WireControlFrame> Frames { get; set; } = new List<WireControlFrame>();
        }

        private sealed class WireControlFrame
        {
            public int Ordinal { get; set; }
            public string WireSha256 { get; set; } = string.Empty;
            public string RawBase64 { get; set; } = string.Empty;
            public string WelcomeWireSha256 { get; set; } = string.Empty;
            public string WelcomeRawBase64 { get; set; } = string.Empty;
            public string DecodeStatus { get; set; } = string.Empty;
            public string MessageType { get; set; } = string.Empty;
            public string SessionId { get; set; } = string.Empty;
            public string RoomId { get; set; } = string.Empty;
            public string ScopeId { get; set; } = string.Empty;
            public string AccountId { get; set; } = string.Empty;
            public string ConnectionRole { get; set; } = string.Empty;
            public string Audience { get; set; } = string.Empty;
            public int BoundSeat { get; set; }
            public int SeatId { get; set; }
            public int OwnerSeat { get; set; }
            public ulong BindingEpoch { get; set; }
            public int PrivateClosedTileCount { get; set; }
            public long PrivateCheatPoints { get; set; }
            public int PrivateLedgerEntryCount { get; set; }
            public ulong ControlEpoch { get; set; }
            public ulong ControlVersion { get; set; }
            public ulong StateVersion { get; set; }
            public bool UnknownFieldsZero { get; set; }
        }

        [Fact]
        public async Task Public_and_player_snapshots_are_typed_and_seat_owned_on_real_channels()
        {
            await using var harness = new P41Harness();
            harness.Options.SeatPlan = LocalTableSeatPlan.AllFourHumanSeats;
            await using LocalTableRoom room = await LocalTableHost.OpenRoomAsync(harness.Options, TestContext.Current.CancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(MatrixTimeout);
            var tickets = new LocalTableConnectionTicket[4];
            var channels = new ITableChannel[4];
            var sessions = new TableSession[4];
            try
            {
                for (int seat = 0; seat < 4; seat++)
                {
                    tickets[seat] = room.CreatePlayerTicket(seat);
                    channels[seat] = await room.ConnectAsync(tickets[seat], timeout.Token);
                    sessions[seat] = new TableSession(channels[seat], new InMemoryOutboxStore());
                    await sessions[seat].OpenAsync(new TableSessionOpenRequest(tickets[seat].Value, "privacy-seat-" + seat), timeout.Token);
                }
                foreach (TableSession session in sessions)
                {
                    for (int i = 0; i < 16 && session.State != TableConnectionState.Ready; i++)
                        await session.PumpOnceAsync(timeout.Token);
                    Assert.Equal(TableConnectionState.Ready, session.State);
                }
                for (int owner = 0; owner < 4; owner++)
                {
                    var snapshot = Assert.IsType<PlayerSnapshot>(sessions[owner].PlayerSnapshot);
                    Assert.Equal(owner, snapshot.PrivateView.Seat);
                    Assert.Equal(owner, sessions[owner].Binding!.SeatId);
                    Assert.NotEmpty(snapshot.PrivateView.ClosedTiles);
                    Assert.InRange(snapshot.PrivateView.ClosedTiles.Count + (snapshot.PrivateView.HasDrawnTileId ? 1 : 0), 13, 14);
                    for (int foreign = 0; foreign < 4; foreign++)
                        if (foreign != owner) Assert.NotEqual(foreign, snapshot.PrivateView.Seat);
                }
                LocalTableConnectionTicket spectatorTicket = room.CreateSpectatorTicket();
                ITableChannel spectatorChannel = await room.ConnectAsync(spectatorTicket, timeout.Token);
                var spectator = new TableSession(spectatorChannel, new InMemoryOutboxStore());
                await spectator.OpenAsync(new TableSessionOpenRequest(spectatorTicket.Value, "privacy-spectator"), timeout.Token);
                for (int i = 0; i < 16 && spectator.State != TableConnectionState.Ready; i++)
                    await spectator.PumpOnceAsync(timeout.Token);
                Assert.Equal(TableConnectionState.Ready, spectator.State);
                Assert.Null(spectator.PlayerSnapshot);
                var publicSnapshot = Assert.IsType<PublicSnapshot>(spectator.PublicSnapshot);
                Assert.NotEmpty(publicSnapshot.RoundId);
                Assert.NotNull(publicSnapshot.View);
                await spectator.CloseAsync("TEST_DONE", CancellationToken.None);
                await spectatorChannel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
            }
            finally
            {
                for (int i = 0; i < sessions.Length; i++)
                {
                    if (sessions[i] != null) try { await sessions[i].CloseAsync("TEST_DONE", CancellationToken.None); } catch { }
                    if (channels[i] != null) try { await channels[i].CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None); } catch { }
                }
            }
        }

        [Fact]
        public async Task Legal_public_commands_produce_non_empty_owner_sleeve_and_fee_ledger()
        {
            await using var harness = new P41Harness();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(MatrixTimeout);
            (ITableChannel channel, TableSession session) = await OpenLegacySessionAsync(harness, timeout.Token);
            using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            Task pump = PumpAsync(session, pumpCts.Token);
            try
            {
                LocalPrivacyScenarioFacts facts = await LocalPrivacyScenario.CaptureSleeveAndFeeAsync(session, timeout.Token);
                Assert.False(string.IsNullOrWhiteSpace(facts.RoundId));
                Assert.True(facts.BaselineStateVersion < facts.TileMarkStateVersion);
                Assert.True(facts.TileMarkStateVersion < facts.SleeveStateVersion);
                Assert.True(facts.BaselineLedgerEntryCount >= 0);
                Assert.True(facts.TileMarkLedgerEntryCount > facts.BaselineLedgerEntryCount);
                Assert.True(facts.SleeveLedgerEntryCount > facts.TileMarkLedgerEntryCount);
                Assert.True(facts.TileMarkCheatPoints < facts.BaselineCheatPoints);
                Assert.True(facts.SleeveCheatPoints < facts.TileMarkCheatPoints);
                Assert.Equal(4, facts.MarkedTiles.Count);
                Assert.True(facts.MarkedTileCount >= 4);
                Assert.False(string.IsNullOrWhiteSpace(facts.SleeveOfferId));
                Assert.NotEmpty(facts.SleeveOfferTiles);
                Assert.True(facts.SleeveOfferTiles.Count >= 2);
            }
            finally
            {
                pumpCts.Cancel();
                try { await pump; } catch { }
                await session.CloseAsync("TEST_DONE", CancellationToken.None);
                await channel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
            }
        }

        [Fact]
        public async Task Legal_public_commands_produce_non_empty_facts_for_each_human_owner_seat()
        {
            for (int targetSeat = 0; targetSeat < 4; targetSeat++)
            {
                await using var harness = new P41Harness();
                harness.Options.SeatPlan = LocalTableSeatPlan.AllFourHumanSeats;
                await using LocalTableRoom room = await LocalTableHost.OpenRoomAsync(harness.Options, TestContext.Current.CancellationToken);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(75));
                var channels = new ITableChannel[4];
                var sessions = new TableSession[4];
                using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                using var driverCts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                var pumps = new List<Task>();
                Task? driver = null;
                try
                {
                    for (int seat = 0; seat < 4; seat++)
                    {
                        LocalTableConnectionTicket ticket = room.CreatePlayerTicket(seat);
                        channels[seat] = await room.ConnectAsync(ticket, timeout.Token);
                        sessions[seat] = new TableSession(channels[seat], new InMemoryOutboxStore());
                        await sessions[seat].OpenAsync(new TableSessionOpenRequest(ticket.Value, "owner-seat-" + seat), timeout.Token);
                        pumps.Add(PumpAsync(sessions[seat], pumpCts.Token));
                    }
                    foreach (TableSession session in sessions) await PumpUntilReadyAsync(session, timeout.Token);
                    driver = DriveOtherSeatsAsync(sessions, targetSeat, driverCts.Token);
                    LocalPrivacyScenarioFacts facts = await LocalPrivacyScenario.CaptureSleeveAndFeeAsync(
                        sessions[targetSeat],
                        timeout.Token,
                        submitChoice: false,
                        seatId: targetSeat);
                    driverCts.Cancel();
                    try { await driver; } catch (OperationCanceledException) { }
                    Assert.Equal(targetSeat, sessions[targetSeat].PlayerSnapshot!.PrivateView.Seat);
                    Assert.True(facts.TileMarkLedgerEntryCount > facts.BaselineLedgerEntryCount);
                    Assert.True(facts.SleeveLedgerEntryCount > facts.TileMarkLedgerEntryCount);
                    Assert.NotEmpty(facts.SleeveOfferTiles);
                    Assert.False(string.IsNullOrWhiteSpace(facts.SleeveOfferId));
                }
                finally
                {
                    driverCts.Cancel();
                    if (driver != null) { try { await driver; } catch { } }
                    pumpCts.Cancel();
                    try { await Task.WhenAll(pumps); } catch { }
                    for (int seat = 0; seat < 4; seat++)
                    {
                        if (sessions[seat] != null) try { await sessions[seat].CloseAsync("TEST_DONE", CancellationToken.None); } catch { }
                        if (channels[seat] != null) try { await channels[seat].CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None); } catch { }
                    }
                }
            }
        }

        [Fact]
        public async Task Real_room_four_owners_and_spectator_observe_same_version_non_empty_private_target_facts()
        {
            await using var harness = new P41Harness();
            harness.Options.SeatPlan = LocalTableSeatPlan.AllFourHumanSeats;
            await using LocalTableRoom room = await LocalTableHost.OpenRoomAsync(harness.Options, TestContext.Current.CancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var channels = new ITableChannel[4];
            var sessions = new TableSession[4];
            ITableChannel? spectatorChannel = null;
            TableSession? spectator = null;
            using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            using var driverCts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            var pumps = new List<Task>();
            Task? driver = null;
            try
            {
                for (int seat = 0; seat < 4; seat++)
                {
                    LocalTableConnectionTicket ticket = room.CreatePlayerTicket(seat);
                    channels[seat] = await room.ConnectAsync(ticket, timeout.Token);
                    sessions[seat] = new TableSession(channels[seat], new InMemoryOutboxStore());
                    await sessions[seat].OpenAsync(new TableSessionOpenRequest(ticket.Value, "room-owner-" + seat), timeout.Token);
                    pumps.Add(PumpAsync(sessions[seat], pumpCts.Token));
                }
                foreach (TableSession session in sessions) await PumpUntilReadyAsync(session, timeout.Token);

                LocalTableConnectionTicket spectatorTicket = room.CreateSpectatorTicket();
                spectatorChannel = await room.ConnectAsync(spectatorTicket, timeout.Token);
                spectator = new TableSession(spectatorChannel, new InMemoryOutboxStore());
                await spectator.OpenAsync(new TableSessionOpenRequest(spectatorTicket.Value, "room-owner-spectator"), timeout.Token);
                pumps.Add(PumpAsync(spectator, pumpCts.Token));
                await PumpUntilReadyAsync(spectator, timeout.Token);

                driver = DriveOtherSeatsAsync(sessions, 0, driverCts.Token);
                LocalPrivacyScenarioFacts facts = await LocalPrivacyScenario.CaptureSleeveAndFeeAsync(sessions[0], timeout.Token, submitChoice: false);
                driverCts.Cancel();
                try { await driver; } catch (OperationCanceledException) { }

                ulong targetVersion = facts.SleeveStateVersion;
                foreach (TableSession session in sessions)
                    targetVersion = Math.Max(targetVersion, session.PlayerSnapshot?.StateVersion ?? 0UL);
                targetVersion = Math.Max(targetVersion, spectator.PublicSnapshot?.StateVersion ?? 0UL);
                for (int i = 0; i < 300; i++)
                {
                    bool allAtVersion = true;
                    foreach (TableSession session in sessions)
                        allAtVersion &= (session.PlayerSnapshot?.StateVersion ?? 0UL) == targetVersion;
                    allAtVersion &= (spectator.PublicSnapshot?.StateVersion ?? 0UL) == targetVersion;
                    if (allAtVersion) break;
                    await Task.Delay(20, timeout.Token);
                }

                PlayerSnapshot[] snapshots = new PlayerSnapshot[4];
                for (int seat = 0; seat < 4; seat++)
                {
                    snapshots[seat] = Assert.IsType<PlayerSnapshot>(sessions[seat].PlayerSnapshot);
                    Assert.Equal(seat, snapshots[seat].PrivateView.Seat);
                    Assert.NotEmpty(snapshots[seat].PrivateView.ClosedTiles);
                    Assert.Equal(targetVersion, snapshots[seat].StateVersion);
                    Assert.NotNull(sessions[seat].Binding);
                    Assert.Equal(seat, sessions[seat].Binding!.SeatId);
                }
                PublicSnapshot publicSnapshot = Assert.IsType<PublicSnapshot>(spectator.PublicSnapshot);
                Assert.Null(spectator.PlayerSnapshot);
                Assert.Equal(targetVersion, publicSnapshot.StateVersion);
                Assert.NotNull(publicSnapshot.View);

                PrivateCheatView targetCheat = Assert.IsType<PrivateCheatView>(snapshots[0].PrivateView.Cheat);
                Assert.NotNull(targetCheat.SleeveBloom);
                Assert.False(string.IsNullOrWhiteSpace(targetCheat.SleeveBloom.OfferId));
                Assert.NotEmpty(targetCheat.LedgerEntries);
                Assert.True(targetCheat.CheatPoints < 120);
                string targetOffer = targetCheat.SleeveBloom.OfferId;
                for (int seat = 1; seat < 4; seat++)
                {
                    PrivateCheatView foreign = Assert.IsType<PrivateCheatView>(snapshots[seat].PrivateView.Cheat);
                    if (foreign.SleeveBloom != null)
                        Assert.NotEqual(targetOffer, foreign.SleeveBloom.OfferId);
                }
            }
            finally
            {
                pumpCts.Cancel();
                if (driver != null) { try { await driver; } catch { } }
                try { await Task.WhenAll(pumps); } catch { }
                if (spectator != null) try { await spectator.CloseAsync("TEST_DONE", CancellationToken.None); } catch { }
                if (spectatorChannel != null) try { await spectatorChannel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None); } catch { }
                for (int seat = 0; seat < 4; seat++)
                {
                    if (sessions[seat] != null) try { await sessions[seat].CloseAsync("TEST_DONE", CancellationToken.None); } catch { }
                    if (channels[seat] != null) try { await channels[seat].CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None); } catch { }
                }
            }
        }

        [Fact]
        public async Task Slow_real_channel_queue_saturates_without_stopping_room_or_other_bindings()
        {
            await using var harness = new P41Harness();
            await using LocalTableRoom room = await LocalTableHost.OpenRoomAsync(harness.Options, TestContext.Current.CancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(MatrixTimeout);

            LocalTableConnectionTicket playerTicket = room.CreatePlayerTicket(0);
            ITableChannel playerChannel = await room.ConnectAsync(playerTicket, timeout.Token);
            var player = new TableSession(playerChannel, new InMemoryOutboxStore());
            LocalTableConnectionTicket slowTicket = room.CreateSpectatorTicket();
            ITableChannel slowChannel = await room.ConnectAsync(slowTicket, timeout.Token);
            var slow = new TableSession(slowChannel, new InMemoryOutboxStore());
            using var playerPumpCts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            Task? playerPump = null;
            try
            {
                await player.OpenAsync(new TableSessionOpenRequest(playerTicket.Value, "slow-player"), timeout.Token);
                await PumpUntilReadyAsync(player, timeout.Token);
                playerPump = PumpAsync(player, playerPumpCts.Token);

                await slow.OpenAsync(new TableSessionOpenRequest(slowTicket.Value, "slow-spectator"), timeout.Token);
                await PumpUntilReadyAsync(slow, timeout.Token);
                Assert.Equal(ProtocolRole.Spectator, slow.Binding!.Role);
                Assert.Null(slow.PlayerSnapshot);
                Assert.NotNull(slow.PublicSnapshot);
                string slowSessionId = slow.Binding.SessionId.Value;
                ulong slowBindingEpoch = slow.Binding.BindingEpoch.Value;

                int accepted = 0;
                try
                {
                    for (int i = 0; i < 600; i++)
                    {
                        await slowChannel.SendAsync(
                            WireCodec.Encode(HeartbeatEnvelope(slowSessionId, slowBindingEpoch, harness.Options.RoomId, i)),
                            timeout.Token);
                        accepted++;
                    }
                }
                catch (TableChannelClosedException)
                {
                    // The bounded queue closed the slow consumer; that is the expected
                    // saturation path and must not propagate to another binding.
                }

                Assert.True(accepted >= 257, "The slow channel never reached the production queue bound.");
                await Assert.ThrowsAnyAsync<TableChannelClosedException>(
                    async () => await slowChannel.ReceiveAsync(timeout.Token));

                Assert.Equal(TableConnectionState.Ready, player.State);
                Assert.NotNull(player.PlayerSnapshot);

                PlayerSnapshot beforeLiveRequest = player.PlayerSnapshot!;
                BusinessPayload releasePayload = CommandPayloadFactory.ReleaseControl(
                    beforeLiveRequest.RoundId,
                    beforeLiveRequest.PrivateView.Control?.ControlEpoch ?? 0UL);
                var liveRequest = new RequestId("slow-player-live-request");
                var liveResolved = new TaskCompletionSource<TableReceiptResolvedEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler<TableReceiptResolvedEventArgs> liveHandler = (_, args) =>
                {
                    if (args.Command.Key.RequestId.Value == liveRequest.Value) liveResolved.TrySetResult(args);
                };
                player.ReceiptResolved += liveHandler;
                try
                {
                    var liveCommand = new ProtocolCommand(
                        CommandKind.ReleaseControl,
                        releasePayload,
                        liveRequest,
                        player.Binding!.Scope.ScopeId,
                        new StateVersion(beforeLiveRequest.StateVersion),
                        new RoundId(beforeLiveRequest.RoundId),
                        decisionId: null,
                        controlEpoch: new ControlEpoch(beforeLiveRequest.PrivateView.Control?.ControlEpoch ?? 0UL));
                    CommandSubmission liveSubmission = await player.SubmitAsync(liveCommand, timeout.Token);
                    Assert.True(liveSubmission.Sent);
                    for (int i = 0; i < 500 && !liveResolved.Task.IsCompleted; i++)
                        await Task.Delay(10, timeout.Token);
                    Assert.True(liveResolved.Task.IsCompleted, "The original player request did not resolve.");
                    TableReceiptResolvedEventArgs liveReceipt = await liveResolved.Task;
                    Assert.True(
                        liveReceipt.Resolution.ReceiptStatus == Mahjong.Contracts.ReceiptStatus.Recorded ||
                        liveReceipt.Resolution.ReceiptStatus == Mahjong.Contracts.ReceiptStatus.Applied);
                    Assert.Equal(TableConnectionState.Ready, player.State);
                }
                finally
                {
                    player.ReceiptResolved -= liveHandler;
                }

                LocalTableConnectionTicket replacementTicket = room.CreateSpectatorTicket();
                ITableChannel replacementChannel = await room.ConnectAsync(replacementTicket, timeout.Token);
                var replacement = new TableSession(replacementChannel, new InMemoryOutboxStore());
                try
                {
                    await replacement.OpenAsync(new TableSessionOpenRequest(replacementTicket.Value, "slow-replacement"), timeout.Token);
                    await PumpUntilReadyAsync(replacement, timeout.Token);
                    Assert.NotNull(replacement.PublicSnapshot);
                }
                finally
                {
                    await replacement.CloseAsync("TEST_DONE", CancellationToken.None);
                    await replacementChannel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None);
                }
            }
            finally
            {
                playerPumpCts.Cancel();
                if (playerPump != null) { try { await playerPump; } catch { } }
                try { await player.CloseAsync("TEST_DONE", CancellationToken.None); } catch { }
                try { await playerChannel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None); } catch { }
                try { await slow.CloseAsync("TEST_DONE", CancellationToken.None); } catch { }
                try { await slowChannel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None); } catch { }
            }
        }

        [Fact]
        public async Task Delayed_real_private_wire_from_old_seat_binding_is_ignored_by_new_session()
        {
            await using var harness = new P41Harness();
            harness.Options.SeatPlan = LocalTableSeatPlan.AllFourHumanSeats;
            await using LocalTableRoom room = await LocalTableHost.OpenRoomAsync(harness.Options, TestContext.Current.CancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(MatrixTimeout);

            LocalTableConnectionTicket seatZeroTicket = room.CreatePlayerTicket(0);
            ITableChannel seatZeroInner = await room.ConnectAsync(seatZeroTicket, timeout.Token);
            var seatZeroChannel = new CaptureChannel(seatZeroInner);
            var seatZero = new TableSession(seatZeroChannel, new InMemoryOutboxStore());
            LocalTableConnectionTicket seatOneTicket;
            ITableChannel seatOneInner;
            var seatOneChannel = default(CaptureChannel);
            TableSession? seatOne = null;
            try
            {
                await seatZero.OpenAsync(new TableSessionOpenRequest(seatZeroTicket.Value, "delayed-v0"), timeout.Token);
                await PumpUntilReadyAsync(seatZero, timeout.Token);
                var oldPrivate = Assert.IsType<PlayerSnapshot>(seatZero.PlayerSnapshot);
                byte[] delayedFrame = seatZeroChannel.CapturedPlayerSnapshotFrame
                    ?? throw new InvalidOperationException("No real serialized player snapshot was captured.");
                WireDecodeResult decodedOld = WireCodec.Decode(delayedFrame);
                Assert.True(decodedOld.IsOk);
                Assert.Equal(0, decodedOld.Envelope!.SeatId);
                Assert.Equal(seatZero.Binding!.SessionId.Value, decodedOld.Envelope.SessionId);

                seatOneTicket = room.CreatePlayerTicket(1);
                seatOneInner = await room.ConnectAsync(seatOneTicket, timeout.Token);
                seatOneChannel = new CaptureChannel(seatOneInner);
                seatOne = new TableSession(seatOneChannel, new InMemoryOutboxStore());
                await seatOne.OpenAsync(new TableSessionOpenRequest(seatOneTicket.Value, "delayed-v1"), timeout.Token);
                await PumpUntilReadyAsync(seatOne, timeout.Token);
                var current = Assert.IsType<PlayerSnapshot>(seatOne.PlayerSnapshot);
                Assert.Equal(1, current.PrivateView.Seat);
                string currentTiles = string.Join(",", current.PrivateView.ClosedTiles);

                seatOneChannel.InjectNextReceive(delayedFrame);
                await seatOne.PumpOnceAsync(timeout.Token);
                var after = Assert.IsType<PlayerSnapshot>(seatOne.PlayerSnapshot);
                Assert.Equal(seatOne.Binding!.SessionId.Value, after.SessionId);
                Assert.Equal(1, after.PrivateView.Seat);
                Assert.Equal(currentTiles, string.Join(",", after.PrivateView.ClosedTiles));
                Assert.NotEqual(seatZero.Binding.SessionId.Value, seatOne.Binding.SessionId.Value);
            }
            finally
            {
                if (seatOne != null)
                {
                    try { await seatOne.CloseAsync("TEST_DONE", CancellationToken.None); } catch { }
                    try { await seatOneChannel!.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None); } catch { }
                }
                try { await seatZero.CloseAsync("TEST_DONE", CancellationToken.None); } catch { }
                try { await seatZeroChannel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None); } catch { }
            }
        }

        [Fact]
        public async Task Closed_player_to_spectator_to_new_player_receive_path_rejects_old_private_raw()
        {
            await using var harness = new P41Harness();
            harness.Options.SeatPlan = LocalTableSeatPlan.AllFourHumanSeats;
            await using LocalTableRoom room = await LocalTableHost.OpenRoomAsync(harness.Options, TestContext.Current.CancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(MatrixTimeout);

            LocalTableConnectionTicket firstTicket = room.CreatePlayerTicket(0);
            ITableChannel firstInner = await room.ConnectAsync(firstTicket, timeout.Token);
            var firstChannel = new CaptureChannel(firstInner);
            var first = new TableSession(firstChannel, new InMemoryOutboxStore());
            ITableChannel? spectatorChannel = null;
            TableSession? spectator = null;
            ITableChannel? reopenedInner = null;
            CaptureChannel? reopenedChannel = null;
            TableSession? reopened = null;
            try
            {
                await first.OpenAsync(new TableSessionOpenRequest(firstTicket.Value, "path-first"), timeout.Token);
                await PumpUntilReadyAsync(first, timeout.Token);
                byte[] oldFrame = firstChannel.CapturedPlayerSnapshotFrame
                    ?? throw new InvalidOperationException("No real private frame was captured before player close.");
                WireDecodeResult decoded = WireCodec.Decode(oldFrame);
                Assert.True(decoded.IsOk);
                Assert.Equal(0, decoded.Envelope!.SeatId);
                string oldSession = first.Binding!.SessionId.Value;

                await first.CloseAsync("ROLE_SWITCH", CancellationToken.None);
                await firstChannel.CloseAsync(new TableChannelCloseReason("ROLE_SWITCH", true), CancellationToken.None);

                LocalTableConnectionTicket spectatorTicket = room.CreateSpectatorTicket();
                spectatorChannel = await room.ConnectAsync(spectatorTicket, timeout.Token);
                spectator = new TableSession(spectatorChannel, new InMemoryOutboxStore());
                await spectator.OpenAsync(new TableSessionOpenRequest(spectatorTicket.Value, "path-spectator"), timeout.Token);
                await PumpUntilReadyAsync(spectator, timeout.Token);
                Assert.Null(spectator.PlayerSnapshot);
                Assert.NotNull(spectator.PublicSnapshot);

                LocalTableConnectionTicket reopenedTicket = room.CreatePlayerTicket(0);
                reopenedInner = await room.ConnectAsync(reopenedTicket, timeout.Token);
                reopenedChannel = new CaptureChannel(reopenedInner);
                reopened = new TableSession(reopenedChannel, new InMemoryOutboxStore());
                await reopened.OpenAsync(new TableSessionOpenRequest(reopenedTicket.Value, "path-reopened"), timeout.Token);
                await PumpUntilReadyAsync(reopened, timeout.Token);
                var current = Assert.IsType<PlayerSnapshot>(reopened.PlayerSnapshot);
                string currentTiles = string.Join(",", current.PrivateView.ClosedTiles);
                Assert.NotEqual(oldSession, reopened.Binding!.SessionId.Value);

                reopenedChannel.InjectNextReceive(oldFrame);
                await reopened.PumpOnceAsync(timeout.Token);
                var after = Assert.IsType<PlayerSnapshot>(reopened.PlayerSnapshot);
                Assert.Equal(reopened.Binding.SessionId.Value, after.SessionId);
                Assert.Equal(0, after.PrivateView.Seat);
                Assert.Equal(currentTiles, string.Join(",", after.PrivateView.ClosedTiles));
            }
            finally
            {
                if (reopened != null) try { await reopened.CloseAsync("TEST_DONE", CancellationToken.None); } catch { }
                if (reopenedChannel != null) try { await reopenedChannel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None); } catch { }
                if (spectator != null) try { await spectator.CloseAsync("TEST_DONE", CancellationToken.None); } catch { }
                if (spectatorChannel != null) try { await spectatorChannel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None); } catch { }
                try { await first.CloseAsync("TEST_DONE", CancellationToken.None); } catch { }
                try { await firstChannel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None); } catch { }
            }
        }

        [Fact]
        public async Task New_room_cold_reopen_queries_original_scope_without_resubmitting_or_duplicating()
        {
            await using var harness = new P41Harness();
            harness.Options.AutoPlay = false;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(MatrixTimeout);
            var outbox = new InMemoryOutboxStore();
            LocalTableRoom room = await LocalTableHost.OpenRoomAsync(harness.Options, timeout.Token);
            TableSession? firstSession = null;
            ITableChannel? firstChannel = null;
            LocalTableRoom? recoveredRoom = null;
            CaptureChannel? recoveredChannel = null;
            TableSession? recoveredSession = null;
            try
            {
                LocalTableConnectionTicket firstTicket = room.CreatePlayerTicket(0);
                firstChannel = await room.ConnectAsync(firstTicket, timeout.Token);
                firstSession = new TableSession(firstChannel, outbox);
                await firstSession.OpenAsync(new TableSessionOpenRequest(firstTicket.Value, "recovery-first"), timeout.Token);
                await PumpUntilReadyAsync(firstSession, timeout.Token);
                PlayerSnapshot baseline = Assert.IsType<PlayerSnapshot>(firstSession.PlayerSnapshot);
                var decision = Assert.IsType<DecisionView>(baseline.PrivateView.Decision);
                var discard = decision.Actions.First(action => action.ActionType == (uint)ProtocolActionType.DiscardTile);
                BusinessPayload payload = CommandPayloadFactory.SubmitAction(
                    baseline.RoundId,
                    decision.DecisionId,
                    decision.ControlEpoch,
                    ProtocolActionType.DiscardTile,
                    tileIds: discard.TileIds);
                var request = new RequestId("privacy-recovery-original");
                RequestScopeId originalScope = firstSession.Binding!.Scope.ScopeId;
                string originalDigest = CanonicalPayloadDigest.Compute(payload).ToHex();
                var command = new ProtocolCommand(
                    CommandKind.SubmitAction,
                    payload,
                    request,
                    originalScope,
                    new StateVersion(baseline.StateVersion),
                    new RoundId(baseline.RoundId),
                    decisionId: new DecisionId(decision.DecisionId),
                    controlEpoch: new ControlEpoch(decision.ControlEpoch));
                CommandSubmission submitted = await firstSession.SubmitAsync(command, timeout.Token);
                Assert.True(submitted.Sent);

                LocalTableRecoveryFacts before = (await LocalTableHost.CaptureRecoveryAsync(harness.Options.SaveDirectory, timeout.Token)).Facts;
                LocalTableRecoveryRequestFact original = Assert.Single(before.Requests,
                    fact => fact.AccountId == "local-" + harness.Options.RoomId + "-seat0" &&
                        fact.ScopeId == originalScope.Value && fact.RequestId == request.Value);
                Assert.Equal("APPLIED", original.Status);

                await firstSession.CloseAsync("ROOM_COLD_REOPEN", CancellationToken.None);
                await firstChannel.CloseAsync(new TableChannelCloseReason("ROOM_COLD_REOPEN", true), CancellationToken.None);
                await room.DisposeAsync();

                recoveredRoom = await LocalTableHost.OpenRoomAsync(harness.Options, timeout.Token);
                LocalTableConnectionTicket recoveredTicket = recoveredRoom.CreatePlayerTicket(0);
                ITableChannel rawRecovered = await recoveredRoom.ConnectAsync(recoveredTicket, timeout.Token);
                recoveredChannel = new CaptureChannel(rawRecovered);
                recoveredSession = new TableSession(recoveredChannel, outbox);
                TableReceiptResolvedEventArgs? reconciled = null;
                recoveredSession.ReceiptResolved += (_, args) =>
                {
                    if (args.Command.Key.RequestId.Value == request.Value) reconciled = args;
                };
                await recoveredSession.OpenAsync(new TableSessionOpenRequest(recoveredTicket.Value, "recovery-reopened"), timeout.Token);
                await PumpUntilReadyAsync(recoveredSession, timeout.Token);
                for (int i = 0; i < 30 && recoveredSession.PendingCommands.Count > 0; i++)
                {
                    await recoveredSession.ReconcilePendingAsync(timeout.Token);
                    await recoveredSession.PumpOnceAsync(timeout.Token);
                }

                Assert.Empty(recoveredSession.PendingCommands);
                Assert.NotNull(reconciled);
                Assert.True(reconciled!.Resolution.FromReceiptLookup);
                Assert.Equal(Mahjong.Contracts.ReceiptStatus.Applied, reconciled.Resolution.ReceiptStatus);
                Assert.Equal(originalScope.Value, reconciled.Command.Key.ScopeId.Value);
                Assert.Equal(request.Value, reconciled.Command.Key.RequestId.Value);
                Assert.Equal(originalDigest, reconciled.Resolution.CanonicalPayloadSha256);
                Assert.Equal(0, recoveredChannel.CommandFrames);
                Assert.True(recoveredChannel.ReceiptQueryFrames > 0);

                LocalTableRecoveryFacts after = (await LocalTableHost.CaptureRecoveryAsync(harness.Options.SaveDirectory, timeout.Token)).Facts;
                Assert.Equal(before.StateVersion, after.StateVersion);
                Assert.Equal(before.EventSeq, after.EventSeq);
                Assert.Equal(before.StateFingerprint, after.StateFingerprint);
                Assert.Equal(before.Ledger.EntriesSha256, after.Ledger.EntriesSha256);
                Assert.Equal(before.Ledger.BalancesSha256, after.Ledger.BalancesSha256);
                Assert.Equal(before.Physical.TilesSha256, after.Physical.TilesSha256);
            }
            finally
            {
                if (recoveredSession != null) try { await recoveredSession.CloseAsync("TEST_DONE", CancellationToken.None); } catch { }
                if (recoveredChannel != null) try { await recoveredChannel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None); } catch { }
                if (recoveredRoom != null) try { await recoveredRoom.DisposeAsync(); } catch { }
                if (firstSession != null) try { await firstSession.CloseAsync("TEST_DONE", CancellationToken.None); } catch { }
                if (firstChannel != null) try { await firstChannel.CloseAsync(new TableChannelCloseReason("TEST_DONE", true), CancellationToken.None); } catch { }
                try { await room.DisposeAsync(); } catch { }
            }
        }

        private static async Task<(ITableChannel, TableSession)> OpenLegacySessionAsync(
            P41Harness harness,
            CancellationToken cancellationToken)
        {
            ITableChannel channel = await LocalTableHost.CreateChannelAsync(harness.Options, cancellationToken);
            string channelTicket = LocalTableHost.IssueTicket(harness.Options);
            await channel.OpenAsync(new TableChannelConnectRequest(channelTicket, "privacy-matrix", null), cancellationToken);
            var session = new TableSession(channel, new InMemoryOutboxStore());
            string sessionTicket = LocalTableHost.IssueTicket(harness.Options);
            await session.OpenAsync(new TableSessionOpenRequest(sessionTicket, "privacy-matrix"), cancellationToken);
            await PumpUntilReadyAsync(session, cancellationToken);
            return (channel, session);
        }

        private static async Task PumpUntilReadyAsync(TableSession session, CancellationToken cancellationToken)
        {
            for (int i = 0; i < 32 && session.State != TableConnectionState.Ready; i++)
            {
                if (session.State == TableConnectionState.Faulted)
                    throw new InvalidOperationException("Session faulted before Ready: " + (session.LastError ?? "<no error>"));
                await session.PumpOnceAsync(cancellationToken);
            }
            Assert.Equal(TableConnectionState.Ready, session.State);
        }

        private static async Task PumpAsync(TableSession session, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                    await session.PumpOnceAsync(cancellationToken);
            }
            catch (Exception exception) when (
                exception is OperationCanceledException ||
                exception is TableChannelClosedException ||
                exception is InvalidOperationException)
            {
            }
        }

        private static async Task DriveOtherSeatsAsync(
            TableSession[] sessions,
            int targetSeat,
            CancellationToken cancellationToken)
        {
            var lastSentVersion = new Dictionary<int, ulong>();
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    for (int seat = 0; seat < sessions.Length; seat++)
                    {
                        if (seat == targetSeat || sessions[seat] == null) continue;
                        PlayerSnapshot? snapshot = sessions[seat].PlayerSnapshot;
                        var board = snapshot?.PublicView?.Board;
                        if (snapshot == null || board == null || board.CurrentSeat != seat) continue;
                        if (sessions[seat].PendingCommands.Count > 0) continue;
                        if (lastSentVersion.TryGetValue(seat, out ulong sentVersion) &&
                            sentVersion == snapshot.StateVersion) continue;

                        ProtocolActionType? action = null;
                        uint[]? tiles = null;
                        switch (board.Phase)
                        {
                            case GamePhase.CheatWindow:
                            case GamePhase.Discard:
                                action = ProtocolActionType.DiscardTile;
                                uint tile = snapshot.PrivateView.HasDrawnTileId
                                    ? snapshot.PrivateView.DrawnTileId
                                    : snapshot.PrivateView.ClosedTiles[0];
                                tiles = new[] { tile };
                                break;
                            case GamePhase.ResponseWindow:
                            case GamePhase.ChankanWindow:
                                action = ProtocolActionType.Pass;
                                break;
                        }
                        if (!action.HasValue) continue;
                        try
                        {
                            await SubmitActionNoWaitAsync(sessions[seat], action.Value, tiles, cancellationToken);
                            lastSentVersion[seat] = snapshot.StateVersion;
                        }
                        catch (Exception exception) when (
                            exception is OperationCanceledException ||
                            exception is TableChannelClosedException ||
                            exception is InvalidOperationException)
                        {
                        }
                    }
                    await Task.Delay(10, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private static async Task SubmitActionNoWaitAsync(
            TableSession session,
            ProtocolActionType action,
            uint[]? tiles,
            CancellationToken cancellationToken)
        {
            PlayerSnapshot snapshot = session.PlayerSnapshot
                ?? throw new InvalidOperationException("No player snapshot is available for the room driver.");
            BusinessPayload payload = CommandPayloadFactory.SubmitAction(
                snapshot.RoundId,
                "room-driver",
                0UL,
                action,
                tileIds: tiles);
            var command = new ProtocolCommand(
                CommandKind.SubmitAction,
                payload,
                new RequestId("room-driver-" + Guid.NewGuid().ToString("N")),
                session.Binding!.Scope.ScopeId,
                new StateVersion(snapshot.StateVersion),
                new RoundId(snapshot.RoundId),
                decisionId: null,
                controlEpoch: new ControlEpoch(snapshot.PrivateView.Control?.ControlEpoch ?? 0UL));
            await session.SubmitAsync(command, cancellationToken);
        }

        private static Envelope HeartbeatEnvelope(string sessionId, ulong bindingEpoch, string roomId, int sequence)
        {
            return new Envelope
            {
                ProtocolMajor = Mahjong.Contracts.ProtocolConstants.Major,
                ProtocolMinor = Mahjong.Contracts.ProtocolConstants.Minor,
                RulesetVersion = Mahjong.Contracts.ProtocolConstants.RulesetVersion,
                ProjectionVersion = Mahjong.Contracts.ProtocolConstants.ProjectionVersion,
                MessageType = MessageType.Heartbeat,
                SessionId = sessionId,
                RoomId = roomId,
                SeatId = -1,
                BindingEpoch = bindingEpoch,
                Heartbeat = new Heartbeat
                {
                    Ping = new Ping { Nonce = sequence.ToString("x16", System.Globalization.CultureInfo.InvariantCulture) }
                }
            };
        }

        private sealed class CaptureChannel : ITableChannel
        {
            private readonly ITableChannel _inner;
            private byte[]? _injectedFrame;

            public CaptureChannel(ITableChannel inner) => _inner = inner;

            public bool IsOpen => _inner.IsOpen;
            public byte[]? CapturedPlayerSnapshotFrame { get; private set; }
            public int CommandFrames { get; private set; }
            public int ReceiptQueryFrames { get; private set; }

            public Task OpenAsync(TableChannelConnectRequest request, CancellationToken cancellationToken) =>
                _inner.OpenAsync(request, cancellationToken);

            public Task SendAsync(ReadOnlyMemory<byte> wireEnvelope, CancellationToken cancellationToken)
            {
                WireDecodeResult decoded = WireCodec.Decode(wireEnvelope.ToArray());
                if (decoded.IsOk && decoded.Envelope != null)
                {
                    if (decoded.Envelope.MessageType == MessageType.Command) CommandFrames++;
                    if (decoded.Envelope.MessageType == MessageType.ReceiptQuery) ReceiptQueryFrames++;
                }
                return _inner.SendAsync(wireEnvelope, cancellationToken);
            }

            public async Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
            {
                byte[]? injected = Interlocked.Exchange(ref _injectedFrame, null);
                if (injected != null) return new ReadOnlyMemory<byte>(injected);
                ReadOnlyMemory<byte> frame = await _inner.ReceiveAsync(cancellationToken);
                WireDecodeResult decoded = WireCodec.Decode(frame.ToArray());
                if (decoded.IsOk && decoded.Envelope?.MessageType == MessageType.SyncResponse &&
                    decoded.Envelope.SyncResponse?.Target == SyncTarget.PlayerStream)
                    CapturedPlayerSnapshotFrame = frame.ToArray();
                return frame;
            }

            public void InjectNextReceive(byte[] frame)
            {
                if (frame == null) throw new ArgumentNullException(nameof(frame));
                if (Interlocked.CompareExchange(ref _injectedFrame, (byte[])frame.Clone(), null) != null)
                    throw new InvalidOperationException("An injected wire frame is already pending.");
            }

            public Task CloseAsync(TableChannelCloseReason reason, CancellationToken cancellationToken) =>
                _inner.CloseAsync(reason, cancellationToken);
        }
    }
}
