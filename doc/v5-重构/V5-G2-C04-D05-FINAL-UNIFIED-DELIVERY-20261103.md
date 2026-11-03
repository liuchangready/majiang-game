# Round 4 authoritative status (2026-11-03)

**Final status: NOT ACCEPTED.** R4 continued the same-source repair after the R3 independent review. The released-control producer/consumer identity contract and default runner path-boundary checks now pass their cheap behavior matrices, and the PlayMode producer has the same structured reconnect fields as the Windows driver. However, a real PlayMode run at code head `598ea2cdd9841658536c500281a6dc08674a97b1` still failed 6/7 at the released-control recovery assertion. A diagnostic-only follow-up at `6b4dc2436755382fa792cd9720a204a63fa9e16d` timed out during Unity project import before tests began, so it did not reveal which recovered field differed. No final ordinary Windows package/P1/P2 run was performed. FU-02 remains an unresolved out-of-allowlist diagnosis. The five groups are reported below; no production or out-of-allowlist file was changed.

This section supersedes Round 3's current-status statements. The prior Round 3, Round 2, and Round 1 sections remain unchanged as history and are not evidence for the R4 code head.

## Round 4 source identity and changes

| Item | Value |
|---|---|
| Starting R4 code head | `86cb2c4623aa10d0a316d60a750c3dcb30b4a41d` |
| R4 code commits | `fa0fcca7` released-control evidence and serialized recovery; `02e8e0e1` producer output scope correction; `598ea2cd` mirror reconnect evidence in PlayMode; `6b4dc243` include failed recovery field values in the assertion message |
| Current code head | `6b4dc2436755382fa792cd9720a204a63fa9e16d` |
| Current delivery report identity | This report is a docs-only change committed separately after code head `6b4dc243`; runner source identity must continue to use the code head, never the docs-only commit |
| Changed code paths from the R3 code head | `tools/tests/C04PrivacyPlayer.Tests.ps1`; `tools/tests/C04PrivacyRunner.Tests.ps1`; `tools/unity/run-p45-c04-privacy-matrix.ps1`; `unity/Mahjong.Unity/Assets/Scripts/App/C04PrivacyPlayerDriver.cs`; `unity/Mahjong.Unity/Assets/Tests/PlayMode/C04PrivacyRoleSwitchPlayModeTests.cs` |
| Scope | All five code paths are within the original 11-path allowlist. No production Client/Domain/Contracts/LocalHost authority, out-of-list test, schema, asmdef, machine configuration, or unrelated tracked path was changed. |
| Unity source closure | 540 files / 14,195,023 bytes / SHA-256 `aa92a5cfe1939042375738a166e4aa33333619e6fa19d934efbca7154a4170b3` |
| Latest staged Unity inputs | 366 files / SHA-256 `df4b32c74d91236ea4c45fe78c06524bc5cc425a547bc9cfe63a990b0f22ff21` (the timed-out run did not complete import/compile, so this is input identity, not a build result) |
| Toolchain | Unity `6000.3.27f1` (`6000.3.27.6761814`), Unity dotnet `8.0.3226.50405`, SDK `8.0.426` |
| Diff and tree | `git diff --check b92c7d01..6b4dc243` exit 0; source tree was clean before this docs-only change |

## Round 4 stage evidence

### B/C: producer/consumer contracts and negative matrices

- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/C04PrivacyRunner.Tests.ps1` completed with native exit 0, **27/27**. Default output root: `build/runner-tests-3ae839c814724fd7a191659cbb68e684`. It exercises the actual semantic evidence assertion with fixture-only producer documents, including the positive seven-case schema, missing/empty/wrong-identity evidence, false deadline progress, meaningless extra file, and released-control receipt/epoch/session mutations. These fixtures are explicitly not GAME evidence.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/C04PrivacyPlayer.Tests.ps1 -RunRoot build/854a85d6590d47e98f23ca18bdff5d1d` completed with native exit 0, **32/32 source-contract checks**. Summary SHA-256 `B76B7C838A345D3DED17F9F279BB901BF247E8769FF1C63AA6C1C1E5BE198F75`. This verifies source wiring only, not a Windows Player run.
- R3 FU-03 was corrected by separating ClaimControl's `Recorded/LOCAL_CONTROL` receipt from the stale command's `Rejected/DECISION_MISMATCH` receipt and by binding the captured decision epoch, session/scope/binding, and single stale frame to the semantic consumer. The deadline artifact remains structured; the evidence-count threshold was not relaxed.
- R3 FU-04's default fixture path failure was corrected by compact fixture directory names with a pre-write path-budget rejection. The default runner command above now passes. This does not establish that the real Windows save/outbox paths work in the ordinary Player package.
- The negative matrix still rejects: receipt identity conflation; shared arbitrary wrong reason; captured/stale epoch mismatch; non-advancing claim epoch; snapshot owner/epoch mismatch; stale commit metadata; duplicate stale frames or pending commands; wrong scope/session/round/decision; missing/empty/invalid deadline progress; meaningless extra files; and over-budget output roots. Accepted verifier semantics were not changed.

### D: current-source PlayMode attempts

First current-source run command:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-c04-privacy-matrix.ps1 -ExpectedSourceHead 598ea2cdd9841658536c500281a6dc08674a97b1 -RunRoot build/aa99e58c0e6f4c2bab255f6d4d647920 -TimeoutSeconds 1200
```

It acquired the shared mutex after 513 seconds, fresh-built shared plugin provenance (native exit 0), and Unity exited native `2` after 346.889 seconds. The XML has the exact seven tests, **6 passed / 1 failed / 0 skipped**. The failure is `RealRoom_ReleasedSeatAiAdvancesAfterPlayerCloseWithSpectatorOnly`, at the formal reconnect identity/fresh-Ready-baseline assertion. XML SHA-256 `F59CE510FF800BD7CFEE839A36DBA39083BD6168120CBD01FD13A8F5A1694E8F`; runner summary SHA-256 `B658F6051E86BF31D4B71123A85A3C07D6E31D7F717BDF64D94C6E3443DE24E2`; stable run manifest SHA-256 `69D775CA036323767D38764A42675AC60CCFD183AA6EBF54E98B949D59F6CF6E` under `build/aa99e58c0e6f4c2bab255f6d4d647920/`.

The failed condition includes resumed session ID, scope ID, binding epoch, snapshot control epoch, transport-open state, pending count, last error, and pump error. The original R4 failure only wrote the combined assertion, so the exact false field was not persisted. A diagnostic-only code change in `6b4dc243` added those values to the thrown message.

Diagnostic follow-up command:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-c04-privacy-matrix.ps1 -ExpectedSourceHead 6b4dc2436755382fa792cd9720a204a63fa9e16d -RunRoot build/7f42c7d712d54f5cb8b6954cd39238b4 -TimeoutSeconds 1200
```

This attempt acquired the mutex immediately and completed verifier build/self-test and shared-plugin build with native exit 0, but Unity was stopped by the runner's owned-process timeout at elapsed 2709.788 seconds while importing the staged project. No PlayMode test XML or new application result was produced. `summary.json` SHA-256 is `46FD019B58BAE9DF4DFA769E7D175CDC0BE63089B1BCD42AB8FFD63805D9CDB3`; stable manifest SHA-256 `C8516C20FE46FCB91DB2E86CE53824A2F09E7F429BE262D13625945E196E23C8`. The runner recorded PID `13252`, exited/cleaned it, released the mutex and staging drive, and sealed the output. The timeout run is preserved; no unchanged Unity retry was made.

### E: current status and fixed denominator

No final ordinary `Mahjong.exe` build or Windows matrix was run because current-source PlayMode has a real failure and the diagnostic rerun did not reach tests. No Test Player XML is substituted. No current-source package manifest, eight-DLL comparison, P1, or P2 process-restart/outbox evidence exists. Therefore every final matrix row N1-N8, U1-U3, P1-P2, and C04-G5-01..03 remains **BLOCKED**; this does not change the original denominators or turn tool/fixture results into application PASS.

## Round 4 consolidated findings and repair mapping

### D05-FU-01 — BLOCKER — final unified same-source application matrix incomplete

- **位置 / reproduction:** `tools/unity/run-p45-c04-privacy-matrix.ps1`; reproduce the first result with the exact `598ea2c` PlayMode command above. The exact `6b4dc243` diagnostic command above timed out before test execution.
- **Actual vs expected:** one real PlayMode case fails (7 total / 6 pass / 1 fail / 0 skip); the current diagnostic run produced no XML; no final ordinary Windows package, P1, P2, eight-DLL comparison, or per-row final proof exists. Expected: final frozen source, seven exact PlayMode cases passing, and a single immutable ordinary `Mahjong.exe` package for all seven Windows cases plus P2 restart/query.
- **Evidence:** `build/aa99e58c0e6f4c2bab255f6d4d647920/playmode-results.xml`, `summary.json`, `manifest.sha256`; `build/7f42c7d712d54f5cb8b6954cd39238b4/summary.json`, `manifest.sha256`.
- **Repair required:** resolve and prove the released-control recovery failure in authorized glue; only then, from the new frozen clean code head, run the final seven-case PlayMode and one-build ordinary Windows matrix. Preserve package files and the eight shared DLL hashes before/after, and exercise P2 using the original request/scope/canonical payload/outbox query across real process restart.
- **Acceptance assertions:** exact seven names 7/7 with zero skips; ordinary Windows same package for all scenarios; P2 original receipt and exactly-once side effect with before/after PID/start and package manifest; all N/U/P/G5 rows have source-bound independent evidence.

### D05-FU-02 — MAJOR — original LocalHost recovery suite failure still lacks deterministic diagnosis

- **位置 / reproduction:** read-only `dotnet/tests/Mahjong.LocalHost.Tests/LocalHostRecoveryTests.cs:72,147-158,794-813`. Preserve and refer to the original full-suite XML/first-failure command and hashes recorded in the Round 1/Round 3 sections; do not rerun the unchanged full suite to seek green.
- **Actual vs expected:** canonical suite remains 178/179 with `version=1`, `discard=1`; the controlled-copy diagnostic previously observed `version=4` and AI commits at versions 2-4, so it did not reproduce the old failure. A non-empty decision at `StateVersion >= minimumVersion` is only a source-supported hypothesis, not a demonstrated root cause.
- **Evidence:** original `localhost.xml` and `localhost-failures.xml` paths/hashes in the earlier sections; R3 controlled-copy JSON and reviewer root `build/87378b88821043b1817af86c14f7f863/diag`.
- **Repair/triage required:** keep production and out-of-list test files read-only. Supply a deterministic minimal run preserving release-before/after decision, seat, epoch, phase, commit, and durable boundary, or accurately label the predicate theory unverified and give caller the exact out-of-scope change needed.
- **Acceptance assertions:** explain the original ordering with runtime evidence; retain strict version advancement, durable events and physical conservation; after any separately authorized fix, canonical 179/179 with zero skip, retaining the original failure artifact.

### D05-FU-03 — MAJOR — semantic artifact contract repaired in fixtures, real producer not yet accepted

- **位置 / reproduction:** `C04PrivacyPlayerDriver.cs` released-control receipt serialization and `run-p45-c04-privacy-matrix.ps1` `Assert-C04PlayerCaseEvidence`; run `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/C04PrivacyRunner.Tests.ps1`.
- **Actual vs expected:** R3 defects A/B are corrected in producer, consumer and fixture schemas; default runner behavior tests pass 27/27. Actual Unity released-control producer still fails before writing reconnect evidence, so no real Windows producer artifact has passed the semantic consumer. Expected: true ClaimControl `Recorded/LOCAL_CONTROL` and stale `Rejected/DECISION_MISMATCH` represented separately and bound to the same original decision/session/scope with captured epoch equality and no side effect.
- **Evidence:** `build/runner-tests-3ae839c814724fd7a191659cbb68e684`; real PlayMode failure XML and summary under `build/aa99e58c0e6f4c2bab255f6d4d647920`.
- **Repair required:** keep the structured claim/stale schema and semantic checks; complete a real producer run and audit all seven case outputs against consumer assertions. Do not add empty files, lower artifact counts, or hand-edit producer fields.
- **Acceptance assertions / negative matrix:** fixture positive is explicitly fixture-only; reject wrong shared reason, wrong captured/stale epoch, non-advanced claim epoch, owner/snapshot mismatch, stale commit metadata, duplicate stale sends, pending commands, wrong case/decision/session/scope/round, missing/empty deadline evidence, and meaningless extra files. The real Windows producer artifact must pass the same consumer.

### D05-FU-04 — MAJOR — compact path contract passes cheap tests, Windows filesystem acceptance remains open

- **位置 / reproduction:** runner fixture path construction in `tools/tests/C04PrivacyRunner.Tests.ps1` and Player state root construction in `C04PrivacyPlayerDriver.cs`; run the default runner test command above.
- **Actual vs expected:** R3 default fixture MAX_PATH failures are fixed; default 27/27 and prior explicit short-root 27/27 pass, and over-budget roots are rejected before fixture writes. Current-source ordinary Windows driver/state/outbox execution has not run, so the original Windows `Mahjong.exe` path boundary is not yet accepted. Expected: all real case artifacts fit without machine long-path configuration and retain complete case identity.
- **Evidence:** `build/runner-tests-3ae839c814724fd7a191659cbb68e684`; prior R3 Windows package path failure and its hash are preserved in the Round 3 section; no current P1 package exists.
- **Repair required:** keep compact unique artifact path components and preflight length check; prove actual ordinary Windows output paths with all seven scenarios and P2. Do not shorten the seven semantic test names or change machine policy.
- **Acceptance assertions:** default and supported explicit GUID roots pass with native exit 0; an over-budget root fails before writing; final Windows recovery/deadline/closed-binding cases persist valid evidence without path exceptions.

### D05-FU-05 — MAJOR — same-identity control stream recovery assertion fails in real PlayMode

- **位置 / reproduction:** `C04PrivacyRoleSwitchPlayModeTests.RunReleasedControlScenarioAsync` formal reconnect around lines 972-1027 and the equivalent authorized Windows driver path. Reproduce with the exact `598ea2c` PlayMode command above; it fails `RealRoom_ReleasedSeatAiAdvancesAfterPlayerCloseWithSpectatorOnly` at the identity/new-Ready assertion.
- **Actual vs expected:** the claim is recorded, the cursor-floor branch reaches `Reconnecting`, the old pump is stopped, and same-session `OpenAsync` is attempted; the compound assertion then fails. Current preserved XML lacks the per-field message because it predates commit `6b4dc243`. The follow-up intended to expose those values timed out during Unity import before executing the test. Expected: formal recovery over original authenticated identity yields a fresh binding/Ready baseline without repeating claim or changing the stale request.
- **Evidence:** `build/aa99e58c0e6f4c2bab255f6d4d647920/playmode-results.xml` (SHA above), its `unity-editor.log` and summary; `build/7f42c7d712d54f5cb8b6954cd39238b4` sealed timeout. The R3 reviewer diagnostic call chain remains preserved in the Round 3 section and establishes `CONTROL_APPLIED` cursor regression -> close -> `LOCAL_CLOSED`.
- **Repair required:** use the newly added field-specific failure output on a completed controlled run to identify the exact failed identity/floor/readiness field; fix only allowed scenario glue using the existing serialized formal recovery path, same ticket/session identity, and one original stale action. No new claim, new decision, duplicate action, weakened cursor floor, Client/LocalHost edit, or unlimited wait.
- **Acceptance assertions:** capture release/claim receipt, old and new binding/session/scope, snapshot owner/epoch/cursor, Ready, transport, pending, and raw frames; show recovery branch truly executed; submit original stale decision/epoch exactly once and receive authority `DECISION_MISMATCH` with no commit metadata, duplicate side effect, or pending request; then pass the same case in final PlayMode and ordinary Windows package.

### Shared negative matrix for FU-03/FU-04/FU-05

Use the actual runner assertion with fixture-only positive controls and mutations for each missing/empty artifact, wrong case/decision/request/round/session/scope/binding, wrong ClaimControl receipt status/reason, wrong stale status/reason, captured epoch unequal to stale epoch, non-advancing claim epoch, stale snapshot owner/epoch mismatch, stale commit metadata, duplicate stale frame, nonzero pending command, no spectator deadline progress, meaningless third file, long output path, nonzero native process exit, cancellation/timeout residual writer, and package file mutation. Every negative must reject with native nonzero; every positive fixture must remain labeled fixture-only. Final GAME evidence must be producer-generated raw/data from the actual application and pass this exact consumer.

