# V5 进度检查与 PR20 fb9043b 双 CI 独立复核

日期：2026-10-28，Asia/Shanghai。调用者：Codex。

结论：本轮最新已发布头 `fb9043ba73eebff81f9bb28f85bbcadbe06bdad1` 的双 CI 原始证据复核 PASS；G1 APPROVED 保持，G2–G5 未批准，V4 继续生产。无待审新交付，无新增派单、返修、改派或恢复任务；不合并 v5、不部署、不创建 complete.flag。

## 基线与发布归属

- fetch 后 `origin/v5=8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`，PR19 已合并，不重新开启。
- 本批沿用唯一 [PR20](https://github.com/liuchangready/majiang-game/pull/20)，源 `feat/v5-post-g1-batch`，目标 `v5`。本次只发布此报告，不纳入 receiver 在途代码。
- 独占工作树 `D:/majiang-wt/codex-progress-20261028-2210`、分支 `wt/codex-progress-20261028-2210`：从 fetched origin/v5 创建，确认祖先关系后 fast-forward 至 fb9043b；初始 tracked clean。
- `git diff --quiet 5cc27b6..fb9043b -- dotnet tools unity .github` exit 0；期间净变化仅前一份已验收报告。本次仍核验新 SHA 对应的新 run，未借用旧 run 宣称通过。
- 主目录原有 CLAUDE.md、doc/ui/round-preparation/PROMPTS.md 和两份未跟踪 V5 文档原样保留。receiver 工作树及 Unity 环境/Resources/Burst 未修改。已有 LocalHost PR21 属另一 owner，本轮未操作。

## 任务账本与实际占位

本轮全局 `list_recent_tasks` 调用一次。返回体包含大量历史轮次，仅通过本地持久账本只读补全状态和用户改派审计，未修改账本或轮询 receiver 日志。无 waiting_review；queued 计入占位。

| 路由 | active / target | 线程及本次快照 |
|---|---:|---|
| DeepSeek / deepseek/deepseek-v4.1-flash | **3 / 2** | `thread_20261027_154459_1c10` P4.1c R2 reworking；`thread_20261028_124552_6fe6` 插件来源校验 R4 queued；`thread_20261027_154500_f0b7` P4.1d queued |
| Claude Code / glm-5.3-flash | **1 / 1** | `thread_20261027_230302_93fb` 持久 TableService 宿主 R2 reworking |
| Anti / configured default | **0 / 1** | mesh 状态 exhausted，429，记录恢复时间 2026-11-01T16:54:27.999Z；本轮未用其它模型替代 |
| 总计 | **4 / 4** | 保留既有线程，不为调整配比打断或复制任务 |

P4.1c/d 有历史用户暂停记录，随后分别在 2026-10-28T09:49:42.608Z / 09:49:32.598Z 有用户 executor picker 改至 DeepSeek 的审计；本轮仅按实际状态计数，未调用 resume/retry/retarget，未把旧暂停记录解释成可以自行恢复的授权。c/d 使用同一 LocalHost 工作树，d 等待该工作树执行槽，不是物理并行。

已 completed 的 P4.1a/b、CI 与 PG 修复维持已有验收/MR 记录，不重复批准；原 P4.1 R36 failed 线程已拆分，仍由子任务承担未关闭义务，未盲目复制旧任务。当前没有可加入第五项的名额。

## 实际检查与原始证据

证据根 E 为本工作树 ignored `build/ci-audit/`。两个 run 的 API、artifact API、完整 job log 和 artifact 下载分别保存，原生命令 exit 0。复核不是本机重新执行远端业务测试。

| run | event / API head | 实际 checkout | 结果 |
|---|---|---|---|
| [40688369456](https://github.com/liuchangready/majiang-game/actions/runs/40688369456) | push / fb9043b | fb9043ba73eebff81f9bb28f85bbcadbe06bdad1 | PASS |
| [40688377453](https://github.com/liuchangready/majiang-game/actions/runs/40688377453) | pull_request / fb9043b | 820743f7d72ed99efab140107bdc86276f35e1c5 | PASS |

两 run 均 attempt 1、completed/success。通过 GitHub git commit API 核对 PR 合成提交双父为 8fd7c6c 与 fb9043b，tree 等于 reviewed head 的 `e188a1848dadfc1302a2b5c19eb10ff97c8754e7`，并与 artifact run-info、每项目 meta 的实际 checkout 一致。

本轮实际执行：

```powershell
gh api repos/liuchangready/majiang-game/actions/runs/40688369456
gh api repos/liuchangready/majiang-game/actions/runs/40688369456/artifacts
gh run view 40688369456 --log
gh run download 40688369456 --dir build/ci-audit/40688369456/artifacts
# 对 40688377453 执行同组命令，保存到独立目录。
gh api repos/liuchangready/majiang-game/git/commits/820743f7d72ed99efab140107bdc86276f35e1c5
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File build/ci-audit/audit.ps1
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File tools/ci/test-v5-direct-results.ps1
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File tools/ci/test-v5-native-exit.ps1
```

- 独立 audit **397 checks / 0 failures / exit 0**。直接提取生产 verifier 的 `Read-DirectResultXml` 与 `Get-DirectRunnerCounts`，核验 22 个原生 XML 结构/collection/test 节点计数、全 Pass、零 errors/fail/skip/not-run，交叉核对 meta 的 XML SHA、原生退出码、head 以及原始 console 计数。每 run 11 个程序集 **2,299/2,299**。
- 每 run 真 PG16.10 全量 XML **42/42**、定向 **13/13**；table wrapper 全阶段 PASS。强杀记录 forced=true、非零退出，marker SHA 一致；crash reader 恢复 stateVersion=1、eventCount=2、Applied 回执、cheat 状态、未发布 outbox；空库 restore 的 ownerEpoch 从 2 到 3、outbox SHA 一致。原始 table manifest 的每条 SHA/长度逐项通过；Lobby wrapper 无 failedStages、plannedExit=0、rootDeleted=true。未连接或清理本机任何 PG。
- 本机生产 XML 门禁正负矩阵 **19/19 / exit 0**，证据 `artifacts/direct-results-selftest-81cc5cf7a7ad43cc997a2138a42d37bf/`。
- 本机原生退出/超时/forced writer 传播 **6/6 / exit 0**，证据 `artifacts/native-exit-29329d50cf924b68b52d2bd0eef7bc91/`；两个远端 run 的原始六项结果也各自全部通过。
- 审计脚本首次把 ExpectedAssemblyName 传成 DLL 文件名而非无扩展名程序集名，修正调用后继续；随后 Windows PowerShell 普通路径 API 无法读取 manifest 中超长 compiler-cache 路径，改用真实 `\\?\` 文件流后全部匹配。这是调用者工具适配，未修改生产 verifier、测试、下载证据或被审代码。

435 个非 tmp 原始/审计证据文件写入 `E/evidence-sha256.csv`，manifest 不包含自身；compiler tmp 文件不列入该总索引，但 table 原始 manifest 中列出的 tmp 文件仍已逐条验证。

| 文件 | SHA256 |
|---|---|
| E/audit.ps1 | C9C506909D9E3EA4C286B98CBE99F8BB59660B2F4E7593744AA6319613C96968 |
| E/audit-result.json | 1114806603EFB595F131103E2B2F59F94A6D9184F71400D6903882EF307A1101 |
| E/evidence-sha256.csv | D8064A0D2DEF9FBD3148B0EF881F7BE455FD089EAE0CDF52672CF5A3AB55050D |

## 门槛、限制与下一依赖

G1 APPROVED；G2/G3/G4/G5 未批准。181 classified 仍未覆盖。P4.2-B/P4.5 等完整 accepted LocalHost；远端后续整场/对账等持久宿主 accepted；P6.1/P6.2 等 G2+G3，P7 等 G4。插件来源校验在途，不取其未提交代码。

这次远端绿不关闭历史 39970580589 缺 framework log 的根因，也不关闭 D-PG-SUP-02 四失败归因。crash reader 未直接记录 136/stateHash/ledgerHash，10/11 runner 测试 DLL SHA 未记录等上一报告缺口保留；未把专用 fixture 覆盖扩张成 crash reader 直接证据。没有 rerun/改动 CI。

Unity、IL2CPP、Android/iOS、真实设备、跨物理网络本轮 **NOT_RUN**；Android 完整阶段的历史 FAIL 不由工具链组件或本轮 CI 消除，iOS 环境 **BLOCKED**，真实设备仍 **NOT_RUN**。不将 Editor/桌面组件结果推广为真机或 G2–G5 通过。

调度器实际 Running、PT30M、IgnoreNew；complete.flag 不存在。仅提交本报告并沿用 PR20 发布；本轮没有新 accepted receiver，因此没有新增 approve/MR 登记义务。本工作树的唯一 tracked 增量将保留在 PR20，未删除临时工作树，等待该批后续正式合并。
