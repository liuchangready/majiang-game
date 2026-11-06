# V5 progress and repaired-head CI audit, 2026-10-28 02:45 UTC+8

G1 remains APPROVED; G2–G5 remain unapproved. V4 remains production.
This invocation accepted no receiver implementation and started no new task.

## Baseline and ownership

Fetched `origin/v5` remains `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`.
PR19 remains merged. The only open review batch targeting v5 is
[PR20](https://github.com/liuchangready/majiang-game/pull/20), source
`467b64b8e1f044adfc50b0ecd9dfee7d31f0eed8` at inspection.
Caller created `D:/majiang-wt/codex-progress-20261028-0245`, branch
`wt/codex-progress-20261028-0245`, from fetched origin/v5, then fast-forwarded
that reviewed PR20 source. This report is its only tracked change.
Ancestry and `git diff --check origin/v5..HEAD` pass. Main checkout and all
receiver dirty files, Unity assets/Resources/environment/Burst and frozen
baselines were preserved. No receiver code was copied into this batch.

## Independent original CI evidence

Both original artifact bundles and full job logs were downloaded. These are
completed original runs, not new local executions or blind retries.

| Run | Actual checkout | Independently audited result |
|---|---|---|
| [push 40558742508](https://github.com/liuchangready/majiang-game/actions/runs/40558742508) | `467b64b8e1f044adfc50b0ecd9dfee7d31f0eed8` | 2,286/2,286 PASS |
| [PR 40558752011](https://github.com/liuchangready/majiang-game/actions/runs/40558752011) | `e27ad53a7fdf17663b53ebf72d86fd92694e5e47` | 2,286/2,286 PASS |

Fetched the exact synthetic PR object: its parents are accepted v5 `8fd7c6c`
and source `467b64b`; `git diff --exit-code 467b64b e27ad53` exits 0.
Both implementation trees are `2aff53604dff65bf931b46fa66b7f7bbc1d14143`.
The API source SHA was not confused with the actual PR checkout.

For each run, all 11 direct runner raw summaries agree with native exit 0,
matching checkout metadata and positive selection. Failed/errors/skipped/not-run
are all zero. Counts per run: Ai 444, Architecture 92, Client 182, Client.Wss 80,
Contracts 149, Domain 529, Infrastructure 35, LobbyService 89, TableRuntime 400,
TableService 152, linked Unity.TableModel 134. Three build-only helpers add no tests.
The fresh solution build has 0 warnings/errors; 12-assembly closure and all four
companion fixtures pass. Historical companion git-pin verification is NOT_RUN.

The newly added production-launcher probes pass 6/6 in each remote run:
exit 0/1/2/17 propagates exactly, timeout throws, forced writer termination is
nonzero (-1). The outer table wrapper, inner Lobby wrapper and raw unified
runner results agree. The previous `b5c1c17` push FAIL remains FAIL; this new
evidence closes the repaired-head CI follow-up, not the old missing Client
framework-log incident (`39970580589`, still UNRESOLVED).

Real PostgreSQL 16.10 evidence includes 35/35 and targeted 13/13 with all XML
test nodes Pass, committed writer termination, actual database crash/restart,
cold reader recovery and pg_dump/restore into an empty database. Raw recovery
records show stateVersion 1, two events, Applied receipt, restored cheat state
and exact outbox payload; owner epochs are 2 after restart and 3 after restore.
Both wrappers record successful ownership-verified cleanup. Targeted repeats
and launcher probes are not added to the 2,286 unique runner total.
This is storage component evidence; the in-flight durable WSS host is unaccepted.

Evidence root: `artifacts/progress-20261028-0245/` in this worktree. It contains
original downloads/job logs/API metadata, one-read ledger snapshot, tree proof,
`audit-ci.ps1`, `audit-extra.ps1`, their results/logs, pause results and scheduler
inspection. Both final audit scripts exit 0. The first extra-audit invocation
exited 1 because its PowerShell 5.1 JSON-array wrapper counted two run objects
as one; its log is preserved as `extra-audit-first-array-shape-failure.log`.
The audit script array handling was corrected; no source, assertion, test or
remote artifact was changed. A later directory lookup also initially used an
incorrect fixed `native-exit` name; actual GUID directories were then inspected.
The frozen manifest covers 468 evidence files, handling long Windows paths.
Manifest SHA256: `C00BBB8BCD279954325BECC0B71CF70389DD0436FFBC9F4A55F27BB372650065`.

## Allocation and retained obligations

Global recent bridge tasks were listed ONCE across all workspaces.
No waiting_review delivery was present. Initial literal lifecycle counts were
DeepSeek 2, GLM 1, Anti 3, total 6/4: four reworking plus two queued.
Both queued entries retained explicit user manual-pause records from
2026-10-27 22:31 UTC+8. Caller re-applied those existing pauses; each bridge
response returned PAUSED. No task was resumed, retried, retargeted or duplicated.

| Receiver | Active/target after returned pause results | Existing task |
|---|---:|---|
| DeepSeek / deepseek/deepseek-v4.1-flash | 2/2 | Android `thread_20261027_194656_c897`; Unity B2b `thread_20261027_230256_426c` |
| Claude Code / glm-5.3-flash | 1/1 | Durable host `thread_20261027_230302_93fb` |
| Anti / configured gemini-3.8-flash-high | 1/1 | Atomic LocalHost store `thread_20261027_154439_11d2` |
| Total | 4/4 | Four separate worktrees; all existing routes preserved |

Paused and reserved: furiten/Ron `thread_20261027_154459_1c10`, control
reconciliation `thread_20261027_154500_f0b7`. Failed candidate task
`thread_20261027_154438_fc96` and failed parent `thread_20261026_163026_eac6`
retain unaccepted code and shared LocalHost reservations. No automatic retry
or merge-back discharge was inferred from their status. Older timeout/watchdog
assessments remain visible on reworking threads; lifecycle is not acceptance.
Recent completed components have recorded PR20 merge-back records.

Inspected working HEADs: Android `fcce34e`, Unity B2b `49889e0`, durable host
`c0f16eb`, LocalHost `e3e988e`; each has uncommitted files. None is accepted
as a new delivery. Unity's generated/resource/scene changes were left untouched.
No model discovery/dispatch call was needed because no task could be added.

## Gates and next dependencies

P4.2-B production composition and P4.5 require accepted LocalHost and completed
Unity B2b. P5.4b full HTTPS/WSS/PostgreSQL multi-process E2E follows accepted
durable-host composition. G4 requires G2+G3 and actual devices/network evidence.
181 classified remains uncovered; Android remains FAIL pending accepted repair,
iOS BLOCKED, real devices/cross-physical-network qualification NOT_RUN.
No new local fresh code/PG/Unity/device tests were run in this invocation.

Scheduler: enabled, PT30M, IgnoreNew, currently Running. complete.flag is absent.
Publication uses existing PR20 only; no merge to v5, release, deployment or
complete.flag creation is performed. Report publication CI must be assessed
on its own run; this audit names only the exact repaired source above.
