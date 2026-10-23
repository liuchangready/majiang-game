# P3.2c integration inspection — 2026-10-24 04:48 UTC+8

Status: **FAIL / independent integrated gate**. Accepted P3.2c was concurrently published by the original caller at `32f645e`; this invocation made no push. V4 remains production; G1 is BLOCKED. No G5 completion flag was created.

The global bridge list was inspected once for this invocation. All V5 component threads were completed. P3.2c `thread_20261024_031743_6c0a` initially had its merge-back obligation open; a final reservation read confirmed the original caller recorded it at `32f645e`. Its component approval is for source `06f809fe74383eb331212256929ae1c1042de403`, not a complete P3.2 or G1 approval. No paused thread was resumed.

## Candidate and ownership

- Remote PR #19 remains `feat/v5-1-integration -> v5`. Initial head was `8af240d1cd1c958295b2f04d724409bd6077ad75`; final observed head is `32f645ec9b134e9ad261a2df89437416b20b3119`.
- A new isolated caller tree `D:/majiang-wt/codex-p32c-integration-20261024`, branch `wt/v5-codex-p32c-integration-20261024`, starts at that exact reviewed remote head. The older integration trees and receiver trees were not modified.
- Local merge candidate `8523d3d3211346358af9b1ab324f454b395ae4c2` merges the accepted P3.2c source without conflict. `git diff --check 8af240d..8523d3d` passes. No in-flight changes were included.
- This invocation did **not push** its candidate or duplicate the original caller's merge-back record. After observing that concurrent published integration, it fetched and merged the remote head normally. `git diff --exit-code origin/feat/v5-1-integration -- dotnet tools unity .github` passes: this tree and published `32f645e` have identical implementation source. Preserve this tree and its evidence for the next accepted repair; no runtime source was changed after the failing check.
- Main checkout remains at `d833348`, with unrelated artifacts preserved. No Unity environment/Resources/Burst assets changed.

## Independent checks on the candidate

