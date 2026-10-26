# G1 gate 2 golden and companion increment review: caller acceptance

Date: 2026-10-26. Existing bridge thread: `thread_20261026_121817_acea`, round 3.
Candidate: `289969a948a3884f974242fd631813901422bbbc`.
Frozen review baseline: `08c4a75345484aadaf47b30f340992532f9c6ce2`.

Caller verdict: **PASS, no known defects in this task's scope**. The existing bridge `approve_task` returned `COMPLETED`. No new task, session, rework or delegation was created.

The candidate adds exactly the authorized `V5-G1-GOLDEN-REVIEW-ANTI-20261026.md` report, with clean status and diff-check. No production, tests, fixtures, catalog, manifest, shared ledger or verifier changes are included. D-R2-01/02/03 are resolved: real tracked paths and resolvable ancestor SHAs replace the bad references; registry anchors and counts are restored; provenance coverage is accurately bounded; evidence paths, subjects and rounds are explicit. The caller rechecked all 15 cited evidence hashes, the four accepted ancestor commits, source pin fields, consumers and independent acceptance records.

The report supports **gate 2 PASS under the accepted hard-closeout rule**. The four companion fixtures remain separate increments. AI has an accepted independent registry in `V5-N3-AI-DISCARD-FLAGS.md` section 8; the other three have shared-ledger section 8/8a registrations. The ledger whitelist rejects AI companion injection despite its omission from the supplemental blacklist. Shared-index maintenance and a unified companion checker remain nonblocking recommendations, not invented closeout conditions.

Repository-level companion hash validation remains **NOT_COVERED**. C# tests protect only fields with actual assertions. Replacing `provenance.generatedAtHead` with forty zeroes still passes six tests; this is an explicit coverage counterexample, never a claim of full provenance validation.

## Fresh caller verification

