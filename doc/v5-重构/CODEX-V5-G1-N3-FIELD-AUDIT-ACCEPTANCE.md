# N-3 frozen golden field audit: caller independent acceptance

Thread: `thread_20261025_051932_5eeb`; delivery: `8f6d90c` on `wt/v5-n3-golden-field-audit`; audit baseline: `8254c9b783740ddf519cebc15bb50a8f9974cf58`.

This accepts the audit report component and its embedded executable evidence. The report is a historical audit of the frozen baseline, not a current-head inventory. Subsequent golden binding implementations already in PR19 do not rewrite these frozen findings. N-3, G1 and G5 remain unapproved; V4 remains production.

Delivery scope is one document, `V5-G1-N3-GOLDEN-FIELD-AUDIT.md`; no production, fixture, catalog, manifest or test source was changed. Report SHA256: `ecae055e29afd7e3d83192031e01876edc36367c79b91db3d3515d51967a3e7f`. The caller integration retains the report bytes and Git blob, and adds this acceptance record. User changes in the main checkout were left untouched.

Fresh tools were extracted into `D:/majiang-wt/codex-n3-r7-evidence-7816dc7072d04451862c965b0c0029d7` (E). All nine source pieces and the rules TSV match their embedded SHA256 pins. The rules remain `24959db1f5bc463b2d61e0072e01c95d117c4398b6a929a1aa6b6c28a3e44a31`.

- Release Domain.Tests and Ai.Tests builds: native exit 0, zero warnings/errors. Six golden classes: 9/7/6/16/4/3 = 45 passed, zero failed/skipped/not-run; each native exit 0.
- Fresh FullCensus and CensusInstances: native exit 0; 1516 normalized paths and 655145 nodes. `instances.tsv` SHA256 `049a097b9d37ed01931835e18cbee6a9db5809826e090dc36ce51187787c6a12`. Coverage verifies zero unmapped paths.
- Mapping v5 baseline: native exit 0; 542 rules, 655145 units, 592 gap units, UNA 17, 27 fixture-qualified mixed paths, all in cheat-v1. The path projection remains 101 and is distinct from the complete instance denominator.
- R6-1: independent oracle recomputes all 27 mixed class sets and reports zero mismatches; all seven old cross-fixture witnesses are clean. Deliberately assigning one checker output line to round-v1 produces native exit 1 and two oracle mismatches. Rotating all scoped sibling families returns native exit 0 with all 1567 CASE-/PATH-UNITS output lines byte-identical; the historical first-match DIST projection changes as documented.
- R6-2: the direct caller-shape mutation changes the unscoped scoring status rule from UNBOUND to UNA. Native exit 1, exactly 48 `UNA-NOT-NULL` instance errors, and no unrelated error substitution. The built-in unscoped negative and prior scoped negative are also retained.
- Handler census: 75 cases, A16/B2/C6/D50/E1, native exit 0. The fresh report-extracted round-five driver matches all sixteen mutation expectations and completes with native exit 0; its five deliberately failing bound-value controls each produce native exit 1. These expected failures demonstrate coverage, not regressions.

All sixteen mapping negatives passed their wrappers with native exit 0 and rejected the mutations with the following exact error totals:

| Kind | Check errors | Result |
|---|---:|---|
| strip-anchors | 336 | NEGATIVE-DETECTED |
| drop-rule | 2 | NEGATIVE-DETECTED |
| anchorless-rule | 4 | NEGATIVE-DETECTED |
| nonexistent-anchors | 786 | NEGATIVE-DETECTED |
| duplicate-rule | 1 | NEGATIVE-DETECTED |
| scalar-container | 324 | NEGATIVE-DETECTED |
| scope-extra-case | 1 | NEGATIVE-DETECTED |
| scope-drop-case | 1 | NEGATIVE-DETECTED |
| scope-unknown-case | 2 | NEGATIVE-DETECTED |
| scope-dup-case | 1 | NEGATIVE-DETECTED |
| scope-cross-section | 1 | NEGATIVE-DETECTED |
| scope-where-broken | 1 | NEGATIVE-DETECTED |
| where-value-broken | 2 | NEGATIVE-DETECTED |
| una-drop | 11 | NEGATIVE-DETECTED |
| una-nonnull | 17 | NEGATIVE-DETECTED |
| una-unscoped-nonnull | 48 | NEGATIVE-DETECTED |

E/review-summary.json SHA256: C53212EE5606109517AAEDF91DB79F5A50F0EF531FCA50902E06F2C88303A4AD.
E/sha256.json SHA256: 53685D5EF7290B13379A6061BEEA75736EB70577F0A39D1043550E171E457B4B. The manifest snapshots the validation scripts, inputs and raw logs before publication. E/finalize-evidence.ps1 asserts the complete matrix and the baseline/test/probe invariants; native exit 0.

Environment: the initial attempt to launch CensusInstances from C: failed because C: had zero free bytes. All new evidence was copied to E and process-local TEMP/TMP moved to E/temp; CensusInstances and subsequent builds/tests then passed. No system environment, existing artifacts or user files were deleted. Logs are PowerShell-redirected UTF-16; the oracle receives an explicitly UTF-8 copy of check.log. Raw log hashes therefore need not equal receiver hashes, while deterministic census/tool/rule byte pins do match.

No new runtime behavior was introduced. Domain rules, terminal-state/136-tile conservation, scoring, deadlines, concurrency/recovery and privacy implementations remain unchanged. PG/WSS/Unity/IL2CPP/Gradle/devices/physical-network and this publication's remote CI are NOT_RUN and are not claimed passed. Acceptance of this report does not accept the remaining golden-binding work or close migration gates.

Integration: source report merged without conflict into PR19's published head f9fd8e8 at f057b928646d0864fbd4442ccd35bb0da1fdb8a8. Only this report and this caller acceptance record are added by the publication; runtime trees and fixtures match the prior PR19 head. Worktree checkout initially applied CRLF; the report was copied byte-for-byte from the approved delivery before staging, preserving its documented raw SHA. No unrelated unpushed integration commits existed at review start. This publication reuses the open PR19 (feat/v5-1-integration -> v5); no per-task PR is created.

Bridge approve_task returned COMPLETED and archived the same thread on 2026-10-25. The task's original callerSessionId is retained; no task, agent or native receiver session was replaced.
