# Round 2 authoritative status (2026-11-03)

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
