# V5 LocalHost Furiten Race — Test-Only Fix Delivery (2026-10-30)

Status: **candidate test-only patch plus reproducible evidence for caller review. This is not an
acceptance, not a PR20 PASS, and not a G2–G5 approval. PR20 remains unaccepted until the caller
independently reviews and applies the patch. V4 remains the only production baseline.**

- Accepted baseline: `origin/v5 = 8fd7c6cb701dfbb41c28938de4186749bdd1d6c7` (also this
  worktree's `HEAD`).
- Frozen unaccepted candidate: `00cd02d80172ebd13a2945e4e8bacfe486ae0c21`
  (tree `72502a19a808738f9e2c801f83e47d46dc07761d`; parent
  `2bb6e681c26060407542f09bc2495c4b30b2c7c0`).
- Base worktree / branch: `D:/majiang-wt/furiten-fix-20261030b`,
  `wt/v5-furiten-fix-20261030b`.
- Accepted experiment report: `V5-LOCALHOST-FURITEN-RACE-REMEDY-20261030.md` at
  `54c460a` (blob `8300d17817ec15b4612e8e513b6810986f6569c3`, SHA-256
  `BF540D57A58161964870AD8D387EF8721F0D49664F4CA785617B1CE55A8599DB`).
- Patch: `doc/v5-重构/patches/V5-LOCALHOST-FURITEN-RACE-REMEDY-TEST-FIX-20261030.patch`.
- This report is the only other committed file. The candidate tree was extracted and modified
  only inside ignored `build/furiten-test-fix-20261030b/`.
- No production change, push, PR change, delegation, CI rerun, Unity change, trust change, or
  modification of any other worktree.

---

## 0. Verdict

| Question | Result | Evidence |
|---|---|---|
| Is the patch confined to one test file? | **PASS** | `git apply --stat`: 1 file, **28 insertions(+), 8 deletions(-)**; no production path. |
| Does the patch apply to the exact frozen candidate? | **PASS** | Candidate blob `d5486ea` → postimage blob `0bc67c8`; `git apply --check` exit **0**; patched file SHA-256 `448EE73F...`. |
| Does the patched project build Release cleanly? | **PASS** | exit **0**, **0 warnings / 0 errors**, 43.28 s. |
| Focused target test, patched, 20 consecutive runs? | **PASS 20/20** | exit **0** each; every run `Total: 1, Failed: 0, Skipped: 0`; 1.35–1.63 s. |
| Patched plus 750 ms delay after the pass, 10 runs? | **PASS 10/10** | exit **0** each; every run `Total: 1, Failed: 0, Skipped: 0`; 2.10–4.30 s. |
| Full LocalHost suite, patched? | **PASS 115/115** | `Total: 115, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0`; 29.09 s. |
| Pristine candidate, the same focused test, 20 runs? | **PASS 20/20** | This is **not** proof that the original test has no race (see §6). |
| Pristine candidate plus 750 ms, 3 runs? | **FAIL 3/3 (expected control)** | exit **1**; `Assert.True()` Expected True / Actual False; line **1065**. |
| Patched but stop barrier removed, plus 750 ms, 3 runs? | **FAIL 3/3 (expected control)** | exit **1**; `Expected: 5 / Actual: 7`; the added boundary assertion fires first. |
| Cold recovery loses the persisted seat-0 furiten bit, 3 runs? | **FAIL 3/3 (expected mutation)** | exit **1**; `cold restart lost the committed furiten fact`; line **1108**. |
| Next-draw clearing removed, 3 runs? | **FAIL 3/3 (expected mutation)** | exit **1**; `the next draw must clear temporary furiten`; line **1114**. |
| Are the accepted report's 88 evidence entries intact? | **PASS 88/88** | All `SHA256SUMS.txt` entries match; the manifest itself is `AFED9D71...`. |
| Was CI rerun or the historical CI interleaving identified? | **NOT RUN / UNKNOWN** | No CI trigger, rerun, or API query. The exact CI save/state/tick interleaving is still BLOCKED/UNKNOWN. |
| Is PR20 or any gate accepted? | **NO** | PR20 remains unaccepted; G2–G5 are not approved; V4 remains production. |

Short conclusion: the accepted experiment's patch is applicable and survives independent isolated
reproduction on the current frozen candidate. The patch does not make the test more permissive:
it stops the real driver while seat 0 has an open human decision, pins the durable read to the
pass's `CommandVersion`, verifies cold recovery before any new draw, and then restarts the real
driver to prove the next draw clears temporary furiten. Twelve expected-failure control runs show
the assertions are sensitive to the race, the missing barrier, and two real behavior mutations.

---

## 1. Exact candidate identity and source equivalence

The caller named PR20 `00cd02d`, while the accepted experiment used `53cd8c3` as its source
candidate. `00cd02d` is a later docs-only descendant; the target test file is byte-identical.

| Item | Value |
|---|---|
| `00cd02d` subject | `docs(v5): correct 54c push artifact digest in CI audit` |
| `00cd02d` tree | `72502a19a808738f9e2c801f83e47d46dc07761d` |
| `00cd02d` parent | `2bb6e681c26060407542f09bc2495c4b30b2c7c0` |
| `53cd8c3` tree used by the experiment | `36e8351d932b5f33d440ed5e147c2615620d725b` |
| Target path | `dotnet/tests/Mahjong.LocalHost.Tests/LocalHostCandidateAcceptanceTests.cs` |
| Target blob in both `53cd8c3` and `00cd02d` | `d5486eaebb9a6db096130f8ae05ddc08adb3d8e5` |
| `00cd02d` changes since `53cd8c3` | three added docs only; the target test file is unchanged |

Therefore the patch is against the exact source required by the caller: `00cd02d`'s target file is
the same blob as the accepted experiment's target.

---

## 2. Isolation and hash verification

`build/` is ignored by `.gitignore:3`:

```text
.gitignore:3:build/   build/furiten-test-fix-20261030b/...
```

The candidate was extracted only by `git archive` into that ignored directory:

```powershell
git archive --format=tar -o build/furiten-test-fix-20261030b/pr20-00cd02d.tar 00cd02d
tar -xf build/furiten-test-fix-20261030b/pr20-00cd02d.tar -C build/furiten-test-fix-20261030b/candidate
```

No candidate source was merged, staged, or committed in the base worktree.

### 2.1 Archive line-ending caveat (important for replay)

On this Windows host, plain `git archive` materializes `*.cs` as **CRLF**. The patch is **LF** and
targets the blob, so `git apply` on that extraction silently reports `Skipped patch` and leaves the
file unchanged. Two additional facts made the check misleading:

1. `build/` is inside the base Git worktree. Without a ceiling, `git apply` resolves the parent
   repository and looks for the patch path relative to the wrong root.
2. The accepted report's candidate-file SHA-256 `4DFBE23F...` is the **CRLF archive form**, not the
   raw blob form. The raw candidate blob SHA-256 is `1CAAF305...`.

The reproducible exact-tree procedure used here was:

```powershell
# Re-archive (the hash itself is not used as the patch precondition)
git -c core.eol=lf archive --format=tar -o build/furiten-test-fix-20261030b/pr20-00cd02d-lf.tar 00cd02d
tar -xf build/furiten-test-fix-20261030b/pr20-00cd02d-lf.tar -C build/furiten-test-fix-20261030b/base

# Materialize the exact LF target blob (cmd redirection preserves bytes)
cmd /c "git cat-file blob d5486ea > build\furiten-test-fix-20261030b\base\dotnet\tests\Mahjong.LocalHost.Tests\LocalHostCandidateAcceptanceTests.cs"

# Keep 'base' pristine; apply to a copy named 'fix'
Copy-Item -Recurse build\furiten-test-fix-20261030b\base build\furiten-test-fix-20261030b\fix

# Prevent Git from discovering the parent repository, then check/apply
$env:GIT_CEILING_DIRECTORIES = 'D:\majiang-wt\furiten-fix-20261030b\build\furiten-test-fix-20261030b'
cd build\furiten-test-fix-20261030b\fix
git apply --check <patch>   # exit 0
git apply <patch>           # exit 0
```

Hash verification:

| Artifact | SHA-256 / blob |
|---|---|
| Patch (`final-test-only.patch` / delivered patch) | `DED09A4337CEB04E15B7642255EDB6C66A987E61997A00F9E382AB3A62623B7D` |
| Candidate target raw blob | blob `d5486ea...`; SHA-256 `1CAAF3052B458CE4B2067BD64F3421831D4E859A3DA4D372D043C6E505A73FCC` |
| Candidate target after Windows archive (CRLF) | SHA-256 `4DFBE23FE73BE89A3022164458AD3C71C20B6B5EA678E3422963739CADC91B4B` |
| Patched target postimage | blob `0bc67c8...`; SHA-256 `448EE73F5702232447BFF6471EFAEE7C2D5D2B0786822749EDD63916EBF52BE4` |
| Patched file equals evidence `fixed.cs` | **yes**, `448EE73F...` |
| Plain candidate tar (`00cd02d`) | `3BC11229345DA1C4F61232ACBFEBBDE24A8EC148A652E9F994EA3A60A90E7C6D` |
| LF re-archive (`00cd02d`) | `BF799A62E8F83B887D860171C209E1E1F52314770E2C87DAFFA9404539A22B99` |
| Accepted experiment tar (`53cd8c3`) | `E7854D3A57EDA345871BF51C2E5CC11EDEAF14D10B26B85236AAEC4DA0CB3F86` |
| Evidence `SHA256SUMS.txt` | `AFED9D716933E921A4405723382885ABEE79CC483D0A89378C93442B87E94CB7` |

All **88/88** entries inside the accepted evidence manifest were rehashed and matched; there were
0 mismatches and 0 missing files.

---

## 3. Patch contents

Patch path:

`doc/v5-重构/patches/V5-LOCALHOST-FURITEN-RACE-REMEDY-TEST-FIX-20261030.patch`

The complete patch is 6,349 bytes, one file, **28 insertions / 8 deletions**. It is reproduced
verbatim below:

```diff
diff --git a/dotnet/tests/Mahjong.LocalHost.Tests/LocalHostCandidateAcceptanceTests.cs b/dotnet/tests/Mahjong.LocalHost.Tests/LocalHostCandidateAcceptanceTests.cs
index d5486ea..0bc67c8 100644
--- a/dotnet/tests/Mahjong.LocalHost.Tests/LocalHostCandidateAcceptanceTests.cs
+++ b/dotnet/tests/Mahjong.LocalHost.Tests/LocalHostCandidateAcceptanceTests.cs
@@ -903,6 +903,7 @@ namespace Mahjong.LocalHost.Tests
         public async Task ClientPassOnRonSetsFuritenAndColdRestartKeepsItUntilTheNextDraw()
         {
             await using var harness = new P41Harness();
+            harness.Options.AutoPlay = false;
             CancellationToken ct = TestContext.Current.CancellationToken;
 
             // Same legal wall as RonCandidateAndSubmissionTest: seat 0 waits on 3p (44) and
@@ -919,7 +920,11 @@ namespace Mahjong.LocalHost.Tests
             harness.Options.InitialRound = BuildRoundWithSpecificSlots(specific);
 
             ulong versionAfterPass;
+            ulong passCommandVersion;
             (ITableChannel channel, TableSession session) = await OpenAsync(harness, ct);
+            LocalTableRuntime? runtime = ((LocalTableChannel)channel).RuntimeForMatch;
+            Assert.NotNull(runtime);
+            runtime!.DriverForTest.Start();
             try
             {
                 using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
@@ -971,6 +976,15 @@ namespace Mahjong.LocalHost.Tests
                     ronDecision.Actions,
                     action => action.ActionType == (uint)ProtocolActionType.DeclareRon);
 
+                // Freeze the automatic driver while seat 0 has an open human decision. The
+                // response window cannot advance without the pass, and the stop happens before
+                // the pass commits, so no autonomous draw can clear furiten between the pass
+                // receipt and the durable-store read below.
+                ulong versionBeforeStop = session.PlayerSnapshot!.StateVersion;
+                await runtime.DriverForTest.StopAsync();
+                Assert.False(runtime.DriverRunning);
+                Assert.Equal(versionBeforeStop, session.PlayerSnapshot!.StateVersion);
+
                 var passResolved = new TaskCompletionSource<TableReceiptResolvedEventArgs>(
                     TaskCreationOptions.RunContinuationsAsynchronously);
                 session.ReceiptResolved += (_, args) =>
@@ -1000,6 +1014,8 @@ namespace Mahjong.LocalHost.Tests
                     passResolved.Task,
                     pumpCts.Token);
                 Assert.Equal(ReceiptStatus.Applied, passReceipt.Resolution.ReceiptStatus);
+                Assert.True(passReceipt.Resolution.CommandVersion.HasValue);
+                passCommandVersion = passReceipt.Resolution.CommandVersion!.Value.Value;
 
                 for (int i = 0; i < 50 && session.PlayerSnapshot?.PrivateView?.Furiten != true; i++)
                 {
@@ -1009,8 +1025,6 @@ namespace Mahjong.LocalHost.Tests
                 PlayerSnapshot afterPass = session.PlayerSnapshot!;
                 Assert.True(afterPass.PrivateView.Furiten, "the owner projection must carry temporary furiten");
                 versionAfterPass = afterPass.StateVersion;
-                LocalTableRuntime? runtime = ((LocalTableChannel)channel).RuntimeForMatch;
-                Assert.NotNull(runtime);
                 Assert.True(runtime!.ActorForTest.FuritenForHost.IsTemporary(0));
                 Assert.False(runtime.ActorForTest.FuritenForHost.IsTemporary(1));
 
@@ -1060,6 +1074,9 @@ namespace Mahjong.LocalHost.Tests
             // latest projection facts; inspect them before any recovery path can normalize them.
             using (LocalTableStore store = await LocalTableStore.OpenAsync(harness.Options, harness.Clock, null, ct))
             {
+                TableStateRow savedState = Assert.IsType<TableStateRow>(
+                    await store.LoadStateAsync(P41Harness.RoomId, ct));
+                Assert.Equal(passCommandVersion, savedState.StateVersion.Value);
                 Assert.True(store.LoadTemporaryFuriten()[0]);
                 Assert.False(store.LoadRiichiFuriten()[0]);
                 TableCommittedFactsSnapshot facts = Assert.IsType<TableCommittedFactsSnapshot>(
@@ -1075,24 +1092,27 @@ namespace Mahjong.LocalHost.Tests
             }
 
             // Cold restart: the furiten bit must come from the committed projection facts, not
-            // from process memory, and must survive until seat 0 actually draws.
+            // from process memory, and must survive until seat 0 actually draws. AutoPlay is
+            // still off, so the restored snapshot is inspected before any new draw can commit;
+            // only then is the real driver started to reach the clearing draw.
             (ITableChannel reopened, TableSession restored) = await OpenAsync(harness, ct);
+            LocalTableRuntime? restoredRuntime = ((LocalTableChannel)reopened).RuntimeForMatch;
+            Assert.NotNull(restoredRuntime);
+            Assert.False(restoredRuntime!.DriverRunning);
             try
             {
                 using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                 pumpCts.CancelAfter(TimeSpan.FromSeconds(30));
                 Task pump = StartPump(restored, pumpCts.Token);
-                for (int i = 0; i < 50 && restored.PlayerSnapshot?.PrivateView?.Furiten != true; i++)
-                {
-                    await Task.Delay(20, pumpCts.Token);
-                }
 
                 Assert.True(restored.PlayerSnapshot!.PrivateView.Furiten, "cold restart lost the committed furiten fact");
-                Assert.True(restored.PlayerSnapshot.StateVersion >= versionAfterPass);
+                Assert.Equal(versionAfterPass, restored.PlayerSnapshot.StateVersion);
 
+                restoredRuntime.DriverForTest.Start();
                 PlayerSnapshot nextTurn = await WaitForDecisionAsync(restored, pumpCts.Token);
                 Assert.True(nextTurn.StateVersion > versionAfterPass);
                 Assert.False(nextTurn.PrivateView.Furiten, "the next draw must clear temporary furiten");
+                Assert.False(restoredRuntime.ActorForTest.FuritenForHost.IsTemporary(0));
 
                 pumpCts.Cancel();
                 await pump;
```

The patch does not add a production API. It uses only existing test seams:
`LocalTableHostOptions.AutoPlay`, `LocalTableRuntime.DriverForTest`/`DriverRunning`, and
`LocalTableDriver.Start()`/`StopAsync()`. It deletes no assertion, adds no retry loop, adds no
skip, and does not change the production default `AutoPlay = true`.

---

## 4. Environment and commands

| Item | Value |
|---|---|
| OS | Windows 10 Pro, NT 10.0.19045 |
| CPU / RAM | 16 logical processors / 16,890,716,160 bytes |
| .NET SDK | `8.0.426` |
| PowerShell | `5.1.19041.6456` |
| Runner | xunit.v3 direct executable (`dotnet <test>.dll`) |
| Candidate root | `build/furiten-test-fix-20261030b/base/` |
| Patched root | `build/furiten-test-fix-20261030b/fix/` |
| Probe roots | `base-delay/`, `fix-delay/`, `mut-nostop/`, `mut-persist/`, `mut-clear/` |

Build command, executed once per isolated tree:

```powershell
dotnet build dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj -c Release --nologo
```

Focused command:

```powershell
dotnet <tree>/dotnet/tests/Mahjong.LocalHost.Tests/bin/Release/net8.0/Mahjong.LocalHost.Tests.dll `
  -noLogo -reporter default `
  -method 'Mahjong.LocalHost.Tests.LocalHostCandidateAcceptanceTests.ClientPassOnRonSetsFuritenAndColdRestartKeepsItUntilTheNextDraw'
```

Full LocalHost command:

```powershell
dotnet <tree>/dotnet/tests/Mahjong.LocalHost.Tests/bin/Release/net8.0/Mahjong.LocalHost.Tests.dll `
  -noLogo -reporter default
```

Build results:

| Tree | Exit | Warnings / errors | Build log SHA-256 |
|---|---:|---|---|
| `base` | 0 | 0 / 0 | `AF31C13F26F1C1914EB045C6179E2909F46C489A0CB093CF23B81B7817EADE3B` |
| `fix` | 0 | 0 / 0 | `0C3DB1E5B283DD3248FFCD458EF1799C18950760CACD52AF44E852CF18C2BCAB` |
| `base-delay` | 0 | 0 / 0 | `AA0EC6C11BCB26200A85A69C0E7F1B4F19E5057D805405EB26989B92E5AAA9FE` |
| `fix-delay` | 0 | 0 / 0 | `A27B34E57AB194A83E5F0128E3C44F8B3DF344F507F5AFA1B8E8232107E7CD49` |
| `mut-nostop` | 0 | 0 / 0 | `DA8A1C5C46125896DDF1AF51E5821D4AB1B1D9639F24B2412D2A7A26773B3EC1` |
| `mut-persist` | 0 | 0 / 0 | `618C0B2919B56E6F432C951DA24E73027778B538A42EA09D1557B64DF0E84177` |
| `mut-clear` | 0 | 0 / 0 | `3A97A58E45C7E9D33E88545C12BD41753B08F736551B58A4482F65ED62CD3D20` |

The patched build log is `logs-fix-build.txt`; it ends with `0 个警告 / 0 个错误`, 43.28 s.

---

## 5. Results matrix

| # | Tree / variant | Runs | Exit | Result | Key counts / failure |
|---|---|---:|---:|---|---|
| R1 | pristine `base` | 20 | 0 | **PASS 20/20** | every run `Total: 1, Failed: 0, Skipped: 0`; 1.30–1.50 s |
| R2 | patched `fix` | 20 | 0 | **PASS 20/20** | every run `Total: 1, Failed: 0, Skipped: 0`; 1.35–1.63 s |
| R3 | `base-delay` (pristine + 750 ms) | 3 | 1 | **FAIL 3/3** | `Assert.True()` Expected True / Actual False; line **1065** |
| R4 | `fix-delay` (patched + 750 ms) | 10 | 0 | **PASS 10/10** | every run `Total: 1, Failed: 0, Skipped: 0`; 2.10–4.30 s |
| R5 | `mut-nostop` (patched, stop removed + 750 ms) | 3 | 1 | **FAIL 3/3** | `Assert.Equal()` Expected 5 / Actual 7; line **1058** |
| R6 | `mut-persist` (cold recovery drops seat-0 bit) | 3 | 1 | **FAIL 3/3** | `cold restart lost the committed furiten fact`; line **1108** |
| R7 | `mut-clear` (next-draw clearing removed) | 3 | 1 | **FAIL 3/3** | `the next draw must clear temporary furiten`; line **1114** |
| R8 | patched full LocalHost suite | 1 | 0 | **PASS 115/115** | `Total: 115, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0`; 29.09 s |

Summary: **50/50 expected passing targeted runs passed**; **12/12 expected failing control runs
failed for the intended assertion**; **115/115 full-suite tests passed**. There was also one
pre-matrix patched smoke run (`logs-fix-run1.txt`, exit 0, `Total: 1, Failed: 0`).

Focused result CSVs:

| Matrix | File | SHA-256 |
|---|---|---|
| R1 | `base-20-runs/results.csv` | `A1D9A02FD5C5C001670AF7C6BB7350815E908D9F7F38DB0BA77756583629CBA4` |
| R2 | `fix-20-runs/results.csv` | `AAE8CFB54ACBBA3F8FD8E6570CD710225006EDE8567DACEA494178C885ECB6ED` |
| R3 | `base-delay-runs/results.csv` | `79AADFCF4B1397CFD169D80180EE8AE0C1F4B067DAA015D6BCEE5B0562062E38` |
| R4 | `fix-delay-runs/results.csv` | `32D006865B05C787F9EC87A44AC70DB3BE84DD195D983FAB5DE3AC54FE8D7A8B` |
| R5 | `mut-nostop-runs/results.csv` | `FBD70BF2E76CDC216013226B2A77BF974D61E55D59B0F25A7484D38D6499AE4F` |
| R6 | `mut-persist-runs/results.csv` | `FBD70BF2E76CDC216013226B2A77BF974D61E55D59B0F25A7484D38D6499AE4F` |
| R7 | `mut-clear-runs/results.csv` | `FBD70BF2E76CDC216013226B2A77BF974D61E55D59B0F25A7484D38D6499AE4F` |

R5–R7 have identical CSV bodies (`run,exit,total,failed,skipped` with three failing rows), hence
the identical hash; the run logs distinguish the failures.

Full-suite log: `logs-fix-full-suite.txt`, SHA-256
`CA1CE86F5426ED24EC692E703ED614E392A27F44DEC96A5C63BBE956A8D7FDCA`.

---

## 6. Negative and mutation probes

All mutations exist only in ignored `build/` copies. They are **not** part of the delivered
patch and must not enter the candidate or production. Each failing run was repeated three times;
all three failed the same way.

### 6.1 R3 — pristine candidate plus 750 ms (race reproduction)

After the forged Ron is rejected, before `pumpCts.Cancel()`:

```csharp
await Task.Delay(750, pumpCts.Token);
```

Observed 3/3:

```text
Assert.True() Failure
Expected: True
Actual:   False
dotnet\tests\Mahjong.LocalHost.Tests\LocalHostCandidateAcceptanceTests.cs(1065,0)
```

This is the same assertion as the accepted report's E2 and confirms that the original test can
read a post-draw `false` after the pass.

### 6.2 R5 — stop barrier removed plus 750 ms

The whole `StopAsync()`/`DriverRunning`/version block before the pass was replaced by a comment, and
the following was added after the forged-Ron rejection:

```csharp
await Task.Delay(750, pumpCts.Token);
Assert.Equal(versionAfterPass, session.PlayerSnapshot!.StateVersion);
```

Observed 3/3:

```text
Assert.Equal() Failure: Values differ
Expected: 5
Actual:   7
...LocalHostCandidateAcceptanceTests.cs(1058,0)
```

This shows the stop barrier is the necessary step, not a faster read: without it, the driver
advances the committed state from 5 to 7 and the new boundary assertion fails before the store is
read.

### 6.3 R6 — cold recovery drops the persisted seat-0 furiten bit

In `dotnet/src/Mahjong.TableRuntime/TableActor.cs`, the recovery path was changed from:

```csharp
_round = baseline.Round;
InstallFuriten(FuritenFromProjectionFacts(baseline.Round, baseline.ProjectionFacts));
```

to:

```csharp
_round = baseline.Round;
// MUTATION: simulate a lost persisted temporary-furiten bit on recovery.
InstallFuriten(FuritenFromProjectionFacts(baseline.Round, baseline.ProjectionFacts)
    .WithTemporary(0, false));
```

Observed 3/3:

```text
cold restart lost the committed furiten fact
...LocalHostCandidateAcceptanceTests.cs(1108,0)
```

This proves the cold-restart assertion is not a tautology: it fails when the restored committed
fact is lost.

### 6.4 R7 — next-draw clearing removed

In `TableActor.AdvanceFuriten`, the two clearing writes were commented out:

```csharp
next = next.WithTemporary(committed.Seat, false);
next = next.WithTemporary(seat, false);
```

Observed 3/3:

```text
the next draw must clear temporary furiten
...LocalHostCandidateAcceptanceTests.cs(1114,0)
```

This proves the negative direction (the next draw must clear) is still enforced by real behavior.

Mutation source hashes (ignored copies only):

| File | SHA-256 | Blob |
|---|---|---|
| `mut-nostop/.../LocalHostCandidateAcceptanceTests.cs` | `8700BFF91E6FB5B4D86061C819D860439F85C9939DCFBEAE457EFBB56C2F7B09` | `daf14c2...` |
| `mut-persist/.../TableActor.cs` | `2F494FCD50061407CA32C7A23FC93C4DBC183D1275A43949CDF48FD91B00D50A` | `394913e...` |
| `mut-clear/.../TableActor.cs` | `076A39D3C1BD21EAE066ABE9B4CE3E1B7CA0CB944148BF26F8DDCFCCBE1474FC` | `a53140d...` |
| patched `TableActor.cs` (unmodified production source) | `1BD0BC8DA981BBABD4D98CECDF87CF44A5926B04060B86C7791446055100A076` | `2b819e7...` |

`mut-nostop`'s blob differs from the accepted report's `ce3dfaa` only because this reproduction
preserved CRLF on the patched tree; the textual mutation is the accepted report's E5.

---

## 7. Key log hashes

All logs are under ignored
`build/furiten-test-fix-20261030b/`; no log is committed.

| Log | SHA-256 |
|---|---|
| `logs-base-build.txt` | `AF31C13F26F1C1914EB045C6179E2909F46C489A0CB093CF23B81B7817EADE3B` |
| `logs-fix-build.txt` | `0C3DB1E5B283DD3248FFCD458EF1799C18950760CACD52AF44E852CF18C2BCAB` |
| `logs-fix-run1.txt` | `D4D703975EF2C56334734A49731AF527CBEF76EB0E95D2E050A57B450F040C83` |
| `logs-fix-full-suite.txt` | `CA1CE86F5426ED24EC692E703ED614E392A27F44DEC96A5C63BBE956A8D7FDCA` |
| `base-delay-runs/run-1.log` | `5F94435A610752AAFCE07F4781F05E4FCFAA60CB075E0EAF1F294C21266FBC17` |
| `fix-delay-runs/run-1.log` | `FB5E78ED6692B6F6AD1C7AAFEC3CA3562E70C8AB2322BF3CADD0093669D3FB6C` |
| `mut-nostop-runs/run-1.log` | `FAF42A626FCD262E5DE104A9013606AB067FAD5BDDE5A2C084B632433556AC2A` |
| `mut-persist-runs/run-1.log` | `CD3CC72826A68431B4D1C3665BE3FE0136001000A584C16E91D6EC9EAFB0BFB6` |
| `mut-clear-runs/run-1.log` | `BBA10BFD2D2A1AD2F721A40AF8D862F24D3DD759CB9415FFDF795A38640F90B4` |

The other 19 patched focused logs and all 20 pristine logs are retained in the same ignored
directories.

---

## 8. Boundaries and unknowns

1. **Historical CI interleaving is UNKNOWN/BLOCKED.** The CI failure's exact save,
   `stateVersion`, and driver tick were not available. No CI run was triggered, canceled, queried,
   or rerun. R3 reproduces the mechanism on this host; it does not prove the CI run's exact
   interleaving, nor does it prove CI is fixed.
2. **No claim that the original test is deterministic.** Pristine 20/20 only says this host did not
   hit the race in those runs. R3 is the control that makes the race observable.
3. **No remote CI, full-solution, container, single-core, or other-OS validation.** Results are
   Windows 10, .NET 8.0.426, 16 logical CPUs, local NTFS.
4. **No Unity/Android/iOS/PG/WSS/cross-physical-network work.** Those are NOT_RUN and outside this
   task.
5. **No production behavior change.** `AutoPlay` remains `true` by default; the patch only controls
   it inside the test. The production mutations in §6 exist only in ignored probe copies.
6. **Stop-barrier precondition.** `StopAsync()` is safe here because seat 0 has an open human Ron
   decision and the driver cannot advance past it. If future driver semantics allow autonomous
   progress during a human decision, the barrier must be re-reviewed.
7. **Fixed-wall dependency.** The test depends on the candidate's fixed wall. If the wall or seat
   order changes, the "human decision blocks the driver" precondition must be rechecked.
8. **750 ms is a diagnostic magnitude, not a CI measurement.** R4 shows the patch is not
   winning a race by reading faster; it is not a claim about the CI runner's actual delay.
9. **PR20 and gates unchanged.** The candidate remains unaccepted; G2–G5 are not approved; V4 is
   still the only production baseline.

---

## 9. Deliverables

Exactly two committed files:

1. `doc/v5-重构/patches/V5-LOCALHOST-FURITEN-RACE-REMEDY-TEST-FIX-20261030.patch`
   — SHA-256 `DED09A4337CEB04E15B7642255EDB6C66A987E61997A00F9E382AB3A62623B7D`.
2. `doc/v5-重构/V5-LOCALHOST-FURITEN-RACE-REMEDY-TEST-FIX-20261030.md`
   — this report.

No production source, verifier, solution, CI workflow, Unity asset, trust setting, PR, remote, or
other worktree was changed. The candidate was extracted and tested only under the ignored
`build/furiten-test-fix-20261030b/` tree.

**Caller action:** independently review the patch, apply it to the `00cd02d` candidate batch, and
rerun the focused and full LocalHost tests. Do not infer PR20, G2–G5, or production acceptance from
this report.