No final-source ordinary Windows package was built, so FU-01 remains a blocker. FU-02 and FU-05 remain unresolved major findings; FU-03 and FU-04 pass their cheap contract layers but remain open at real Player acceptance. The final matrix is not approved for handoff.

# Round 3 authoritative status (2026-11-03)

**Final status: NOT ACCEPTED.** FU-03’s producer/consumer artifact mismatch now has structured case-specific evidence and 26/26 fixture contract checks. A real Unity run at the current code head compiled and executed the exact seven PlayMode cases: six passed, one failed because the reopened player channel entered `Reconnecting`/`LOCAL_CLOSED` before the required stale request could reach authority. A Windows package from an earlier source head exposed a 260-character save-path boundary; the authorized driver now shortens those temporary identifiers, but the final-source Windows build could not start because the shared Unity mutex remained held by another worktree for the full 600-second bound. FU-02 remains unproven. No production or out-of-allowlist file was changed.

This section supersedes the Round 2 current-status paragraphs below. Round 1 and Round 2 sections remain unchanged as history; they are not evidence for the current source head.

## Round 3 source identity and changes

| Item | Value |
|---|---|
| Starting candidate | `603005a4cd87e889b3b13264df8c70660a54ae47` |
| Current code head (docs-only report commit follows separately) | `86cb2c4623aa10d0a316d60a750c3dcb30b4a41d` |
| Code commits in this continuation | `c1534016` semantic C04 evidence contract; `dcbb2f75` deadline wire types; `295230b4` spectator snapshot types; `f9e43b25` recovery/control evidence; `47e1fff3` pre-submit control capture; `a203596f` short Windows temp paths; `86cb2c46` reconnect diagnostics |
| Tracked changed paths from the starting candidate | `tools/tests/C04PrivacyPlayer.Tests.ps1`; `tools/tests/C04PrivacyRunner.Tests.ps1`; `tools/unity/run-p45-c04-privacy-matrix.ps1`; `unity/Mahjong.Unity/Assets/Scripts/App/C04PrivacyPlayerDriver.cs`; its `.meta`; `unity/Mahjong.Unity/Assets/Tests/PlayMode/C04PrivacyRoleSwitchPlayModeTests.cs`; this delivery report |
| Scope check | The six code paths above are within the original 11-path allowlist. The report is the only documentation path. No Client, Domain, Contracts, LocalHost authority, schema, asmdef, machine configuration, or out-of-list test was modified. |
| Final Unity input closure | 540 files / 14,193,231 bytes / SHA-256 `ec943188a4adbbf4a55eee753b2dd315a763fa537aeae425ac2c5477d5074ddb`; final source-stage manifest in `build/e12f7d86bd4f4a47884676485145fb87/source-closure.sha256`. |
| Final staged Unity inputs | 366 files / 33,505,854 bytes / SHA-256 `08a5afba59d9f8e5d84c6dd9680669e99826d212731a9279297ff6d1177cefc3`; `build/e12f7d86bd4f4a47884676485145fb87/staged-input.sha256`. |
| Toolchain | Unity `6000.3.27f1` (`6000.3.27.6761814`), Unity dotnet `8.0.3226.50405`, SDK `8.0.426`. |
| Code diff | `git diff --check 603005a4..86cb2c46` exit 0; the current tested code checkout was clean. The final ordinary Windows runner was later invoked at this exact expected source head. |

The final Editor run necessarily compiled the final staged Unity source before executing PlayMode. Earlier standalone `CompileOnly` passes at `f9e43b25` and `47e1fff3` are retained, but they do not substitute for the current-head PlayMode compilation or a final Windows package build.

## Round 3 stage results and evidence

### A — identity, environment, and lock

The exact final-source Windows command was:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-c04-privacy-matrix.ps1 -WindowsAppMatrix -ExpectedSourceHead 86cb2c4623aa10d0a316d60a750c3dcb30b4a41d -RunRoot build/46a30dd529924372b71f24d36825ab76 -TimeoutSeconds 1200
```

It passed source identity and clean-tree checks, built and self-tested the accepted verifier (`17 positive / 67 negative`), then waited on `Local\MahjongV5P45UnityValidation` for 600 seconds and exited 1 before shared-plugin build or Unity launch. The sealed summary is `build/46a30dd529924372b71f24d36825ab76/summary.json` (SHA-256 `F11283EA78536E7DCCB23961E9D3F0DF16E8BB78AC6D8724746AA3B2C8D674BB`); its seal manifest is `manifest.sha256` (SHA-256 `6C88468FAABB91CAEB80C0681D4B6D61F045EB7FCCE807E6062A14E19637DD88`). No Unity or player writer was started by this final attempt.

Read-only process inspection identified PID `12824`, start time `2026-11-02 18:18:32`, running a PlayMode test in `E:\majiang-review\g2-c04-d05-luna6-20261102-c04d05a1\build\cf79eb99ea3d493b8411cf67457e86ae\staging-project`. It is not owned by this task and was not stopped or modified. No lock bypass or second unchanged expensive attempt was made. The exact recovery condition is that this Unity owner exits normally and releases the shared mutex; then rerun the final command above from a clean checkout of code head `86cb2c4623aa10d0a316d60a750c3dcb30b4a41d`. The report commit changes only docs and must not be passed as `ExpectedSourceHead`.

### B/C — verifier and adapter contracts

- The accepted verifier remained unchanged. On the final Windows-runner preflight it built with native exit 0, and `self-test` exited 0 with `positive=17 negative=67 serializedFrames=5 source=WireCodec`.
- `tools/tests/C04PrivacyPlayer.Tests.ps1` at code head `86cb2c46` exited 0 with `24/24` source-contract checks. Evidence summary: `build/b57a3bf1e5fd4b8eb20a36b74682a961/summary.json`. It checks fixed seven-case dispatch, opt-in/no-allocation behavior, real app/session paths, deadline facts, control-session diagnostics, compact path components, package/restart wiring, and unique metadata GUID. These are source checks, not Player execution.
- `tools/tests/C04PrivacyRunner.Tests.ps1` at the post-FU-03 runner revision exited 0 with `26/26` contract checks (`build/79c388706c4247a7a091adb7fb629066`). The added behavior fixtures invoke the actual semantic evidence assertion function; all are explicitly fixture-only and are not GAME evidence.
- The unified negative contract matrix rejects: missing/duplicate/unknown opt-in arguments before driver/evidence allocation; wrong source or dirty checkout; malformed/zero/renamed seven-case XML; missing required case artifacts; empty, wrong-case, wrong-decision, wrong-epoch, wrong deadline, no progress, or meaningless extra files; invalid raw owner/audience/binding/scope/version/protobuf/hash through the accepted verifier; package mutation; P2 request/scope/digest substitution or duplicate command; native nonzero, timeout, cancellation, residual writer, and unstable seal. No verifier relaxation, skip, or empty denominator was introduced.

### D — exact final-source PlayMode result

Exact command:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-c04-privacy-matrix.ps1 -ExpectedSourceHead 86cb2c4623aa10d0a316d60a750c3dcb30b4a41d -RunRoot build/e12f7d86bd4f4a47884676485145fb87 -TimeoutSeconds 1200
```

Unity process `unity-playmode` PID `32356` exited native `2`, not timed out and not cancelled. XML has exactly 7 cases, 6 passed, 1 failed, 0 skipped. XML SHA-256 is `3C14BE9F565DAAFB40A10648EF1FD68C6F496362AD254C42A9B92CC43FDA4FFB`; stable seal manifest SHA-256 is `BB730EDFA1308F47872EF0CD05EF0C042E9D2C6B78B3A6E61DB5D14175252E8A`.

| Exact case | Result |
|---|---|
| `RealRoom_FourPlayersSpectatorAndRoleSwitchUseIndependentSessions` | PASS |
| `RealRoom_DelayedOldRawReceiveIsRejectedByNewBinding` | PASS |
| `RealRoom_ColdReopenReconcilesOriginalRequestWithoutDuplicate` | PASS |
| `RealRoom_LegalSleeveAndFeeOwnershipAcrossFourSeatsAndSpectator` | PASS |
| `RealRoom_ReleasedSeatAiAdvancesAfterPlayerCloseWithSpectatorOnly` | FAIL |
| `RealRoom_OpenDecisionDeadlineContinuesAfterPlayerClose` | PASS |
| `RealRoom_ClosedPlayerToSpectatorToNewPlayerReceivePathRejectsOldRaw` | PASS |

The failure is not a missing evidence artifact or a timeout. At the stale-command boundary the real `TableSession` was `Reconnecting`; its binding/scope and snapshot remained present, the control epoch snapshot was 1, the claim sent one command frame, but the underlying channel was closed. `LastError=LOCAL_CLOSED`, `transportOpen=false`, and the pump loop recorded no exception. The stale command was therefore never sent to authority and no authoritative `DECISION_MISMATCH` receipt was obtained. Original diagnostic JSON SHA-256 `136688BBE9573772B252147AD1BEE6853817C397C53753F2226C3A7FCD1B976B`; error text SHA-256 `EC55B11BE0D13FD93DBAC48DFDCA18DE3B9DC8837FF8357B4AA7C374C0C10115` under `build/e12f7d86bd4f4a47884676485145fb87/privacy-evidence/released-control/`.

### Earlier single ordinary Windows package — supplemental, not final-head acceptance

At code head `47e1fff355468fab5c47c5ce2eef412894ba559b`, one ordinary Windows package was built once at `build/2d9677e3d5db427d9fcdb7775959a40b/player/Mahjong.exe` (exe SHA-256 `f083b0e879a9298cdcf9933a8f3b4f2b013e13f7d3e51221fc0d756b79e28807`). This is a prior source-head package and cannot certify head `86cb2c46`.

The canonical runner process executed argument-rejection controls (three native nonzero exits), then the first two real cases passed; recovery failed native 72 in `LocalTableStore.CreateRoomAsync` because the temporary save path did not exist. A failure-continuation diagnostic then ran the remaining four cases against the **same unchanged package**: four-owner privacy passed, while release-control, deadline, and closed-binding each failed native 72. Real raw captures from role-switch and four-owner passed the real verifier. A direct same-package P2 prepare/recover was also run as a separately labeled continuation: two distinct `Mahjong.exe` PIDs/start times, same original request `c04-player-c04-d05-final-unified-p2-discard`, same scope `42b2c75f6be742edb2549bc6a3b704f8`, same canonical digest `0769e5d8848626ca2af725d2813acced3aa6212ea3a2cee43cd45720c078efa6`, old-scope lookup count 1, original command resend count 0, zero delta on repeat query, `physical136Unique=true`. It is useful real evidence for that earlier package only; it is not a canonical successful final-head `WindowsAppMatrix` run.

The package was 205 build-manifest files (the marker excludes the package-manifest file); the runner’s actual before/after walk counted 206 files including that manifest. Both walks were byte-for-byte equal at SHA-256 `f8772d4212e84bfa4e4d98a24959eb1df8703e75213bc0d9c33b1ed6d9b15b5f`. This comparison occurred after the diagnostic continuation; the runner’s own final `windows-app-package-after.sha256` was not produced because its first failed case stopped the canonical matrix. Eight shared DLL hashes are in `build/2d9677e3d5db427d9fcdb7775959a40b/player-build.json`; they are prior-head evidence, not a final-head closure comparison.

## Round 3 issue ledger — complete known items

### D05-FU-01 — BLOCKER — final-head ordinary Windows package and unified app matrix not established

- **Location:** final `-WindowsAppMatrix` stage in `tools/unity/run-p45-c04-privacy-matrix.ps1`; final attempt root `build/46a30dd529924372b71f24d36825ab76`.
- **Reproduction:** the exact command in Stage A above, with expected head `86cb2c46...`.
- **Actual vs expected:** source identity and accepted verifier preflight passed, then shared mutex acquisition timed out at 600 seconds before shared DLL build, Unity build, `Mahjong.exe`, or final-head P1/P2. Expected one fresh immutable ordinary package, all seven app cases, raw verification, P2 restart, and exact before/after manifest comparison.
- **Evidence:** summary and manifest hashes recorded above; external lock owner PID/start/worktree recorded in Stage A. Older package evidence at head `47e1fff3` is supplemental and explicitly not promoted.
- **Required fix/next action:** no code change is implied by this environment timeout. After the identified owner exits and releases the mutex, rerun at a clean checkout of code head `86cb2c46`; do not kill or bypass the other writer. Keep all runner failures as failures and complete both app cases and P2 on one package.
- **Acceptance:** native build succeeds; one package and all 7 cases execute; real raw verifier accepts legitimate captures and rejects all malformed classes; P2 uses the original scope/request/digest and proves zero duplicate effects; before/after every-file count/length/SHA and the 8 shared DLL set match.

### D05-FU-02 — MAJOR — original LocalHost recovery follow-up failure remains undiagnosed

- **Location:** out-of-allowlist read-only source `dotnet/tests/Mahjong.LocalHost.Tests/LocalHostRecoveryTests.cs:72,147–158,794–813`.
- **Reproduction:** the preserved original full-suite command was `D:/Unity/dotnet/dotnet.exe exec dotnet/tests/Mahjong.LocalHost.Tests/bin/Release/net8.0/Mahjong.LocalHost.Tests.dll -noLogo -automated -parallelMode none -failSkips -result-xml <exclusive-root>/localhost.xml`.
- **Actual vs expected:** preserved canonical result remains `179 total / 178 pass / 1 fail / 0 skip`; `ColdRestartBoundaryIsStableBeforeAndAfterAutomaticFollowUp` observed version 1 / discard 1 and returned before follow-up. An earlier isolated rerun was 1/1 but did not explain or replace the full-suite failure.
- **New diagnostic:** a controlled ignored copy/harness at `build/294a1597af054779a41213056b11e2eb/` ran the production LocalHost/Client/TableSession channel path 1/1 with native exit 0. It showed release immediately at version 1/eventSeq 2 with no decision, AI follow-ups at versions 2–4, and a new human decision at version 4. It did **not** reproduce the original 1/1 observation order; therefore “stale decision” remains only a source-supported hypothesis, not a diagnosed cause. JSON SHA-256 `A4C588A9E4E80B2D88656B526B47FF27C5E50B9F970F288704CCF8FCC62D5240`.
- **Evidence:** first full-suite XML and SHA from the prior report remain preserved at `build/5c4608d017774b4798a9a54ea174850a/localhost.xml` (SHA `4923FAF6980E86672C259505112C606733D2972891D9DDE3159F59F552C85EFD`); diagnostic summary/samples are in the R3 root above.
- **Required fix/next action:** no out-of-list test or production edit is authorized. Caller/reviewer must identify the deterministic scheduling/decision boundary from the original failure before proposing a minimal patch. Preserve seat/decision/epoch/phase, durable state/events and actual follow-up commit in the reproduction; do not merely change `>=` to `>` or use sleeps.
- **Acceptance:** deterministic repro explains the original failed order and proposed cause; after separately authorized repair, canonical LocalHost suite is 179/179, zero skip. No unchanged full-suite rerun was used here.

### D05-FU-03 — MAJOR — deadline and all-case evidence contract (implementation fixed; real Windows acceptance pending FU-01)

- **Original defect/reproduction:** `build/49f188d64fc4444a8fb053f5049ddeaf/repro-deadline-contract.ps1`; previously a successful deadline producer had only generic summary/log while the runner demanded at least three files, so it was rejected as incomplete.
- **Actual vs expected before fix:** deadline scenario had no case-specific evidence; arbitrary file count was not semantic proof. Expected actual decision/deadline/binding/close/spectator/cursor/progress facts, with case identity and schema validation.
- **Repair mapping:** `c1534016` replaced the arbitrary count with `Assert-C04PlayerCaseEvidence` contracts for all seven cases and added missing-artifact/semantics fixture checks; `dcbb2f75` and `295230b4` corrected actual deadline clock and spectator snapshot types. Deadline driver now writes `deadline-evidence.json` from live decision, clock, session/binding, close state, spectator baseline and later version/cursor/phase.
- **Validation:** real runner contract tests `26/26`; deadline fixture positive plus wrong/missing identity, decision, epoch, deadline, no-progress, and meaningless-third-file negatives all pass as expected. Fixtures are explicitly not GAME. The final-current-head PlayMode deadline case passed `1/1`; however the current-head ordinary Windows case never ran due FU-01. The earlier Windows attempt at head `47e1` stopped on the path defect before its deadline evidence could be produced.
- **Evidence/fix state:** implementation and fixture-consumer defect are repaired. Do not mark the end-to-end requirement complete until a final-head ordinary Windows deadline case emits and passes the semantic contract from real production traffic.
- **Acceptance:** seven schemas are all validated semantically; no dummy file/count threshold; one immutable final-head package contains a successful deadline artifact with original decision/seat/epoch/deadline, real binding close, spectator baseline and actual cursor/version progress. Missing or contradictory fields are rejected.

