# V5 定时检视：PR20 当前头与后续展示任务

日期：2026-10-27 02:15–02:25，Asia/Shanghai。调用者 Codex。

## 基线与任务

本轮全局桥接列表读取一次（最近 100 个线程）。已 fetch：`origin/v5` 仍为
`8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`，PR19 已合并，不重开。
唯一后续批次为 [PR20](https://github.com/liuchangready/majiang-game/pull/20)，
已接受头 `42953c949822608719cb8b862ab062646fea2547`，仍为 draft。
P5.2、伴随验证接入及 P4.2-core 均已接受并记录 merge-back；本轮没有重复批准。

| 线程 | 实际状态/路由 | 本轮处理 |
| --- | --- | --- |
| `thread_20261026_163026_eac6` P4.1 | reworking，第22轮，DeepSeek Harness / deepseek-v4.1-flash | 保留在途工作，不读取其未提交源码作为集成输入 |
| `thread_20261026_163027_db9e` P5.1 | running，第1轮，DeepSeek Harness / z-ai/glm-5.3-flash | 保留用户执行器选择器于 02:02 左右改变的路由，不视为已交付 |
| `thread_20261026_163027_3b00` P4.2-core | completed，Anti，MR synced | 按已接受接口准备 P4.4，无重复派单 |
| `thread_20261025_194659_8d8a` 旧P4.1合同准备 | failed，原生session不存在，mergePending=false | 已有后续合同补充及正式实现线程承接，不重试旧线程 |

初始活动数2/4。DeepSeek Harness活动2，其中指定Flash为1、GLM为1；
Claude Code活动0；Anti活动0。计划只补Anti的P4.4：其依赖P4.2只读接口已接受，
与两个在途宿主任务独立。补后为3/4；不再补派第三个DeepSeek Harness或第二个GLM。
缺少精确2 Flash + 1 Claude/GLM配置的原因是用户手动改变的在途P5.1路由；
依照保留路由要求不自动改回。P4.3也已具备接口依赖，但本轮不突破该配额矛盾。

P4.4准备树 `D:/majiang-wt/p44-spectator-anti-20261027`，
分支 `wt/v5-p44-spectator-anti-20261027`：先从当前origin/v5创建，
再fast-forward到已独立接受的后继 `42953c9`；clean，祖先检查通过。
后续桥接返回才证明派发成功，本记录不预填线程ID。

## 当前头双CI独立复核

本轮下载并复核远端历史实跑，不把它们写成本轮新执行全量测试：

| run | API源头 / 实际checkout | 原始证据结果 |
| --- | --- | --- |
| [push 40374446244](https://github.com/liuchangready/majiang-game/actions/runs/40374446244) | `42953c9` / 同SHA | 9测试工程合计1962通过，0失败/错误/跳过；12行工程门PASS |
| [PR 40374456610](https://github.com/liuchangready/majiang-game/actions/runs/40374456610) | `42953c9` / `5e194e2f14b305bcb2a292c0c99d4020e5e183f4` | 同上；合成提交parents恰为`8fd7c6c`与`42953c9`，tree与源头完全相同 |

逐工程原始direct runner输出及native meta相互校验：Domain529、TableRuntime400、
Ai444、Client153、Architecture83、Contracts149、Infrastructure35、Lobby89、Wss80。
每项native exit0；raw输出均errors/failed/skipped=0。direct模式没有额外框架日志时，
不声称存在不存在的框架文件。旧39970580589丢失框架日志的历史失败原因仍未裁定。

两个run均含solution build、scratch 12 DLL闭包及伴随4/4 PASS。
各自PostgreSQL16.10桌存储wrapper的11阶段及大厅wrapper全部PASS；
桌测试XML35/35、定向XML13/13，0失败/错误/跳过/not-run。
原始证据包括已提交writer被kill、实际PostgreSQL crash/restart、
独立reader恢复终态/非空账本/回执/outbox、从空库restore及owned cluster清理。
两个桌证据manifest各134项，合计268/268逐字节长度及SHA256复核通过。
初次Windows PowerShell长路径访问失败，改用扩展路径FileStream重哈希后全量通过，
未把长路径错误当作CI失败或缺失产物。

远端伴随历史Git pins仍为NOT_RUN；当前字节及内嵌来源PASS。
本轮本地另跑`verify-v5-companion-fixtures.ps1 -VerifyGitPins`：native0，
当前4/4及四份历史freeze blob全PASS。不得把本地历史检查归为远端CI运行。

## 本轮本地检查与证据

独占检视树 `D:/majiang-wt/codex-progress-20261027-bridge`，从origin/v5创建后
fast-forward至接受头；没有合入任何receiver未提交工作。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File artifacts/ci-42953c9/audit.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/check-unity-boundaries.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/verify-v5-companion-fixtures.ps1 -VerifyGitPins
git diff --check origin/v5..HEAD
```

均exit0。boundary仅静态检查，本轮未重跑Unity/Player/IL2CPP/本地PostgreSQL。
证据根`artifacts/ci-42953c9`保存双run产物、job日志、audit脚本/结果及本地日志；
406文件的`evidence-sha256.csv` SHA256：
`4DD91FD107A0EEA5AC866553BCB0AA94176D6B59DE0C59799B6C5344CEAFB48A`。

调度任务`Mahjong-V5-0-1-Progress`存在且当前Running；complete.flag不存在。
本轮不创建新timer、不重开PR19、不merge PR20、不修改主checkout或receiver源码。
G1 APPROVED；G2–G5未批准，V4仍为生产。181 classified未覆盖，
Android FAIL、iOS BLOCKED、真机/跨物理网络NOT_RUN继续明确保留。
P4.2-B等待P4.1工厂；P4.5等待P4.1/P4.3/P4.4；P5.4b等待P5.1交付及真实集成。
