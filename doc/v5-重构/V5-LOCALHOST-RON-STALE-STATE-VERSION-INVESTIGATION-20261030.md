# V5 LocalHost `RonCandidateAndSubmissionTest` `STALE_STATE_VERSION` — Investigation and Test-Only Remedy

Date: 2026-10-30
Status: **investigation complete; a test-only candidate patch is proposed. This is not PR20 acceptance,
not a G2–G5 approval, and not a production acceptance. V4 remains the production baseline.**

- Candidate implementation: `00cd02d80172ebd13a2945e4e8bacfe486ae0c21`
  (tree `72502a19a808738f9e2c801f83e47d46dc07761d`).
- Test-patch base at PR20: `acdc275d6c2b321ac66ff8994737b02d7bdd11db`.
- Delivered local commit base: `8fd7c6c` (accepted G1 baseline).
- Exact LF archive SHA-256: `BF799A62E8F83B887D860171C209E1E1F52314770E2C87DAFFA9404539A22B99`
  (2,332 files).
- Accepted test patches, byte-exact:
  - furiten remedy `DED09A4337CEB04E15B7642255EDB6C66A987E61997A00F9E382AB3A62623B7D`;
  - caller two-fixes `500AB2301394B96B82366BD523F1239DAB5B6877979CF29EC7DAEF60CABA7BE1`.
- Candidate patch: `doc/v5-重构/patches/V5-LOCALHOST-RON-STALE-STATE-VERSION-TEST-FIX-20261030.patch`,
  SHA-256 `65DCF0F6F02D3B0056B1C6F66A2B71D9E102D1659D07505FC2376C1EF499151C`, LF-only.
- All work in this task ran under the ignored isolated root
  `build/localhost-ron-stale-20261030/`. Nothing there was merged, staged, or committed.

## 0. Verdict

| Question | Finding |
|---|---|
| Historical failure | **Proven**: LocalHost full-suite run 1 failed 114/115, exit 1, `RonCandidateAndSubmissionTest` — `ron rejected: STALE_STATE_VERSION` at `LocalHostCandidateAcceptanceTests.cs:865`. The failed run is retained, not erased. |
| Exact historical commit interleaving | **Not proven**. The raw xUnit result was overwritten by a later run; the preserved transcript is not the raw framework file. The precise commit/read order in run 1 is a hypothesis. |
| Failure mechanism | **Proven by code and a deterministic controlled probe**: a client snapshot containing the open ron decision at version 2, with the authority at version 4 and the same pending decision id, is rejected with exactly `STALE_STATE_VERSION`; the same command with version 4 applies. |
| Product defect | **Not proven**. The authority's version fence is correct. A product-level stale-command rejection is *possible in principle* (any optimistic-concurrency client can submit against a not-yet-updated view), but this evidence does not establish a product defect or any state/safety failure. |
| Remedy | **Test-only one-file patch**: stop the autonomous driver after the AI response seats have responded, converge the client projection to the authoritative version, then submit the ron once. No retries, no skips, no production change, no assertion removed. |
| Validation | LF build 0 warnings/0 errors; focused Ron 20/20; full LocalHost suite 5/5 at 115/115, 0 failed, 0 skipped; cold-rebuild full run 115/115; three related regression tests 3/3 each. |
| Unified suite / other gates | **NOT RUN** in this task by instruction. No PR20, G2–G5, CI, Unity, Android, iOS, push, or delegation. |

## 1. Inputs and method

### 1.1 Exact candidate and patches

The candidate was extracted only with `git archive`, in LF form:

```powershell
git -c core.eol=lf -c core.autocrlf=false archive --format=tar `
  -o build/localhost-ron-stale-20261030/pr20-00cd02d-lf.tar 00cd02d
tar -xf build/localhost-ron-stale-20261030/pr20-00cd02d-lf.tar `
  -C build/localhost-ron-stale-20261030/candidate-lf
```