### D05-FU-04 — MAJOR — ordinary Player local-save paths reached Windows `MAX_PATH` (shortened; final-head runtime check pending)

- **Location:** authorized driver `unity/Mahjong.Unity/Assets/Scripts/App/C04PrivacyPlayerDriver.cs`, `StateRoot` and per-scenario room IDs; the LocalHost writer itself was not changed.
- **Reproduction:** on the prior ordinary Player package, run `RealRoom_ColdReopenReconcilesOriginalRequestWithoutDuplicate`, `RealRoom_OpenDecisionDeadlineContinuesAfterPlayerClose`, or `RealRoom_ClosedPlayerToSpectatorToNewPlayerReceivePathRejectsOldRaw` through `-WindowsAppMatrix`. Player log at `build/2d9677e3d5db427d9fcdb7775959a40b/windows-app-cases/RealRoom_ColdReopenReconcilesOriginalRequestWithoutDuplicate.unity-player.log` reports `Local temporary write failed` on a `.save.tmp.<guid>` path whose exact length is 260; related cases fail the same way. The shorter four-owner path passed in that same package.
- **Actual vs expected:** app-created `local` directory was present when set up but the nested atomic temp-save path hit the legacy Windows path ceiling. Expected every authorized temporary state/save path to remain below the platform limit without changing production persistence.
- **Repair mapping:** `a203596f` shortens temporary state-folder and room-id random components while retaining GUID uniqueness; no production source or `UnityAppPaths` was modified. Player source-contract check 24/24 verifies compact path components.
- **Validation:** final-source Editor run compiles and passes 6/7, but Editor temp root is shorter and does not prove ordinary Windows paths. The final Windows attempt at head `86cb2c46` timed out waiting for the shared mutex before building, so the fix is implemented but not runtime accepted.
- **Acceptance:** after FU-01 lock release, the final-head ordinary Windows package completes recovery, deadline, and closed-binding cases with durable files and no path errors; semantic outputs pass and package hashes stay unchanged.

### D05-FU-05 — MAJOR (new) — released/claimed player connection closes before authoritative stale rejection

- **Why new:** the previous review did not have a successful Unity compilation and a real final-source seven-case run. This first actual run after compiling the new app driver exposed the connection transition; source-string and fixture checks could not reveal it.
- **Location:** allowed reproduction in `unity/Mahjong.Unity/Assets/Tests/PlayMode/C04PrivacyRoleSwitchPlayModeTests.cs:927–1027` and driver `unity/Mahjong.Unity/Assets/Scripts/App/C04PrivacyPlayerDriver.cs:960–1080`; command admission gate is read-only `dotnet/src/Mahjong.Client/TableSession.cs:355–357`. Likely close/lifecycle investigation requires out-of-allowlist `dotnet/src/Mahjong.LocalHost/LocalTableRuntime.cs`, `dotnet/src/Mahjong.LocalHost/LocalTableRoom.cs`, `dotnet/src/Mahjong.Client/TableSession.cs`, and the Unity `LocalTableChannelFactory`.
- **Reproduction:** the final PlayMode command in Stage D; exact case `RealRoom_ReleasedSeatAiAdvancesAfterPlayerCloseWithSpectatorOnly` fails in XML. The scenario releases seat 0, closes that player, observes spectator-only AI progress, opens a real new player session and issues ClaimControl, then captures the pre-stale command state.
- **Actual vs expected:** current snapshot shows control epoch 1 and the claim sent one real command frame, but the session is `Reconnecting`, binding remains present, `LastError=LOCAL_CLOSED`, transport `IsOpen=false`, pump exception `<none>`. The client gate rejects before sending the stale command. Expected a still-ready bound session that sends the original stale decision and receives exact authoritative `DECISION_MISMATCH`, with no commit metadata/effect.
- **Evidence:** `build/e12f7d86bd4f4a47884676485145fb87/privacy-evidence/released-control/control-session-before-stale-submit.json` and `control-session-submit-error.txt` with hashes in Stage D; same failure reproduced on final-source runs at `a203596f` and `86cb2c46`. The new `LastError`/transport/pump capture is diagnostic and does not alter production behavior.
- **Required fix/next action:** no production/client/LocalHost edit is authorized. Preserve this exact reproduction and ask the caller/reviewer to investigate the real transport/room close reason and minimum necessary production path. Do not reopen a session, suppress the failure, skip the authoritative stale query, or claim control rejection from a client-side submission exception.
- **Acceptance:** a real Player and PlayMode run keeps the claimed session Ready long enough to send exactly one old-decision request through the real wire path, receives the required rejection reason, proves no metadata/pending command/side effect, and independently records current control owner/epoch/Ready.

## Round 3 fixed matrix status

Statuses below describe the final unified source-head requirement. Evidence from the earlier head-`47e1` Windows package is identified as supplemental only.

| Row | Status | Current-source evidence / limit |
|---|---|---|
| N1 | BLOCKED | Four-seat/player/spectator role isolation passes in current PlayMode; final ordinary package not run. |
| N2 | BLOCKED | Spectator/public role behavior passes in current PlayMode; final ordinary package not run. |
| N3 | BLOCKED | Non-empty sleeve/fee owner evidence passes the current PlayMode case; final Windows raw-owner verification not run. |
| N4 | BLOCKED | Current PlayMode real delayed-old-raw receive case passes; final ordinary package evidence unavailable. |
| N5 | BLOCKED | No final-source app slow-consumer saturation/cursor/close-isolation execution. |
| N6 | FAIL | Current-source release/claim path becomes `LOCAL_CLOSED` before stale authoritative receipt; open-deadline case itself passes. |
| N7 | BLOCKED | Current PlayMode cold reopen passes; required final-head ordinary Windows original-outbox process restart was not run. Prior-head P2 continuation is supplemental. |
| N8 | FAIL | Required release/claim/seat lifecycle proof is interrupted by the same client transport close; current-source case fails. |
| U1 | FAIL | Exact seven current-source PlayMode names: 6/7 passed, 0 skipped, native Unity exit 2. |
| U2 | BLOCKED | Current PlayMode role-switch, legal sleeve/fee, delayed raw, and closed-binding cases pass; ordinary Windows same-package confirmation missing. |
| U3 | FAIL | Recovery and deadline cases pass in PlayMode; release/claim control case fails before authoritative stale rejection. |
| P1 | BLOCKED | No final-head ordinary Windows `Mahjong.exe` matrix; prior-head package is not promoted. |
| P2 | BLOCKED | Real two-process same-package P2 passed only on prior head `47e1fff3`; final-head P2 not run. |
| C04-G5-01 | BLOCKED | Current PlayMode four-owner positive passes; final ordinary Windows owner/raw comparison missing. |
| C04-G5-02 | BLOCKED | Current PlayMode non-empty private fee ownership passes; final ordinary Windows independent raw comparison missing. |
| C04-G5-03 | BLOCKED | Current PlayMode old-raw receive path passes; final ordinary Windows path not run. |

## Round 3 handoff

All currently known findings are FU-01 through FU-05 above; no confirmed additional business defect was added. FU-03 consumer/producer contract work and FU-04 short-path implementation have explicit code/test mappings, but their real final-head Windows acceptance remains open. FU-02’s root cause remains a hypothesis after a non-reproducing controlled diagnostic. FU-05 requires caller/reviewer judgment because the likely production lifecycle files are outside the authorized list. The receiver does not approve, push, create/amend a PR, merge, deploy, change task/scheduler state, or alter the other Unity process. C04/G2/G5 is not released.

The report’s docs-only commit changes the checkout HEAD after the final tested code head. To resume validation, use a clean checkout/worktree at `86cb2c4623aa10d0a316d60a750c3dcb30b4a41d`, then run the exact final-source command in Stage A with a fresh ignored GUID root after the external PID releases the mutex. Do not use the docs commit hash as the expected source hash.

# Round 2 authoritative status (historical; superseded by Round 3 above)

**Final status: NOT ACCEPTED; the same-package application matrix remains BLOCKED.** The authorized opt-in driver, runner path, and negative/source-contract adaptations are now implemented in the approved file list. Latest source-contract checks pass, but there is no successful Unity compile, no ordinary Windows application package, and no final PlayMode or same-package Windows run. The final Unity compile attempt exited 1 while resolving `com.unity.burst` with `TAR_ENTRY_ERROR: ENOSPC`; after that attempt E: had 789,540,864 bytes free. Stage D was not started. FU-02 is diagnosed as a LocalHost test wait/predicate defect outside the allowlist and is handed to the caller with the exact path and minimum repair proposal below.

## Final code identity and authorized changes

| Item | Value |
|---|---|
| Starting code head | `603005a4cd87e889b3b13264df8c70660a54ae47` |
| Final code head (before this docs-only report commit) | `92d29d4038f3d87b5b9fec3b626eca7254f623c8` |
| Code commits | `9bd951a69ba42ca58f17b16bf06c147b8faa0584`; `cc20037fcc424e13051ec20c3bda0a18ff2f49de`; `c29af3f073844586a9e6732b166f8c4d954e3084`; `92d29d4038f3d87b5b9fec3b626eca7254f623c8` |
| Tracked code files | `tools/tests/C04PrivacyPlayer.Tests.ps1`; `tools/tests/C04PrivacyRunner.Tests.ps1`; `tools/unity/run-p45-c04-privacy-matrix.ps1`; `unity/Mahjong.Unity/Assets/Scripts/App/C04PrivacyPlayerDriver.cs`; `unity/Mahjong.Unity/Assets/Scripts/App/C04PrivacyPlayerDriver.cs.meta` |
| Final source closure | 540 files, 14,184,662 bytes, SHA-256 `d6b0ac1634d145f4a79bdcd77a79d03d15e023d352157d8ae998423069267f19`; preflight root `build/7c99844ca7b948c5a0a6102681d8e885/` |
| Final Unity input copy | 366 files, 33,486,337 bytes; source and staging manifests both SHA-256 `4acc9558a95d4aa1cdfd6fedf8666468f3886f3198bc7c8de79bcd57b48e3a38`; copy-only root `build/df585bfd1e69490fb49da2bb038cc637/`; this is a byte-verified input copy, not a build |
| Editor / dotnet | Unity `6000.3.27f1` build `6000.3.27.6761814`; Unity dotnet `8.0.3226.50405`, SDK `8.0.426` |
| Tracked state | Code diff is limited to the five allowlisted paths above. `git diff --check 603005a4..92d29d40` exits 0. The report is committed separately as docs-only. |

The opt-in bootstrap reads command-line args before allocating the driver GameObject; with no explicit enable argument it returns without creating a host, evidence directory, or thread. It dispatches the seven exact PlayMode scenario names through the existing app/room/channel/TableSession scenario bodies. The runner now has `-CompileOnly`, `-WindowsAppMatrix`, fresh staged input copying, argument-negative controls, one ordinary `player/Mahjong.exe` build path, exact seven-name dispatch, raw-verifier invocation, package before/after hash checks, and the two-process P2 route using the original recovery request. These implementations are source-reviewed and source-contract-tested only; they are not runtime evidence.

## Round 2 stage results

### A — identity and environment

The final short preflight command was:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-c04-privacy-matrix.ps1 -RunRoot build/7c99844ca7b948c5a0a6102681d8e885 -PreflightOnly -ExpectedSourceHead 92d29d4038f3d87b5b9fec3b626eca7254f623c8 -TimeoutSeconds 120 -MutexTimeoutSeconds 600
```

Native exit 0; source clean, `git diff --check` 0, no Unity process, no test XML. Unity was actually launched in earlier CompileOnly attempts and connected to the existing licensing client, which resolved an assigned Unity Personal entitlement; no license or machine settings were changed. The shared mutex `Local\MahjongV5P45UnityValidation` was acquired and released in the successful component/tool/compile attempts. All owned child processes in their runner summaries ended; no external Unity process was stopped.

A final byte-copy-only verification reused the actual runner functions with `-LoadFunctionsOnly` and `Copy-C04UnityInputs`. It copied `Assets`, `Packages`, and `ProjectSettings` to a fresh ignored GUID root and compared canonical source and destination manifests: both `366 / 33,486,337 / 4acc9558...`. It started no Unity process. This binds the final whitespace-clean driver input but does not compile it.

### B — components and minimum glue

Round 1 retained component evidence remains the relevant .NET closure because this round changed only Unity runner/driver glue: LocalHost `179 total / 178 pass / 1 fail / 0 skip`; isolated `ColdRestartBoundaryIsStableBeforeAndAfterAutomaticFollowUp` `1/1 pass`; TableRuntime `483/483`; Client `182/182`; TableModel `236/236`. No unchanged LocalHost full-suite rerun was used to overwrite the first failure. The fresh verifier build/self-test in `build/33e03293cfa24ff3bc7e0e2d91d64cb5/` passed before Unity package resolution failed; the accepted round-1 verifier negatives remain `17 positive / 67 negative`, with 67/67 actual CLI rejections as recorded earlier in this report.

Latest glue checks, after commit `92d29d40`, were:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/C04PrivacyPlayer.Tests.ps1 -RunRoot build/5507d82c4dfd4c6f824dc198c934d841
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/C04PrivacyRunner.Tests.ps1 -TestRunRoot build/b10786ba7a39435e8504c49cff76e791
```

Both native exits were 0. Player source-contract: `17 passed / 0 failed` (not Player execution). Runner contract: `20/20` (not application execution). Runner cases include rejection of wrong source/dirty state, input mutation, incorrect/empty XML denominator, missing package scenes/files, native nonzero, timeout/cancel/child cleanup failures, lock errors, and seal failures. Existing verifier self-test remains `17 positive / 67 negative`; it was not loosened.

### Compile-only attempts and preserved first failures

The three Unity-source compile attempts and the earlier stopped attempts are all retained; they are not represented as green:

| Evidence root / code head | Native result | Failure and disposition |
|---|---:|---|
| `build/7db73b61c2ca46ab8ee0eb5807409b82/`, `9bd951a6...` | Unity exit 1 after 128.096 s | Compiler rejected malformed JSON escaping in the new driver at lines 1639–1643 (`CS1056`, `CS1003`, `CS1010`, etc.). Fixed by commit `cc20037f...`, changing the escape helper. First compiler log retained; seal manifest SHA-256 `16d8e65fea490817a9d3d56f0267dca69ff2a0b1ab8d385b11de38959e282a96`. |
| `build/2076d3e9ffb94f268da897439ab79fc5/`, `cc20037f...` | Unity exit 1 after 54.613 s | Compiler found missing `C04ControlContract` at driver line 555 (`CS0103`). Fixed by commit `c29af3f0...`, bringing the exact accepted assertion helper into the driver. First compiler log retained; seal manifest SHA-256 `ab0dd056ec40c3112414b75134459d676ad1f70547aff1f2a8d0083935eac05f`. |
| `build/31bce07fd08f4f5c98477010fc75f388/`, `c29af3f...` | Shared-plugin build exit 1 after 5.252 s; Unity not started | NuGet reported insufficient disk space. Seal manifest SHA-256 `d0d8218eb5bfa2d0bbe95300b5618fe184bf1dd9c0760cfb78f3df861601f59f`. |
| `build/33e03293cfa24ff3bc7e0e2d91d64cb5/`, `c29af3f...` | Shared-plugin build exit 0; Unity exit 1 after 59.696 s | Unity resolved the license, then package resolution failed unpacking `com.unity.burst`: `TAR_ENTRY_ERROR: ENOSPC: no space left on device, write`. No C# compile result. Seal manifest SHA-256 `af7d42822c9376971353b3a379a00ba133e731d245e16c57436a3a377bd6fa28`. The Unity input in this attempt predates the later whitespace-only cleanup. |
| `build/4edc2db38ffc407ba02e28ff74236ba5/` | Runner exit 1 at preflight | My hand-entered expected source hash had an extra character. No Unity/plugin/test process started; seal `1313c9a9fd5d42dcbc0e2aa9af0e2c42455a0e5ff7a1f9d98421da5c6f21cc84`. |
| `build/2dad8ad6edbc4c03aa3fe4ab563b9099/` | Runner exit 1 at preflight | I supplied the cache run root instead of the exact `staging-project/Library` path. No Unity process started; seal `38692b06cfd33b819fcf9abe8e4bb5bc323191da39c02c868c529e6a3a0e67d3`. The corrected full-path invocation is recorded in the stage card for `build/33e03293...`. |

