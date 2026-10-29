# V5 LocalHost Caller Run Investigation — 113/115 at PR20 `00cd02d`

Date: 2026-10-30  
Worktree: `D:/majiang-wt/localhost-scope-race-20261030`  
Branch: `wt/v5-localhost-scope-race-20261030`  
Accepted base: `origin/v5` = `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`  
Unaccepted PR20 head: `00cd02d80172ebd13a2945e4e8bacfe486ae0c21`  
Status: **investigation complete; report only; PR20 remains unaccepted.**

## 0. Verdict

The caller's `113/115` full `dotnet test` run is **not** evidence of a production scope-persistence or
furiten defect, and it is **not** caused by the approved separate test patch `fdb005b`.

| Failure | Root cause | Evidence status |
|---|---|---|
| `ClientPassFuritenBlocksSecondRealRonUntilTheNextDraw` (`furiten-read worker exit=3`) | Test helper launches `CrashWorker.dll` with `Environment.ProcessPath`. Under `dotnet test`/MTP that is the xUnit test apphost (`Mahjong.LocalHost.Tests.exe`), which rejects the DLL path as an unknown option and exits 3. The worker is never run. | **PROVEN / REPRODUCED**, deterministic under `dotnet test` |
| `ExpiredOrRevokedScopeCannotPersistRejectionOnEitherCommandPath` (`Expected: 1 / Actual: 2`, line 1839) | The four-cell test releases a valid, parked AI follow-up **before** reading the durable state row and asserting it did not move. The released AI commits one state version (caller saw `1 -> 2`); the assertion races that autonomous commit. The unauthorized scope probe itself is transport-only and does not move state. | **MECHANISM PROVEN** by controlled probes; **caller's exact natural schedule NOT_REPRODUCED**; **production defect NOT_PROVEN** |

Neither failure is labelled “flaky”. Failure 1 is deterministic under the caller's runner. Failure 2 is
an ordering defect in the test: it fails whenever the released AI commit wins the race to the state
read, and passes when the assertion read wins. The failure was reproduced exactly (same cell, same
assertion, same `Expected: 1 / Actual: 2`) by a test-only 50 ms diagnostic delay after the existing
release point.

## 1. Scope and constraints

- Investigated only the caller's two failures recorded in the approval of
  `thread_20261030_061247_f720`.
- Inspected exact PR20 `00cd02d` only through `git archive` into ignored `build/`.
- Made no production or tracked test change. All probes are copies under
  `build/localhost-scope-race-20261030/` (ignored by `.gitignore`).
- No push, PR/MR, branch rewrite, CI rerun, trust/Unity/CI-workflow change, or other-worktree
  mutation. No gate approval. No claim that PR20 or G2–G5 is accepted.
- Base `8fd7c6c` does not contain either PR20 test file
  (`LocalControlTests.cs`, `LocalHostCandidateAcceptanceTests.cs`), so no base-vs-PR20 test
  comparison is possible; base was used only as the accepted commit identity.

## 2. Exact inputs and integrity

| Item | Value |
|---|---|
| Candidate commit | `00cd02d80172ebd13a2945e4e8bacfe486ae0c21` |
| Candidate tree | `72502a19a808738f9e2c801f83e47d46dc07761d` |
| Candidate parent | `2bb6e681c26060407542f09bc2495c4b30b2c7c0` |
| Archive | `build/localhost-scope-race-20261030/pr20-00cd02d.tar` |
| Archive SHA-256 | `3BC11229345DA1C4F61232ACBFEBBDE24A8EC148A652E9F994EA3A60A90E7C6D` |
| `LocalControlTests.cs` blob | `761a51ed6faf4f80a9592136bcedd447ed495cc2` |
| `LocalHostCandidateAcceptanceTests.cs` blob | `d5486eaebb9a6db096130f8ae05ddc08adb3d8e5` |
| Candidate acceptance file SHA-256 (archive, CRLF) | `4DFBE23FE73BE89A3022164458AD3C71C20B6B5EA678E3422963739CADC91B4B` |
| fdb005b patch | `doc/v5-重构/patches/V5-LOCALHOST-FURITEN-RACE-REMEDY-TEST-FIX-20261030.patch` |
| Patch SHA-256 | `DED09A4337CEB04E15B7642255EDB6C66A987E61997A00F9E382AB3A62623B7D` |
| Patch postimage blob / SHA-256 | `0bc67c8558f469b0792bf9f07dbd7dc5e28862f1` / `448EE73F5702232447BFF6471EFAEE7C2D5D2B0786822749EDD63916EBF52BE4` |
| Patch scope | `28 insertions(+), 8 deletions(-)`, only `LocalHostCandidateAcceptanceTests.cs` |