| Input | Bytes | SHA-256 |
|---|---:|---|
| `pr20-00cd02d-lf.tar` | 387,584,000 | `BF799A62E8F83B887D860171C209E1E1F52314770E2C87DAFFA9404539A22B99` |
| furiten test patch | 6,349 | `DED09A4337CEB04E15B7642255EDB6C66A987E61997A00F9E382AB3A62623B7D` |
| caller two-fixes patch | 15,194 | `500AB2301394B96B82366BD523F1239DAB5B6877979CF29EC7DAEF60CABA7BE1` |
| delivered candidate patch | 4,289 | `65DCF0F6F02D3B0056B1C6F66A2B71D9E102D1659D07505FC2376C1EF499151C` |

Both accepted patches were materialized from the exact blobs in `acdc275`, checked with
`git apply --check --whitespace=error-all`, and applied with `core.autocrlf=false` into the LF archive.
The delivered patch was then applied on top. The isolated local seed is not a source commit and does
not change candidate identity.

### 1.2 Blob and file identity

| Item | LF SHA-256 / blob |
|---|---|
| `LocalHostCandidateAcceptanceTests.cs` after the two accepted patches | `A387C0E06BF13AE3C83AE2BB343FDEDFC210BF3652C6797EC214645806C92A2A` (blob `e677967`) |
| `LocalHostCandidateAcceptanceTests.cs` after this candidate patch | `035F2448187DCDFC54C963BA82D59C6AD7DC9810D64A8E77E79D26CA305D5E81` (blob `9f0232a`) |
| `dotnet/src/Mahjong.TableRuntime/TableActor.cs` | `1BD0BC8DA981BBABD4D98CECDF87CF44A5926B04060B86C7791446055100A076` |
| `dotnet/src/Mahjong.LocalHost/LocalTableDriver.cs` | `39719A0DBFA0D0CA72B438C134B752B3059433691F3B174E75C0B7C07F2E2BD9` |
| `dotnet/src/Mahjong.LocalHost/LocalTableRuntime.cs` | `D49E937C647007F101DA2CC8A25F14D17F0603358BC2E54C013B54EB46FAF606` |
| `tools/verify-v5.ps1` | `363A398D9CEFCA1C7F2BA5AC5DD2B7989BD6EDE49812C6E1E9BA487CAD7EDE66` |

`git status --porcelain -- dotnet/src` is empty in the LF validation tree. The verifier is unchanged.
Only test files are modified.

## 2. Proven facts

### 2.1 The historical failure is real and retained

The prior validation evidence (read-only for this task) records:

- `dotnet-test-localhost-full-run1.log` SHA-256
  `80461329F2CBF68EF89CE521F34CA4EDEA8B63FE210FDE931D8E3B69C5580056`;
  native exit 1; `失败: 1，通过: 114，已跳过: 0，总计: 115`; duration 30s 673ms.
- `evidence/failure-transcript.txt` SHA-256
  `0534BB9B579D1A2BA4A71C5ED32AEB8898EDDBF49EACE85A4420A70E0EEC94BB`;
  test failed in 338ms with:

```text
ron rejected: STALE_STATE_VERSION
  at Mahjong.LocalHost.Tests.LocalHostCandidateAcceptanceTests.RonCandidateAndSubmissionTest()
     ...LocalHostCandidateAcceptanceTests.cs:865
```

The raw xUnit v3 `TestResults` file for run 1 was overwritten by a later full-suite run before it
could be copied. The transcript is explicitly **not** the raw framework file. The console log and
native exit 1 independently preserve the failure. No retry or skip is recorded: 0 skipped and 0
not-run in that run.

### 2.2 The failing test's timing path

At `00cd02d`, `RonCandidateAndSubmissionTest`:

- waits for the `DeclareRon` decision (`LocalHostCandidateAcceptanceTests.cs:831–834`);
- sleeps a fixed `await Task.Delay(150, …)` (`:835`);
- rereads the live mutable `session.PlayerSnapshot` (`:836`);
- constructs the command with `new StateVersion(session.PlayerSnapshot!.StateVersion)` (`:854`);
- asserts `ReceiptStatus.Applied` (`:865`).

