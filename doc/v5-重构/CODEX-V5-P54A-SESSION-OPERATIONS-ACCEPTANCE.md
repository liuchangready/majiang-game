# P5.4a session operations and frozen 978a CI evidence: caller acceptance

Date: 2026-10-26. Evidence-review thread: `thread_20261026_134733_764b`, round 2.
Frozen evidence baseline: `978a15667f80094ed63c86f69c53195439c95a2c`.
Accepted report head: `e53595216cace1251bf870a85c84aaa4c9bc29e3`.

Caller verdict: **PASS for this task's report and frozen-head integration-evidence scope**. The existing thread was approved using `approve_task`, which returned `COMPLETED`. D-C1-01 and D-C1-02 are closed. The candidate's net change from the frozen baseline is exactly one added file, [V5-978A-INTEGRATED-CI-REVIEW-20261026.md](V5-978A-INTEGRATED-CI-REVIEW-20261026.md); the old unauthorized path is absent, tracked status is clean, and diff-check exits 0. Report SHA256: `E074B9D189B6A7B64E05091E5323149E98DB3F456DA05B18D75F16F8446E430E`.

## Original P5.4a acceptance provenance

This record also closes the caller documentation follow-up D-R3-01. It preserves the original acceptance as a historical ledger result, separately from the fresh verification below. It does not rewrite the earlier G1 acceptance or traceability matrix.

The existing implementation thread `thread_20261026_103011_054d` records caller approval after development round 3, for `bfc0839cea1ad5199efed42cd2562f5f33bd3ce2` on baseline `00c71cd`. The four authorized files were `TableSession.cs`, `TableSessionOperationQueue.cs`, `TableSessionRunner.cs`, and `TableSessionOperationQueueTests.cs`. Merge `74216a701859ed8efce3302c15a366a01b9fd402` integrated that accepted increment into PR19.

The original approval explicitly closed D-R2-01 (frame/fault association through ReceiveAttempt and lifecycle fencing), D-R2-02 (one unfinished receive per binding), and D-R2-03 (owner execution, actual cached-state publication, then completion). Its recorded independent results were Client 153/153 including the 21 queue cases, WSS runner/heartbeat 27/27, ReviewR2 3/3, and retained Close/TOCTOU 2/2, with zero failures/errors/skips/not-run and native exits 0. Historical evidence was `D:/majiang-wt/client-session-ops-20261026/build/codex-p54a-review-r3/`. Those historical executions were not rerun or relabeled as current executions here.

The current reviewer independently confirmed that the four increment files have no diff between `bfc0839` and `978a156`. `TableSessionOperationQueue` and `ITableSessionFrameIngress` remain delivered client APIs; production wiring through the later P4.2 consumer remains outside this acceptance.

## Current independent verification

Executed in `D:/majiang-wt/integrated-ci-glm-20261026` at accepted report head `e535952`; its production/test content is unchanged from frozen `978a156`. SDK: `D:/Unity/dotnet/dotnet.exe`, version 8.0.426. Evidence root E:

`artifacts/caller-round2-fe2e6300c0094462b1ae2f4eac1a12a0/`

TEMP/TMP were set only in the verification process to E/temp. Both projects were freshly built before either test DLL was executed:

```powershell
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.Client.Tests/Mahjong.Client.Tests.csproj -c Release --no-incremental --nologo
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.Client.Wss.Tests/Mahjong.Client.Wss.Tests.csproj -c Release --no-incremental --nologo
```

Both native exits were 0, with zero warnings and errors. Tests used these direct-runner commands, where E is the absolute evidence-root path above:

```powershell
D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Client.Tests/bin/Release/net8.0/Mahjong.Client.Tests.dll -noColor -noLogo -class Mahjong.Client.Tests.TableSessionOperationQueueTests -result-xml <E>/operationqueue.xml
D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Client.Wss.Tests/bin/Release/net8.0/Mahjong.Client.Wss.Tests.dll -noColor -noLogo -class Mahjong.Client.Wss.Tests.TableSessionRunnerTests -result-xml <E>/runner.xml
```

