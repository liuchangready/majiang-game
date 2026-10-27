# V5 progress and independent Client verification, 2026-10-27 22:55 UTC+8

G1 remains APPROVED. G2–G5 remain unapproved; V4 remains production. No
complete.flag, production deployment, or merge to v5 was performed.

## Accepted baseline and concurrent publication

Fetched origin/v5 is `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`.
PR19 remains merged. The single current review PR is
[PR20](https://github.com/liuchangready/majiang-game/pull/20), targeting v5.
The dirty main checkout at `6c35736` and all receiver worktrees were preserved.

This invocation built an isolated candidate from origin/v5, fast-forwarded the
accepted PR20 source `c5efdf2442dec548e8be82f2363a72a5218b8735`, and applied
Client commits `4158b5b` and `378c337`. While verification ran, another caller
accepted and published exactly those Client changes at `81dc0a1a1828d1da22577aa4e9603225dc2d09c5`.
`git diff --exit-code` over dotnet/tools/unity/.github confirmed identical
implementation trees. No duplicate component merge or approval was performed.
The completed Client thread's PR20 merge-back was recorded at that published SHA.

## Independent checks actually executed

Evidence root: `D:/majiang-wt/codex-progress-20261027-2255/build/codex-b2a-r2/`.
Fresh Release builds used `D:/Unity/dotnet/dotnet.exe build <project> -c Release
--nologo -v:minimal`, followed by direct fresh DLL execution with `-result-xml`.
Every build and runner below exited 0, with zero failed/errors/skipped/not-run.

| Check | Passed/selected |
|---|---:|
| Client | 182/182 |
| Contracts | 149/149 |
| Client.Wss | 80/80 |
| TableService | 152/152 |
| Unity.TableModel, linked production sources | 134/134 |
| Architecture | 92/92 |
| Original independent history-watermark probes | 4/4 |
| Total | 793/793 |

The original probe source SHA256 remains
`BD4B7C52E3670E966A67DE281C1616E0B23903CC885E84198F224441E210709F`.
Only the copied project link was adjusted for the authorized singular test
filename. SYNC and empty-full-PUSH watermarks now exclude unseen historical
events while allowing sparse new events. Net Client changes are five authorized
paths; whitespace checks passed. Privacy, binding reset, immutable typed mapping,
baseline versus playable-event semantics, and scope were reviewed. No additional
material defect was established.

Historical PR20 CI at c5efdf2 was independently inspected:
[PR run 40500310868](https://github.com/liuchangready/majiang-game/actions/runs/40500310868)
completed successfully. Its downloaded `unified/summary.log` records 14 PASS
project rows and companion PASS, including actual Infrastructure 35/35. The raw
artifact is preserved under `ci-c5-pr/`. This is historical evidence, not a CI
claim for 81dc0a1 or this documentation commit. New-source CI remains unverified.

## Allocation and next dependencies

Global bridge list was requested once. Before replacement preparation, active
tasks were DeepSeek 1/2 (Android repair `thread_20261027_194656_c897`), GLM 0/1,
Anti 1/1 (LocalHost store `thread_20261027_154439_11d2`), total 2/4. Client
`thread_20261027_194656_8f37` moved from waiting_review to completed during review.
LocalHost c/d remain manually paused, and failed a/parent threads retain their
existing obligations; none were resumed or duplicated.

Dependency-ready replacement scopes are P4.3-B2b Unity consumption of the now
accepted Client events (DeepSeek), and P5.4b-A durable TableService composition
using the already accepted PostgreSQL ports (GLM). These are separate worktrees
and files, independent of Android and LocalHost. Dispatch outcomes and IDs belong
to the bridge ledger, not this preparation record. Shared solution/architecture
registration remains caller-owned.

P4.2-B and P4.5 still require accepted LocalHost, not its uncommitted files.
P5.4b full HTTPS/WSS/PostgreSQL multi-process E2E follows durable host composition;
the current P5.1 production host still constructs in-memory stores and scopes.
G4 requires both G2 and G3, then real platforms/devices/network evidence. The 181
classified coverage gap, Android FAIL, iOS BLOCKED, and device/cross-network
NOT_RUN remain explicit. This invocation did not rerun PG or Unity runtime tests.
The scheduler remains enabled at PT30M/IgnoreNew; complete.flag is absent.