The patch applied with `git apply --check` exit 0 to the LF-normalized candidate file and produced
exactly the postimage blob/hash above. `git apply --stat` shows one test file only.

## 3. Caller evidence preserved

The caller's actual output is available and was copied into ignored evidence; it was not reconstructed
from memory:

| Evidence | Original path | Preserved path | SHA-256 |
|---|---|---|---|
| Caller reviewer/wake stream (includes the full `dotnet test` stdout) | `C:/Users/sncdc/.agent-mesh-bridge/logs/thread_20261030_061247_f720_round_1.reviewer.log` | `evidence/caller-thread_f720_round1.reviewer.log` | `B0793B09E72EF749E541135FE31FE7EEF60AA977304D2264C4938B5D717F03C4` |
| Caller TestResults log | `D:/majiang-wt/furiten-fix-20261030b/build/furiten-test-fix-20261030b/fix/dotnet/tests/Mahjong.LocalHost.Tests/bin/Release/net8.0/TestResults/Mahjong.LocalHost.Tests_net8.0_x64.log` | `evidence/Mahjong.LocalHost.Tests_net8.0_x64.log` | `FC45530C7822D1FCF1A4E9900EB98514A39C9F976F4B9173E44AA65C1D4C65EC` |

Caller command, from the wake stream:

```powershell
# cwd: D:\majiang-wt\furiten-fix-20261030b\build\furiten-test-fix-20261030b\fix
dotnet test dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj -c Release --no-restore
```

Caller result: **exit 1**, `总计: 115`, `失败: 2`, `成功: 113`, `已跳过: 0`, duration
`32s 938ms`. The runner was xUnit.net v3 MTP v2 (`4.0.2+0c7aa4991f`, 64-bit .NET 8.0.32). The
runner command line in the TestResults log was:

```text
...\Mahjong.LocalHost.Tests.exe --internal-msbuild-node testingplatform.pipe.200c71dc3c424cacac8bb9c566fa861b
```

Caller failure text (verbatim shape):

```text
失败 LocalControlTests.ExpiredOrRevokedScopeCannotPersistRejectionOnEitherCommandPath (4s 805ms)
  Assert.Equal() Failure: Values differ
  Expected: 1
  Actual:   2
    ... LocalControlTests.cs:1839 (ExpiredOrRevokedScopeCellAsync)
    ... LocalControlTests.cs:1728 (fourth cell: normal/revoked)

失败 LocalHostCandidateAcceptanceTests.ClientPassFuritenBlocksSecondRealRonUntilTheNextDraw (738ms)
  furiten-read worker exit=3 stderr=
    ... LocalHostCandidateAcceptanceTests.cs:1253
```

The caller's tree was candidate `00cd02d` + fdb005b (`fix`), but the two failing test files are
outside the patch: fdb005b changes only `ClientPassOnRonSetsFuritenAndColdRestartKeepsItUntilTheNextDraw`
in `LocalHostCandidateAcceptanceTests.cs`, not `LocalControlTests.cs` and not the second-ron test.

## 4. Environment and runner topology

| Item | Value |
|---|---|
| OS | Windows 10.0.19045 |
| SDK | .NET SDK `8.0.426`, host `8.0.32` |
| Runner | xUnit.net v3 `4.0.2`, Microsoft.Testing.Platform v2 |
| CPU | 16 logical processors |
| Direct runner form | `dotnet ...\Mahjong.LocalHost.Tests.dll -noLogo -reporter default` |
| Caller form | `dotnet test ... -c Release` (MTP launches the test apphost) |

`P41Harness` and all LocalHost tests use unique GUID save roots under `build/p41-tests`, so cross-test
file collision is not the cause.

`LocalControlTests` has no `[Collection("LocalHostSerial")]`; `LocalHostCandidateAcceptanceTests`,
`LocalHostDecisionTests`, and `LocalMatchTests` do. With xUnit v3's default parallel collections,
`LocalControlTests` can run concurrently with the serial collection. This is a plausible scheduling
amplifier for the failure-2 race, but it is not required for the defect and was not proven to be the
caller's trigger.

