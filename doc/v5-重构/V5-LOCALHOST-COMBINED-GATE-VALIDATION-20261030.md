# V5 LocalHost Combined Gate — Independent Validation (2026-10-30)

Status: **independent validation report only. PR20 remains unaccepted. This is not a PR20 PASS,
not a G2–G5 approval, and not a production acceptance. V4 remains the production baseline.**

- Accepted base: `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7` (tree
  `61f50856aedd78e0f8609e282baa8313ae8f2a14`).
- Unaccepted PR20 head: `acdc275d6c2b321ac66ff8994737b02d7bdd11db` (tree
  `d8d0a244b00cc9b233ce601feaa880c823644560`).
- Frozen candidate implementation: `00cd02d80172ebd13a2945e4e8bacfe486ae0c21`
  (tree `72502a19a808738f9e2c801f83e47d46dc07761d`, parent
  `2bb6e681c26060407542f09bc2495c4b30b2c7c0`).
- The two test-only patches from `acdc275` were applied byte-exact to the exact LF archive of
  `00cd02d`; no production or verifier source was edited by this task.
- All validation ran in isolated, ignored `build/localhost-combined-gate-20261030/revalidation-20261030/`.
  Nothing there was merged, staged, or committed.

## 0. Verdict

| Item | Result |
|---|---|
| Exact archive of `00cd02d` | **PASS** — `git archive` SHA-256 `BF799A62…`, 2,332 files |
| Patch SHA-256 and scope | **PASS** — both patch files match their accepted hashes; 1 + 3 test files; no `dotnet/src` path |
| Clean applicability | **PASS** — `git apply --check --whitespace=error-all` exit 0 for both, in both orders |
| Postimage | **PASS** — every postimage blob matches the patch index; final combination blob is exactly `e6779677…` |
| Production/test assertion preservation | **PASS** — only three test files modified; `dotnet/src` and the verifier are byte-identical to the candidate; no caller assertion text lost; no skip/retry added |
| Focused patched regressions | **PASS** — 9/9 runs (`1 / 0 / 0` each), native exit 0; the failing test also passed 5/5 in isolation |
| LocalHost full Release `dotnet test` | **FAIL (intermittent)** — 10 runs: run 1 failed `115 total / 114 passed / 1 failed / 0 skipped`, native exit 1; runs 2–8 and two additional cold/`--no-restore` runs passed `115 / 0 / 0`, native exit 0. Failure: `RonCandidateAndSubmissionTest` → `ron rejected: STALE_STATE_VERSION` at line 865 |
| Unchanged unified verifier | **PASS** — `OVERALL: PASS`; 14 test projects, 2,641/2,641 tests, 0 failed, 0 skipped, 0 not-run; 20 project rows PASS; companion 4/4 PASS; verifier exit 0 |
| Real PostgreSQL test stages | **PASS** — 16.10 disposable loopback clusters; 42/42 Infrastructure, 13/13 targeted, crash/recovery/dump/restore PASS |
| PG wrapper exit | **FAIL at cleanup only (exit 1)** — all verification stages PASS; the wrapper's `Remove-Item` hit a Windows long-path limit and left the stopped cluster on disk; manual owned cleanup then PASS |
| Overall candidate gate | **NOT PASS / REWORK** — the two patches are clean and their targets pass, but the combined candidate does not have a stable full LocalHost suite |
| PR20 / G2–G5 | **NOT APPROVED** — this report does not accept PR20, the LocalHost batch, or any gate |

The LocalHost full-suite failure is real and is retained. Subsequent clean runs were used only to
characterize it; they do not erase the first failure. The failure is **not** in either patched test
hunk: it is the unpatched `RonCandidateAndSubmissionTest` timing path described in §5.1. A later
caller recheck of the existing `combo` reported 115 passed / 0 failed / 0 skipped, exit 0; that
observation is recorded in §5.1 but is not one of this report's 10 runs and does not erase run 1.

## 1. Inputs and identity

The candidate was extracted only with `git archive` into the ignored `build/` tree:

```powershell
git -c core.eol=lf -c core.autocrlf=false archive --format=tar `
  -o build/localhost-combined-gate-20261030/revalidation-20261030/pr20-00cd02d-lf.tar 00cd02d
tar -xf build/localhost-combined-gate-20261030/revalidation-20261030/pr20-00cd02d-lf.tar `
  -C build/localhost-combined-gate-20261030/revalidation-20261030/candidate
```

| Item | Value |
|---|---|
| Candidate archive | `pr20-00cd02d-lf.tar` |
| Archive SHA-256 | `BF799A62E8F83B887D860171C209E1E1F52314770E2C87DAFFA9404539A22B99` |
| Archive files | 2,332 |
| `00cd02d` tree / parent | `72502a19a808738f9e2c801f83e47d46dc07761d` / `2bb6e681c26060407542f09bc2495c4b30b2c7c0` |
| `acdc275` tree | `d8d0a244b00cc9b233ce601feaa880c823644560` |

