# V5 进度检查与当前 PR20 双 CI 原始证据复核

日期：2026-10-28 07:47，Asia/Shanghai。调用者：Codex。

本轮结论：**已发布 P5.2 时钟修复后的双 CI 原始证据 PASS；G2–G5 未批准，V4 继续生产。** 本轮没有批准新的 receiver 交付、集成在途代码、恢复暂停任务或新增派单。只发布本检查记录到现有 PR20，不另开 PR，不合并 v5、不部署、不创建 complete.flag。

## 基线与隔离

- fetch 后 `origin/v5` 仍为 `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`；PR19 已合并，不重新开启。
- 已审查 PR20 源头：`c6a56a16a5ffa3ffdd2b35381a8ee625a03e5e90`，分支 `feat/v5-post-g1-batch`，目标 `v5`。P5.2 时钟修复线程 `thread_20261028_065214_ea5b` 已 COMPLETED，且 mergeRequest 已 synced 到此头；本轮不重复 approve/MR 登记。
- 本轮工作树 `D:/majiang-wt/codex-v5-ci-review-20261028-0748`，分支 `wt/codex-v5-ci-review-20261028-0748`：从最新已 fetch 的 origin/v5 创建，校验祖先关系后 fast-forward 到上述 PR20 头。时间后缀仅是目录名。
- 主目录原有 `CLAUDE.md`、`doc/ui/round-preparation/PROMPTS.md` 两处修改及两份未跟踪 V5 文档保留；receiver 工作树、Unity 环境/Resources/Burst 未修改。

## 本轮实做与证据

证据根 E：`D:/majiang-wt/codex-v5-ci-review-20261028-0748/build/ci-audit`（ignored）。

分别执行以下只读下载，两个 run 独立并行；每个原生退出码均为 0：

```powershell
gh api repos/liuchangready/majiang-game/actions/runs/40601604738
gh api repos/liuchangready/majiang-game/actions/runs/40601604738/artifacts
gh run view 40601604738 --log
gh run download 40601604738 --dir E/40601604738/artifacts
# pull_request run 40601613757 执行同组命令，写入独立目录。
gh api repos/liuchangready/majiang-game/git/commits/f569d8ffc8a8a401a3bcf9c68a0443a8952b18a5
powershell -NoProfile -ExecutionPolicy Bypass -File E/audit.ps1
```

`E/audit.ps1` 本轮独立读取原始 run identity、每项目 direct runner 日志和 native meta、真实 PG XML、wrapper 阶段结果、crash/restore reader JSON 及 native-exit 自检结果。native 0，`AUDIT-PASS`；不是本轮在本机重新运行 2,288 项、PG 或 Unity。

