# V5 LocalHost 451f4ec Integrated Candidate — Independent Review

- Date: 2026-10-29 (Asia/Shanghai).
- Reviewer: independent DeepSeek Harness review; no delegation, no subagent, no caller/receiver native session reuse.
- Candidate: `451f4ec9803cb45c9b01a5724d58718ac6ad1728` (`fix(v5): reconcile pending decision ownership on transitions`, parent `83253fe7f4c3097d091541c69ab067f955e4a1c5`).
- Review base: `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7` (accepted `origin/v5`; candidate ancestor, `git merge-base --is-ancestor` exit 0).
- Candidate tree: `23a5734f8506ab979eb7114875113132678e2bce`.
- Worktree: `D:/majiang-wt/local-gate-451f-ds-20261029`, branch `wt/v5-local-gate-451f-20261029`.
- Evidence root: `build/lh-gate-451f4ec-review/` (ignored).
- Status: **REVIEW COMPLETE — NOT AN ACCEPTANCE VERDICT**. G2–G5 are not claimed; V4 remains production.

## 0. Verdict

| Area | Result |
|---|---|
| Requested non-PG local build/test scope | **GREEN**: all fresh builds and all requested suites passed with 0 failed / 0 skipped / 0 errors / 0 not-run. |
| Cold pending-ownership fix | **No blocking defect found**: pending/timing ownership, atomic rollback, recovery filtering, and cold-process tests passed. |
| D-LH-GATE-01 (control tags) | **Local gate green**: candidate mapper 10/10, reviewer mapper+host probe 4/4, legacy wire rejection 1/1; two MINOR residuals remain. |
| D-LH-GATE-02 (durable stale rejection) | **Local gate green**: `LocalControlTests` 23/23, including dropped-reply outbox and cold-process reconciliation. |
| D-LH-GATE-03 (visibility fixture) | **Local gate green**: all four golden blobs equal the accepted base; candidate companion 4/4 and baseline git-pin companion 4/4. |
| D-LH-GATE-04 (version-capture race) | **Local gate green on non-PG scope**: LocalHost 115/115 twice and the exact cold-version test 5/5 repeats. The original PG-load condition was **NOT_RUN**. |
| G2 | **NOT_RUN / not met**: P4.2–P4.5 Unity/Player/device and full local-flow evidence are absent. |
| Acceptance | None. This report does not accept the candidate; the caller retains review, integration, and acceptance authority. |

No blocking product defect was reproduced in the requested scope. Three MINOR review observations are recorded in §8. The candidate is materially improved, but “all requested local suites are green” is not a G2 pass and not an acceptance decision.

## 1. Inputs read

- `AGENTS.md` (V4 production architecture, seven-module rules, V5 boundary statement, 136-tile conservation).
- `doc/v5-重构/V5-0-1-IMPLEMENTATION-TASKS.md` (current taskbook; P4.1–P4.5 and G2 exit criteria at lines 128–164).
- `doc/v5-重构/DEEPSEEK-V5-MODULE-MIGRATION-PLAN.md` (current V5 migration plan; LocalHost/TableRuntime/Client boundaries).
- `doc/v5-重构/V5-REVIEW-REWORK-PROTOCOL.md` (one complete defect list per round; no skip/old-DLL/`Total=0` green; evidence and口径 rules).
- Earlier gate inputs: `V5-LOCALHOST-BATCH-GATE-INDEPENDENT-REVIEW-20261029.md`, `V5-LOCALHOST-COLD-DECISION-DIAGNOSIS-20261029.md`, `V5-LOCALHOST-COLD-DECISION-FIX-REPORT-20261029.md`, and the caller’s `CODEX-V5-LOCALHOST-GATE-03-04-ACCEPTANCE-20261029.md`.
- Candidate snapshot copies of the same documents, read from the exact archive; no receiver `bin/`, `Library/`, or old test artifact was used.

## 2. Candidate isolation and workspace discipline

`git archive` was used to create an isolated snapshot; the candidate was not merged, checked out, or committed wholesale.

