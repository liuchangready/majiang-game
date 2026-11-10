# 随机千术 Basic AI：独立复核与 MR 收口

- 任务：`thread_20261111_013905_2d5b`；复核者：Codex；2026-11-11。
- 实际开发起点：`0f5d82b3a39a15bff26a8b440e0fda5a37f7018d`。验收提交：`e5c4edbfd7801316a5ea8af49f4ac17e9753402f`。
- 独立工作树：`E:/agent-space/thread_20261111_013905_2d5b/mr-worktree`；分支：`publish/ai-review-2d5b-20261111`。
- 已有同批汇总 MR：#28；收口起点：`b77dfc84dc4c81257668e7608c839dd81ec5a4d6`。重放两轮任务提交后候选：`26c624dd`，无冲突；相对任务候选的 dotnet 差异仅为已有回放实现与测试两个文件。
- 允许修改：本复核文档与从最终源码重新生成的共享 DLL/PDB；已验收任务源码保持原样。其他工作树（包括源目录未提交的 AGENTS.md）保持独立。

## 本任务验收

本任务已调用 `approve_task`。独立构建 solution 为 exit 0、0 警告/错误；Ai 457、LocalHost 201、TableRuntime 498、Architecture 132，共 1288/1288，0 失败/跳过。原 reserve 复现工程由 exit 1 变为 exit 0。

已复核 reserve/溢出、托管默认付费隔离、同局恢复预算及权威入口的过期身份、重复提交和日志恢复。开发者最终 100 局原件位于任务工作树 `build/cheat-ai-100/a2f6ea61b09648c583f7255a55b1e7c2/`：100/100 终局、136 与分数守恒；408 个文件哈希及实现源码哈希匹配；总 wall-clock 502400 ms，3120 次扣费/20625 点，12779 条决策。此处是原件复核，不冒充独立重跑。

## MR 组合验证任务卡

- 唯一输出根：`build/ai-mr-review/bba43c2cbf9e4c7c8f8dc76656b910bf/`；不覆盖历史证据。
- 目标：保留 MR #28 已验收的清理、恢复和回放改动，加入本任务并重新同步共享插件；核验实际组合源码。
- 固定分母：四个相关测试项目全部用例；100 个连续独立 LocalHost 局，每局新 room/roundId。成功须终局、136 守恒、分数与立直棒守恒、真实千术账本及逐局记录。
- 依赖已就绪；其他近期独立任务均已有各自 MR，无待汇总的未发布同批任务。无文件冲突。
- 阶段 A：`powershell -NoProfile -File tools/unity/build-shared.ps1 -Configuration Release -DotnetPath D:/game_dev_tools/dotnet/dotnet.exe`，然后 `check-shared-plugins.ps1 -Configuration Release`；上限 300 秒。
- 阶段 B：四个 `dotnet test dotnet/tests/Mahjong.{Ai,LocalHost,TableRuntime,Architecture}.Tests/...csproj -c Release --nologo`，每项目上限 300 秒；先通过，再短预检 runner 一局。
- 阶段 C：构建 `dotnet/tools/CheatAiBatchRunner/CheatAiBatchRunner.csproj -c Release`；以 `--rounds 100 --seed 20261111 --round-timeout-seconds 180 --overall-timeout-minutes 20` 运行，输出本根 `games-100/`。短预检用独占 `precheck/`。
- 无 Unity/设备/数据库等共享资源；等待上限 600 秒（本阶段无需等待）；runner 每 30 秒心跳，复核者同时报告进度。正常取消；不停止其他进程。
- 首败保留并停止；仅修复原因/输入改变后重试。只操作本工作树和自有输出。不改测试断言或黄金样本。
- 计时边界：runner 内连续局循环 wall-clock，不含编译/测试；逐局、日志、配置、源码输入及 SHA-256 一并保留。
- 组合测试已通过：Ai 457、LocalHost 211（含此前回放测试）、TableRuntime 498、Architecture 132，共 1298/1298；均为 Release、0 失败/跳过、原生 exit 0。solution、显式 LocalHost 测试工程、runner 构建均 exit 0、0 警告/错误。
- 首败保留：并行 test build 同时构建共用的 TableRuntime.Tests，导致 LocalHost 编译 CS2012（DLL 文件锁）；随后 `--no-build` 因 LocalHost 不在 solution 中而缺少输出。显式构建该工程后，以 `--no-build --no-restore` 运行通过。日志：`localhost-tests.log`、`localhost-final.log`、`localhost-build.log`、`localhost-verified.log`。未改断言或夹具。
- 共享插件：`build-shared.ps1` exit 0；`check-shared-plugins.ps1 -Configuration Release -Json` 为 OK，161 inputs / 19 outputs / 0 failures；来源原件封存在本输出根 `shared-plugin-provenance.json`。
- 冻结实现输入 206 个文件，记录于 `source-sha256.txt`；runner Release 身份及精确命令见 `build-info.json`。一局预检 PASS，4867 ms、exit 0。
- 独立组合 100 局完成：原生 exit 0；请求/完成/失败/未运行为 100/100/0/0；wall-clock 452880 ms（7 分 32.880 秒）。每局平均 4528.79 ms，最小 2000 ms、最大 5968 ms、p50 4892 ms、p95 5611 ms。
- Ron 40、Tsumo 16、ExhaustiveDraw 44；100/100 终局、136 张与分数守恒；每座位本局花费最多 60 点，无负余额。3120 次扣费、20625 点、12779 条决策。
- `summarize.ps1` 核验 402 项 runner 原始哈希及 206 个冻结源码输入通过；100 局逐一与开发者最终原件比较 seed、终局、赢家/输家、分数、费用和完整决策序列，一致 100/100。
- 额外真实组合检查：只读 `LocalRecordedRounds.List` 读取全部 100 局独立存档，均为一份有效公开回放且事件非空，`PUBLIC_AI_REPLAYS_OK=100`、exit 0。没有启动第二个桌面宿主。
- 原件：`games-100/run.json`、100 个 `games-100/rounds/round-NNN.json` 及相应存档目录、`decisions.jsonl`、`games-100.log`、`games-100.exit.txt`、`summary.json`。runner 每 30 秒心跳，最后 7:29 为 99 局完成；首败无。

## 验收边界

Unity Player、Android、设备、G2、袖里藏花 off-turn 和单场 match 比分推进为 NOT_RUN。保留 MR #28 已记录的全批次 PostgreSQL 和 Unity 路径限制；本任务相关测试通过不能代表这些门禁通过。MR 保持 draft，远端合并与部署未执行。