Commit `92d29d40` removed seven trailing-whitespace-only blank lines found by comparing the complete code diff against the starting candidate. This was discovered after the compiler attempts because the runner's per-run `git diff --check` checks the then-clean working tree, not whitespace already committed in earlier iteration commits. Latest glue contract tests and final `git diff --check 603005a4..92d29d40` pass. Since the post-cleanup Unity attempt could not be started without capacity, final C# compilation remains unverified. After the last Unity attempt E: had only `789,540,864` bytes free; do not rerun an unchanged Unity import or package build in that state.

### C–E — alignment, application matrix, seal

The real runner source-contract suite passed `20/20`; the actual accepted verifier remains unchanged. The final copied Unity input manifest is available, but there is no frozen compiled closure: no final Unity App DLL, no final 8-DLL comparison, no player build marker, no ordinary Windows package, and no app raw frames. We therefore did not enter Stage D or claim Stage E application evidence. All runner/build writers ended and their own run roots were sealed; the copy-only input root has its explicit `copy-summary.json` and matching source/staged manifests. A consolidated file-level evidence manifest is listed below.

The exact seven PlayMode names remain the original seven listed in the historical section: each is **NOT_RUN (0/1)**. No case was removed, renamed, filtered to zero, or passed by fixture. No standalone Windows Test Player XML is used as the ordinary `Mahjong.exe` package.

## Final matrix status

Every fixed row remains `BLOCKED` because the required final application proof is absent:

| Row | Status | Missing evidence |
|---|---|---|
| N1 | BLOCKED | No same-build real owner-private baseline and current legal action comparison. |
| N2 | BLOCKED | No spectator public-whitelist application evidence. |
| N3 | BLOCKED | No non-empty sleeve/private fee owner-audience-version comparison. |
| N4 | BLOCKED | No final real old-raw receive fencing through current binding. |
| N5 | BLOCKED | No bounded slow-consumer saturation, cursor, cancellation, and close isolation run. |
| N6 | BLOCKED | No final control epoch/owner/Ready/deadline application matrix. |
| N7 | BLOCKED | No same-original-request Player outbox reconciliation after process restart. |
| N8 | BLOCKED | No final lease and room isolation/handoff proof. |
| U1 | BLOCKED | Seven exact PlayMode tests not run against final compiled input. |
| U2 | BLOCKED | Role switch, cache clearing, actual delayed raw, sleeve/fee application evidence absent. |
| U3 | BLOCKED | Unity control/recovery/deadline matrix not run. |
| P1 | BLOCKED | No ordinary `Mahjong.exe` package matrix. |
| P2 | BLOCKED | No same-package process restart and original outbox receipt query. |
| C04-G5-01 | BLOCKED | No four-owner real sleeve privacy positive comparison. |
| C04-G5-02 | BLOCKED | No real non-empty private fee/ledger comparison from a legal production command. |
| C04-G5-03 | BLOCKED | No Unity/Windows delayed old raw receive rejection. |

The seven exact test names and each NOT_RUN count are listed in the retained Stage D section. The `17/17`, `20/20`, verifier, and .NET component counts are helper/tool/component results only and do not promote any matrix row.

## Consolidated issue ledger — all known items

### D05-FU-01 — BLOCKER — authorized driver implemented, final unified app proof still blocked

- **Location:** `unity/Mahjong.Unity/Assets/Scripts/App/C04PrivacyPlayerDriver.cs` and `.meta`; `tools/unity/run-p45-c04-privacy-matrix.ps1`; `tools/tests/C04PrivacyPlayer.Tests.ps1`; final result gates in this report. The earlier absence defect is repaired in code; the remaining blocker is lack of a successful compile and same-package execution.
- **Reproduction command:** run the final source preflight above, then the authorized command below with adequate E: capacity and a fresh GUID root. The final attempt with the exact frozen source and input copy is preserved in `build/33e03293...`; it fails before C# compilation because Unity cannot unpack `com.unity.burst` (`ENOSPC`).
  ```powershell
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-c04-privacy-matrix.ps1 -RunRoot build/<fresh-32-hex-guid> -WindowsAppMatrix -ExpectedSourceHead 92d29d4038f3d87b5b9fec3b626eca7254f623c8 -TimeoutSeconds 1200 -MutexTimeoutSeconds 600
  ```
- **Actual vs expected:** actual: 7/7 PlayMode NOT_RUN; zero ordinary Windows package; zero P1/P2 raw/process results; no final 8-DLL or package before/after digest. Expected: seven exact PlayMode tests on the frozen source; one ordinary `Mahjong.exe` built once, all Windows scenes plus P2 on that exact immutable package; original request/scope/canonical/outbox receipt query after process restart with zero repeated side effect; full raw verifier and per-file SHA evidence.
- **Evidence:** source/tests `build/5507d82c4dfd4c6f824dc198c934d841/summary.json`, `build/b10786ba7a39435e8504c49cff76e791/runner-tests.json`; Unity input copy `build/df585bfd1e69490fb49da2bb038cc637/copy-summary.json`; compile log and sealed manifest under `build/33e03293cfa24ff3bc7e0e2d91d64cb5/`; latest free space 789,540,864 bytes. No final runtime artifact exists.
- **Repair/next action:** the approved glue has been implemented. The next executable repair step is external capacity restoration, then compile the exact `92d29d40` closure and complete all B/C gates before running D. Any new compiler or product failure must be added only if newly discovered and mapped to an allowlisted fix or a precise out-of-scope handoff. Do not promote source-contract checks as runtime proof.
- **Unified negative matrix:** (1) normal no-opt-in launch allocates no driver host/path/thread and otherwise remains ordinary; (2) missing, repeated, unknown-case, and malformed required args fail before driver resources; (3) missing/duplicate one of seven case names, XML `Total=0`, incorrect filter/name, native nonzero, timeout or cancellation must fail and seal; (4) any package file or one of eight shared DLL hashes changing before/after must fail; (5) wrong owner/audience/binding/scope/version, unknown wire fields, malformed protobuf, or bad frame hash must fail the real verifier; (6) P2 request/scope/canonical identity changes, replay/resubmit, duplicate side effect, changed original receipt, or same-PID “restart” must fail. Source/runner tests cover only their stated tool contracts; runtime controls remain unrun.
- **Acceptance assertions:** PlayMode exact seven names `7/7`, zero skip; fresh ordinary Windows package and all scenes + P2 from that one package; per-file package and eight shared DLL identity unchanged; real raw verifier records from actual receive path; private nonempty legal sleeve/fee owner+audience+version comparisons and public whitelist; current binding rejects injected old raw; Ready/cursor/epoch/deadline floors and 136 entities; P2 distinct PID/start, same original request/scope/canonical payload, actual original outbox receipt query/reconcile, exactly one side effect and original receipt; all N/U/P/G5 rows independently auditable.

### D05-FU-01A — MAJOR — two sequential adapter compile blockers found on first Unity builds (source fixes applied; final compiler run blocked)

- **Location:** `unity/Mahjong.Unity/Assets/Scripts/App/C04PrivacyPlayerDriver.cs:555,1639–1643`.
- **Reproduction:**
  ```powershell
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-c04-privacy-matrix.ps1 -RunRoot build/7db73b61c2ca46ab8ee0eb5807409b82 -CompileOnly -ExpectedSourceHead 9bd951a69ba42ca58f17b16bf06c147b8faa0584 -TimeoutSeconds 900
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-c04-privacy-matrix.ps1 -RunRoot build/2076d3e9ffb94f268da897439ab79fc5 -CompileOnly -ExpectedSourceHead cc20037fcc424e13051ec20c3bda0a18ff2f49de -ReuseLibraryCacheFrom build/7db73b61c2ca46ab8ee0eb5807409b82/staging-project/Library -TimeoutSeconds 900
  ```
- **Actual vs expected:** first compile exited 1 on malformed JSON escaping (`CS1056`, `CS1003`, `CS1010`); after that source fix, the next compile exited 1 because `C04ControlContract` was missing (`CS0103`). Expected: both issues fixed and a final Unity script compilation with nonempty `Mahjong.Unity.App.dll`. The later attempt did not reach C# compilation because package extraction failed first with ENOSPC.
- **Evidence:** `build/7db73b61c2ca46ab8ee0eb5807409b82/unity-compile.log`, `build/2076d3e9ffb94f268da897439ab79fc5/unity-compile.log`, their `summary.json` and sealed manifests. First repair map: escape helper corrected in `cc20037f...`; second: accepted `C04ControlContract.Assert` helper included in `c29af3f0...`.
- **Unified repair/acceptance:** retain both compiler failures, compile the exact final code head successfully, require no compiler errors and a nonempty final app assembly, then use that build closure for PlayMode and the one-package Windows matrix. The two source changes are present; compilation after them remains unverified due the later external disk failure.
- **Why newly found:** the driver did not exist in the starting candidate. Its syntax was only executable after the first authorized implementation, and the missing helper surfaced after the syntax error was repaired and Unity advanced to the next compile diagnostic.

### D05-FU-01B — MAJOR (resolved) — committed driver diff contained seven whitespace-only blank lines

- **Location:** `unity/Mahjong.Unity/Assets/Scripts/App/C04PrivacyPlayerDriver.cs:29,149,246,436,462,509,523` relative to the original task diff.
- **Reproduction:** `git diff --check 603005a4cd87e889b3b13264df8c70660a54ae47 c29af3f073844586a9e6732b166f8c4d954e3084` returned native exit 2 with seven trailing-whitespace findings. Final assertion: `git diff --check 603005a4cd87e889b3b13264df8c70660a54ae47 92d29d4038f3d87b5b9fec3b626eca7254f623c8` returned native exit 0.
- **Actual vs expected:** the complete source delta failed whitespace validation; expected zero findings.
- **Evidence and repair map:** `build/e1750131bd014e5dac7df4be5d2dc0f3/historical-diffcheck-failure.log`, `historical-diffcheck-failure.exit`, `final-code-diffcheck.log`, `final-code-diffcheck.exit`, and `diffcheck-comparison.json`. Removed all seven lines' trailing spaces in commit `92d29d40...`.
- **Acceptance assertion:** final diff-check exit 0 and clean worktree; both hold at final code head. No runtime assertion was weakened.
- **Why newly found:** the runner's preflight checks the current working tree against its own HEAD; whitespace was already committed during the implementation sequence. The complete diff against the task's starting head exposed it during final source review.
### D05-FU-02 — MAJOR — LocalHost recovery test accepts a stale decision snapshot, then reports no follow-up commit

- **Location:** `dotnet/tests/Mahjong.LocalHost.Tests/LocalHostRecoveryTests.cs:72,87–143,147–158,794–813` and `dotnet/tests/Mahjong.LocalHost.Tests/LocalHostCommandTests.cs:159–220`. Both test files are outside the 11-path allowlist and were not modified.
- **Reproduction command:** the retained original full-suite command is `D:/Unity/dotnet/dotnet.exe exec dotnet/tests/Mahjong.LocalHost.Tests/bin/Release/net8.0/Mahjong.LocalHost.Tests.dll -noLogo -automated -parallelMode none -failSkips -result-xml build/5c4608d017774b4798a9a54ea174850a/localhost.xml`. It produced `178/179`, native exit 1, failing `ColdRestartBoundaryIsStableBeforeAndAfterAutomaticFollowUp`: `observed=1 discard=1`. The unchanged isolated rerun was already `1/1` pass and was not repeated.
- **Actual vs expected:** after the test blocks seat 1's AI submit, confirms the durable discard boundary, releases the AI hook, and calls `WaitForQuiescentSnapshotAsync(session, discard.CommandVersion)`, the helper permits `StateVersion >= minimumVersion` and treats any non-null `PrivateView.Decision` as quiescent. That permits the same version-1 snapshot to return before a new follow-up commit. The caller then cancels the snapshot pump and fails its strict `final.StateVersion > discard.CommandVersion` assertion. The expected condition for this test is a snapshot strictly newer than the discard and an authoritative quiescent decision/boundary for the intended owner/current seat; a stale decision alone cannot satisfy it.
- **Evidence:** original full-suite XML and first-failure XML in `build/5c4608d017774b4798a9a54ea174850a/localhost.xml` and `localhost-failures.xml` (SHA-256 `4923FAF6980E86672C259505112C606733D2972891D9DDE3159F59F552C85EFD` and `A189BAFCE72D36F4714FD4D8747C2A377AD73F7A588C7E4ABF6EEAFD06855AFC`); isolated `1/1` XML remains there (SHA-256 `22025273856432D5A8C5E45A00096208C92407B618CDF398198A0C62E51E33B5`). Source condition is at lines 794–813; terminal assertion is lines 155–158.
- **Repair request to caller/reviewer:** outside this task, change the recovery test's wait predicate to require `StateVersion > discard.CommandVersion` for automatic follow-up, and bind the accepted decision/boundary to the current authoritative seat/decision/epoch rather than any non-null private decision. Keep the existing strict post-release state-version assertion and add a deterministic assertion that the released AI action commits before the pump is cancelled. Do not patch production behavior or expand this task's allowlist here.
- **Acceptance assertion:** preserve the first failure; with the test-harness fix, rerun relevant deterministic control and LocalHost full suite, require `179/179`, zero skipped, and show final state version greater than discard with matching durable state/event and the chosen seat/epoch. The isolated pass does not erase the original full-suite failure.

The earlier driver compile failures are also recorded in the Stage B table: malformed JSON escaping fixed by `cc20037f...`, missing `C04ControlContract` fixed by `c29af3f0...`, and seven diff-check whitespace findings fixed by `92d29d40`. They were first observable when the newly authorized driver reached Unity compilation or the complete starting-candidate diff review; none was hidden by reducing tests. The compile corrections have source and contract-test evidence, but await a successful compiler run. The disk-full condition is an environmental blocker, not a source defect and not permission to skip the final build.

## Final evidence index and handoff

Round 2 evidence roots: `build/7c99844ca7b948c5a0a6102681d8e885/` final preflight; `build/df585bfd1e69490fb49da2bb038cc637/` final byte-verified Unity input copy; `build/5507d82c4dfd4c6f824dc198c934d841/` driver source contract; `build/b10786ba7a39435e8504c49cff76e791/` runner contract; `build/7db73b61c2ca46ab8ee0eb5807409b82/`, `build/2076d3e9ffb94f268da897439ab79fc5/`, `build/31bce07fd08f4f5c98477010fc75f388/`, `build/33e03293cfa24ff3bc7e0e2d91d64cb5/` compile attempts and retained failures; `build/4edc2db38ffc407ba02e28ff74236ba5/` and `build/2dad8ad6edbc4c03aa3fe4ab563b9099/` preflight invocation errors; `build/e1750131bd014e5dac7df4be5d2dc0f3/` contains the reproducible historical/final diff-check pair. Round 1 component/verifier evidence remains under `build/5c4608d017774b4798a9a54ea174850a/`.

A consolidated file-level manifest SHA-256 is recorded here after all owned writers stopped: `afce1a7f40e3e8b7c9eb3ada21a6276440a775d3aad2fe00c639d46a77e3c64d` (`build/d05-final-unified-round2-evidence.sha256`; 6,248 entries / 783,189,362 input bytes). Per-run seal manifests and hashes above remain intact. The manifest excludes only Unity `Library` caches, which the runner itself excludes from each seal; no sealed evidence file was rewritten.

No push, PR operation, reviewer approval, merge, deployment, scheduler/complete-flag edit, or old-task status edit was performed. The report is docs-only and separate from source/build identity. Dedicated reviewer/caller retains final D05 acceptance authority; this run does not authorize wider C04/G2/G5 release.

---
# G2-C04-D05 Final Unified Validation Delivery

Date: 2026-11-03 (Asia/Shanghai)
Executor: Codex / GPT-6-Luna
Independent acceptance: dedicated caller/reviewer (not performed by executor)

## Round 1 result (historical; current outcome is recorded first below)

**NOT ACCEPTED / INCOMPLETE.** This run completed environment checks, fresh component builds and tests, verifier negatives, and runner tests. It did not produce a final frozen Unity closure, run the seven PlayMode cases, build an ordinary Windows `Mahjong.exe`, or execute the privacy/control/recovery matrix in that same package. No P1/P2 or G5 row is passed. This report preserves the first failure and does not combine the prior Test Player results with a different executable.

## Round 1 identity and scope (historical)