Command (Windows PowerShell 5.1 child process):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/ci/verify-v5-postgres.ps1 -Configuration Release -RunnerMode dotnetTest -DotnetPath D:/Unity/dotnet/dotnet.exe -Port 55463 -Root D:/majiang-pg-codex-p32c-20261024 -LogDir D:/majiang-wt/codex-p32c-integration-20261024/artifacts/codex-p32c-integrated
```

| Check | Result |
|---|---|
| Solution Release build | PASS, 0 warnings / 0 errors |
| Scratch Unity dependency closure | PASS, 12 DLLs; no tracked Unity assets touched |
| Domain | PASS, 313 / 0 / 0 |
| AI | PASS, 14 / 0 / 0 |
| Contracts | PASS, 104 / 0 / 0 |
| Client | PASS, 132 / 0 / 0 |
| Client.Wss unit tests | PASS, 80 / 0 / 0 |
| Lobby with actual PostgreSQL | PASS, 89 / 0 / 0 |
| TableRuntime | FAIL, 149 passed / 1 failed / 0 skipped |
| Unified verifier / wrapper | FAIL, exit 1; **881 passed / 1 failed / 0 skipped** |
| Eight independent malicious Wire probes | PASS, all 8 REJECTED, exit 0 |
| Independent window-only Wire probe | PASS, exact windowId retained, open/closed bytes differ, exit 0 |

Both probe programs were copied from the earlier caller's standalone reproductions into this tree's `artifacts/independent-probes`, with project references changed to **this candidate tree**. They were run using `D:/Unity/dotnet/dotnet.exe run --project <Review.csproj> -c Release --nologo -v minimal`; they do not merely rerun against the receiver tree.

Real disposable PostgreSQL 16.10 was downloaded with the pinned hash, bound only to loopback port 55463, identity-checked, and migrated twice. The owned cluster was cleaned; its root is absent and no listener remained on 55463. Environment restoration passed under Windows PowerShell 5.1. This does not establish the CI pwsh environment-restoration result.

The local failure is `TableStreamActorTests.RecoveryAfterCommitRebaselinesTheFinalCommittedState`: expected 3 frames, observed 2, at TableStreamActorTests.cs:310 / TableStreamTestSupport.cs:131. The original raw framework log is preserved. The test submits a command and immediately begins recovery without awaiting delivery of its pre-recovery update; delivery fencing can intentionally discard that queued old-generation update. This is a source-based explanation to investigate with a controlled scheduling test, **not a finding that production behavior or the test has already been fixed**.

## Remote checks actually inspected

Both job logs, full uploaded bundles and original framework logs were downloaded before any follow-up. No retry was requested.

| Run | Actual checked-out source | Actual result |
|---|---|---|
| [40020648672 push](https://github.com/liuchangready/majiang-game/actions/runs/40020648672) | `8af240d1cd1c958295b2f04d724409bd6077ad75` | FAIL: 851 passed / 2 failed / 0 skipped; env-restore also FAIL |
| [40020658782 PR](https://github.com/liuchangready/majiang-game/actions/runs/40020658782) | synthetic merge `9093d95e8b4e91fd7a4b9ca7a4c1776a7bba3077`, PR head `8af240d` | Verifier 853 / 0 / 0 PASS; wrapper FAIL on env-restore |

Both runs built successfully and passed the 12-DLL check. Actual PostgreSQL Lobby tests were 89 / 0 / 0 in both. Client was 132 / 0 / 0 in both; the historical Client failure in run39970580589 remains UNRESOLVED and is not classified as flaky or fixed.

The push run names two TableRuntime failures:

1. `RecoveryAfterCommitRebaselinesTheFinalCommittedState`: expected 3 frames, observed 2.
2. `NewBindingReplacesOldHandleAndOldHandleCannotCancelOrSyncNewStream`: expected 1 frame from the displaced first binding, observed 0. The test replaces the binding before waiting for that first delivery; inspect the intended fencing semantics with deterministic ordering.

Both remote wrappers report all four originally unset environment variables restored as a present empty string (empty-string SHA256), with `restored=false`. Source uses `SetEnvironmentVariable(name, $null)` and then requires a null readback. The workflow runs `shell: pwsh`; this shell/runtime difference is an investigation lead. Preserve absent-versus-empty semantics and restoration verification; do not force a green result or change the verifier.

## Evidence and next work

### Final remote state observed during this invocation

The original caller published accepted P3.2c through merge `955c419` and acceptance document `32f645e`, and recorded its existing thread's merge-back. This invocation detected that head change before any push and did not overwrite it. The original caller's local acceptance records 793 / 0 / 0 without a real-PG unified run; that evidence does not supersede the independent 881 / 1 / 0 result above.

The latest two CI bundles were also downloaded, including framework logs, before follow-up:

| Run | Actual checkout | Unified tests / wrapper |
|---|---|---|
| [40021452487 push](https://github.com/liuchangready/majiang-game/actions/runs/40021452487) | `32f645ec9b134e9ad261a2df89437416b20b3119` | **882 / 0 / 0 PASS**, verifier exit 0; wrapper exit 1, four env-restore failures |
| [40021457715 PR](https://github.com/liuchangready/majiang-game/actions/runs/40021457715) | synthetic merge `e2dffe6ca3283e5befc376325269347c0e6fdd62`, PR head `32f645e` | **882 / 0 / 0 PASS**, verifier exit 0; wrapper exit 1, four env-restore failures |

Both have actual PG 89 / 0 / 0, Client 132 / 0 / 0, TableRuntime 150 / 0 / 0, build 0 warnings/errors and 12 DLL PASS. Neither is an overall CI PASS. The earlier preserved stream-test failures and this invocation's exact-source reproduction still require deterministic investigation; later passing schedules are not a fix.

Stable evidence root: `D:/majiang/artifacts/progress-20261024-current/`; its REVIEW.md, full remote bundles, local integrated gate, probe programs/logs and `evidence-sha256.csv` are retained. No PostgreSQL data directory was archived. Full raw local framework logs are also under this tree's `artifacts/codex-p32c-integrated/test-results`.

Prepare the original-owner actor and CI threads for same-thread rework, plus an independent DeepSeek physical golden export for G-001a/b/c, G-002 and G-003a/b. If an archived reviewer-role gate refuses rework, preserve its exact refusal and use only an explicitly scoped successor follow-up with that provenance; never retarget or impersonate the old native role. Temporary successor preparation trees are unused until an actual dispatch succeeds.

Full P3.2 still needs actor-to-Contracts/Wire joint wiring and real byte-limit/identity tests after these component regressions are closed. P3.4 waits for complete P3.2 and accepted P3.3. G1 remains BLOCKED by those contracts and required evidence; P4.1/P4.2/P5.1/P5.2 are not released. Real network/device/Unity IL2CPP checks are NOT_RUN here; missing toolchains, licensing and product decisions remain explicit.
