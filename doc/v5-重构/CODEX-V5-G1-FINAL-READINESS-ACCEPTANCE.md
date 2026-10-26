# G1 final readiness convergence: caller acceptance

Date: 2026-10-26. Existing bridge thread: `thread_20261026_144755_d25e`, round 2.
Frozen evidence baseline: `71a1fbb0eeeedf19adebb7931e22e1c22a9994d3`.
Accepted candidate: `b04134d2acc2447ff6052a286cecf50ac72098da`.

Caller verdict: **PASS for this task's report and convergence-evidence scope**.
The original thread's `approve_task` returned `COMPLETED`.
D-R1-01/02/03 are closed; no new known defects in this scope.
This accepts the final recommendation as a decision input; it does not itself approve G1 or dispatch P4/P5.

The candidate adds exactly `V5-G1-FINAL-READINESS-20261026.md` relative to the frozen baseline.
The old GLM filename is absent, tracked status is clean, and diff-check exits 0.
Report SHA256: `D6DB1F8432EF03E9E64F2793F2506FEC6D7FAF59379E9346EA887EEDE69E19D1`.

The handoff follows accepted addendum section 3.1: static LocalTableHost.CreateChannelAsync,
internal LocalTableChannel, and the already registered LocalHost project.
P4.2 core can use the unavailable factory independently; P4.2-B waits for the real P4.1 factory.
P5.2 has no ticket dependency; P5.1 can test the frozen ticket contract with a fake signer.
The report enumerates all thirteen changed paths from 08c4a75 to 71a1fbb exactly.

## Fresh caller verification

Executed in `D:/majiang-wt/g1-final-readiness-glm-20261026`.
Evidence root E: `D:/majiang-wt/g1-final-readiness-glm-20261026/artifacts/codex-g1-r2-review-1793000685764` (ignored).
SDK: `D:/Unity/dotnet/dotnet.exe`; TEMP/TMP were set only in the verification process to E.

```powershell
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --no-incremental --nologo
D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.TableRuntime.Tests/bin/Release/net8.0/Mahjong.TableRuntime.Tests.dll -class '*SettlementAuthorityUnavailableTests' -result-xml <E>/authority.xml
powershell -NoProfile -ExecutionPolicy Bypass -File tools/verify-v5-golden-catalog.ps1
```

| Check | Native exit | Result |
|---|---:|---|
| Fresh Release build | 0 | 0 warnings, 0 errors |
| Direct runner and independent XML count | 0 | total 3, passed 3, failed/errors/skipped/not-run 0 |
| Original catalog verifier | 0 | fixtures 6 / requirements 73 / cases 10,389 / random 10,000 / facts 45 |
| Delivered EOL generator, run via D:/Git/bin/bash.exe | 0 | byte-identical final CSV; 25 CRLF + 10 LF + 9 NON_TREE, no NO_MATCH |
| Scope, thirteen-path inventory, five input ancestor assertions, diff-check and clean status | 0 | all passed |

Authority XML SHA256: `A3B8EBA8B421C0C92515AB42FB55C1297BE07807B03364C143C08CAD688124C7`.
Regenerated CSV SHA256: `BB69177F62EB73E54AC3B517D6FDBEBCC2A0DDCD086EAF66B597F15085E4B680`.
Generator SHA256: `6C6EE46CD2820350D7C3E906A55499524E0452EC7FB0EF16E832763782676018`.
Logs: E/build.log, E/authority.log, E/authority.xml, E/catalog.log, E/ev-eol-proof-regenerated.csv.
The generator is retained at `artifacts/g1-final-readiness-20261026/ev-eol-proof-generate.sh`.

Read-only remote checks confirm [push 40279597103](https://github.com/liuchangready/majiang-game/actions/runs/40279597103)
and [PR 40279601879](https://github.com/liuchangready/majiang-game/actions/runs/40279601879) completed successfully.
The caller rehashed the cached push run-info, summary and PG wrapper summary against the report,
and all twelve PG manifest entries match. These are immutable earlier CI artifacts, not a new local full run
or new database execution. The push evidence is for 71a1fbb; the PR checkout remains the distinct merge ref
recorded in the report. The earlier 1,829 and frozen-head 1,832 counts are not relabeled as fresh local runs.

The original ledger verifier's exit 1, seven absent TEMP evidence files, Android build failure,
iOS environment gap and historical run 39970580589 UNRESOLVED status remain explicitly recorded.
The 35/35 EOL-content proof does not turn those checks green.

## Unified publication

Integration worktree: `D:/majiang-wt/codex-g1-final-accepted-20261026-1793000791527`, based on published PR19 head
`c5698412c44b70565ecf4fc89846f998194cbf30`.
The accepted S-1 dependency guard, S-2 Domain scanner and companion verifier candidate heads
are all ancestors of that published head; no additional unpublished changes from those tasks remain to combine here.
This handoff adds only the accepted final report and this caller acceptance record.
Production, tests, fixtures, ledger, catalog and tools are unchanged from the published head.

Publication reuses the single existing [PR19](https://github.com/liuchangready/majiang-game/pull/19),
source `feat/v5-1-integration`, base `v5`, through a normal push and `record_merge_request`.
PR19 is not merged, v5 is unchanged, and the main checkout's user edits are untouched.