## 5. Reproduction matrix

All builds are isolated, fresh, Release, no shared `bin/obj` between trees. “Pristine” means exact
`00cd02d`; “patched” means `00cd02d` + fdb005b.

| # | Tree / runner | Scope | Native exit | Counts | Result |
|---|---|---|---:|---|---|
| R1 | pristine, direct | focused `ExpiredOrRevoked...` x30 | 0 each | 1 / 0 / 0 each | **30/30 PASS** |
| R2 | pristine, direct | full LocalHost | 0 | 115 / 0 / 0 | **PASS 115/115**, 25.896 s |
| R3 | pristine, `dotnet test` | full LocalHost x5 | 1 each | 115 / 1 / 0 each | **5/5 fail**, only `ClientPassFuriten...` |
| R4 | patched, direct | focused fdb005b target | 0 | 1 / 0 / 0 | **PASS 1/1** |
| R5 | patched, direct | full LocalHost | 0 | 115 / 0 / 0 | **PASS 115/115**, 26.023 s |
| R6 | patched, `dotnet test` | full LocalHost | 1 | 115 / 1 / 0 | **fail**, only `ClientPassFuriten...` |
| R7 | pristine + worker-host probe, `dotnet test` | full LocalHost x4 | 0,0,0,1 | 115 / 0 / 0 (x3); 115 / 1 / 0 (x1) | worker failure gone; 4th run failed a **different** pristine test (see §9) |
| R8 | patched + worker-host + hold probes, `dotnet test` | full LocalHost x3 | 0 each | 115 / 0 / 0 each | **3/3 PASS 115/115** |
| R9 | pristine + 50 ms delay probe, direct | focused `ExpiredOrRevoked...` x2 | 1 each | 1 / 1 / 0 each | **2/2 fail**, `Expected: 1 / Actual: 2`, same cell/assertion |
| R10 | pristine + 500 ms delay probe, direct | focused `ExpiredOrRevoked...` x1 | 1 | 1 / 1 / 0 | **fail**, `Expected: 1 / Actual: 4`, same cell/assertion |
| R11 | pristine + hold-through-assert probe, direct | focused `ExpiredOrRevoked...` x10 | 0 each | 1 / 0 / 0 each | **10/10 PASS** |
| R12 | manual apphost invocation | `Mahjong.LocalHost.Tests.exe <CrashWorker.dll> ... furiten-read ...` | 3 | n/a | stdout `error: unknown option: ...\Mahjong.LocalHost.CrashWorker.dll`, stderr empty |

Key logs and hashes are in §12. R3/R6 show the worker failure is runner-dependent, not patch-dependent.
R7/R8 show the worker fix removes it from the full `dotnet test` path without skipping or relaxing the
worker assertion.

## 6. Failure 1 root cause: worker host is the test apphost under `dotnet test`

### 6.1 Code path

Candidate `LocalHostCandidateAcceptanceTests.cs:1204-1215` (pristine numbering):

```csharp
var startInfo = new System.Diagnostics.ProcessStartInfo
{
    FileName = Environment.ProcessPath ?? "dotnet",
    Arguments = "\"" + workerDll + "\" \"" + harness.Options.SaveDirectory + "\" \"" +
                P41Harness.RoomId + "\" \"" + marker + "\" furiten-read " + ...
```

Under `dotnet test`, MTP starts the test assembly as an apphost process. `Environment.ProcessPath` is
therefore `...\Mahjong.LocalHost.Tests.exe`, **not** `dotnet.exe`. The child command becomes:

```text
Mahjong.LocalHost.Tests.exe "...\Mahjong.LocalHost.CrashWorker.dll" "<saveDir>" "room-1" "<marker>" furiten-read <clock...>
```

xUnit treats the first token as an unknown option and exits 3. The worker DLL never opens.

### 6.2 Direct observations

Manual invocation of the apphost with the worker's argument vector (same directory, `DOTNET_ROOT`
set so the apphost can start):

```text
exit=3
--- stdout ---
error: unknown option: D:\majiang-wt\localhost-scope-race-20261030\build\localhost-scope-race-20261030\pristine\dotnet\tests\Mahjong.LocalHost.CrashWorker\bin\Release\net8.0\Mahjong.LocalHost.CrashWorker.dll
--- stderr ---
(empty)
```

