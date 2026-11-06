# D-A68-02 independent acceptance

2026-10-31. Thread: `thread_20261031_164807_3177`, round 2.
Reviewed delivery: `35461677216fd37d8b70f60a8d1ee1d028594786`.
Batch base: `355bf8750d7ea15fc586b87920ebfca2bfb72122`.
Implementation: `b4c04d31a85256e1d233e76995bd23cf9172d348`;
identity/schema repair: `6a0876d980c59cb5f4b69b2e17ad471423e77c67`.

**Verdict: accept this component.** D-DNT-R1-01 is resolved; no known material
defect remains in the reviewed task scope. This is not batch, phase, deployment,
Unity, mobile or remote dotnet-test CI acceptance.

The real `dotnet test` invocation now requests native xUnit per-case XML, retains
its raw framework/console output and exit, and verifies the current assembly,
fresh dedicated output path, native case IDs, collection/case/assembly counts,
runner identity, supported schema and agreement with the console and exit.
Duplicate display names are allowed; duplicate native IDs are rejected.
The repaired gate matches the complete locked framework/version token with
optional build metadata and explicitly requires native schema 3. The existing
direct gate remains unchanged. PostgreSQL wrappers forward an explicit runner
mode and retain their direct default. Workflow and business sources are unchanged.

## Caller execution at the reviewed delivery

Evidence root in the delivery worktree: `build/caller-dnt-r2-165fd4/`.
These commands were actually executed by the caller, with separate raw logs.

| Check | Result |
|---|---|
| Expanded production-gate self-test | 48/48 expected outcomes, native exit 0 |
| Original caller boundary probe, unchanged | 4/4 expected outcomes, native exit 0 |
| Existing direct-result regression | 19/19, native exit 0 |
| Existing captured-exit regression | 51/51, native exit 0 |
| Existing native-exit regression | 6/6, native exit 0 |
| Four changed PowerShell scripts parsed | 0 parse errors |
| Unified Release solution build | native exit 0; 0 warnings/errors |
| Unified real dotnetTest Architecture | 132/132; native XML gate PASS |
| Unified real dotnetTest TableService | 180/180; native XML gate PASS |
| Scratch plugin build/copy | PASS, native exit 0 |
| Mandatory companion certification | 4/4 accepted fixtures, PASS |
| Unified overall | PASS, native exit 0; 312 cases; zero fail/skip/not-run/errors |

The self-test includes actual live `dotnet test` probes: three passing cases
(including duplicate display names), and a controlled failed case with native
exit 1 that the production gate correctly rejects. Synthetic negative gate
fixtures are not represented as executed business tests.

The original boundary probe SHA-256 remains
`9DA07FFA1E5861C7EA082BE11BFA688F6D8AEF77DACFFBA42426EE551AC9CA71`.
Its positive framework/schema pair still passes. Wrong patch `4.0.20`, appended
`INVALID` and schema `999` now fail for the appropriate identity/schema reason.
The expanded matrix also covers other patch versions, empty build metadata,
wrong runner family and schemas 2/4. The round-1 failing proof is preserved as
`build/caller-dnt-075f61/boundary-probe-results.red.json`, SHA-256
`1391B6EDAA0B8E25AD98B5C18A605C2F20D810BC2B73B4E6A5DEEEF1D99EC0C8`.

The verifier bytes tested in this round have SHA-256
`41510892820C6B1E72B8BD182C171C23778E992F0E8AD6B87505862CCB1C065B`.
Fresh native XML digests:

- Architecture: `A8CC8C3C6C08A881C366D63E11BD9E47319251481C47EA2B0A6158355F1F3B0E`.
- TableService: `962992D033C18628FA7BFAB89159BC37981F7F6E3BAAFA032741352A863B42F7`.

The raw command/head/path/exit attribution is in `scoped/run-info.txt` and
`scoped/runs/*/dotnetTest.meta.txt`; `scoped/summary.log` records the gate results.
`evidence-sha256.json` inventories the caller evidence. The scoped command used
`-RunnerMode dotnetTest -NoProjectDiscovery` and the existing ignored two-project
manifest, with plugin and companion gates enabled.

## Recorded PostgreSQL evidence replay

The caller checked all 235 size/SHA-256 entries in the receiver's recorded bundle
`artifacts/dotnettest-pg2-20261031-175613/`; all matched. The caller then ran the
corrected production gate against all 14 recorded XML/console/native-exit sets,
using their original run timestamp, assembly paths and metadata: 14/14 passed,
2,732/2,732 cases, zero failed/skipped/not-run/errors. See
`build/caller-dnt-r2-165fd4/replay/replay-results.json` and `replay.log`.

This is **evidence replay, not new execution**. This round did not start another
PostgreSQL cluster or rerun recovery/disaster-recovery scenarios. Their original
execution, failures and limits remain described in the receiver delivery report.

## Publication boundary

Publish the accepted tool/report commits and this caller report together to the
existing `feat/v5-post-g1-batch` branch and Draft PR20 targeting `v5`; do not open
another MR. The receiver delivery is limited to its original five allowed files;
this caller report is the only added review document. Delta diff-check is clean.
The historical 1,573 whitespace findings are outside this task and remain recorded.

Historical 7fe7dbf double-success CI used direct execution. Remote dotnetTest
per-case CI remains **NOT_RUN**; the present changes do not switch the workflow.
New publication-head CI must be assessed against that exact head separately.
B2 terminal rework and unaccepted P4.5/PR24 are excluded from this publication.
V4 remains production, G1 is approved and G2-G5 are unapproved. Android FAIL,
iOS BLOCKED, classified 181 and physical-network/device NOT_RUN limits remain.
Keep PR20 Draft; no merge, release, scheduler or complete-flag action follows
from this component acceptance.
