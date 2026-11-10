# G2 Windows Luna6 execution supplement — Round 10

Status: **NOT ACCEPTED**. Round 10 began at bridge-created clean HEAD `4d277c1f474a0eacd2c93c0c0ed11bea61a1b6bf` (parent `4caa642ff5f9d184c4cfcb39a315ad3fcd803508`, original base `4523485cfd4ec2b32185a7da5c13f6cbaf54a1e1`). The prior round's source and fixture changes are in this HEAD. D-8-01 contract and D-9-01 fixture timeout were independently verified in Round 9 and are closed; D-6-01 remains closed. No new defect ID was confirmed in Round 10.

## Round 10 code change

`unity/Mahjong.Unity/Assets/Scripts/App/LocalTableChannelFactory.cs` changes the opt-in C04 scenario documentation from “four” to “five”, matching the fixed scenario set after D-8-01 added the dealer-Tsumo case. No runtime behavior changed. No tests or Unity run were necessary for this comment-only correction. The worktree is dirty with this file and this supplement plus task/delivery/binding ledger updates; no manual commit, push, or merge was made.

## Open MAJOR items (no status improvement in this round)

### D-1-02 — real game/control/service row execution

- **Location:** C04 Census G1-01/02/03, G2-01..04, G4-01..03 and final G7-02 binding.
- **Reproduction:** current Binding remains 18 PASS / 11 PARTIAL / 3 NOT_RUN; see the R9 supplement and the listed probes/runners.
- **Actual vs expected:** G1 dealer-Tsumo and G2 four action scenarios have corrected tool contracts, but their new ordinary Player GAME evidence has not been run. Deterministic exhaustive draw/dealer continuation with a fresh `roundId` and G2 chankan/rinshan remain unimplemented as Player scenarios. The G4 real Player probe exists at `unity/Mahjong.Unity/Assets/Scripts/App/LocalControlPlayerProbe.cs` with runner `tools/unity/test-p45-control-ui.ps1`, but it has not run on the final candidate. G7-02 LocalHost service-boundary evidence was accepted as support in Round 8/R6; its final candidate row binding remains open.
- **Evidence:** R9 checked support manifest `E:/g2-r9-d8-20261109/round9-review-manifest.json` (SHA-256 `F898481E7D71829525344F73CB4001B6496FFE93731C386D34FA6B80D0CAB592`); no Unity Player evidence was added in Round 10.
- **Repair:** add deterministic production-channel G1 exhaustive draw/dealer-repeat with new-round identity and G2 added-kan/chankan/rinshan scenes plus case-specific invalid-action zero-change checks. Run the current five action GAME cases, their verifier mutation matrix, and existing G4 control probe on the frozen candidate. Preserve the accepted G7-02 originals and bind them rather than re-running unchanged tests.
- **Acceptance:** all cited Census state/request/receipt assertions have real nonzero Player/PlayMode/service evidence and zero skips; initial inventory is 136; dealer continuation uses a new `roundId`; G2 includes positive and invalid zero-change evidence; G4 proves old AI/player decisions are fenced by epoch, same-room reconciliation, and explicit claim. Existing deadlines, idempotency, privacy, and terminal invariants remain intact.

### D-1-01 — G8 layout and physical input

- **Location:** C04-G8-01/02.
- **Reproduction:** compare the current Binding with the DPI-aware originals at `build/review-r2-a198409d/ui2/`.
- **Actual vs expected:** only the complete base Menu has visual evidence. Supported base/wide/tall table and settlement frames and physical keyboard/mouse menu-to-settlement input bound to authoritative receipts remain absent.
- **Repair:** use one DPI context for capture, client measurement, coordinates, and input; unique smoke data and absolute Player log. Do not alter Canvas scaling or substitute API-driver input for physical input.
- **Acceptance:** visually complete supported-layout frames; real keyboard/mouse reaches room, game, settlement, and dialogs; each input causes at most one valid request and cancel/focus/old input causes none; bind package, screenshot, state, and receipt.

### D-1-05 — common candidate/package closure

- **Location:** final candidate, ordinary Player package, and all 32 Windows Binding rows.
- **Actual vs expected:** no single final Player package contains the required evidence. Existing runner packages remain support evidence only.
- **Repair:** after the missing scenarios and collector are implemented, use the bridge clean-candidate lifecycle and original fresh/clean/verifier guards to execute one common package; preserve all first failures and stable originals.
- **Acceptance:** candidate and generated inputs, eight DLLs, full package/resources/plugins, BuildReport and complete file hashes bind to every required request/state/receipt/screenshot. No package splicing, stale DLL, skip, or refreshed-summary substitution. Android nine rows remain excluded/NOT_RUN.

### D-1-04 — G7-01 external offline environment

- **Location:** C04-G7-01.
- **Actual vs expected:** the unchanged offline preflight at `E:/g2-r4-offline-preflight.txt` (SHA-256 `6E7C9555727E4092C5FB33529ABC2AE960724C0C61E556C35E76C76DF51DB340`) records no approved isolated environment; no offline match/no-egress evidence exists. Keep NOT_RUN.
- **Repair:** do not modify host-wide networking/security or repeat the unchanged preflight. Complete all feasible implementation and execution first; report this only as the remaining external condition if it is the sole open item.
- **Acceptance:** same-run environment/process/package/local transport/no-egress observation binds to full match scores, ranking, ledger, and terminal state.

Round 10 did not run Unity, Player, PlayMode, or the common package matrix. This supplement makes no Windows row progress claim. Exact prior D-8/D-9 commands, test counts, first failures, and hashes remain in the R9 supplement. No Android result, G2 sign-off, or V4 status change is asserted.
