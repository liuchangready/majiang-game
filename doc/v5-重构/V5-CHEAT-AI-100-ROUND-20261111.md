# V5 千术随机 AI 接入与 100 局长验证任务卡（复核返修版）

- 任务 ID：thread_20261111_013905_2d5b / `V5-CHEAT-AI-100-ROUND-20261111`
- 目标：新增一种 AI，普通动作使用 `BasicAiStrategy`，在权威授权的合法时机随机选择已有千术；接入 LocalHost；用该 AI 连续完成 100 局真实对局，保留逐局记录、总耗时和统计。
- 执行者：DSH（本任务）
- 复核者：桥接调用方；本轮为第 2 轮返修，不转派、不推送。

## 身份与范围

- 源仓库：`D:/majiang`
- 源分支：`chore/cleanup-step1-20261110`
- 源 HEAD：`0f5d82b3a39a15bff26a8b440e0fda5a37f7018d`
- 源工作区观察：`M AGENTS.md`（未提交；本任务未修改 `D:/majiang`）
- 本任务隔离工作树：`E:/agent-space/thread_20261111_013905_2d5b/worktree`
- 本任务分支：`mesh/task/thread_20261111_013905_2d5b`
- 第 1 轮工作已由桥接器自动形成提交 `f9bc04851ec17aad5ed686ed693f1f395cc43ddb`（Agent work thread ... round 1）；本轮修复为其上的未提交工作树改动。本任务未推送。
- 允许修改：`dotnet/src/Mahjong.Ai/`、`dotnet/src/Mahjong.TableRuntime/`、`dotnet/src/Mahjong.LocalHost/`、对应测试、`dotnet/tools/CheatAiBatchRunner/`、本任务文档。
- 明确排除：不修改源工作树；不推送、不合并、不部署；不改规则、不弱化既有断言/黄金样本；不实现新通用框架。

## 设计边界

- 新 AI 稳定 ID：`baseline-basic-random-cheat`（`RandomCheatBasicAiStrategy`）。
- 普通动作：完全委托 `BasicAiStrategy`。
- 千术：只使用 `TableActor` 在串行权威队列中生成的候选；AI 仅看到本座位点数、候选 optionId/能力 ID/费用。
- 提交：`TableActor.SubmitAiCheatAsync` 重新进入同一串行队列，复用 `RoundCheatTransition.Prepare`、计费、账本、136 张物理移动、commit 与恢复日志。
- 范围：落汗、袖里藏花（本回合即时路径）、偷梁换柱。袖里藏花他家回合延迟路径未纳入 AI 候选（NOT_RUN）。
- 随机：`SeededAiRandom`（SplitMix64），显式 seed。
- 配置：`LocalTableHostOptions.Ai` / `LocalMatchHostOptions.Ai`；`LocalAiProfiles.RandomCheatBasic`；默认 `legacy-heuristic` 保持旧行为。
- 无合法候选、未授权、点数不足、超出 reserve/预算、非法或 authority 拒绝时回退普通 Basic 动作。

## 本轮复核缺陷与修复

1. **ReservePoints 未执行**
   - 真实复现：`balance=5, reserve=5, cost=1, isOfferChoice=false` 时旧逻辑返回 `mark`；修复后返回 `null`。复核工程 `build/reviewer-fec79880f8de496880d174e99eb9171b/ReviewProbe.csproj` 现 `exit 0`。
   - 修复：正费用候选必须满足 `spent + cost <= MaxSpendPerRound` 且 `cost <= balance - reserve`；使用 `long`/`long` 比较避免加法溢出；0 费用已付款 offer 的安全收尾不受 reserve 限制。
2. **托管默认主动花点**
   - 修复：`LocalAiOptions.TakeoverMaySpend` 默认 `false`；driver 区分 Computer/Takeover；actor capture 与 submit 都执行托管付费闸门；已有 0 费用 offer 仍可解决。
3. **保存/恢复重置本局预算**
   - 修复：`TableActor.GetAiCheatSpentThisRound(seat)` 从权威 ledger + AI request-id 统计本局已花点；driver 每次重建后调用 `RestoreRoundSpend`；同一 round 不重置，新 round 重置。
4. **新权威入口缺少测试**
   - 新增 `TableAiCheatAuthorityTests`：非自动控制、stale state/control/recovery、重复 plan 不二次扣费、Takeover 默认拒绝/显式允许、真实付费 AI 千术 journal 恢复后账本与 136 守恒且不重复花费。
   - 新增 LocalHost 保存/重开同局预算测试。

