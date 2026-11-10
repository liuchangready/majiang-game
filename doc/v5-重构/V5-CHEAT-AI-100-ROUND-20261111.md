# V5 千术随机 AI 接入与 100 局长验证任务卡

- 任务 ID：thread_20261111_013905_2d5b / `V5-CHEAT-AI-100-ROUND-20261111`
- 目标：新增一种 AI，普通动作使用 `BasicAiStrategy`，在权威授权的合法时机随机选择已有千术；接入 LocalHost；用该 AI 连续完成 100 局真实对局，保留逐局记录、总耗时和统计。
- 执行者：DSH（本任务）
- 复核者：本任务未委派独立复核者；交付由桥接调用方复核。

## 身份与范围

- 源仓库：`D:/majiang`
- 源分支：`chore/cleanup-step1-20261110`
- 源 HEAD：`0f5d82b3a39a15bff26a8b440e0fda5a37f7018d`
- 源工作区观察：`M AGENTS.md`（未提交；本任务未修改 `D:/majiang`）
- 本任务隔离工作树：`E:/agent-space/thread_20261111_013905_2d5b/worktree`
- 本任务分支：`mesh/task/thread_20261111_013905_2d5b`
- 本任务基线 commit：`0f5d82b3a39a15bff26a8b440e0fda5a37f7018d`
- 允许修改：`dotnet/src/Mahjong.Ai/`、`dotnet/src/Mahjong.TableRuntime/`、`dotnet/src/Mahjong.LocalHost/`、对应测试、`dotnet/tools/CheatAiBatchRunner/`、本任务文档。
- 明确排除：不修改源工作树；不提交、不推送、不合并、不部署；不改规则、不弱化既有断言/黄金样本；不实现新通用框架。

## 设计边界

- 新 AI 稳定 ID：`baseline-basic-random-cheat`（`RandomCheatBasicAiStrategy`）。
- 普通动作：完全委托 `BasicAiStrategy`。
- 千术：只使用 `TableActor` 在串行权威队列中生成的候选；AI 仅看到本座位点数、候选 optionId/能力 ID/费用，看不到 `GameState`、牌山顺序、对手暗牌或提交回调。
- 提交：`TableActor.SubmitAiCheatAsync` 重新进入同一串行队列，复用 `RoundCheatTransition.Prepare`、计费、账本、136 张物理移动、commit 与恢复日志；不另建规则路径。
- 范围：落汗、袖里藏花（本回合即时路径）、偷梁换柱。袖里藏花他家回合延迟路径未纳入本次 AI 候选，原因是其交付走独立的内部 discard-delivery journal，超出本功能修改范围；该项在结论中列为 NOT_RUN/未覆盖。
- 随机：`SeededAiRandom`（SplitMix64），显式 seed；每座位每局累计预算 `60` 点，选择时预扣，authority 拒绝也不会超预算。
- 配置：`LocalTableHostOptions.Ai` / `LocalMatchHostOptions.Ai`；`LocalAiProfiles.RandomCheatBasic`；默认 `legacy-heuristic` 保持旧行为。
- 无合法候选、未授权、点数不足、超预算、非法或 authority 拒绝时返回普通 Basic 动作。

## 阶段表

| 阶段 | 实际命令 | 进入条件 | 成功条件 | 超时 | 产物 |
|---|---|---|---|---|---|
| A 环境短预检 | `dotnet build dotnet/src/Mahjong.LocalHost/Mahjong.LocalHost.csproj`；1 局 runner | 源码只读检查完成 | 构建 0 错误；1 局真实终局、136 守恒、至少一次千术 | 180s/局 | 构建输出、`build/cheat-ai-precheck/` |
| B 定向测试 | `dotnet test`（Ai / LocalHost / TableRuntime / Architecture）；3 局预算预检 | A 通过 | 新策略正负例、Basic 等价、随机可复现；真实 LocalHost 终局与账本断言通过 | 300s/测试项目 | 测试输出 |
| C 100 局完整验证 | 见下方“100 局命令” | B 通过 | 100/100 真实终局；每局 136 守恒；逐局记录/汇总/哈希完整 | 120 分钟总，180s/局 | `build/cheat-ai-100/5bf17294fee94dc49fb606c8772119c6/` |
| D 交付 | 哈希清单、源码身份、结果核对 | C 结束 | 明确 PASS/FAIL/NOT_RUN；不覆盖历史证据 | — | 本文件 + 输出目录 |

