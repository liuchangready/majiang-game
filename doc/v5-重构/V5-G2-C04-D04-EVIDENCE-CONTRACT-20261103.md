# G2-C04-D04 raw evidence contract (A)

Status: component contract for D04-A, not D04 or GAME acceptance. The old case JSON remains readable as metadata, but a case without `rawEvidenceIndex` fails closed. No legacy summary or caller-provided hash can substitute for raw bytes.

## Fixed verifier entry point

```powershell
dotnet <C04PlayerCheatVerifier2.dll> verify --evidence <case-json> --case <authorized|negative> --source-head <40-hex-sha> --package-manifest <path>
```

The executable returns native exit 0 only when every required raw check succeeds. Missing files, malformed/unknown values, mismatched bindings, non-definitive receipts, wrong rejection reasons, changed rejected state, and package mismatch return nonzero. Stdout is one JSON object (`schemaVersion`, `kind`, `status`, `evidence`, `case`, `sourceHead`, `packageManifest`, computed `facts`, and structured `errors`). The caller must check the native exit; `status` text never overrides it. `facts` are derived from decoded bytes and read-only recovery, not copied from the old assertion list.

`tools/unity/c04-player-cheats-evidence.psm1` only invokes that same CLI and returns its actual native code/output. It makes no PASS/FAIL semantic decisions of its own.

## Package manifest

The package manifest is JSON:

```json
{
  "kind": "c04-player-package-manifest",
  "sourceHead": "<40-hex-sha>",
  "files": [
    { "path": "Mahjong.exe", "bytes": 123, "sha256": "<64-hex-sha>" }
  ]
}
```

`files` must be nonempty. Every file path is relative to the manifest directory, exists, is not a reparse point, and has the declared byte length and SHA-256. The verifier hashes the manifest bytes and requires that digest in the raw index. This binds the evidence capture to the exact manifest and its package file closure; a caller-supplied `sourceHead` by itself is insufficient.

## Case metadata and raw index

The case JSON retains existing metadata and adds:

```json
{ "sourceHead": "<40-hex-sha>", "rawEvidenceIndex": "raw/index.json", "rawEvidenceIndexSha256": "<64-hex-sha>" }
```

`rawEvidenceIndex` and all descendants are relative to the case JSON directory. The case metadata's `rawEvidenceIndexSha256` must match the index bytes. Absolute paths, any `.`/`..` path component, missing files, and reparse points are rejected. Every raw reference is `{ "path": "raw/file.bin", "sha256": "<64-hex-sha>" }`; the verifier recalculates every digest from the referenced bytes.

The index is versioned and classifies the capture honestly:

```json
{
  "schemaVersion": 2,
  "producer": "LOCALHOST_CHANNEL",
  "sourceHead": "<40-hex-sha>",
  "packageManifestSha256": "<sha256 of the exact manifest bytes>",
  "actions": [
    {
      "id": "tile-mark",
      "welcome": { "path": "raw/tile-mark-welcome.pb", "sha256": "..." },
      "command": { "path": "raw/tile-mark-command.pb", "sha256": "..." },
      "receipt": { "path": "raw/tile-mark-receipt.pb", "sha256": "..." },
      "acceptedPrivatePublication": { "path": "raw/tile-mark-player-update.pb", "sha256": "..." },
      "before": {
        "authoritySave": { "path": "raw/before.save", "sha256": "..." },
        "cheatState": { "path": "raw/before-cheat.bin", "sha256": "..." }
      },
      "after": {
        "authoritySave": { "path": "raw/after.save", "sha256": "..." },
        "cheatState": { "path": "raw/after-cheat.bin", "sha256": "..." }
      }
    }
  ],
  "sentFrames": [{ "path": "raw/sent-00000.pb", "sha256": "..." }],
  "receivedFrames": [{ "path": "raw/received-00000.pb", "sha256": "..." }]
}
```

An authorized index contains exactly these ordered rows: `tile-mark`, `sleeve-bloom`, `sleeve-second`, `beam-swap`, `beam-swap-choice`. Every row has original serialized command and receipt envelopes, its exact accepted private PlayerUpdate, and paired read-only recovery save plus current cheat-state bytes before and at the exact command commit. Recovery post-state must be the receipt's command version; a later `latest` save cannot stand in for it. The accepted event must be private, have the expected event type, and lie within that receipt's event high-water. Command version, event sequence, state version, stream version, and projection cursor remain separate values.

A negative index contains exactly `unauthorized` and `insufficient-funds` rows. Each has a different original canonical payload and request ID, a typed REJECTED receipt, a Welcome bound to the command's room/session/seat/scope, and before/after save plus cheat-state originals. The actual production-channel reasons are `DECISION_MISMATCH` for the missing decision authorization case and `CHEAT_INSUFFICIENT_FUNDS` for low balance (`INSUFFICIENT_FUNDS` is a different protocol constant and is not accepted). A wire/protocol error is not a business rejection. The verifier requires each composite durable request in its after recovery with exact raw payload SHA, receipt ID, evaluated version, REJECTED status, no confirmed command/event version, and zero state/event/cheat/ledger/entity side effects. Negative rows do not claim an accepted publication.

## Derived assertions

