using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.LocalHost;
using WireReceipt = Mahjong.Contracts.Wire.Receipt;
using WireReceiptStatus = Mahjong.Contracts.Wire.ReceiptStatus;
using WireSleeveBloomView = Mahjong.Contracts.Wire.SleeveBloomView;
using WireBeamSwapView = Mahjong.Contracts.Wire.BeamSwapView;
using WireBeamSwapCandidateView = Mahjong.Contracts.Wire.BeamSwapCandidateView;

internal static class Program
{
    private static readonly List<string> Errors = new List<string>();
    private static readonly List<object> Facts = new List<object>();

    private static int Main(string[] args)
    {
        string casePath = "";
        string caseKind = "";
        string sourceHead = "";
        string packagePath = "";
        string status = "FAIL";
        try
        {
            if (args.Length > 0 && args[0] == "capture-localhost-authorized")
            {
                return CaptureLocalHostAuthorizedAsync(args).GetAwaiter().GetResult();
            }
            Dictionary<string, string> options = Parse(args);
            casePath = Required(options, "--evidence");
            caseKind = Required(options, "--case");
            sourceHead = Required(options, "--source-head");
            packagePath = Required(options, "--package-manifest");
            if (caseKind != "authorized" && caseKind != "negative") throw new InvalidDataException("CASE_UNSUPPORTED");
            if (sourceHead.Length != 40 || sourceHead.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("SOURCE_HEAD_INVALID");
            Verify(casePath, caseKind, sourceHead, packagePath);
            status = Errors.Count == 0 ? "PASS" : "FAIL";
        }
        catch (Exception ex)
        {
            Errors.Add(ex.GetType().Name + ":" + ex.Message);
        }

        var result = new
        {
            schemaVersion = 2,
            kind = "c04-player-cheat-raw-verification",
            status,
            evidence = SafeFullPath(casePath),
            @case = caseKind,
            sourceHead,
            packageManifest = SafeFullPath(packagePath),
            facts = Facts,
            errors = Errors
        };
        Console.Out.WriteLine(JsonSerializer.Serialize(result));
        return status == "PASS" ? 0 : 1;
    }

    private static async Task<int> CaptureLocalHostAuthorizedAsync(string[] args)
    {
        try
        {
            Dictionary<string, string> options = ParseCapture(args);
            string outputRoot = Path.GetFullPath(Required(options, "--output-root"));
            string sourceHead = Required(options, "--source-head");
            string packagePath = Path.GetFullPath(Required(options, "--package-manifest"));
            if (sourceHead.Length != 40 || sourceHead.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("SOURCE_HEAD_INVALID");
            if (Directory.Exists(outputRoot) || File.Exists(outputRoot)) throw new IOException("REFUSE_OVERWRITE");
            Directory.CreateDirectory(outputRoot);
            string packageHash = Sha(File.ReadAllBytes(packagePath));
            await CaptureAuthorizedRowsAsync(outputRoot, sourceHead, packageHash).ConfigureAwait(false);
            Console.Out.WriteLine(JsonSerializer.Serialize(new { status = "CAPTURED", producer = "LOCALHOST_CHANNEL", casePath = Path.Combine(outputRoot, "case-authorized.json"), packageManifestSha256 = packageHash }));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { status = "FAIL", error = ex.GetType().Name + ":" + ex.Message }));
            return 1;
        }
    }