| Item | Value |
|---|---|
| Workspace / branch | `E:/majiang-review/g2-c04-d05-final-unified-luna6-20261103` / `wt/v5-g2-c04-d05-final-unified-luna6-20261103` |
| Starting code head | `603005a4cd87e889b3b13264df8c70660a54ae47` |
| Accepted PR20 parent candidate | `2c8238cd29386ab52994c8e3839dfdb85059660c` |
| Starting worktree | clean |
| Final code head | `603005a4cd87e889b3b13264df8c70660a54ae47` (no code changes) |
| Final tracked change | this delivery report only; separate from code/build identity |
| Unity / editor build | `6000.3.27f1` / `6000.3.27.6761814`, `D:/Unity/Hub/Editor/6000.3.27f1/Editor/Unity.exe` |
| Unity dotnet / SDK | `8.0.3226.50405` / `8.0.426` |
| Initial PR20 CI observation | At the task handoff, runs `41593580643` and `41593574031` were both `IN_PROGRESS` for `2c8238cd...`; neither was polled again and neither certifies this harness. |

The accepted D05 privacy, recovery, and control components were treated as prior inputs. Their historical green results were not used to pass this unified application matrix. The task card, repository instructions, long-validation template, review/rework protocol, channel boundary, implementation task, and prior privacy/verifier/recovery/control delivery records were consulted. The exclusive stage card is `build/5c4608d017774b4798a9a54ea174850a/stage-card.txt`.

## Stage A: environment and identity

The initial source identity and clean state matched the assignment. Unity and dotnet executables were present. The shared mutex `Local\MahjongV5P45UnityValidation` was available at the short probe; it was not held while Unity work was attempted because the task never entered Unity execution. Existing Unity PID 12824 belongs to another worktree (`E:/majiang-review/g2-c04-d05-luna6-20261102-c04d05a1`) and was left untouched. The E: workspace had 2.7 GB free at the first probe and 2.48 GB free at final inspection. No license or machine setting was changed. The final source preflight was read-only and reported clean source, zero diff-check errors, 540 closure files / 14,169,048 bytes, source closure SHA-256 `4af3f7ecd4c573bf77295565cc6d5d9c2593056a0d3f3ee2ea7b93eaac6affcf`; preflight seal manifest SHA-256 `22097f936bb255a96f64b832824983b512e290cdb02b5a506f53b986ae534529`. This preflight is not a Unity import, build, or license check.

Preflight command:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-c04-privacy-matrix.ps1 -PreflightOnly -ExpectedSourceHead 603005a4cd87e889b3b13264df8c70660a54ae47 -RunRoot build/911f82c6a5544ec58a3430127214ad77
```

Native exit 0; `summary.json` status `PASS`, mode `playmode`, `preflightOnly=true`, with no test XML or Unity process. Evidence: `build/911f82c6a5544ec58a3430127214ad77/`.

## Stage B: component builds and real test DLLs

Commands ran from this checkout, writing fresh Release outputs under its `dotnet/**/bin/Release/net8.0` directories. Test DLLs were invoked directly with the Unity-bundled .NET 8 host; these are test execution results, not merely successful builds.

| Component | Build / execution | Result |
|---|---|---|
| LocalHost | `D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj -c Release --nologo -v:m -nr:false` then direct `Mahjong.LocalHost.Tests.dll -noLogo -automated -parallelMode none -failSkips -result-xml build/5c4608d017774b4798a9a54ea174850a/localhost.xml` | Build 0 warnings / 0 errors. Tests 179 total: 178 passed, **1 failed**, 0 skipped/errors/not-run; native exit 1. XML SHA-256 `4923FAF6980E86672C259505112C606733D2972891D9DDE3159F59F552C85EFD`. First failure retained in `localhost-failures.xml` (SHA-256 `A189BAFCE72D36F4714FD4D8747C2A377AD73F7A588C7E4ABF6EEAFD06855AFC`). |
| LocalHost isolated repro | Same DLL with `-method Mahjong.LocalHost.Tests.LocalHostRecoveryTests.ColdRestartBoundaryIsStableBeforeAndAfterAutomaticFollowUp -noLogo -automated -parallelMode none -failSkips -result-xml build/5c4608d017774b4798a9a54ea174850a/localhost-repro.xml` | 1/1 passed, native exit 0, XML SHA-256 `22025273856432D5A8C5E45A00096208C92407B618CDF398198A0C62E51E33B5`. It does not erase the full-suite failure. No unchanged full-suite retry was run. |
| TableRuntime | Fresh dependency output from LocalHost build; direct test DLL run with `-noLogo -automated -parallelMode none -failSkips -result-xml .../tableruntime.xml` | 483/483 passed, zero skipped/errors/not-run, native exit 0; evidence `build/5c4608d017774b4798a9a54ea174850a/tableruntime.xml`. |
| Client | `dotnet build dotnet/tests/Mahjong.Client.Tests/Mahjong.Client.Tests.csproj -c Release --nologo -v:m -nr:false`, then fresh test DLL with `-noLogo -automated -parallelMode none -failSkips -result-xml .../client.xml` | Build zero warnings/errors; 182/182 passed, zero skipped/errors/not-run, native exit 0; XML SHA-256 `6DBC4ED1897AD4FD1EDCAA7120B5016264241BD891F7FED298726B9C27B436C3`. |
| TableModel | `dotnet build dotnet/tests/Mahjong.Unity.TableModel.Tests/Mahjong.Unity.TableModel.Tests.csproj -c Release --nologo -v:m -nr:false`, then fresh test DLL with `-noLogo -automated -parallelMode none -failSkips -result-xml .../tablemodel.xml` | Build zero warnings/errors; 236/236 passed, zero skipped/errors/not-run, native exit 0; XML SHA-256 `3B4BD9D4BF789829B0C818A318725256FE05C243041F804D8CEFCEF10497E969`. |

The LocalHost first failure is `LocalHostRecoveryTests.ColdRestartBoundaryIsStableBeforeAndAfterAutomaticFollowUp`, at `dotnet/tests/Mahjong.LocalHost.Tests/LocalHostRecoveryTests.cs:155`: expected an automatic follow-up commit after release, observed `version=1`, `discard=1`. Its test source is outside the 11-path authorized edit list. The full-suite failure and isolated success are both retained; this run has not established whether it is timing-only or a product defect.

## Stage C: producer/verifier/runner alignment and negative coverage

The accepted verifier was freshly built and exercised without changing it:

```powershell
D:/Unity/dotnet/dotnet.exe build dotnet/tools/C04PrivacyMatrixVerifier/C04PrivacyMatrixVerifier.csproj -c Release --nologo -v:m -nr:false
D:/Unity/dotnet/dotnet.exe exec dotnet/tools/C04PrivacyMatrixVerifier/bin/Release/net8.0/C04PrivacyMatrixVerifier.dll self-test build/5c4608d017774b4798a9a54ea174850a/verifier-self-test
```

Build native exit 0, zero warnings/errors. Self-test native exit 0: `SELF_TEST_PASS positive=17 negative=67 serializedFrames=5 source=WireCodec`. The 67 WireCodec-derived mutations were also submitted individually to the real `verify` CLI: 67/67 rejected (native nonzero for each), zero accepted. Summary `build/5c4608d017774b4798a9a54ea174850a/verifier-cli-negative/summary.json`, SHA-256 `8C939F17B9F31F45DE1F9DF4FEE3D516A8E2F39E09D64D56D8F3156AF6A3B4CF`. These are tool fixtures, not GAME traffic or app proof.

Runner test command:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/C04PrivacyRunner.Tests.ps1 -TestRunRoot build/5c4608d017774b4798a9a54ea174850a/runner-tests
```

Native exit 0; 16/16 runner contract tests passed, including XML/marker/package identity, timeout/cancel/finalization, scene requirements, mutex wait, and sealing checks. These are runner tests, not Player execution.

**Known integration gap:** the current runner has `-PreflightOnly`, `-BuildPlayer`, and `-WindowsPlayerTests`; the latter launches a StandaloneWindows64 Test Player. It has no ordinary Windows `Mahjong.exe` seven-case/restart matrix. The authorized `C04PrivacyPlayerDriver.cs`, its `.meta`, and `tools/tests/C04PrivacyPlayer.Tests.ps1` were absent. They were not added in this run. Therefore the old Test Player and separate ordinary application cannot be combined as a same-package result. No `Mahjong.exe` package, package manifest, eight-DLL before/after comparison, raw Player capture, or P2 process restart exists in this run's evidence.

## Stages D and E

Stage D did not start: the source/DLL/package freeze gate was not met because the LocalHost component suite contained an unresolved failure and the authorized ordinary Player driver/runner adaptation was absent. No unchanged expensive Unity retry was made. Consequently there is no seven-case PlayMode XML, no ordinary Windows Player package, no `Mahjong.exe` case result, no before/after package hash comparison, and no same-scope/request P2 query/reconciliation after process restart.

The seven required PlayMode names and run counts are retained here; each is **NOT_RUN (0/1)**, not a filtered success:

1. `Mahjong.Unity.Tests.PlayMode.C04PrivacyRoleSwitchPlayModeTests.RealRoom_FourPlayersSpectatorAndRoleSwitchUseIndependentSessions` — NOT_RUN (0/1)
2. `Mahjong.Unity.Tests.PlayMode.C04PrivacyRoleSwitchPlayModeTests.RealRoom_DelayedOldRawReceiveIsRejectedByNewBinding` — NOT_RUN (0/1)
3. `Mahjong.Unity.Tests.PlayMode.C04PrivacyRoleSwitchPlayModeTests.RealRoom_ColdReopenReconcilesOriginalRequestWithoutDuplicate` — NOT_RUN (0/1)
4. `Mahjong.Unity.Tests.PlayMode.C04PrivacyRoleSwitchPlayModeTests.RealRoom_LegalSleeveAndFeeOwnershipAcrossFourSeatsAndSpectator` — NOT_RUN (0/1)
5. `Mahjong.Unity.Tests.PlayMode.C04PrivacyRoleSwitchPlayModeTests.RealRoom_ReleasedSeatAiAdvancesAfterPlayerCloseWithSpectatorOnly` — NOT_RUN (0/1)
6. `Mahjong.Unity.Tests.PlayMode.C04PrivacyRoleSwitchPlayModeTests.RealRoom_OpenDecisionDeadlineContinuesAfterPlayerClose` — NOT_RUN (0/1)
7. `Mahjong.Unity.Tests.PlayMode.C04PrivacyRoleSwitchPlayModeTests.RealRoom_ClosedPlayerToSpectatorToNewPlayerReceivePathRejectsOldRaw` — NOT_RUN (0/1)

No owned Unity Player writer or Player process was started by this run. Existing PID 12824 was not ours and remains untouched. The preflight run wrote its own sealed manifest. The component/tool logs and XMLs are stable under the task GUID evidence directory; no Unity build/cache writer was started. The package and Player-stage manifests are absent because those stages did not run.

## Fixed matrix status

`BLOCKED` means the required final unified application evidence was not produced. Earlier accepted component results and this run's tool-fixture green results do not promote these rows.

| Row | Status | Evidence boundary |
|---|---|---|
| N1 | BLOCKED | No final unified Player/PlayMode owner-private and same-version legal-action evidence. |
| N2 | BLOCKED | No final unified spectator-public-only application evidence. |
| N3 | BLOCKED | No final unified non-empty sleeve private-content and owner/audience/version evidence. |
| N4 | BLOCKED | No final unified delayed old raw receive injection through the app binding path. |
| N5 | BLOCKED | No final unified bounded slow-consumer saturation/cursor/close isolation run. |
| N6 | BLOCKED | No final unified control epoch/owner/Ready/deadline matrix in app. |
| N7 | BLOCKED | No final unified same original request/scope/canonical payload reconciliation in Player, including process restart. |
| N8 | BLOCKED | No final unified app isolation/lease handoff proof. |
| U1 | BLOCKED | No final seven-case PlayMode run against the final frozen closure. |
| U2 | BLOCKED | No final role-switch, cache-clear, real delayed-wire and non-empty sleeve/fee application evidence. |
| U3 | BLOCKED | No final Unity control/recovery/deadline matrix. |
| P1 | BLOCKED | No ordinary `Mahjong.exe` package matrix; Test Player is a different binary and was not used as substitute. |
| P2 | BLOCKED | No same-package Windows process restart and original-outbox query proof. |
| C04-G5-01 | BLOCKED | No final four-owner real sleeve privacy positive comparison. |
| C04-G5-02 | BLOCKED | No final real non-empty private fee/ledger comparison from a legal production command. |
| C04-G5-03 | BLOCKED | No final Unity/Windows delayed old raw receive rejection. |

## Consolidated known blockers / defects

### D05-FU-01 — BLOCKER — authorized same-package app driver/runner is not implemented

- **Location:** `unity/Mahjong.Unity/Assets/Scripts/App/C04PrivacyPlayerDriver.cs` and `.meta` are absent; `tools/tests/C04PrivacyPlayer.Tests.ps1` is absent; `tools/unity/run-p45-c04-privacy-matrix.ps1` has no ordinary `Mahjong.exe` matrix mode. The allowlist also permits the build adapter, PlayMode/test adaptations and this report.
- **Reproduction:** `Get-ChildItem unity/Mahjong.Unity/Assets/Scripts/App/C04PrivacyPlayerDriver.cs,tools/tests/C04PrivacyPlayer.Tests.ps1` reports missing paths; inspect runner parameters and invoke `-WindowsPlayerTests`, which selects `StandaloneWindows64` Test Player instead of the built ordinary `player/Mahjong.exe`.
- **Actual vs expected:** actual available route cannot execute all seven equivalent cases plus P2 process restart using one immutable ordinary Windows package; expected explicit CLI opt-in driver invoking real `AppComposition` / room / channel / `TableSession`, with normal launch allocating no driver resources and runner evidence tying each case and P2 restart to one package hash.
- **Evidence:** no Player artifacts in `build/5c4608d017774b4798a9a54ea174850a/`; prior Test Player distinction is documented in the source task card and prior privacy delivery. Current runner source head is `603005a4...`.
- **Repair required:** implement and test the minimum authorized opt-in driver, Unity build/runtime argument wiring, one-build multi-case runner, exact seven-case result checks, package before/after per-file hash check, and P2 original scope/request/canonical/outbox reconciliation across process restart. Keep normal launch resource/behavior neutral. Use only the 11 approved tracked paths.
- **Acceptance assertions:** missing/malformed opt-in arguments fail before any host/path/thread is created; no args leaves ordinary launch unchanged; all seven exact names execute nonzero and produce independently verified real raw evidence; all seven and P2 run from one package with the same package manifest before/after; P2 uses the original request and produces exactly one side effect/receipt without resubmission.

### D05-FU-02 — MAJOR — fresh LocalHost full-suite recovery follow-up assertion failed

- **Location:** `dotnet/tests/Mahjong.LocalHost.Tests/LocalHostRecoveryTests.cs:72,155` (outside the permitted edit list).
- **Reproduction:** from the repository root, execute the fresh DLL:
  ```powershell
  D:/Unity/dotnet/dotnet.exe exec dotnet/tests/Mahjong.LocalHost.Tests/bin/Release/net8.0/Mahjong.LocalHost.Tests.dll -noLogo -automated -parallelMode none -failSkips -result-xml build/5c4608d017774b4798a9a54ea174850a/localhost.xml
  ```
  The isolated diagnostic command was:
  ```powershell
  D:/Unity/dotnet/dotnet.exe exec dotnet/tests/Mahjong.LocalHost.Tests/bin/Release/net8.0/Mahjong.LocalHost.Tests.dll -method Mahjong.LocalHost.Tests.LocalHostRecoveryTests.ColdRestartBoundaryIsStableBeforeAndAfterAutomaticFollowUp -noLogo -automated -parallelMode none -failSkips -result-xml build/5c4608d017774b4798a9a54ea174850a/localhost-repro.xml
  ```
- **Actual vs expected:** full fresh DLL run 178/179, native exit 1; expected follow-up state to advance after release, observed `version=1`, `discard=1`. Isolated test rerun passed 1/1, so the cause is unresolved, not repaired.
- **Evidence:** `build/5c4608d017774b4798a9a54ea174850a/localhost.xml` and `localhost-failures.xml`; hashes in Stage B. First failed XML preserved.
- **Repair/triage required:** determine whether concurrency/order or a real recovery defect caused the mismatch using a deterministic minimal reproduction. If source repair is required, it is outside this task's allowlist; provide the minimal repro and exact required path to caller/reviewer instead of editing out of scope. Do not weaken the assertion or rerun the unchanged full suite to overwrite the failure.
- **Acceptance assertions:** explain and reproduce the original failure; demonstrate stable full-suite success from a relevant changed input or prove the exact test is an unrelated nondeterministic harness issue with caller review; zero skips and retain the original failure artifact.

