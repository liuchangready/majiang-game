# D-CAPTURE-01 caller acceptance — 2026-10-29

Decision: **round-3 task acceptance PASS**, including D-CAPTURE-01, D-CAPTURE-01-SCOPE and D-CAPTURE-01-RESTORE-EXISTENCE, for thread `thread_20261029_114646_759c` at `5a8aae8035147cec92f4efb97d496f273d75b349`. The original reviewer callback independently verified this candidate and successfully called `approve_task`; the bridge returned `COMPLETED`. The earlier scheduled session's role rejection described below is historical. This task acceptance does not approve LocalHost activation, G2–G5 or release. V4 remains production.

## Round 3 original reviewer verification

The reviewer ran in `D:/majiang-wt/native-capture-ds-20261029-1145` using Windows PowerShell 5.1.19041.6456 and SDK 8.0.426. Candidate changes remain exactly the three expected files. `git diff --check e3ddd83..5a8aae8` passed and tracked state was clean. Production now saves variable existence independently from its value, removes script/global LASTEXITCODE before invocation, and restores the previous value or absence in finally. Caller-local state is untouched. The XML/console/native-exit/skip gates remain strict; no domain, concurrency, protocol or privacy code changed in this task.

Evidence R is `artifacts/caller-capture-r3-d2d45d1bc2584dedba4d75b07718a9fe/` in that worktree.

| Actual execution | Result |
|---|---|
| `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File tools/ci/test-v5-captured-exit.ps1 -EvidenceDir <R>/captured` | native 0; 51/51, 0 failures; AST extraction of production function |
| `test-v5-native-exit.ps1`, same PowerShell flags | native 0; 6/6, including timeout and forced kill |
| `test-v5-direct-results.ps1`, same PowerShell flags | native 0; 19/19; all negative cases still rejected |
| Original `artifacts/caller-capture-r2-1793248746625/probe-existence.ps1 -EvidenceDir <R>/independent-existence`, same PowerShell flags | native 0; 7/7; absent script/global variables remain absent for no-native, exits 0/1/2/17, throw and missing executable |
| Fresh Architecture build and real direct runner through production capture | build/run native 0; 0 warnings/errors; 130/130, 0 failed/errors/skipped/notRun; XML gate PASS |
| Real `dotnet test` through production capture | native 0; Total130, Failed0, Skipped0 |
| Isolated unified direct invocation | native 0; Architecture130/130 and companion4/4; plugin explicitly NOT_RUN |
| Receiver round-3 evidence manifest | 224/224 lengths and SHA256 hashes match |
| Independent large-stream log inspection | exactly 1200 stdout and 1200 stderr payload lines retained |

Fresh direct and dotnet commands use the inspected receiver scripts `artifacts/d-capture-01-54b8788057ae426e90759cbfa3facb4d/{direct-run-probe,dotnet-test-probe}.ps1 -RepoRoot <worktree> -EvidenceDir <R> -DotnetPath D:/Unity/dotnet/dotnet.exe`. The reviewer checked the resolved project bin/obj deletion targets before running the fresh-build probe. Direct XML SHA256 is `D641BD8187EADA747146F7965CF9EE9B36BE1F54B1A14BAAF37E85D8D36C73C5`. Its 130 individual test nodes, console counts and native exit agree.

The isolated unified command uses `tools/verify-v5.ps1 -ManifestPath <R>/unified-tiny/manifest.json -LogDir <R>/unified-tiny/logs -RunnerMode direct -NoProjectDiscovery -SkipPluginCopyCheck -DotnetPath D:/Unity/dotnet/dotnet.exe`, with the same PowerShell flags. Its XML SHA256 is `AA4FD0961C2DB7C24C0E37F08892065AFC9270CB250F1AB1DDD6B04EFAF9A963`. The override applies only to this diagnostic run.

After task approval, the reviewer merged fetched PR20 head `e0d9ff30a9a4fb3b0900836a545f5d89bdd59d9f` without conflicts, producing `e501ff3111c9334faf6c70364f379250ac1f7e27`. Only round-3 implementation, its matrix and delivery documentation were missing from that published batch. The merged production capture hash remains `363A398D9CEFCA1C7F2BA5AC5DD2B7989BD6EDE49812C6E1E9BA487CAD7EDE66`.

R/caller-audit.json independently confirms all case-array counts and both sets of 130 XML nodes. The first audit incorrectly wrapped two JSON arrays as single items; its failed audit is retained in R/caller-audit-first-array-wrapper-error.json. The corrected audit reads those existing arrays and exits 0; no product test was rerun to change a result. R/caller-evidence-sha256.csv covers 143 files, SHA256 `54DAE348F0D9171049110E9B921F0EDC3DA4AFC37B65A0D9236127F44AD1A107`.

## Round 3 batch closeout

The first full-wrapper attempt at `artifacts/closeout-c98d3cc3/` passed Infrastructure42/42, targeted13/13, forced-writer kill, PostgreSQL crash/restart recovery and backup/restore. The reviewer incorrectly supplied a LobbyRoot inside the worktree; the existing lobby wrapper refused it with exit 2 before unified verification started. The table wrapper returned 1 and safely removed its owned cluster. This attempt remains FAIL; no test or guard was changed. The corrected invocation uses a new table cluster under this worktree's artifacts and a unique lobby cluster under TEMP, with both absolute cleanup roots checked before execution.

