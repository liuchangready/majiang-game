# V5 progress and current-source CI audit, 2026-10-28 00:45 UTC+8

G1 remains APPROVED. G2–G5 remain unapproved; V4 remains production.
This invocation accepted no receiver code, dispatched/resumed/retried no task,
and performed no deployment, merge to v5, or complete.flag creation.

## Baseline and review ownership

Fetched `origin/v5` remains `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`.
PR19 remains merged. The only open batch review targeting v5 is
[PR20](https://github.com/liuchangready/majiang-game/pull/20), whose inspected
source is `c0f16eb77c70296978f658dd5382b9288d556e05`.

Caller created `D:/majiang-wt/codex-progress-20261028-0045`, branch
`wt/codex-progress-20261028-0045`, from fetched origin/v5 and fast-forwarded the
reviewed PR20 source. Ancestry and `git diff --check origin/v5..HEAD` passed.
This report is the only tracked change prepared here; all receiver changes,
the dirty main checkout, Unity Resources/Burst/environment files and frozen
in-flight baselines were preserved. Publication uses the existing PR20 branch.

## Current-source remote checks independently audited

Both original artifacts and job logs were downloaded. These are original runs,
not blind retries or a CI claim inferred from a green badge.

| Run | Event | Actual checkout | Raw test result |
|---|---|---|---|
| [40526410333](https://github.com/liuchangready/majiang-game/actions/runs/40526410333) | push | `c0f16eb77c70296978f658dd5382b9288d556e05` | 2,286/2,286 PASS |
| [40526647949](https://github.com/liuchangready/majiang-game/actions/runs/40526647949) | pull_request | `dc41ded86e28af2f17a729be2a29c71eb398a74a` | 2,286/2,286 PASS |

The PR checkout's parents are accepted v5 `8fd7c6c` and source `c0f16eb`.
Fetched its exact object and verified full-tree equality to source using
`git diff --exit-code c0f16eb dc41ded` (exit 0). Both trees are
`35ce050ca372ad6170e26275226f3e5c692cff4b`.

For each run, the caller audit checked all 11 direct runner logs against their
per-invocation metadata: positive selection, native exit 0, matching checkout,
and zero failed/errors/skipped/not-run. Each run contains:

| Project | Passed/selected |
|---|---:|
| Client.Wss | 80/80 |
| TableRuntime | 400/400 |
| Ai | 444/444 |
| Architecture | 92/92 |
| Client | 182/182 |
| Unity.TableModel (linked production source, .NET runner) | 134/134 |
| Contracts | 149/149 |
| LobbyService | 89/89 |
| Domain | 529/529 |
| TableService | 152/152 |
| Infrastructure | 35/35 |
| Total per run | 2,286/2,286 |

Both fresh Release solution builds record 0 warnings/errors; 12-assembly plugin
closure and four companion fixtures pass. The summary's 14 project rows include
three build-only helpers, which contribute no test cases. Historical companion
git-pin verification is explicitly NOT_RUN in these CI invocations; current
bytes and embedded provenance were checked by the existing gate.

For both runs, PostgreSQL wrapper JSON, original test XML and raw recovery logs
were inspected. PostgreSQL 16.10 loopback table storage ran 35/35 and targeted
13/13 with zero failed/errors/skipped/not-run. Writer termination after durable
commit, actual database crash/restart, cold reader recovery and pg_dump/restore
into an empty database all record PASS. Separate Lobby PostgreSQL migration,
real concurrency/replay/rollback evidence and ownership-verified cleanup pass.
These repeated targeted/storage selections are not added to the 2,286 total.
This does not qualify the in-flight production durable-host composition task.

Evidence root:
`D:/majiang-wt/codex-progress-20261028-0045/artifacts/progress-20261028-0045/`.
It preserves both downloaded artifact trees, original job logs and metadata,
`audit-ci.ps1`, `audit-result.json`, `tree-proof.txt`, `ledger-snapshot.json`,
`scheduler.json` and `evidence-sha256.csv`. The audit script exited 0 and checked
22 runner summaries plus four real-PG result XMLs. The frozen manifest hashes
419 files with no omissions (long Windows paths handled explicitly).

## Allocation and outstanding reservations

The global recent-task list was requested ONCE, without workspace filtering.
No waiting_review task was present. Counting queued/running/reworking entries
as required gives the following inherited allocation:

| Receiver | Running/reworking | Queued | Active/target |
|---|---:|---:|---:|
| DeepSeek Harness / deepseek/deepseek-v4.1-flash | 2 | 0 | 2/2 |
| Claude Code / glm-5.3-flash | 1 | 0 | 1/1 |
| Anti / configured gemini-3.8-flash-high | 1 | 3 | 4/1 |
| Total | 4 | 3 | 7/4 |

- DeepSeek Android repair: `thread_20261027_194656_c897`, reworking.
- DeepSeek Unity B2b event consumption: `thread_20261027_230256_426c`, running.
- GLM durable TableService composition: `thread_20261027_230302_93fb`, running.
- Anti LocalHost candidates: `thread_20261027_154438_fc96`, running.
- Anti LocalHost atomic store: `thread_20261027_154439_11d2`, queued, round 4.
- Anti LocalHost furiten/Ron: `thread_20261027_154459_1c10`, queued.
- Anti LocalHost control reconciliation: `thread_20261027_154500_f0b7`, queued.

The four Anti entries share the same LocalHost worktree and overlapping source
scope; this is serialized ownership, not four independent physical workspaces.
The historical manually-paused c/d instruction remains binding: the current
ledger says queued, but this caller did not resume, retarget or duplicate them.
Some active entries also retain earlier TIMEOUT/WATCHDOG/RATE_LIMITED round
assessments; ledger lifecycle alone is not delivery or acceptance evidence.
With seven active/pending entries already present, this invocation adds none
and does not disrupt inherited tasks to manufacture the requested allocation.

Failed parent `thread_20261026_163026_eac6` still has unaccepted LocalHost
merge-back obligations; child scopes retain ownership. The old failed contract
preparation thread `thread_20261025_194659_8d8a` was superseded by accepted
contract/audit/addendum work and was not duplicated or deleted. Recent completed
Client/P5.1/Unity components have PR20 merge-request records. No in-flight dirty
files were included. LocalHost HEAD was `e3e988e`, with candidate/driver/tests
dirty; its queued store rework still records a cold-recovery defect. Neither
that draft nor its old passing subset counts as accepted P4.1.

## Gates and next dependencies

P4.2-B production composition and P4.5 full local-match/recovery remain blocked
on accepted LocalHost and the remaining Unity event integration. P5.4b full
HTTPS/WSS/PostgreSQL end-to-end verification follows accepted durable-host
composition; storage component tests do not replace that work. G4 requires G2
and G3 plus real platform/device/network evidence; no G2–G5 approval is given.

The 181 classified coverage gap remains uncovered. Android remains FAIL pending
the existing repair's actual accepted evidence; iOS remains BLOCKED, and device
and cross-physical-network qualification remains NOT_RUN. No local fresh code
tests, Unity runtime/device tests or new PG cluster were run in this invocation;
the useful independent work was the current-source original CI artifact audit.
The old missing-framework-log incident is not diagnosed as flaky or resolved
by these newer passing runs.

Scheduler inspection: enabled, PT30M, IgnoreNew, currently Running;
`C:/Users/sncdc/.codex/automations/mahjong-v5/complete.flag` is absent.