These two items are the complete known blockers from this run. The absent driver is unfinished authorized glue, not evidence of a production business defect. No evidence supports additional production defects. No BLOCKER/MAJOR fix was completed in this run; therefore this delivery is not an acceptance request and must not be treated as approval.

## Evidence index and final repository state

Evidence root: `build/5c4608d017774b4798a9a54ea174850a/` (ignored build output). It contains the stage card, fresh component XMLs/build log, retained first failure, isolated repro, verifier self-test, 67 negative verifier case logs/summary, and 16 runner test logs. A stable manifest was written after all owned writers stopped: 274 pre-seal evidence files, manifest SHA-256 `178d92967300caa4f3ae9e0f43a21ac1c5d0c3653572c0e5bb4ad1b9b4d4e736`, recorded in `final-evidence-seal.txt`. Preflight source seal: `build/911f82c6a5544ec58a3430127214ad77/`. No raw frames, Unity test XML, Player package, eight-DLL comparison, or Windows process records were generated.

Final tracked source remains at `603005a4cd87e889b3b13264df8c70660a54ae47`; `git diff --check` was clean before this report was added. Report-vs-code identity is deliberately separate. No push, PR operation, merge, deployment, scheduler/complete-flag change, or old-task status change was performed. Dedicated reviewer/caller retains independent acceptance authority.

---

# Round 5 continuation: R4 repair mapping and final candidate evidence

**Current executor result: NOT ACCEPTED / FINAL WINDOWS MATRIX BLOCKED.** All R4-authorized glue defects that could be corrected within the original 11 paths were corrected together, and the frozen code head passed the exact seven-case PlayMode suite. The final ordinary Windows matrix could not acquire the shared Unity mutex within 600 seconds, so it produced no ordinary `Mahjong.exe`, P1/P2 run, eight-DLL/package comparison, or final Windows raw artifacts. The mutex was held by Unity work in other worktrees; those processes were left untouched. FU-02 remains an unresolved diagnosis in read-only code. This report does not request or imply independent acceptance.

## R5 identity, scope, and retained checks

| Item | Value |
|---|---|
| Code head | `054efd5d15e90c3cdbd821231d6c10482f7c9e72` |
| R5 code changes since R4 code head `6b4dc2436755382fa792cd9720a204a63fa9e16d` | `f45d24cd`, `07928474`, `82ab4650`, `338b9350`, `0878137c`, `d936bedf`, `8aad62c9`, `dee2c7ec`, `f00caa62`, `db6c1f44`, `054efd5d` |
| Current code worktree | clean before this docs-only update; final `git diff --check 603005a4..054efd5d` exit 0 |
| Changed tracked code paths | `tools/tests/C04PrivacyPlayer.Tests.ps1`, `tools/tests/C04PrivacyRunner.Tests.ps1`, `tools/unity/run-p45-c04-privacy-matrix.ps1`, `unity/Mahjong.Unity/Assets/Scripts/App/C04PrivacyPlayerDriver.cs`, `unity/Mahjong.Unity/Assets/Scripts/App/C04PrivacyProbe.cs`, `unity/Mahjong.Unity/Assets/Tests/PlayMode/C04PrivacyRoleSwitchPlayModeTests.cs`; all within the original allowlist |
| Unity / dotnet | Unity `6000.3.27f1` (`6000.3.27.6761814`); Unity dotnet host `8.0.3226.50405`, SDK `8.0.426` |
| Final PlayMode source closure | 540 files / 14,205,136 bytes; SHA-256 `1cf801265dcbee2c826c79c4d0f59eddc8a10bb4a5ebdd108a49e2319b7dad97`; staged inputs 366 files / 33,559,963 bytes; SHA-256 `2bb491798fd990d63318d0a4a1c2554a41bf2454083d73ece608e7c15c247961` |
| Runner behavior tests | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/C04PrivacyRunner.Tests.ps1 -TestRunRoot build/runner-r5-canonical-raw-1ef95dca5b3a44a59d1ec0e89fdf2b95`; native exit 0, 32/32, zero failures; `runner-tests.json` SHA-256 `89ec10fa14c1f8b12f255f8d938cffcae45d50d7b948d5d327c21d0440221ae` |
| Player source-contract tests | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/C04PrivacyPlayer.Tests.ps1 -RunRoot build/4f07e35713094ba5be6752cf4f88b71d`; native exit 0, 47/47, zero failures; summary SHA-256 `b013fedce608d5e4162cef8e600c5c690eae688a0a9ddcb2d0dd455b670555ac` |

The fixture-only runner tests execute the actual semantic consumer and retain positive and negative controls. They reject wrong original/current decision relationships, unauthorized scope/binding/account/role changes, malformed or missing deadline/reconnect evidence, stale command mutation, stale commit metadata, duplicate command evidence, pending commands, invalid raw hashes or binding mappings, meaningless extra files, and over-budget fixture roots. Fixture results remain tool evidence, not GAME or Player evidence. The accepted `C04PrivacyMatrixVerifier` was not modified or relaxed.

## D: final frozen PlayMode result

Exact command:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-c04-privacy-matrix.ps1 -ExpectedSourceHead 054efd5d15e90c3cdbd821231d6c10482f7c9e72 -RunRoot build/4c0b6ab4ddcc4e8aaf19bd31a5089717 -TimeoutSeconds 1200 -MutexTimeoutSeconds 600 -MutexHeartbeatSeconds 30
```

Runner summary is `build/4c0b6ab4ddcc4e8aaf19bd31a5089717/summary.json` (SHA-256 `d037c0aa6f982f4727a0d6acc9d70c2c312d1b6d8a802337f059278f4ce2cc9f`); exact XML is `playmode-results.xml` (7 total / 7 passed / 0 failed / 0 skipped; SHA-256 `3eb9285b0f4757a167b064dd03be63a58422b83b0c7e5dedfa3f9a8a2b058b30`). The stable run manifest is `manifest.sha256`, SHA-256 `7eac44bc1091f5e95c340f92c78dd6cfa6335fedf18ec6ef1521d1c979211498`. Unity exited native 0 after 140.373 seconds. The XML contains all seven required full test names, with no filter mismatch or skipped case. The verifier build, verifier self-test (`positive=17`, `negative=67`, `serializedFrames=5`), shared-plugin provenance, and all four real PlayMode raw verifier steps exited native 0. This proves the PlayMode candidate only; it does not claim the Editor binary and Windows Player binary are identical.

| Real PlayMode raw | Frames | SHA-256 | Verifier process |
|---|---:|---|---|
| role-switch | 11 | `179a5bf4d9f391e0b1ac5743118186d2c98d15a326f0b791cee9f7456cc1844a` | native 0 |
| four-owner | 54 | `b605c9755ab52deadc22cd5e5dd4e496579614286c29d1ac55272bb11665ac69` | native 0 |
| closed-binding | 6 | `6eeb014844468d21a1e125d570176d01e1d009fea0b9470778fd51b33e4cb15e` | native 0 |
| recovery | 8 | `9e88c12beb8657c0f7794acda2e576e427db2b63e4527ac254265110773dd953` | native 0 |

The R5 recovery capture waits, within a finite bound, for the actual four TableSessions to reach the observed state-version/event-sequence floor before sealing raw; this preserves the verifier's historical owner-projection checks. The prior raw missing-owner-projection failure remains preserved under the earlier run root and was not overwritten. The released-control raw is retained as required case evidence and checked by its dedicated binding/hash consumer; it is not incorrectly routed through the canonical four-owner verifier, whose single-scope/four-seat contract is not applicable across formal WELCOME scope rotations.

## E: final ordinary Windows run and resource boundary

Stage card: `build/r5-stage-5f909f64e2f8446e9da27b5e5d603a51/stage-card.txt`.

Exact command:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-c04-privacy-matrix.ps1 -WindowsAppMatrix -ExpectedSourceHead 054efd5d15e90c3cdbd821231d6c10482f7c9e72 -RunRoot build/6b35f4d0a1114ee38fdd5a9c0301cf73 -TimeoutSeconds 1200 -MutexTimeoutSeconds 600 -MutexHeartbeatSeconds 30
```

The runner verified clean code head `054efd5d...`, the same 540-file source closure, and 366 freshly staged inputs. Its verifier build and self-test exited native 0. It then waited for `Local\MahjongV5P45UnityValidation` for the full 600-second limit, emitting 30-second heartbeats, and exited nonzero with `Unity mutex wait exceeded the configured bound of 600 seconds.` Summary `build/6b35f4d0a1114ee38fdd5a9c0301cf73/summary.json` records `status=FAIL`, `acquired=false`, source clean, and no Unity process; SHA-256 `f1f80ef7004c96cc0cc1d168841cb2bb7f9aea96724a694bfc1dd8927fc319d`. Stable seal `manifest.sha256` SHA-256 `e955c6e4237a8286c0d8a9428cbab4d1094d5a809017be6cad37ee8c5bdd557e`, 700 entries. No ordinary Player package or Windows scenario started.

At the precheck, E: had 311,700,496,384 free bytes. Another worktree had Unity PID 12824 running its PlayMode test, and a separate worktree had Unity PID 31500 building its C05 Android package. Neither was owned by this task; both were left untouched. Because those writers remained active after the bounded wait, the shared-resource condition had not changed and no immediate identical retry was made. The prior final Windows attempts and their failures remain separately sealed; none are substituted for the current code head.

## R5 complete known finding list and mapping

The five existing findings remain the complete set. R5 adds no confirmed production-business defect. The R4-specific glue defects are repaired within scope; where final Windows evidence is still needed, the finding remains open with that evidence boundary stated explicitly.

### D05-FU-01 — BLOCKER — final unified ordinary Windows/P1/P2 matrix is still not executed

- **Location:** final frozen Windows route in `tools/unity/run-p45-c04-privacy-matrix.ps1`; report Stages D/E.
- **Reproduction:** run the exact Windows command in the preceding section. It reaches the shared mutex gate and fails after 600 seconds before Unity starts. The current PlayMode command above independently runs the seven exact tests.
- **Actual vs expected:** current code has final PlayMode 7/7, zero skips, four verified real raw sets. The ordinary Windows run has no package, no case execution, no eight-shared-DLL comparison, and no process-restart/outbox P2 proof because the runner could not acquire the shared mutex. Expected: one ordinary `Mahjong.exe` package covering the seven case flows plus original-request P2 restart/reconcile, with before/after package file identity unchanged.
- **Evidence:** `build/4c0b6ab4ddcc4e8aaf19bd31a5089717/summary.json`, XML and raw artifacts; `build/6b35f4d0a1114ee38fdd5a9c0301cf73/summary.json`, `runner.log`, and `manifest.sha256`.
- **Repair/next action:** no code repair is currently indicated by this lock failure. Resume only after the external Unity writer releases the mutex; then run the same frozen code head through the one-build Windows matrix. Do not bypass the lock or use the Test Player as a substitute.
- **Acceptance assertion:** one immutable ordinary Windows package supplies all seven cases and P2; exact original scope/request/canonical/outbox query yields the original receipt and exactly-once side effect across real process restart; package file lengths/SHA and eight shared DLLs match before/after; full raw, N/U/P/G5 evidence is independently reviewable.

### D05-FU-02 — MAJOR — LocalHost recovery historical first failure remains unproven

- **Location:** read-only `dotnet/tests/Mahjong.LocalHost.Tests/LocalHostRecoveryTests.cs:72,147–158,794–813`.
- **Reproduction:** preserve the original full-suite command and XML from `build/5c4608d017774b4798a9a54ea174850a/localhost.xml`; do not rerun the unchanged full suite merely to seek a green result. The same command is recorded in Round 1 Stage B above.
- **Actual vs expected:** the retained full suite was 178/179 with `version=1`, `discard=1`; an isolated rerun passed 1/1, and the later controlled positive order observed version 4 with AI commits at versions 2–4 rather than reproducing the failure. No causal reproduction of the original order exists. A source predicate allowing any nonempty decision at `StateVersion >= minimumVersion` remains a hypothesis, not a diagnosis.
- **Evidence:** original `localhost.xml` and `localhost-failures.xml` plus hashes in Round 1; R3 diagnostic materials under `build/87378b88821043b1817af86c14f7f863/diag`.
- **Repair/triage required:** retain the out-of-allowlist test and production files as read-only. A diagnostic-only controlled copy must capture decision/seat/epoch/phase, release ordering, actual commit and durable boundary at the original version-1 observation. If a real fix requires out-of-scope files, provide exact minimal repro and path to caller for a separate scope decision.
- **Acceptance assertion:** deterministic runtime evidence explains the historical failure while retaining strict version, durable event and 136-entity assertions; later canonical 179/179 zero-skip evidence must follow an authorized relevant fix or reviewer determination that the failure is unrelated.

### D05-FU-03 — MAJOR — semantic producer/consumer contract repaired; final ordinary Player artifact still pending

