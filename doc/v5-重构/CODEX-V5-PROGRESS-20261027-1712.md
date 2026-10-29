# V5 定时复核：PR20 当前集成证据与任务占用

日期：2026-10-27，Asia/Shanghai。G1 APPROVED；G2–G5 未批准，V4 继续生产。

## 基线与本轮范围

本轮全局 bridge recent tasks 仅读取一次（100 条），没有派发、重试、改派、暂停或批准任务。
主目录 `D:/majiang` 仍在旧 `feat/v5-1-integration`，存在用户修改和未跟踪文档，全部保留。
fetch 后 `origin/v5=8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`，planning 树同此提交且干净。
PR19 已合并，不重开或写入关闭的源分支。

复核树 `D:/majiang-wt/codex-progress-20261027-1712`、分支
`wt/codex-progress-20261027-1712` 从最新 `origin/v5` 建立，随后仅 fast-forward 到现有
PR20 的已接受头 `b84161424a4899afe94fc46faf1c35c18a1a24b5`。
没有合入 P4.1/P5.1 在途源码、DLL 或未提交文件。本轮新增内容只有本报告；原始证据存 ignored artifacts。

## 任务占用与依赖

实际配置：**DeepSeek 0/目标2，GLM 0/目标1，Anti 4/目标1；活动合计4/上限4**。
queued 计入占用。此配置不满足用户期望的模型分配，但本轮保留既有路由，不新增第五项或擅自改派。

| 任务 | 路由 | 本次账本状态 |
| --- | --- | --- |
| P4.1a `thread_20261027_154438_fc96` | Anti / configured default | running |
| P4.1b `thread_20261027_154439_11d2` | Anti / configured default | queued |
| P4.1c `thread_20261027_154459_1c10` | Anti / configured default | queued |
| P4.1d `thread_20261027_154500_f0b7` | Anti / configured default | queued |

四项均使用 `D:/majiang-wt/p41-localhost-impl-20261026`；a/c/d 共享 LocalTableRuntime，
b/c/d 共享 LocalTableStore 等文件，**不是四路独立物理并行**。当前串行队列没有被本轮绕过。
原 P4.1 `thread_20261026_163026_eac6` failed（DeepSeek 周额度），保留原范围和审查历史；
其原始缺陷现由上述四个既有子线程负责，不重新派发原任务。

P5.1 `thread_20261026_163027_db9e` 第5轮 failed（GLM 429，记录的额度重置时间17:45:07）。
第4轮返修要求仍有效：投影候选污染/鸣牌后弃牌/候选边界、WELCOME 顶号唯一性、编码扫描绕过。
未批准，未并入 PR20；其文件范围仍属原线程。本轮四个活动占用已满，不启动第五项重试。
旧 P4.1 前置准备失败线程的已替代文档义务不作为新任务复制。
无 waiting_review 项需要本轮作新代码裁决；completed 的 P4.3-B1/P4.4/P4.3-A/P4.2-core/P5.2
均已有 PR20 merge-back 记录，未重复 approve 或 record。

下一步：先审查 P4.1 返回结果；P4.2-B 等待可接受 LocalHost factory，P4.5 等待 P4.1–P4.4。
P5.4b 等待 P5.1 接受及 P5.2/P5.3/Client 的实际集成；P6.1/P6.2 仍等待 G2+G3。

## 当前 PR20 原始 CI 独立核实

两次 run 的 artifact 与完整 job log 均下载保存，没有重跑或把旧 SHA 的证据移用到新头。

| 运行 | 实际执行提交 | 结果 |
| --- | --- | --- |
| [PR run40469861145](https://github.com/liuchangready/majiang-game/actions/runs/40469861145) | `1b719d3b1a79dd0fc9e0e7c770cab1d76205015c`，PR20 合成 merge | PASS |
| [push run40469854903](https://github.com/liuchangready/majiang-game/actions/runs/40469854903) | `b84161424a4899afe94fc46faf1c35c18a1a24b5` | PASS |

fetch 合成提交后核实父提交为 `8fd7c6c` 和 `b841614`；两者树 SHA 均为
`7a618b9f0a343a3a1c5aa9960f0ade2cf5a80a40`，所以 PR run 与 push run 测试的是同一内容树。

每个 artifact 的十份 direct runner 日志和十份 native exit metadata 均独立解析：
Architecture83、WSS80、TableRuntime400、Client153、TableModel134、AI444、Contracts149、
Lobby89、Domain529、Infrastructure35，合计 **2096 PASS / 0 failed / 0 skipped / 0 errors / 0 Not Run**。
构建0 warning/0 error；实际 plugins 目录12个 DLL；伴随黄金4/4 PASS。
这些是 .NET 测试，不能算 Unity 场景或 IL2CPP 实测。

真实 PostgreSQL16.10 的 table wrapper 全阶段 PASS，35/35 XML、定向13/13、提交后杀 writer、
PostgreSQL immediate crash/restart、cold reader、空库备份恢复均有原始证据；这些 wrapper 复跑
不重复计入2096。Lobby wrapper plannedExit0、failedStages0、owned cluster rootDeleted=true。
测试专用 tmp/xml-selfcheck 的故意失败/跳过 XML 是门控负例，不计入实际测试结果。
当前 Client153/153 为本提交结果；历史 run39970580589 缺原框架日志的根因仍 UNRESOLVED。

核实命令包括 `gh run view --json ...`、`gh api .../artifacts`、`gh run download --dir ...`、
`gh run view --log`、`git show --no-patch --format=...`，以及对下载日志/XML/meta/JSON 的独立断言。

## 本地检查与证据

在复核树执行原版检查器：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/check-unity-boundaries.ps1 -Project unity/Mahjong.Unity -Policy tools/unity/p42-boundary-policy.json -Report artifacts/progress-20261027-1712/local-static-boundary.json
```

native exit0，Passed=true；`git diff --check origin/v5..HEAD` exit0。
本轮未运行 Unity Editor、StrictCompiled、Player 冒烟、Android/iOS 或物理跨网；不继承为新实测。
P4.3-B1 原验收报告保留其实际 Editor/PlayMode/Player build 证据，本轮只补齐当前头远端 CI 待核实项。

证据根：`D:/majiang-wt/codex-progress-20261027-1712/artifacts/progress-20261027-1712/`。
`independent-evidence-check.json` 保存两 run 的断言结果，`evidence-sha256.csv` 保存410个文件的
路径/大小/完整 SHA256，manifest SHA256：
`50470907899AD8254628DB9C264891126B959367CC6D4D41C9AAA8549639D972`。
首次生成 manifest 遇 PowerShell 长路径解析失败；改用只读文件流后成功，未删改下载证据。

Scheduler 实际 enabled=true、PT30M、IgnoreNew；complete.flag 不存在，未创建。
181 classified 仍未覆盖，Android FAIL、iOS BLOCKED、设备/跨网 NOT_RUN 保留；本轮没有升级阶段门。
