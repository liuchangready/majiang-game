# V5 progress and CI failure review, 2026-10-28

G1 remains APPROVED; G2–G5 remain unapproved. V4 remains production.

## Reviewed baseline and allocation

Fetched origin/v5 is `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`.
PR19 is MERGED at that commit. The one open batch PR is
[PR20](https://github.com/liuchangready/majiang-game/pull/20), reviewed source
`b5c1c170b77105ffba97b6f1a0741c6c9e562a37`.

The global bridge ledger was listed ONCE across all workspaces. No waiting_review
delivery was present. Active counts include queued entries:

| Receiver | Active/target | Threads |
|---|---:|---|
| DeepSeek / deepseek/deepseek-v4.1-flash | 2/2 | `thread_20261027_194656_c897` Android; `thread_20261027_230256_426c` Unity B2b |
| Claude Code / glm-5.3-flash | 1/1 | `thread_20261027_230302_93fb` durable host |
| Anti / configured gemini-3.8-flash-high | 4/1 | `thread_20261027_154438_fc96` candidates; queued `thread_20261027_154439_11d2` store, `thread_20261027_154459_1c10` furiten/Ron, `thread_20261027_154500_f0b7` control |
| Total | 7/4 | Four running/reworking plus three queued |

The four Anti entries share one LocalHost worktree and overlapping scopes;
this is serialization, not four independent physical tasks. No new delegation,
retry, rework, retarget, approval or manual resume occurred. Historical manually
paused c/d instructions remain binding despite the current queued ledger state.
The failed parent `thread_20261026_163026_eac6` remains unaccepted with its
merge-back obligation; children retain reservations. Recent completed components
have PR20 MR records. No unresolved completed merge reservation was found.

Caller created `D:/majiang-wt/codex-ci-exit-fix-20261028`,
branch `wt/codex-ci-exit-fix-20261028`, from fetched origin/v5, then
fast-forwarded the reviewed PR20 source. This invocation's four code/test/CI files
do not overlap any active task's declared scope. Receiver trees, dirty main
checkout files, Unity Resources/Burst/environment and frozen baselines were preserved.

## Raw remote evidence overrides the green badge

Both runs report SUCCESS in GitHub. Original artifacts and full job logs were
downloaded and independently checked; the outcomes differ:

| Run | Actual checkout | Raw result |
|---|---|---|
| [push 40544348887](https://github.com/liuchangready/majiang-game/actions/runs/40544348887) | `b5c1c170b77105ffba97b6f1a0741c6c9e562a37` | FAIL: 2,285 passed / 1 failed / 2,286 selected; zero errors/skips/not-run |
| [PR 40544358114](https://github.com/liuchangready/majiang-game/actions/runs/40544358114) | `a294e979d739d683d1ce50cc3b076c3119a1ccab` | PASS: 2,286/2,286 across 11 direct runners; native exits 0, zero errors/failed/skips/not-run |

The fetched PR merge object has parents accepted v5 `8fd7c6c` and source
`b5c1c17`. Full-tree equality to source was verified with git diff (exit 0).
The strict dual-run audit exits 1 on the push artifact; the separate PR audit
exits 0. No remote rerun was requested and no original evidence was overwritten.

The push failure is
`RuntimeDecisionRuntimeTests.IllegalAction_AndWrongDecisionCannotAdvanceTheAuthority`,
line 310 of the reviewed source: expected Rejected, actual Applied.
The direct runner records 152 selected, 151 passed, 1 failed, native exit 1.
The unchanged inner Lobby wrapper correctly records verifier exit 1 and plannedExit 1.
The outer table wrapper nevertheless records unified-verification PASS/exit 0.

The passing PR artifact additionally contains real PostgreSQL 16.10 table
35/35 and targeted 13/13 XML, writer termination, DB crash/restart, cold recovery,
empty-database restore, Lobby migration/transaction evidence and cleanup.
Build, 12-DLL closure and current companion fixture checks pass.
Historical companion git pins remain NOT_RUN in these CI invocations.
Storage evidence does not qualify the in-flight durable WSS host composition.

## Defects and concrete repairs

**D-CI-20261028-01 — BLOCKER, tools/ci/verify-v5-table-postgres.ps1,
Invoke-Native.** Windows PowerShell 5.1 loses the exit code on the returned
Start-Process object when its handle was never retained. Converting null to int
produces zero. Independent unmodified-function probes actually returned zero for
child exit 1, 2 and 17 (three failures; exit-0 control passed).
The launcher now retains the process handle before waiting and refuses a missing
exit code. The sibling writer-kill path receives the same handle/null protection
so recorded kill exits cannot silently become zero. A production-function AST
probe covers exit 0/1/2/17, stdout/stderr, paths with spaces, timeout and forced
writer termination. CI runs it before the wrapper and uploads its raw results.

**D-CI-20261028-02 — MAJOR, RuntimeDecisionRuntimeTests.TileOutOfRange.**
The negative input helper excluded ClosedTiles but omitted the separately
projected DrawnTileId. It could select a legal drawn tile and correctly receive
Applied, invalidating its own Rejected assertion. A fixed Domain round with
closed tiles 1..13 and drawn tile 0 reproduces the original failure before the
helper repair: fresh build native 0; selected 1, failed 1, native 1,
expected Rejected / actual Applied. The helper now excludes the drawn tile.
The real-host test keeps the deterministic round and all original
rejection/no-side-effect assertions. Production authority code and validation
were not changed. This root cause was reproduced, not labelled flaky.

## Commands, checks and evidence

Working directory for new builds/tests:
`D:/majiang-wt/codex-ci-exit-fix-20261028`.

- `powershell.exe -NoProfile -File tools/ci/test-v5-native-exit.ps1`:
  native 0; 6/6 checks PASS, including forced-writer exit -1. Production functions
  are extracted from the actual script, not reimplemented mocks.
- `D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.TableService.Tests/Mahjong.TableService.Tests.csproj -c Release --no-incremental --no-restore -m:1 -p:UseSharedCompilation=false`:
  native 0; 0 warnings/errors. Initial build restored dependencies; the final
  build rebuilt them without restore. Both original build logs are retained.
- `D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.TableService.Tests/bin/Release/net8.0/Mahjong.TableService.Tests.dll -noLogo -xml artifacts/caller-ci-exit/after.xml`:
  native 0; 152/152 PASS, 0 failed/errors/skipped/not-run. The XML test nodes
  and assembly totals were independently checked; no non-Pass node exists.
- `git diff --check`: exit 0.

Original evidence root:
`D:/majiang/artifacts/v5-progress-20261028-015042-75e08914/`.
It contains both artifact trees/job logs, strict failing dual-run audit,
passing PR audit, push raw per-project counts, exact merge/tree proof, original
and repaired-launcher probes, and the one-read ledger snapshot.
Its manifest covers 436 files; SHA256:
`0EE864E487C6207CEC33FC70C3D267482ABEE9EA49340DAD8818967C3426FF63`.

Local evidence:
`artifacts/caller-ci-exit/` (before/after build, raw logs and XML) and
`artifacts/native-exit-a93f9f38c50742e5a854f5514042a8d2/` (six launcher probes).
Before XML SHA256:
`3D4543BE4F1723561AB16B9B9C3E00923BF3F9E65FA256EF5BB69146F68C5657`.
Six-probe results SHA256:
`3886116BC8BC9272683E1D9E16E2402CE6F1809EEC8EBC2BD8A98ADD1D0E3DD0`.
After XML SHA256:
`006E427171493DB9A41E6034134FCBB47FDE8BC2D2C5B14000F3786133C23F98`.
Fresh tested DLL SHA256:
`B319DFF80D81BC1A5C5F43EE21783D3AAFC4C36EB6E93107A00C42F0557AF4F1`.

Publication is caller-owned on the same PR20 branch, after these focused checks.
New full wrapper/PG/Unity/device execution is NOT_RUN locally; new-head remote
checks must be assessed separately. Historical push FAIL remains FAIL after
the repair. The old Client failure 39970580589 remains UNRESOLVED.

## Next gates and scheduler

P4.2-B and P4.5 await accepted LocalHost and Unity B2b. Full P5.4b remote E2E
awaits accepted durable-host composition; G4 requires G2+G3 and actual
device/network evidence. No new G2/G3/G4/G5 approval is made.

181 classified remains uncovered. Android remains FAIL pending its repair's
accepted evidence; iOS remains BLOCKED; real device and cross-physical-network
qualification remains NOT_RUN. Scheduler is enabled, PT30M, IgnoreNew, currently
Running. complete.flag is absent. No deployment, v5 merge or complete.flag write
was performed.