The test does not set `AutoPlay = false`. `LocalTableHostOptions.AutoPlay` defaults to `true`
(`Mahjong.LocalHost/LocalTableHost.cs:61`), and `LocalTableRuntime` starts the driver during
construction (`LocalTableRuntime.cs:89–92`). The test therefore races an autonomous driver while it
reads a live client projection.

### 2.3 The authority's rejection is the designed version fence

`TableActor` validates a command in this order (`Mahjong.TableRuntime/TableActor.cs`):

1. round / control epoch;
2. pending decision id and decision timing (`:2416–2449`);
3. `expectedStateVersion != _stateVersion` → `STALE_STATE_VERSION` (`:2458–2464`);
4. control epoch.

The observed reason therefore means: the decision id was still valid, but the command carried an
older state version than the authority's current `_stateVersion`. It does not mean the decision was
missing or expired.

### 2.4 The response window legitimately advances the authority while seat 0's decision stays open

In this deterministic fixture the response window observed by instrumentation was
`EligibleSeats = {0, 2, 3}`: seat 0 is the human ron decision; seats 2 and 3 are AI response seats.
`LocalTableDriver` opens decisions and, for AI seats, completes them (`LocalTableDriver.cs:222–277`,
`339–533`). Each AI response is a real `TableActor.SubmitAsync` commit (`:456–524`). The state version
moves while seat 0's pending decision id remains `local-human-2-0-ResponseWindow`.

A supporting instrumented run of the same round recorded the authority moving from version 2 to 4
while that pending id was unchanged, and the client projection moving from 3 to 4. The trace is
preserved at `evidence/ron-timing.log` (`501E17E829E863F363DAE6C4170CA26E266D9E6AA2316CB6C631AA4222AF0CEA`).
The actor/client reads in that trace are not atomic, so only the monotonic advance and the unchanged
pending id are used as supporting evidence; the controlled probe in §3 is the primary proof.

### 2.5 Controlled positive/negative probe

A temporary test-only probe was added in the isolated copy (not delivered) using the same round:

1. `AutoPlay = false`; start the driver explicitly.
2. Reach the open `DeclareRon` decision.
3. Stop the client pump so the snapshot that contains the ron decision stays at its observed
   version (`staleV`).
4. Let the driver finish the AI response seats; stop it.
5. Submit the **same** command fields (round, decision id, control epoch, scope) first with
   `staleV`, then with the authoritative current version.

Three runs, 3/3:

| Probe | Observed | Result |
|---|---|---|
| stale version | `staleV=2`, authority `4`, same decision `local-human-2-0-ResponseWindow` | `Rejected` / `STALE_STATE_VERSION` |
| current version | authority `4` | `Applied`, `CommandVersion=5` |

Exact log: `evidence/controlled-probe.log` (`D59A2FCF44A6B73F6BDB13F79D524492230D48FC8F1EB4AC0AD21906AA9FD4CF`).
This is deterministic evidence that a stale client version plus a still-open decision produces the
exact historical reason. It is **not** proof of the historical run's exact schedule.

## 3. Facts, hypotheses, and product-defect assessment

### 3.1 Proven

- The historical full-suite failure occurred once in the prior validation's ten runs and is preserved.
- The test reads a mutable client snapshot after a fixed 150ms delay while the autonomous driver is
  enabled.
- The response window contains AI seats whose response commands advance `_stateVersion`; seat 0's ron decision
  remains pending across those commits.
- `TableActor` rejects a command whose expected version is not current with `STALE_STATE_VERSION`.
- With a deliberately stale version and the same valid decision, the exact reason string is
  reproduced; with the current version the same command applies.
- The test-only patch removes this race and passes focused and full LocalHost runs.

### 3.2 Hypotheses (not proven)

- In historical run 1, the 150ms delay was too short for the client pump to apply the AI response
  commits, or a commit landed between the snapshot reread and the command submission. The exact
  sequence and the exact stale version in run 1 are not captured.