The exact archive has no `.git`. An isolated local seed (`a781fe93b43af7feb0136db20b6b5b98c73f992e`,
branch `master`) was created **inside the ignored archive only** so `git apply` and the verifier's
run-identity record could work. That seed is not a source commit and does not change the candidate
identity. The archive itself, its tree, and the per-file patch preimages are the authoritative
identity.

The ambient shell had `CI=1` (and no `GITHUB_ACTIONS`), so the verifier recorded `ci provenance:
CI=1`. **No remote CI run, trigger, rerun, or API query occurred.** This was a local run with an
inherited environment variable; the report does not claim CI.

## 2. Patch integrity, clean applicability, postimage

Both patch files were materialized from the exact blobs in `acdc275`:

| Patch | Blob | Bytes | SHA-256 |
|---|---|---:|---|
| `V5-LOCALHOST-FURITEN-RACE-REMEDY-TEST-FIX-20261030.patch` | `c9a34e47c888e6ee965c6b2c471c4f0ab0acb0cf` | 6,349 | `DED09A4337CEB04E15B7642255EDB6C66A987E61997A00F9E382AB3A62623B7D` |
| `V5-LOCALHOST-CALLER-113-115-TWO-FIXES-20261030.patch` | `894cf43070be93d1331c0226fb5b1bb112c0b0f3` | 15,194 | `500AB2301394B96B82366BD523F1239DAB5B6877979CF29EC7DAEF60CABA7BE1` |

Both are LF-only (0 CRLF bytes) and both expected SHA-256 values match the accepted reports.

### 2.1 Clean applicability

| Order | Check | Apply | Scope |
|---|---|---:|---|
| Furiten patch first, then caller patch | 0 for each | 0 for each | 1 file, `+28/−8`; then 3 test files, `+179/−62` raw |
| Caller patch first, then furiten patch | 0 for each | 0 for each | same final blobs in both orders |

All checks used `git apply --check --whitespace=error-all` on the exact LF archive. No whitespace
error, reject, or fuzz was accepted.

### 2.2 Postimage

| File | Furiten-only postimage | Caller-only postimage | Final combination |
|---|---|---|---|
| `LocalHostCandidateAcceptanceTests.cs` | `0bc67c8558f469b0792bf9f07dbd7dc5e28862f1` | `e6cf08a8cc1d820e49f3f2005a4d199106a0ed03` | `e6779677ff56ab3eb5a80f2d04015af9472fdf44` |
| `LocalControlTests.cs` | unchanged | `e1b9f7a985cdf305abebe8bd70a5b83ab49fd88d` | `e1b9f7a985cdf305abebe8bd70a5b83ab49fd88d` |
| `P41Harness.cs` | unchanged | `273bba4b9ad8d865774948e5bf88a1a432553e88` | `273bba4b9ad8d865774948e5bf88a1a432553e88` |

Final combination file SHA-256 (LF): `LocalControlTests.cs`
`C3B8DC465987A1CAA572F5E1B221B098EFCA07DD7AA38F261AD61FCAFCFB1AB5`;
`LocalHostCandidateAcceptanceTests.cs`
`A387C0E06BF13AE3C83AE2BB343FDEDFC210BF3652C6797EC214645806C92A2A`;
`P41Harness.cs`
`B602886C58948DF5F605B77AA5C518010ABBFAC73EAAAB247C5E38BBB4C809E5`.

Caller-only LF hashes match the accepted report exactly:

| File | LF SHA-256 |
|---|---|
| `LocalControlTests.cs` | `C3B8DC465987A1CAA572F5E1B221B098EFCA07DD7AA38F261AD61FCAFCFB1AB5` |
| `LocalHostCandidateAcceptanceTests.cs` | `2DEDEB9246D8943169BC5D2F6EE867E2C9EFE21B8B4785A039C8D31FCE0366B2` |
| `P41Harness.cs` | `B602886C58948DF5F605B77AA5C518010ABBFAC73EAAAB247C5E38BBB4C809E5` |

**Line-ending note.** The canonical LF furiten postimage has blob
`0bc67c8558f469b0792bf9f07dbd7dc5e28862f1` and LF SHA-256
`1DCEB467C6A7C37C8975296FBDA756642BF7552DE025F72C8E458F3054400C3F`. The accepted furiten report's
`448EE73F5702232447BFF6471EFAEE7C2D5D2B0786822749EDD63916EBF52BE4` is the SHA-256 of the **CRLF
working-tree form** of the same canonical blob. Re-applying the patch to a CRLF-materialized copy
reproduced `448EE73F…` while `git hash-object` still normalized it to `0bc67c8…`. This is a
line-ending representation difference, not a content mismatch. The exact LF archive used here is
what makes the two accepted patches apply cleanly.

## 3. Production and assertions untouched

After both patches, the isolated tree reports exactly:

```text
 M dotnet/tests/Mahjong.LocalHost.Tests/LocalControlTests.cs
 M dotnet/tests/Mahjong.LocalHost.Tests/LocalHostCandidateAcceptanceTests.cs
 M dotnet/tests/Mahjong.LocalHost.Tests/P41Harness.cs
```

`git status --porcelain -- dotnet/src` is empty. The production `Mahjong.TableRuntime/TableActor.cs`
is byte-identical to the candidate:

| File | Blob | SHA-256 |
|---|---|---|
| `dotnet/src/Mahjong.TableRuntime/TableActor.cs` | `2b819e789484ffa0bf73b4adc8cbda342825f896` | `1BD0BC8DA981BBABD4D98CECDF87CF44A5926B04060B86C7791446055100A076` |

The unified verifier is unchanged relative to the `00cd02d` candidate: `tools/verify-v5.ps1` blob
`0c0d815393886e9f31fa6eec89184632ef28e03f`, SHA-256
`363A398D9CEFCA1C7F2BA5AC5DD2B7989BD6EDE49812C6E1E9BA487CAD7EDE66`.

Assertion preservation:

| File | `Assert.` lines before → after | `[Fact]/[Theory]` before → after | Pre-existing assertion text lost |
|---|---|---|---|
| `LocalControlTests.cs` | 414 → 420 | 23 → 23 | 0 |
| `LocalHostCandidateAcceptanceTests.cs` (caller patch alone) | 270 → 270 | 17 → 17 | 0 |
| `P41Harness.cs` | 0 → 0 | 0 → 0 | 0 |

The caller patch's raw `−7/+13` `Assert.` lines are a move/reindent plus six net new assertions;
exact trimmed-text comparison found zero lost assertion text. The furiten patch is the accepted
remedy and was not edited: it replaces the old `StateVersion >= versionAfterPass` check with an
exact `Assert.Equal`, moves one `Assert.NotNull` to the new scope, and adds the stop/cold-read
boundary assertions. That is its accepted content, not a change made by this validation. No
`Skip=`, retry attribute, deleted `[Fact]`, or deleted `[Theory]` was introduced.

## 4. Environment

| Item | Value |
|---|---|
| OS | Windows 10 Pro 10.0.19045, 16 logical processors |
| .NET SDK / host | `8.0.426` / `8.0.32` |
| dotnet path | `D:\Unity\dotnet\dotnet.exe` |
| xUnit / runner | xUnit.net v3 4.0.2 with Microsoft.Testing.Platform, direct in-process runner |
| Git | `2.51.0.windows.2` |
| LocalHost build | Release, isolated `bin/obj`, no shared outputs |
| PostgreSQL | 16.10 official EDB binaries, SHA-256-pinned, loopback-only, trust auth |
| PG ports | 55612 (table cluster) and 55613 (lobby cluster), both owned and disposable |

## 5. Test results

### 5.1 LocalHost full Release `dotnet test`

Build:

```powershell
dotnet build dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj -c Release --nologo
```

| Step | Native exit | Warnings / errors | Time | Log SHA-256 |
|---|---:|---|---:|---|
| Release build | 0 | 0 / 0 | 33.36 s | `87E5FDB4C5AE7406ED7C1D9E0C9DA4C30230C39C27FA54E6292E1481F5CB1D80` |
| Release rebuild (`--no-incremental`) | 0 | 0 / 0 | 18.02 s | `96ADC1AF17463B93C79DE8B47E786D9F22FFE9B6E13A314149486F1BF08AC387` |

Full suite, caller-equivalent `dotnet test`:

| Run | Command | Native exit | Total | Passed | Failed | Skipped | Duration | Log SHA-256 |
|---|---|---:|---:|---:|---:|---:|---:|---|
| 1 | `dotnet test … -c Release --no-restore` (cold after build) | **1** | 115 | 114 | **1** | 0 | 30s 673ms | `80461329F2CBF68EF89CE521F34CA4EDEA8B63FE210FDE931D8E3B69C5580056` |
| 2 | `dotnet test … -c Release --no-build --no-restore` | 0 | 115 | 115 | 0 | 0 | 27s 378ms | `AC918FEF573F6BDBA44E1A279BFF580D3DA8565C41771306F6D9B3A1FD392B21` |
| 3 | same | 0 | 115 | 115 | 0 | 0 | 27s 573ms | `D1323AB72F40872BA3AE475D0ABA2526D4C54497F6156E43556F337DEA75A888` |
| 4 | same | 0 | 115 | 115 | 0 | 0 | 28s 584ms | `0C30478CF0B65E278A8CC748A5F53A8234A162F4DC62988A64F529B9560A4E07` |
| 5 | same | 0 | 115 | 115 | 0 | 0 | 29s 062ms | `48DC23E5DF9667F988941DC89E3746D2DBA65592CE60B81890CD38A90280B00D` |
| 6 | same | 0 | 115 | 115 | 0 | 0 | 28s 177ms | `FC5739B8390B20F8568EE932E891D7E1619B0600F37F6ECB7239FB613E83CEC3` |
| 7 | same | 0 | 115 | 115 | 0 | 0 | 28s 394ms | `4374F6BBFBE046B6806CBDB024A7D0AD25338501A08A603A92E4B29207AE1188` |
| 8 | same | 0 | 115 | 115 | 0 | 0 | 28s 746ms | `8D62FB376AC457F69CB5B1A8724F82D6C98A451D5E1A7484AF919C0520BD8F3F` |
| cold | after `--no-incremental` rebuild, `--no-build --no-restore` | 0 | 115 | 115 | 0 | 0 | 28s 543ms | `337EFE7D3EA0FAC97BDF3C792B5695451EB1B0FAC6760E9C72B07CB979380915` |
| cold 2 | `dotnet test … -c Release --no-restore` (after warm build) | 0 | 115 | 115 | 0 | 0 | 31s 715ms | `8DFD49466B9F481232FDFB32168F5B5A3CD44930B4470AED635DDBB1074FB354` |

