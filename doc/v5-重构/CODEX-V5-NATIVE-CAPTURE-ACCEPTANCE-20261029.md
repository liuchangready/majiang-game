# D-CAPTURE-01 caller acceptance — 2026-10-29

Decision: **PASS for the native capture repair**, including D-CAPTURE-01-SCOPE. This accepts bridge thread `thread_20261029_114646_759c` round 2 at `6f0a31784fc7f8d483623ad07678f1968fa77bc6`. It does not approve the full batch, LocalHost activation, G2–G5 or release. V4 remains production.

The caller created `D:/majiang-wt/codex-capture-integration-20261029-1793248950316` from fetched accepted origin/v5 `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`, fast-forwarded to reviewed PR20 `27ec2b66e45637d2b63e632f6c9cb4a2441e9824`, and merged only this accepted candidate. Integration commit `e2664722f40773a12b64d918be3463a9fa391e76` changes exactly the verifier, its new captured-exit self-test and the delivery report. Receiver tracked state was clean; default diff-check passed. No other task's uncommitted output was included.

## Independent execution

All commands below ran in that integration tree using Windows PowerShell 5.1.19041.6456 and SDK 8.0.426, `D:/Unity/dotnet/dotnet.exe`. Evidence E is `artifacts/caller-capture-acceptance/`.

| Command / check | Actual result |
|---|---|
| `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File tools/ci/test-v5-captured-exit.ps1` | native 0; 44/44, 0 failures |
| `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File tools/ci/test-v5-native-exit.ps1` | native 0; 6/6, including timeout and forced writer |
| `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File tools/ci/test-v5-direct-results.ps1` | native 0; 19/19; missing, malformed, stale, zero, skipped and mismatched evidence still rejected |
| Original caller local-scope probe, unchanged source, new E/original-probe-replay | native 0; no-native returns null; actual exit17 returns 17; caller local value remains 0 |
| Fresh Architecture build and direct runner through production Invoke-Captured | build/run native 0; 0 warnings/errors; 132 selected/passed, 0 failed/errors/skipped/notRun; XML gate PASS |
| Real `dotnet test` through production Invoke-Captured | native 0; Total132, Failed0, Skipped0 |
| Isolated unified invocation | native 0; Architecture132/132; companion4/4; OVERALL PASS for one-project manifest only; plugin NOT_RUN |
| Receiver round-2 manifest | 202/202 file lengths and SHA256 hashes match |

Fresh direct and dotnet test used the receiver's inspected `artifacts/d-capture-01-54b8788057ae426e90759cbfa3facb4d/{direct-run-probe,dotnet-test-probe}.ps1` with `-RepoRoot <integration tree> -EvidenceDir <E>`; the new tree had no prior build output. Direct DLL SHA256: `1392D064EFD2B8B7A6D5001B5A587BA76A1EA51B4CE3D3E7BBE6CD15873636A0`. Direct XML SHA256: `5769365FBB2D65F7D044527E82060F46DA5249410B3BD167D08486CFE9CBB201`. Caller independently counted individual XML test nodes for both direct invocations.

The isolated unified command was `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File tools/verify-v5.ps1 -ManifestPath <E>/unified-tiny/manifest.json -LogDir <E>/unified-tiny/logs -RunnerMode direct -NoProjectDiscovery -SkipPluginCopyCheck -DotnetPath D:/Unity/dotnet/dotnet.exe`. The manifest and empty Tiny.sln are retained. This diagnostic override is confined to this run; default production discovery/manifest, XML/console/skip gates and plugin behavior were not weakened.

Caller audited the actual diff and real AST-extracted production function. It captures native stderr without converting expected diagnostics into terminating PowerShell exceptions, rejects real PowerShell errors and unknown exits, and uses explicit script/global scope to avoid stale caller-local exit codes. Domain, deadlines, leases, control epochs, protocol, private state and production certificate validation were untouched.

The three matrices are retained in `artifacts/captured-exit-selftest-7bdf61ac37cd45b58ed7c85bce8ee2fb`, `artifacts/native-exit-81c9944b24724fe5b311225a7e684aee` and `artifacts/direct-results-selftest-c5e0420e22f34068a45318ebdb09ece3`. E/Audit.ps1 validates the JSON case arrays and XML nodes. Its first audit mistakenly treated each JSON wrapper as a case; that audit error is preserved in `matrix-audit-first-wrapper-error.json`. The corrected audit exits 0 and reads the existing results arrays; product tests were not rerun to alter a result.

E/caller-evidence-sha256.csv covers 137 retained files, SHA256 `7B5E87DA9BC7ECEB0141BB13D4CA983E4FA562AF74C0A68B9C0E08FD575EA015`. The preceding historical verifier FAIL and pre-fix local-scope failures remain in the receiver report and original evidence.

## Routing, remaining work and publication

One global ledger read found legacy GLM `thread_20261029_064512_04b1` round3 reworking (one active slot), this DeepSeek capture delivery waiting_review, and DeepSeek Unity `thread_20261028_223624_e4ae` waiting on that LocalHost dependency. No GLM/Anti discovery, execution, rework, retry, resume, cancellation, pause or retarget was issued. Any further legacy execution is BLOCKED by current routing policy.

After capture acceptance there is one available slot. P5.4b-A is accepted/published at 27ec2b6; taskbook P5.4b's remaining actual HTTPS Lobby ticket to WSS/PostgreSQL client integration is dependency-ready and independent of the reserved LocalHost/Unity files. A single DeepSeek follow-up may be prepared; task ID must be taken from dispatch result. Full Unity activation stays with its original thread. Failed historical LocalHost threads are not resumed.

Existing PR20 is the sole publication target, base v5. PR19 remains merged/closed. No merge into v5 or default branch, force push, release or complete.flag is authorized here.

Exact prior PR20 head27ec2b6 remote runs [40817096599](https://github.com/liuchangready/majiang-game/actions/runs/40817096599) and [40817090774](https://github.com/liuchangready/majiang-game/actions/runs/40817090774) have failure status, empty steps and empty runner names. Both check annotations explicitly report account payment/spending-limit prevention before execution. Remote tests are BLOCKED/NOT_RUN; no retry or CI-only ref was created. Raw annotations are retained in E. A later published SHA needs its own remote evidence.

Complete unified PG/WSS batch was **NOT_RUN this invocation**, and its prior FAIL is not relabeled. Android retains FAIL, iOS BLOCKED, real devices/cross-physical-network NOT_RUN, and classified181 remains uncovered. G1 APPROVED; G2–G5 unapproved. Scheduler was read-only verified Running/PT30M/IgnoreNew and complete.flag absent.