- The failure was more likely on the cold first run because JIT/IO/scheduling delayed the client
  pump. This is plausible but not measured; 8 clean runs of the unfixed accepted-patch baseline
  passed on this host, so the historical race was not reproduced naturally here.

### 3.3 Product defect?

**No product defect is proven.**

- The rejection is the authority's intended optimistic-concurrency behavior. Accepting a command
  against a moved version would risk applying a decision to a state the caller did not observe.
- The AI responses are legitimate response-window commands. No duplicate commit, lost ledger entry,
  physical-conservation violation, or hidden-information leak is observed.
- A product-level *UX* analogue is possible in principle: a UI command built from a view that has
  not yet applied a commit can be rejected. The product UI builds commands from
  `_latestView.Identity.StateVersion` and surfaces rejection (`TablePresenter.cs:493–497`,
  `2258–2262`). This task found no evidence of an actual user-facing lost ron or state corruption.
- Therefore the defect established here is in the test's synchronization, not in the production
  authority. The patch does not mask a production failure; it stops the test from racing the
  authority. A separate product review of retry/refresh UX is optional and is not justified as a
  production change by this evidence.

## 4. Candidate patch

File modified: `dotnet/tests/Mahjong.LocalHost.Tests/LocalHostCandidateAcceptanceTests.cs` only.
Base: `00cd02d` + both accepted test patches. Patch is LF-only, 0 CR bytes.

Changes:

1. Set `harness.Options.AutoPlay = false` and start the driver explicitly with
   `runtime.DriverForTest.Start()`, matching the accepted furiten remedy's explicit-driver pattern.
2. After the `DeclareRon` decision opens, wait until every non-seat-0 eligible window seat has
   responded. This preserves the intended AI-response coverage and lets the ron still be the
   resolving command.
3. `await runtime.DriverForTest.StopAsync()`, assert `DriverRunning == false`.
4. Read the authoritative `TableActor` snapshot; wait until the client snapshot's `StateVersion`
   equals it; assert equality. Read the authority again and assert no movement after the stop.
5. Build and submit the ron from the converged snapshot once. Remove only the fixed 150ms sleep.

Preservation:

| Check | Before (accepted patches) | After candidate patch |
|---|---:|---:|
| `Assert.` lines | 278 | 283 (+5) |
| `[Fact]` / `[Theory]` | 17 | 17 |
| `Skip=` / retry attributes | 0 | 0 |
| Pre-existing assertion text removed | — | 0 |
| `dotnet/src` changes | — | 0 |
| Verifier changes | — | 0 |

The five added assertions are the explicit start non-null check, the pre-stop runtime non-null
check, the driver-stopped check, the client/authority version equality, and the
no-post-stop-authority-movement equality. The command is still submitted exactly once; no retry, no
skip, no exception swallowing.

## 5. Validation in the isolated LF copy

### 5.1 Environment

| Item | Value |
|---|---|
| OS | Windows 10 Pro 10.0.19045, 16 logical CPUs |
| .NET SDK / host | `8.0.426` / `8.0.32` |
| dotnet path | `D:\Unity\dotnet\dotnet.exe` |
| xUnit | xUnit.net v3 4.0.2, direct in-process runner |
| Build | Release, isolated `bin/obj` under the LF copy |

### 5.2 Build

```powershell
dotnet build dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj -c Release --nologo
```

Exit 0; 0 warnings / 0 errors; log `evidence/lf-build.log`
(`7F4D5EFE1E083841A252CF17D9EF033618D23F504E1030A3116CADCDBB64732B`).

### 5.3 Focused failing test

Command (from `bin/Release/net8.0`):

```powershell
dotnet Mahjong.LocalHost.Tests.dll -noLogo -reporter default `
  -method "Mahjong.LocalHost.Tests.LocalHostCandidateAcceptanceTests.RonCandidateAndSubmissionTest"