| run | API 源头 | 实际 checkout | 复核结果 |
|---|---|---|---|
| [40601604738 / push](https://github.com/liuchangready/majiang-game/actions/runs/40601604738) | c6a56a1 | c6a56a16a5ffa3ffdd2b35381a8ee625a03e5e90 | PASS |
| [40601613757 / pull_request](https://github.com/liuchangready/majiang-game/actions/runs/40601613757) | c6a56a1 | f569d8ffc8a8a401a3bcf9c68a0443a8952b18a5 | PASS |

通过 GitHub git commit API 与原始 checkout 日志核实：PR 合成提交的两个 parent 恰为 `8fd7c6c` 与 `c6a56a1`；其 tree 与本轮精确源头 `HEAD^{tree}` 完全相同。不能把 API head_sha 当作合成 checkout。两个 run 均为 completed/success，原始 direct meta 的 head 与各自真实 checkout 一致。

两次独立 CI 各自的原始测试计数如下，逐个与 meta 交叉核对。**每个项目 Errors/Failed/Skipped/Not Run 均为 0，native exit 均为 0。**

| 测试项目 | 每次 Total/Passed |
|---|---:|
| Ai | 444 |
| Architecture | 92 |
| Client | 182 |
| Client.Wss | 80 |
| Contracts | 149 |
| Domain | 529 |
| Infrastructure | 37 |
| LobbyService | 89 |
| TableRuntime | 400 |
| TableService | 152 |
| Unity.TableModel | 134 |
| 合计（11 测试项目） | **2,288** |

统一 summary 的 14 PASS 是 11 测试项目 + 3 个 build-only helper，不是 14 个测试程序集。两次均验证 solution build、12 assemblies plugin copy、四伴随 fixture gate PASS。伴随历史 git pins 本次远端命令未请求，仍为 NOT_RUN，不冒充已跑历史 pin 检查。

两次真实 PG16.10 的原始证据均已复核：

- 全量 XML 37/37、定向 XML 13/13；原失败 `NonEmptyCheatLedgerAndFacts_WithCheckpointAndTail_RecoverStrictly` 及新增注入 UTC 一致性/真实跨 boot UTC 回退拒绝两项，恰各 1 个 Pass。
- writer 已 durable commit 后强杀、PostgreSQL immediate restart、独立 reader PASS；再 pg_dump/pg_restore 到空库，独立 reader PASS。两个结果都恢复 stateVersion=1、eventCount=2、Applied receipt、terminal、cheat state 与相同 outbox 摘要，ownerEpoch 分别为 2/3，outbox 未发布。
- table wrapper 全阶段 PASS/native 0，原始末行保留 ownership-verified cluster removed；Lobby wrapper 无失败阶段、rootDeleted=true、环境恢复 true。
- 六项 native exit 传播自检每次全部 PASS；这不代替业务测试数。

每个下载目录包含 229 个原始文件，本轮保留下载内容。`evidence-sha256.csv` 为 403 个非 tmp 文件建立 SHA256 索引；tmp/compiler-cache 明确排除且未删除。关键结果：

| 文件 | SHA256 |
|---|---|
| E/audit-result.json | A741C298048B9131D9FC9920484DE1536888F4D74C14F72B63C4ED00BC914BF6 |
| E/evidence-sha256.csv | F1D5FF2ED1918118169A7EFA6A26BBD3DD019C3922BA4C08CD6CB26F6599D111 |

本报告之外所有新文件仅在 ignored E；只提交本报告。原 40593411699 的时钟失败已由专属修复/反证和本次新头双 CI 跟进，历史失败原证据不改写。更早 39970580589 缺失 framework log 的根因仍 UNRESOLVED，不能借本次绿 run 宣称关闭。

## 任务占位与依赖

本轮 `list_recent_tasks` 工具全局调用一次；过大的返回未再次请求 list。只读本地持久台账补全线程摘要与暂停/改派审计，无修改账本，无轮询 receiver 日志。

| 路由 | active / target | 线程与实际状态 |
|---|---:|---|
| DeepSeek / deepseek/deepseek-v4.1-flash | 1 / 2 | `thread_20261027_230256_426c`：B2b，R9 reworking |
| Claude Code / glm-5.3-flash | 3 / 1 | `thread_20261027_230302_93fb`：持久宿主 reworking；`thread_20261027_154459_1c10`：P4.1c queued；`thread_20261027_154500_f0b7`：P4.1d queued |
| Anti / configured default gemini-3.8-flash-high | 1 / 1 | `thread_20261027_154438_fc96`：P4.1a reworking |
| 总计 | **5 / 4** | queued 计占位；已有超额，本轮不新增派单 |

P4.1c/d 最近分别保留用户 2026-10-27T23:23:17Z / 23:23:22Z 手动暂停记录，之后有用户 executor picker 切到 Claude Code 的审计，当前 queued。事实按实际队列计数，同时保留暂停约束；本轮没有把切换执行器解释为 caller 可恢复/改派的授权，没有调用 resume/retry/retarget。它们与 P4.1a 使用同一工作树，排队不等于三项物理并行。不为填 DeepSeek 空槽复制任务，也不打断已有接收者。

P4.1b `thread_20261027_154439_11d2` 已 completed/MR_RECORDED 于既有 PR21（目标为 PR20 来源分支），其存档矩阵组件通过不代表完整 LocalHost 通过；PR21 本轮只读，未合并/新建替代 PR。所有新发布仍只进入现有 PR20 targeting v5。

- P4.2-B/P4.5：等完整 accepted LocalHost 及 B2b；不接收其在途未提交代码。
- 远端 P5.4b 后续端到端：等持久宿主 accepted；此前 review 的未关闭义务由原线程继续承担，不重复派发。
- P6.1/P6.2：等 G2+G3；P7：等 G4。不能为填槽提前启动。

## 当前门槛与限制

G1 APPROVED 保持；G2/G3/G4/G5 未批准。181 classified 仍未覆盖。本轮没有执行 Unity、IL2CPP、Android/iOS 或跨物理网络；Android 工具链既有组件验收不扩张为完整游戏或真机通过，完整游戏/设备/跨物理网络 NOT_RUN，iOS BLOCKED。

调度器 `Mahjong-V5-0-1-Progress` 实际 Enabled、Running、PT30M、IgnoreNew；complete.flag 不存在。主目录未修改，无生产发布。后续仍需各组件原线程完整交付、独立验收、同批 PR20 集成及真实 G2–G5 证据。