This exactly matches the failing assertion's `furiten-read worker exit=3 stderr=`.

Under `dotnet test`, the probe log records:

```text
ProcessPath=...\probe-fix-worker\dotnet\tests\Mahjong.LocalHost.Tests\bin\Release\net8.0\Mahjong.LocalHost.Tests.exe
workerHost=dotnet
```

Evidence: `evidence/apphost-worker-stdout.txt`,
`evidence/apphost-worker-stderr.txt`, `evidence/process-path.log`.

### 6.3 Why it passes in direct runs

Direct invocation is `dotnet <tests>.dll`; `Environment.ProcessPath` is then
`...\dotnet.exe`, which can execute `CrashWorker.dll` correctly. This explains the prior
direct-run `115/115` and the caller's `dotnet test` `exit=3` without invoking “flaky”.

### 6.4 Existing correct pattern in the same test project

`LocalHostRecoveryTests.cs:1149-1211` already contains the correct pattern:

- `CreateWorkerStartInfo(...)` resolves the host through `ResolveDotnetPath()`;
- `ResolveDotnetPath()` reuses `Environment.ProcessPath` only when its basename is `dotnet`;
- otherwise it falls back to `DOTNET_ROOT`, then `dotnet`;
- it also forwards `DOTNET_ROOT` to the child process.

The futiren worker test duplicates a simpler, incorrect launch instead of using this helper.

### 6.5 Status

**PROVEN and REPRODUCED.** This is a test-harness defect, not a production `CrashWorker`, LocalHost,
furiten, or scope defect. It is deterministic under the caller's `dotnet test` command and absent under
the direct runner. It is independent of fdb005b: pristine R3 and patched R6 both fail the same test.

## 7. Failure 2 root cause: released valid AI commit races the state-stillness assertion

### 7.1 Exact assertion

Candidate `LocalControlTests.cs`:

- `ExpiredOrRevokedScopeCannotPersistRejectionOnEitherCommandPath` starts at `:1719`;
- four cells are invoked at `:1725-1728`; the failing caller frame is the last cell,
  `control:false, revoke:true` (`:1728`);
- the frozen snapshot is taken at `:1764`, the scope is revoked at `:1766`;
- the AI hold is released in `finally` at `:1789-1793`, **before** the unauthorized probe at
  `:1795-1824`;
- the state assertion is `:1839`:
  `Assert.Equal(frozen.StateVersion.Value, after.StateVersion.Value)`.

The held AI is a real, current follow-up: `LocalTableDriver.CompleteAiDecisionAsync` executes the
test hook at `:446-449`, reads the **current** pending decision at `:451-452`, and submits the chosen
action at `:465`. `TableActor` accepts it when the decision is open and not past its deadline
(`:2416-2448`). Therefore release is a valid autonomous commit, not a stale submission.

The same test file already documents this exact semantics in
`HeldAiFollowUpIsNotDeclaredSettledAndItsLateCommitLandsAfterRelease` (`:2305-2394`), whose comments
state that releasing the hold “deterministically lands the deferred autonomous commits” and whose
assertion requires the state version to advance. The four-cell test freezes `frozen` while the AI is
parked, releases it, and then asserts the state did not move. Those two tests encode opposite
expectations for the same held-AI release.

### 7.2 Controlled probes

All probes differ from the pristine test only in ignored copies; no tracked test was changed.

1. **50 ms after release, target cell only** (`probe-delay`): the released AI commits before the
   probe reads the row. Result 2/2:

   ```text
   Assert.Equal() Failure: Values differ
   Expected: 1
   Actual:   2
   ... LocalControlTests.cs(1847)   # original 1839 + 8 inserted probe lines
   ... LocalControlTests.cs(1728)   # normal/revoked cell
   ```

   This is the caller's exact cell, assertion, and expected/actual pair.

2. **500 ms after release**: result `Expected: 1 / Actual: 4`, confirming multiple autonomous
   commits (held AI plus follow-ups), not a single one-off.

3. **Keep the hold attached through the probe and the state assertion, release afterwards**
   (`probe-hold`): **10/10 PASS**. The unauthorized probe is rejected transport-only, no durable
   receipt/digest/control result appears, and the state row stays at `frozen`. This isolates the
   probe path as clean and the released AI as the only state mover.