- **Location:** `Assert-C04PlayerCaseEvidence` and raw-scene routing in `tools/unity/run-p45-c04-privacy-matrix.ps1`; reconnect/deadline/control output in `C04PrivacyPlayerDriver.cs` and `C04PrivacyRoleSwitchPlayModeTests.cs`.
- **Reproduction:** fixture behavior matrix: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/C04PrivacyRunner.Tests.ps1 -TestRunRoot build/runner-r5-canonical-raw-1ef95dca5b3a44a59d1ec0e89fdf2b95` (32/32 native 0). Real producer test path: final PlayMode command above, 7/7 native 0; Windows producer command is listed under FU-01 and was blocked before launch.
- **Actual vs expected:** R4 had (a) claim and stale receipt fields conflated, (b) equality demanded between the original stale decision and current live decision, (c) same scope demanded across formal WELCOME rebinding, and (d) reconnect JSON producer/consumer directory mismatch. R5 corrected those relationships and output paths. A newly exposed boundary was also corrected: multi-seat canonical raw verification is restricted to its four designated scenes, while the released-control stream is retained and checked for wire hashes and `(session,bindingEpoch)` scope identity; verifier semantics remain unchanged. Fixture suite and current seven-case PlayMode pass. No final ordinary Windows producer artifact has yet been consumed because of FU-01.
- **Evidence:** R4 fixture repro `build/d663bf9d34924f1ea3419d69e6313915/recovery-consumer-contract.log`; reviewer real component recovery trace `build/d663bf9d34924f1ea3419d69e6313915/control-epoch-recovery.log`; current `build/runner-r5-canonical-raw-1ef95dca5b3a44a59d1ec0e89fdf2b95/runner-tests.json` and PlayMode evidence root `build/4c0b6ab4ddcc4e8aaf19bd31a5089717/`.
- **Repair mapping:** R5 commits through `054efd5d` preserve distinct `Recorded/LOCAL_CONTROL` claim and `Rejected/DECISION_MISMATCH` stale receipts, the original captured decision/action/epoch, and actual old/new WELCOME identities. Consumer checks account/role/room/session, current binding, scope issuance and mapping instead of stale/live decision equality or unchanging active scope. The raw validator selects only the four canonical multi-owner scenes and applies a control-specific verified consumer to released-control raw. No empty file, lowered artifact count, fixture-only state or verifier relaxation was used.
- **Unified negative assertions:** reject wrong account/role/room/session; scope not issued by WELCOME; command scope mismatched to current binding; mutation of captured or stale decision/epoch/action; identical arbitrary wrong reasons; claim epoch not advancing; snapshot owner/epoch mismatch; stale commit metadata; duplicate sends; nonzero pending; malformed/missing wire/hash; wrong binding-scope map; missing/empty deadline or reconnect evidence; false progress; and meaningless extra files. Correct distinct current/stale decisions and legitimate WELCOME scope rotation must be accepted.
- **Acceptance assertion:** the current producer's real ordinary Windows evidence must pass the same semantic consumer, with original stale request rejected exactly once and no commit metadata/pending side effect. This final Windows assertion remains BLOCKED solely because the matrix did not get the shared mutex.

### D05-FU-04 — MAJOR — default fixture path defect closed; final Windows filesystem paths unverified

- **Location:** default roots and semantic evidence fixture layout in `tools/tests/C04PrivacyRunner.Tests.ps1`; real case state/evidence paths in `C04PrivacyPlayerDriver.cs`.
- **Reproduction:** default runner command as recorded in R4, and current explicit-root command listed under FU-03. R4's failing default fixture root and successful short-root retry are both preserved in its evidence section; current R5 behavior matrix includes over-budget pre-write rejection.
- **Actual vs expected:** default path bug is fixed and retained; current runner behavior matrix passes 32/32. The real Windows package never launched in the current candidate, so no claim is made that Player save/outbox/recovery/deadline/control paths fit the actual Windows path boundary. Expected: supported default and GUID roots work without changing machine long-path policy, and every final Windows case artifact is persisted and consumed.
- **Evidence:** current `runner-tests.json`; historical default-root failure and short-root pass under R3/R4 evidence; current Windows lock failure in `build/6b35f4d0a1114ee38fdd5a9c0301cf73/summary.json`.
- **Repair mapping:** compact fixture path names and pre-write budget validation are retained; reconnect evidence path now matches the consumer. No machine setting or seven-case name was changed.
- **Acceptance assertion:** after the shared lock is available, the same Windows package must complete recovery, deadline, closed-binding and control artifacts without path exceptions; all evidence must pass the same consumer and package file identity must remain unchanged.

### D05-FU-05 — MAJOR — formal control recovery assertions corrected and PlayMode verified

- **Location:** `RunReleasedControlScenarioAsync` in `C04PrivacyPlayerDriver.cs` and the matching PlayMode control scenario.
- **Reproduction:** current exact seven-case PlayMode command under Stage D; specifically `RealRoom_ReleasedSeatAiAdvancesAfterPlayerCloseWithSpectatorOnly`. Independent real-component diagnostic command and trace are recorded in R4/R3 reviewer evidence.
- **Actual vs expected:** reviewer real-channel evidence showed cursor-floor regression correctly closes the old binding, then formal recovery over the original session/ticket returns Ready with same session, newly issued scope, binding `1→2`, authoritative control epoch `2`/human, transport open and pending zero; the original stale command is rejected once with `DECISION_MISMATCH` and no command/event commit metadata. R4 glue incorrectly reused cached epoch 1 as the resumed authority and required the active scope to remain unchanged. R5 now distinguishes pre-claim cached epoch from post-WELCOME authoritative epoch, preserves original stale seeds, validates the new formal binding/scope, and uses an explicit serialized recovery order. The exact seven-case PlayMode passes 7/7; no final ordinary Windows run exists.
- **Evidence:** `build/d663bf9d34924f1ea3419d69e6313915/control-epoch-recovery.log` SHA-256 `d1831c4cc4c6822e1e2d1f4b6ad504a25218ada5ebb38a249198d85ed398133e`; current `playmode-results.xml` and recovery raw in `build/4c0b6ab4ddcc4e8aaf19bd31a5089717/`.
- **Repair mapping:** R5 code keeps the old captured claim epoch, claim-before epoch, and resumed authoritative epoch as distinct values; verifies same authentication session/ticket/account/role/seat/room, real WELCOME scope rotation, higher authoritative epoch, new binding, cursor floors, Ready and current owner. It does not resend claim, change the stale decision/action/epoch, lower floors, or submit from a reconnecting state. A normal no-regression Ready positive control remains separate from the forced cursor-floor recovery path.
- **Acceptance assertion:** PlayMode recovery is verified; repeat the same assertions in the final Windows package, including one send of the original stale request, exact `DECISION_MISMATCH`, zero metadata/side effect/pending, and package hash stability. Windows portion remains blocked under FU-01.

## Fixed matrix state at R5

| Row(s) | R5 state | Evidence boundary |
|---|---|---|
| N1–N8 | BLOCKED | Current PlayMode contributes overlapping real application evidence, but the required final unified ordinary Windows matrix was not executed. |
| U1 | PASS | Exact seven PlayMode tests 7/7, zero skips, native Unity exit 0. |
| U2 | PASS | Role switch, delayed old raw, legal nonempty sleeve/fee owner checks and cache/rebind paths are in the exact seven-case PlayMode XML and real raw evidence. |
| U3 | PASS | Control, formal recovery, deadline continuity, closed binding and cold reopen are in the exact seven-case PlayMode XML; final Windows parity remains blocked. |
| P1 | BLOCKED | No ordinary Windows `Mahjong.exe` package was built on code head `054efd5d`. |
| P2 | BLOCKED | No current-head Windows process restart, original outbox query/reconcile, or before/after process/package proof. The earlier package P2 remains supplementary only. |
| C04-G5-01..03 | BLOCKED for final unified matrix; PlayMode evidence retained | Four-owner nonempty private-content/fee and delayed-old-raw PlayMode evidence exists, but no same-candidate ordinary Windows matrix completed. |

The R4 PlayMode 6/7 failure, prior raw owner-projection failure, earlier Unity ENOSPC/import failures, the R5 canonical-raw consumer failure on the prior code head, and the current 600-second Windows mutex failure are all retained as separate immutable run roots. The current head has no final Windows product result; components, fixtures, PlayMode, Test Player and older ordinary packages are not substituted for it.

## R5 tracked state and handoff

R5 code head is `054efd5d15e90c3cdbd821231d6c10482f7c9e72`; this R5 report is a separate docs-only commit and must not be supplied as `-ExpectedSourceHead`. The code and report identities are reported separately. No push, PR operation, approval, merge, deployment, scheduler/complete-flag change, or out-of-scope file edit was performed. Dedicated reviewer/caller retains D05 acceptance authority.

## Round 6 — final unified candidate, typed raw consumer, and interruption recovery

**Executor result: final PlayMode and ordinary Windows application matrix PASS on the frozen code head; overall D05 remains NOT ACCEPTED because FU-02 is unresolved and N5/N8 are not established by the seven-case matrix.** This section supersedes the R5 statement that the current ordinary Windows matrix had not run. Earlier R5 evidence and conclusions above remain historical and unchanged.

### R6 identity, repairs, and retained failures

| Item | Value |
|---|---|
| Final frozen code head | `3aec0c3a92bb87e954163ad1847c78dcb08eba3a` |
| R6 code commits | `b1aefb90d9cc911ba9f56419059f02a272525b04`, `55ff8a1070f1c6ad642a86bfae4bc95f2c9acbb7`, `3aec0c3a92bb87e954163ad1847c78dcb08eba3a` |
| Final PlayMode root | `build/5aeb60c9fc8f4b58aa840b44a02f4b98` |
| Final ordinary Windows root | `build/77cc2a1e52224f1d9336a0987086d90b` |
| Windows source closure | 540 files / 14,240,924 bytes; SHA-256 `e309b48fc9f2ee9f5136e76e4da353e4fdda0b7760db54ae4a92979743a9d154` |
| Staged Unity input | 366 files / 33,560,894 bytes; SHA-256 `0ba359594efce657298720f5c5cec9ffe31012a405a943fa8b4a3517a23c251c` |
| Unity / dotnet | Unity `6000.3.27f1` (`6000.3.27.6761814`); Unity dotnet host `8.0.3226.50405`, SDK `8.0.426` |
| Pre-existing run failures preserved | R5 source `054efd5d` consumer failure; R6 raw-timeline failure at `86f1f559...`; R6 P2 status-case failure at `0c4d5440...`; R6 initial final-raw equal-binding rejection at `build/77cc...`'s predecessor `build/86f1...` as recorded in its immutable root; all earlier Unity ENOSPC/import failures, R4 6/7 PlayMode, and watchdog interruption remain separate evidence. |

R6 code changes remained within the original 11-path allowlist. The five R6 code paths were `dotnet/tests/Mahjong.LocalHost.Tests/C04PrivacyStreamMatrixTests.cs`, `tools/tests/C04PrivacyPlayer.Tests.ps1`, `tools/tests/C04PrivacyRunner.Tests.ps1`, `tools/unity/run-p45-c04-privacy-matrix.ps1`, and `unity/Mahjong.Unity/Assets/Scripts/App/C04PrivacyPlayerDriver.cs`. Production domain/client/authority/protocol/asmdef code and the out-of-scope LocalHost recovery test were not changed.

### R6 defect register and repair mapping

These are the complete known FU-01..FU-05 groups after R6. R6 found no additional confirmed production-business defect. A later reviewer should append a finding only for genuinely new evidence and explain why it could not have been found in these runs.

#### FU-01 — BLOCKER, final unified evidence and interrupted-run audit

- **R5 reproduction / actual:** the bounded Windows runner in `build/6b35f4d0a1114ee38fdd5a9c0301cf73` timed out after 600 seconds waiting for the shared Unity mutex (`acquired=false`); the watchdog-interrupted run under `build/0494d8c914d5476f8e1fb0472f284baf` had a real ordinary Player and case/raw outputs but no `summary.json`, `seal.json`, `manifest.sha256`, or `windows-app-matrix.json`. It was therefore an interrupted run, not a normally finalized failure or a pass.
- **R6 repair and evidence:** the final frozen source completed both the exact seven PlayMode cases and one ordinary Windows package matrix, including P2 process restart. The interrupted old run was separately inventoried under `build/r6-interruption-recovery-e00810ac4de7498d84a5ceb4e76306eb` as `RECOVERED_AFTER_INTERRUPTION`; original files were not rewritten, cache directories were excluded and left untouched, original runner exit remains `UNKNOWN`, and steps after the last raw verifier remain unknown/not run. `recovery-audit.json` SHA-256 `809002AA0521E87A7C09F82C24E29CD8A3E24F072CC1CAC76E5B6E0939D0522C`; original-file inventory SHA-256 `4A4D8D6CDE16620A8F065A2813520EB71126FA2ED6F1C1838A20634E742ECBA6`; audit manifest SHA-256 `154154E0E54013EFAE52924E5888B8B6DC7CC25225FA76ED74B8F3E50C799C31` (1,201 files / 168,497,477 bytes).
- **Old interrupted package boundary:** source/build head `db6c1f44dc8272509ccab51dc0cef75aa0abbe18`; `Mahjong.exe` 667,136 bytes, SHA-256 `F083B0E879A9298CDCF9933A8F3B4F2B013E13F7D3E51221FC0D756B79E28807`; package marker covers 205 files / 128,831,784 bytes, SHA-256 `D267C3CE29DF3A3082E30B45B8361CF547205D18FD2197F5D21DB8E06ABD08A8`. All seven old case processes and the last closed-binding raw verifier exited 0; the last raw log showed 6 frames and 67 negative checks. The old runner's final exit, P2, summary, seal, and package-after state remain unknown/not run. This package is not final-candidate evidence.
- **R6 acceptance:** final frozen source passed seven PlayMode tests and the complete ordinary Windows seven-case/P2 matrix as recorded below. The interruption audit repairs the missing recovery record; it does not convert the old run into PASS. N5/N8 remain `NOT_RUN` in the fixed denominator table, and FU-02 remains open, so overall D05 is not accepted.

#### FU-02 — MAJOR, LocalHost recovery first-failure causality remains unresolved

- **Location and reproduction:** read-only `dotnet/tests/Mahjong.LocalHost.Tests/LocalHostRecoveryTests.cs:72,147–158,794–813`; original full-suite invocation and XML are retained under `build/5c4608d017774b4798a9a54ea174850a/` and earlier report sections. Do not rerun the unchanged full suite merely to seek green.
- **Actual vs expected:** original canonical execution was 178/179, zero skips, with `version=1`, `discard=1`; isolated rerun passed 1/1 and controlled positive-order diagnostics observed later AI versions but did not reproduce the original order. No deterministic runtime capture identifies the decision/seat/epoch/phase at the accepted version-1 observation or proves the actual commit/durable boundary. The prior `StateVersion >= minimumVersion` / nonempty-decision observation remains a hypothesis, not a diagnosis.
- **R6 disposition and evidence:** no out-of-scope tests or production files were changed, and no no-change full-suite retry was performed. Retain the original failure and controlled diagnostics as separate evidence. This is the outstanding MAJOR finding; it is not hidden by the final PlayMode/Windows successes.
- **Required caller follow-up / acceptance:** produce a deterministic diagnostic-only reproduction of the original event order with the actual snapshot decision, seat, epoch, phase, release timing, commit, and durable state/event evidence. If correction requires an out-of-allowlist path, submit exact repro, path, and minimal patch proposal for a separate scope decision. Do not merely change `>=` to `>`, add sleep, or relax version/136-entity assertions. A later canonical 179/179 zero-skip result requires an authorized relevant fix or reviewer determination that the historical failure is unrelated.

#### FU-03 — R5 BLOCKER, released-control raw consumer and producer/consumer contract — repaired in R6

- **R5 defect and reproduction:** `Assert-C04ReleasedControlRaw` in `tools/unity/run-p45-c04-privacy-matrix.ps1` trusted SHA and JSON labels instead of decoding raw bytes, required one session for a valid release/claim timeline, and accepted rehashed malformed protobuf/WELCOME and a real foreign-owner frame. Reproduction is preserved at `build/3fa2e2ae5d3b4f79adeca25cbc76361a/repro-control-raw.ps1`; its fixture-only output/log is `control-raw-repro.log` (SHA-256 `6c17d2caf0a08414c8d97893baa89be4f3aad53c13f081384714ae6abc5c6f3`).
- **R6 repair:** `dotnet/tests/Mahjong.LocalHost.Tests/C04PrivacyStreamMatrixTests.cs` now decodes each actual player and WELCOME frame through production `WireCodec` and validates typed identity, role, seat, room, session, binding, scope, account, owner, and private content against the phase metadata. The validator accepts the legitimate two-session release/claim timeline and valid equal resumed/Ready binding while preserving ordered phase relationships. `C04PrivacyRunner.Tests.ps1` uses actual WireCodec protobuf positive fixtures. The accepted canonical four-owner verifier and its four-scene boundary were not changed.
- **Unified negative matrix:** tested against actual consumer: malformed player protobuf and malformed WELCOME with recomputed hashes; actual foreign-owner wire with valid hash; typed fields conflicting with outer metadata; invalid or unexpected session/scope/account/role/room/binding; omitted/conflicting phase; invalid frame hash; and control timeline binding/scope mutations. The real PlayMode, interrupted Windows, prior current Windows, and final Windows raw timelines were consumed, not represented as test-game fixtures. Current final Windows raw is 9 frames, SHA-256 `BC475D8E0F6F778B1B5B5B8E410E556040DE331A6AB3B7C4135574C524AFDA48`, with four negative checks; consumer native exit 0. Direct validation logs: `build/r6-real-win-wire-629e/wire-control.log`, `build/r6-real-playmode-wire-61ac/wire-control.log`, and `build/r6-interrupted-win-wire-4e26/wire-control.log`; the current final app evidence is under `build/77cc2a1e52224f1d9336a0987086d90b/.../wire-validation/`.
- **Cheap gates:** `C04PrivacyRunner.Tests.ps1` 33/33, native 0, `build/c22391dd976948ce918c661f4f0c52c8`; `C04PrivacyPlayer.Tests.ps1` 47/47, native 0, `build/ac715f39a1b6468a93dcb08c9ea4e56f`. The former includes valid real protobuf consumer evidence and malformed/foreign-owner rejection behavior. These are tool-contract evidence, not GAME evidence.
- **R6 acceptance:** actual final PlayMode and ordinary Windows producer raw passed the same typed consumer. Hash-only acceptance and metadata-only ownership remain rejected. FU-03's known defect is repaired within the allowlist.

#### FU-04 — MAJOR, path boundary

- **Known issue and reproduction:** prior runner-default fixture failed at the released-control nested output with Windows path-not-found; the failing default-root and successful short-GUID-root evidence are preserved in R3/R4 roots cited above. In R4, default runner behavior passed 27/27 and source checks passed 32/32; the exact final Windows filesystem case paths still had not been exercised then.
- **R6 repair/verification:** compact fixture paths and pre-write path budget checks remain in place; explicit-GUID runner behavior now passes 33/33. The final ordinary Windows package persisted and consumed deadline, reconnect, released-control, recovery, closed-binding and P2 evidence without a MAX_PATH/path exception. All seven scenario summaries and required artifacts are present under `build/77cc2a1e52224f1d9336a0987086d90b/windows-app-cases/`.
- **Unified negatives and acceptance:** over-budget roots fail before partial scenario writes; normal default and explicit roots pass. Final app outputs are in the package-outside evidence root, and the ordinary package before/after file manifest is identical. FU-04 is closed for this candidate.

#### FU-05 — MAJOR, control recovery semantics

- **R4 defect and reproduction:** `RunReleasedControlScenarioAsync` used cached pre-claim epoch as if it were the post-WELCOME authoritative epoch and demanded an unchanged active scope. The read-only production runtime issues a new scope in WELCOME while retaining the same authenticated session/ticket. The real component trace in `build/d663bf9d34924f1ea3419d69e6313915/control-epoch-recovery.log` (SHA-256 `d1831c4cc4c6822e1e2d1f4b6ad504a25218ada5ebb38a249198d85ed398133e`) showed formal recovery, binding `1→2`, authoritative epoch 2/human, and one `Rejected/DECISION_MISMATCH` stale command with no commit metadata or pending request.
- **R6 repair and acceptance:** control glue keeps cached epoch, claim-before epoch, and resumed authoritative epoch distinct; validates the actual WELCOME-issued scope/binding and preserved identity; retains original stale decision/action/epoch and submits it once through the real channel. The PlayMode and final Windows control scenarios passed, including cursor-floor-triggered formal reconnect, Ready, epoch/owner, precise stale rejection, zero command/event metadata, zero pending and no duplicate side effect. Final Windows raw also passed FU-03 typed verification. FU-05 is closed for this candidate; no production fix was needed.

### R6 inexpensive tests, exact application commands, and final evidence

The final code was clean at `3aec0c3a92bb87e954163ad1847c78dcb08eba3a`; PlayMode and Windows runs independently validated this same source head, the same 540-file source closure and same 366-file staged Unity input.

**Runner and player contracts:**

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/C04PrivacyRunner.Tests.ps1 -TestRunRoot build/c22391dd976948ce918c661f4f0c52c8
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/tests/C04PrivacyPlayer.Tests.ps1 -RunRoot build/ac715f39a1b6468a93dcb08c9ea4e56f
```

