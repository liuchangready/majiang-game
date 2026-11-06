# V5 定时检视：当前 PR20 双 CI 与额度阻塞

日期：2026-10-27 06:43 检视，Asia/Shanghai。调用者 Codex。

## 判定与基线

G1 APPROVED；G2–G5 未批准，V4 仍是唯一生产。当前 `origin/v5` 为
`8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`，PR19 已合并且不重开。
本批唯一 [PR20](https://github.com/liuchangready/majiang-game/pull/20) 的已接受源头为
`6095453714503d5aaff5e5ed6f43092e49665aad`，包含 P4.3-A、P4.2-core、
P5.2 及伴随黄金校验接入；仍是 draft。本轮不合并 PR20。

本轮全局 bridge recent tasks 只读取一次（100 个，跨工作区）。没有 waiting_review
任务或尚未记录 MR 的新接受交付；不重复批准 P4.3-A。

## 实际任务与分配

| 线程 | 实际状态及路由 | 本轮处理 |
| --- | --- | --- |
| `thread_20261027_022047_f12b` P4.4 | reworking R5；Anti / 配置默认 gemini-3.8-flash-high | 保持原生会话、工作树和文件预约；不轮询、不打断 |
| `thread_20261026_163026_eac6` P4.1 | failed R36；DeepSeek Harness / deepseek/deepseek-v4.1-flash | RATE_LIMITED，保留原线程；HEAD `d5b05d0`，tracked clean；未接受 |
| `thread_20261026_163027_db9e` P5.1 | failed R4；DeepSeek Harness / z-ai/glm-5.3-flash | RATE_LIMITED，保留用户手动改过的路由；HEAD `5aadd90`，12 项已修改/新增路径保持原状；未接受 |
| `thread_20261027_032015_62d3` P4.3-A | completed，DeepSeek Flash；MR synced `6095453` | 仅纯模型范围已接受；不重复派单 |
| `thread_20261025_194659_8d8a` 旧 P4.1 合同准备 | failed；旧原生 session 不存在，mergePending=false | 正式合同补充及 P4.1 实现线程已承接，不重建旧线程 |

活动数：**DeepSeek 指定 Flash 0/2，Claude Code / GLM 0/1，Anti 1/1；
总活动 1/4**。failed 不计活动，实际 P5.1 的 DeepSeek/GLM 路由也不冒充指定
Claude Code/GLM 名额。两条额度错误均明确给出重置时刻
`2026-10-27T11:38:15.851Z`（北京时间今日 19:38:15.851）；本轮未向已确认耗尽的
通道重复提交或擅自换模型。模型目录已检查，两个指定默认 ID 仍匹配配置；Anti 保留默认。

三个空位的具体依赖：P4.3-B 按已接受 P4.3-A 交接等待 P4.4 释放共享 App/
场景注册/asmdef/边界策略范围；P4.2-B 等待 P4.1 真实工厂；P4.5 等待
P4.1、完整 P4.3 和 P4.4；P5.4b 等待 P5.1 及真实接口联合验收。
P6.1/P6.2 依赖 G2+G3，P7 依赖 G4。没有据此另造替代任务或恢复手动暂停线程。

## 当前源头双 CI 独立证据审计

本轮下载并核验既有远端实跑；以下不是本轮本地执行 2,096 项测试。

| run | API 源头 / 实际 checkout | 原始结果 |
| --- | --- | --- |
| [push 40410415833](https://github.com/liuchangready/majiang-game/actions/runs/40410415833) | `6095453` / 同 SHA | 2,096 passed，0 failed/errors/skipped/not-run；13 工程门 PASS |
| [PR 40410423079](https://github.com/liuchangready/majiang-game/actions/runs/40410423079) | `6095453` / `532b06195b94429c1dd4b3c25ccb6f227ebd2e83` | 同上；合成提交父节点恰为 `8fd7c6c` 与 `6095453` |

实际 checkout 的 tree 都是 `cd682722b1f34fc101f6879a89d60297f7ffcdea`，与接受源头
完全一致。逐工程 raw direct runner 与 native meta 双向核对，10 个测试工程分别为
Domain529、TableRuntime400、Ai444、Client153、Architecture83、Contracts149、
Infrastructure35、Lobby89、Wss80、Unity.TableModel134。三个 helper 只计构建，不计测试。
每项 native exit0；2,096 不再沿用旧头的 1,962。Unity.TableModel 为 .NET 纯模型测试，
不能据此声明 Unity Editor/PlayMode/Player 通过。

两个 run 的 solution build、scratch 12 DLL 闭包及伴随 4/4 gate 均 PASS。
远端伴随历史 Git pins 明确 NOT_RUN（当前字节和内嵌来源已校验），本轮不补写成 PASS。
桌存储 PostgreSQL16.10 wrapper 各 11 阶段 PASS，XML 各 35/35 与定向13/13，
failed/errors/skipped/not-run 均0；包含 writer 在提交后被 kill、实际 PG crash/restart、
独立 reader 恢复终态/非空账本/回执/outbox、空库备份恢复和 owned cluster 清理。
大厅 wrapper 的实际退出/阶段、清理及环境还原均 PASS。
两份桌证据清单各134项、两份大厅证据各12项，**292/292** SHA256 复核通过，
桌清单同时核对实际长度。没有将 XML 门控的故意失败自测 fixture 当作正式测试失败。
Direct 模式注明 `(none)` 的额外 framework logs 不虚称存在；
旧 run39970580589 的缺失框架日志及原因仍 UNRESOLVED。

## 本轮实际动作与产物

独占检视树 `D:/majiang-wt/codex-progress-20261027-0643`，分支
`wt/v5-codex-progress-20261027-0643`：从最新 fetched origin/v5 创建，再 ff-only
至已接受 `6095453`。未纳入任何 receiver 未提交源码；本轮受管增量仅此报告。

证据根 E：`build/codex-progress-20261027-0643/`，保留在本树 ignored 目录。
下载命令为 `gh run download <runId> --dir E/{push,pr}`，同时保存 run API 元数据和完整 job log。
实跑独立审计：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File E/audit.ps1
git diff --check origin/v5..HEAD
```

最终 audit native exit0；两条 `AUDIT PASS`，计数/退出/原始日志/manifest/实际 Git parents
与 tree 均验证。首次审计脚本将 ConvertFrom-Json 数组额外包装，造成路径类型错误，
native1；仅修正 ignored 审计脚本并重跑，首次失败保留 `audit-initial.log`，未改远端证据。
另一次 shell 的未引用 `HEAD^{tree}` 参数解析失败后改成引用参数，最终独立审计成功，
不把该命令的输出当树一致证据。本轮不重跑本地全量、PG、Unity 或设备测试。

| E 下产物 | SHA256 |
| --- | --- |
| audit.ps1 | `96A2689FA73C4936EF41C97F091B992AC2B84B97AA919DD31D7A62FFC59060F4` |
| audit.json | `F3D0F953DC906E73E8170064CD9B8CA53A9DD96206B35E354B08787124C56E9B` |
| audit-final.log | `07057F3F5C768E937420C078747D817D0FC0AA2F2EFCD6FB3B7979AC4211DAF0` |
| evidence-sha256.csv（413 文件） | `8A9BEDD848D3744147B623352E13BA9B06A6E40E67518260FE12B87D78EEF93E` |

调度任务 `Mahjong-V5-0-1-Progress` 当前 Running、PT30M、IgnoreNew；complete.flag 不存在。
本轮不新建 timer、不重开 PR19、不改变 V4 或用户主工作区文件。报告沿用当前 PR20 发布，
不创建组件 MR；报告后的新 SHA 若触发 CI，其状态须另读，不套用上述已完成 run 的 SHA。
181 classified 未覆盖、Android FAIL、iOS BLOCKED、真机及跨物理网络 NOT_RUN 均保留。
