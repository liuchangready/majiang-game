# N3 final-six adjudication: caller acceptance

Date: 2026-10-26. Existing bridge thread: `thread_20261026_110220_f3c1`.
Candidate: `eb54476ec7bdc08ef570d94f3313a8f68f64b2c5`, based on accepted `7606c5c1daf2523f1bdb653e439fcc4f3f278cca`.
Independent delivery: `fd9486e04604d683363d6915eb2b7b4d70792e8a`; only its expected 88-line report was added, with clean status and diff-check.

Caller verdict: **PASS, no known defects in this task's scope**. The existing bridge approve_task returned COMPLETED. No extra rework round, new thread or agent delegation was used.

## Independent caller checks

The caller reviewed the report, the three candidate files, accepted residual decisions and B6 evidence, and the actual companion fixture/C# consumption points. A separate structural script checked all six identities (54/54 identity fields), all 586 unchanged raw rows, all 411 unchanged bound rows and all 37 original evidence pins. Seven added pins are backed by accepted evidence. Verifier changes remain limited to counts, seven pins, three package digests and success text; algorithms and checks are retained. The independent report's two full log SHA values were checked against actual files.

Four cheat boundaries remain precise: alias/materialisation is no rule exemption; the capacity typo creates no rule; no-round and no-open-decision are not equivalent replays; an empty filter is not a physically empty dead wall. B6 uses nonempty companion behavior evidence, while frozen ai-v1 stays empty and PlayerView lacks per-discard flags. None of the six rows becomes bound/full. Owners, triggers and 2026-11-30 revisits remain.

Evidence root E: `D:/majiang-wt/n3-final-adjudication-review-glm-20261026/artifacts/TEMP/caller-n3-final-acceptance` (ignored).
Mirror M: `D:/majiang-wt/n3-final-adjudication-review-glm-20261026/artifacts/TEMP/glm-n3-final-review-20261026/mirror`.
The mirror's ledger and verifier equal the candidate files modulo EOL; its 44 evidence inputs were freshly hash-checked before execution. It preserves the documented mixed raw-byte EOL requirements and inherited census evidence. No pins were refreshed and no tracked source bytes were changed for provisioning.

Commands, from the delivery worktree:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File artifacts/TEMP/caller-n3-final-acceptance/structural.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File artifacts/TEMP/caller-n3-final-acceptance/run.ps1
```

The runner generates its own byte-preserving negative copies and invokes:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File <M>/tools/verify-v5-n3-closeout-ledger.ps1 -RepoRoot <M>
# Repeat with -LedgerPath <E>/negA-classified-reason-removed.tsv
# Repeat with -LedgerPath <E>/negB-b6-pseudo-bound.tsv
```

| Actual run | Native exit | Result | Log SHA256 |
|---|---:|---|---|
| Baseline | 0 | 24 CHECK-OK, zero failures; 44/44 hashes and 14/14 digests | `8BDA74BD1BA0CEFE5A2942A439A0CF647FAD6B568DFC4470BCDFB84322F8EA7F` |
| NegA: remove new classified reason | 1 | Exactly one classified-reason failure; digest still passes | `BDD19B663415323AD3B935E9CFC889D491681AE5B7C6E715ACE2FCC7927B3CF1` |
| NegB: upgrade two B6 prose rows to bound | 1 | Exactly 11 failures: bound-anchor x4, bound-commit x2, verdict-vector x2, verdict-total x2, adjudication-pin x1 | `C7564B3592E2589EA36768C0FB76C5B0CC35CC7FE8222F4708DF72C857D8CF8F` |

Logs are E/baseline.log, E/negA.log and E/negB.log. Structural log SHA256: `0B4A3079309C02C22668B460116BF9DD643BFDD914EBF972BE5555430E00223F`.
The runner completed with exit 0 and CALLER-ACCEPTANCE-OK; summary.json retains numeric exits and actual tokens. Neither negative was rejected for missing dependencies. The first caller wrapper invocation failed while generating negatives because a BOM-less PowerShell script contained a Chinese literal path; the corrected character-built path ran all three checks. This invocation issue did not change the candidate. The initial JSON summary included PowerShell metadata; summary.json removes that metadata without changing logs or results.

## Unified publication and remaining gates

Isolated integration tree: `D:/majiang-wt/codex-n3-final-closeout-20261026`. It starts from published PR19 head `5d3ed03892f3d04ecfa3c24c9aa349ccb9df008a`, preserving the independently accepted P4.1 contract addendum. Merge `233786e08501372c89cfad726914797e16bd9e43` adds the candidate and independent report without conflict; all four reviewed files remain byte-identical to fd9486e. This caller record is the only additional tracked file. Main-worktree user edits to CLAUDE.md and doc/ui/round-preparation/PROMPTS.md remain untouched. In-flight/unaccepted sibling work is excluded.

The caller reuses the existing single [PR19](https://github.com/liuchangready/majiang-game/pull/19), source `feat/v5-1-integration`, base `v5`, with one normal fast-forward push and bridge record_merge_request using the actual publication SHA. No component PR or PR merge is authorized by this record.

The accepted ledger is **592 = 411 bound + 181 classified + 0 pending**, 14 packages; classified remains uncovered. Frozen 1516-path/655145-node/592-GAP/17-UNA denominators are unchanged. PASS covers this documentation/verifier batch only. Runtime behavior uses previously accepted evidence; full runtime, PG/WSS, Unity/IL2CPP, device, physical-network and new remote-CI runs were NOT_RUN here. The companion-fixture catalog increment still needs its own check, followed by all nine G1 gates at one final integration head. V4 remains the sole production architecture; G1 is unapproved, G5 incomplete, and no complete.flag was created.