| Selection | Total | Passed | Failed/errors/skipped/not-run | Native exit |
|---|---:|---:|---|---:|
| TableSessionOperationQueueTests | 21 | 21 | 0/0/0/0 | 0 |
| TableSessionRunnerTests | 25 | 25 | 0/0/0/0 | 0 |
| Total | 46 | 46 | 0/0/0/0 | 0 |

The reviewer parsed both XML assembly counters independently and checked nonzero selection. These cases cover split receive/apply ingress, serialized operations, commands progressing during a parked receive, and close/reconnect/cancellation ordering. The candidate report contains the individual case mapping.

| Evidence under E | Complete SHA256 |
|---|---|
| build-Mahjong.Client.Tests.log | `F3B41D15AE504941BE79A3DD787AD6284BE410B85BE635FC3D3DD8D9D17A8432` |
| build-Mahjong.Client.Wss.Tests.log | `6E7DA720A589BDA547BE6760430FEF7C444E81E090A35C399718660444BEB143` |
| operationqueue.log | `C612CD728795C7DDB8C7D29339D687D489A813AACD68204F4AD1497040EE4997` |
| operationqueue.xml | `605D4ACC4AD17561A1501C77317DB68A79CF3CF8C48BA0AE37D46A2DF14A0B0E` |
| runner.log | `343996093A1A6688FDF8226FC7C9192577470AAB26C475DC6B94859400CC5FAB` |
| runner.xml | `84DDAFEDFD79F9E972638037D178535AF2A8D59EAAF5D0F8F7CECF2B26AEC355` |
| ci-counts.json | `0845EE57C2D3C657B70503A951F3A68CBE83B1723028E308642E91ABE223F6D0` |

## CI artifact audit

The reviewer rehashed both existing downloaded manifests: 62/62 original files matched per run. The push directory has 64 files including two local conversion copies; the PR directory has 63 including one conversion copy. None of those three `evidence-sha256-unix.txt` files appears in the original-file manifests. The original native execution log records download-time counts before those copies were generated. All report rows naming an artifact path and full SHA were also rehashed successfully.

The original framework logs, after removing ANSI color codes, contain seven projects per run with passed counts 444/153/80/149/517/89/397, totaling 1,829, with failed/skipped 0. Both PG evidence manifests match 12/12 entries; both wrappers report plannedExit 0 and no failed stages. These are audits of existing CI executions, not new local PG or CI runs.

The [push run 40273525993](https://github.com/liuchangready/majiang-game/actions/runs/40273525993) checks out exactly `978a15667f80094ed63c86f69c53195439c95a2c`. The [PR run 40273530505](https://github.com/liuchangready/majiang-game/actions/runs/40273530505) checks out merge ref `a3ac2fefd38c09dba6bf6c057752d28fc1461d6b`; its parents include the frozen head. The push run supplies the exact-head evidence. CI logs have summary counters rather than individual test names; the local XML supplies the named-case evidence. PG evidence belongs to Lobby storage, not P5.2 TableRuntime persistence.

## Unified publication and remaining boundaries

The caller integrates into an isolated worktree, `D:/majiang-wt/codex-978a-ci-accepted-20261026`, based on published PR19 head `d9d17d8299fc348149f581a0741ddd07b1f519f6`. That base already contains the accepted P2.2 platform record and settlement AuthorityUnavailable regression from this period. The new net change is only the accepted report plus this caller record; no production, test, verifier, fixture, or Unity configuration changes are added. Other tasks still under rework are excluded from this publication and do not invalidate this task.

Publication uses a normal fast-forward push to the existing `feat/v5-1-integration` branch and the single [PR19](https://github.com/liuchangready/majiang-game/pull/19), targeting `v5`, with its actual publication SHA recorded through `record_merge_request`. No component PR or merge into v5 is performed. The main checkout's user edits remain untouched.

The 1,829-case CI result certifies frozen `978a156`, not a full-suite execution on subsequent integration heads. Historical failure 39970580589 remains UNRESOLVED. This task does not certify P5.2, new Unity/IL2CPP/device runs, or cross-physical-network behavior. Overall G1 remains unapproved, G5 incomplete, V4 remains the sole production architecture, and no `complete.flag` is created.