4. **Combined test-only probes** (worker host + hold-through-assert) on top of fdb005b:
   **3/3 full `dotnet test` runs = 115/115, 0 failed, 0 skipped, exit 0**.

### 7.3 Why the caller saw it and our direct runs did not

Focused direct runs of this test are 30/30 PASS. Six pristine full `dotnet test` runs (the five-run
matrix plus one exploratory invocation whose `--filter` was ignored and which therefore executed the
full suite) and one patched full `dotnet test` run did not hit the natural race; the caller's full
`dotnet test` run did. The exact scheduler interleaving
is not captured in any artifact. Plausible amplifiers, not proven causes:

- `LocalControlTests` is outside the `LocalHostSerial` collection and can run concurrently with the
  three serial heavy classes;
- the caller ran the full suite at `32.9 s` (our runs: `25.4–25.7 s`), consistent with a slower or
  more contended moment;
- the same batch's B1 task was still running, which may have added CPU/thread-pool contention.

None of these is required: the test's release-before-read order is sufficient to make the assertion
schedule-dependent. A 50 ms delay is enough to reproduce it deterministically.

### 7.4 Status

- **Test-ordering defect: PROVEN.** The released valid AI commit moves `StateVersion` from 1 to 2
  (and onward), exactly the caller's `Expected: 1 / Actual: 2`.
- **Unauthorized-scope persistence defect: NOT_PROVEN.** Under the hold the probe leaves no durable
  receipt/digest/control result and no state movement (10/10).
- **Caller's exact natural interleaving: NOT_REPRODUCED** (no scheduler trace); the failing
  assertion itself is reproduced exactly by the controlled delay probe.

## 8. fdb005b is not the cause

| Check | Result |
|---|---|
| Patch scope | one test file, `LocalHostCandidateAcceptanceTests.cs`, `+28/-8` |
| Patch target | `ClientPassOnRonSetsFuritenAndColdRestartKeepsItUntilTheNextDraw` only |
| `LocalControlTests.cs` touched? | No |
| Second-ron test touched? | No |
| Pristine full `dotnet test` | 5/5 fail `ClientPassFuriten...` |
| Patched full `dotnet test` | fails `ClientPassFuriten...` (114/115) |
| Patched direct full | 115/115 PASS |
| Patched + both test-only probe fixes, `dotnet test` | 3/3 115/115 PASS |

The patch changes the other furiten test's driver lifecycle; it neither causes nor fixes either
reported failure. It was accepted in the separate furiten-fix task scope, but it is not part of PR20
acceptance and is not re-reviewed here.

## 9. Bounded fix recommendation (test-only; not applied here)

### 9.1 Failure 1 — resolve the real dotnet host

In `LocalHostCandidateAcceptanceTests.ClientPassFuritenBlocksSecondRealRonUntilTheNextDraw`:

- do **not** use `Environment.ProcessPath` unconditionally;
- reuse `LocalHostRecoveryTests.ResolveDotnetPath()` / `CreateWorkerStartInfo(...)` (or extract an
  equivalent internal helper into `P41Harness`);
- reuse the process path only when its basename is `dotnet`;
- otherwise resolve `DOTNET_HOST_PATH` / `DOTNET_ROOT` / `dotnet` from PATH and forward
  `DOTNET_ROOT` to the child;
- keep the worker DLL, argument vector, 30 s timeout, exit-code assertion, marker parsing, and
  furiten assertions unchanged.

Do not “fix” this by skipping the worker, accepting exit 3, or deleting the cross-process check.

Validated by probe: worker-host fix alone gives 3 consecutive 115/115 `dotnet test` runs; the fourth
run failed a different pristine test (see §10). The combined fix tree gives 3/3 115/115.

### 9.2 Failure 2 — observe state before releasing the AI, then release

In `ExpiredOrRevokedScopeCellAsync`:

- keep `hold` attached through the unauthorized probe, the durable-record assertions, the state-row
  assertions, and the receipt lookup;
- release/detach in a `finally` **after** those assertions;
- after release, use the existing `WaitUntilFollowUpsSettleAsync(...)` milestone before harness
  disposal (as `HeldAiFollowUpIsNotDeclaredSettledAndItsLateCommitLandsAfterRelease` already does).