## 100 局命令与资源边界

```powershell
dotnet run --no-build --project dotnet/tools/CheatAiBatchRunner/CheatAiBatchRunner.csproj -- `
  --rounds 100 `
  --seed 20261111 `
  --output E:/agent-space/thread_20261111_013905_2d5b/worktree/build/cheat-ai-100/5bf17294fee94dc49fb606c8772119c6 `
  --round-timeout-seconds 180 `
  --overall-timeout-minutes 120
```

- 输出根（唯一）：`build/cheat-ai-100/5bf17294fee94dc49fb606c8772119c6/`
- 每局目录：`rounds/round-NNN/`（独立 LocalHost SaveDirectory）
- 逐局记录：`rounds/round-NNN.json`
- 逐决策事件：`decisions.jsonl`（一行一条，仅包含座位/阶段/optionId/能力/结果/状态版本）
- 汇总：`run.json`、`summary.json`；配置：`config.json`
- 哈希清单：`sha256.txt`（runner 原始）、`sha256-final.txt`（完整）
- 运行方式：4 个座位全部 `Computer`，每局新房间、新 `RoundId`；固定 seed 派生每局 wall/随机。
- 共享资源：无 Unity、无设备、无数据库锁；仅本进程独占输出目录。总等待上限 120 分钟；每 30 秒 heartbeat；取消为 Ctrl+C（有界 cancellation）。
- 失败处理：首败保留并停止；不覆盖旧输出；重跑使用新 GUID 根。

## 验证断言

- 每局 `summary.Finished == true` 且结果为真实终局（Tsumo/Ron/ExhaustiveDraw/AbortiveDraw）。
- `TilePositionMap.Validate` 通过，`AssignedCount == 136`。
- 分数守恒：`sum(finalScores) + remainingRiichiSticks * 1000 == 100000`。
- 千术账本：由 `TableCheatLedgerSnapshot` 的 `TILE_MARK`/`SLEEVE_BLOOM`/`BEAM_SWAP` 负数条目聚合；每局至少一次实际扣费；非法/拒绝在 `decisions.jsonl` 中可检索。
- 组件/策略测试不等同于 G2、设备或上线验收。

## 定向测试结果

| 命令 | 退出码 | 结果 |
|---|---|---|
| `dotnet build dotnet/Mahjong.sln -v:minimal --nologo` | 0 | 0 警告 / 0 错误 |
| `dotnet test dotnet/tests/Mahjong.Ai.Tests/Mahjong.Ai.Tests.csproj --no-build --nologo` | 0 | 452/452 通过 |
| `dotnet test dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj --no-build --nologo` | 0 | 200/200 通过 |
| `dotnet test dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj --no-build --nologo` | 0 | 493/493 通过 |
| `dotnet test dotnet/tests/Mahjong.Architecture.Tests/Mahjong.Architecture.Tests.csproj --no-build --nologo` | 0 | 132/132 通过（首次因缺少 Release 产物失败；构建全部 `dotnet/src` Release 后通过） |

新增测试覆盖：
- `SeededAiRandom` 同 seed 确定性、范围约束。
- 固定 seed 的 cheat 选择序列一致。
- disabled policy 永不选千术；点数不足/单能力关闭/超 per-use 预算的候选被过滤；空候选返回 `null`（普通 Basic 回退）。
- 每座位每局累计预算不被突破；跨局重新计数。
- offer choice 不允许 skip 且只返回授权候选。
- 普通动作与 `BasicAiStrategy` 输出一致。
- 真实 LocalHost 1 局：终局、136 守恒、分数+立直棒守恒、至少一次真实千术扣费。

## 100 局结果（PASS）

- 总 wall-clock：`512554 ms`（`00:08:32.554`）
- 请求 / 完成 / 失败 / 未运行：`100 / 100 / 0 / 0`
- 每局耗时：平均 `5125.5 ms`，最小 `2269 ms`，最大 `7361 ms`，p50 `5451 ms`，p95 `6409 ms`
- 终局分布：Ron `40`，Tsumo `16`，ExhaustiveDraw `44`，AbortiveDraw `0`
- 136 守恒：`100/100` 局 `AssignedCount == 136` 且 `IsValid == true`
- 分数守恒：`100/100` 局 `sum(finalScores) + remainingRiichiSticks * 1000 == 100000`
- 千术账本：`3120` 次扣费 / `20625` 点；`TILE_MARK 1175/1175`，`SLEEVE_BLOOM 906/9060`，`BEAM_SWAP 1039/10390`
- 策略预算：每座位每局累计 `60` 点；实际平均 `206.25` 点/局（四座位合计）
- 逐决策记录：`12779` 条；`7714` 普通动作、`5065` 已接受千术决策、`0` 千术拒绝、`0` 普通策略回退
- 负余额局：`0`；无千术扣费局：`0`
- 首败：无；runner 原生退出码 `0`
- 心跳：每 30 秒；最后一次 `00:08:30` 显示 99 完成、0 失败、当前第 100 局

输出根（唯一 GUID）：`build/cheat-ai-100/5bf17294fee94dc49fb606c8772119c6/`

> 早先 `build/cheat-ai-100/ff8709864cb54728b404c25d4e1a47da/` 是预算累计修正前的 100 局历史证据，已保留不覆盖；最终结论以上表与下述 GUID 为准。

| 产物 | 路径 |
|---|---|
| 逐局记录 | `rounds/round-001.json` … `round-100.json`；每局存档 `rounds/round-NNN/` |
| 逐决策记录 | `decisions.jsonl`（12779 行，一行一条） |
| runner 汇总 | `run.json` |
| 后处理统计 | `summary.json` |
| 配置/seed/命令 | `config.json` |
| 源码哈希 | `source-sha256.txt` |
| 构建/源码身份 | `build-info.json` |
| runner 原始清单 | `sha256.txt`（402 个文件） |
| 完整清单 | `sha256-final.txt`（408 个文件，含 README/summary/config/build-info） |

## 复现性

用最终构建的 runner 以同 seed 重跑 3 局（`build/cheat-ai-precheck/budget-three`）：第 1–3 局的 seed、终局类型、赢家、最终分数、千术扣费次数与最终 100 局运行完全一致。说明随机分支、累计预算和终局可复现；该 3 局不作为 100 局的替代。

## 状态

- A：PASS
- B：PASS
- C：PASS（100/100）
- D：PASS（证据闭包已封存；未提交/推送/合并/部署）

## 源码身份

- HEAD：`0f5d82b3a39a15bff26a8b440e0fda5a37f7018d`（未提交改动；本任务不提交）
- 分支：`mesh/task/thread_20261111_013905_2d5b`
- 工作树：`E:/agent-space/thread_20261111_013905_2d5b/worktree`
- 源工作树：`D:/majiang`，HEAD 同上，观察 `M AGENTS.md`（未触碰）
- 实现源码 SHA-256：见输出根 `source-sha256.txt`
- 最终构建产物哈希：见输出根 `build-info.json`

## 未覆盖 / NOT_RUN

- Unity Player / Android 设备 / 共享设备验收：NOT_RUN（本任务明确使用无头 .NET LocalHost）。
- 真实 100 局之外的历史对局或模拟计数：未使用。
- 袖里藏花他家回合（off-turn）激活及延迟交付：NOT_RUN；actor capture 不提供该候选。
- 单场连续 match 的 100 局比分推进：NOT_RUN；本次为 100 个连续独立 LocalHost 局（每局新房间/新 RoundId）。
- G2、上线、发布、推送、合并：NOT_RUN。