**Failure (run 1), retained.** `Mahjong.LocalHost.Tests.LocalHostCandidateAcceptanceTests.RonCandidateAndSubmissionTest`
failed in 338 ms with:

```text
ron rejected: STALE_STATE_VERSION
  at Mahjong.LocalHost.Tests.LocalHostCandidateAcceptanceTests.RonCandidateAndSubmissionTest()
     ...LocalHostCandidateAcceptanceTests.cs:865
```

The console log records `失败: 1，通过: 114，已跳过: 0，总计: 115`; the command's native exit was 1.
There were **0 skipped and 0 not-run** cases in every full run.

**Later caller recheck (recorded, not this report's run).** The caller reports one additional Release
`dotnet test` on the existing `combo` with **115 passed / 0 failed / 0 skipped, native exit 0**. No
log for that invocation was supplied to this report; it is retained as a later observation and does
not erase, replace, or explain away the run-1 failure above.

**Evidence limitation.** The raw xUnit v3 `TestResults` file for run 1 was overwritten by a later
full-suite run before it could be copied. The console log and `failure-transcript.txt` preserve the
failure, but the transcript is explicitly **not** the raw framework file. The loss is reported rather
than hidden.

### 5.2 Investigation of the failure

The failure is a timing-sensitive test/authority interaction, not one of the patched targets:

- The test begins at `LocalHostCandidateAcceptanceTests.cs:774`. It waits for a `DeclareRon`
  decision, then does `await Task.Delay(150, …)` (`:835`), rereads the live `session.PlayerSnapshot`
  (`:836`), and builds the command with `new StateVersion(session.PlayerSnapshot!.StateVersion)`
  (`:854`). It then asserts `ReceiptStatus.Applied` at `:865`.
- `P41Harness` does not set `AutoPlay = false` for this test. `LocalTableHostOptions.AutoPlay`
  defaults to `true` (`Mahjong.LocalHost/LocalTableHost.cs:61`), and `LocalTableRuntime` starts the
  driver at construction (`LocalTableRuntime.cs:89-92`).
- `TableActor` rejects a command whose expected version is not current with
  `STALE_STATE_VERSION` (`Mahjong.TableRuntime/TableActor.cs:2458-2463`). That is the observed
  reason code.
- Neither accepted patch touches this test: the furiten hunk starts at line 903; the caller hunks
  are at lines ~1201 and ~1743. The failing test is at line 773. The same code exists in the
  pristine `00cd02d` candidate.
- The single test passed **5/5** when run alone (`1 / 0 / 0`, exit 0), and 9 subsequent full runs
  passed. The failure is therefore intermittent, consistent with the live-version read after the
  delay; no deterministic production defect is proven. It is retained as a candidate-level
  stability failure and requires a separate test-only remedy (for example, the same explicit
  driver/version freezing used by the accepted furiten remedy) before the suite can be called green.

No retry was used to erase the failure. The subsequent runs are recorded above as characterization.

### 5.3 Focused regressions

Direct DLL runner (`Mahjong.LocalHost.Tests.dll -noLogo -reporter default -method "<FQN>"`):

| Test | Runs | Each exit | Each counts | Result |
|---|---:|---:|---|---|
| `ClientPassOnRonSetsFuritenAndColdRestartKeepsItUntilTheNextDraw` (approved furiten target) | 3 | 0 | `1 / 0 / 0` | PASS 3/3 |
| `ClientPassFuritenBlocksSecondRealRonUntilTheNextDraw` (worker-host fix) | 3 | 0 | `1 / 0 / 0` | PASS 3/3 |
| `ExpiredOrRevokedScopeCannotPersistRejectionOnEitherCommandPath` (hold-through-read fix) | 3 | 0 | `1 / 0 / 0` | PASS 3/3 |
| `RonCandidateAndSubmissionTest` (characterization only, not a fix) | 5 | 0 | `1 / 0 / 0` | PASS 5/5 |

The two caller-reported failures (`furiten-read worker exit=3` and `Expected: 1 / Actual: 2`) did not
occur with both patches applied. The focused 9/9 result does not change the full-suite failure.

### 5.4 Unchanged unified verifier

Command run through the CI-equivalent real-PostgreSQL wrapper (the wrapper invokes
`tools/verify-v5.ps1` unchanged, with `-RunnerMode direct`, project discovery and plugin-copy check
enabled):

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File <candidate>/tools/ci/verify-v5-table-postgres.ps1 `
  -Configuration Release -RunUnifiedVerification -Port 55612 -LobbyPort 55613 `
  -Root <candidate>/artifacts/p52-pg-cluster-revalidation-20261030 `
  -PgZipPath D:/majiang-pg-tmp/cache/postgresql-16.10-1-windows-x64-binaries.zip `
  -LogDir <evidence>/pg-table-wrapper -UnifiedLogDir <evidence>/unified
```

| Project | Total | Failed | Skipped | Result |
|---|---:|---:|---:|---|
| `Mahjong.TableRuntime.Tests` | 453 | 0 | 0 | PASS |
| `Mahjong.Domain.Tests` | 529 | 0 | 0 | PASS |
| `Mahjong.Ai.Tests` | 444 | 0 | 0 | PASS |
| `Mahjong.Client.Tests` | 182 | 0 | 0 | PASS |
| `Mahjong.Unity.TableModel.Tests` | 140 | 0 | 0 | PASS |
| `Mahjong.Client.Wss.Tests` | 82 | 0 | 0 | PASS |
| `Mahjong.Architecture.Tests` | 132 | 0 | 0 | PASS |
| `Mahjong.TableService.DurableHost.Tests` | 59 | 0 | 0 | PASS |
| `Mahjong.P5.4b.B1.HttpsAdmission.Tests` | 54 | 0 | 0 | PASS |
| `Mahjong.LobbyService.Tests` | 89 | 0 | 0 | PASS |
| `Mahjong.Contracts.Tests` | 150 | 0 | 0 | PASS |
| `Mahjong.Infrastructure.Tests` | 42 | 0 | 0 | PASS |
| `Mahjong.TableService.Tests` | 170 | 0 | 0 | PASS |
| `Mahjong.LocalHost.Tests` | 115 | 0 | 0 | PASS |
| **Total** | **2,641** | **0** | **0** | **14 test projects PASS** |

- **20 project rows PASS** (14 test projects + 6 build-only helpers); **0 FAIL, 0 BLOCKED, 0 NOT_RUN**.
- Companion gate: **PASS 4/4** fixtures (`movement-v1`, `shuffle-reversed-v1`, `visibility-v1`,
  `ai-discard-flags-v1`). Historical git pins were `NOT_RUN` by the verifier's documented shallow
  policy; current bytes and embedded provenance were verified offline.
- Solution build: exit 0, 0 warnings / 0 errors. Plugin copy: PASS, 12 required assemblies in a
  scratch directory; tracked Unity assets untouched.
- Verifier `OVERALL: PASS`; verifier native exit 0. `summary.log` SHA-256
  `D7C610082907F26A48797F7DB06EB7D598450E1214527A7497E3E881905A7718`;
  `sln-build.log` SHA-256
  `53106CCA8413A75F944F843B53BF71D1263DD60A2618BE405EDC5C00A6942AC1`;
  `plugin-copy.log` SHA-256
  `4B65380E8758AE01D522653C29336FC8D0B7429B17E987F7350B8D541C75227E`.
- `Mahjong.TableService.DurableHost.Tests` took ~884 s (59/59); it was slow, not hung, and completed
  with exit 0.
- The unified verifier's independent direct run of `Mahjong.LocalHost.Tests` was **115 / 0 / 0**,
  exit 0 (`direct-Mahjong.LocalHost.Tests.log` SHA-256
  `9DB951E23A6A4D4DAB8BDD628FF351625E140369868A7D6AF65E1E469CDCBAD0`; XML SHA-256
  `94FE6F89658B308BDC893F0CC0294EF81FED70AEBD9DA92073E12CF4E4AB361B`). This pass does not erase
  the full-suite failure in §5.1.

### 5.5 Real PostgreSQL stages and cleanup failure

The table wrapper's stages all passed before cleanup:

| Stage | Result |
|---|---|
| cluster | PASS — PostgreSQL 16.10, data directory under the owned candidate `artifacts` root, port 55612 |
| XML gating self-check | PASS — 7 negative XML cases |
| fresh build | PASS |
| Infrastructure tests | PASS — 42/42, 0 failed/skipped/not-run, exit 0 |
| targeted `PostgresTableStoreTests` | PASS — 13/13, exit 0 |
| crash writer | PASS — killed after durable commit, state=1 seq=2 |
| PostgreSQL crash restart | PASS — recovery lines observed |
| crash reader | PASS — terminal receipt Applied, outbox/ledger recovered |
| backup/restore | PASS — `pg_dump`/`pg_restore` into an empty database, 2 events recovered |
| unified verifier | PASS — exit 0 |
| result | PASS — all P5.2 real-PostgreSQL stages passed |

Then the wrapper's **cleanup stage failed** and made the wrapper exit 1:

```text
[p52-pg] cleanup FAIL: Could not find a part of the path 'authorization_server.py'.
[p52-pg] stage cleanup: FAIL (exit 1) Could not find a part of the path 'authorization_server.py'.
P52-TABLE-POSTGRES-FAIL
```

Investigation:

- The PostgreSQL server had already stopped (`pg-cleanup-stop.log`: server shut down); ports
  55612/55613 were not listening and no matching `postgres.exe` remained.
- The wrapper's `Remove-Item -Recurse` was deleting the EDB package's bundled
  `pgsql/pgAdmin 4/python/...` tree. The owned cluster root contained **19,785 leftover entries**;
  **2,755 paths were at least 260 characters**, with a maximum of **316** characters. The named file
  `...\authlib\integrations\django_oauth1\authorization_server.py` is exactly 260 characters, above
  the Windows `MAX_PATH` limit for the wrapper's delete path.
- This is a cleanup-path defect in the PG wrapper, not a test, production, or patch failure. It did
  not alter any test result; the verifier had already returned exit 0 and all stages had passed.
- The stopped, owned cluster was then removed manually after re-checking its `pg-owner.json`
  (`owner=mahjong-p52-pg`, exact root, port 55612), the stopped server, and both ports:

```powershell
Remove-Item -LiteralPath "\\?\D:\majiang-wt\localhost-combined-gate-20261030\build\localhost-combined-gate-20261030\revalidation-20261030\combo\artifacts\p52-pg-cluster-revalidation-20261030" `
  -Recurse -Force
# cmdlet completed; root gone; ports 55612/55613 free; no matching postgres.exe
```

Manual cleanup: **PASS** (`manual-pg-cleanup.log` SHA-256
`CB84223632398B136B8D6D5CDB4A71F44A4459CBA9B0D4F9442365C79A3E533A`). The wrapper's exit 1 is
retained in the record and is **not** presented as a passing gate.

## 6. Failures, skips, and not-run scope

| Category | Result |
|---|---|
| LocalHost full-suite failures | **1** — `RonCandidateAndSubmissionTest`, `STALE_STATE_VERSION`, run 1 |
| Later caller recheck (not this report's run) | 115 passed / 0 failed / 0 skipped, exit 0; no log supplied; does not erase run 1 |
| LocalHost full-suite skips / not-run | 0 / 0 in every run |
| Patch-target failures | 0 — focused 9/9 PASS |
| Unified test failures / skips / not-run | 0 / 0 / 0 |
| Verifier companion gate | PASS 4/4 |
| PG wrapper cleanup | **FAIL (exit 1)** — long-path delete; manual cleanup PASS |
| Git-pin historical verification | NOT_RUN (verifier's shallow policy) |
| Unity Editor / PlayMode / IL2CPP / Windows Player | NOT_RUN |
| Android / iOS / physical devices | NOT_RUN |
| Remote CI / PR checks | NOT_RUN — no CI was triggered, rerun, or queried |
| Full Unity/Android solution | NOT_RUN — the unified verifier covers the .NET tree only |

No test failure was suppressed, no test was retried to erase a failure, and no assertion or skip was
changed to obtain green. The LocalHost failure and the PG cleanup failure are both retained.

## 7. Boundaries and non-claims

- No source commit, source edit, PR20 merge, push, PR/MR, CI trigger, trust change, Unity environment
  change, or delegation by this task.
- No other worktree was modified. B1 patch work was neither read into this tree nor edited.
- The candidate production tree is the unaccepted PR20 implementation; the test-only patches change
  no production path.
- This report does not approve PR20, the LocalHost batch, G2, G3, G4, or G5. The caller reviews
  independently.
- The cleanup defect is reported as a residual harness issue; it was not patched here because the
  task is validation-only and requires the unified verifier to remain unchanged.
- The LocalHost full-suite failure is a candidate-level stability finding. It is not attributed to
  either accepted patch without further evidence, and it is not a production-defect proof.

## 8. Evidence

Ignored evidence root:
`build/localhost-combined-gate-20261030/revalidation-20261030/` (not committed). Key files:

| Artifact | SHA-256 |
|---|---|
| `pr20-00cd02d-lf.tar` | `BF799A62E8F83B887D860171C209E1E1F52314770E2C87DAFFA9404539A22B99` |
| furiten patch | `DED09A4337CEB04E15B7642255EDB6C66A987E61997A00F9E382AB3A62623B7D` |
| caller patch | `500AB2301394B96B82366BD523F1239DAB5B6877979CF29EC7DAEF60CABA7BE1` |
| `build-localhost-release.log` | `87E5FDB4C5AE7406ED7C1D9E0C9DA4C30230C39C27FA54E6292E1481F5CB1D80` |
| `dotnet-test-localhost-full-run1.log` (the failure) | `80461329F2CBF68EF89CE521F34CA4EDEA8B63FE210FDE931D8E3B69C5580056` |
| `dotnet-test-localhost-full-run2.log` | `AC918FEF573F6BDBA44E1A279BFF580D3DA8565C41771306F6D9B3A1FD392B21` |
| `dotnet-test-localhost-full-run8.log` | `8D62FB376AC457F69CB5B1A8724F82D6C98A451D5E1A7484AF919C0520BD8F3F` |
| `dotnet-test-localhost-full-cold-after-rebuild.log` | `337EFE7D3EA0FAC97BDF3C792B5695451EB1B0FAC6760E9C72B07CB979380915` |
| `dotnet-test-localhost-full-cold2.log` | `8DFD49466B9F481232FDFB32168F5B5A3CD44930B4470AED635DDBB1074FB354` |
| `evidence/failure-transcript.txt` (transcript, not raw) | `0534BB9B579D1A2BA4A71C5ED32AEB8898EDDBF49EACE85A4420A70E0EEC94BB` |
| `unified/summary.log` | `D7C610082907F26A48797F7DB06EB7D598450E1214527A7497E3E881905A7718` |
| `unified/companion-fixtures.log` | `0434CC3CFE3CBC435F0E9DDEC9D41ABB12D48576E08A0BF7DB38549709FC42D9` |
| `unified/direct-Mahjong.LocalHost.Tests.log` | `9DB951E23A6A4D4DAB8BDD628FF351625E140369868A7D6AF65E1E469CDCBAD0` |
| `pg-wrapper-console.log` | `84EA49562FA8672F23BAE97A965881C29C2F98854FAF450F3FCC3C7521C50FD2` |
| `pg-table-wrapper/wrapper-summary.json` | `5F93871A98EF034D251A317448194C0F9DD5B398F130320A00715496DD1B8EFA` |
| `pg-table-wrapper/tests.xml` | `E577FCFA8567BC1443D53CC332B20D2C15B341E5F56C37BE7378F21EFBEC7573` |
| `pg-table-wrapper/tests-targeted.xml` | `7E6F16C65462EF6FE7B294A95081AECDDBE367CEC11892D50B8A2621010E9A80` |
| `evidence/leftover-pg-owner.json` | `84578B3B9B41E345FF754D47D89A57DBC5BEB509A546ABEEAFE0099B3DA18680` |
| `evidence/leftover-long-paths.txt` | `2B907A6CAFD363CFBB91565CEDB0FCA62548CAD47824F9109582797260A211BE` |
| `evidence/manual-pg-cleanup.log` | `CB84223632398B136B8D6D5CDB4A71F44A4459CBA9B0D4F9442365C79A3E533A` |
| `evidence/candidate-identity.txt` | `73200D058270DE11488C1C788E51ABE79068CF196C55E278D92298028717E811` |
| `evidence/commands.txt` | `F74B46BE663E01249C13840CD8BAA844257DBCDFD9A6E14718A7D58A5BFD549B` |
| `SHA256SUMS.txt` (65 entries) | `7B69E3B7ABED594DE4849FF2AE235CB34EFCC346FB55BEC6787541DFEB67A42E` |
| `repro-20261030/commands.log` (fresh §9) | `6BE679BF8B535DD887B1ABAE4B4325B522235EABCF88C8626B7A8F68249BD195` |
| `repro-20261030/release-build.log` | `F7204D681CE17D5656739DDDD1641A565F683064C2C5C36BE006FF75866EFA5B` |
| `repro-20261030/release-test.log` | `847E14C5849CB9E580111F354BCD19B62B13E8C1C45C8F23614A2FE995D96700` |

The 65-entry manifest covers the original stable evidence files; it excludes transient compiler temp
files and the failed dump attempt. The fresh §9 logs above are separate from that manifest. The exact
archive and both patches remain under the ignored evidence root.

## 9. Reproduction summary

Run the whole block from the worktree root
(`D:\majiang-wt\localhost-combined-gate-20261030` or the checkout root). It creates a fresh
`repro-20261030` tree, extracts the exact `00cd02d` archive twice (pristine `candidate` plus working
`combo`), creates a local-only git seed for `git apply`, applies both patches using **absolute**
patch paths, checks the postimages, and runs the LocalHost Release build/test in `combo`.

Correction note: the earlier draft passed worktree-relative patch paths to
`git -C <combo> apply`; Git resolved them against `<combo>` and exited 128. The script below uses
absolute `$patch1`/`$patch2`, creates `combo` explicitly, and runs the tests from that patched copy.

```powershell
$repo = (Resolve-Path '.').Path
if (-not (Test-Path (Join-Path $repo 'dotnet/Mahjong.sln'))) {
    throw 'Run this block from the worktree root.'
}

$stageRoot = Join-Path $repo 'build/localhost-combined-gate-20261030'
$stage = Join-Path $stageRoot 'repro-20261030'
if (Test-Path -LiteralPath $stage) { throw "Refusing to reuse existing $stage" }
$candidate = Join-Path $stage 'candidate'
$combo = Join-Path $stage 'combo'
New-Item -ItemType Directory -Force -Path $stage, $candidate, $combo | Out-Null

# Absolute patch paths: `git -C $combo apply` resolves a RELATIVE patch path
# against $combo, not against the caller's working directory.
$patch1 = Join-Path $stageRoot 'V5-LOCALHOST-FURITEN-RACE-REMEDY-TEST-FIX-20261030.patch'
$patch2 = Join-Path $stageRoot 'V5-LOCALHOST-CALLER-113-115-TWO-FIXES-20261030.patch'
$archive = Join-Path $stage 'pr20-00cd02d-lf.tar'

# Exact LF archive; extract once for candidate and once for combo.
git -c core.eol=lf -c core.autocrlf=false archive --format=tar -o $archive 00cd02d
tar -xf $archive -C $candidate
tar -xf $archive -C $combo

# Local seed only: gives `git apply` an index; not a source commit.
git -C $combo init -q
git -C $combo config core.autocrlf false
git -C $combo config core.eol lf
git -C $combo config user.name repro
git -C $combo config user.email repro@example.invalid
git -C $combo add -A
git -C $combo commit -q -m 'exact 00cd02d archive'

# Apply both accepted patches with clean applicability checks.
git -C $combo apply --check --whitespace=error-all $patch1
git -C $combo apply --whitespace=error-all $patch1
git -C $combo apply --check --whitespace=error-all $patch2
git -C $combo apply --whitespace=error-all $patch2

# Postimages and production-preservation checks.
git -C $combo hash-object dotnet/tests/Mahjong.LocalHost.Tests/LocalControlTests.cs
# e1b9f7a985cdf305abebe8bd70a5b83ab49fd88d
git -C $combo hash-object dotnet/tests/Mahjong.LocalHost.Tests/LocalHostCandidateAcceptanceTests.cs
# e6779677ff56ab3eb5a80f2d04015af9472fdf44
git -C $combo hash-object dotnet/tests/Mahjong.LocalHost.Tests/P41Harness.cs
# 273bba4b9ad8d865774948e5bf88a1a432553e88
if (git -C $combo status --porcelain -- dotnet/src) {
    throw 'production tree changed'
}
git -C $combo status --porcelain -- dotnet/tests
# Expected: exactly the three modified test files above.

$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
& $dotnet build "$combo/dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj" -c Release --nologo
& $dotnet test "$combo/dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj" -c Release --no-restore
```

**Verified result of the fresh reproduction** (logged `repro-20261030`, 2026-10-30):

| Step | Native exit | Result |
|---|---:|---|
| archive / extract candidate / extract combo | 0 / 0 / 0 | exact LF archive, 2,332 files |
| local init / config / add / commit | 0 / 0 / 0 / 0 | local seed only |
| patch 1 check / apply | 0 / 0 | furiten patch |
| patch 2 check / apply | 0 / 0 | caller patch |
| postimages | — | `e1b9f7a9…`, `e6779677…`, `273bba4b…` |
| `dotnet/src` status | — | empty |
| `dotnet test` status | — | only the three test files modified |
| Release build | 0 | 0 warnings / 0 errors |
| Release `dotnet test` | 0 | **115 passed / 0 failed / 0 skipped**, 28s 608ms |

This fresh LocalHost-only reproduction passed. The exact §9 block was also executed a second time in
a separate `repro-20261030-exact` copy and returned **115 / 0 / 0, exit 0, 29s 667ms** (raw
framework log SHA-256 `11BF5196D6565C47CA129E61B3C53DB719F760B9C0F2FBF14DE93D91D9893763`). Both
are additional clean runs; neither erases the run-1 intermittent failure recorded in §5.1.

Optional full verifier on the same `combo` (same real-PG wrapper as §5.4; the wrapper cleanup
long-path defect in §5.5 still applies):

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File "$combo/tools/ci/verify-v5-table-postgres.ps1" `
  -Configuration Release -RunUnifiedVerification -Port 55612 -LobbyPort 55613 `
  -Root "$combo/artifacts/p52-pg-cluster-repro-20261030" `
  -PgZipPath D:/majiang-pg-tmp/cache/postgresql-16.10-1-windows-x64-binaries.zip `
  -LogDir "$stage/pg-table-wrapper" -UnifiedLogDir "$stage/unified" `
  -DotnetPath $dotnet
```

The full unified run in §5.4 was executed on `revalidation-20261030`, not repeated in this
LocalHost-only `repro-20261030`; the command above is the same wrapper with the fresh paths.

The original run list is preserved at `evidence/commands.txt`; the corrected reproduction transcript
is `repro-20261030/commands.log` (SHA-256
`6BE679BF8B535DD887B1ABAE4B4325B522235EABCF88C8626B7A8F68249BD195`). No source commit, push, PR,
CI, delegation, trust, or Unity change was made by this validation.