This preserves every existing assertion and the intent “revocation/probe must not move state”.
It adds no retry, no sleep-based green, no assertion weakening, and no production change.

Do not fix this only by adding a delay, by removing the state-stillness assertion, or by adding
`[Collection("LocalHostSerial")]` alone (the latter may reduce the window but does not remove the
release-before-read ordering).

Validated by probes: hold-through-assert 10/10 PASS; 50 ms release-first 2/2 FAIL with the caller's
exact values; combined fix 3/3 full-suite PASS.

### 9.3 Suggested ownership

Both are `Mahjong.LocalHost.Tests` test-harness fixes, not production or gate changes. They should be
reviewed and committed separately from production code and from PR20 acceptance.

## 10. Unresolved observations and limits

1. **Caller's exact natural interleaving is not captured.** No scheduler trace, thread-pool state, or
   per-test timing exists for the caller's `113/115` run. The report proves the mechanism and exact
   assertion, not the precise instant.
2. **Failure 2 did not fail naturally in our runs.** 30 focused direct runs and 6 full `dotnet test`
   runs (5 pristine, 1 patched) passed this test; only the caller's run and the controlled 50/500 ms
   release-first probes failed it. It is therefore not labelled flaky; it is an ordering hazard.
3. **`LocalControlTests` is outside `LocalHostSerial`.** This may amplify the race under the default
   parallel scheduler, but changing collection membership alone is not a complete fix and was not
   proven to be the caller's trigger.
4. **Pristine worker-fix probe, run 4:** full suite failed a different test,
   `ClientPassOnRonSetsFuritenAndColdRestartKeepsItUntilTheNextDraw`, at candidate line 1002
   (`Expected: Applied / Actual: Rejected`). This is the separate cold-restart/driver race that
   fdb005b targets; it is outside the two reported failures and was not investigated further.
   The caller's patched tree already contains that remedy, so it is not the reported `113/115` cause.
5. **No CI rerun, remote run, PR update, Unity/IL2CPP/Android/PG/WSS test, or gate review.** No
   conclusion is drawn for those environments.
6. **No production defect is asserted.** The scope rejection path was observed clean under the AI
   hold; the failed state-stillness assertion is a test baseline/release-order defect.
7. **No failure mechanism is BLOCKED.** Both failure mechanisms are reproduced or proven. The only
   BLOCKED item is the caller's exact natural scheduler interleaving, for which no trace exists; it is
   reported as `NOT_REPRODUCED`, not as a production defect or as “flaky”.

## 11. Status table

| Item | Status | Basis |
|---|---|---|
| Caller logs located and preserved | **DONE** | reviewer stream + TestResults copied, SHA-256 recorded |
| Exact `00cd02d` inspected | **DONE** | `git archive`, tree `72502a1...` |
| fdb005b patch applicability/integrity | **VERIFIED** | `git apply --check` 0; postimage `0bc67c8...` |
| Failure 1 (worker exit 3) root cause | **PROVEN / REPRODUCED** | `dotnet test` 5/5 pristine, 1/1 patched; apphost exit 3; host fix 115/115 |
| Failure 1 production involvement | **NONE FOUND** | worker never launched; test-host dispatch only |
| Failure 2 mechanism | **PROVEN** | 50 ms release-first probe exact `1/2`; 500 ms `1/4`; hold probe 10/10 |
| Failure 2 natural caller schedule | **NOT_REPRODUCED** | no scheduler trace; controlled probe only |
| Failure 2 production scope defect | **NOT_PROVEN** | hold probe leaves store/state untouched |
| fdb005b causal role in either failure | **NONE** | pristine and patched fail identically under `dotnet test` |
| PR20 acceptance / G2–G5 | **UNCHANGED / NOT APPROVED** | this report does not review or accept PR20 |
| CI / remote / Unity / Android / PG / WSS | **NOT_RUN** | outside scope |

## 12. Evidence index (ignored `build/`)

Root: `build/localhost-scope-race-20261030/`  
Manifest: `evidence/SHA256SUMS.txt` (89 files), SHA-256
`1F2960190A505C6847333A9EE5C87D7E3434841C2D199C224022C7879293DD90`.

