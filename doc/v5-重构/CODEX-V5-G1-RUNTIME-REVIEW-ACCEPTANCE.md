# G1 runtime eight-gate review: caller acceptance

Date: 2026-10-26. Existing bridge thread: `thread_20261026_121817_a3cb`, round 2.
Frozen baseline: `08c4a75345484aadaf47b30f340992532f9c6ce2`.
Accepted candidate: `9b48523add741146e911aa641e630ed5f2711118`.

Caller verdict: **PASS for this task's review/report scope**. `approve_task` returned `COMPLETED`. D-R1-01 and D-R1-02 are closed. No new task, session, delegation or rework was created.

The candidate adds exactly `V5-G1-RUNTIME-REVIEW-20261026.md` relative to the frozen baseline. The old unauthorized filename is absent; the title names eight gates (1/3/4/5/6/7/8/9); status is clean and diff-check exits 0. Report SHA256: `E10BEE9B459E58720ABB5CC2DFD666AA5FD2B712AC3A7E8BFEE88189E4E4FB8E`.

The report now distinguishes definitions from expanded cases: RoundDecision 24 Fact -> 24 cases, N1b 40 Fact plus one Theory with two InlineData -> 42 cases, N2Host 22 Fact -> 22 cases. The 88 cases are correctly attributed to the caller's earlier directed run, whose ten-class total was 199. Contracts full-suite counts explicitly mean total/passed 149 and failed/skipped 0. `ProtocGenerationIsRepeatable` is included in the 13 SchemaCompatibility cases and actually invokes protoc twice, then compares generated code and descriptor bytes.

## Fresh verification in the candidate workspace

Evidence root E: `D:/mj-g1rt-caller-r2-20261026-verify` (outside the repository).
Process-local TEMP/TMP point to E/TEMP. The caller ran:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:/mj-g1rt-caller-r2-20261026-verify/verify-r2.ps1
```

The script freshly builds Contracts.Tests, Domain.Tests and TableRuntime.Tests using `D:/Unity/dotnet/dotnet.exe build <project> -c Release --no-incremental --nologo`. All three native exits are 0. Only after successful builds, it invokes the test DLLs directly using `-class <qualified-class> -result-xml <E>/<class>.xml`. Native exits and XML selection counts are checked independently.

| Class | Total | Passed | Failed/errors/skipped/not-run | Native exit |
|---|---:|---:|---|---:|
| SchemaCompatibilityTests | 13 | 13 | 0/0/0/0 | 0 |
| RoundDecisionTests | 24 | 24 | 0/0/0/0 | 0 |
| N1bCheatExecutionTests | 42 | 42 | 0/0/0/0 | 0 |
| N2HostSettlementTests | 22 | 22 | 0/0/0/0 | 0 |
| Total | 101 | 101 | 0/0/0/0 | 0 |

The caller also rehashed all 22 artifacts cited in report section 13. The existing local PG bundle and both CI bundles each have 1,808 passed, zero failed/skipped, seven framework logs matching their metadata hashes, and twelve matching PG manifest entries. Combined: 21 framework logs and 36 PG entries match. Each wrapper has native exit 0, no failed stages, successful owned-cluster cleanup and restoration of all four environment variables. These are audits of immutable earlier full runs, not newly executed PG/CI runs.

Read-only `gh run view` confirms runs [40266679969](https://github.com/liuchangready/majiang-game/actions/runs/40266679969) and [40266677478](https://github.com/liuchangready/majiang-game/actions/runs/40266677478) completed successfully, metadata head `08c4a75`. The PR artifact's actual checkout is merge ref `9bfa01b014e34e70c58235f2b2817a9b75e8664a`; the push artifact checks out the frozen head itself. Historical failed run 39970580589 remains UNRESOLVED; newer green runs do not establish its cause or repair.

| Evidence under E | SHA256 |
|---|---|
| verify-r2.log | `79D642E793EA3D43ED6BAEEA2C50CA2B08E4461192BFB94458F36F6421887416` |
| tests.json (counts, native exits, commands, XML hashes) | `9F5DB2F72F839C40F07313792A1F447A1288E337BD79B81FA0812A468109A9D9` |

## Unified publication and boundaries

Isolated integration worktree: `D:/majiang-wt/codex-g1-runtime-accepted-20261026`, based on published PR19 head `cbfa44154b298fb797e486b6de7f39f36e1eff5b`. The merge retains already accepted P5.4a and gate 2 changes. The only added files are the candidate report and this caller acceptance record. Production code, tests, fixtures, catalog, ledger and tools are unchanged from that published head. No unaccepted P2.2 work is included; main-worktree user edits remain untouched.

Publication reuses the existing single [PR19](https://github.com/liuchangready/majiang-game/pull/19), source `feat/v5-1-integration`, base `v5`, through a normal fast-forward push and `record_merge_request` with the actual publication SHA. PR19 is not merged.

This accepts the frozen-head eight-gate review as an input to caller adjudication. It does not certify a new full-suite run on the newer P5.4a integration head. The original AuthorityUnavailable test gap (MINOR) and the report's environment/platform limits remain recorded. Gate 2 has its separate accepted review; P2.2 is still being reworked and is excluded here. Overall G1 remains unapproved, G5 incomplete, V4 the sole production architecture, and no `complete.flag` is created. New Unity/IL2CPP, Android/iOS/device and cross-physical-network validation was NOT_RUN in this task.
