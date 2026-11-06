# V5 进展独立核对：PR20 当前提交与执行器阻塞

日期：2026-10-27，Asia/Shanghai。结论：本次远端产物审计 PASS；G1 APPROVED，G2–G5 未批准，V4 继续生产。

## 基线与工作树

`git fetch origin` 后，`origin/v5` 仍为 `8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`。
PR19 已合并，不再使用其关闭的源分支发布。
唯一后续批次为 draft [PR20](https://github.com/liuchangready/majiang-game/pull/20)，接受源头为
`9d4a0400b44cc2ce598c391b39539ddf69ffe829`，包含已接受 P5.2、伴随校验、P4.2-core、P4.3-A、P4.4。
失败的 P4.1、P5.1 不在该树中。

本次独占审计树：`D:/majiang-wt/codex-progress-20261027-0745`。
它从当前 `origin/v5` 创建，再 fast-forward 接受的 PR20 增量；未修改旧集成树、receiver 树或主工作区用户文件。
本次仅新增本报告，不改变实现、测试或 verifier。

## 当前提交远端证据

本次下载并独立解析两份完整 artifact，核对框架原始日志、native meta、真实 PG XML、恢复实值及 SHA256。
这属于远端已完成执行的证据审计，不是本机重新执行 2,096 个测试。

| 执行 | 实际检出提交 | 结果 |
| --- | --- | --- |
| [push 40420843235](https://github.com/liuchangready/majiang-game/actions/runs/40420843235) | `9d4a0400b44cc2ce598c391b39539ddf69ffe829` | SUCCESS；10 项目 2,096/2,096；failed/errors/skipped/not-run 全 0；所有 native test exit 0 |
| [PR 40420849170](https://github.com/liuchangready/majiang-game/actions/runs/40420849170) | `c55898ce56de166aaf3f59a63a8f8f3e2996dd49` | SUCCESS；相同项目、计数及零失败/零跳过 |

GitHub commit API 确认 PR checkout 的两个 parents 精确为 `8fd7c6c`、`9d4a040`，
tree 为 `3a56b3ddba7fc6b20bd72e3eb93bb3d38c3d3058`，与 source `9d4a040^{tree}` 相等。
两次 API headSha 均为接受源头 `9d4a040`；PR API headSha 与实际 checkout SHA 的区别已明确核实。

每次实际产物均包含：

- solution build 成功、0 errors；scratch 插件闭包 12 DLL；伴随样本 4/4、native exit 0。
- PostgreSQL 16.10：table 全量 XML 35/35、targeted XML 13/13，逐个 test 节点均为 Pass，零 skip/errors/not-run。
- committed-writer forced kill 与 commit marker 哈希吻合；PG immediate stop/restart；reader 恢复 stateVersion=1、lastEventSeq=2、Applied receipt、terminal、cheat state 和匹配的 outbox payload。
- `pg_dump/pg_restore` 至空库后重新恢复成功；两层 wrapper 退出 0、受 ownership 约束的资源清理有记录。
- 每次 table manifest **134/134** 长度与 SHA256 匹配、Lobby manifest **12/12** SHA256 匹配；两次合计 **268 + 24**。未把两个 run 的合计冒充单次计数。
- 历史 companion Git blob pins 明确 NOT_RUN。旧 CI 缺失 framework log 的历史故障没有因此被改写为已解释。

完整本地证据根：`artifacts/progress-20261027-0745/`。
可重跑审计命令：`powershell.exe -NoProfile -ExecutionPolicy Bypass -File artifacts/progress-20261027-0745/audit.ps1`。
最终审计 native exit 0，409 个文件入 evidence manifest。
审计脚本初稿的 PowerShell 数组/长路径错误已修正，仅影响 ignored 审计脚本；原始下载产物未改。

| 文件 | SHA256 |
| --- | --- |
| `audit-results.json` | `FC591F443BAC99574D49B40DFCB94A39F5A7D6521C1BA4F61129A1C496D2E5E4` |
| `evidence-sha256.csv` | `C5D5933345F5A1F7B1D1764CB09503D708CA2D5A1CA566865AA2B83ED5FB6A7C` |

原始 artifact、manifest 与审计脚本留在独占树 ignored artifacts，不提交数据库 dump/DLL。

## 台账、路由与下一依赖

全局 `list_recent_tasks` 调用一次；返回含完整历史，随后只对失败线程做针对性详情核对。
没有 waiting_review 任务；已接受任务均已记录 PR20 merge-back，没有遗漏的新接受代码。

| 线程 | 当前实际状态与处理 |
| --- | --- |
| P4.1 `thread_20261026_163026_eac6` | round36 failed/RATE_LIMITED，provider 返回 weekly limit 恢复时间 `2026-10-27T11:38:15.851Z`（北京时间 19:38）。保留原 DS 路由与冻结树，不重复派发或无效重试。R35 已接受两个修复，剩余 1 BLOCKER + 3 MAJOR 未通过。相同类别已多轮重现，按复核协议 §3 需人工裁决，停止自动循环返修。 |
| P5.1 `thread_20261026_163027_db9e` | bridge round4 failed，原生会话 `e327178b-f398-4c25-a7c3-9f5c8f60e0e0` 占用；进程核对发现同一 session 已由本机 `claude.exe --resume` 打开（PID10100，07:41），保守预留一个 GLM 槽，不并发重试、不终止用户进程。R3 的 1 BLOCKER + 2 MAJOR + 1 MINOR 尚未接受。 |
| P4.3-A `thread_20261027_032015_62d3` / P4.4 `thread_20261027_022047_f12b` | completed，PR20 已记录；P4.4 已释放 App/注册 scope，玩家场景可以继续。 |

P5.1 的 routing audit 记录用户于 `2026-10-26T23:13:29Z` 在 VS Code picker 将其改回 Claude Code / `glm-5.3-flashx`，并批准模型切换。
这是该在途线程的用户路由例外；本次不静默改回 Flash，也不把 FlashX 统计成严格的 Flash 配额达标。
模型发现仍确认 DS 默认为 `deepseek/deepseek-v4.1-flash`，Claude 默认为 `glm-5.3-flash`，Anti 默认为 `gemini-3.8-flash-high`。

准备一个具体依赖就绪的 Anti 任务 P4.3-B1：玩家 Table 场景、候选输入、实际 Client 操作队列提交、已确认快照动画与场景绑定。
独占树 `D:/majiang-wt/p43b-scenes-anti-20261027` / `wt/v5-p43b-scenes-anti-20261027` 从 `origin/v5=8fd7c6c` 创建，
随后只 fast-forward 上述接受的 PR20 增量，冻结实现起点 `9d4a040`。任务 ID/派发是否成功以 bridge 最终返回为准，此处仅记录 PREPARED。
不等待未接受 LocalHost，不把测试 channel 安装为生产通道；完整 typed-visible-event 上游供给与 P4.2-B/真实本地整场仍分别保留缺口。

准备派发前保守分配：DS 0/2（provider blocked）；GLM 1/1 槽保留（原生会话、用户选择 FlashX；严格 Flash active=0）；Anti 0/1，拟补 1；总预留 1/4，成功派发后 2/4。
空余 DS 槽不可由第三种路由填充。P4.5 等待 P4.1/P4.2-B/完整 P4.3；P5.4b 等待接受 P5.1；G4 等待 G2+G3；G5 等待实际发布准备和最终批准。

Windows scheduler 仍启用、PT30M、IgnoreNew；本次检查处于 Running。`complete.flag` 不存在。
181 classified 未覆盖、Android FAIL、iOS BLOCKED、真机/跨物理网络 NOT_RUN 保留。
本报告后续发布沿用 PR20；新增文档提交的 CI 与上述已审计 source-SHA 执行分开记录。