```

| Runs | Each exit | Each counts | Result |
|---:|---:|---|---|
| 20 | 0 | `1 passed / 0 failed / 0 skipped / 0 not-run` | **20/20 PASS** |

Times 1.04–3.83s. Logs `evidence/lf-ron-focus-1.log` …
`evidence/lf-ron-focus-20.log`; representative hashes:
`C400BDBD42A66FD398648B2007AFA599B1346431D4239853B70ED24122C9C188` (run 1),
`02086A6B5B04FF1B300AD9B64538C7C9A04A930D938AE720D48CCB2B5C33E190` (run 20).

### 5.4 Full LocalHost suite

```powershell
dotnet test dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj `
  -c Release --no-build --no-restore --nologo
```

| Run | Exit | Total | Passed | Failed | Skipped | Not run | Duration | Log SHA-256 |
|---:|---:|---:|---:|---:|---:|---:|---:|---|
| 1 | 0 | 115 | 115 | 0 | 0 | 0 | 27.012s | `5347993866B01226BFBCEB445E5E26F131DE0F3CDF475C580F4E0D282BC1DC10` |
| 2 | 0 | 115 | 115 | 0 | 0 | 0 | 26.535s | `524B27C784FC8344CA947FE9C6B752A6F80FA56C345BBC38A10F7405320DE18B` |
| 3 | 0 | 115 | 115 | 0 | 0 | 0 | 26.804s | `427CAD12935D73EE0B470A8551D9AB410A7B818FF07706AA66D9C58D8445875C` |
| 4 | 0 | 115 | 115 | 0 | 0 | 0 | 28.448s | `EAFD0F04702D5AC518A60B96D99BAE00C337ACFC9A28F761D2873E3CB77BE491` |
| 5 | 0 | 115 | 115 | 0 | 0 | 0 | 27.670s | `0BC0816B9AE321B53EC23E7106693BAB779D8AD021694014A47C9BBA7322DC8B` |

Cold characterization:

```powershell
dotnet build … -c Release --no-incremental --nologo   # exit 0, 0 warnings / 0 errors
dotnet test … -c Release --no-build --no-restore --nologo
```

Exit 0; 115/115, 0 failed, 0 skipped; 28.874s; `evidence/lf-full-cold.log`
(`E260B7AA04AE9796A11601AA956E121350BAF1A7C96E0A0761A443752E7C469A`).

### 5.5 Related regression tests

Direct runner, 3 runs each:

| Test | Result | Logs |
|---|---|---|
| `ClientPassOnRonSetsFuritenAndColdRestartKeepsItUntilTheNextDraw` | 3/3 PASS, exit 0 | `lf-focus-furiten-pass-{1..3}.log` |
| `ClientPassFuritenBlocksSecondRealRonUntilTheNextDraw` | 3/3 PASS, exit 0 | `lf-focus-furiten-worker-{1..3}.log` |
| `ExpiredOrRevokedScopeCannotPersistRejectionOnEitherCommandPath` | 3/3 PASS, exit 0 | `lf-focus-scope-race-{1..3}.log` |

Representative hashes: `B7E8A26E…` (furiten-pass 1), `8EC856AE…` (furiten-worker 1),
`9A1D7BC7…` (scope-race 1).

### 5.6 Unfixed accepted-patch baseline

The same exact `00cd02d` + both accepted patches, without this candidate patch, was built and run
8 times on this host:

| Runs | Exit | Result |
|---:|---:|---|
| 8 | 0 each | 115/115, 0 failed, 0 skipped |

Logs `evidence/unpatched2-full-run{1..8}.log`. This **does not erase** the historical run 1
failure; it confirms the race is rare and scheduling-dependent on this host. The controlled probe
in §2.5 is the deterministic reproduction of the mechanism.

## 6. Limitations and non-claims

- No unified verifier, no other .NET test project, no Unity Editor/PlayMode/IL2CPP, no Android/iOS,
  no physical device, no remote CI, no push, no PR, no trust change, no Unity environment change,
  no delegation.
- The raw historical xUnit file is lost; the preserved transcript is not the raw framework file.
- The historical interleaving is not proven. The controlled probe pauses the client pump; it proves
  a sufficient mechanism, not the exact run-1 schedule.
