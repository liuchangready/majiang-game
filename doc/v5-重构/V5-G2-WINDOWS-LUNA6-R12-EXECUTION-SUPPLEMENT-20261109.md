# G2 Windows Luna6 execution supplement — Round 12

Status: **NOT ACCEPTED**. Round 12 started from clean bridge HEAD `1d5c600811a4078d2a80fb1f0bf852bb3ea85f6b` (parent `4d277c1f474a0eacd2c93c0c0ed11bea61a1b6bf`, original base `4523485cfd4ec2b32185a7da5c13f6cbaf54a1e1`). No new review defect was identified. Existing closed IDs remain closed.

## Code delivered this round

`tools/unity/run-p45-c04-player-actions.ps1` now runs the existing `LocalControlPlayerProbe` using the same fresh Windows Player executable it just built for the five fixed action cases. It uses a separate `-smoke-data` directory and records the probe's runtime log and the absolute `-logFile` under `control/`. The summary binds the G4 supporting result to `player-package.json`. Before recording success the runner requires one result record, both old-player and old-AI rejections to be `StaleControlEpoch`, `releaseEpoch = initialEpoch + 1`, `claimEpoch = releaseEpoch + 1`, and the old command epoch to equal the original epoch. This does not satisfy the Census disconnect-policy case by itself: the current probe uses the production ReleaseControl button rather than simulating an actual channel disconnect. It is therefore supporting G4 evidence only, not a G4 row pass.

The runner source SHA-256 is `0530F4C3DC34EA504508F109833B72A1C53D3D5419CB6B8F8F029D27E765727F`.

## Verification performed

- Parsed the complete runner with the Windows PowerShell AST parser: no syntax errors.
- Extracted `Assert-C04LocalControlProbeResult` from that parsed source and exercised one healthy record and four negatives (missing result, skipped release epoch, old command epoch mismatch, and Applied stale-AI receipt): 1 accepted, 4 rejected.
- Ran the changed runner's complete FixtureOnly command:

  `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File tools/unity/run-p45-c04-player-actions.ps1 -FixtureOnly -UnityPath D:/game_dev_tools/unity/Editor/Unity.exe -DotnetPath D:/game_dev_tools/dotnet/dotnet.exe -RunRoot build/9bc5ff873b59451fbfd4c69b8911619b -TimeoutSeconds 600`

  It emitted `C04_TOOL_FIXTURES_PASS positive=3 negative=65 protobuf=PASS malformed=2 timeoutTree=PASS mutexTimeoutSeal=PASS mutexCancellationSeal=PASS outputDrain=PASS outputFiles=123` and completed successfully. Stable fixture seal SHA-256: `E851E2AFC2D5329ED591E1136628012884F183965934BDD02DEFE50DA84DBA13`; runner stage log SHA-256: `FBFAB3052F14DD0E0D673E91FCA33AAE3F5C8E7338A3796D6C80E10F760247E4`.

FixtureOnly did not launch Unity or a Windows Player. `fixture-result.json` is a synthetic runner fixture and is not product evidence. No full LocalHost suite was repeated because its source input did not change. No Player, PlayMode, visual-input, common-package matrix, or offline run was performed.

The related runner contract suite was also executed:

`powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File tools/tests/G2TestRunner.Tests.ps1`

Native exit was 0 with 30 assertions, 0 failed. `build/g2-runner-test-67b684adc4874e4db2ea762937875643/test-summary.json` SHA-256: `B3D17BCA6A142A8115434B8636A39E3A5CA58C3BAC3E1C79CED8E6BC11A5C93D` (`evidenceClass=TOOL_FIXTURE`).

## Existing review items, still open

### D-1-02 — G1/G2 scenarios, G4 execution and final binding (MAJOR)