    private static Dictionary<string, string> ParseCapture(string[] args)
    {
        if (args.Length == 0 || args[0] != "capture-localhost-authorized") throw new ArgumentException("Bad capture command.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 1; i < args.Length; i++)
        {
            string key = args[i];
            if (!key.StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length || values.ContainsKey(key)) throw new ArgumentException("Invalid capture option: " + key);
            values.Add(key, args[++i]);
        }
        if (values.Count != 3 || values.Keys.Any(k => k != "--output-root" && k != "--source-head" && k != "--package-manifest"))
            throw new ArgumentException("Expected --output-root, --source-head and --package-manifest.");
        return values;
    }

    private static async Task CaptureAuthorizedRowsAsync(string outputRoot, string sourceHead, string packageHash)
    {
        string rawRoot = Path.Combine(outputRoot, "raw");
        Directory.CreateDirectory(rawRoot);
        string saveRoot = Path.Combine(outputRoot, "localhost-save");
        LocalTableHostOptions hostOptions = LocalPlayerCheatScenario.CreateOptions(saveRoot,
            LocalPlayerCheatScenario.DefaultRoomId, LocalPlayerCheatScenarioKind.AuthorizedThreeCheats, "c04-evidence-owner");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        CancellationToken ct = cancellation.Token;
        ITableChannel inner = await LocalTableHost.CreateChannelAsync(hostOptions, ct).ConfigureAwait(false);
        var captureChannel = new CapturingTableChannel(inner);
        string ticket = LocalTableHost.IssueTicket(hostOptions);
        await captureChannel.OpenAsync(new TableChannelConnectRequest(ticket, "c04-verifier", null), ct).ConfigureAwait(false);
        var session = new TableSession(captureChannel, new InMemoryOutboxStore());
        await session.OpenAsync(new TableSessionOpenRequest(LocalTableHost.IssueTicket(hostOptions), "c04-verifier"), ct).ConfigureAwait(false);
        for (int i = 0; i < 50 && session.State != TableConnectionState.Ready; i++) await session.PumpOnceAsync(ct).ConfigureAwait(false);
        if (session.State != TableConnectionState.Ready) throw new InvalidDataException("LOCALHOST_SESSION_NOT_READY");
        using var pumpCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task pump = PumpSessionAsync(session, pumpCancellation.Token);
        var rows = new List<object>();
        try
        {
            PlayerSnapshot snapshot = await WaitSnapshotAsync(session, s => s.PublicView.Board.Phase == GamePhase.CheatWindow &&
                s.PublicView.Board.CurrentSeat == 0 && s.PrivateView?.Decision != null, ct).ConfigureAwait(false);
            rows.Add(await CaptureActionAsync("tile-mark", session, captureChannel, hostOptions, snapshot,
                CommandKind.SubmitTileMark,
                CommandPayloadFactory.SubmitTileMark(snapshot.RoundId, snapshot.PrivateView!.Decision!.DecisionId,
                    snapshot.PrivateView.Decision.ControlEpoch, LocalPlayerCheatScenario.ProposedTileMarkTileIds), rawRoot, ct).ConfigureAwait(false));
            snapshot = await WaitSnapshotAsync(session, s => s.PrivateView?.Cheat?.CheatPoints == 119, ct).ConfigureAwait(false);
            await SendActionAsync(session, snapshot, ProtocolActionType.DiscardTile, new[] { 120u }, ct).ConfigureAwait(false);
            snapshot = await AdvanceDealerTurnAsync(session, snapshot.StateVersion, ct).ConfigureAwait(false);

            rows.Add(await CaptureActionAsync("sleeve-bloom", session, captureChannel, hostOptions, snapshot,
                CommandKind.SubmitSleeveBloom,
                CommandPayloadFactory.SubmitSleeveBloom(snapshot.RoundId, snapshot.PrivateView!.Decision!.DecisionId,
                    snapshot.PrivateView.Decision.ControlEpoch), rawRoot, ct).ConfigureAwait(false));
            snapshot = await WaitSnapshotAsync(session, s => s.PrivateView?.Cheat?.CheatPoints == 109 && s.PrivateView.Cheat.SleeveBloom != null, ct).ConfigureAwait(false);
            WireSleeveBloomView sleeveOffer = snapshot.PrivateView!.Cheat.SleeveBloom!;
            rows.Add(await CaptureActionAsync("sleeve-second", session, captureChannel, hostOptions, snapshot,
                CommandKind.SubmitSleeveBloomChoice,
                CommandPayloadFactory.SubmitSleeveBloomChoice(sleeveOffer.OfferId, SleeveBloomChoice.Second), rawRoot, ct).ConfigureAwait(false));
            snapshot = await WaitSnapshotAsync(session, s => s.PrivateView?.Cheat?.SleeveBloom == null && s.PrivateView?.Cheat?.CheatPoints == 109, ct).ConfigureAwait(false);
            uint discard = snapshot.PrivateView!.HasDrawnTileId ? snapshot.PrivateView.DrawnTileId : snapshot.PrivateView.ClosedTiles[0];
            await SendActionAsync(session, snapshot, ProtocolActionType.DiscardTile, new[] { discard }, ct).ConfigureAwait(false);
            snapshot = await AdvanceDealerTurnAsync(session, snapshot.StateVersion, ct).ConfigureAwait(false);

            rows.Add(await CaptureActionAsync("beam-swap", session, captureChannel, hostOptions, snapshot,
                CommandKind.SubmitBeamSwap,
                CommandPayloadFactory.SubmitBeamSwap(snapshot.RoundId, snapshot.PrivateView!.Decision!.DecisionId,
                    snapshot.PrivateView.Decision.ControlEpoch), rawRoot, ct).ConfigureAwait(false));
            snapshot = await WaitSnapshotAsync(session, s => s.PrivateView?.Cheat?.CheatPoints == 99 && s.PrivateView.Cheat.BeamSwap != null, ct).ConfigureAwait(false);
            WireBeamSwapView beamOffer = snapshot.PrivateView!.Cheat.BeamSwap!;
            WireBeamSwapCandidateView candidate = beamOffer.Candidates.First(c => c.HasTileId);
            uint handTile = beamOffer.SelectableHandTileIds[0];
            rows.Add(await CaptureActionAsync("beam-swap-choice", session, captureChannel, hostOptions, snapshot,
                CommandKind.SubmitBeamSwapChoice,
                CommandPayloadFactory.SubmitBeamSwapChoice(beamOffer.OfferId, BeamSwapChoice.Swap, handTile, candidate.Slot), rawRoot, ct).ConfigureAwait(false));

            var index = new { schemaVersion = 2, producer = "LOCALHOST_CHANNEL", sourceHead,
                packageManifestSha256 = packageHash, actions = rows };
            byte[] indexBytes = JsonSerializer.SerializeToUtf8Bytes(index, new JsonSerializerOptions { WriteIndented = true });
            string indexFile = Path.Combine(rawRoot, "index.json");
            File.WriteAllBytes(indexFile, indexBytes);
            var caseMetadata = new { schemaVersion = 1, sourceHead, rawEvidenceIndex = "raw/index.json", rawEvidenceIndexSha256 = Sha(indexBytes) };
            File.WriteAllBytes(Path.Combine(outputRoot, "case-authorized.json"), JsonSerializer.SerializeToUtf8Bytes(caseMetadata, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            pumpCancellation.Cancel();
            try { await pump.ConfigureAwait(false); } catch (OperationCanceledException) { }
            await session.CloseAsync("EVIDENCE_CAPTURE_DONE", CancellationToken.None).ConfigureAwait(false);
            await captureChannel.CloseAsync(new TableChannelCloseReason("EVIDENCE_CAPTURE_DONE", true), CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task<object> CaptureActionAsync(string id, TableSession session, CapturingTableChannel channel,
        LocalTableHostOptions options, PlayerSnapshot snapshot, CommandKind kind, BusinessPayload payload,
        string rawRoot, CancellationToken ct)
    {
        LocalTableRecoveryCapture before = await LocalTableHost.CaptureRecoveryAsync(options.SaveDirectory, ct).ConfigureAwait(false);
        int sentStart = channel.SentCount, receivedStart = channel.ReceivedCount;
        string requestId = "c04-raw-" + Guid.NewGuid().ToString("N");
        var command = new ProtocolCommand(kind, payload, new RequestId(requestId), session.Binding!.Scope.ScopeId,
            new StateVersion(snapshot.StateVersion), new RoundId(snapshot.RoundId),
            decisionId: snapshot.PrivateView?.Decision == null ? null : new DecisionId(snapshot.PrivateView.Decision.DecisionId),
            controlEpoch: new ControlEpoch(snapshot.PrivateView?.Decision?.ControlEpoch ?? 0));
        var completion = new TaskCompletionSource<TableReceiptResolvedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<TableReceiptResolvedEventArgs> handler = (_, e) => { if (e.Command.RequestId == requestId) completion.TrySetResult(e); };
        session.ReceiptResolved += handler;
        TableReceiptResolvedEventArgs resolved;
        try
        {
            CommandSubmission submission = await session.SubmitAsync(command, ct).ConfigureAwait(false);
            if (!submission.Sent) throw new InvalidDataException(id + ":COMMAND_NOT_SENT");
            resolved = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        }
        finally { session.ReceiptResolved -= handler; }
        Envelope commandEnvelope = FindEnvelope(channel.SentFrom(sentStart), e => e.MessageType == MessageType.Command && e.RequestId == requestId);
        Envelope receiptEnvelope = FindEnvelope(channel.ReceivedFrom(receivedStart), e => e.MessageType == MessageType.Receipt && e.RequestId == requestId);
        WireReceipt receipt = receiptEnvelope.Receipt;
        if (receipt.Status != WireReceiptStatus.Applied || !receipt.HasCommandVersion) throw new InvalidDataException(id + ":REAL_CHANNEL_COMMAND_NOT_APPLIED:" + receipt.ReasonCode);
        byte[] publicationBytes = await FindPublicationAsync(channel, receivedStart, receipt.CommandVersion, ct).ConfigureAwait(false);
        LocalTableRecoveryCapture after = await LocalTableHost.CaptureRecoveryAsync(options.SaveDirectory, ct).ConfigureAwait(false);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = id,
            ["command"] = WriteRaw(rawRoot, id + "-command.pb", commandEnvelope.ToByteArray()),
            ["receipt"] = WriteRaw(rawRoot, id + "-receipt.pb", receiptEnvelope.ToByteArray()),
            ["acceptedPrivatePublication"] = WriteRaw(rawRoot, id + "-private-update.pb", publicationBytes),
            ["before"] = new Dictionary<string, object?> {
                ["authoritySave"] = WriteRaw(rawRoot, id + "-before.save", before.AuthoritySaveBytes),
                ["cheatState"] = WriteRaw(rawRoot, id + "-before-cheat.bin", before.CheatStatePayload) },
            ["after"] = new Dictionary<string, object?> {
                ["authoritySave"] = WriteRaw(rawRoot, id + "-after.save", after.AuthoritySaveBytes),
                ["cheatState"] = WriteRaw(rawRoot, id + "-after-cheat.bin", after.CheatStatePayload) }
        };
    }

    private static async Task<byte[]> FindPublicationAsync(CapturingTableChannel channel, int start, ulong commandVersion, CancellationToken ct)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            foreach (byte[] bytes in channel.ReceivedFrom(start))
            {
                WireDecodeResult decoded = WireCodec.Decode(bytes);
                Envelope? envelope = decoded.Envelope;
                if (envelope?.MessageType == MessageType.PlayerUpdate && envelope.PlayerUpdate.VisibleEvents.Any(e => e.CommandVersion == commandVersion))
                    return bytes;
            }
            await Task.Delay(10, ct).ConfigureAwait(false);
        }
        throw new TimeoutException("ACCEPTED_PRIVATE_PUBLICATION_TIMEOUT");
    }

    private static Envelope FindEnvelope(IEnumerable<byte[]> frames, Func<Envelope, bool> predicate)
    {
        foreach (byte[] bytes in frames)
        {
            WireDecodeResult decoded = WireCodec.Decode(bytes);
            if (decoded.Envelope != null && predicate(decoded.Envelope)) return decoded.Envelope;
        }
        throw new InvalidDataException("CAPTURE_FRAME_NOT_FOUND");
    }

    private static object WriteRaw(string rawRoot, string name, byte[] bytes)
    {
        string file = Path.Combine(rawRoot, name);
        File.WriteAllBytes(file, bytes);
        return new { path = "raw/" + name, sha256 = Sha(bytes) };
    }

    private static async Task PumpSessionAsync(TableSession session, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested) await session.PumpOnceAsync(ct).ConfigureAwait(false);
    }

    private static async Task<PlayerSnapshot> WaitSnapshotAsync(TableSession session, Func<PlayerSnapshot, bool> predicate, CancellationToken ct)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            PlayerSnapshot? value = session.PlayerSnapshot;
            if (value != null && predicate(value)) return value;
            await Task.Delay(10, ct).ConfigureAwait(false);
        }
        throw new TimeoutException("LOCALHOST_SNAPSHOT_TIMEOUT");
    }