- The unfixed baseline passed 8/8 on this host, so the historical failure was not naturally
  reproduced here.
- The fix is specific to this deterministic fixture. If the AI response behavior or eligible-seat
  set changes, the wait condition must be reviewed. It waits for real responses and does not skip
  them.
- No CPU-saturation/stress run was performed. The fixed suite was not run under artificial load.
- The temporary controlled probe is not delivered; only its logs are preserved to keep the candidate
  patch minimal.
- This report does not approve PR20, the LocalHost batch, or G2–G5. The caller reviews independently.

## 7. Evidence

Ignored root: `build/localhost-ron-stale-20261030/` (not committed). Manifest:
`build/localhost-ron-stale-20261030/evidence/SHA256SUMS-ron-stale.txt`, SHA-256
`873045C2BEB6D781AE95566A406DBA07A209F9965A69E40BE87CE9EC99142DD1` (176 entries).

Key artifacts:

| Artifact | SHA-256 |
|---|---|
| `pr20-00cd02d-lf.tar` | `BF799A62E8F83B887D860171C209E1E1F52314770E2C87DAFFA9404539A22B99` |
| `final-test-fix.patch` | `65DCF0F6F02D3B0056B1C6F66A2B71D9E102D1659D07505FC2376C1EF499151C` |
| fixed LF test file | `035F2448187DCDFC54C963BA82D59C6AD7DC9810D64A8E77E79D26CA305D5E81` |
| accepted-patch LF test file | `A387C0E06BF13AE3C83AE2BB343FDEDFC210BF3652C6797EC214645806C92A2A` |
| `lf-build.log` | `7F4D5EFE1E083841A252CF17D9EF033618D23F504E1030A3116CADCDBB64732B` |
| `lf-full-run1.log` | `5347993866B01226BFBCEB445E5E26F131DE0F3CDF475C580F4E0D282BC1DC10` |
| `lf-full-cold.log` | `E260B7AA04AE9796A11601AA956E121350BAF1A7C96E0A0761A443752E7C469A` |
| `controlled-probe.log` | `D59A2FCF44A6B73F6BDB13F79D524492230D48FC8F1EB4AC0AD21906AA9FD4CF` |
| `ron-timing.log` (supporting) | `501E17E829E863F363DAE6C4170CA26E266D9E6AA2316CB6C631AA4222AF0CEA` |
| historical run 1 console log | `80461329F2CBF68EF89CE521F34CA4EDEA8B63FE210FDE931D8E3B69C5580056` |
| historical failure transcript | `0534BB9B579D1A2BA4A71C5ED32AEB8898EDDBF49EACE85A4420A70E0EEC94BB` |

## 8. Reproduction

```powershell
# 1. exact LF candidate
git -c core.eol=lf -c core.autocrlf=false archive --format=tar `
  -o build/localhost-ron-stale-20261030/pr20-00cd02d-lf.tar 00cd02d
tar -xf build/localhost-ron-stale-20261030/pr20-00cd02d-lf.tar `
  -C build/localhost-ron-stale-20261030/candidate-lf

# 2. isolated seed, no autocrlf conversion
cd build/localhost-ron-stale-20261030/candidate-lf
git init; git config core.autocrlf false; git config core.eol lf
git add -A; git commit -m seed

# 3. accepted patches then this candidate patch, all LF
git apply --check --whitespace=error-all ../furiten.patch
git apply --whitespace=error-all ../furiten.patch
git apply --check --whitespace=error-all ../caller.patch
git apply --whitespace=error-all ../caller.patch
git apply --check --whitespace=error-all ../final-test-fix.patch
git apply --whitespace=error-all ../final-test-fix.patch

# 4. build and full LocalHost suite
dotnet build dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj -c Release --nologo
dotnet test dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj `
  -c Release --no-build --no-restore --nologo
```

No source commit, push, PR, CI trigger, trust change, Unity environment edit, or delegation was made
by this task. The other worktree and its evidence were read-only.