**Corrected full batch: PASS, native wrapper exit 0**, on merged implementation `e501ff3111c9334faf6c70364f379250ac1f7e27`. The only uncommitted tracked change during execution was this caller acceptance document. Evidence B is `artifacts/closeout-e146bc3c/`; B/COMMANDS.txt retains the full command and resolved roots. The command invokes `tools/ci/verify-v5-table-postgres.ps1 -RunUnifiedVerification` with SDK `D:/Unity/dotnet/dotnet.exe`, independent table/lobby ports 50785/50786, default verifier manifest and project discovery, and plugin copying enabled. It uses real PostgreSQL 16.10, real WSS tests and freshly built assemblies. No skip, discovery, manifest or plugin override was supplied.

| Final verification | Result |
|---|---|
| Fresh solution build | native 0; 0 warnings/errors |
| Unified project rows | 18 PASS / 0 FAIL / 0 BLOCKED / 0 NOT_RUN; 13 test assemblies and 5 build-only helpers |
| Independent XML audit | 2549 selected/passed and 2549 individual test nodes; failed/errors/skipped/notRun all 0 |
| DurableHost | 59/59, native 0; expected RECOVERY_LEASE_HELD diagnostics retained; no capture invocation exception |
| Companion | 4/4, native 0 |
| Plugin copy | PASS; 12 assemblies in scratch output; tracked Unity assets untouched |
| Table PostgreSQL wrapper | native 0; Infrastructure42/42 and targeted13/13, forced-kill/new-process crash recovery, immediate PG restart and backup/restore all PASS; these repeated tests are not added to unified2549 |
| Lobby wrapper | native 0; migration applied twice; all wrapper stages PASS |
| Cleanup and restoration | both owned cluster roots absent; fresh-child environment restoration proven PASS |

B/caller-xml-audit.json contains every assembly's counts. DurableHost XML SHA256 is `78F4F7AF7A66477BB0EDC114F8BE87FA7BE3C784AEB609F0F7C5E47D127A5B16`; the formerly truncated stderr scenario completed with all 59 test nodes, console counts and native exit 0 agreeing. B/unified/summary.log SHA256 is `0FFE7E5F0026725C84506B5E6ECFE6DB0B438F57C333D55A77CAAC31B3FF00D5`; B/unified/pg/wrapper-summary.json SHA256 is `06CEC47C94F1C82C20E3DF6CFEA1F61F86FF9DC04C7361184F5F3C0391EAA841`. B/caller-evidence-sha256.csv covers 223 files, SHA256 `DB1A1878F822A04DE1B25ED1B5A9D116F479C77AC787FD2B6540F3EFCD51BDF6`.

This new successful batch does not rewrite the historical 17-PASS/1-FAIL run or the incorrect-root attempt. It does not accept the outstanding LocalHost rejection-receipt/full Unity activation tasks, actual HTTPS Lobby-ticket integration, G2–G5, account settlement, production TLS deployment or device/network evidence. Android remains historical FAIL, iOS BLOCKED, real device/cross-physical-network NOT_RUN, classified181 uncovered; pwsh is unavailable. V4 remains production.

Publication uses only existing Draft PR20, source `feat/v5-post-g1-batch`, base `v5`, normal push to origin. The final publication SHA and remote-check state are recorded through `record_merge_request`. Local batch PASS is separate from remote CI; no CI retry/ref, merge into v5, deployment, scheduler change or complete.flag is part of this closeout.

## Historical round 2 verification and publication

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

Existing PR20 is the sole publication target, base v5. The reviewed implementation and this independent acceptance were normally pushed at 15090461883537ec79f6aff5638458a855ba328f; gh PR head and ls-remote matched. record_merge_request succeeded (MR_RECORDED/synced) and explicitly records the separate ledger-approval role blocker. This documentation correction leaves implementation/tests/tools unchanged. PR19 remains merged/closed. No merge into v5 or default branch, force push, release or complete.flag is authorized here.

Exact prior PR20 head27ec2b6 remote runs [40817096599](https://github.com/liuchangready/majiang-game/actions/runs/40817096599) and [40817090774](https://github.com/liuchangready/majiang-game/actions/runs/40817090774) have failure status, empty steps and empty runner names. Both check annotations explicitly report account payment/spending-limit prevention before execution. Remote tests are BLOCKED/NOT_RUN; no retry or CI-only ref was created. Raw annotations are retained in E. A later published SHA needs its own remote evidence.

Complete unified PG/WSS batch was **NOT_RUN in that historical round-2 invocation**, and its prior FAIL is not relabeled. Android retains FAIL, iOS BLOCKED, real devices/cross-physical-network NOT_RUN, and classified181 remains uncovered. G1 APPROVED; G2–G5 unapproved. That caller read-only verified the scheduler Running/PT30M/IgnoreNew and complete.flag absent.
