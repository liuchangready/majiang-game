#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mahjong.Client;
using Mahjong.Contracts;
using Mahjong.Contracts.Wire;
using Mahjong.LocalHost;
using Mahjong.Unity.Platform;
using Mahjong.Unity.Table;
using Mahjong.Unity.UI;
using Mahjong.Unity.UI.Session;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using WireCodec = Mahjong.Contracts.Wire.WireCodec;
using WireEnvelope = Mahjong.Contracts.Wire.Envelope;
using WireMessageType = Mahjong.Contracts.Wire.MessageType;

namespace Mahjong.Unity.App
{
    /// <summary>
    /// Strictly command-line opt-in Windows Player probe for C04-G3. Without
    /// <c>-c04-player-cheats &lt;evidence-dir&gt;</c> this type allocates no directory,
    /// host, channel, thread, coroutine or scheduler work: the runtime initializer returns
    /// before even creating its GameObject.
    ///
    /// <para>
    /// The probe loads the real Table scene and drives the real <see cref="TablePresenter"/>
    /// controls. Every cheat, discard and pass therefore travels through
    /// TablePresenter -> ITableCommandSink -> TableSession.SubmitAsync -> LocalTableChannel
    /// -> the real LocalHost authority -> the protocol -> the client view. It never calls
    /// Domain/TableActor to perform a target action and never hand-writes a view, receipt,
    /// event, ledger or success line.
    /// </para>
    /// </summary>
    public sealed class C04PlayerCheatProbe : MonoBehaviour
    {
        private const string OptInArg = "-c04-player-cheats";
        private const string CaseArg = "-c04-player-cheats-case";
        private const string SourceHeadArg = "-c04-source-head";
        private const string BuildCommitArg = "-c04-build-commit";
        private const string PlayerPathArg = "-c04-player-path";
        private const string UnityVersionArg = "-c04-unity-version";
        private const string PackageManifestArg = "-c04-package-manifest";

        private string _evidenceDir = string.Empty;
        private string _caseName = string.Empty;
        private string _sourceHead = string.Empty;
        private string _buildCommit = string.Empty;
        private string _playerPath = string.Empty;
        private string _unityVersion = string.Empty;
        private string _packageManifestSha256 = string.Empty;

        private MainThreadDispatcher? _dispatcher;
        private AppComposition? _composition;
        private SessionCoordinator? _coordinator;
        private TablePresenter? _presenter;
        private C04PlayerCheatChannelFactory? _factory;
        private C04RawFrameCapture? _capture;
        private string _saveDirectory = string.Empty;