```powershell
git archive --format=zip -o build/lh-gate-451f4ec-review/candidate-451f4ec.zip 451f4ec9803cb45c9b01a5724d58718ac6ad1728
# native exit 0
Expand-Archive -LiteralPath build/lh-gate-451f4ec-review/candidate-451f4ec.zip `
  -DestinationPath build/lh-gate-451f4ec-review/snapshot-451f4ec
```

| Item | Value |
|---|---|
| Archive SHA-256 | `9AECD693F4C0E0C87EC55EE769CA07BF3C5F3A5DF3FB48965A2F1C49D40C3EAA` |
| Extracted files | 2,314 |
| `.git` in snapshot | absent (`Test-Path` false) |
| Candidate net diff vs base | 582 paths, `+187022/−1622` (includes PR20 Unity/docs/B1 material; this review is scoped below) |
| Cold-fix diff vs `83253fe` | 4 files, `+374/−56`: `TableActor.cs`, `TableResponseDecisionReconciliation.cs`, `TableRuntime.Tests/TableResponseDecisionInvalidationTests.cs`, `LocalHost.Tests/LocalControlTests.cs` |
| Review worktree tracked status | clean before this report; only this ignored evidence and this report were produced |

The candidate is broader than the cold-decision fix. This report verifies the LocalHost/TableRuntime/Client/Architecture/Contracts/TableService paths relevant to the cold fix and prior D-LH-GATE defects. It does not validate the candidate’s Unity/Player/B1 content as a whole.

Constraints observed: no push, PR update, merge, deployment, delegation, machine-wide trust change, Unity Resources/Burst modification, or modification of another worktree. The only tracked deliverable in this worktree is this report, committed on top of `8fd7c6c`; no candidate source is committed. Evidence is isolated under the ignored `build/` directory.

## 3. Fresh Release builds

All builds used `D:/Unity/dotnet/dotnet.exe` SDK `8.0.426`, Release, `--no-incremental --nologo -m:1 -nr:false -p:UseSharedCompilation=false`. Logs are under the evidence root; native exits and zero-warning/zero-error results are in `build-summary.csv`.

- `Mahjong.sln`: native exit 0, 0 warnings, 0 errors.
- Unlisted test/worker projects built separately: `Mahjong.Architecture.Tests`, `Mahjong.Client.Wss.Tests`, `Mahjong.LocalHost.Tests`, `Mahjong.LocalHost.CrashWorker`, `Mahjong.TableRuntime.Tests`, `Mahjong.Unity.TableModel.Tests`: all native exit 0, 0 warnings, 0 errors.
- The fresh worker at `dotnet/tests/Mahjong.LocalHost.CrashWorker/bin/Release/net8.0/Mahjong.LocalHost.CrashWorker.dll` (SHA-256 `B08F36355ED5B07035CA9CC5F2A271C3A0EBFB1B5F41C38C83C34AC220DDEFAC`) was present for the cold-process tests; the tests assert its existence and launch it through `D:/Unity/dotnet/dotnet.exe`.

Test execution used the direct xUnit v3 DLL runner, not `dotnet test` and not an old build:

```powershell
D:/Unity/dotnet/dotnet.exe <fresh test DLL> -noLogo -reporter default -result-xml <xml>
# focused: add -class <class> / -method <method> -parallelMode none
```

Every XML was parsed from `/assemblies/assembly` and checked for nonzero total, exact pass/fail/skip/error/not-run counts, and native exit.

## 4. Complete non-PG suites

All rows below are direct runs of fresh Release DLLs. `test-counts.csv` contains the machine-readable table and XML SHA-256 values.

| Suite | Total | Passed | Failed | Skipped | Errors | Not-run | Exit | XML SHA-256 |
|---|---:|---:|---:|---:|---:|---:|---:|---|
| TableRuntime | 453 | 453 | 0 | 0 | 0 | 0 | 0 | `EB417900982DEE8322A37DCC0243D7BE3F64B9567D75723D6FF8416608CB83FB` |
| LocalHost | 115 | 115 | 0 | 0 | 0 | 0 | 0 | `F74C3D1D603E1C38C85D83F501F1BA06B6F4A81DFB11EB23D3C8AF9B9D2DA8C5` |
| Client | 182 | 182 | 0 | 0 | 0 | 0 | 0 | `30DFE9FDABC53B24160F6CFF2C098D8BAA0F598D10D3A2A292C79882D6E974ED` |
| Architecture | 132 | 132 | 0 | 0 | 0 | 0 | 0 | `A56CF5C0BF3D41824F7885113E0C34BBDB593C8526A3C1273AED49A1F81CBE39` |
| Client.Wss | 80 | 80 | 0 | 0 | 0 | 0 | 0 | `E06761A3A5E862719843902C5F069921EC704DDEF37F142C78CF26CD87262E4C` |
| TableService | 171 | 171 | 0 | 0 | 0 | 0 | 0 | `310EB3E3F1040BC98AFCB7C2F66DE8D461F221A8BC530775237EBC38008E4E59` |
| Contracts | 150 | 150 | 0 | 0 | 0 | 0 | 0 | `0833CFF3BC2D165300AF55E458F18D5E98BD47BE812B702B778AA3CF6BAAE029` |
| Unity.TableModel | 140 | 140 | 0 | 0 | 0 | 0 | 0 | `E05A9C04CB2EB32F2B67F5082A78C8DB0FBB2B89BE40524A36ECF67CA2DA3CAD` |

Totals: **1,423 passed / 0 failed / 0 skipped / 0 errors / 0 not-run**. This includes 4 reviewer probe tests in the TableService row; the candidate baseline TableService run before those probes were added was **167/167**, XML SHA `ABB30220C5C08016803C023402228D6F972268B0EC913F478DCD4362938D217E`. Excluding the 4 reviewer probes, the candidate-only total is **1,419**.

A second independent full LocalHost run passed **115/115**, exit 0, XML SHA `709E3F94F474D16A318ADB8B3AC479FFB4135AA8AF442EAFF0562C81F17BBC76`. No suite reported skip, not-run, or `Total=0`.

## 5. Focused probes

| Probe | Result | Exit | Evidence |
|---|---|---:|---|
| `TableResponseDecisionInvalidationTests` class | 22/22 | 0 | `runs/probe-tableruntime-ownership/ownership-class.xml`, SHA `C50EDB19EDA507ABFCEB1935AA4208809C330F849F28A3BEDAEBE51818872A92` |
| `LocalControlTests` class | 23/23 | 0 | `runs/probe-localhost-control/control-class.xml`, SHA `718F2D5789E9D75E98AE057B63EA7903C7638672DDCA7325BC03C2EF498B81ED` |
| Eight cold/outbox/furiten/worker cases | 8/8 | 0 each | `runs/probe-localhost-cold/cold-cases.csv` |
| `ColdRestartRestoresCommittedStateVersion` repeats | 5/5 | 0 each | `runs/probe-gate04-repeat/repeat-summary.csv` |
| Candidate `TableCommandMapperControlContractTests` | 10/10 | 0 | `runs/probe-durablehost-mapper/mapper-class.xml`, SHA `21DCCB9DA6640E389DEB9AEFFF9C3FBF908C12D121B26D8C9EF3F71D07DC4FCA` |
| Reviewer mapper + host-level control probe | 4/4 | 0 | `runs/probe-tableservice-gate01/probe-class-r4.xml`, SHA `018248276E5FB41A14601418F0D158CCCD436969BE2A442D67B61DB5ABC22303` |
| Legacy control-tag wire rejection | 1/1 | 0 | `runs/probe-tableservice-gate01/legacy-wire.xml`, SHA `798C37E338226D64681159EAB1B14A2529DE1B50D583DDFBC901A43DACC3441C` |

The eight cold cases were:

- `PersistedStaleControlRejectionSurvivesColdProcessRestartUnchanged`
- `DroppedStaleControlRejectionReconcilesFromPersistentOutboxOnReconnect`
- `ControlOutboxIsReconciledByASeparateColdProcess`
- `PersistedStaleControlRejectionExactReplayAndConflictKeepSingleRecord`
- `ClientPassFuritenBlocksSecondRealRonUntilTheNextDraw` (D-LH-CI-COLD)
- `ClientPassOnRonSetsFuritenAndColdRestartKeepsItUntilTheNextDraw`
- `PendingChiCandidateSurvivesColdRecoveryAndSubmitsSuccessfully`
- `CrashWorkerRealProcessKillLeavesDurableReceipt`

Each was a separate direct run, native exit 0, 1/1. The worker path above was resolved and launched by the candidate tests; no in-process `dispose/new` substitute was used for the cold cases.

### 5.1 Pending ownership and atomic rollback

The 22-test class includes, and passed:

- `TurnDecisionIsRemovedAtomicallyWhenCommitAdvancesCurrentSeat`
- `FailedTurnCommitLeavesPendingAndTimingUntouchedThenRetries`
- `FinishedRoundClearsEveryPendingAndTimingInTheSameCommit`
- `ReviewerStaleTurnMustNotSurviveNewResponseWindow`
- `ResponseDecisionFromDifferentWindowIdentityIsDroppedDespiteSeatOverlap`
- `ClosedWindowDropsStaleDecisionAndTimingInTheSameCommit`
- `CurrentLegalClosedKanSurvivesRecovery`
- `CurrentLegalAddedKanSurvivesRecovery`
- `FailedCommitDoesNotConsumeOrInvalidateAnyResponseDecision`
- `DurableCommitFaultLeavesPendingAndReserveUntouchedThenRetries`

Source review agrees with the tests:

- `TableResponseDecisionReconciliation.FilterAfterTransition` receives the prior and next `RoundStateMachine` and shares one `IsStillOwned` predicate with `FilterRecovered` (`TableResponseDecisionReconciliation.cs:23-167`).
- Rejected decisions are cancelled on the **candidate** `DecisionClock` inside the same transition, never on the installed clock (`:76-80`).
- `TableActor` passes the still-prior `_round` at all three commit paths (`TableActor.cs:3596`, `:3830`, `:4094`), builds pending/timing from `candidateClock`, commits the timing snapshot with the durable request (`:3838-3911`), and installs `_round`, clock, timing version, and pending only after a durable commit (`:3965-3983`).
- A failed or cancelled commit returns before installation; the tests above exercise the failure path.

No atomicity or ownership defect was reproduced.

## 6. Prior D-LH-GATE defects

### D-LH-GATE-01 — control tags

- Candidate `TableCommandMapper.MapControl` now reads frozen tags 20/21, rejects legacy tag 1, and produces `TableCommandRoute.Control` with `Reclaim` and `ExpectedControlEpoch` (`TableCommandMapper.cs:618-647`).
- Candidate `TableCommandMapperControlContractTests`: **10/10** (2 positive, 8 negative), exit 0, no PG fixture started; this is a pure mapper class inside the durable-host test project.
- Reviewer probe: factory `ClaimControl`/`ReleaseControl` map to `Control` with exact epoch/reason; legacy tag 1 returns `PreActorRejection` with `PAYLOAD_INVALID`; host-level `TableTestClient` receives an authoritative receipt. **4/4**, exit 0. The probe deliberately uses `CommandPayloadFactory` (contract tags), not duplicated literals.
- Existing `SessionFlowTests.ControlCommandKind_IsGatedByTheFrozenFieldContract` still rejects the old wire shape: **1/1**, exit 0.
- Full TableService: 171/171 (167 candidate baseline + 4 reviewer probes), exit 0.

Residual MINOR observations are in §8.1 and §8.2; they do not reproduce the old `PAYLOAD_INVALID` failure for valid 20/21 commands.

### D-LH-GATE-02 — durable stale-decision rejection

- `LocalTableRuntime.RejectStaleDecisionAsync` now checks an already-committed durable receipt first (exact replay replays; different bytes/kind conflict), then validates scope, then persists the definitive rejection before publishing it (`LocalTableRuntime.cs:1118-1251`).
- `RejectDurably` uses the request idempotency key, leaves the record untouched on an existing result, and emits a transport error if the durable commit fails rather than claiming durability (`:1192-1251`).
- `LocalControlTests` **23/23**, including the dropped-reply reconnect case, cold-process restart, exact replay/conflict, expired/revoked scope, concurrent rejections, and commit-failure semantics.
- The exact cold test that failed earlier, `PersistedStaleControlRejectionSurvivesColdProcessRestartUnchanged`, passed 1/1 with the worker.

### D-LH-GATE-03 — frozen visibility fixture

- `git diff --name-status 8fd7c6c..451f4ec -- tests/v5-golden` is empty (exit 0).
- All four companion blobs equal the base:

```text
movement-v1.json        25331005f30edfb2870cba8ee4a1b582400f346b
shuffle-reversed-v1.json 16ae21dafdf487707bf8f4064a879de22779654f
visibility-v1.json      eddcc00035978be64bca27efc27ec352ce686bf5
ai-discard-flags-v1.json ef329f5562b47e6e1f9304f247d7a3bb36cf7125
```

- Candidate snapshot companion: **4/4 PASS**, native exit 0 (`companion-candidate.log`).
- Base worktree companion with `-VerifyGitPins`: **4/4 PASS**, native exit 0 (`companion-baseline-gitpins.log`), including the historical freeze blobs.
- The archive has no `.git`, so `-VerifyGitPins` could not be run against the candidate exact tree itself. The candidate fixture bytes were verified independently and their blobs are identical to the accepted base; that is sufficient to show D-LH-GATE-03’s in-place rewrite is gone, but it is not a substitute for a candidate-repository pin check.

### D-LH-GATE-04 — cold version-capture race

- `ColdRestartRestoresCommittedStateVersion` now takes `discard.CommandVersion` from the resolved receipt and waits for the quiescent human/terminal boundary before sampling, instead of reading an arbitrary snapshot version after the discard (`LocalHostRecoveryTests.cs:24-69`, helper `:794-814`).
- `AssertRecoveryBoundaryAsync` retains exact equality between the durable row/checkpoint/event versions and the recovered snapshot; the final assertions were not weakened to `>=` or `>0`.
- Full LocalHost: **115/115** twice. Exact test: **5/5** sequential runs, each 1/1, exits 0. XML SHAs are in `repeat-summary.csv`.
- The original PG-load failure (`saved=2 committed=1`) was not reproduced because PostgreSQL is explicitly out of this task’s scope. This report does **not** claim the PG-load condition is closed; it records that the non-PG local test is stable and the source-level version capture is corrected.

## 7. Static review of the cold fix

The fix is directionally correct and narrower than a wholesale candidate merge:

- The transition and recovery paths now share one ownership predicate, preventing the previous rule drift between `FilterAfterTransition` and `FilterRecovered`.
- A new response window drops every pre-window pending, including a stale turn at a seat that is eligible in the new window; a same-identity open window retains only genuine unresponded responses; a closed window drops all pre-window decisions; a no-window state retains only the current seat’s turn decision with a turn anchor or legal turn-kan shape.
- `TableActor.ProcessOpenDecisionAsync` still accepts a stale host open by contract and relies on the next owning transition to invalidate it. The earlier diagnosis identified this as a defense-in-depth option; the candidate intentionally leaves it unchanged. The regression `ReviewerStaleTurnMustNotSurviveNewResponseWindow` covers the reported consequence.
- No `sleep`, retry loop, skip, or assertion relaxation was added to obtain green. The waiting helper is event-driven and uses a version floor only to avoid the pre-discard snapshot; the final boundary assertions remain exact.

No product-code defect was found in this review of the fix. Two contract-maintainability observations remain in §8.

## 8. Findings

### 8.1 MINOR — duplicated frozen control tag literals

`Mahjong.TableService/TableCommandMapper.cs:169-176` defines local `ControlRoundIdField = 20` and `ControlExpectedEpochField = 21`, and `MapControl` uses them at `:637-639`. The candidate also contains `CommandPayloadFields.ControlRoundId` / `ExpectedControlEpochV2`. The candidate’s own `TableCommandMapperControlContractTests` repeats the same literals (`PostgresDurableTableHostTests.cs:798-800`), so it would not notice a future contract tag change. The reviewer probe uses `CommandPayloadFactory` and passes today, so this is a maintainability/drift risk, not a current behavior failure.

**Suggestion:** use the frozen `CommandPayloadFields` constants (or a generated single source of truth) and keep the negative/mixed-tag matrix. No code change was made in this review.

### 8.2 MINOR — control `roundId` is parsed but not bound to the route

`MapControl` requires string tag 20 (`ControlRoundId`) and uint64 tag 21, but `TableCommandRoute.Control` carries only seat/owner epoch/expected control epoch/reason; it drops the parsed round id (`TableCommandMapper.cs:637-646`, route definition `:89-108`). `TableSessionHandler` consumes the route without a round-id comparison (`TableSessionHandler.cs:1483-1494`). Control is therefore seat/epoch-bound, not round-bound. No failing behavior was observed (the actor fences owner/control epochs and the LocalHost/TableService tests pass), and the earlier D-LH-GATE-01 requirement made round binding optional. Confirm with the contract owner whether a control command for an unrelated round should be refused; if yes, add a route/actor fence and a negative test.

### 8.3 MINOR — candidate tree is not `git diff --check` clean

`git diff --check 8fd7c6c..451f4ec` exits **2** with **33** whitespace warnings, all in candidate docs/patch/log files:

- `doc/v5-重构/V5-LOCALHOST-COLD-DECISION-FIX-20261029.patch`: 16
- `doc/v5-重构/patches/V5-B1-FIRST-RECEIPT-DIGEST-FIX-20261029.patch`: 14
- three B1 evidence `.log` files: 1 each

The code-only check `git diff --check ... -- '*.cs' '*.csproj' '*.json'` exits 0 with no output. This is a hygiene/documentation finding, not a product-code failure.

## 9. Reviewer-side failures preserved

These are probe-authoring failures, not candidate test failures. They are preserved rather than hidden.

1. **Initial TableService probe expectation was wrong.** The probe expected `CanonicalEncodingException` for a legacy tag-1 payload, but the public `Map` validator returns `PreActorRejection` with `PAYLOAD_INVALID` before `MapControl` is reached. Result: 3 total / 2 passed / 1 failed, native exit 1. XML SHA `9081209B8F784D26547272B82B6DD03468378883D89E195B91D915D83C124A5B`; log preserved as `probe-class-initial-wrong-expectation.log`. The expectation was corrected in the ignored probe; the corrected r2 run was 3/3 exit 0, and the final host-level probe r4 was 4/4 exit 0.
2. **Host-level probe initially failed to compile.** `ReceiptStatus` was ambiguous between `Mahjong.Contracts` and `Mahjong.Contracts.Wire`; native exit 1 with 2 CS0104 errors (`build-probe-tableservice-r3.log`). The ignored probe was qualified and rebuilt; r4 exit 0. No candidate source was changed.
3. **Candidate whitespace check** is recorded in §8.3; it is the only candidate-tree failure found, and it is docs/patch-only.

No candidate suite was rerun to turn a candidate failure into green. The full LocalHost second run and the 5 gate-04 repeats are stability samples, not retries of a failure.

## 10. NOT_RUN and remaining G2 prerequisites

The following are **not** results and must not be read as PASS:

1. **Default unified gate / PostgreSQL / B1:** not run. The separate B1 task owns PostgreSQL resources. No `pg_ctl`, PostgreSQL cluster, table PG wrapper, or lobby PG wrapper was started. TableService’s full PG durable-host suite is NOT_RUN; only the pure 10-test mapper class was run without PG.
2. **D-LH-GATE-04 original PG-load reproduction:** NOT_RUN. The local non-PG stability evidence is real, but it does not recreate the earlier PG-load `saved=2 committed=1` condition.
3. **Unity Editor / PlayMode / Player / IL2CPP / Android / iOS / real device / cross-physical-network:** NOT_RUN. `Unity.TableModel` 140/140 is a pure .NET model test, not Unity Editor or PlayMode evidence. The candidate’s Unity PlayMode tests were not executed.
4. **P4.2–P4.5:** not accepted by this report. G2 requires P4.1–P4.5, a real menu → local room → full match → next round/match → settlement flow, three cheat abilities, AI takeover/reclaim, disconnect/local play, kill/restart, corrupt-save recovery, desktop/Android build/run records, and a `V5-2-REVIEW.md` with versions/hardware/commands/results. None of those are provided here.
5. **P4.1 full single-player whole-match exit:** the LocalHost unit/integration suites cover many scenarios, including cold recovery and control, but this review did not execute a manual/automated whole-match UI flow. A green unit suite is not the P4.1 exit criterion.
6. **Remote WSS/TCP end-to-end, TLS, public network, server restart, remote CI:** NOT_RUN. `Client.Wss` 80/80 is local transport testing only. No remote CI artifact was fetched or claimed.
7. **Candidate exact-tree companion `-VerifyGitPins`:** not possible from the archive (no `.git`). Baseline pins and exact blob equality were used instead; see §6 D-LH-GATE-03.
8. **G2–G5:** not claimed. G1 remains the existing approved stage; G2–G5 are not approved; V4 remains the only production baseline.

## 11. Evidence index

Evidence root (ignored): `D:/majiang-wt/local-gate-451f-ds-20261029/build/lh-gate-451f4ec-review/`.

| File | Purpose | SHA-256 |
|---|---|---|
| `candidate-451f4ec.zip` | exact `git archive` of candidate | `9AECD693F4C0E0C87EC55EE769CA07BF3C5F3A5DF3FB48965A2F1C49D40C3EAA` |
| `commands.txt` | exact commands and native exits, including failures | `97DAA09EAD52AC08774DDF3BF062A5A232C74F1A30CF727E7E005D01DA0FF82F` |
| `all-runs.csv` | every XML row with counts/exit/SHA | `6EB36D54289C9245DEDF75E0B3145BB60A901D689DE87ABC601E3B78BDD97A91` |
| `test-counts.csv` | complete non-PG suite counts | `95CD3672CBC1FF9FA99E897964070643C76E1E32CAD5620FA81F4D0F5AAED385` |
| `build-summary.csv` | build exits, warnings, errors | see manifest |
| `fresh-dll-sha256.csv` | fresh production/test/worker DLL SHA | see manifest |
| `summary.json` | machine-readable review summary | `D9CE413263615D43B3AA64D65A6A46DDD2432AF69658A1F163B8AE4E513AF901` |
| `companion-candidate.log` | candidate companion 4/4 | `67EBF542161BFB17A935E1609B38A2B17006FE7C5828E61F6224E92F5C07F681` |
| `companion-baseline-gitpins.log` | base companion 4/4 with git pins | `6377693CF7E081FCE642DA62A4E7F2776D0C94015F5F1AEDB9016943F91784F0` |
| `candidate-diff-check.log` | full diff-check output (exit 2, 33 warnings) | see manifest |
| `candidate-diff-check-code.log` | code-only diff-check (exit 0) | see manifest |
| `manifest-verify.log` | 95 manifest rows rehashed, 0 mismatch/missing | `CC501E7CAB28EA9723F18523FEEDBEE05DB3B3BBC496A30BF3364FEBE9771287` |
| `evidence-sha256.csv` | evidence manifest, 95 rows, self excluded | `D7F068B218A296CC0DA4E7026B616C82168069090FA188496D8A645B42F20986` |

`manifest-verify.log` reports **95 OK / 0 mismatch / 0 missing** for all evidence files except the manifest itself and the verify log. XML files, logs, and CSV rows preserve the exact native exits, counts, and SHA values; no artifact was rewritten after verification.

## 12. Conclusion

- The requested non-PG local review is complete: fresh Release builds pass, the complete requested suites pass, and the focused pending-ownership, atomic-rollback, cold-recovery, and prior D-LH-GATE probes pass.
- The cold-decision fix at `451f4ec` closes the reproduced pending-ownership failure: the parent/runtime no longer retains a turn decision after the turn moves, recovery and transition share the same ownership predicate, and pending/timing changes are committed atomically.
- D-LH-GATE-01/02/03/04 are locally green on the evidence above. D-LH-GATE-04’s original PG-load condition remains un-reproduced and is explicitly NOT_RUN.
- Three MINOR observations remain: duplicated control tag literals, an unbound control `roundId`, and docs/patch whitespace. None is a reproduced product failure; the first two should be resolved or explicitly accepted by the contract owner.
- **This is a review input, not an acceptance verdict.** The candidate is not accepted by this report; the caller independently reviews and integrates it into the existing PR20. G2–G5 remain unapproved; V4 remains the production baseline.