Results: runner 33/33, native 0; Player source contract 47/47, native 0. The final clean build's verifier self-test reported positive 17 / negative 67 / serialized frames 5. Argument rejection controls for missing required values, unknown case and duplicate enable each returned the expected native 71.

**Final PlayMode command:**

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-c04-privacy-matrix.ps1 -ExpectedSourceHead 3aec0c3a92bb87e954163ad1847c78dcb08eba3a -RunRoot build/5aeb60c9fc8f4b58aa840b44a02f4b98 -TimeoutSeconds 1200 -MutexTimeoutSeconds 600 -MutexHeartbeatSeconds 30 -ReuseLibraryCacheFrom build/0c4d544023434cc9aec54fbad8f369f6/staging-project/Library
```

Unity exited native 0; exact seven tests passed, zero failed/skipped. Summary SHA-256 `0a3ad1f18ea16244e031c0b9cc2697a0ef99ff1255619de20d0975efa715d901`; XML SHA-256 `04df164d69f3f96e03022b67f485d899f610d7d32dbb2bf942a644733d264ef9`; sealed manifest SHA-256 `5f5621bd6c426b45f60495f3bf4aa8f968f7a35b7b0edf94fc47f6f34add0e01` (2,708 files / 254,532,044 bytes).

| Exact PlayMode test | Result |
|---|---|
| `Mahjong.Unity.Tests.PlayMode.C04PrivacyRoleSwitchPlayModeTests.RealRoom_FourPlayersSpectatorAndRoleSwitchUseIndependentSessions` | PASS |
| `Mahjong.Unity.Tests.PlayMode.C04PrivacyRoleSwitchPlayModeTests.RealRoom_DelayedOldRawReceiveIsRejectedByNewBinding` | PASS |
| `Mahjong.Unity.Tests.PlayMode.C04PrivacyRoleSwitchPlayModeTests.RealRoom_ColdReopenReconcilesOriginalRequestWithoutDuplicate` | PASS |
| `Mahjong.Unity.Tests.PlayMode.C04PrivacyRoleSwitchPlayModeTests.RealRoom_LegalSleeveAndFeeOwnershipAcrossFourSeatsAndSpectator` | PASS |
| `Mahjong.Unity.Tests.PlayMode.C04PrivacyRoleSwitchPlayModeTests.RealRoom_ReleasedSeatAiAdvancesAfterPlayerCloseWithSpectatorOnly` | PASS |
| `Mahjong.Unity.Tests.PlayMode.C04PrivacyRoleSwitchPlayModeTests.RealRoom_OpenDecisionDeadlineContinuesAfterPlayerClose` | PASS |
| `Mahjong.Unity.Tests.PlayMode.C04PrivacyRoleSwitchPlayModeTests.RealRoom_ClosedPlayerToSpectatorToNewPlayerReceivePathRejectsOldRaw` | PASS |

**Final ordinary Windows one-package command:**

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/run-p45-c04-privacy-matrix.ps1 -WindowsAppMatrix -ExpectedSourceHead 3aec0c3a92bb87e954163ad1847c78dcb08eba3a -RunRoot build/77cc2a1e52224f1d9336a0987086d90b -TimeoutSeconds 1200 -MutexTimeoutSeconds 600 -MutexHeartbeatSeconds 30 -ReuseLibraryCacheFrom build/5aeb60c9fc8f4b58aa840b44a02f4b98/staging-project/Library
```

The shared mutex was acquired immediately. The ordinary Player build, all seven scenarios, the P2 prepare/restart/query/reconcile, the four accepted canonical raw verifiers, and the released-control WireCodec consumer exited native 0; matrix status is PASS. Summary SHA-256 `AE01845D9D244B26EEC840F064BDAFD8F6AFA341F8F9AC4819F10EB64562C8F2`; Windows matrix SHA-256 `E8571CECDD6047F652039458EB5A8080DA323C5B6CB7DA679C062237F0B78402`; stable manifest SHA-256 `527FF33EC7D8304DE702D41999366891F1F74D37B6004E82AA067658BA926034`; seal SHA-256 `DEE7346206E9B151BECD030741084D6194A6BD9CF3EBDBDC3D2B3F2BC988099B` (3,014 files / 329,747,361 bytes).

The same ordinary package was used for all seven cases and both P2 processes. `Mahjong.exe` SHA-256 is `F083B0E879A9298CDCF9933A8F3B4F2B013E13F7D3E51221FC0D756B79E28807` (667,136 bytes). Package before and after each contain 206 files / 128,855,873 bytes with identical canonical SHA-256 `e914b470d8977c9ebe10da08330091041ea3f6441fd8c1b3fafae088a6143b83`. The inner build marker covers 205 files and excludes its own manifest; its package SHA `17e4579ad530df6b4a6b60a0798bf361075b0a0d35fbf8071dbd3258e2e58bb3` is therefore not expected to equal the outer 206-file before/after manifest.

| Windows scenario | Native exit | Main real evidence |
|---|---:|---|
| `RealRoom_FourPlayersSpectatorAndRoleSwitchUseIndependentSessions` | 0 | Four-owner raw, private content, owner/audience/version checks |
| `RealRoom_DelayedOldRawReceiveIsRejectedByNewBinding` | 0 | Delayed old raw receive/binding fence |
| `RealRoom_ColdReopenReconcilesOriginalRequestWithoutDuplicate` | 0 | Recovery query and original request raw |
| `RealRoom_LegalSleeveAndFeeOwnershipAcrossFourSeatsAndSpectator` | 0 | Four-owner private and fee evidence |
| `RealRoom_ReleasedSeatAiAdvancesAfterPlayerCloseWithSpectatorOnly` | 0 | Release/control epochs, formal recovery, stale decision rejection, typed 9-frame raw |
| `RealRoom_OpenDecisionDeadlineContinuesAfterPlayerClose` | 0 | Structured deadline evidence |
| `RealRoom_ClosedPlayerToSpectatorToNewPlayerReceivePathRejectsOldRaw` | 0 | Closed-binding raw rejection |

Raw verifier counts and hashes:

| Capture | Frames | SHA-256 | Verifier |
|---|---:|---|---|
| role-switch | 7 | `6ed58036cf01e6f2d7c0af4c284bdad71e2f546d4fb0849ad2f5a2412c5ce36e` | native 0 |
| four-owner | 50 | `f218d8beffe7108be563cd17362ae6c0146172c50302696d901b710ae885d895` | native 0 |
| closed-binding | 6 | `7c0eb9db3f38d6fed2b86f85348d847b33688b88185641057f400159226871f2` | native 0 |
| recovery | 8 | `23aac1db552141e00c3449dd0d11ea444d6d69317212c0d31a455ad1f036366d` | native 0 |
| released-control typed wire | 9 | `BC475D8E0F6F778B1B5B5B8E410E556040DE331A6AB3B7C4135574C524AFDA48` | native 0; four negative mutations rejected |

Eight shared DLL identities were equal between the frozen PlayMode and ordinary Windows inputs and were recorded by `shared-plugin-provenance.json`:

| DLL | Bytes | SHA-256 |
|---|---:|---|
| `Google.Protobuf.dll` | 483,936 | `16921420D56EE464D4B4C43BA70C7300172E2CF3852F80CB91B71932569D3A5F` |
| `Mahjong.Ai.dll` | 128,512 | `F6B51DBDAB00CB75C8F7E5DBA8D1F7183F6AFDB565F2E0C86D642A2C3B9C1819` |
| `Mahjong.Client.dll` | 207,872 | `4DD6FDCCA535B52D1888C2A836C7C76489A22E04FB9813F0333658056F065625` |
| `Mahjong.Contracts.dll` | 120,320 | `B8CFEAF4B5B87DAF51BA7610EE0356E3D7E00C8BCC94E53950FE120CAF0C010C` |
| `Mahjong.Contracts.Wire.dll` | 390,144 | `C17AA6F21768D1869AAC00EA031302F870D14E4B5B7CC0C5EAEFDE2A85A1291D` |
| `Mahjong.Domain.dll` | 377,856 | `EA609697160FF579759F23820CE5456348EEC7CC7F0C30946C3610C1BAE33CEE` |
| `Mahjong.LocalHost.dll` | 311,296 | `3D62B2E6773BC1ADC368DD9171AFF069035432D3D2F061AE618F84FF0EBD0AC0` |
| `Mahjong.TableRuntime.dll` | 505,344 | `789D24E513A84AB6DA177E447093F7496E97CAA55B8E52C12DF0310971270A1B` |

P2 process-restart evidence uses the original request, not a replacement request. Prepare process PID 4056 began `2026-11-03T20:39:19.4089466Z`; recovery process PID 7464 began `2026-11-03T20:39:23.0701190Z`; both ran the same package path and executable SHA. Original request `c04-player-c04-d05-final-unified-p2-discard`, original scope `bd993e6faa9b481c869ce75dddd9d10b`, account `local-local-main-seat0`, canonical digest `4824549adf319a1f5f8868312fd0dafb0d6283e1c6dd1d91c7e4b46685556506`, payload SHA `0B9043274BE8E0E35332BF8A08BC78BD6CFA5FAD246320A7FB597A4F94FF3F07`, original result `APPLIED`, command version 1, event sequence 2. Prepare persisted one unresolved outbox entry. Recovery performed one old-scope query, found the existing receipt, sent zero original commands, reported zero duplicate commands, and produced zero delta on repeat reconcile. One discard effect remained: hand 14→13, river 0→1 with tile 32 present; 136 unique physical entities remained. Prepare SHA-256 `755054E4E3B1873901E907891843685A0F3A7D16825773BC8153CD4E5175D7EF`; result SHA-256 `4626C1EEEDD1F630D5147711D1DD150997CCCEA766BE8E7A76CEC1EEDEE4FA03`.

### Fixed denominator status at R6

This table reports evidence status, not approval of the wider C04/G2/G5 phase. N5 and N8 have no complete final-candidate case in the seven-case contract and remain NOT_RUN; FU-02 is separately unresolved.

| Row | R6 state | Evidence and boundary |
|---|---|---|
| N1 four seats / one actor | PASS | Final PlayMode and ordinary Windows four-player/spectator scenario; same state progression and independent seat sessions. |
| N2 spectator public stream | PASS | Four-owner and spectator real-channel captures; typed raw verification and public/private field boundary. |
| N3 protected raw values | PASS | Nonempty per-owner private/fee evidence, actual WireCodec decoding, foreign-owner and malformed-wire negatives. |
| N4 role switch / stale binding | PASS | Final PlayMode and Windows role-switch, delayed old raw, closed binding, and formal rebind cases. |
| N5 independent cursor / multiple viewer close | NOT_RUN | The fixed N5 contract includes four players plus two spectators and independent cursor/close combinations; those exact combinations are not all represented by the seven final cases. |
| N6 AI/control/deadline | PASS | Final release/AI advancement, control recovery, epoch/stale decision rejection, and deadline continuity in PlayMode and Windows. |
| N7 recovery/idempotency | PASS | Final Windows P2 original-request outbox lookup and reconcile across two processes, zero duplicate command/effect. This does not claim all N7 scope beyond the recorded original-request scenario. |
| N8 lease/actor negative | NOT_RUN | Same-directory lease conflict and separate-directory concurrent actor contract were not run in the final seven-case package. |
| U1 room composition | PASS | Exact PlayMode four-player/spectator real-room test. |
| U2 privacy role switch | PASS | Exact PlayMode role switch, stale frame and cache/binding tests. |
| U3 control/AI/close | PASS | Exact PlayMode release, AI, spectator close and deadline tests. |
| P1 ordinary Windows app | PASS | One ordinary immutable package, seven scenarios, native exits 0, package before/after equality. |
| P2 process restart | PASS | Original request/scope/canonical outbox query and receipt, two distinct processes, exactly-once result. |
| C04-G5-01 four owners | PASS as D05 evidence | Real four-owner PlayMode/Windows capture and typed field checks; no broader G5 phase approval implied. |
| C04-G5-02 spectator public privacy | PASS as D05 evidence | Real spectator/public raw and foreign-owner/private field negatives; no broader G5 phase approval implied. |
| C04-G5-03 role switch/stale binding | PASS as D05 evidence | Real receive fencing and old-raw rejection; no broader G5 phase approval implied. |

### Final R6 defect disposition and remaining negatives

| Finding | R6 disposition |
|---|---|
| FU-01 BLOCKER | Final PlayMode, ordinary Windows one-package matrix, P2, and interrupted-run recovery audit now have sealed evidence. The original interrupted run remains `UNKNOWN`, not PASS. Fixed N5/N8 cases remain `NOT_RUN`; overall task is not accepted. |
| FU-02 MAJOR | OPEN. Historical LocalHost 178/179 causality still lacks deterministic runtime evidence; out-of-scope test/production paths remain untouched. |
| FU-03 R5 BLOCKER | Repaired. Typed WireCodec consumer accepts authentic multi-session/binding timeline and rejects malformed protobuf/WELCOME, rehashed malformed bytes, foreign owner, metadata conflicts, wrong identity/scope/binding, missing phases, and hashes. |
| FU-04 MAJOR | Repaired for supported fixture roots and final Windows outputs; no machine long-path setting change. |
| FU-05 MAJOR | Repaired for final PlayMode and Windows control recovery; authoritative epoch, valid scope rotation, original stale command, exact rejection and zero side effects verified. |

The unified raw negative matrix retains: malformed player or WELCOME bytes after recomputing hash; authentic foreign-owner frame with valid hash; typed-field/outer-metadata disagreement; unauthorized session, account, room, role, seat, scope or binding; missing/conflicting phase; bad hash; stale decision/action/epoch mutation; wrong receipt reasons/status; stale commit metadata; duplicate command; nonzero pending; and invalid control epoch/owner/floor. P2 consumer negatives retain wrong case/request/scope, zero unresolved outbox, duplicate effect, missing state advancement, altered canonical/payload digest, and a 135-entity result. These are executable consumer checks, not hand-written PASS fields.

### Historical failures kept distinct

1. Earlier R5 full Windows run: mutex timeout, no Unity start.
2. R5 watchdog-interrupted old package: recovered audit above; original runner exit unknown and later phases unknown.
3. R6 raw consumer run `build/86f1f559692e43d6bbfd11727b5e62b7`: valid equal resumed/Ready binding was initially rejected; fixed and revalidated through the final run.
4. R6 Windows run `build/0c4d544023434cc9aec54fbad8f369f6`: seven cases and raw verification completed, but P2 consumer rejected valid producer status `APPLIED` because of case-sensitive casing; fix was behavior-tested and the fresh final package was rerun.
5. Final Windows package at `build/77cc2a1e52224f1d9336a0987086d90b` and final PlayMode at `build/5aeb60c9fc8f4b58aa840b44a02f4b98` are the only R6 final candidate results. Older package P2 and fixtures are not substituted.

### R6 closure and handoff

The R6 report is a docs-only change, separate from the frozen code head above. Before the report commit, the working tree contains only the allowed delivery report; `git diff --check 603005a4cd87e889b3b13264df8c70660a54ae47..HEAD` and allowlist review must pass. The final report commit hash is recorded after commit. No other tracked file is authorized by this report. Dedicated reviewer/caller retains authority to accept or reject D05. No push, PR operation, approval, merge, deployment, or out-of-scope change was performed.