    private static async Task SendActionAsync(TableSession session, PlayerSnapshot snapshot, ProtocolActionType action, uint[]? tiles, CancellationToken ct)
    {
        string requestId = "c04-advance-" + Guid.NewGuid().ToString("N");
        BusinessPayload payload = CommandPayloadFactory.SubmitAction(snapshot.RoundId, snapshot.PrivateView!.Decision!.DecisionId,
            snapshot.PrivateView.Decision.ControlEpoch, action, tileIds: tiles);
        var command = new ProtocolCommand(CommandKind.SubmitAction, payload, new RequestId(requestId), session.Binding!.Scope.ScopeId,
            new StateVersion(snapshot.StateVersion), new RoundId(snapshot.RoundId), decisionId: new DecisionId(snapshot.PrivateView.Decision.DecisionId),
            controlEpoch: new ControlEpoch(snapshot.PrivateView.Decision.ControlEpoch));
        var completion = new TaskCompletionSource<TableReceiptResolvedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<TableReceiptResolvedEventArgs> handler = (_, e) => { if (e.Command.RequestId == requestId) completion.TrySetResult(e); };
        session.ReceiptResolved += handler;
        try
        {
            CommandSubmission submission = await session.SubmitAsync(command, ct).ConfigureAwait(false);
            if (!submission.Sent) throw new InvalidDataException("AUTO_ADVANCE_COMMAND_NOT_SENT");
            TableReceiptResolvedEventArgs receipt = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            if (receipt.Resolution.ReceiptStatus != Mahjong.Contracts.ReceiptStatus.Applied)
            {
                if (receipt.Resolution.ReasonCode == ProtocolErrorCode.DecisionMismatch && session.PlayerSnapshot?.StateVersion > snapshot.StateVersion) return;
                throw new InvalidDataException("AUTO_ADVANCE_REJECTED:" + receipt.Resolution.ReasonCode);
            }
        }
        finally { session.ReceiptResolved -= handler; }
    }

