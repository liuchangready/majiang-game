# V5 检查：保留两个 DeepSeek 在途任务，核验最新 CI 外部阻塞

2026-10-29 04:45，Asia/Shanghai。G1 APPROVED；G2–G5 未批准；V4 仍生产，181 classified 未覆盖。

## 基线、台账与分配

本轮只查询一次全局 recent tasks（100条、不按工作区过滤），未轮询未变化任务。最新 fetch 后 origin/v5 仍为 `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`；唯一当前批次 PR20 为 OPEN，目标 v5，源 `feat/v5-post-g1-batch`，审查头 `209de29504ded1991310decbc4e154949d949c35`。PR19 已合并，不重开、不复用其旧源分支。

| 分配 | 活动/目标 | 原线程及实际状态 |
|---|---:|---|
| DeepSeek Harness / deepseek/deepseek-v4.1-flash | 2/2 | `thread_20261029_041602_732e` running round1：D-LH-GATE-03/04；`thread_20261027_230302_93fb` reworking round3：P5.4b-A 持久宿主 |
| Claude Code / GLM | 0/1 | 无新增就绪且不冲突任务，本轮不请求模型发现 |
| Anti | 0/1 | 无新增就绪且不冲突任务，本轮不请求模型发现 |
| 合计 | 2/4 | 不重复派发，不以其他模型补第三个 DeepSeek |

`thread_20261028_223624_e4ae` 为 waiting_review / reviewBlocked，不是正在执行的第三个 DeepSeek；原任务与范围预留继续保留。其 reviewBlocked 已记录 R2 caller 独立复核准备返修 PASS（`8b4fb49`），完整 P4.2-B 仍等待真实 LocalHost，Windows Player 真实菜单开始本地局 smoke 门槛保留。未重复审核、返修或提前 approve。历史 failed P4.1 父线程已由 a/b/c/d 承接，不重试；组件接受与未解决集成缺陷分别记录。

## 本轮独立核验

隔离工作树 `D:/majiang-wt/codex-inspection-20261029-0445`，分支 `wt/v5-codex-inspection-20261029-0445`：先从最新 accepted origin/v5 创建，再 `merge --ff-only origin/feat/v5-post-g1-batch` 到上述审查头。变更前 tracked clean。主目录原未提交文件、receiver 工作树、Unity Resources/Burst 均未修改。

原生命令 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/audit-latest-ci.ps1`，native exit0。脚本只读取 GitHub API，核验精确 SHA、completed/failure、唯一 verify job、零 steps、零 artifacts，并要求原始 annotation 明确账户付款/额度错误。

| 远端实际运行 | head SHA | API结论 / 本轮归类 |
|---|---|---|
| [40760726726](https://github.com/liuchangready/majiang-game/actions/runs/40760726726) | `209de29504ded1991310decbc4e154949d949c35` | failure；BLOCKED_ACCOUNT_JOB_NOT_STARTED；测试 NOT_RUN |
| [40760718952](https://github.com/liuchangready/majiang-game/actions/runs/40760718952) | 同上 | failure；BLOCKED_ACCOUNT_JOB_NOT_STARTED；测试 NOT_RUN |

两个原始 annotation 都说明近期账户付款失败或支出额度需要提高。没有测试步骤和产物，不能记为产品测试失败或通过；本轮没有 rerun、workflow dispatch 或创建 CI-only ref。账户付款/额度须由账户管理方解决，本轮不更改账户设置。旧原生测试失败与未归因项仍保留，不因该外部阻塞注销。

证据根为本树 `build/inspection-evidence-39d310c71e33480ea2a6f3b81c896286/`；含两次 run JSON、两次 annotations、两次 artifacts 响应、审计结论和脚本。manifest `sha256.csv` 共8行，逐项重哈希8/8，manifest SHA256 `134AACD9241BD88589DCF511BBC5C90BF01F327BF52F39CE207FE4888C1A9812`。PASS 仅指证据审计，不代表 CI 或阶段门禁通过。

## 门禁与下一依赖

- D-LH-GATE-03/04 已由在途 `732e` 独占修复，等待交付后独立复核。不能把正在构建或 receiver 最后输出当最终验收。
- D-LH-GATE-01 的 TableService 控制 payload 映射处于在途 `93fb` 范围，不另派重叠实现；持久宿主仍等最终返修及真实 PG/WSS 独立验收。
- D-LH-GATE-02 控制拒绝回执对账仍 OPEN/BLOCKED。既有原线程 `thread_20261027_154500_f0b7` 已 completed，但 caller 的 same-thread request_rework 被 bridge 以无 active reviewer role 拒绝；该拒绝在 `732e` 的冻结任务说明中明确保留。本轮不反复尝试同一权限条件，不创建替代控制实现绕过角色检查。需要恢复合法 reviewer 处理路径；本轮未声称此缺陷已修。
- 完整 LocalHost 四项门禁关闭及整合复验后，才可继续原 P4.2-B 激活与 P4.5/G2；持久宿主接受后才可推进剩余真实 HTTPS 大厅签票、多客户端整场对账/P5.4b/G3。P6 等 G2+G3，P7 等 G4。无就绪且范围独立的新 GLM/Anti 实现，因此保留空槽，不以旧限流恢复时间推断当前可用性。

本轮没有代码/PG/Unity/Player/IL2CPP/整场/真机/跨物理网络新运行，不改写既有 Android/iOS 状态。scheduler 实读 Running、PT30M、IgnoreNew；complete.flag 不存在。新增受管内容仅本进度文档，发布仍使用现有 PR20；不含在途未验收代码，不合并 v5、不部署、不创建 complete.flag。
