# V5 scheduled inspection — 2026-10-29 10:15 Asia/Shanghai

V4 remains production. G1 APPROVED; G2–G5 unapproved. This inspection confirms acceptance of the returned R2 review report as review input. It does not accept or activate the complete P4.2-B implementation, merge PR20 into v5, or authorize a release or complete.flag.

## Routing and ledger

The global recent-task ledger was read once, without workspace filtering (limit 1000). Active allocation at that read:

| Executor | Active / target | Threads |
|---|---|---|
| DeepSeek | 1 / 2 | `thread_20261027_230302_93fb`, round 4 reworking; P5.4b-A PostgreSQL durable TableService |
| GLM | 1 / 0, dispatch disabled | `thread_20261029_064512_04b1`, round 2 reworking; legacy LocalHost rejected receipt repair |
| Anti | 0 / 0, dispatch disabled | None active |
| Total | 2 / ceiling 2 | No free slot; no new dispatch or rework |

The legacy task remains in flight unchanged. No GLM/Anti model lookup, execution, retry, resume, cancellation, pause, retarget or duplicate scope was issued. Any future legacy execution requirement is BLOCKED by the current routing restriction. Existing failed LocalHost threads `eac6` and `8d8a` were not resumed. Original Unity task `thread_20261028_223624_e4ae` remains waiting_review with its complete-component dependency gate intact.

## Accepted baseline and publication boundary

Fetched `origin/v5` remains `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`. Planning worktree is clean at that baseline. The shared main checkout remains on `feat/v5-1-integration` at `6c35736` with unrelated tracked/untracked files preserved.

New caller integration worktree `D:/majiang-wt/codex-inspection-20261029-1015`, branch `wt/v5-codex-inspection-20261029-1015`, was created from fetched origin/v5, then fast-forwarded to reviewed PR20 head `4d0511e272ea5259d20bd562ecd7599188366648`. Only review documentation is added. `dotnet`, `unity`, `tools` and `.github` trees equal that reviewed head (git diff --quiet exit 0). Publication uses the existing PR20 source `feat/v5-post-g1-batch`; PR19 and its closed source are not reused.

## R2 review-input adjudication

Review thread: `thread_20261029_064512_663a` (DeepSeek). Committed report: `f4ce9c5adb8c9bf3570acab084ea3782248008f5`. Product candidate examined by that review: `8b4fb49c0c2b50dd82c1f9077c56928ab3f504b6`, frozen base `0eb694d20051af45afb69d95abb7e94248cd2fa6`.

Caller inspected AppRouter start ownership/navigation generation/Busy handling and LobbyPresenter rebinding/destruction behavior, the independent probe sources and preserved raw XML. Caller independently recomputed all **31/31** evidence file lengths and SHA256 values, all matched. Manifest SHA256: `6D19FFBF4E8C37AFAFFF4535DEA59D909345E5B10789CF20F3F4727CB7C49965`. Caller counted individual test-case elements:

| Receiver fresh evidence | Selected | Passed | Failed | Other |
|---|---:|---:|---:|---:|
| EditMode | 82 | 82 | 0 | 0 |
| PlayMode | 134 | 134 | 0 | 0 |
| Independent R2 EditMode probes | 3 | 3 | 0 | 0 |
| Independent R2 PlayMode probes | 2 | 2 | 0 | 0 |
| Preserved original reflection probes | 5 | 4 | 1 | 0 |

The receiver records fresh build-shared exit 0, 0 warnings/errors, 19 copied files; shell verifier exit 0 with StrictCompiled and 13/13 negative boundaries; Client 182/182. These are receiver executions, audited here; **caller did not rerun Unity or .NET tests this invocation**.

The retained failure is `Probe_SceneIdOnlyGuard_SameSceneReturn_StillLoadsTable`, expected Cancelled but actual Busy after reflection forced a transition without advancing the public navigation generation. Caller agrees this artificial-state assertion does not invalidate the public-entry same-scene/different-scene probes, both passed and assert Cancelled, closed channel, null coordinator and no late Table navigation. The failed probe remains FAIL in the record; it is not relabeled PASS or deleted.

Report-input result: **PASS with caller documentation correction**. At the initial read the committed report twice said ten paths while listing eleven; caller independently verified eleven. The receiver's equivalent uncommitted two-line correction was preserved. During publication another caller completed this same review: accepted commit `c6549a82adb1194cf3f66158016f56a86c9651aa` and published PR20 head `b8d108553b24b2a1b47f63c48e3e1a7fda7392d0` already contain the correction, report and caller execution appendix. This invocation's attempted normal push was safely rejected as non-fast-forward. After fetching and reviewing the documentation-only delta, the redundant local report import was dropped; this invocation now adds only this progress record on b8d1085. No force push, duplicate approval or duplicate MR recording was issued. A single task-details read confirmed status completed and mergeRequest synced at b8d1085. No receiver product code or ignored activation patch was integrated.

The concurrently published appendix records that caller's actual Rebuild/build-shared, Client 182/182, focused Unity EditMode 23/23 and PlayMode 5/5, and StrictCompiled PASS. Those executions belong to that caller; this invocation independently audited the earlier 31-file evidence and did not rerun tests. Both evidence sets remain separately attributed.

Full P4.2-B remains **BLOCKED_DEPENDENCY**. Production LocalTableChannelFactory is not activated. LH03/04 and response/kan repairs have separate accepted evidence; LH02 is still in legacy rework, and the existing LocalHost batch failure record remains binding. Windows Player build plus real-menu local-game smoke is **BLOCKED / NOT_RUN**. P4.5 follows accepted LocalHost and complete P4.2–P4.4; no premature activation/rework was sent to e4ae.

## Remote CI: FAIL status, execution BLOCKED

Exact reviewed head `4d0511e272ea5259d20bd562ecd7599188366648`:

- [Push run 40790577932](https://github.com/liuchangready/majiang-game/actions/runs/40790577932): failure, verify steps empty, check `123174508732`.
- [PR run 40790584052](https://github.com/liuchangready/majiang-game/actions/runs/40790584052): failure, verify steps empty, check `123174527217`.

Both annotations state that the job did not start because recent account payments failed or the spending limit needs increasing. This is an account billing BLOCKED condition; no build/test count or code-failure diagnosis can be inferred. Push-run artifacts API returns zero artifacts. No blind retry, new CI-only ref or workflow change was made. The account owner must resolve billing before a reviewed-head run can supply remote validation.

## Caller checks, retained evidence and next dependency

`artifacts/inspection-1015/Audit.ps1` exits 0, writes per-file hash audit, independently counted XML results and raw CI annotations. Evidence lives under this caller worktree; `evidence-sha256.csv` covers the audit script and outputs. Integrated documentation diff-check passes. Product trees match 4d0511e; no in-flight delta is included.

Scheduler read-only verification: `Mahjong-V5-0-1-Progress` enabled/running, PT30M, IgnoreNew. `C:/Users/sncdc/.codex/automations/mahjong-v5/complete.flag` absent; no scheduler change.

Next: review the existing 93fb/04b1 returned evidence when available; keep legacy routing restriction and two-task ceiling. Once LocalHost batch evidence is actually accepted, original DeepSeek Unity thread e4ae can proceed under discovery/scope/slot gates. Real PG/WSS checks for its exact eventual integrated candidate remain required. G2/G3 precede G4; Android FAIL/iOS BLOCKED/device NOT_RUN and classified 181 coverage gaps remain explicit. No G5 completion is claimed.