Evidence root E: `D:/majiang-wt/g1-golden-review-anti-20261026/artifacts/TEMP/caller-gate2-r3-20261026` (ignored).
All scripts use process-local TEMP/TMP under E. Commands run from the candidate worktree:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File <E>/runtime.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File <E>/verifiers.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File <E>/audit.ps1
```

The runtime script first builds `Mahjong.Ai.Tests.csproj` in Release with `--no-incremental --nologo`, then invokes the fresh DLL directly with `-class Mahjong.Ai.Tests.AiDiscardFlagsGoldenTests -result-xml <E>/<case>.xml`. It requires six selected tests, zero errors/skips/not-run, and checks actual exits and failures for each case.

| Check | Native exit | Actual result |
|---|---:|---|
| Fresh AI build | 0 | Zero warnings/errors |
| Original direct runner | 0 | Total 6, passed 6, failed/errors/skipped/not-run 0 |
| Mutated fixture ID | 1 | Total 6, passed 5, failed 1, errors/skipped/not-run 0 |
| Mutated provenance SHA | 0 | Total 6, passed 6; documented NON_COVERAGE |
| Original catalog verifier | 0 | 6 fixtures / 73 requirements / 10,389 cases / 10,000 random / 45 facts; full17/partial21/none32/v5-only3 |
| Original ledger verifier | 0 | 24 CHECK-OK, 44 evidence pins, 14 package digests, 411 bound / 181 classified / 0 pending |
| AI companion injected into ledger | 1 | Exactly 4 semantic failures: fixture-unknown/id-shape/ledger-leak/ledger-extra |
| Two classified B6 rows forged as bound | 1 | Exactly 11 semantic failures: bound-anchor x4, bound-commit x2, verdict-vector x2, verdict-total x2, adjudication-pin x1 |
| Mandatory classified reason removed | 1 | Exactly 1 classified-reason failure |

The original ledger verifier runs from the current candidate with `-RepoRoot` pointing to the existing provisioned mirror at `artifacts/TEMP/anti-g1-gate2-review-20261026/mirror`. Before invocation, the script compares the mirror ledger and verifier to the candidate modulo EOL. The original evidence root and accepted mixed raw-byte EOL provisioning are retained. No tracked bytes or pins are refreshed. The original verifier validates all 44 evidence hashes itself.

`audit.ps1` checks paths and hashes from the report's actual evidence column. Its first invocation also counted the command-column XML path, causing a wrapper count error; the corrected script reads only the evidence column and verifies 15/15. This caller-script correction did not change the candidate or prior evidence.

The negative-ledger input files are explicit overrides, and their rejections are semantic rather than missing-input/compile failures. The verifier runner completed with exit 0. Its first JSON serialization included PowerShell string metadata; `verifier-summary-compact.json` retains the same exits and exact tokens without that metadata. Original logs and the initial summary are retained. `sha256.json` covers the scripts, inputs, logs, XML and summaries (manifest SHA256 `E3AFD8E2736ED8D4BDD7B31C53C824A78BA7251AEC365D3A57AED8E9518DFA0B`).

| Evidence under E | SHA256 |
|---|---|
| build.log | `05B0ED8989629B9739566FD3B06E1D19FDC4B6E99F95547730EFC7E6B5839A81` |
| original.xml | `B03286B88014CB525CD74978AA11DB19C6A4B3DBCBEB09078C8452AD70190209` |
| mutated-id.xml | `5C62033096863A5E2954A7F3B436327BEA9A78B9A8B2529EDE4923A8B962CD7F` |
| mutated-provenance.xml | `714EC87340F3BF953D516AAD387CF3017BF3A2DB50F4698C4327C984C845C94B` |
| catalog.log | `47B1DFE44E6DCDBE219BED47DA0FB5BB2D1813930DDDA560FDDCB0288D5B7C6E` |
| ledger.log | `B0189E0E94DBC6A96C36A6F349AA7E3B0CEFE5B4197A4308B221ACA28707F72E` |
| negative-ai-fixture.log | `F483F22E2E7D17B118A095FF2F6399630E76A51EE5E9514E82E4678963E7F6C5` |
| negative-bound.log | `80A5BB8F41F25B6B3F6C052F3F877A444F949176398CC1317C535BF1520D218B` |
| negative-reason.log | `DD8ABAC1425D48DD1CF9F8E30C54B71F1487A3F21151EF1D230EAB8FE21CD305` |
| audit.log | `FC5A7297C4278F9713A16A05E5F7E45B7ED2B565F9EBDE3822FDF61051E8CE9A` |
| integration-catalog.log | `E6A02E6FE5E9EA63DFF11F23AE28F00D940D11C2EEF0198BF1A789436EC6F523` |

## Unified publication and boundaries

Isolated integration worktree: `D:/majiang-wt/codex-gate2-acceptance-20261026`, based on published PR19 head `74216a701859ed8efce3302c15a366a01b9fd402`. This retains the already accepted P5.4a session-operation changes. Merge `c3f3615` adds the candidate report without conflict or content changes. This caller record is the only additional tracked file. Implementation, tests, tools and fixtures equal the published head; the original catalog verifier also passes in the integration worktree. No unaccepted sibling work is included; main-worktree user edits remain untouched.

Publication reuses the existing single [PR19](https://github.com/liuchangready/majiang-game/pull/19), source `feat/v5-1-integration`, base `v5`, with one normal fast-forward push and bridge `record_merge_request` using the actual publication SHA. No component PR is created and PR19 is not merged.

Accepted ledger: **592 = 411 bound + 181 classified + 0 pending**, 14 packages. The 181 classified rows remain uncovered, with accepted owners and revisit obligations. Frozen 1516-path/655145-node/592-GAP/17-UNA denominators are unchanged. This accepts the gate 2 review and companion increment check only. Other eight gates and P2.2 remain with their own tasks; no dependency is used to reject this report. Full runtime, PG/WSS, Unity/IL2CPP, devices, physical networks and new remote CI were NOT_RUN in this task. G1 overall remains unapproved, G5 incomplete, V4 the sole production architecture, and no `complete.flag` is created.