| Evidence | Path |
|---|---|
| Candidate archive | `pr20-00cd02d.tar` |
| Pristine exact tree | `pristine/` |
| Patched exact tree | `patched/` |
| Build logs | `build-pristine.log`, `build-patched.log`, `build-probe-*.log` |
| Caller preserved stream / TestResults | `evidence/caller-thread_f720_round1.reviewer.log`, `evidence/Mahjong.LocalHost.Tests_net8.0_x64.log` |
| Apphost failure streams | `evidence/apphost-worker-stdout.txt`, `evidence/apphost-worker-stderr.txt` |
| Runner ProcessPath log | `evidence/process-path.log` |
| 30 focused control runs | `runs-control-direct/` |
| 5 pristine `dotnet test` full runs | `runs-full-dotnettest/` |
| Patched direct / `dotnet test` | `runs-patched-direct/`, `runs-patched-dotnettest/` |
| Pristine direct full run | `runs-pristine-direct-full/` |
| 50 ms / 500 ms release-first probes | `probe-delay-50-run1.log`, `probe-delay-50-run2.log`, `probe-delay-run1.log` |
| Hold-through-assert probe | `probe-hold/`, `runs-probe-hold/` |
| Worker-host probe | `probe-fix-worker/`, `runs-probe-fix-worker/` |
| Combined-fix probe | `probe-both-fixes/`, `runs-probe-both-fixes/` |

Key log hashes:

| File | SHA-256 |
|---|---|
| `probe-delay-50-run1.log` | `4E55F01BEEDBADEB4E5498E2F1522508B28FD47064258E42F55ACB4E0352B128` |
| `probe-delay-50-run2.log` | `FE3D6822F604D8710E0327675BB1CF0962FC63D748CD577EAC7DAB6E5889615F` |
| `probe-delay-run1.log` (500 ms) | `0D4BCFD75993402D17229DC4F602FE423F6E6468F8D1E1BDB5630B017A1ACAA8` |
| `evidence/apphost-worker-stdout.txt` | `D2F0FECDF3B729F6F6EC31CE826295FCB3C4B148DC58B35E29E3FC83FBD9C862` |
| `evidence/apphost-worker-stderr.txt` | `E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855` |
| `evidence/process-path.log` | `4D2F7156D9AA6B5CAF97A0926D78AE7A58541A1BE36C4C9D1D2C29F6EC182A7D` |
| `runs-control-direct/results.txt` | `E8A9168C2FF129AEB26AD5BC009AB72D5A9C96B6E17F5F421CFA340F5093090D` |

## 13. Reproduction commands

All commands are from the isolated tree; `build/` is ignored.

```powershell
# exact archive
git archive --format=tar -o build/localhost-scope-race-20261030/pr20-00cd02d.tar 00cd02d
tar -xf build/localhost-scope-race-20261030/pr20-00cd02d.tar -C build/localhost-scope-race-20261030/pristine

# fresh Release build
dotnet build dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj -c Release --nologo

# direct focused control test
dotnet dotnet/tests/Mahjong.LocalHost.Tests/bin/Release/net8.0/Mahjong.LocalHost.Tests.dll `
  -noLogo -reporter default `
  -method 'Mahjong.LocalHost.Tests.LocalControlTests.ExpiredOrRevokedScopeCannotPersistRejectionOnEitherCommandPath'

# caller-equivalent full run (reproduces the worker exit=3)
dotnet test dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj -c Release --no-build

# apphost-worker negative probe
$env:DOTNET_ROOT = 'D:\Unity\dotnet'
& .\dotnet\tests\Mahjong.LocalHost.Tests\bin\Release\net8.0\Mahjong.LocalHost.Tests.exe `
  .\dotnet\tests\Mahjong.LocalHost.CrashWorker\bin\Release\net8.0\Mahjong.LocalHost.CrashWorker.dll `
  <saveDir> room-1 <marker> furiten-read 1000 1000
# expected: exit 3, stdout "error: unknown option: ...\Mahjong.LocalHost.CrashWorker.dll", stderr empty
```

The 50 ms / hold-through probes are diagnostic copies under `probe-delay/` and `probe-hold/`; they are
not proposed as the fix. Their commands use the same focused method and direct runner.

## 14. Delivery

This commit contains **only this report**. No production or test source, verifier, solution, Unity,
CI workflow, trust, or other-worktree change is included. No PR/push/CI rerun. PR20 and G2–G5 remain
unaccepted; the caller reviews independently.