        private readonly List<C04ReceiptRecord> _receipts = new List<C04ReceiptRecord>();
        private readonly List<C04RawAction> _rawActions = new List<C04RawAction>();
        private bool _waitOk;
        private C04ReceiptRecord? _lastReceipt;
        private int _receiptMark;
        private LocalTableRecoveryFacts? _taskFacts;
        private LocalTableRecoveryCapture? _lastCapture;
        private byte[]? _welcomeFrame;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
            string[] args = Environment.GetCommandLineArgs();
            string? evidenceDir = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == OptInArg && i + 1 < args.Length)
                {
                    evidenceDir = args[i + 1];
                    break;
                }
            }

            // Strict opt-in boundary. Normal launches must not create a directory, host,
            // channel, thread, coroutine or scheduled callback.
            if (string.IsNullOrWhiteSpace(evidenceDir))
            {
                return;
            }

            var go = new GameObject("C04PlayerCheatProbe");
            UnityEngine.Object.DontDestroyOnLoad(go);
            C04PlayerCheatProbe probe = go.AddComponent<C04PlayerCheatProbe>();
            probe.Initialize(evidenceDir!, ReadArg(args, CaseArg, "authorized"), args);
        }

        private static string ReadArg(string[] args, string name, string fallback)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == name && i + 1 < args.Length)
                {
                    return args[i + 1];
                }
            }

            return fallback;
        }

        private static string? ReadOptionalArg(string[] args, string name)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == name && i + 1 < args.Length)
                {
                    return args[i + 1];
                }
            }

            return null;
        }

        /// <summary>
        /// Exact raw association used by both the Player probe and its deterministic fixture.
        /// It never decodes into a fabricated success: missing command, receipt or accepted
        /// PlayerUpdate returns false, and the caller must abort the case.
        /// </summary>
        public static bool TryAssociateRaw(
            byte[][] sentFrames,
            byte[][] receivedFrames,
            string requestId,
            ulong commandVersion,
            bool requirePublication,
            out byte[]? command,
            out byte[]? receipt,
            out byte[]? publication)
        {
            command = null;
            receipt = null;
            publication = null;
            if (sentFrames == null || receivedFrames == null || string.IsNullOrEmpty(requestId))
            {
                return false;
            }

            for (int i = 0; i < sentFrames.Length; i++)
            {
                WireDecodeResult decoded = WireCodec.Decode(sentFrames[i]);
                if (decoded.IsOk && decoded.Envelope != null &&
                    decoded.Envelope.MessageType == WireMessageType.Command &&
                    string.Equals(decoded.Envelope.RequestId, requestId, StringComparison.Ordinal))
                {
                    command = sentFrames[i];
                    break;
                }
            }

            for (int i = 0; i < receivedFrames.Length; i++)
            {
                WireDecodeResult decoded = WireCodec.Decode(receivedFrames[i]);
                if (decoded.IsOk && decoded.Envelope != null &&
                    decoded.Envelope.MessageType == WireMessageType.Receipt &&
                    string.Equals(decoded.Envelope.RequestId, requestId, StringComparison.Ordinal))
                {
                    receipt = receivedFrames[i];
                    break;
                }
            }

            if (requirePublication && commandVersion > 0)
            {
                for (int i = 0; i < receivedFrames.Length; i++)
                {
                    WireDecodeResult decoded = WireCodec.Decode(receivedFrames[i]);
                    if (decoded.IsOk && decoded.Envelope != null &&
                        decoded.Envelope.MessageType == WireMessageType.PlayerUpdate &&
                        decoded.Envelope.PlayerUpdate != null &&
                        decoded.Envelope.PlayerUpdate.VisibleEvents.Any(
                            ev => ev.CommandVersion == commandVersion))
                    {
                        publication = receivedFrames[i];
                        break;
                    }
                }
            }

            return command != null &&
                   receipt != null &&
                   (!requirePublication || publication != null);
        }

        private void Initialize(string evidenceDir, string caseName, string[] args)
        {
            _evidenceDir = Path.GetFullPath(evidenceDir);
            _caseName = caseName;
            _sourceHead = ReadOptionalArg(args, SourceHeadArg) ?? string.Empty;
            _buildCommit = ReadOptionalArg(args, BuildCommitArg) ?? string.Empty;
            _playerPath = ReadOptionalArg(args, PlayerPathArg) ?? string.Empty;
            _unityVersion = ReadOptionalArg(args, UnityVersionArg) ?? Application.unityVersion;
            string? packageManifest = ReadOptionalArg(args, PackageManifestArg);
            if (!string.IsNullOrWhiteSpace(packageManifest) && File.Exists(packageManifest))
            {
                _packageManifestSha256 = Sha256(File.ReadAllBytes(packageManifest));
            }

            Directory.CreateDirectory(_evidenceDir);
            Directory.CreateDirectory(Path.Combine(_evidenceDir, "raw"));
        }

        private void Start()
        {
            StartCoroutine(Run());
        }

        private void Update()
        {
            _dispatcher?.Update(512, 16.0f);
        }

        private IEnumerator Run()
        {
            Application.runInBackground = true;
            UiThemeProvider.EnsureLineBreakingRules();
            UiInputLifecycle.ResetForTest();

            // Let the production Boot scene create AppRoot/Menu. The probe never uses
            // that composition's session; it owns a separate, isolated composition.
            float bootDeadline = Time.realtimeSinceStartup + 15f;
            while (AppRoot.Instance == null && Time.realtimeSinceStartup < bootDeadline)
            {
                yield return null;
            }

            if (string.Equals(_caseName, "negative", StringComparison.OrdinalIgnoreCase))
            {
                yield return RunNegativeCase();
            }
            else
            {
                yield return RunAuthorizedCaseV2();
            }

            int exitCode = _caseFailed ? 1 : 0;
            yield return null;
            yield return null;
            Debug.Log("C04_PROBE_EXIT: case=" + _caseName + " exit=" + exitCode);
            Application.Quit(exitCode);
        }

        // ---------------------------------------------------------------- cases

        private bool _caseFailed;
        private C04Evidence? _evidence;

        private IEnumerator RunAuthorizedCase()
        {
            var evidence = CreateEvidence("authorized", C04PlayerCheatScenario.AuthorizedThreeCheats);
            _evidence = evidence;
            try
            {
                yield return OpenComposition(C04PlayerCheatScenario.AuthorizedThreeCheats);
                if (_presenter == null || _coordinator == null || _factory == null)
                {
                    Check(evidence, "G3-SETUP", false, "real TablePresenter bound to a real local session", "setup failed");
                    yield break;
                }

                yield return WaitInitialView();
                if (!_waitOk)
                {
                    Check(evidence, "G3-SETUP", false, "confirmed private cheat view", "timeout");
                    yield break;
                }

                LocalPlayerCheatScenarioFacts facts = _factory.Facts;
                Check(
                    evidence,
                    "G3-05-INITIAL-136",
                    facts.EntityIds.Count == 136 &&
                    facts.EntityIds.Distinct().Count() == 136 &&
                    facts.EntityIds.SequenceEqual(Enumerable.Range(0, 136).Select(v => (uint)v)),
                    "unique physical entities 0..135",
                    "count=" + facts.EntityIds.Count + " distinct=" + facts.EntityIds.Distinct().Count());

                yield return CaptureFacts();
                if (!_waitOk || _taskFacts == null)
                {
                    Check(evidence, "G3-05-CAPTURE-BEFORE", false, "authority physical facts", "capture failed");
                    yield break;
                }

                evidence.before = C04AuthorityFacts.From(_taskFacts);
                CheckPhysical(evidence, "G3-05-BEFORE", _taskFacts);
                Check(evidence, "G3-04-BEFORE", _taskFacts.Ledger.EntryCount == 0 && _taskFacts.Ledger.Balances[0] == 120,
                    "initial points 120, no entries", "entries=" + _taskFacts.Ledger.EntryCount + " points=" + _taskFacts.Ledger.Balances[0]);

                // ---- G3-01 TileMark: BLOCKED at the accepted Player UI entry.
                // The confirmed projection sets PrivateCheatView.TileMark to null while
                // owner.MarkGroups.Count == 0 (TableProjectionContractMapper.MapOwnerCheat),
                // and TableCommandIntentFactory.CreateSubmitTileMark rejects a null TileMark
                // with TILE_MARK_UNAVAILABLE. The first mark therefore cannot be initiated
                // from TablePresenter. This is a real product gap, not a probe workaround;
                // the .NET C04 authority test still proves the server accepts a first mark.
                bool firstMarkUnavailable = _presenter.LatestView!.Own!.Cheat!.TileMark == null &&
                                            !FindButton("TileMarkButton").gameObject.activeSelf;
                Check(
                    evidence,
                    "G3-01-UI-ENTRY",
                    firstMarkUnavailable,
                    "reproduce missing first TileMark UI entry",
                    "tileMark=" + (_presenter.LatestView.Own.Cheat.TileMark == null ? "null" : "present") +
                    " buttonActive=" + FindButton("TileMarkButton").gameObject.activeSelf);
                evidence.blockedRows = new[] { "G3-01" };
                evidence.observations.Add(new C04Observation
                {
                    kind = "blocked",
                    detail = "G3-01 first TileMark cannot be initiated from TablePresenter: " +
                             "PrivateCheatView.TileMark is null until an owner mark group exists, and " +
                             "TableCommandIntentFactory.CreateSubmitTileMark rejects that null. " +
                             "Minimal fix needs TablePresenter.cs plus the projection that supplies " +
                             "selectable hand tiles before the first group.",
                    tileIds = string.Join(",", facts.ProposedTileMarkTileIds),
                    cheatPoints = 120,
                    offerId = string.Empty,
                    eventTypes = "BLOCKED"
                });

                // ---- discard the fixed safe north honour and advance one turn.
                yield return DiscardEntity(120u);
                if (!_waitOk)
                {
                    Check(evidence, "G3-ADVANCE-1", false, "discard accepted", "discard failed");
                    yield break;
                }

                yield return AdvanceToNextDealerTurn();
                if (!_waitOk)
                {
                    Check(evidence, "G3-ADVANCE-1", false, "next dealer cheat window", "timeout");
                    yield break;
                }

                // ---- G3-02 SleeveBloom: BLOCKED at the accepted Player UI entry.
                // After the dealer discard the authority keeps the off-turn window id, and
                // TablePresenter.OpenSleeveBloomDialog classifies any non-empty WindowId as
                // OFF_TURN even on the dealer's own turn. The immediate activation is therefore
                // unreachable from the presenter, and the off-turn intent is not authorized on
                // the current seat. This is a real product gap, not a probe workaround.
                PlayerTableViewState afterAdvance = _presenter.LatestView!;
                bool staleWindow = !string.IsNullOrEmpty(afterAdvance.Own!.Cheat!.WindowId);
                Check(
                    evidence,
                    "G3-02-UI-ENTRY",
                    staleWindow,
                    "reproduce stale off-turn window at the dealer turn",
                    "window=" + afterAdvance.Own.Cheat.WindowId + " offer=" + (afterAdvance.Own.Cheat.ImmediateSleeveBloom != null));
                evidence.blockedRows = new[] { "G3-01", "G3-02" };
                evidence.observations.Add(new C04Observation
                {
                    kind = "blocked",
                    detail = "G3-02 immediate SleeveBloom cannot be reached from TablePresenter: " +
                             "the discard window id remains set and OpenSleeveBloomDialog always " +
                             "chooses SubmitSleeveBloomOffTurn when WindowId is non-empty. The " +
                             "off-turn command is not authorized on the dealer's own turn. " +
                             "Minimal fix needs TablePresenter.cs plus the window lifecycle.",
                    tileIds = string.Empty,
                    cheatPoints = 120,
                    offerId = string.Empty,
                    eventTypes = "BLOCKED"
                });

                // ---- G3-03 BeamSwap (cost 10, SWAP really moves entities)
                PlayerTableViewState beforeBeam = _presenter.LatestView!;
                ulong beamVersionBefore = beforeBeam.Identity.StateVersion;
                _receiptMark = _receipts.Count;
                ClickButton("BeamSwapButton");
                yield return WaitReceipt(CommandKind.SubmitBeamSwap, 15f);
                C04ReceiptRecord? beamReceipt = _lastReceipt;
                if (beamReceipt == null)
                {
                    Check(evidence, "G3-03", false, "SubmitBeamSwap Applied", "no matching receipt");
                    yield break;
                }

                evidence.requests.Add(beamReceipt.ToRecord(beamVersionBefore, 0));
                Check(evidence, "G3-03-RECEIPT", beamReceipt.Status == "APPLIED",
                    "SubmitBeamSwap Applied", beamReceipt.Status + "/" + beamReceipt.Reason);
                yield return WaitView(v => v.Own?.Cheat?.CheatPoints == 110 &&
                                          v.Own.Cheat.Ledger.Count == 1 &&
                                          v.Own.Cheat.BeamSwap != null, 15f);
                if (!_waitOk)
                {
                    Check(evidence, "G3-03", false, "BeamSwap offer at 99 points", "timeout");
                    yield break;
                }

                PlayerTableViewState beamOffered = _presenter.LatestView!;
                evidence.requests[evidence.requests.Count - 1].stateVersionAfter = unchecked((long)beamOffered.Identity.StateVersion);
                PlayerBeamSwapState beamOffer = beamOffered.Own!.Cheat.BeamSwap!;
                Check(evidence, "G3-03-OFFER", beamOffer.Candidates.Count > 0 && beamOffer.SelectableHandTileIds.Count > 0,
                    "real wall candidates and selectable hand tiles",
                    "candidates=" + beamOffer.Candidates.Count + " hand=" + beamOffer.SelectableHandTileIds.Count);
                Check(evidence, "G3-04-BEAM-FEE",
                    beamOffered.Own.Cheat.Ledger.Count == 1 &&
                    beamOffered.Own.Cheat.Ledger[0].Amount == -10 &&
                    beamOffered.Own.Cheat.Ledger[0].BalanceAfter == 110 &&
                    string.Equals(beamOffered.Own.Cheat.Ledger[0].ReasonCode, "BEAM_SWAP", StringComparison.Ordinal),
                    "one -10 entry, balance 110", DescribeLedger(beamOffered.Own.Cheat));

                yield return CaptureFacts();
                if (!_waitOk || _taskFacts == null)
                {
                    Check(evidence, "G3-03-AUTHORITY", false, "authority capture after BeamSwap offer", "capture failed");
                    yield break;
                }

                LocalTableRecoveryFacts afterBeamOffer = _taskFacts;
                evidence.afterBeam = C04AuthorityFacts.From(afterBeamOffer);
                CheckPhysical(evidence, "G3-05-AFTER-BEAM", afterBeamOffer);

                PlayerBeamSwapCandidateState target = beamOffer.Candidates[0];
                uint handTile = beamOffer.SelectableHandTileIds[0];
                ulong choiceBeamVersionBefore = beamOffered.Identity.StateVersion;
                _receiptMark = _receipts.Count;
                ClickButton("BeamSwapButton");
                yield return null;
                ClickButton("BeamSlot_0");
                yield return null;
                ClickHandEntity(handTile);
                yield return null;
                _receiptMark = _receipts.Count;
                ClickButton("ConfirmBeamSwapButton");
                yield return WaitReceipt(CommandKind.SubmitBeamSwapChoice, 15f);
                C04ReceiptRecord? beamChoice = _lastReceipt;
                if (beamChoice == null)
                {
                    Check(evidence, "G3-03-CHOICE", false, "SubmitBeamSwapChoice Applied", "no matching receipt");
                    yield break;
                }

                evidence.requests.Add(beamChoice.ToRecord(choiceBeamVersionBefore, 0));
                Check(evidence, "G3-03-CHOICE-RECEIPT", beamChoice.Status == "APPLIED",
                    "SubmitBeamSwapChoice(SWAP) Applied", beamChoice.Status + "/" + beamChoice.Reason);
                yield return WaitView(v => v.Own?.Cheat?.BeamSwap == null, 15f);
                if (!_waitOk)
                {
                    Check(evidence, "G3-03-CHOICE", false, "BeamSwap offer resolved", "timeout");
                    yield break;
                }

                PlayerTableViewState afterBeamChoice = _presenter.LatestView!;
                evidence.requests[evidence.requests.Count - 1].stateVersionAfter = unchecked((long)afterBeamChoice.Identity.StateVersion);
                Check(evidence, "G3-03-CHOICE-FEE",
                    afterBeamChoice.Own!.Cheat.CheatPoints == 110 &&
                    afterBeamChoice.Own.Cheat.Ledger.Count == 1,
                    "choice does not charge again",
                    "points=" + afterBeamChoice.Own.Cheat.CheatPoints + " entries=" + afterBeamChoice.Own.Cheat.Ledger.Count);

                yield return CaptureFacts();
                if (!_waitOk || _taskFacts == null)
                {
                    Check(evidence, "G3-03-PHYSICAL", false, "authority capture after BeamSwap choice", "capture failed");
                    yield break;
                }

                LocalTableRecoveryFacts final = _taskFacts;
                evidence.afterFinal = C04AuthorityFacts.From(final);
                CheckPhysical(evidence, "G3-05-FINAL", final);
                Check(evidence, "G3-03-PHYSICAL-MOVED",
                    !string.Equals(afterBeamOffer.Physical.TilesSha256, final.Physical.TilesSha256, StringComparison.Ordinal),
                    "physical hand/wall swap",
                    afterBeamOffer.Physical.TilesSha256 + " vs " + final.Physical.TilesSha256);
                Check(evidence, "G3-04-FINAL-LEDGER",
                    final.Ledger.EntryCount == 1 &&
                    final.Ledger.Balances[0] == 110 &&
                    final.Ledger.Balances[1] == 120 &&
                    final.Ledger.Balances[2] == 120 &&
                    final.Ledger.Balances[3] == 120 &&
                    final.Ledger.Points == 470,
                    "one entry; seat0=110; total=470",
                    "entries=" + final.Ledger.EntryCount + " balances=" + string.Join(",", final.Ledger.Balances));
                CheckRequestApplied(evidence, "G3-03-REQUEST", final, beamReceipt);
                CheckRequestApplied(evidence, "G3-03-CHOICE-REQUEST", final, beamChoice);

                evidence.observations.Add(new C04Observation
                {
                    kind = "ledger",
                    detail = "seat0 ledger after all three cheats",
                    tileIds = string.Join(",", final.Ledger.Balances),
                    cheatPoints = final.Ledger.Balances[0],
                    offerId = string.Empty,
                    eventTypes = "TILE_MARK,SLEEVE_BLOOM,BEAM_SWAP"
                });
            }

            finally
            {
                evidence.finishedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                evidence.status = _caseFailed ? "FAIL" : "PASS";
                CloseComposition();
                WriteEvidence(evidence);
            }
        }

        private IEnumerator RunAuthorizedCaseV2()
        {
            var evidence = CreateEvidence("authorized", C04PlayerCheatScenario.AuthorizedThreeCheats);
            _evidence = evidence;
            try
            {
                yield return OpenComposition(C04PlayerCheatScenario.AuthorizedThreeCheats);
                if (_presenter == null || _coordinator == null || _factory == null)
                {
                    Check(evidence, "G3-SETUP", false, "real TablePresenter bound to a real local session", "setup failed");
                    yield break;
                }

                yield return WaitInitialView();
                if (!_waitOk)
                {
                    Check(evidence, "G3-SETUP", false, "confirmed private cheat view", "timeout");
                    yield break;
                }

                LocalPlayerCheatScenarioFacts facts = _factory.Facts;
                Check(evidence, "G3-05-INITIAL-136",
                    facts.EntityIds.Count == 136 &&
                    facts.EntityIds.Distinct().Count() == 136 &&
                    facts.EntityIds.SequenceEqual(Enumerable.Range(0, 136).Select(v => (uint)v)),
                    "unique physical entities 0..135",
                    "count=" + facts.EntityIds.Count + " distinct=" + facts.EntityIds.Distinct().Count());

                yield return CaptureFacts();
                if (!_waitOk || _taskFacts == null)
                {
                    Check(evidence, "G3-05-BEFORE", false, "authority physical facts", "capture failed");
                    yield break;
                }

                evidence.before = C04AuthorityFacts.From(_taskFacts);
                CheckPhysical(evidence, "G3-05-BEFORE", _taskFacts);
                Check(evidence, "G3-04-BEFORE", _taskFacts.Ledger.EntryCount == 0 && _taskFacts.Ledger.Balances[0] == 120,
                    "initial points 120, no entries",
                    "entries=" + _taskFacts.Ledger.EntryCount + " points=" + _taskFacts.Ledger.Balances[0]);

                uint[] markTiles = facts.ProposedTileMarkTileIds.ToArray();
                ulong markVersion = _presenter.LatestView!.Identity.StateVersion;
                int markSentStart = _capture?.SentCount ?? 0;
                int markReceivedStart = _capture?.ReceivedCount ?? 0;
                LocalTableRecoveryCapture? markBefore = _lastCapture;
                _receiptMark = _receipts.Count;
                ClickButton("TileMarkButton");
                yield return null;
                for (int i = 0; i < markTiles.Length; i++)
                {
                    ClickHandEntity(markTiles[i]);
                    yield return null;
                }

                ClickButton("ConfirmTileMarkButton");
                yield return WaitReceipt(CommandKind.SubmitTileMark, 15f);
                C04ReceiptRecord? mark = _lastReceipt;
                if (mark == null)
                {
                    Check(evidence, "G3-01", false, "SubmitTileMark Applied", "no receipt");
                    yield break;
                }

                evidence.requests.Add(mark.ToRecord(markVersion, 0));
                Check(evidence, "G3-01-RECEIPT", mark.Status == "APPLIED", "Applied", mark.Status + "/" + mark.Reason);
                yield return WaitView(v => v.Own?.Cheat?.CheatPoints == 119 && v.Own.Cheat.Ledger.Count == 1 &&
                                          v.Own.Cheat.TileMark != null, 15f);
                if (!_waitOk)
                {
                    Check(evidence, "G3-01", false, "119 points, one ledger entry and a private mark group", "timeout");
                    yield break;
                }

                PlayerTableViewState marked = _presenter.LatestView!;
                evidence.requests[evidence.requests.Count - 1].stateVersionAfter = unchecked((long)marked.Identity.StateVersion);
                bool markGroup = marked.Own!.Cheat.TileMark!.Groups.Any(
                    group => group.Tiles.Select(t => t.TileId).SequenceEqual(markTiles));
                Check(evidence, "G3-01-LEDGER",
                    marked.Own.Cheat.Ledger.Count == 1 &&
                    marked.Own.Cheat.Ledger[0].Amount == -1 &&
                    marked.Own.Cheat.Ledger[0].BalanceAfter == 119 &&
                    string.Equals(marked.Own.Cheat.Ledger[0].ReasonCode, "TILE_MARK", StringComparison.Ordinal),
                    "one -1 entry, balance 119, TILE_MARK",
                    DescribeLedger(marked.Own.Cheat));
                Check(evidence, "G3-01-MARK", markGroup,
                    "private mark group exactly " + string.Join(",", markTiles),
                    DescribeMarks(marked.Own.Cheat));

                yield return CaptureFacts();
                if (!_waitOk || _taskFacts == null)
                {
                    Check(evidence, "G3-01-AUTHORITY", false, "authority capture after TileMark", "capture failed");
                    yield break;
                }

                LocalTableRecoveryFacts afterMark = _taskFacts;
                evidence.afterMark = C04AuthorityFacts.From(afterMark);
                CheckPhysical(evidence, "G3-05-AFTER-MARK", afterMark);
                RecordRawAction("tile-mark", mark, markSentStart, markReceivedStart, markBefore, true);

                yield return DiscardEntity(120u);
                if (!_waitOk)
                {
                    Check(evidence, "G3-ADVANCE-1", false, "discard accepted", "discard failed");
                    yield break;
                }

                yield return AdvanceToNextDealerTurn();
                if (!_waitOk)
                {
                    Check(evidence, "G3-ADVANCE-1", false, "next dealer window", "timeout");
                    yield break;
                }

                // The legal turns above advanced the authority. Capture the exact command
                // baseline now; reusing the mark post-state here produces a stale baseline.
                yield return CaptureFacts();
                if (!_waitOk || _lastCapture == null)
                {
                    Check(evidence, "G3-02-BASELINE", false, "authority capture at SleeveBloom command baseline", "capture failed");
                    yield break;
                }

                PlayerTableViewState beforeBloom = _presenter.LatestView!;
                uint drawnBefore = beforeBloom.Own!.DrawnTileId!.Value;
                ulong bloomVersion = beforeBloom.Identity.StateVersion;
                int bloomSentStart = _capture?.SentCount ?? 0;
                int bloomReceivedStart = _capture?.ReceivedCount ?? 0;
                LocalTableRecoveryCapture? bloomBefore = _lastCapture;
                _receiptMark = _receipts.Count;
                ClickButton("SleeveBloomButton");
                yield return WaitReceipt(CommandKind.SubmitSleeveBloom, 15f);
                C04ReceiptRecord? bloom = _lastReceipt;
                if (bloom == null)
                {
                    Check(evidence, "G3-02", false, "SubmitSleeveBloom Applied", "no receipt");
                    yield break;
                }

                evidence.requests.Add(bloom.ToRecord(bloomVersion, 0));
                Check(evidence, "G3-02-RECEIPT", bloom.Status == "APPLIED", "Applied", bloom.Status + "/" + bloom.Reason);
                yield return WaitView(v => v.Own?.Cheat?.CheatPoints == 109 &&
                                          v.Own.Cheat.Ledger.Count == 2 &&
                                          v.Own.Cheat.ImmediateSleeveBloom != null, 15f);
                if (!_waitOk)
                {
                    Check(evidence, "G3-02", false, "109 points and a SleeveBloom offer", "timeout");
                    yield break;
                }

                PlayerTableViewState offered = _presenter.LatestView!;
                evidence.requests[evidence.requests.Count - 1].stateVersionAfter = unchecked((long)offered.Identity.StateVersion);
                PlayerSleeveBloomState offer = offered.Own!.Cheat.ImmediateSleeveBloom!;
                uint second = offer.SecondTileId!.Value;
                ulong bloomChoiceVersion = offered.Identity.StateVersion;
                yield return CaptureFacts();
                if (!_waitOk || _lastCapture == null)
                {
                    Check(evidence, "G3-02-AUTHORITY", false, "authority capture after SleeveBloom activation", "capture failed");
                    yield break;
                }

                RecordRawAction("sleeve-bloom", bloom, bloomSentStart, bloomReceivedStart, bloomBefore, true);
                LocalTableRecoveryCapture? bloomChoiceBefore = _lastCapture;
                int bloomChoiceSentStart = _capture?.SentCount ?? 0;
                int bloomChoiceReceivedStart = _capture?.ReceivedCount ?? 0;
                _receiptMark = _receipts.Count;
                ClickButton("SleeveBloomButton");
                yield return null;
                ClickButton("SleeveSwapButton");
                yield return WaitReceipt(CommandKind.SubmitSleeveBloomChoice, 15f);
                C04ReceiptRecord? bloomChoice = _lastReceipt;
                if (bloomChoice == null)
                {
                    Check(evidence, "G3-02-CHOICE", false, "SubmitSleeveBloomChoice Applied", "no receipt");
                    yield break;
                }

                evidence.requests.Add(bloomChoice.ToRecord(bloomChoiceVersion, 0));
                Check(evidence, "G3-02-CHOICE-RECEIPT", bloomChoice.Status == "APPLIED",
                    "Applied", bloomChoice.Status + "/" + bloomChoice.Reason);
                yield return WaitView(v => v.Own?.Cheat?.ImmediateSleeveBloom == null, 15f);
                if (!_waitOk)
                {
                    Check(evidence, "G3-02-CHOICE", false, "SleeveBloom offer resolved", "timeout");
                    yield break;
                }

                PlayerTableViewState bloomed = _presenter.LatestView!;
                evidence.requests[evidence.requests.Count - 1].stateVersionAfter = unchecked((long)bloomed.Identity.StateVersion);
                Check(evidence, "G3-02-CHOICE-FEE",
                    bloomed.Own!.Cheat.CheatPoints == 109 && bloomed.Own.Cheat.Ledger.Count == 2,
                    "choice does not charge again",
                    "points=" + bloomed.Own.Cheat.CheatPoints + " entries=" + bloomed.Own.Cheat.Ledger.Count);
                Check(evidence, "G3-02-SECOND-SWAP",
                    bloomed.Own.DrawnTileId == second && drawnBefore != second,
                    "drawn tile becomes " + second,
                    "drawn=" + bloomed.Own.DrawnTileId);

                yield return CaptureFacts();
                if (!_waitOk || _taskFacts == null)
                {
                    Check(evidence, "G3-02-AUTHORITY", false, "authority capture after SleeveBloom", "capture failed");
                    yield break;
                }

                LocalTableRecoveryFacts afterBloom = _taskFacts;
                evidence.afterBloomChoice = C04AuthorityFacts.From(afterBloom);
                CheckPhysical(evidence, "G3-05-AFTER-BLOOM", afterBloom);
                Check(evidence, "G3-02-PHYSICAL-MOVED",
                    !string.Equals(afterMark.Physical.TilesSha256, afterBloom.Physical.TilesSha256, StringComparison.Ordinal),
                    "physical hand/wall swap",
                    afterMark.Physical.TilesSha256 + " vs " + afterBloom.Physical.TilesSha256);
                RecordRawAction(
                    "sleeve-second",
                    bloomChoice,
                    bloomChoiceSentStart,
                    bloomChoiceReceivedStart,
                    bloomChoiceBefore,
                    true);

                uint discardTile = bloomed.Own.DrawnTileId ?? bloomed.Own.ClosedTiles[0];
                yield return DiscardEntity(discardTile);
                if (!_waitOk)
                {
                    Check(evidence, "G3-ADVANCE-2", false, "discard accepted", "discard failed");
                    yield break;
                }

                yield return AdvanceToNextDealerTurn();
                if (!_waitOk)
                {
                    Check(evidence, "G3-ADVANCE-2", false, "third dealer window", "timeout");
                    yield break;
                }

                yield return CaptureFacts();
                if (!_waitOk || _lastCapture == null)
                {
                    Check(evidence, "G3-03-AUTHORITY", false, "authority capture before BeamSwap", "capture failed");
                    yield break;
                }

                PlayerTableViewState beforeBeam = _presenter.LatestView!;
                ulong beamVersion = beforeBeam.Identity.StateVersion;
                int beamSentStart = _capture?.SentCount ?? 0;
                int beamReceivedStart = _capture?.ReceivedCount ?? 0;
                LocalTableRecoveryCapture? beamBefore = _lastCapture;
                _receiptMark = _receipts.Count;
                ClickButton("BeamSwapButton");
                yield return WaitReceipt(CommandKind.SubmitBeamSwap, 15f);
                C04ReceiptRecord? beam = _lastReceipt;
                if (beam == null)
                {
                    Check(evidence, "G3-03", false, "SubmitBeamSwap Applied", "no receipt");
                    yield break;
                }

                evidence.requests.Add(beam.ToRecord(beamVersion, 0));
                Check(evidence, "G3-03-RECEIPT", beam.Status == "APPLIED", "Applied", beam.Status + "/" + beam.Reason);
                yield return WaitView(v => v.Own?.Cheat?.CheatPoints == 99 &&
                                          v.Own.Cheat.Ledger.Count == 3 &&
                                          v.Own.Cheat.BeamSwap != null, 15f);
                if (!_waitOk)
                {
                    Check(evidence, "G3-03", false, "99 points and a BeamSwap offer", "timeout");
                    yield break;
                }

                PlayerTableViewState beamOffered = _presenter.LatestView!;
                evidence.requests[evidence.requests.Count - 1].stateVersionAfter = unchecked((long)beamOffered.Identity.StateVersion);
                PlayerBeamSwapState beamOffer = beamOffered.Own!.Cheat.BeamSwap!;
                uint handTile = beamOffer.SelectableHandTileIds[0];
                ulong beamChoiceVersion = beamOffered.Identity.StateVersion;
                yield return CaptureFacts();
                if (!_waitOk || _lastCapture == null)
                {
                    Check(evidence, "G3-03-AUTHORITY", false, "authority capture after BeamSwap activation", "capture failed");
                    yield break;
                }

                RecordRawAction("beam-swap", beam, beamSentStart, beamReceivedStart, beamBefore, true);
                LocalTableRecoveryCapture? beamChoiceBefore = _lastCapture;
                int beamChoiceSentStart = _capture?.SentCount ?? 0;
                int beamChoiceReceivedStart = _capture?.ReceivedCount ?? 0;
                _receiptMark = _receipts.Count;
                ClickButton("BeamSwapButton");
                yield return null;
                ClickButton("BeamSlot_0");
                yield return null;
                ClickHandEntity(handTile);
                yield return null;
                ClickButton("ConfirmBeamSwapButton");
                yield return WaitReceipt(CommandKind.SubmitBeamSwapChoice, 15f);
                C04ReceiptRecord? beamChoice = _lastReceipt;
                if (beamChoice == null)
                {
                    Check(evidence, "G3-03-CHOICE", false, "SubmitBeamSwapChoice Applied", "no receipt");
                    yield break;
                }

                evidence.requests.Add(beamChoice.ToRecord(beamChoiceVersion, 0));
                Check(evidence, "G3-03-CHOICE-RECEIPT", beamChoice.Status == "APPLIED",
                    "Applied", beamChoice.Status + "/" + beamChoice.Reason);
                yield return WaitView(v => v.Own?.Cheat?.BeamSwap == null, 15f);
                if (!_waitOk)
                {
                    Check(evidence, "G3-03-CHOICE", false, "BeamSwap offer resolved", "timeout");
                    yield break;
                }

                PlayerTableViewState afterBeam = _presenter.LatestView!;
                evidence.requests[evidence.requests.Count - 1].stateVersionAfter = unchecked((long)afterBeam.Identity.StateVersion);
                Check(evidence, "G3-03-CHOICE-FEE",
                    afterBeam.Own!.Cheat.CheatPoints == 99 && afterBeam.Own.Cheat.Ledger.Count == 3,
                    "choice does not charge again",
                    "points=" + afterBeam.Own.Cheat.CheatPoints + " entries=" + afterBeam.Own.Cheat.Ledger.Count);

                yield return CaptureFacts();
                if (!_waitOk || _taskFacts == null)
                {
                    Check(evidence, "G3-03-AUTHORITY", false, "authority capture after BeamSwap", "capture failed");
                    yield break;
                }

                LocalTableRecoveryFacts final = _taskFacts;
                evidence.afterFinal = C04AuthorityFacts.From(final);
                CheckPhysical(evidence, "G3-05-FINAL", final);
                RecordRawAction(
                    "beam-swap-choice",
                    beamChoice,
                    beamChoiceSentStart,
                    beamChoiceReceivedStart,
                    beamChoiceBefore,
                    true);
                Check(evidence, "G3-03-PHYSICAL-MOVED",
                    !string.Equals(afterBloom.Physical.TilesSha256, final.Physical.TilesSha256, StringComparison.Ordinal),
                    "physical hand/wall swap",
                    afterBloom.Physical.TilesSha256 + " vs " + final.Physical.TilesSha256);
                Check(evidence, "G3-04-FINAL-LEDGER",
                    final.Ledger.EntryCount == 3 &&
                    final.Ledger.Balances[0] == 99 &&
                    final.Ledger.Balances[1] == 120 &&
                    final.Ledger.Balances[2] == 120 &&
                    final.Ledger.Balances[3] == 120 &&
                    final.Ledger.Points == 459,
                    "three entries; seat0=99; total=459",
                    "entries=" + final.Ledger.EntryCount + " balances=" + string.Join(",", final.Ledger.Balances));

                CheckRequestApplied(evidence, "G3-01-REQUEST", final, mark);
                CheckRequestApplied(evidence, "G3-02-REQUEST", final, bloom);
                CheckRequestApplied(evidence, "G3-02-CHOICE-REQUEST", final, bloomChoice);
                CheckRequestApplied(evidence, "G3-03-REQUEST", final, beam);
                CheckRequestApplied(evidence, "G3-03-CHOICE-REQUEST", final, beamChoice);
            }
            finally
            {
                evidence.finishedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                evidence.status = _caseFailed ? "FAIL" : "PASS";
                CloseComposition();
                WriteEvidence(evidence);
            }
        }
        private IEnumerator RunNegativeCase()
        {
            var evidence = CreateEvidence("negative", C04PlayerCheatScenario.InsufficientBalance);
            _evidence = evidence;
            try
            {
                yield return OpenComposition(C04PlayerCheatScenario.InsufficientBalance);
                if (_presenter == null || _coordinator == null || _factory == null)
                {
                    Check(evidence, "G3-06-SETUP", false, "real TablePresenter bound to a real local session", "setup failed");
                    yield break;
                }

                yield return WaitInitialView();
                if (!_waitOk)
                {
                    Check(evidence, "G3-06-SETUP", false, "confirmed private cheat view", "timeout");
                    yield break;
                }

                LocalPlayerCheatScenarioFacts facts = _factory.Facts;
                Check(evidence, "G3-06-INITIAL-136",
                    facts.EntityIds.Count == 136 &&
                    facts.EntityIds.Distinct().Count() == 136,
                    "unique physical entities", "count=" + facts.EntityIds.Count);

                yield return CaptureFacts();
                if (!_waitOk || _taskFacts == null)
                {
                    Check(evidence, "G3-06-CAPTURE", false, "authority physical facts", "capture failed");
                    yield break;
                }

                LocalTableRecoveryFacts beforeUnauthorized = _taskFacts;
                evidence.before = C04AuthorityFacts.From(beforeUnauthorized);
                CheckPhysical(evidence, "G3-06-BEFORE-136", beforeUnauthorized);

                // ---- Unauthorized: the UI never offers an invalid decision, so this negative
                // enters at the same real ITableCommandSink boundary with a distinct request id
                // and a payload whose decision id is not the pending authorization.
                string unauthorizedId = "c04-g3-unauthorized-" + Guid.NewGuid().ToString("N");
                BusinessPayload unauthorizedPayload = CommandPayloadFactory.SubmitTileMark(
                    _presenter.LatestView!.Identity.RoundId,
                    "c04-no-such-decision",
                    _presenter.LatestView.Own!.Decision!.ControlEpoch,
                    facts.ProposedTileMarkTileIds.ToArray());
                var unauthorizedCommand = new ProtocolCommand(
                    CommandKind.SubmitTileMark,
                    unauthorizedPayload,
                    new RequestId(unauthorizedId),
                    new RequestScopeId(_presenter.LatestView.Identity.ScopeId),
                    new StateVersion(_presenter.LatestView.Identity.StateVersion),
                    new RoundId(_presenter.LatestView.Identity.RoundId),
                    decisionId: new DecisionId("c04-no-such-decision"),
                    controlEpoch: new ControlEpoch(_presenter.LatestView.Own.Decision.ControlEpoch));
                int unauthorizedSentStart = _capture?.SentCount ?? 0;
                int unauthorizedReceivedStart = _capture?.ReceivedCount ?? 0;
                LocalTableRecoveryCapture? unauthorizedBefore = _lastCapture;
                var unauthorizedCompletion = new TaskCompletionSource<CommandSubmission>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _ = SubmitAsync(unauthorizedCommand, unauthorizedCompletion);
                yield return WaitTask(unauthorizedCompletion.Task, 15f);
                if (!_waitOk)
                {
                    Check(evidence, "G3-06-UNAUTHORIZED", false, "unauthorized submission reaches authority", "submit failed");
                    yield break;
                }

                yield return WaitReceiptById(unauthorizedId, 15f);
                C04ReceiptRecord? unauthorizedReceipt = _lastReceipt;
                if (unauthorizedReceipt == null)
                {
                    Check(evidence, "G3-06-UNAUTHORIZED", false, "Rejected receipt for " + unauthorizedId, "no receipt");
                    yield break;
                }

                unauthorizedReceipt.DecisionId = "c04-no-such-decision";
                unauthorizedReceipt.ControlEpoch = _presenter.LatestView!.Own!.Decision!.ControlEpoch;
                evidence.requests.Add(unauthorizedReceipt.ToRecord(
                    _presenter.LatestView.Identity.StateVersion,
                    _presenter.LatestView.Identity.StateVersion,
                    unauthorizedPayload));
                Check(evidence, "G3-06-UNAUTHORIZED-REJECTED",
                    unauthorizedReceipt.Status == "REJECTED",
                    "REJECTED",
                    unauthorizedReceipt.Status + "/" + unauthorizedReceipt.Reason);

                yield return new WaitForSecondsRealtime(0.25f);
                yield return CaptureFacts();
                if (!_waitOk || _taskFacts == null)
                {
                    Check(evidence, "G3-06-UNAUTHORIZED-ZERO", false, "authority capture after unauthorized", "capture failed");
                    yield break;
                }

                LocalTableRecoveryFacts afterUnauthorized = _taskFacts;
                evidence.afterUnauthorized = C04AuthorityFacts.From(afterUnauthorized);
                CheckPhysical(evidence, "G3-06-UNAUTHORIZED-136", afterUnauthorized);
                CheckZeroDelta(evidence, "G3-06-UNAUTHORIZED-ZERO", beforeUnauthorized, afterUnauthorized);
                CheckRequestRejected(evidence, "G3-06-UNAUTHORIZED-REQUEST", afterUnauthorized, unauthorizedReceipt);
                RecordRawAction(
                    "unauthorized",
                    unauthorizedReceipt,
                    unauthorizedSentStart,
                    unauthorizedReceivedStart,
                    unauthorizedBefore,
                    false);

                // ---- Legal positive controls: twelve real SleeveBloom activations (10 each)
                // bring the balance from 120 to 0, so the same rule with a sufficient balance
                // is demonstrated before the underfunded attempt.
                yield return DiscardEntity(120u);
                if (!_waitOk)
                {
                    Check(evidence, "G3-06-ADVANCE", false, "discard accepted", "discard failed");
                    yield break;
                }

                yield return AdvanceToNextDealerTurn();
                if (!_waitOk)
                {
                    Check(evidence, "G3-06-ADVANCE", false, "next dealer window", "timeout");
                    yield break;
                }

                for (int turn = 0; turn < 12; turn++)
                {
                    PlayerTableViewState beforeBloom = _presenter.LatestView!;
                    ulong versionBefore = beforeBloom.Identity.StateVersion;
                    _receiptMark = _receipts.Count;
                ClickButton("SleeveBloomButton");
                    yield return WaitReceipt(CommandKind.SubmitSleeveBloom, 15f);
                    C04ReceiptRecord? bloom = _lastReceipt;
                    if (bloom == null || bloom.Status != "APPLIED")
                    {
                        Check(evidence, "G3-06-POSITIVE-CONTROL", false, "authorized SleeveBloom #" + turn, bloom?.Status ?? "no receipt");
                        yield break;
                    }

                    evidence.requests.Add(bloom.ToRecord(versionBefore, 0));
                    long expected = 120 - (10 * (turn + 1));
                    yield return WaitView(v => v.Own?.Cheat?.CheatPoints == expected &&
                                              v.Own.Cheat.ImmediateSleeveBloom != null, 15f);
                    if (!_waitOk)
                    {
                        Check(evidence, "G3-06-POSITIVE-CONTROL", false, "SleeveBloom offer at " + expected, "timeout");
                        yield break;
                    }

                    PlayerSleeveBloomState offer = _presenter.LatestView!.Own!.Cheat.ImmediateSleeveBloom!;
                    ulong choiceVersion = _presenter.LatestView.Identity.StateVersion;
                    _receiptMark = _receipts.Count;
                ClickButton("SleeveBloomButton");
                    yield return null;
                    _receiptMark = _receipts.Count;
                    ClickButton("SleeveStoreButton");
                    yield return WaitReceipt(CommandKind.SubmitSleeveBloomChoice, 15f);
                    C04ReceiptRecord? choice = _lastReceipt;
                    if (choice == null || choice.Status != "APPLIED")
                    {
                        Check(evidence, "G3-06-POSITIVE-CONTROL", false, "SleeveBloom choice #" + turn, choice?.Status ?? "no receipt");
                        yield break;
                    }

                    evidence.requests.Add(choice.ToRecord(choiceVersion, 0));
                    yield return WaitView(v => v.Own?.Cheat?.ImmediateSleeveBloom == null, 15f);
                    if (!_waitOk)
                    {
                        Check(evidence, "G3-06-POSITIVE-CONTROL", false, "offer resolved #" + turn, "timeout");
                        yield break;
                    }

                    uint tile = _presenter.LatestView!.Own!.DrawnTileId ?? _presenter.LatestView.Own.ClosedTiles[0];
                    yield return DiscardEntity(tile);
                    if (!_waitOk)
                    {
                        Check(evidence, "G3-06-ADVANCE", false, "discard accepted #" + turn, "discard failed");
                        yield break;
                    }

                    if (turn + 1 < 12)
                    {
                        yield return AdvanceToNextDealerTurn();
                        if (!_waitOk)
                        {
                            Check(evidence, "G3-06-ADVANCE", false, "next dealer window #" + turn, "timeout");
                            yield break;
                        }
                    }
                }

                yield return WaitView(v => v.Own?.Cheat?.CheatPoints == 0, 15f);
                if (!_waitOk)
                {
                    Check(evidence, "G3-06-POSITIVE-CONTROL", false, "balance 0 after controls", "timeout");
                    yield break;
                }

                yield return AdvanceToNextDealerTurn();
                if (!_waitOk)
                {
                    Check(evidence, "G3-06-INSUFFICIENT", false, "next dealer window for underfunded attempt", "timeout");
                    yield break;
                }

                yield return CaptureFacts();
                if (!_waitOk || _taskFacts == null)
                {
                    Check(evidence, "G3-06-INSUFFICIENT", false, "authority capture before underfunded attempt", "capture failed");
                    yield break;
                }

                LocalTableRecoveryFacts beforeInsufficient = _taskFacts;
                evidence.beforeInsufficient = C04AuthorityFacts.From(beforeInsufficient);
                CheckPhysical(evidence, "G3-06-INSUFFICIENT-BEFORE-136", beforeInsufficient);
                Check(evidence, "G3-06-INSUFFICIENT-PRECONDITION",
                    beforeInsufficient.Ledger.Balances[0] == 0 && beforeInsufficient.Ledger.EntryCount == 12,
                    "balance 0, twelve legal entries",
                    "balance=" + beforeInsufficient.Ledger.Balances[0] + " entries=" + beforeInsufficient.Ledger.EntryCount);

                PlayerTableViewState underfunded = _presenter.LatestView!;
                ulong underfundedVersion = underfunded.Identity.StateVersion;
                int insufficientSentStart = _capture?.SentCount ?? 0;
                int insufficientReceivedStart = _capture?.ReceivedCount ?? 0;
                LocalTableRecoveryCapture? insufficientBefore = _lastCapture;
                _receiptMark = _receipts.Count;
                ClickButton("SleeveBloomButton");
                yield return WaitReceipt(CommandKind.SubmitSleeveBloom, 15f);
                C04ReceiptRecord? rejected = _lastReceipt;
                if (rejected == null)
                {
                    Check(evidence, "G3-06-INSUFFICIENT", false, "Rejected underfunded SleeveBloom", "no receipt");
                    yield break;
                }

                evidence.requests.Add(rejected.ToRecord(underfundedVersion, 0));
                Check(evidence, "G3-06-INSUFFICIENT-REJECTED",
                    rejected.Status == "REJECTED" &&
                    rejected.Reason.IndexOf("INSUFFICIENT", StringComparison.OrdinalIgnoreCase) >= 0,
                    "REJECTED with insufficient-funds reason",
                    rejected.Status + "/" + rejected.Reason);

                yield return new WaitForSecondsRealtime(0.25f);
                yield return CaptureFacts();
                if (!_waitOk || _taskFacts == null)
                {
                    Check(evidence, "G3-06-INSUFFICIENT", false, "authority capture after underfunded rejection", "capture failed");
                    yield break;
                }

                LocalTableRecoveryFacts afterInsufficient = _taskFacts;
                evidence.afterInsufficient = C04AuthorityFacts.From(afterInsufficient);
                CheckPhysical(evidence, "G3-06-INSUFFICIENT-136", afterInsufficient);
                CheckZeroDelta(evidence, "G3-06-INSUFFICIENT-ZERO", beforeInsufficient, afterInsufficient);
                CheckRequestRejected(evidence, "G3-06-INSUFFICIENT-REQUEST", afterInsufficient, rejected);
                RecordRawAction(
                    "insufficient-funds",
                    rejected,
                    insufficientSentStart,
                    insufficientReceivedStart,
                    insufficientBefore,
                    false);
                Check(evidence, "G3-06-DISTINCT-REQUESTS",
                    !string.Equals(unauthorizedId, rejected.RequestId, StringComparison.Ordinal) &&
                    !string.Equals(unauthorizedReceipt.PayloadSha256, rejected.PayloadSha256, StringComparison.Ordinal),
                    "two independent request ids and payloads",
                    unauthorizedId + "/" + unauthorizedReceipt.PayloadSha256 + " vs " + rejected.RequestId + "/" + rejected.PayloadSha256);

                evidence.observations.Add(new C04Observation
                {
                    kind = "rejections",
                    detail = "unauthorized decision and insufficient balance rejected by the real authority",
                    tileIds = string.Empty,
                    cheatPoints = afterInsufficient.Ledger.Balances[0],
                    offerId = string.Empty,
                    eventTypes = "REJECTED"
                });
            }

            finally
            {
                evidence.finishedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                evidence.status = _caseFailed ? "FAIL" : "PASS";
                CloseComposition();
                WriteEvidence(evidence);
            }
        }

        // ---------------------------------------------------------------- composition / scene

        private IEnumerator OpenComposition(C04PlayerCheatScenario kind)
        {
            string caseRoot = Path.Combine(_evidenceDir, "state", _caseName);
            Directory.CreateDirectory(caseRoot);
            UiInputLifecycle.ResetForTest();
            var paths = new UnityAppPaths(caseRoot);
            _capture = new C04RawFrameCapture();
            _factory = new C04PlayerCheatChannelFactory(
                paths,
                kind,
                "c04-probe-" + _caseName,
                capture: _capture);
            _saveDirectory = _factory.SaveDirectory;
            _dispatcher = new MainThreadDispatcher(512);
            _composition = new AppComposition(
                new AppConfig(),
                _dispatcher,
                themeProvider: null,
                channelFactory: _factory,
                persistentDataPath: caseRoot);

            var openCompletion = new TaskCompletionSource<SessionCoordinator>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _ = OpenSessionAsync(openCompletion);
            yield return WaitTask(openCompletion.Task, 30f);
            if (!_waitOk)
            {
                Check(_evidence!, "G3-SESSION", false, "real local session opens", "open failed");
                yield break;
            }

            _coordinator = openCompletion.Task.Result;
            _coordinator.ReceiptResolved += OnReceiptResolved;

            AsyncOperation load = SceneManager.LoadSceneAsync("Table", LoadSceneMode.Single);
            while (load != null && !load.isDone)
            {
                yield return null;
            }

            // Let TableSceneBinding.Start run before rebinding the presenter to the probe's
            // own composition. The production binding points at AppRoot's unused composition.
            yield return null;
            yield return null;
            yield return null;

            _presenter = UnityEngine.Object.FindFirstObjectByType<TablePresenter>();
            if (_presenter == null)
            {
                Check(_evidence!, "G3-TABLE", false, "real TablePresenter in Table scene", "not found");
                yield break;
            }

            _presenter.Initialize(null, _coordinator, _coordinator);
            yield return null;
            yield return null;
            _waitOk = true;
        }

        private async Task OpenSessionAsync(TaskCompletionSource<SessionCoordinator> completion)
        {
            try
            {
                SessionCoordinator coordinator = await _composition!.OpenLocalSessionAsync(CancellationToken.None);
                completion.TrySetResult(coordinator);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }

        private async Task SubmitAsync(
            ProtocolCommand command,
            TaskCompletionSource<CommandSubmission> completion)
        {
            try
            {
                CommandSubmission submission = await _coordinator!.SubmitAsync(command, CancellationToken.None);
                completion.TrySetResult(submission);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }

        private void CloseComposition()
        {
            if (_coordinator != null)
            {
                _coordinator.ReceiptResolved -= OnReceiptResolved;
            }

            if (_presenter != null)
            {
                _presenter.Unbind();
            }

            if (_composition != null)
            {
                _ = _composition.ShutdownAsync("C04_PROBE_DONE");
            }

            _presenter = null;
            _coordinator = null;
            _composition = null;
            _factory = null;
            _dispatcher = null;
            _receipts.Clear();
        }

        private void OnReceiptResolved(object? sender, TableReceiptResolvedEventArgs args)
        {
            _receipts.Add(new C04ReceiptRecord
            {
                RequestId = args.Command.RequestId,
                Kind = args.Command.Kind,
                Status = args.Resolution.ReceiptStatus.ToString().ToUpperInvariant(),
                Reason = args.Resolution.ReasonCode ?? string.Empty,
                PayloadSha256 = args.Command.DigestSha256,
                DecisionId = _presenter?.LatestView?.Own?.Decision?.DecisionId ?? string.Empty,
                ControlEpoch = _presenter?.LatestView?.Own?.Decision?.ControlEpoch ?? 0,
                Seat = _presenter?.LatestView?.Identity.Seat ?? 0,
                RoundId = _presenter?.LatestView?.Identity.RoundId ?? string.Empty,
                HasCommandVersion = args.Resolution.CommandVersion.HasValue,
                CommandVersion = args.Resolution.CommandVersion?.Value ?? 0,
                EventSeq = args.Resolution.EventSeq?.Value ?? 0
            });
        }

        // ---------------------------------------------------------------- UI actions

        private Button FindButton(string name)
        {
            if (_presenter == null)
            {
                throw new InvalidOperationException("No TablePresenter bound.");
            }

            Button[] buttons = _presenter.GetComponentsInChildren<Button>(true);
            for (int i = 0; i < buttons.Length; i++)
            {
                if (string.Equals(buttons[i].name, name, StringComparison.Ordinal))
                {
                    return buttons[i];
                }
            }

            throw new InvalidOperationException("Table button not found: " + name);
        }

        private void ClickButton(string name)
        {
            Button button = FindButton(name);
            UiInputLifecycle.Instance.NotifySubmit(button.gameObject);
            button.onClick.Invoke();
        }

        private void ClickHandEntity(uint tileId)
        {
            if (_presenter?.LatestView?.Own == null)
            {
                return;
            }

            IReadOnlyList<uint> closed = _presenter.LatestView.Own.ClosedTiles;
            for (int i = 0; i < closed.Count; i++)
            {
                if (closed[i] == tileId)
                {
                    ClickButton("HandTile_" + i.ToString(CultureInfo.InvariantCulture));
                    return;
                }
            }

            if (_presenter.LatestView.Own.DrawnTileId == tileId)
            {
                ClickButton("DrawnTile");
            }
        }

        private IEnumerator DiscardEntity(uint requestedTileId)
        {
            _waitOk = false;
            PlayerTableViewState? before = _presenter?.LatestView;
            if (before?.Own?.Decision == null)
            {
                yield break;
            }

            var diagnostic = new C04DiscardDiagnostic
            {
                requestedTileId = requestedTileId,
                decisionId = before.Own.Decision.DecisionId,
                stateVersionBefore = unchecked((long)before.Identity.StateVersion),
                drawnTileId = before.Own.DrawnTileId ?? 0,
                hasDrawnTile = before.Own.DrawnTileId.HasValue,
                closedTiles = string.Join(",", before.Own.ClosedTiles),
                candidateTiles = DescribeDiscardCandidates(before)
            };
            uint? selectedTile = PickConfirmedDiscardCandidate(before, requestedTileId, out string selectionReason);
            diagnostic.selectionReason = selectionReason;
            if (!selectedTile.HasValue)
            {
                FailDiscard(diagnostic, "NO_CONFIRMED_IN_HAND_DISCARD_CANDIDATE", selectionReason);
                yield break;
            }

            diagnostic.selectedTileId = selectedTile.Value;
            int sentStart = _capture?.SentCount ?? 0;
            int receivedStart = _capture?.ReceivedCount ?? 0;
            ClickHandEntity(selectedTile.Value);
            yield return null;

            TableSelection selection = _presenter!.InteractionState.Selection;
            diagnostic.selectionKind = selection.Kind.ToString();
            diagnostic.selectedAction = selection.Action?.Action.ToString() ?? string.Empty;
            Button discardButton = FindButton("DiscardButton");
            diagnostic.discardButtonActive = discardButton.gameObject.activeSelf;
            diagnostic.discardButtonInteractable = discardButton.interactable;
            diagnostic.pendingRequestId = _presenter.InteractionState.Pending?.Intent.Command.RequestId.Value ?? string.Empty;
            if (!diagnostic.discardButtonActive || !diagnostic.discardButtonInteractable)
            {
                FailDiscard(diagnostic, "DISCARD_BUTTON_NOT_ACTIONABLE", diagnostic.selectionKind + "/" + diagnostic.selectedAction);
                yield break;
            }

            _receiptMark = _receipts.Count;
            ClickButton("DiscardButton");
            string pendingAfterClick = _presenter.InteractionState.Pending?.Intent.Command.RequestId.Value ?? string.Empty;
            yield return WaitReceipt(CommandKind.SubmitAction, 15f);
            diagnostic.receiptStatus = _lastReceipt?.Status ?? string.Empty;
            diagnostic.receiptReason = _lastReceipt?.Reason ?? string.Empty;
            if (!_waitOk || _lastReceipt == null ||
                !string.Equals(_lastReceipt.Status, "APPLIED", StringComparison.Ordinal))
            {
                diagnostic.pendingRequestId = pendingAfterClick;
                FailDiscard(diagnostic, "DISCARD_NOT_APPLIED", diagnostic.receiptStatus + "/" + diagnostic.receiptReason);
                yield break;
            }

            C04ReceiptRecord receipt = _lastReceipt;
            diagnostic.requestId = receipt.RequestId;
            diagnostic.pendingRequestId = pendingAfterClick;
            diagnostic.commandVersion = receipt.CommandVersion;
            byte[]? commandFrame = null;
            byte[]? receiptFrame = null;
            byte[]? publicationFrame = null;
            float publicationDeadline = Time.realtimeSinceStartup + 5f;
            while (Time.realtimeSinceStartup < publicationDeadline)
            {
                if (TryAssociateRaw(
                    _capture?.SentFrom(sentStart) ?? Array.Empty<byte[]>(),
                    _capture?.ReceivedFrom(receivedStart) ?? Array.Empty<byte[]>(),
                    receipt.RequestId,
                    receipt.CommandVersion,
                    true,
                    out commandFrame,
                    out receiptFrame,
                    out publicationFrame))
                {
                    break;
                }

                yield return null;
            }

            if (commandFrame == null || receiptFrame == null || publicationFrame == null)
            {
                FailDiscard(
                    diagnostic,
                    "DISCARD_RAW_ASSOCIATION_MISSING",
                    "command=" + (commandFrame != null) + " receipt=" + (receiptFrame != null) +
                    " publication=" + (publicationFrame != null));
                yield break;
            }

            string rawPrefix = "discard-" + _evidence!.discards.Count.ToString("D2", CultureInfo.InvariantCulture);
            C04RawRef commandRef = WriteRawRef(rawPrefix + "-command.pb", commandFrame);
            C04RawRef receiptRef = WriteRawRef(rawPrefix + "-receipt.pb", receiptFrame);
            C04RawRef publicationRef = WriteRawRef(rawPrefix + "-private-update.pb", publicationFrame);
            diagnostic.commandPath = commandRef.path;
            diagnostic.commandSha256 = commandRef.sha256;
            diagnostic.receiptPath = receiptRef.path;
            diagnostic.receiptSha256 = receiptRef.sha256;
            diagnostic.publicationPath = publicationRef.path;
            diagnostic.publicationSha256 = publicationRef.sha256;

            yield return WaitView(v => v.Identity.StateVersion > before.Identity.StateVersion, 10f);
            diagnostic.stateVersionAfter = unchecked((long)(_presenter.LatestView?.Identity.StateVersion ?? 0));
            bool viewAdvanced = _waitOk;
            bool transcriptContainsPublication = IsFrameStillInTranscript(publicationFrame);
            bool exactApplication = !string.IsNullOrEmpty(diagnostic.requestId) &&
                                    receipt.HasCommandVersion &&
                                    (ulong)diagnostic.stateVersionAfter >= receipt.CommandVersion &&
                                    transcriptContainsPublication &&
                                    viewAdvanced;
            diagnostic.waitOk = exactApplication;
            if (!viewAdvanced)
            {
                FailDiscard(diagnostic, "DISCARD_VIEW_DID_NOT_ADVANCE", diagnostic.stateVersionBefore + "->" + diagnostic.stateVersionAfter);
                yield break;
            }

            if (!transcriptContainsPublication)
            {
                FailDiscard(diagnostic, "DISCARD_PUBLICATION_NOT_IN_TRANSCRIPT", diagnostic.publicationPath);
                yield break;
            }

            if (!exactApplication)
            {
                FailDiscard(diagnostic, "DISCARD_EXACT_APPLICATION_NOT_PROVEN", diagnostic.requestId);
                yield break;
            }

            _waitOk = true;
            AddDiscardDiagnostic(diagnostic);
        }

        private uint? PickConfirmedDiscardCandidate(
            PlayerTableViewState view,
            uint requestedTileId,
            out string reason)
        {
            PlayerDecisionState? decision = view.Own?.Decision;
            if (decision == null)
            {
                reason = "NO_DECISION";
                return null;
            }

            bool InHand(uint tile)
            {
                if (view.Own!.DrawnTileId == tile)
                {
                    return true;
                }

                IReadOnlyList<uint> closed = view.Own.ClosedTiles;
                for (int i = 0; i < closed.Count; i++)
                {
                    if (closed[i] == tile)
                    {
                        return true;
                    }
                }

                return false;
            }

            bool requestedIsCandidate = false;
            uint? fallback = null;
            for (int i = 0; i < decision.Candidates.Count; i++)
            {
                PlayerLegalActionState candidate = decision.Candidates[i];
                if (candidate.Action != PlayerActionKind.DiscardTile)
                {
                    continue;
                }

                for (int t = 0; t < candidate.TileIds.Count; t++)
                {
                    uint tile = candidate.TileIds[t];
                    if (!InHand(tile))
                    {
                        continue;
                    }

                    if (tile == requestedTileId)
                    {
                        requestedIsCandidate = true;
                    }

                    if (!fallback.HasValue)
                    {
                        fallback = tile;
                    }
                }
            }

            if (requestedIsCandidate)
            {
                reason = "REQUESTED_TILE_IS_CONFIRMED_CANDIDATE";
                return requestedTileId;
            }

            if (fallback.HasValue)
            {
                reason = "REQUESTED_TILE_NOT_IN_CONFIRMED_CANDIDATES; SELECTED_IN_HAND_CANDIDATE";
                return fallback.Value;
            }

            reason = "NO_CONFIRMED_CANDIDATE_STILL_IN_HAND";
            return null;
        }

        private static string DescribeDiscardCandidates(PlayerTableViewState view)
        {
            PlayerDecisionState? decision = view.Own?.Decision;
            if (decision == null)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            for (int i = 0; i < decision.Candidates.Count; i++)
            {
                PlayerLegalActionState candidate = decision.Candidates[i];
                if (candidate.Action == PlayerActionKind.DiscardTile)
                {
                    parts.Add(string.Join("|", candidate.TileIds));
                }
            }

            return string.Join(";", parts);
        }

        private void FailDiscard(C04DiscardDiagnostic diagnostic, string code, string detail)
        {
            diagnostic.failure = code;
            diagnostic.waitOk = false;
            _waitOk = false;
            AddDiscardDiagnostic(diagnostic);
            if (_evidence != null)
            {
                Check(
                    _evidence,
                    "G3-DISCARD-" + code,
                    false,
                    "discard command/receipt/publication and UI application proven",
                    code + ":" + detail);
            }
            else
            {
                _caseFailed = true;
            }
        }

        private void AddDiscardDiagnostic(C04DiscardDiagnostic diagnostic)
        {
            if (_evidence != null)
            {
                _evidence.discards.Add(diagnostic);
            }

            Debug.Log(
                "C04_PROBE_DISCARD: requested=" + diagnostic.requestedTileId +
                " selected=" + diagnostic.selectedTileId +
                " reason=" + diagnostic.selectionReason +
                " selection=" + diagnostic.selectionKind + "/" + diagnostic.selectedAction +
                " button=" + diagnostic.discardButtonActive + "/" + diagnostic.discardButtonInteractable +
                " pending=" + diagnostic.pendingRequestId +
                " request=" + diagnostic.requestId +
                " commandVersion=" + diagnostic.commandVersion +
                " receipt=" + diagnostic.receiptStatus + "/" + diagnostic.receiptReason +
                " raw=" + diagnostic.commandSha256 + "/" + diagnostic.receiptSha256 + "/" + diagnostic.publicationSha256 +
                " version=" + diagnostic.stateVersionBefore + "->" + diagnostic.stateVersionAfter +
                " failure=" + diagnostic.failure);
        }

        private IEnumerator AdvanceToNextDealerTurn()
        {
            _waitOk = false;
            ulong version = _presenter?.LatestView?.Identity.StateVersion ?? 0;
            float deadline = Time.realtimeSinceStartup + 90f;
            while (Time.realtimeSinceStartup < deadline)
            {
                PlayerTableViewState? view = _presenter?.LatestView;
                if (view != null && view.Identity.StateVersion > version)
                {
                    if (view.Board.Phase == PlayerGamePhase.CheatWindow &&
                        view.Board.CurrentSeat == 0 &&
                        view.Own?.Decision != null)
                    {
                        _waitOk = true;
                        yield break;
                    }

                    if (view.Own?.Decision != null)
                    {
                        if (view.Board.CurrentSeat == 0 &&
                            (view.Board.Phase == PlayerGamePhase.CheatWindow ||
                             view.Board.Phase == PlayerGamePhase.Discard))
                        {
                            uint requested = view.Own.DrawnTileId ?? view.Own.ClosedTiles[0];
                            uint? tile = PickConfirmedDiscardCandidate(view, requested, out _);
                            if (!tile.HasValue)
                            {
                                _waitOk = false;
                                yield break;
                            }

                            ClickHandEntity(tile.Value);
                            yield return null;
                            Button discard = FindButton("DiscardButton");
                            if (!discard.gameObject.activeSelf || !discard.interactable)
                            {
                                _waitOk = false;
                                yield break;
                            }

                            ClickButton("DiscardButton");
                            version = view.Identity.StateVersion;
                            yield return null;
                            continue;
                        }

                        if (view.Board.Phase == PlayerGamePhase.ResponseWindow ||
                            view.Board.Phase == PlayerGamePhase.ChankanWindow)
                        {
                            if (view.Own.Decision.Candidates.Any(a => a.Action == PlayerActionKind.Pass))
                            {
                                ClickButton("PassButton");
                                version = view.Identity.StateVersion;
                                yield return null;
                                continue;
                            }
                        }
                    }
                }

                yield return null;
            }
        }

        // ---------------------------------------------------------------- waits

        private IEnumerator WaitInitialView()
        {
            _waitOk = false;
            float deadline = Time.realtimeSinceStartup + 30f;
            while (Time.realtimeSinceStartup < deadline)
            {
                PlayerTableViewState? view = _presenter?.LatestView;
                if (view != null &&
                    view.Identity.IsPrivateAuthorized &&
                    view.Own?.Cheat != null &&
                    view.Own.Decision != null)
                {
                    _waitOk = true;
                    yield break;
                }

                yield return null;
            }
        }

        private IEnumerator WaitView(Func<PlayerTableViewState, bool> predicate, float timeoutSeconds)
        {
            _waitOk = false;
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (Time.realtimeSinceStartup < deadline)
            {
                PlayerTableViewState? view = _presenter?.LatestView;
                if (view != null && predicate(view))
                {
                    _waitOk = true;
                    yield break;
                }

                yield return null;
            }
        }

        private IEnumerator WaitReceipt(CommandKind kind, float timeoutSeconds)
        {
            _waitOk = false;
            _lastReceipt = null;
            int start = _receiptMark;
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (Time.realtimeSinceStartup < deadline)
            {
                for (int i = start; i < _receipts.Count; i++)
                {
                    if (_receipts[i].Kind == kind)
                    {
                        _lastReceipt = _receipts[i];
                        _waitOk = true;
                        yield break;
                    }
                }

                yield return null;
            }
        }

        private IEnumerator WaitReceiptById(string requestId, float timeoutSeconds)
        {
            _waitOk = false;
            _lastReceipt = null;
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (Time.realtimeSinceStartup < deadline)
            {
                for (int i = _receipts.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(_receipts[i].RequestId, requestId, StringComparison.Ordinal))
                    {
                        _lastReceipt = _receipts[i];
                        _waitOk = true;
                        yield break;
                    }
                }

                yield return null;
            }
        }

        private IEnumerator WaitTask(Task task, float timeoutSeconds)
        {
            _waitOk = false;
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (!task.IsCompleted && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            _waitOk = task.IsCompleted && !task.IsFaulted && !task.IsCanceled;
            if (!_waitOk)
            {
                Debug.LogWarning("C04_PROBE_TASK_FAILED: " + (task.Exception?.ToString() ?? "timeout"));
            }
        }

        private IEnumerator WaitTask<T>(Task<T> task, float timeoutSeconds) where T : class
        {
            _waitOk = false;
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (!task.IsCompleted && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            _waitOk = task.IsCompleted && !task.IsFaulted && !task.IsCanceled;
            if (!_waitOk)
            {
                Debug.LogWarning("C04_PROBE_TASK_FAILED: " + (task.Exception?.ToString() ?? "timeout"));
            }
        }

        private IEnumerator CaptureFacts()
        {
            _waitOk = false;
            _taskFacts = null;
            Task<LocalTableRecoveryCapture> task = LocalTableHost.CaptureRecoveryAsync(_saveDirectory);
            float deadline = Time.realtimeSinceStartup + 20f;
            while (!task.IsCompleted && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            if (task.IsCompleted && !task.IsFaulted && !task.IsCanceled)
            {
                _lastCapture = task.Result;
                _taskFacts = task.Result.Facts;
                _waitOk = true;
            }
            else
            {
                Debug.LogWarning("C04_PROBE_CAPTURE_FAILED: " + (task.Exception?.ToString() ?? "timeout"));
            }
        }

        // ---------------------------------------------------------------- assertions / evidence

        private static C04Evidence CreateEvidence(
            string caseName,
            C04PlayerCheatScenario kind)
        {
            return new C04Evidence
            {
                schemaVersion = 1,
                layer = "WINDOWS_PLAYER",
                scenarioVersion = LocalPlayerCheatScenario.ScenarioVersion,
                caseName = caseName,
                scenarioKind = (int)kind,
                status = "FAIL",
                startedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                sourceHead = string.Empty,
                buildCommit = string.Empty,
                playerPath = string.Empty,
                unityVersion = string.Empty,
                assertions = new List<C04Assertion>(),
                requests = new List<C04RequestRecord>(),
                observations = new List<C04Observation>()
            };
        }

        private void Check(C04Evidence evidence, string id, bool pass, string expected, string actual)
        {
            if (!pass)
            {
                _caseFailed = true;
            }

            evidence.assertions.Add(new C04Assertion
            {
                id = id,
                pass = pass,
                expected = expected ?? string.Empty,
                actual = actual ?? string.Empty
            });
            Debug.Log("C04_PROBE_CHECK: " + id + "=" + (pass ? "PASS" : "FAIL") + " expected=" + expected + " actual=" + actual);
        }

        private void CheckPhysical(C04Evidence evidence, string id, LocalTableRecoveryFacts facts)
        {
            Check(
                evidence,
                id,
                facts.Physical.TileCount == 136 && facts.Physical.UniqueCount == 136,
                "136 tiles / 136 unique",
                facts.Physical.TileCount + "/" + facts.Physical.UniqueCount);
        }

        private void CheckZeroDelta(
            C04Evidence evidence,
            string id,
            LocalTableRecoveryFacts before,
            LocalTableRecoveryFacts after)
        {
            bool zero = string.Equals(before.StateVersion, after.StateVersion, StringComparison.Ordinal) &&
                        string.Equals(before.EventSeq, after.EventSeq, StringComparison.Ordinal) &&
                        string.Equals(before.CheatVersion, after.CheatVersion, StringComparison.Ordinal) &&
                        string.Equals(before.CheatFingerprint, after.CheatFingerprint, StringComparison.Ordinal) &&
                        string.Equals(before.Physical.TilesSha256, after.Physical.TilesSha256, StringComparison.Ordinal) &&
                        before.Ledger.EntryCount == after.Ledger.EntryCount &&
                        string.Equals(before.Ledger.EntriesSha256, after.Ledger.EntriesSha256, StringComparison.Ordinal) &&
                        before.Ledger.Balances.SequenceEqual(after.Ledger.Balances);
            Check(
                evidence,
                id,
                zero,
                "version/event/cheat/ledger/entity unchanged",
                "v=" + before.StateVersion + "->" + after.StateVersion +
                " e=" + before.EventSeq + "->" + after.EventSeq +
                " entries=" + before.Ledger.EntryCount + "->" + after.Ledger.EntryCount +
                " tiles=" + before.Physical.TilesSha256 + "->" + after.Physical.TilesSha256);
        }

        private void CheckRequestApplied(
            C04Evidence evidence,
            string id,
            LocalTableRecoveryFacts facts,
            C04ReceiptRecord receipt)
        {
            LocalTableRecoveryRequestFact? fact = facts.Requests.FirstOrDefault(
                r => string.Equals(r.RequestId, receipt.RequestId, StringComparison.Ordinal));
            if (fact != null)
            {
                receipt.RawPayloadSha256 = fact.PayloadSha256;
                C04RequestRecord? record = evidence.requests.FirstOrDefault(
                    r => string.Equals(r.requestId, receipt.RequestId, StringComparison.Ordinal));
                if (record != null)
                {
                    record.rawPayloadSha256 = fact.PayloadSha256;
                }
            }

            Check(
                evidence,
                id,
                fact != null &&
                string.Equals(fact.Status, "APPLIED", StringComparison.OrdinalIgnoreCase) &&
                fact.Confirmed &&
                string.Equals(fact.PayloadSha256, receipt.RawPayloadSha256, StringComparison.OrdinalIgnoreCase),
                "APPLIED request " + receipt.RequestId + " with matching raw payload hash",
                fact == null
                    ? "missing"
                    : fact.Status + "/confirmed=" + fact.Confirmed + "/" + fact.PayloadSha256);
        }

        private void CheckRequestRejected(
            C04Evidence evidence,
            string id,
            LocalTableRecoveryFacts facts,
            C04ReceiptRecord receipt)
        {
            LocalTableRecoveryRequestFact? fact = facts.Requests.FirstOrDefault(
                r => string.Equals(r.RequestId, receipt.RequestId, StringComparison.Ordinal));
            if (fact != null)
            {
                receipt.RawPayloadSha256 = fact.PayloadSha256;
                C04RequestRecord? record = evidence.requests.FirstOrDefault(
                    r => string.Equals(r.requestId, receipt.RequestId, StringComparison.Ordinal));
                if (record != null)
                {
                    record.rawPayloadSha256 = fact.PayloadSha256;
                }
            }

            Check(
                evidence,
                id,
                fact != null &&
                !string.Equals(fact.Status, "APPLIED", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(fact.PayloadSha256, receipt.RawPayloadSha256, StringComparison.OrdinalIgnoreCase),
                "rejected request " + receipt.RequestId + " with matching raw payload hash",
                fact == null
                    ? "missing"
                    : fact.Status + "/" + fact.PayloadSha256);
        }

        private static string DescribeLedger(PlayerCheatState cheat)
        {
            return string.Join(
                ";",
                cheat.Ledger.Select(
                    e => e.Amount.ToString(CultureInfo.InvariantCulture) + "@" +
                         e.BalanceAfter.ToString(CultureInfo.InvariantCulture) + "/" + e.ReasonCode));
        }

        private static string DescribeMarks(PlayerCheatState cheat)
        {
            if (cheat.TileMark == null)
            {
                return "no tile mark";
            }

            return string.Join(
                ";",
                cheat.TileMark.Groups.Select(
                    g => string.Join(",", g.Tiles.Select(t => t.TileId))));
        }

        private IEnumerator WriteFailureEvidence(string reason, string detail)
        {
            if (_evidence != null)
            {
                _evidence.status = "FAIL";
                _evidence.finishedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                _evidence.observations.Add(new C04Observation
                {
                    kind = "failure",
                    detail = reason + ": " + detail,
                    tileIds = string.Empty,
                    cheatPoints = 0,
                    offerId = string.Empty,
                    eventTypes = "FAIL"
                });
                WriteEvidence(_evidence);
            }

            yield return null;
        }

        private void WriteEvidence(C04Evidence evidence)
        {
            evidence.sourceHead = _sourceHead;
            evidence.buildCommit = _buildCommit;
            evidence.playerPath = _playerPath;
            evidence.unityVersion = _unityVersion;
            evidence.packageManifestSha256 = _packageManifestSha256;
            if (_capture != null && _rawActions.Count > 0)
            {
                WriteRawIndex(evidence);
            }

            evidence.assertionCount = evidence.assertions.Count;
            evidence.passedAssertionCount = evidence.assertions.Count(a => a.pass);
            evidence.failedAssertionCount = evidence.assertions.Count(a => !a.pass);
            string path = Path.Combine(_evidenceDir, "case-" + _caseName + ".json");
            File.WriteAllText(path, JsonUtility.ToJson(evidence, true), new System.Text.UTF8Encoding(false));
            Debug.Log("C04_PROBE_EVIDENCE: " + path);
        }

        // ---------------------------------------------------------------- raw original capture

        private void RecordRawAction(
            string id,
            C04ReceiptRecord receipt,
            int sentStart,
            int receivedStart,
            LocalTableRecoveryCapture? before,
            bool requirePublication)
        {
            C04Evidence? evidence = _evidence;
            if (evidence == null || _capture == null)
            {
                return;
            }

            byte[]? welcome = _welcomeFrame ?? FindReceivedFrame(0, e => e.MessageType == WireMessageType.Welcome);
            bool associated = TryAssociateRaw(
                _capture.SentFrom(sentStart),
                _capture.ReceivedFrom(receivedStart),
                receipt.RequestId,
                receipt.CommandVersion,
                requirePublication,
                out byte[]? command,
                out byte[]? receiptFrame,
                out byte[]? publication);
            if (welcome == null || !associated)
            {
                Check(
                    evidence,
                    id + "-RAW",
                    false,
                    "original welcome/command/receipt/publication bytes captured from the real channel",
                    "welcome=" + (welcome != null) + " command=" + (command != null) +
                    " receipt=" + (receiptFrame != null) + " publication=" + (publication != null) +
                    " request=" + receipt.RequestId);
                return;
            }

            _welcomeFrame = welcome;
            var action = new C04RawAction
            {
                id = id,
                welcome = WriteRawRef(id + "-welcome.pb", welcome),
                command = WriteRawRef(id + "-command.pb", command!),
                receipt = WriteRawRef(id + "-receipt.pb", receiptFrame!)
            };

            if (requirePublication)
            {
                action.acceptedPrivatePublication = WriteRawRef(id + "-private-update.pb", publication!);
            }

            if (before == null || _lastCapture == null)
            {
                Check(evidence, id + "-RAW-RECOVERY", false, "before/after authority save and cheat state", "capture missing");
                return;
            }

            action.before = new C04RawPair
            {
                authoritySave = WriteRawRef(id + "-before.save", before.AuthoritySaveBytes),
                cheatState = WriteRawRef(id + "-before-cheat.bin", before.CheatStatePayload)
            };
            action.after = new C04RawPair
            {
                authoritySave = WriteRawRef(id + "-after.save", _lastCapture.AuthoritySaveBytes),
                cheatState = WriteRawRef(id + "-after-cheat.bin", _lastCapture.CheatStatePayload)
            };
            _rawActions.Add(action);
            Check(
                evidence,
                id + "-RAW",
                true,
                "original channel/recovery bytes captured",
                "request=" + receipt.RequestId + " commandVersion=" + receipt.CommandVersion);
        }

        private byte[]? FindSentFrame(int start, Func<WireEnvelope, bool> predicate)
        {
            if (_capture == null)
            {
                return null;
            }

            byte[][] frames = _capture.SentFrom(start);
            for (int i = 0; i < frames.Length; i++)
            {
                WireDecodeResult decoded = WireCodec.Decode(frames[i]);
                if (decoded.IsOk && decoded.Envelope != null && predicate(decoded.Envelope))
                {
                    return frames[i];
                }
            }

            return null;
        }

        private byte[]? FindReceivedFrame(int start, Func<WireEnvelope, bool> predicate)
        {
            if (_capture == null)
            {
                return null;
            }

            byte[][] frames = _capture.ReceivedFrom(start);
            for (int i = 0; i < frames.Length; i++)
            {
                WireDecodeResult decoded = WireCodec.Decode(frames[i]);
                if (decoded.IsOk && decoded.Envelope != null && predicate(decoded.Envelope))
                {
                    return frames[i];
                }
            }

            return null;
        }

        private bool IsFrameStillInTranscript(byte[] frame)
        {
            if (_capture == null)
            {
                return false;
            }

            byte[][] received = _capture.ReceivedFrom(0);
            for (int i = 0; i < received.Length; i++)
            {
                if (received[i].AsSpan().SequenceEqual(frame))
                {
                    return true;
                }
            }

            return false;
        }

        private C04RawRef WriteRawRef(string name, byte[] bytes)
        {
            string relative = "raw/" + name;
            File.WriteAllBytes(Path.Combine(_evidenceDir, "raw", name), bytes);
            return new C04RawRef { path = relative, sha256 = Sha256(bytes) };
        }

        private void WriteRawIndex(C04Evidence evidence)
        {
            if (_capture == null)
            {
                return;
            }

            var index = new C04RawIndex
            {
                schemaVersion = 2,
                producer = "LOCALHOST_CHANNEL",
                sourceHead = _sourceHead,
                packageManifestSha256 = _packageManifestSha256,
                actions = _rawActions.ToArray(),
                sentFrames = WriteFrameSet("sent", _capture.SentFrom(0)),
                receivedFrames = WriteFrameSet("received", _capture.ReceivedFrom(0))
            };
            byte[] bytes = new System.Text.UTF8Encoding(false).GetBytes(JsonUtility.ToJson(index, true));
            string indexFile = Path.Combine(_evidenceDir, "raw", "index.json");
            File.WriteAllBytes(indexFile, bytes);
            evidence.rawEvidenceIndex = "raw/index.json";
            evidence.rawEvidenceIndexSha256 = Sha256(bytes);
        }

        private C04RawRef[] WriteFrameSet(string prefix, byte[][] frames)
        {
            var refs = new C04RawRef[frames.Length];
            for (int i = 0; i < frames.Length; i++)
            {
                refs[i] = WriteRawRef(prefix + "-" + i.ToString("D5", CultureInfo.InvariantCulture) + ".pb", frames[i]);
            }

            return refs;
        }

        private static string Sha256(byte[] bytes)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                var text = new System.Text.StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                {
                    text.Append(hash[i].ToString("X2", CultureInfo.InvariantCulture));
                }

                return text.ToString();
            }
        }
    }

    // -------------------------------------------------------------------- evidence DTOs

    [Serializable]
    public sealed class C04Evidence
    {
        public int schemaVersion;
        public string layer = "WINDOWS_PLAYER";
        public string scenarioVersion = string.Empty;
        public string caseName = string.Empty;
        public int scenarioKind;
        public string status = "FAIL";
        public string startedUtc = string.Empty;
        public string finishedUtc = string.Empty;
        public string sourceHead = string.Empty;
        public string buildCommit = string.Empty;
        public string playerPath = string.Empty;
        public string unityVersion = string.Empty;
        public string packageManifestSha256 = string.Empty;
        public string rawEvidenceIndex = string.Empty;
        public string rawEvidenceIndexSha256 = string.Empty;
        public int assertionCount;
        public int passedAssertionCount;
        public int failedAssertionCount;
        public string[] blockedRows = Array.Empty<string>();
        public List<C04Assertion> assertions = new List<C04Assertion>();
        public List<C04RequestRecord> requests = new List<C04RequestRecord>();
        public List<C04Observation> observations = new List<C04Observation>();
        public List<C04DiscardDiagnostic> discards = new List<C04DiscardDiagnostic>();
        public C04AuthorityFacts? before;
        public C04AuthorityFacts? afterMark;
        public C04AuthorityFacts? afterBloom;
        public C04AuthorityFacts? afterBloomChoice;
        public C04AuthorityFacts? afterBeam;
        public C04AuthorityFacts? afterFinal;
        public C04AuthorityFacts? afterUnauthorized;
        public C04AuthorityFacts? beforeInsufficient;
        public C04AuthorityFacts? afterInsufficient;
    }

    [Serializable]
    public sealed class C04Assertion
    {
        public string id = string.Empty;
        public bool pass;
        public string expected = string.Empty;
        public string actual = string.Empty;
    }

    [Serializable]
    public sealed class C04Observation
    {
        public string kind = string.Empty;
        public string detail = string.Empty;
        public string tileIds = string.Empty;
        public long cheatPoints;
        public string offerId = string.Empty;
        public string eventTypes = string.Empty;
    }

    [Serializable]
    public sealed class C04RequestRecord
    {
        public string requestId = string.Empty;
        public string kind = string.Empty;
        public string status = string.Empty;
        public string reason = string.Empty;
        public string payloadSha256 = string.Empty;
        public string rawPayloadSha256 = string.Empty;
        public string roundId = string.Empty;
        public int seat;
        public string decisionId = string.Empty;
        public long controlEpoch;
        public long stateVersionBefore;
        public long stateVersionAfter;
        public string source = "TablePresenter";
    }

    [Serializable]
    public sealed class C04DiscardDiagnostic
    {
        public uint requestedTileId;
        public uint selectedTileId;
        public string decisionId = string.Empty;
        public long stateVersionBefore;
        public long stateVersionAfter;
        public uint drawnTileId;
        public bool hasDrawnTile;
        public string closedTiles = string.Empty;
        public string candidateTiles = string.Empty;
        public string selectionReason = string.Empty;
        public string selectionKind = string.Empty;
        public string selectedAction = string.Empty;
        public bool discardButtonActive;
        public bool discardButtonInteractable;
        public string pendingRequestId = string.Empty;
        public string requestId = string.Empty;
        public ulong commandVersion;
        public string commandPath = string.Empty;
        public string commandSha256 = string.Empty;
        public string receiptPath = string.Empty;
        public string receiptSha256 = string.Empty;
        public string publicationPath = string.Empty;
        public string publicationSha256 = string.Empty;
        public string receiptStatus = string.Empty;
        public string receiptReason = string.Empty;
        public bool waitOk;
        public string failure = string.Empty;
    }

    [Serializable]
    public sealed class C04RawRef
    {
        public string path = string.Empty;
        public string sha256 = string.Empty;
    }

    [Serializable]
    public sealed class C04RawPair
    {
        public C04RawRef? authoritySave;
        public C04RawRef? cheatState;
    }

    [Serializable]
    public sealed class C04RawAction
    {
        public string id = string.Empty;
        public C04RawRef? welcome;
        public C04RawRef? command;
        public C04RawRef? receipt;
        public C04RawRef? acceptedPrivatePublication;
        public C04RawPair? before;
        public C04RawPair? after;
    }

    [Serializable]
    public sealed class C04RawIndex
    {
        public int schemaVersion = 2;
        public string producer = "LOCALHOST_CHANNEL";
        public string sourceHead = string.Empty;
        public string packageManifestSha256 = string.Empty;
        public C04RawAction[] actions = Array.Empty<C04RawAction>();
        public C04RawRef[] sentFrames = Array.Empty<C04RawRef>();
        public C04RawRef[] receivedFrames = Array.Empty<C04RawRef>();
    }

    [Serializable]
    public sealed class C04AuthorityFacts
    {
        public string stateVersion = string.Empty;
        public string eventSeq = string.Empty;
        public string cheatVersion = string.Empty;
        public string cheatFingerprint = string.Empty;
        public string tilesSha256 = string.Empty;
        public int tileCount;
        public int uniqueCount;
        public int ledgerEntryCount;
        public int[] balances = Array.Empty<int>();
        public long points;
        public string ledgerEntriesSha256 = string.Empty;
        public int requestCount;
        public string[] requestIds = Array.Empty<string>();
        public string[] requestStatuses = Array.Empty<string>();
        public string[] requestPayloadSha256 = Array.Empty<string>();

        public static C04AuthorityFacts From(LocalTableRecoveryFacts facts)
        {
            return new C04AuthorityFacts
            {
                stateVersion = facts.StateVersion,
                eventSeq = facts.EventSeq,
                cheatVersion = facts.CheatVersion,
                cheatFingerprint = facts.CheatFingerprint,
                tilesSha256 = facts.Physical.TilesSha256,
                tileCount = facts.Physical.TileCount,
                uniqueCount = facts.Physical.UniqueCount,
                ledgerEntryCount = facts.Ledger.EntryCount,
                balances = facts.Ledger.Balances.ToArray(),
                points = facts.Ledger.Points,
                ledgerEntriesSha256 = facts.Ledger.EntriesSha256,
                requestCount = facts.Requests.Count,
                requestIds = facts.Requests.Select(r => r.RequestId).ToArray(),
                requestStatuses = facts.Requests.Select(r => r.Status).ToArray(),
                requestPayloadSha256 = facts.Requests.Select(r => r.PayloadSha256).ToArray()
            };
        }
    }

    internal sealed class C04ReceiptRecord
    {
        public string RequestId = string.Empty;
        public CommandKind Kind;
        public string Status = string.Empty;
        public string Reason = string.Empty;
        public string PayloadSha256 = string.Empty;
        public string RawPayloadSha256 = string.Empty;
        public string DecisionId = string.Empty;
        public ulong ControlEpoch;
        public int Seat;
        public string RoundId = string.Empty;
        public bool HasCommandVersion;
        public ulong CommandVersion;
        public ulong EventSeq;

        public C04RequestRecord ToRecord(ulong stateVersionBefore, ulong stateVersionAfter)
        {
            return ToRecord(stateVersionBefore, stateVersionAfter, null);
        }

        public C04RequestRecord ToRecord(
            ulong stateVersionBefore,
            ulong stateVersionAfter,
            BusinessPayload? payload)
        {
            return new C04RequestRecord
            {
                requestId = RequestId,
                kind = Kind.ToString(),
                status = Status,
                reason = Reason,
                payloadSha256 = PayloadSha256,
                rawPayloadSha256 = payload != null
                    ? RawSha256(payload)
                    : RawPayloadSha256,
                roundId = RoundId,
                seat = Seat,
                decisionId = DecisionId,
                controlEpoch = unchecked((long)ControlEpoch),
                stateVersionBefore = unchecked((long)stateVersionBefore),
                stateVersionAfter = unchecked((long)stateVersionAfter),
                source = "TablePresenter"
            };
        }

        private static string RawSha256(BusinessPayload payload)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(payload.AsSpan().ToArray());
                var text = new System.Text.StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                {
                    text.Append(hash[i].ToString("X2", CultureInfo.InvariantCulture));
                }

                return text.ToString();
            }
        }
    }
}