For authorized rows the verifier decodes with the existing `WireCodec`, recomputes `CanonicalPayloadDigest` from the command's original canonical bytes, cross-checks command kind, request/scope/account/role/session/room/seat/round/control epoch, and matches the typed receipt and recovery request by their actual composite identity. Receipt room/session/seat and scope room/account/role bind to the command; Welcome binds the command scope to the player session. The actual LocalHost private PlayerUpdate envelope has no scope, so it is bound by exact raw transcript membership, room/session/seat, private owner and Welcome. Any optional publication scope must match if present. The exact accepted publication must be byte-identical to a received frame. Cursor copies must agree; stream version is checked within the command's before/after state window and against received history, while projection sequence is anchored to Welcome baseline and the preceding received frame. These values are not equated to event high-water or to each other.

The two digest contracts remain distinct: `WireReceipt.CanonicalPayloadSha256` is compared with `CanonicalPayloadDigest.Compute(originalCanonicalPayload)`, while persisted recovery request facts are compared with SHA-256 of the exact original `canonical_payload` bytes. The recovery comparison also binds status, request/scope/account, command version and event sequence. Neither value is copied from the case summary or accepted as an arbitrary 64-hex string.

Recovery bytes are read with `LocalTableRecoveryFactsReader`; physical positions are read with `LocalPlayerActionScenario.ReadPhysicalAudit`. The fixed result is five applied commands with costs 1/10/10, balances 120→119→109→99, three private ledger entries and no second debit for the choice commands. Each exact recovery post-state and private publication must agree. TileMark's canonical entity set must equal its owner-private marked entity set. SECOND must move the offer's first entity from the dealer hand to the wall and the selected second entity from wall to dealer hand. SWAP must exchange the command's exact dealer hand entity with the unique offer candidate resolved for the submitted target, including source and destination slot. Each recovery image must contain exactly 136 unique assigned entities.

The verifier does not infer that an index's `LOCALHOST_CHANNEL` label proves a Unity or Player execution. Only original production-channel capture bytes can qualify the component as a real LocalHost channel record; the full Windows Player / Unity proof remains with D04-B and D04-C.

## Minimum negative matrix

The acceptance matrix retains the old Strict controls that target this defect and adds grouped mutations:

| Group | Mutations | Required result |
|---|---|---|
| Missing originals | Old nominal case with arbitrary hashes and no raw index; missing command/receipt/publication/recovery member | Nonzero; specific missing/path finding |
| Raw binding | Arbitrary valid 64-hex hash; changed command byte; wrong canonical digest; wrong request or scope | Nonzero after recomputation or typed identity comparison |
| Role and version | Wrong account/role/session/room/seat/round/decision/control epoch; wrong command baseline, receipt evaluated version, command version, event or stream cursor | Nonzero; values remain independently checked |
| Publication | Wrong owner scope; public or malformed update; missing accepted private event; wrong event type/visibility; publication from another command | Nonzero |
| Recovery and effects | Missing/altered recovery; later checkpoint substituted for exact command post-state; wrong ledger/fee/entity destination; non-136 or duplicate physical ID | Nonzero |
| Negative semantics | Same payload/different request; unrelated or malformed/internal reason; altered reason; any state/event/ledger/entity delta | Nonzero |
| Capture/package paths | Absolute path, `..`, missing path, reparse point, wrong source head, wrong manifest digest or package file hash/length | Nonzero |

The current old R8 failures are one retained defect group, `C04-G3-R1-04` (MAJOR), not new discoveries: nominal evidence without originals, arbitrary raw hashes, and unrelated `MALFORMED_MESSAGE` / `INTERNAL_ERROR` reasons with self-claimed zero effect and a nonzero version change. Same-payload/different-request and nonzero-state rejection controls remain required.

## Component commands

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/C04PlayerCheatEvidence.Tests.ps1 `
  -DotNetPath D:/Unity/dotnet/dotnet.exe `
  -VerifierDll <isolated-build>/C04PlayerCheatVerifier2.dll `
  -OutputRoot <repo>/build/<GUID>/evidence-tool-tests

dotnet <C04PlayerCheatVerifier2.dll> capture-localhost-authorized `
  --output-root <new-empty-directory> --source-head <40-hex-sha> --package-manifest <path>

dotnet test dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj `
  --filter FullyQualifiedName~C04PlayerCheatChannelTests `
  --artifacts-path <repo>/build/<GUID>/localhost-artifacts
```

The PowerShell test labels itself `TOOL_FIXTURE`; it drives fresh serialized LocalHost authorized and two-row negative captures through the fixed thin wrapper, plus missing originals, path escape, wrong package binding, arbitrary raw-file hash, and 31 typed native-failure mutations with asserted structured causes. The mutations reserialize copied real frames and refresh only their file/index hashes. Two positive controls remain native 0: a valid lagging stream version and a later legitimate recovery envelope that still contains the exact earlier commit. The legacy C04 Strict fixture suite remains separately covered at 19/19, including its six-row assertion and negative controls. The targeted LocalHost channel suite is a separate real component result, including genuine business rejection cases. Neither result is Unity, Player, or full D04 PASS. The old R8 report and all older failure artifacts remain read-only.