    private static async Task<PlayerSnapshot> AdvanceDealerTurnAsync(TableSession session, ulong version, CancellationToken ct)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            PlayerSnapshot? s = session.PlayerSnapshot;
            if (s != null && s.StateVersion > version && s.PublicView.Board.Phase == GamePhase.CheatWindow && s.PublicView.Board.CurrentSeat == 0 && s.PrivateView?.Decision != null) return s;
            if (s?.PrivateView?.Decision != null)
            {
                GamePhase phase = s.PublicView.Board.Phase;
                if (s.PublicView.Board.CurrentSeat == 0 && (phase == GamePhase.CheatWindow || phase == GamePhase.Discard))
                {
                    uint tile = s.PrivateView!.HasDrawnTileId ? s.PrivateView.DrawnTileId : s.PrivateView.ClosedTiles[0];
                    await SendActionAsync(session, s, ProtocolActionType.DiscardTile, new[] { tile }, ct).ConfigureAwait(false);
                }
                else if (phase == GamePhase.ResponseWindow || phase == GamePhase.ChankanWindow)
                {
                    if (s.PrivateView.Decision.Actions.Any(a => a.ActionType == (uint)ProtocolActionType.Pass))
                        await SendActionAsync(session, s, ProtocolActionType.Pass, null, ct).ConfigureAwait(false);
                }
            }
            await Task.Delay(20, ct).ConfigureAwait(false);
        }
        throw new TimeoutException("DEALER_NEXT_TURN_TIMEOUT");
    }

    private sealed class CapturingTableChannel : ITableChannel
    {
        private readonly ITableChannel _inner;
        private readonly object _gate = new object();
        private readonly List<byte[]> _sent = new List<byte[]>();
        private readonly List<byte[]> _received = new List<byte[]>();
        public CapturingTableChannel(ITableChannel inner) { _inner = inner; }
        public bool IsOpen => _inner.IsOpen;
        public int SentCount { get { lock (_gate) return _sent.Count; } }
        public int ReceivedCount { get { lock (_gate) return _received.Count; } }
        public Task OpenAsync(TableChannelConnectRequest request, CancellationToken cancellationToken) => _inner.OpenAsync(request, cancellationToken);
        public Task SendAsync(ReadOnlyMemory<byte> wireEnvelope, CancellationToken cancellationToken)
        {
            lock (_gate) _sent.Add(wireEnvelope.ToArray());
            return _inner.SendAsync(wireEnvelope, cancellationToken);
        }
        public async Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
        {
            ReadOnlyMemory<byte> bytes = await _inner.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate) _received.Add(bytes.ToArray());
            return bytes;
        }
        public Task CloseAsync(TableChannelCloseReason reason, CancellationToken cancellationToken) => _inner.CloseAsync(reason, cancellationToken);
        public byte[][] SentFrom(int index) { lock (_gate) return _sent.Skip(index).Select(x => x.ToArray()).ToArray(); }
        public byte[][] ReceivedFrom(int index) { lock (_gate) return _received.Skip(index).Select(x => x.ToArray()).ToArray(); }
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        if (args.Length == 0 || args[0] != "verify") throw new ArgumentException("Usage: verify --evidence <case-json> --case <authorized|negative> --source-head <sha> --package-manifest <path>");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 1; i < args.Length; i++)
        {
            string key = args[i];
            if (!key.StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length || values.ContainsKey(key))
                throw new ArgumentException("Invalid or duplicate CLI option: " + key);
            values.Add(key, args[++i]);
        }
        if (values.Count != 4 || values.Keys.Any(k => k != "--evidence" && k != "--case" && k != "--source-head" && k != "--package-manifest"))
            throw new ArgumentException("Expected exactly --evidence, --case, --source-head and --package-manifest.");
        return values;
    }

    private static void Verify(string casePath, string caseKind, string sourceHead, string packagePath)
    {
        string caseFull = Path.GetFullPath(casePath);
        string captureRoot = Path.GetDirectoryName(caseFull) ?? throw new InvalidDataException("CASE_ROOT_MISSING");
        using JsonDocument caseDoc = ParseJson(File.ReadAllBytes(caseFull));
        JsonElement metadata = caseDoc.RootElement;
        string metadataHead = RequiredString(metadata, "sourceHead");
        if (!Eq(metadataHead, sourceHead)) Fail("CASE_SOURCE_HEAD_MISMATCH");
        string rawIndex = RequiredString(metadata, "rawEvidenceIndex");
        string rawIndexSha = RequiredString(metadata, "rawEvidenceIndexSha256");
        string packageFull = Path.GetFullPath(packagePath);
        if ((File.GetAttributes(packageFull) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("PACKAGE_MANIFEST_REPARSE_POINT");
        byte[] packageBytes = File.ReadAllBytes(packageFull);
        string packageSha = Sha(packageBytes);
        using JsonDocument packageDoc = ParseJson(packageBytes);
        JsonElement package = packageDoc.RootElement;
        if (!Eq(RequiredString(package, "sourceHead"), sourceHead)) Fail("PACKAGE_SOURCE_HEAD_MISMATCH");
        if (!Eq(RequiredString(package, "kind"), "c04-player-package-manifest")) Fail("PACKAGE_MANIFEST_KIND");
        JsonElement packageFiles = Property(package, "files");
        if (packageFiles.ValueKind != JsonValueKind.Array || packageFiles.GetArrayLength() == 0) Fail("PACKAGE_FILE_CLOSURE_MISSING");
        string packageRoot = Path.GetDirectoryName(Path.GetFullPath(packagePath)) ?? throw new InvalidDataException("PACKAGE_ROOT_MISSING");
        foreach (JsonElement file in packageFiles.EnumerateArray())
        {
            string relative = RequiredString(file, "path");
            string full = ResolveCaptureFile(packageRoot, relative);
            byte[] bytes = File.ReadAllBytes(full);
            if (bytes.LongLength != Property(file, "bytes").GetInt64() || !Eq(Sha(bytes), RequiredString(file, "sha256"))) Fail("PACKAGE_FILE_MISMATCH:" + relative);
        }

        string indexPath = ResolveCaptureFile(captureRoot, rawIndex);
        byte[] indexBytes = File.ReadAllBytes(indexPath);
        if (!Eq(Sha(indexBytes), rawIndexSha)) Fail("RAW_INDEX_SHA_MISMATCH");
        using JsonDocument indexDoc = ParseJson(indexBytes);
        JsonElement index = indexDoc.RootElement;
        if (Int(index, "schemaVersion") != 2) Fail("RAW_SCHEMA_VERSION");
        if (!Eq(RequiredString(index, "producer"), "LOCALHOST_CHANNEL")) Fail("RAW_PRODUCER_CLASSIFICATION");
        if (!Eq(RequiredString(index, "sourceHead"), sourceHead)) Fail("RAW_SOURCE_HEAD_MISMATCH");
        if (!Eq(RequiredString(index, "packageManifestSha256"), packageSha)) Fail("PACKAGE_MANIFEST_HASH_MISMATCH");

        JsonElement actions = Property(index, "actions");
        string[] expected = caseKind == "authorized"
            ? new[] { "tile-mark", "sleeve-bloom", "sleeve-second", "beam-swap", "beam-swap-choice" }
            : new[] { "unauthorized", "insufficient-funds" };
        if (actions.ValueKind != JsonValueKind.Array || actions.GetArrayLength() != expected.Length)
            Fail("ACTION_COUNT_EXPECTED_" + expected.Length);
        var decoded = new List<ActionCapture>();
        int ordinal = 0;
        foreach (JsonElement action in actions.EnumerateArray())
        {
            if (ordinal >= expected.Length) { Fail("ACTION_COUNT_EXCESS"); break; }
            string id = RequiredString(action, "id");
            if (!Eq(id, expected[ordinal])) Fail("ACTION_ORDER_OR_ID_" + ordinal);
            decoded.Add(ReadAction(captureRoot, action, id, caseKind));
            ordinal++;
        }
        if (decoded.Count == expected.Length)
        {
            if (caseKind == "authorized") VerifyAuthorized(decoded);
            else VerifyNegative(decoded);
        }
    }

    private static ActionCapture ReadAction(string root, JsonElement action, string id, string caseKind)
    {
        byte[] commandBytes = ReadRaw(root, Property(action, "command"));
        byte[] receiptBytes = ReadRaw(root, Property(action, "receipt"));
        byte[] publicationBytes = caseKind == "authorized"
            ? ReadRaw(root, Property(action, "acceptedPrivatePublication"))
            : Array.Empty<byte>();
        JsonElement before = Property(action, "before");
        JsonElement after = Property(action, "after");
        byte[] beforeSave = ReadRaw(root, Property(before, "authoritySave"));
        byte[] beforeCheat = ReadRaw(root, Property(before, "cheatState"));
        byte[] afterSave = ReadRaw(root, Property(after, "authoritySave"));
        byte[] afterCheat = ReadRaw(root, Property(after, "cheatState"));

        Envelope commandEnvelope = Decode(commandBytes, "command");
        Envelope receiptEnvelope = Decode(receiptBytes, "receipt");
        Envelope? publicationEnvelope = publicationBytes.Length == 0 ? null : Decode(publicationBytes, "publication");
        if (commandEnvelope.MessageType != MessageType.Command || commandEnvelope.BodyCase != Envelope.BodyOneofCase.Command)
            Fail(id + ":COMMAND_NOT_TYPED_ENVELOPE");
        if (receiptEnvelope.MessageType != MessageType.Receipt || receiptEnvelope.BodyCase != Envelope.BodyOneofCase.Receipt)
            Fail(id + ":RECEIPT_NOT_TYPED_ENVELOPE");
        if (publicationEnvelope != null && (publicationEnvelope.MessageType != MessageType.PlayerUpdate || publicationEnvelope.BodyCase != Envelope.BodyOneofCase.PlayerUpdate))
            Fail(id + ":PUBLICATION_NOT_PLAYER_UPDATE");

        Command command = commandEnvelope.Command;
        byte[] payload = command.CanonicalPayload.ToByteArray();
        string canonical = CanonicalPayloadDigest.Compute(payload).ToHex();
        CanonicalPayloadDocument parsed = CanonicalPayloadParser.Parse(payload);
        if (parsed.CommandKind != (ushort)command.Type) Fail(id + ":CANONICAL_KIND_MISMATCH");
        if (commandEnvelope.Scope == null || commandEnvelope.Scope.Role != Role.Player || commandEnvelope.SeatId < 0 || commandEnvelope.SeatId > 3) Fail(id + ":COMMAND_SCOPE_NOT_PLAYER");
        if (string.IsNullOrWhiteSpace(commandEnvelope.RequestId) || string.IsNullOrWhiteSpace(commandEnvelope.Scope!.ScopeId)) Fail(id + ":COMMAND_IDENTITY_MISSING");
        if (receiptEnvelope.RequestId != commandEnvelope.RequestId || receiptEnvelope.Scope!.ScopeId != commandEnvelope.Scope!.ScopeId ||
            receiptEnvelope.Scope.AccountId != commandEnvelope.Scope!.AccountId || receiptEnvelope.Scope.Role != commandEnvelope.Scope.Role)
            Fail(id + ":RECEIPT_IDENTITY_MISMATCH");
        if (receiptEnvelope.Receipt.CanonicalPayloadSha256 != canonical) Fail(id + ":RECEIPT_CANONICAL_DIGEST_MISMATCH");

        PlayerUpdate? update = publicationEnvelope?.PlayerUpdate;
        PlayerSnapshot? snapshot = update?.Snapshot;
        if (snapshot != null)
        {
            if (publicationEnvelope!.SeatId != snapshot.PrivateView.Seat)
                Fail(id + ":PRIVATE_PUBLICATION_OWNER_MISMATCH");
            if (publicationEnvelope.Scope != null && (publicationEnvelope.Scope.Role != Role.Player ||
                publicationEnvelope.Scope.AccountId != commandEnvelope.Scope!.AccountId || publicationEnvelope.Scope.ScopeId != commandEnvelope.Scope.ScopeId))
                Fail(id + ":PRIVATE_PUBLICATION_SCOPE_MISMATCH");
            if (publicationEnvelope.SessionId != commandEnvelope.SessionId || publicationEnvelope.RoomId != commandEnvelope.RoomId || publicationEnvelope.SeatId != commandEnvelope.SeatId)
                Fail(id + ":PRIVATE_PUBLICATION_BINDING_MISMATCH");
            if (update!.Cursor == null || snapshot.Cursor == null || publicationEnvelope.Cursor == null ||
                !update.Cursor.Equals(snapshot.Cursor) || !publicationEnvelope.Cursor.Equals(update.Cursor))
                Fail(id + ":PUBLICATION_CURSOR_MISMATCH");
        }

        WireReceipt receipt = receiptEnvelope.Receipt;
        LocalTableRecoveryFacts beforeFacts = LocalTableRecoveryFactsReader.ReadPersistedFacts(beforeSave, beforeCheat);
        LocalTableRecoveryFacts afterFacts = LocalTableRecoveryFactsReader.ReadPersistedFacts(afterSave, afterCheat);
        if (beforeFacts.RoomId != commandEnvelope.RoomId || afterFacts.RoomId != commandEnvelope.RoomId ||
            beforeFacts.RoundId != afterFacts.RoundId || (snapshot != null && snapshot.RoundId != afterFacts.RoundId))
            Fail(id + ":RECOVERY_ROOM_ROUND_MISMATCH");
        if (commandEnvelope.Scope!.RoomId != commandEnvelope.RoomId) Fail(id + ":SCOPE_ROOM_MISMATCH");
        if (beforeFacts.StateVersion != command.ExpectedStateVersion.ToString(CultureInfo.InvariantCulture)) Fail(id + ":COMMAND_BASELINE_VERSION_MISMATCH");
        if (beforeFacts.ControlEpochs[commandEnvelope.SeatId] != checked((long)commandEnvelope.ControlEpoch)) Fail(id + ":COMMAND_CONTROL_EPOCH_MISMATCH");
        if (commandEnvelope.SessionId.Length == 0 || commandEnvelope.RoomId.Length == 0 || commandEnvelope.Scope!.AccountId.Length == 0) Fail(id + ":COMMAND_BINDING_IDENTITY_MISSING");
        if (command.Type == CommandType.SubmitTileMark || command.Type == CommandType.SubmitSleeveBloom || command.Type == CommandType.SubmitBeamSwap)
        {
            string canonicalRound = ReadCanonicalString(parsed, CommandPayloadFields.RoundIdField, "roundId");
            string canonicalDecision = ReadCanonicalString(parsed, CommandPayloadFields.DecisionIdField, "decisionId");
            if (canonicalRound != beforeFacts.RoundId || canonicalRound != afterFacts.RoundId ||
                (!string.IsNullOrEmpty(command.ExpectedRoundId) && canonicalRound != command.ExpectedRoundId))
                Fail(id + ":CANONICAL_ROUND_MISMATCH commandExpected=" + command.ExpectedRoundId + " before=" + beforeFacts.RoundId + " after=" + afterFacts.RoundId + " canonical=" + canonicalRound);
            if (string.IsNullOrWhiteSpace(canonicalDecision) ||
                (!string.IsNullOrEmpty(command.ExpectedDecisionId) && canonicalDecision != command.ExpectedDecisionId))
                Fail(id + ":CANONICAL_DECISION_MISMATCH commandExpected=" + command.ExpectedDecisionId + " canonical=" + canonicalDecision);
            if (parsed.RequireUInt64(CommandPayloadFields.ControlEpochField, "controlEpoch") != commandEnvelope.ControlEpoch) Fail(id + ":CANONICAL_EPOCH_MISMATCH");
        }
        if (receipt.EvaluatedStateVersion != command.ExpectedStateVersion) Fail(id + ":RECEIPT_EVALUATED_VERSION_MISMATCH");
        if (receipt.Status == WireReceiptStatus.Applied)
        {
            if (!receipt.HasCommandVersion || !receipt.HasEventSeq) Fail(id + ":APPLIED_RECEIPT_VERSION_MISSING");
            if (afterFacts.StateVersion != receipt.CommandVersion.ToString(CultureInfo.InvariantCulture)) Fail(id + ":RECOVERY_NOT_EXACT_COMMIT_STATE");
            if (snapshot == null || update == null) Fail(id + ":ACCEPTED_PRIVATE_PUBLICATION_MISSING");
            else if (snapshot.StateVersion != receipt.CommandVersion || snapshot.EventSeq < receipt.EventSeq) Fail(id + ":PUBLICATION_PRECEDES_COMMIT");
            var facts = afterFacts.Requests.SingleOrDefault(r => r.RequestId == commandEnvelope.RequestId && r.ScopeId == commandEnvelope.Scope!.ScopeId && r.AccountId == commandEnvelope.Scope.AccountId);
            string rawPayloadSha256 = Sha(payload);
            if (facts == null || facts.Status != "APPLIED" || !Eq(facts.PayloadSha256, rawPayloadSha256) || facts.CommandVersion != receipt.CommandVersion.ToString(CultureInfo.InvariantCulture) || facts.EventSeq != receipt.EventSeq.ToString(CultureInfo.InvariantCulture))
                Fail(id + ":RECOVERY_RECEIPT_NOT_EXACT actual=" + (facts == null ? "missing" : facts.Status + "/" + facts.PayloadSha256 + "/" + facts.CommandVersion + "/" + facts.EventSeq) + " expected=APPLIED/" + rawPayloadSha256 + "/" + receipt.CommandVersion + "/" + receipt.EventSeq);
            EventType expectedEvent = command.Type == CommandType.SubmitSleeveBloomChoice
                ? EventType.SleeveBloomResolved
                : command.Type == CommandType.SubmitBeamSwapChoice
                    ? EventType.BeamSwapResolved
                    : EventType.CheatPointsChanged;
            if (update == null || !update.VisibleEvents.Any(e => e.CommandVersion == receipt.CommandVersion &&
                e.EventSeq > ulong.Parse(beforeFacts.EventSeq, CultureInfo.InvariantCulture) && e.EventSeq <= receipt.EventSeq &&
                e.EventType == expectedEvent && e.Visibility == EventVisibility.Private)) Fail(id + ":PUBLICATION_MISSING_MATCHED_PRIVATE_EVENT");
        }
        else if (receipt.Status == WireReceiptStatus.Rejected)
        {
            if (receipt.HasCommandVersion || receipt.HasEventSeq) Fail(id + ":REJECTED_RECEIPT_HAS_COMMIT_VERSION");
            if (beforeFacts.StateVersion != afterFacts.StateVersion || beforeFacts.EventSeq != afterFacts.EventSeq || beforeFacts.CheatVersion != afterFacts.CheatVersion ||
                beforeFacts.CheatFingerprint != afterFacts.CheatFingerprint || beforeFacts.Ledger.EntriesSha256 != afterFacts.Ledger.EntriesSha256 ||
                beforeFacts.Physical.TilesSha256 != afterFacts.Physical.TilesSha256)
                Fail(id + ":REJECTED_RECOVERY_SIDE_EFFECT");
            if (!PhysicalMap(beforeSave, beforeCheat).OrderBy(kv => kv.Key).SequenceEqual(PhysicalMap(afterSave, afterCheat).OrderBy(kv => kv.Key))) Fail(id + ":REJECTED_ENTITY_SIDE_EFFECT");
        }
        else Fail(id + ":RECEIPT_NOT_DEFINITIVE");

        return new ActionCapture(id, commandEnvelope, receiptEnvelope, publicationEnvelope, payload, canonical,
            beforeFacts, afterFacts, beforeSave, beforeCheat, afterSave, afterCheat, receipt.ReasonCode);
    }

    private static void VerifyAuthorized(IReadOnlyList<ActionCapture> a)
    {
        CommandType[] kinds = { CommandType.SubmitTileMark, CommandType.SubmitSleeveBloom, CommandType.SubmitSleeveBloomChoice, CommandType.SubmitBeamSwap, CommandType.SubmitBeamSwapChoice };
        for (int i = 0; i < a.Count; i++)
        {
            ActionCapture x = a[i];
            if (x.Command.Command.Type != kinds[i]) Fail(x.Id + ":COMMAND_KIND");
            if (x.Receipt.Receipt.Status != WireReceiptStatus.Applied) Fail(x.Id + ":NOT_APPLIED");
            if (x.Command.Command.ExpectedRoundId != x.After.RoundId) Fail(x.Id + ":ROUND_BINDING");
        }
        long[] balances = { 119, 109, 109, 99, 99 };
        int[] ledgerEntries = { 1, 2, 2, 3, 3 };
        long[] beforeBalances = { 120, 119, 109, 109, 99 };
        int[] beforeLedgerEntries = { 0, 1, 2, 2, 3 };
        for (int i = 0; i < a.Count; i++)
        {
            LocalTableRecoveryFacts facts = a[i].After;
            if (facts.Ledger.Balances[0] != balances[i] || facts.Ledger.EntryCount != ledgerEntries[i]) Fail(a[i].Id + ":LEDGER_OUTCOME");
            if (a[i].Before.Ledger.Balances[0] != beforeBalances[i] || a[i].Before.Ledger.EntryCount != beforeLedgerEntries[i]) Fail(a[i].Id + ":LEDGER_BASELINE");
            if (facts.Physical.TileCount != 136 || facts.Physical.UniqueCount != 136) Fail(a[i].Id + ":PHYSICAL_136");
            var priv = a[i].Publication!.PlayerUpdate.Snapshot.PrivateView;
            var cheat = priv.Cheat ?? throw new InvalidDataException(a[i].Id + ":PRIVATE_CHEAT_MISSING");
            if (cheat.CheatPoints != balances[i] || cheat.LedgerEntries.Count != ledgerEntries[i]) Fail(a[i].Id + ":PRIVATE_PUBLICATION_LEDGER");
            if (priv.Seat != 0 || a[i].Command.SeatId != 0) Fail(a[i].Id + ":AUTHORIZED_SEAT_NOT_DEALER");
            if (i == 0 && (cheat.LedgerEntries[0].Amount != -1 || cheat.LedgerEntries[0].ReasonCode != "TILE_MARK")) Fail("TILE_MARK_LEDGER_ENTRY");
            if (i == 1 && (cheat.LedgerEntries[1].Amount != -10 || cheat.LedgerEntries[1].ReasonCode != "SLEEVE_BLOOM")) Fail("SLEEVE_LEDGER_ENTRY");
            if (i == 3 && (cheat.LedgerEntries[2].Amount != -10 || cheat.LedgerEntries[2].ReasonCode != "BEAM_SWAP")) Fail("BEAM_LEDGER_ENTRY");
            if (a[i].Publication!.PlayerUpdate.Snapshot.Cursor.ProjectionSeq == 0) Fail(a[i].Id + ":CURSOR_NOT_ADVANCED");
            if (!Eq(a[i].Command.Scope!.AccountId, a[0].Command.Scope!.AccountId) || !Eq(a[i].Command.Scope.ScopeId, a[0].Command.Scope.ScopeId) ||
                !Eq(a[i].Command.SessionId, a[0].Command.SessionId) || !Eq(a[i].Command.RoomId, a[0].Command.RoomId) || a[i].Command.SeatId != a[0].Command.SeatId)
                Fail(a[i].Id + ":POSITIVE_IDENTITY_DRIFT");
            Facts.Add(new { id = a[i].Id, requestId = a[i].Command.RequestId, commandExpectedRoundId = a[i].Command.Command.ExpectedRoundId,
                commandExpectedDecisionId = a[i].Command.Command.ExpectedDecisionId, accountId = a[i].Command.Scope!.AccountId,
                scopeId = a[i].Command.Scope!.ScopeId, roomId = a[i].Command.RoomId, sessionId = a[i].Command.SessionId,
                seat = a[i].Command.SeatId, roundId = facts.RoundId, payloadSha256 = a[i].Canonical,
                stateVersionBefore = a[i].Before.StateVersion, stateVersionAfter = facts.StateVersion,
                eventSeq = a[i].Receipt.Receipt.EventSeq, cursorStreamVersion = a[i].Publication!.Cursor!.StreamVersion,
                cursorProjectionSeq = a[i].Publication!.Cursor!.ProjectionSeq, ledgerEntries = facts.Ledger.EntryCount,
                balance = facts.Ledger.Balances[0], physicalCount = facts.Physical.TileCount, uniqueEntities = facts.Physical.UniqueCount });
        }
        if (a.Select(x => x.Command.RequestId).Distinct(StringComparer.Ordinal).Count() != 5) Fail("POSITIVE_REQUESTS_NOT_DISTINCT");
        ulong previousProjectionSeq = 0;
        for (int i = 0; i < a.Count; i++)
        {
            ulong projectionSeq = a[i].Publication!.PlayerUpdate.Cursor.ProjectionSeq;
            if (projectionSeq <= previousProjectionSeq) Fail(a[i].Id + ":PLAYER_CURSOR_NOT_MONOTONIC");
            previousProjectionSeq = projectionSeq;
        }
        uint[] marked = CanonicalPayloadParser.Parse(a[0].Payload).RequireRepeatedUInt32(CommandPayloadFields.TileMarkTileIds, "tileIds").ToArray();
        uint[] publishedMarkRows = a[0].Publication!.PlayerUpdate.Snapshot.PrivateView.Cheat.TileMark.Groups.SelectMany(g => g.Tiles).Select(t => t.TileId).ToArray();
        uint[] publishedMarks = publishedMarkRows.Distinct().ToArray();
        if (publishedMarks.Length != publishedMarkRows.Length) Fail("TILE_MARK_DUPLICATE_PRIVATE_ENTITY");
        if (!marked.OrderBy(v => v).SequenceEqual(publishedMarks.OrderBy(v => v))) Fail("TILE_MARK_PRIVATE_ENTITY_SET_MISMATCH");
        if (a[2].Before.StateVersion != a[1].After.StateVersion || a[4].Before.StateVersion != a[3].After.StateVersion) Fail("CHOICE_NOT_BOUND_TO_IMMEDIATE_OFFER_COMMIT");
        VerifySecondExchange(a[1], a[2]);
        VerifyBeamExchange(a[3], a[4]);
    }

    private static void VerifySecondExchange(ActionCapture activation, ActionCapture x)
    {
        CanonicalPayloadDocument p = CanonicalPayloadParser.Parse(x.Payload);
        if (p.RequireUInt32(CommandPayloadFields.Choice, "choice") != (uint)SleeveBloomChoice.Second) Fail("SECOND_CHOICE_NOT_TYPED");
        WireSleeveBloomView? offerView = activation.Publication!.PlayerUpdate.Snapshot.PrivateView.Cheat.SleeveBloom;
        if (offerView == null || !offerView.HasFirstTileId || !offerView.HasSecondTileId) { Fail("SECOND_OFFER_ENTITIES_MISSING"); return; }
        string offerId = ReadCanonicalString(p, CommandPayloadFields.OfferId, "offerId");
        if (!Eq(offerId, offerView.OfferId)) Fail("SECOND_OFFER_ID_MISMATCH actual=" + offerId + " expected=" + offerView.OfferId);
        Dictionary<int, string> before = PhysicalMap(x.BeforeSave, x.BeforeCheat);
        Dictionary<int, string> after = PhysicalMap(x.AfterSave, x.AfterCheat);
        if (before.Count != 136 || after.Count != 136) Fail("SECOND_PHYSICAL_MAP_INCOMPLETE");
        string firstBefore = before[(int)offerView.FirstTileId];
        string secondBefore = before[(int)offerView.SecondTileId];
        string firstAfter = after[(int)offerView.FirstTileId];
        string secondAfter = after[(int)offerView.SecondTileId];
        if (!firstBefore.StartsWith("HAND|0|", StringComparison.Ordinal) || !firstAfter.StartsWith("WALL|", StringComparison.Ordinal)) Fail("SECOND_FIRST_ENTITY_DESTINATION");
        if (!secondBefore.StartsWith("WALL|", StringComparison.Ordinal) || !secondAfter.StartsWith("HAND|0|", StringComparison.Ordinal)) Fail("SECOND_SELECTED_ENTITY_DESTINATION");
        if (x.Before.Ledger.EntryCount != activation.After.Ledger.EntryCount || x.After.Ledger.EntryCount != activation.After.Ledger.EntryCount) Fail("SECOND_CHOICE_RECHARGED");
    }

    private static void VerifyBeamExchange(ActionCapture activation, ActionCapture x)
    {
        CanonicalPayloadDocument p = CanonicalPayloadParser.Parse(x.Payload);
        if (p.RequireUInt32(CommandPayloadFields.Choice, "choice") != (uint)BeamSwapChoice.Swap) Fail("SWAP_CHOICE_NOT_TYPED");
        if (!p.HasField(CommandPayloadFields.HandTileId) || !p.HasField(CommandPayloadFields.TargetSlot)) Fail("SWAP_ENTITIES_NOT_TYPED");
        Dictionary<int, string> before = PhysicalMap(x.BeforeSave, x.BeforeCheat);
        Dictionary<int, string> after = PhysicalMap(x.AfterSave, x.AfterCheat);
        if (before.Count != 136 || after.Count != 136 || before.OrderBy(kv => kv.Key).SequenceEqual(after.OrderBy(kv => kv.Key))) Fail("SWAP_PHYSICAL_CHANGE_MISSING");
        uint handId = p.RequireUInt32(CommandPayloadFields.HandTileId, "handTileId");
        uint target = p.RequireUInt32(CommandPayloadFields.TargetSlot, "targetSlot");
        WireBeamSwapView? offer = activation.Publication!.PlayerUpdate.Snapshot.PrivateView.Cheat.BeamSwap;
        if (offer == null) { Fail("SWAP_OFFER_MISSING"); return; }
        if (!offer.SelectableHandTileIds.Contains(handId)) Fail("SWAP_HAND_ENTITY_NOT_SELECTABLE");
        WireBeamSwapCandidateView[] candidates = offer.Candidates.Where(c => c.Slot == target && c.SlotIndex == target).ToArray();
        if (candidates.Length != 1 || !candidates[0].HasTileId) { Fail("SWAP_TARGET_NOT_UNIQUE_OFFER_ENTITY"); return; }
        uint targetId = candidates[0].TileId;
        string handBefore = before[(int)handId], handAfter = after[(int)handId];
        string targetBefore = before[(int)targetId], targetAfter = after[(int)targetId];
        if (!handBefore.StartsWith("HAND|0|", StringComparison.Ordinal) || !targetAfter.StartsWith("HAND|0|", StringComparison.Ordinal)) Fail("SWAP_HAND_ENTITY_DESTINATION");
        if (handAfter != targetBefore || targetAfter != handBefore) Fail("SWAP_TARGET_ENTITY_SOURCE_DESTINATION");
        if (x.Before.Ledger.EntryCount != activation.After.Ledger.EntryCount || x.After.Ledger.EntryCount != activation.After.Ledger.EntryCount) Fail("SWAP_CHOICE_RECHARGED");
    }

    private static void VerifyNegative(IReadOnlyList<ActionCapture> a)
    {
        if (a.Select(x => x.Command.RequestId).Distinct(StringComparer.Ordinal).Count() != 2) Fail("NEGATIVE_REQUESTS_NOT_DISTINCT");
        if (a.Select(x => x.Canonical).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 2) Fail("NEGATIVE_PAYLOADS_NOT_DISTINCT");
        if (a[0].Command.Command.Type != CommandType.SubmitTileMark || a[1].Command.Command.Type != CommandType.SubmitSleeveBloom) Fail("NEGATIVE_COMMAND_SHAPES");
        if (a[0].Receipt.Receipt.Status != WireReceiptStatus.Rejected || a[0].ReasonCode != ProtocolErrorCode.DecisionMismatch) Fail("UNAUTHORIZED_REASON_NOT_AUTHORITY_DECISION_MISMATCH");
        if (a[1].Receipt.Receipt.Status != WireReceiptStatus.Rejected || a[1].ReasonCode != ProtocolErrorCode.InsufficientFunds) Fail("INSUFFICIENT_FUNDS_REASON_NOT_CONTRACT_CODE");
        if (a[0].Before.Ledger.Balances[0] != 120 || a[0].Before.Ledger.EntryCount != 0) Fail("UNAUTHORIZED_BASELINE_NOT_CLEAN");
        if (a[1].Before.Ledger.Balances[0] >= 10) Fail("INSUFFICIENT_FUNDS_BASELINE_NOT_INSUFFICIENT");
        foreach (ActionCapture x in a)
        {
            if (x.ReasonCode == ProtocolErrorCode.MalformedMessage || x.ReasonCode == ProtocolErrorCode.InternalError) Fail(x.Id + ":PROTOCOL_ERROR_IS_NOT_BUSINESS_REJECTION");
            Facts.Add(new { id = x.Id, requestId = x.Command.RequestId, payloadSha256 = x.Canonical, reason = x.ReasonCode,
                stateVersionBefore = x.Before.StateVersion, stateVersionAfter = x.After.StateVersion,
                eventSeqBefore = x.Before.EventSeq, eventSeqAfter = x.After.EventSeq,
                ledgerSha256Before = x.Before.Ledger.EntriesSha256, ledgerSha256After = x.After.Ledger.EntriesSha256,
                physicalSha256Before = x.Before.Physical.TilesSha256, physicalSha256After = x.After.Physical.TilesSha256 });
        }
    }

    private static Dictionary<int, string> PhysicalMap(byte[] save, byte[] cheat)
    {
        return LocalPlayerActionScenario.ReadPhysicalAudit(save, cheat).Split('\n').ToDictionary(
            line => int.Parse(line.Split('|')[1], CultureInfo.InvariantCulture), line => string.Join("|", line.Split('|').Skip(2)), EqualityComparer<int>.Default);
    }

    private static Envelope Decode(byte[] bytes, string label)
    {
        WireDecodeResult result = WireCodec.Decode(bytes);
        if (result.Status != WireDecodeStatus.Ok || result.Envelope == null) throw new InvalidDataException(label + ":WIRE_DECODE_" + result.Status);
        WireCodec.EnsureStrict(result.Envelope);
        return result.Envelope;
    }

    private static JsonDocument ParseJson(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return JsonDocument.Parse(new ReadOnlyMemory<byte>(bytes, 3, bytes.Length - 3));
        return JsonDocument.Parse(bytes);
    }

    private static byte[] ReadRaw(string root, JsonElement item)
    {
        string path = ResolveCaptureFile(root, RequiredString(item, "path"));
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length == 0) Fail("RAW_FILE_EMPTY:" + Path.GetFileName(path));
        if (!Eq(Sha(bytes), RequiredString(item, "sha256"))) Fail("RAW_FILE_SHA_MISMATCH:" + Path.GetFileName(path));
        return bytes;
    }

    private static string ResolveCaptureFile(string root, string relative)
    {
        if (Path.IsPathRooted(relative)) throw new InvalidDataException("CAPTURE_PATH_ABSOLUTE");
        string[] components = relative.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (components.Length == 0 || components.Any(part => part == ".." || part == ".")) throw new InvalidDataException("CAPTURE_PATH_TRAVERSAL");
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if ((File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("CAPTURE_ROOT_REPARSE_POINT");
        string full = Path.GetFullPath(Path.Combine(fullRoot, relative));
        string prefix = fullRoot + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("CAPTURE_PATH_ESCAPE");
        string current = fullRoot;
        foreach (string part in Path.GetRelativePath(fullRoot, full).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            if (!File.Exists(current) && !Directory.Exists(current)) throw new FileNotFoundException("CAPTURE_FILE_MISSING", current);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("CAPTURE_REPARSE_POINT");
        }
        if (!File.Exists(full)) throw new FileNotFoundException("CAPTURE_FILE_MISSING", full);
        return full;
    }

    private static JsonElement Property(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement value) ? value : throw new InvalidDataException("MISSING_" + name);
    private static string RequiredString(JsonElement e, string name) => Property(e, name).ValueKind == JsonValueKind.String ? Property(e, name).GetString() ?? throw new InvalidDataException("NULL_" + name) : throw new InvalidDataException("TYPE_" + name);
    private static int Int(JsonElement e, string name) => Property(e, name).GetInt32();
    private static string Required(Dictionary<string, string> d, string key) => d.TryGetValue(key, out string? v) && !string.IsNullOrWhiteSpace(v) ? v : throw new ArgumentException("Missing " + key);
    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static string ReadCanonicalString(CanonicalPayloadDocument payload, ushort fieldNumber, string fieldName)
    {
        byte[] encoded = System.Text.Encoding.UTF8.GetBytes(payload.RequireString(fieldNumber, fieldName));
        if (encoded.Length < 4) throw new InvalidDataException("Canonical string field has no inner length prefix: " + fieldName + ".");
        uint count = BitConverter.ToUInt32(encoded, 0);
        if (count != encoded.Length - 4) throw new InvalidDataException("Canonical string field has an invalid inner length: " + fieldName + ".");
        return new System.Text.UTF8Encoding(false, true).GetString(encoded, 4, encoded.Length - 4);
    }
    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string SafeFullPath(string value) { try { return string.IsNullOrWhiteSpace(value) ? "" : Path.GetFullPath(value); } catch { return ""; } }
    private static void Fail(string code) => Errors.Add(code);

    private sealed class ActionCapture
    {
        public ActionCapture(string id, Envelope command, Envelope receipt, Envelope? publication, byte[] payload, string canonical,
            LocalTableRecoveryFacts before, LocalTableRecoveryFacts after, byte[] beforeSave, byte[] beforeCheat, byte[] afterSave, byte[] afterCheat, string reasonCode)
        { Id = id; Command = command; Receipt = receipt; Publication = publication; Payload = payload; Canonical = canonical; Before = before; After = after; BeforeSave = beforeSave; BeforeCheat = beforeCheat; AfterSave = afterSave; AfterCheat = afterCheat; ReasonCode = reasonCode; }
        public string Id { get; }
        public Envelope Command { get; }
        public Envelope Receipt { get; }
        public Envelope? Publication { get; }
        public byte[] Payload { get; }
        public string Canonical { get; }
        public LocalTableRecoveryFacts Before { get; }
        public LocalTableRecoveryFacts After { get; }
        public byte[] BeforeSave { get; }
        public byte[] BeforeCheat { get; }
        public byte[] AfterSave { get; }
        public byte[] AfterCheat { get; }
        public string ReasonCode { get; }
    }
}