- **Location:** `dotnet/src/Mahjong.LocalHost/LocalPlayerActionScenario.cs`; `unity/Mahjong.Unity/Assets/Scripts/App/C04PlayerActionProbe.cs` and `LocalControlPlayerProbe.cs`; `tools/unity/run-p45-c04-player-actions.ps1` and `tools/unity/test-p45-control-ui.ps1`; G1/G2/G4/G7-02 Binding rows.
- **Reproduction command/scripts:** run the real action runner with `-RunRoot build/<fresh-32-hex-guid>` and pinned Unity/.NET paths; run `tools/unity/test-p45-control-ui.ps1` for the control path; compare their GAME/PlayMode originals with the current 32-row Binding. Round 12 exercised only FixtureOnly, so it has no Player result.
- **Actual vs expected:** G1 dealer Tsumo and G2 Pon/Chi/Riichi/closed Kan have five fixed-case probe contracts, but none has been run as new-candidate Player evidence in this round. G1 exhaustive draw and dealer retention/new `roundId`, and G2 added-kan/chankan positive branch remain without deterministic Player scenarios. G4 runner integration now exists, but its `ReleaseControl` behavior does not prove Census disconnect/takeover; it has not been run on a frozen candidate. Accepted G7-02 service positives/negatives are unchanged support evidence, not final-candidate row bindings.
- **Evidence:** Round 9 support manifest `E:/g2-r9-d8-20261109/round9-review-manifest.json` (SHA-256 `F898481E7D71829525344F73CB4001B6496FFE93731C386D34FA6B80D0CAB592`); Round 12 FixtureOnly seal above. Neither is a replacement for the required Player originals.
- **Repair:** implement deterministic G1 exhaustive draw and dealer continuation and G2 added-kan/chankan/rinshan positive and invalid zero-change branches through the authorized production Player/channel entry. Preserve G1-02 legal Tsumo and G2 rejected-action checks. Run the five action Player/PlayMode cases and G4 through the common package; execute the real GAME verifier positives and per-case mutation negatives. Bind accepted G7-02 service evidence to the final candidate without repeating unchanged service runs.
- **Acceptance:** real nonzero Player/PlayMode/service executions, 0 skip; complete 136-tile opening, Tsumo and exhaustive draw, dealer repeat with a new round ID; Pon/Chi/Riichi/Double Riichi/Kan/rinshan/chankan and invalid zero-change; actual disconnect takeover, same-room reconciliation, explicit claim and stale-epoch rejection; G7-02 LOCAL asset refusal with zero version/event/ledger/outbox/settlement mutations plus server-derived positive. Preserve deadline, idempotency, privacy and terminal-state assertions.

### D-1-01 — G8 visual layout and real input (MAJOR)

- **Location:** C04-G8-01/02 and ordinary Player capture/input entry.
- **Reproduction:** compare the current Binding with the DPI-aware baseline original at `build/review-r2-a198409d/ui2/`; the only complete frame remains the base Menu.
- **Actual vs expected:** no supported wide/tall table or settlement full frames, and no physical mouse/keyboard flow tied to authoritative requests/receipts. Expected is complete supported layouts and the tested menu-to-settlement path.
- **Evidence:** R2 `client-dpi-aware.png` and `player.log`; no new G8 capture was made in Round 12.
- **Repair / acceptance:** implement a DPI-consistent capture/input runner with isolated smoke data and absolute Player log. Capture and visually verify base/wide/tall menu/table/settlement; execute keyboard/mouse through room/game/dialog/settlement, prove at most one valid request per input and no stale/cancel/focus submission, and bind package, frame, state and receipt. Do not change Canvas scaling or treat API-driver calls as physical input.

### D-1-05 — same candidate/package closure (MAJOR)

- **Location:** final candidate/package, 8 DLLs, resources/plugins/BuildReport, and 32-row collector.
- **Reproduction:** compare suite `packages/runs` manifests and row source commits; Round 12 FixtureOnly output is not a Player package.
- **Actual vs expected:** no single final ordinary Windows Player package and no common executable collector cover all required rows. Expected is one candidate, one complete package closure, and all row originals bound to it.
- **Evidence:** main Binding, existing suite package manifests, and Round 12 runner-only change.
- **Repair / acceptance:** finish scenario/visual collector implementation before expensive builds; preserve fresh/clean/verifier guards and make one common-package command run all probes and input captures against the exact same file manifest. Bind source/generated inputs, eight DLLs, all package/resource/plugin hashes, BuildReport, requests, state, receipts and screenshots to every Windows row. Do not splice suite packages or use executable-stub equality. Android nine rows remain excluded/NOT_RUN.

### D-1-04 — G7-01 offline environment (MAJOR, external condition)

- **Location:** C04-G7-01.
- **Reproduction / evidence:** existing unchanged preflight `E:/g2-r4-offline-preflight.txt`, SHA-256 `6E7C9555727E4092C5FB33529ABC2AE960724C0C61E556C35E76C76DF51DB340`.
- **Actual vs expected:** no approved isolated/no-egress environment and no same-package offline match evidence; expected is local full match and measured absence of remote attempts.
- **Repair:** do not alter host-wide network/security settings or repeat the unchanged preflight. Complete every other feasible item first; only when G7-01 is the sole remaining condition should it be reported as an external hold.
- **Acceptance:** same-run environment/process/package/local-transport/no-egress observation bound to final scores, rankings, ledger, and terminal state; otherwise keep NOT_RUN.

No G2 sign-off, Android pass, V4 status change, commit, push, or merge is claimed.