## 定向测试结果（本轮实际执行）

| 命令 | 退出码 | 结果 |
|---|---|---|
| `dotnet build dotnet/Mahjong.sln -v:quiet --nologo` | 0 | 0 警告 / 0 错误 |
| `dotnet test dotnet/tests/Mahjong.Ai.Tests/Mahjong.Ai.Tests.csproj --no-build --nologo` | 0 | 457/457 通过 |
| `dotnet test dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj --no-build --nologo` | 0 | 201/201 通过 |
| `dotnet test dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj --no-build --nologo` | 0 | 498/498 通过 |
| `dotnet test dotnet/tests/Mahjong.Architecture.Tests/Mahjong.Architecture.Tests.csproj --no-build --nologo` | 0 | 132/132 通过（首次缺 Release 产物失败；构建全部 `dotnet/src` Release 后通过） |

注意：本轮新测试首跑出现 4 个失败（旧断言把千术费用假设为 1、stale recovery 未进入 strict fence、durable checkpoint 控制器不匹配）；均已定位并修正测试/夹具，最终 0 failed/skipped。

## 100 局结果（本轮新执行，PASS）

- 总 wall-clock：`502400 ms`（`00:08:22.400`）
- 请求 / 完成 / 失败 / 未运行：`100 / 100 / 0 / 0`
- 每局耗时：平均 `5024 ms`，最小 `2301 ms`，最大 `6900 ms`，p50 `5417 ms`，p95 `6242 ms`
- 终局分布：Ron `40`，Tsumo `16`，ExhaustiveDraw `44`，AbortiveDraw `0`
- 136 守恒：`100/100` 局 `AssignedCount == 136` 且 `IsValid == true`
- 分数守恒：`100/100` 局 `sum(finalScores) + remainingRiichiSticks * 1000 == 100000`
- 千术账本：`3120` 次扣费 / `20625` 点；`TILE_MARK 1175/1175`，`SLEEVE_BLOOM 906/9060`，`BEAM_SWAP 1039/10390`
- 策略预算：每座位每局累计 `60` 点；实际平均 `206.25` 点/局（四座位合计）
- 逐决策记录：`12779` 条；`7714` 普通动作、`5065` 已接受千术决策、`0` 千术拒绝、`0` 普通策略回退
- 负余额局：`0`；无千术扣费局：`0`
- 首败：无；runner 原生退出码 `0`
- 心跳：每 30 秒；最后一次 `00:08:00` 显示 95 完成、0 失败、当前第 96 局

输出根（本轮唯一 GUID）：`build/cheat-ai-100/a2f6ea61b09648c583f7255a55b1e7c2/`

保留的原件（不覆盖）：
- `build/cheat-ai-100/5bf17294fee94dc49fb606c8772119c6/`：上一轮 100 局证据（408 项哈希匹配）。
- `build/cheat-ai-100/ff8709864cb54728b404c25d4e1a47da/`：更早的预算累计修正前证据。

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

最终构建 runner 以同 seed 的 3 局预检与 100 局前 3 局的 seed、终局、赢家、分数、扣费次数一致；本轮 100 局结果与上一轮修复前统计一致，说明预算/托管修复未改变已确认的正常路径。

## 状态

- A：PASS
- B：PASS
- C：PASS（100/100，本轮新执行）
- D：PASS（证据闭包已封存；未推送/合并/部署）

## 源码身份

- 任务分支 HEAD（桥接器第 1 轮自动提交）：`f9bc04851ec17aad5ed686ed693f1f395cc43ddb`
- 本轮修复：HEAD 之上的未提交工作树改动
- 源仓库/源 HEAD：`D:/majiang` / `0f5d82b3a39a15bff26a8b440e0fda5a37f7018d`，观察 `M AGENTS.md`（未触碰）
- 实现源码 SHA-256：见输出根 `source-sha256.txt`
- 最终构建产物哈希：见输出根 `build-info.json`

## 未覆盖 / NOT_RUN

- Unity Player / Android 设备 / 共享设备验收：NOT_RUN。
- 袖里藏花他家回合（off-turn）激活及延迟交付：NOT_RUN；actor capture 不提供该候选。
- 单场连续 match 的 100 局比分推进：NOT_RUN；本次为 100 个连续独立 LocalHost 局（每局新房间/新 RoundId）。
- G2、上线、发布、推送、合并：NOT_RUN。
