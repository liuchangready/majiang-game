# V5 LocalHost Caller 113/115 — Two Test-Only Fixes (2026-10-30)

Status: **candidate test-only patch plus reproducible evidence for independent caller review. This is
not an acceptance, not a PR20 PASS, and not a G2–G5 approval. PR20 remains unaccepted; V4 remains the
only production baseline.**

- Accepted base: `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7` (tree
  `61f50856aedd78e0f8609e282baa8313ae8f2a14`; this worktree's `HEAD`).
- Unaccepted PR20 head: `0e5d77a9f8f4052e6b98c222da7ba94229e5ffe1` (tree
  `d38192d46525a610c043ff57c6e61dac93d3ca73`).
- Frozen candidate inspected: `00cd02d80172ebd13a2945e4e8bacfe486ae0c21` (tree
  `72502a19a808738f9e2c801f83e47d46dc07761d`; parent
  `2bb6e681c26060407542f09bc2495c4b30b2c7c0`).
- `0e5d77a` differs from `00cd02d` only by five added `doc/v5-重构/` files. The three test files
  touched by this patch are byte-identical in both commits (blobs below).
- Scope: `Mahjong.LocalHost.Tests` only. No production, solution, verifier, Unity, trust, CI, PR,
  push, or other-worktree change.

## 0. Verdict

| Defect | Root cause | Fix | Evidence status |
|---|---|---|---|
| `ClientPassFuritenBlocksSecondRealRonUntilTheNextDraw` → `furiten-read worker exit=3` | The test launched `Mahjong.LocalHost.CrashWorker.dll` with `Environment.ProcessPath`. Under `dotnet test`/MTP that is the xUnit test apphost (`Mahjong.LocalHost.Tests.exe`), which rejects the DLL path as an unknown option. The worker never runs. | Resolve a real `dotnet` host: reuse the current process only when its basename is `dotnet`, then `DOTNET_HOST_PATH`, then `DOTNET_ROOT`, then `dotnet` from `PATH`; forward `DOTNET_ROOT`. Worker DLL, argv, 30 s timeout, exit-code assertion, marker parsing, and furiten assertions are unchanged. | **PROVEN / REPRODUCED**: pristine `dotnet test` fails 3/3 with only this test (`114/1/0`, exit 1, worker `exit=3`); pristine direct run passes `115/115`; patched `dotnet test` passes `115/115` (work 1/1, combo 3/3). |
| `ExpiredOrRevokedScopeCannotPersistRejectionOnEitherCommandPath` → `Expected: 1 / Actual: 2` | `finally` released the valid, parked AI follow-up before the unauthorized probe and the state-stillness read. Release lands a real autonomous commit; the assertion then races that commit. | Keep the hold attached through the probe, all durable-receipt assertions, the state-row assertions, and the receipt lookup; release in a `finally` only after them; then wait for the released follow-ups to settle. Add a non-weakening post-release assertion that the held AI actually advanced the state, and re-check that the unauthorized request still has no durable receipt/control row. | **MECHANISM PROVEN**: a 5 ms release-before-read mutation fails 3/3 with the caller's exact `Expected: 1 / Actual: 2`. **Natural caller schedule NOT_REPRODUCED**. **Production scope defect NOT_PROVEN**. Patched/combo full suites pass `115/115`, 0 skipped. |

Neither failure is proven to be a production furiten or scope-persistence defect. Both mechanisms
are reproduced in `Mahjong.LocalHost.Tests` test-harness code; no production code, production
assertion, verifier, skip, or retry was changed to obtain green.

## 1. Exact inputs and integrity

The candidate was extracted only through `git archive` into ignored `build/` (`.gitignore:3`).

| Item | Value |
|---|---|
| Candidate | `00cd02d80172ebd13a2945e4e8bacfe486ae0c21` |
| Candidate tree / parent | `72502a19a808738f9e2c801f83e47d46dc07761d` / `2bb6e681c26060407542f09bc2495c4b30b2c7c0` |
| PR20 head / tree | `0e5d77a9f8f4052e6b98c222da7ba94229e5ffe1` / `d38192d46525a610c043ff57c6e61dac93d3ca73` |
| `00cd02d` LF archive | `build/localhost-two-fixes-20261030/pr20-00cd02d-lf.tar`, SHA-256 `BF799A62E8F83B887D860171C209E1E1F52314770E2C87DAFFA9404539A22B99` |
| `0e5d77a` LF archive | `build/localhost-two-fixes-20261030/pr20-0e5d77a-lf.tar`, SHA-256 `157AED61B2FFAA309D487E45000429035B44992628A751F38F00103FFC98C036` |
| `LocalControlTests.cs` blob (both commits) | `761a51ed6faf4f80a9592136bcedd447ed495cc2` |
| `LocalHostCandidateAcceptanceTests.cs` blob (both commits) | `d5486eaebb9a6db096130f8ae05ddc08adb3d8e5` |
| `LocalHostRecoveryTests.cs` blob (both commits) | `c9740865b57b73a1400b43356f522701df640cde` |
| `P41Harness.cs` blob (both commits) | `3bbaf7fe96567ab5cc04ad9f634fed71a478acb8` |
| Raw LF candidate `LocalControlTests.cs` SHA-256 | `00B4996B50577F4D179371019B23A93B72D3627F197C4ECC3CBC7A06B3A0C5FC` |
| Raw LF candidate `LocalHostCandidateAcceptanceTests.cs` SHA-256 | `1CAAF3052B458CE4B2067BD64F3421831D4E859A3DA4D372D043C6E505A73FCC` |

Accepted base `8fd7c6c` contains **no** `dotnet/tests/Mahjong.LocalHost.Tests` files, so the patch
necessarily targets the unaccepted candidate tree. The delivery commit is based on the accepted
commit identity and adds only two documents; it does not merge PR20 into `v5`.

The approved furiten patch was copied byte-for-byte from the accepted report and preserved:

| Item | Value |
|---|---|
| Approved furiten patch | `doc/v5-重构/patches/V5-LOCALHOST-FURITEN-RACE-REMEDY-TEST-FIX-20261030.patch` in `localhost-scope-race-20261030` |
| Preserved copy | `build/localhost-two-fixes-20261030/approved-furiten.patch` |
| SHA-256 | `DED09A4337CEB04E15B7642255EDB6C66A987E61997A00F9E382AB3A62623B7D` |
| Candidate preimage / patched postimage blob | `d5486ea` / `0bc67c8558f469b0792bf9f07dbd7dc5e28862f1` |
| Scope | one file, `LocalHostCandidateAcceptanceTests.cs`; `28 insertions(+), 8 deletions(-)` |

It was applied unchanged in the combination tests. It is not modified or re-reviewed here.

## 2. Patch design

Delivered patch:
`doc/v5-重构/patches/V5-LOCALHOST-CALLER-113-115-TWO-FIXES-20261030.patch`

- SHA-256: `500AB2301394B96B82366BD523F1239DAB5B6877979CF29EC7DAEF60CABA7BE1`
- 3 files, all under `dotnet/tests/Mahjong.LocalHost.Tests/`.
- Raw stat: `179 insertions(+), 62 deletions(-)`; whitespace-ignored (`git diff -w`):
  `137 insertions(+), 20 deletions(-)`. The extra raw churn is the probe block being moved inside
  the existing `try` (indentation only), not changed assertions.
- LF, UTF-8 without BOM; no CRLF in the patch body.

### 2.1 Worker host (`LocalHostCandidateAcceptanceTests.cs`, `P41Harness.cs`)

`P41Harness` adds `ResolveDotnetHost()` and `CreateWorkerStartInfo(...)`:

1. reuse `Environment.ProcessPath` only when `Path.GetFileNameWithoutExtension` is `dotnet`
   (case-insensitive); never the xUnit/test apphost;
2. otherwise use `DOTNET_HOST_PATH` when it names an existing `dotnet` host;
3. otherwise use `DOTNET_ROOT/dotnet(.exe)` when present;
4. otherwise return `dotnet`, resolved by `ProcessStartInfo` through `PATH`;
5. forward a non-empty `DOTNET_ROOT` to the child (from the environment, or the resolved host's
   directory).

The furiten worker call now uses this helper and appends the same two clock arguments. The following
are unchanged: `CrashWorker.dll` path, save directory, room id, marker path, `furiten-read` mode,
30 s timeout, `ExitCode == 0` assertion, four-line marker parsing, round id, temporary-bit check, and
the subsequent second-Ron rejection assertions. No skip, no accepted `exit=3`, no deleted
cross-process check.

### 2.2 Hold through unauthorized-state assertions (`LocalControlTests.cs`)

`ExpiredOrRevokedScopeCellAsync` now:

1. keeps `AiSubmitHold` attached through the unauthorized command, the durable
   receipt/digest/control lookups, the state-row stillness assertions, and the `NotFound` receipt
   query;
2. releases/detaches in a `finally` **after** those checks (or on failure), so a failure cannot leave
   the driver permanently parked;
3. for the revoked cell (the cell that actually holds an AI follow-up), calls the existing
   `WaitUntilFollowUpsSettleAsync(...)` milestone and asserts the released follow-up advanced
   `StateVersion` past the frozen value;
4. re-checks after release that the unauthorized request still has no durable receipt, digest, or
   control result, that `ControlVersionForTest` is still `0`, and that the receipt query is still
   `NotFound`.

Every pre-existing assertion remains. There is no retry, no sleep-based green, no removed state
assertion, and no collection/parallelism weakening. `Assert.` occurrences in
`LocalControlTests.cs` rise from 414 to 420; `LocalHostCandidateAcceptanceTests.cs` remains 270.

## 3. Clean applicability and SHA verification

All checks used a fresh LF extraction of the exact commit and a fresh local `.git`, with
`core.autocrlf=false`, `git apply --check --whitespace=error-all`.

| Tree | Operation | Exit | Postimage blobs |
|---|---|---:|---|
| exact `00cd02d` LF | `git apply --check` / `git apply` | 0 / 0 | `LocalControlTests.cs` `e1b9f7a985cdf305abebe8bd70a5b83ab49fd88d`; `LocalHostCandidateAcceptanceTests.cs` `e6cf08a8cc1d820e49f3f2005a4d199106a0ed03`; `P41Harness.cs` `273bba4b9ad8d865774948e5bf88a1a432553e88` |
| PR20 head `0e5d77a` LF | `git apply --check` / `git apply` | 0 / 0 | same three postimage blobs |
| `00cd02d` + approved furiten patch, then this patch | furiten check/apply 0/0; this check/apply 0/0 | 0 | `LocalControlTests.cs` `e1b9f7a...`; `LocalHostCandidateAcceptanceTests.cs` `e6779677ff56ab3eb5a80f2d04015af9472fdf44`; `P41Harness.cs` `273bba4...` |
| `00cd02d` + this patch, then approved furiten patch | this check/apply 0/0; furiten check/apply 0/0 | 0 | identical combination postimages |

Postimage file SHA-256 (plain LF file, this patch alone):

| File | SHA-256 |
|---|---|
| `LocalControlTests.cs` | `C3B8DC465987A1CAA572F5E1B221B098EFCA07DD7AA38F261AD61FCAFCFB1AB5` |
| `LocalHostCandidateAcceptanceTests.cs` | `2DEDEB9246D8943169BC5D2F6EE867E2C9EFE21B8B4785A039C8D31FCE0366B2` |
| `P41Harness.cs` | `B602886C58948DF5F605B77AA5C518010ABBFAC73EAAAB247C5E38BBB4C809E5` |

Replay note: a plain Windows `git archive` materializes `*.cs` as CRLF and `git apply` may then
silently skip an LF patch. Extract with
`git -c core.eol=lf -c core.autocrlf=false archive ...`, or materialize the exact blob with
`git cat-file blob <sha>`, before applying. This is a checkout concern, not a patch-context
ambiguity: both LF trees above check cleanly.

## 4. Environment and verification matrix

Environment: Windows 10 Pro 10.0.19045, 16 logical processors, .NET SDK `8.0.426`, host `8.0.32`,
xUnit.net v3 `4.0.2` with Microsoft.Testing.Platform v2, local NTFS. All builds are fresh Release
builds in isolated trees with no shared `bin/obj`.

Tree names:

- `pristine` = exact `00cd02d`.
- `work` = `00cd02d` + this patch.
- `combo` = `00cd02d` + approved furiten patch + this patch.
- `mut-*` = ignored diagnostic copies; not delivered.

Builds:

| Tree | Exit | Warnings / errors | Log SHA-256 |
|---|---:|---|---|
| `pristine` | 0 | 0 / 0 | `74CA2C647CF7AAD9594518BB1ADE4F11E84D0BDA71EDFAECA1B806C97DE83F0D` |
| `work` | 0 | 0 / 0 | `91095882F1C2534379B0EF8E8D7AEC4B289C272C6EAD50BCDB1D69AFEB77333B` |
| `combo` | 0 | 0 / 0 | `4FB344CCF08A5688ECA7DCCA840915EA7E15CF0941729BA022B2240C56209D1A` |
| `mut-release-early` | 0 | 0 / 0 | `ECBC71730D07EA8DE971EC99F324B0211CC7E9D16BAC422A2CB4CA17FDD90619` |
| `mut-receipt` | 0 | 0 / 0 | `5C486F3AF2E791E8B32C3EBACA6C3BCC3B0E45A4AE83AB79A140C3FBEB724488` |

### 4.1 Targeted direct runner

Command form:

```powershell
dotnet <tree>/dotnet/tests/Mahjong.LocalHost.Tests/bin/Release/net8.0/Mahjong.LocalHost.Tests.dll `
  -noLogo -reporter default `
  -method '<fully-qualified-test-name>'
```

| Tree | Test | Runs | Exit each | Counts each | Result |
|---|---|---|---:|---|---|
| `pristine` | `ClientPassFuritenBlocksSecondRealRonUntilTheNextDraw` | 1 | 0 | 1 / 0 / 0 | PASS (7.189 s) |
| `work` | same worker test | 3 | 0 | 1 / 0 / 0 | PASS 3/3 (6.839, 2.538, 2.403 s) |
| `work` | `ExpiredOrRevokedScopeCannotPersistRejectionOnEitherCommandPath` | 6 | 0 | 1 / 0 / 0 | PASS 6/6 (1.32–1.48 s) |
| `combo` | worker test | 1 | 0 | 1 / 0 / 0 | PASS (6.168 s) |
| `combo` | control test | 11 | 0 | 1 / 0 / 0 | PASS 11/11 (first 1.271 s; 10 stress runs all pass) |
| `combo` | `ClientPassOnRonSetsFuritenAndColdRestartKeepsItUntilTheNextDraw` (approved patch target) | 1 | 0 | 1 / 0 / 0 | PASS (1.328 s) |

### 4.2 Full LocalHost suite (115 tests)

| Tree | Runner | Runs | Exit each | Total | Failed | Skipped | Result |
|---|---|---|---:|---:|---:|---:|---|
| `pristine` | direct DLL | 1 | 0 | 115 | 0 | 0 | PASS 115/115, 28.574 s |
| `pristine` | `dotnet test` | 3 | 1 | 115 | 1 | 0 | 114/1/0 each; only worker test, `exit=3` |
| `work` | direct DLL | 1 | 0 | 115 | 0 | 0 | PASS 115/115, 26.374 s |
| `work` | `dotnet test --no-build` | 1 | 0 | 115 | 0 | 0 | PASS 115/115, 26.303 s |
| `combo` | direct DLL | 1 | 0 | 115 | 0 | 0 | PASS 115/115, 25.776 s |
| `combo` | `dotnet test --no-build` | 1 | 0 | 115 | 0 | 0 | PASS 115/115, 25.695 s |
| `combo` | `dotnet test --no-restore` | 2 | 0 | 115 | 0 | 0 | PASS 115/115, 28.428 / 26.895 s |

There were **no skips and no not-run tests** in any passing run. The caller-equivalent command used
for the repeated combination runs was:

```powershell
dotnet test dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj -c Release --no-restore
```

“Full” in this report means the full `Mahjong.LocalHost.Tests` project (the caller's command scope),
not the entire multi-project solution. No PG/WSS/Unity/Android/iOS test project was run.

## 5. Negative controls

All mutations live only in ignored `build/` copies and are **not** in the delivered patch. Each
failing control was run three times.

### 5.1 Worker host control (pristine exact candidate)

The pristine exact candidate has the original `Environment.ProcessPath ?? "dotnet"` launch. Its
worker test:

- passes under the direct DLL runner: `1/0/0`, exit 0;
- fails under the caller's `dotnet test` path in all 3 full runs: `114/1/0`, exit 1, with the exact
  assertion text:

```text
失败 Mahjong.LocalHost.Tests.LocalHostCandidateAcceptanceTests.ClientPassFuritenBlocksSecondRealRonUntilTheNextDraw
  furiten-read worker exit=3 stderr=
```

The preserved TestResults log confirms the runner command was
`Mahjong.LocalHost.Tests.exe --internal-msbuild-node ...`, i.e. the apphost, not `dotnet`.
After applying this patch, the same full `dotnet test` command passes `115/115` (work and combo,
including 3/3 combo runs). This isolates the failure to worker-host dispatch, not to CrashWorker,
LocalHost, furiten, or the approved furiten patch.

### 5.2 Release-before-read state control

In `mut-release-early`, only the test copy is mutated: `hold.Release()` is moved before the probe
and followed by `await Task.Delay(5, ct);`, restoring the caller's ordering. Result 3/3:

```text
Assert.Equal() Failure: Values differ
Expected: 1
Actual:   2
  ... LocalControlTests.cs(1841)
  ... LocalControlTests.cs(1728)   # normal/revoked cell
```

This is the caller's exact cell, assertion shape, and value pair. It shows the state-stillness
assertion is sensitive to the released valid AI commit and that the fix is an ordering change, not
a faster read. It does **not** reproduce the caller's natural scheduler interleaving.

### 5.3 Unauthorized durable-receipt control

In `mut-receipt`, the ignored test copy calls the existing
`LocalTableStore.TryCommitRejectedReceipt(...)` for the probe request after the transport rejection,
simulating an unauthorized scope borrowing the durable rejection path. Result 3/3:

```text
Assert.Null() Failure: Value is not null
Expected: null
Actual:   TableDurableReceipt { AccountId = "local-room-1-seat0", Api = "CLAIM_CONTROL",
          Detail = "mutation: unauthorized rejection persisted", ... }
  ... LocalControlTests.cs(1846)
```

This shows the durable receipt/digest assertions are not vacuous. In the unmutated patched runs the
same assertions pass, both before and after the held AI release, so the transport-only behavior is
observed clean for these cells.

### 5.4 Release behavior

The revoked cell is the one that actually holds a valid AI follow-up. After the hold-through
assertions, the patch releases it, waits for the existing turn-return milestone
(`WaitUntilFollowUpsSettleAsync`), and asserts `afterRelease.StateVersion > after.StateVersion`.
That assertion passes in every patched/combo targeted and full run (including 11 combo targeted
runs), proving the held follow-up was live and its release landed autonomously. The release-before-
read control above shows the same movement is exactly what the old ordering let race the read.

## 6. Diagnosis and evidence labels

### 6.1 Worker host

Root cause is the test helper, not production. Under MTP the current process is the xUnit apphost;
`Process.Start` therefore asks xUnit to interpret the worker DLL path as a command-line option.
The observed `exit=3` with empty stderr and `error: unknown option: ...CrashWorker.dll` is the
test host's argument parser, and the worker is never opened. Direct invocation (`dotnet <test>.dll`)
has `dotnet` as `Environment.ProcessPath`, which is why the direct runner passed. This is
`PROVEN / REPRODUCED`, deterministic under the caller's runner.

### 6.2 Held AI / unauthorized state

The held follow-up is a real, current decision: the hold is attached at the driver's
before-submit hook, the test proves the hook was entered, and after release the state advances. The
unauthorized probe itself is rejected by the transport and leaves no durable receipt, digest,
control result, or state movement while the AI is held. The caller's second failure is therefore a
test baseline/release-order defect, not evidence of a production scope-persistence defect.

Labels, deliberately kept separate:

| Claim | Label | Basis |
|---|---|---|
| Worker `exit=3` mechanism and test-harness causality | **PROVEN / REPRODUCED** | pristine `dotnet test` 3/3 `114/1/0`; direct `115/115`; patched `dotnet test` `115/115` |
| Released valid AI moves `StateVersion` under the stillness read | **PROVEN** | 5 ms release-first mutation 3/3 exact `1/2` |
| Caller's exact natural interleaving | **NOT_REPRODUCED** | no scheduler trace; pristine control test 10/10 direct + 3 full `dotnet test` runs did not fail it |
| Production scope-persistence defect | **NOT_PROVEN** | under the hold, probe leaves no durable receipt/digest/control/state movement; no production change |
| Approved furiten patch caused either failure | **NO** | patch scope is one other test; pristine and patched fail the same worker test under `dotnet test`; combo passes `115/115` |

No failure is labelled “flaky”. The worker failure is deterministic under `dotnet test`; the second
is an ordering hazard with a proven mechanism and an unreproduced natural schedule.

## 7. Boundaries and non-claims

- No push, PR/MR, CI trigger/rerun, branch rewrite, or other-worktree mutation.
- No production, verifier, solution, Unity, trust, Android/iOS, or CI-workflow change.
- No full-solution, PostgreSQL, WSS, container, single-core, or other-OS run.
- No G2–G5 gate review and no PR20 approval. The caller reviews independently.
- The approved furiten patch is preserved and applied unchanged for combination only; this report
  does not re-accept or modify it.
- The patch’s only executable effect is in three `Mahjong.LocalHost.Tests` files. It adds no
  production API, no skip, no retry, and removes no assertion.

## 8. Deliverables

Exactly two committed files:

1. `doc/v5-重构/patches/V5-LOCALHOST-CALLER-113-115-TWO-FIXES-20261030.patch`
   — SHA-256 `500AB2301394B96B82366BD523F1239DAB5B6877979CF29EC7DAEF60CABA7BE1`.
2. `doc/v5-重构/V5-LOCALHOST-CALLER-113-115-TWO-FIXES-20261030.md`
   — this report.

The local commit is based on `8fd7c6c`; parent and commit identity are reported in the delivery
message. Nothing is pushed.

## 9. Evidence index (ignored `build/localhost-two-fixes-20261030/`)

Key logs (SHA-256 of the preserved raw files):

| File | SHA-256 |
|---|---|
| `logs-pristine-build.txt` | `74CA2C647CF7AAD9594518BB1ADE4F11E84D0BDA71EDFAECA1B806C97DE83F0D` |
| `logs-work-build.txt` | `91095882F1C2534379B0EF8E8D7AEC4B289C272C6EAD50BCDB1D69AFEB77333B` |
| `logs-combo-build.txt` | `4FB344CCF08A5688ECA7DCCA840915EA7E15CF0941729BA022B2240C56209D1A` |
| `run-pristine-full-direct.log` | `2243F44C7895421CD232C6EB39FC0EC6DA01CC6EAADEC5C593E758ED674B1F39` |
| `run-pristine-full-dotnettest.log` | `767716DB2D924E4B832B0047EB7BC724F96CC84F47600C60DE84AD7E124B9646` |
| `run-pristine-full-dotnettest-2.log` | `FDDBCF151756B7AD953A4710905E6CC226BD777F40214340AE15E748EA23F222` |
| `run-pristine-full-dotnettest-3.log` | `6DA7E6CC45F57F917F765604F259364C3218EAEBEF60BF6C691A757B7A0805DB` |
| `evidence/pristine-dotnettest-TestResults.log` | `4B76FEC24EDD6F0F319311E238844C9C81CEDAE8051D36165B1AE89F6AADF596` |
| `run-pristine-target-worker-direct.log` | `959CA274E5C3FBE5DEAD3897026F3E070009EDF667C9D22A91BB749006813570` |
| `run-work-target-worker.log` | `24A97A732CED4B3AFEECDB4F37A0B00744DAB5026EFC73CA7BE460908FDB2F1F` |
| `run-work-target-control.log` | `30BD479A89AE1AA3E4280B47FB1513CA449D747258349582C42D91C4D701EA60` |
| `run-work-full-direct.log` | `52C12595A5AD709CC6C8024F5A4FEA1860EFB4C7B9F4146A489F39762BF5B409` |
| `run-work-full-dotnettest.log` | `76709099B832CE9B23EFD06ACC814B15B3ED3AA7DD725375728180CEF4F8C8CB` |
| `run-combo-full-direct.log` | `C9857559AD342ACFFD2ACE7DFFC8DB681E65122A16C30A15C4A0CB9438461918` |
| `run-combo-full-dotnettest.log` | `4409D1DA777BE10F05A9F6F7FE77B8390DBEE429D16ECE8850C22AEB3F0E6759` |
| `run-combo-full-dotnettest-2.log` | `70863BBF9DF2408D810426C6AA1F4D4000FFCF2A4E8D65DE813C765DD2C3CFCE` |
| `run-combo-full-dotnettest-3.log` | `BCB6E3A685AE4A81AA23101B1D72E2983AD07353BC2665430CA31143E096FC25` |
| `run-mut-release-early-5ms.log` | `450AEBD8E2B9A192A245842D6D86C31E29414E37653563C9FD9A8603263AF5F0` |
| `run-mut-release-early-5ms-2.log` | `450D8DED4BC791AEEE10A8B4263B9C7D41AE1FA0A95231A1C93507303660E5D0` |
| `run-mut-release-early-5ms-3.log` | `450D8DED4BC791AEEE10A8B4263B9C7D41AE1FA0A95231A1C93507303660E5D0` |
| `run-mut-receipt.log` | `879571EA9228153B2C1E01FAA26A4EE942B92A6085D76DF44E5BCE940BDF1DF8` |
| `run-mut-receipt-2.log` | `9C7E8604F6FD054D4F39CF58F6C6BFF6635E41740F215181701EDA35D8687EF9` |
| `run-mut-receipt-3.log` | `A540FAAACDF0B8BE10B43B337E96F7DBA9EA17D92C171E4616FE190BA2EDDD86` |
| `logs-mut-release-early-build.txt` | `ECBC71730D07EA8DE971EC99F324B0211CC7E9D16BAC422A2CB4CA17FDD90619` |
| `logs-mut-receipt-build.txt` | `5C486F3AF2E791E8B32C3EBACA6C3BCC3B0E45A4AE83AB79A140C3FBEB724488` |

Other preserved artifacts:

| Artifact | SHA-256 |
|---|---|
| `approved-furiten.patch` | `DED09A4337CEB04E15B7642255EDB6C66A987E61997A00F9E382AB3A62623B7D` |
| `pr20-00cd02d-lf.tar` | `BF799A62E8F83B887D860171C209E1E1F52314770E2C87DAFFA9404539A22B99` |
| `pr20-0e5d77a-lf.tar` | `157AED61B2FFAA309D487E45000429035B44992628A751F38F00103FFC98C036` |
| `candidate-two-fixes.patch` (pre-delivery copy) | `500AB2301394B96B82366BD523F1239DAB5B6877979CF29EC7DAEF60CABA7BE1` |

## 10. Reproduction commands

```powershell
# exact LF candidate (from the accepted-base worktree; build/ is ignored)
git -c core.eol=lf -c core.autocrlf=false archive --format=tar `
  -o build/localhost-two-fixes-20261030/pr20-00cd02d-lf.tar 00cd02d
tar -xf build/localhost-two-fixes-20261030/pr20-00cd02d-lf.tar `
  -C build/localhost-two-fixes-20261030/candidate

# isolated patch application (fresh tree with its own .git, core.autocrlf=false)
git -C build/localhost-two-fixes-20261030/verify-final apply --check --whitespace=error-all `
  D:/majiang-wt/localhost-two-fixes-20261030/doc/v5-重构/patches/V5-LOCALHOST-CALLER-113-115-TWO-FIXES-20261030.patch
git -C build/localhost-two-fixes-20261030/verify-final apply --whitespace=error-all `
  D:/majiang-wt/localhost-two-fixes-20261030/doc/v5-重构/patches/V5-LOCALHOST-CALLER-113-115-TWO-FIXES-20261030.patch

# Release build
dotnet build dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj -c Release --nologo

# targeted direct runner
dotnet <tree>/dotnet/tests/Mahjong.LocalHost.Tests/bin/Release/net8.0/Mahjong.LocalHost.Tests.dll `
  -noLogo -reporter default `
  -method 'Mahjong.LocalHost.Tests.LocalControlTests.ExpiredOrRevokedScopeCannotPersistRejectionOnEitherCommandPath'
dotnet <tree>/dotnet/tests/Mahjong.LocalHost.Tests/bin/Release/net8.0/Mahjong.LocalHost.Tests.dll `
  -noLogo -reporter default `
  -method 'Mahjong.LocalHost.Tests.LocalHostCandidateAcceptanceTests.ClientPassFuritenBlocksSecondRealRonUntilTheNextDraw'

# full LocalHost suite, direct and caller-equivalent
dotnet <tree>/dotnet/tests/Mahjong.LocalHost.Tests/bin/Release/net8.0/Mahjong.LocalHost.Tests.dll `
  -noLogo -reporter default
dotnet test dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj -c Release --no-restore
```

The caller should apply the patch to an LF checkout of `00cd02d` (or `0e5d77a`, whose test files are
identical), apply the approved furiten patch if testing the combination, and rerun the focused and
full `Mahjong.LocalHost.Tests` commands. Do not infer PR20, G2–G5, or production acceptance from
this report.
