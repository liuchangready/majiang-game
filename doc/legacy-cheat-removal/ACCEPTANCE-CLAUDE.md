# 验收记录：删除怀疑度系统与四大绝技未接线代码 (ACCEPTANCE-CLAUDE.md)

- **验收日期**：2026-10-14
- **验收人**：Claude（主会话）
- **开发执行**：Anti（两段会话：桥接 MCP 600s 超时中断后，经用户授权以 `--dangerously-skip-permissions` 重启接续完成）
- **任务简报**：[TASK-ANTI.md](TASK-ANTI.md) ｜ **交付说明**：[DELIVERY-ANTI.md](DELIVERY-ANTI.md)

## 验收结论：**通过**（附 1 处验收方修复与 2 项既有问题备案）

## 验收项

| # | 检查项 | 结果 |
|---|---|---|
| 1 | `./gradlew build`（七模块全量编译 + 测试） | ✅ BUILD SUCCESSFUL |
| 2 | v4-review 独立审查 `com.majiang.review.*`（21 项） | ✅ 0 失败 |
| 3 | 全 v4-review 合跑 | ⚠️ 见"既有问题备案"，与本任务无关 |
| 4 | 残留符号 grep（`SuspicionSystem\|CheatActionExecutor\|CheatTechnique\|CheatAttempted\|BustedEvent\|UseCheatTechnique\|applyDiversion\|swapHandWithSleeve\|SuspicionChanged\|GazeShifted\|DIVERSION`，engine/protocol/server/client/presentation/tools 的 kt+java） | ✅ 0 命中 |
| 5 | 保留清单核对（cheat/mark、cheat/bloom、cheat/beam、CheatLedger、`CheatPointsChanged`、`sleeveTiles`、`swapHandWithWall`、136 守恒审计、doc/archive） | ✅ 全部完好 |
| 6 | 无关改动隔离（`lwjgl3/build.gradle`、`SnapshotLauncher.kt`、`snapshot/`） | ✅ 未被触碰（diff 仅含任务前既有改动） |
| 7 | 测试削弱检查 | ✅ 仅删除被移除符号的用例；映射计数校准（13 actions / 19 events）与实际对齐；其余断言未动 |

## 验收方修复（1 处）

- [EngineProtocolMapperTest.kt](../../server/src/test/kotlin/com/majiang/server/mapping/EngineProtocolMapperTest.kt)：Anti 交付遗漏 3 处 `UseCheatTechnique` 残留（动作清单 :146、when 断言臂 :171-175），导致 `:server:compileTestKotlin` 失败。验收方删除该用例并将测试名 `test all 14 legal action types mapping` 校准为 13。修复后全量构建通过。

## 既有问题备案（与本任务无关，HEAD=301f776 基线上同样复现）

1. `com.majiang.v4authority.V4ServerHostAcceptanceTest` 两用例**稳定失败**（3/3 复现）：
   - `pausedAuthorityExecutorDoesNotBlockInvocationAndCloseEndsQueuedFutures`（期望 STACKING 实为 ROUND_END）
   - `shuffleAndActionReceiptNamespacesRemainIndependentAfterLaterCommit`（期望不相等实为 REJECTED）
   - 经临时 worktree 在 Anti 改动前的 HEAD 上复跑证实为**既有损坏**（普通 `./gradlew test` 不包含 v4-review 测试源，故历史基线未暴露）。建议另行派发排查。
2. `SharedRoomTest.start versus join config and leave operations are serialized on authority queue`：顺序敏感的并发断言（全量合跑高负载下偶发），单独重跑 3/3 通过，判定为偶发。

## 过程记录

- 桥接 MCP `invoke_antigravity` 单次上限 600s，大任务必超时；本次中断后 Anti 进程卡死于权限墙。经用户授权改用 `agy.exe --mode accept-edits --dangerously-skip-permissions --print-timeout 0` 后台直调恢复，顺利完成。
- 简报初版漏列 `SuspicionChanged`/`GazeShifted` 孤儿事件链，验收前发现并以简报第 0 节补交 Anti 一并清理。
