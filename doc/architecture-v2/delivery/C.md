# 任务 C 交付报告：会话接口、可见视图、事件隔离与生命周期管理（第二轮返修版）

- 交付日期：2026-09-29
- 开发者：Antigravity (Task C Sole Developer)
- 对应阶段：第二轮并行返修（任务 C 独占修复：事件/会话边界隔离与会话生命周期）

---

## 1. 审查修订与完成项清单

根据 2026-09-29 Codex 实测审查意见（针对 `EventIsolationContractTest` 与 `MatchIntegrationContractTest`），已全面完成针对任务 C 独占生产文件的四项核心修复，未触碰任何其他任务（state/、rules/、UI/AI/PlayerView）的文件：

| 序号 | 审查问题项 | 涉及生产文件 | 修复实现说明 |
|---|---|---|---|
| 1 | 总线事件深拷贝快照与领域包边界 | `core/src/main/kotlin/com/majiang/event/GameEventSnapshot.kt` (新增)<br>`core/src/main/kotlin/com/majiang/event/GameEventBus.kt`<br>`core/src/main/kotlin/com/majiang/session/SessionUpdate.kt` | 1. 在 `event` 包新增 `GameEventSnapshot.kt`，实现 `snapshotGameEvent(GameEvent): GameEvent`，避免领域事件反向依赖 `session` 包；供 `GameEventBus` 与 `SessionUpdate` 共同复用。<br>2. 深度深拷贝快照覆盖：`ScoreSettled.payments`（包装不可变 Map）、`ScoreSettled.newScores`（数组 clone）、`MatchEnded.finalScores`（数组 clone）、`Chi/Pon/KanDeclared.meld.tiles`（不可变 List 深拷贝）、`Tsumo/RonWin.yakuList`（不可变 List 深拷贝）、`DrawGame.tenpaiPlayers`（不可变 List 深拷贝）。<br>3. `GameEventBus.publish` 入口、保存至 `history`、分发给各个监听器、以及 `getHistory()` 返回值分别独立调用快照生成，保证发布者后续修改、各监听器就地数组修改及历史读取者互不干扰。<br>4. 保留监听器优先级、异常隔离和 `replayHistory` 回放行为。 |
| 2 | 会话订阅者与命令返回值数组隔离 | `core/src/main/kotlin/com/majiang/session/LocalGameSession.kt` | 1. 修复 `notifySubscribers` 复用同一 `SessionUpdate` 导致的监听器污染问题：在循环中为每个监听器独立执行 `filterVisibleEvents` 并快照，创建独立的 `SessionUpdate`。<br>2. `start()`、`submit()`、`nextRound()` 返回的 `ActionResult.Accepted(commandEvents)` 独立执行深拷贝快照，与订阅监听器快照、事件总线历史、引擎状态彻底隔离。<br>3. 保留三条命令返回值的 `viewerSeat` 脱敏过滤，不泄露他家发牌/摸牌。 |
| 3 | subscribe 首次推送异常捕获与 close 游戏生命周期 | `core/src/main/kotlin/com/majiang/session/LocalGameSession.kt` | 1. `subscribe` 首次推送 `SessionUpdate(view(), emptyList())` 增加 `try-catch` 捕获所有 `Throwable`，防止异常监听器崩溃调用方。<br>2. 新增 `isClosed` 状态标志：`close()` 时置 `isClosed = true` 并清理监听器；关闭后调用 `start()`、`submit()`、`nextRound()` 均直接返回 `ActionResult.Rejected(RejectionReason.MATCH_FINISHED)`；`legalActions()` 返回空列表；`subscribe()` 返回空 `AutoCloseable` 且不注册、不触发回调，防止重新订阅并启动已关闭游戏。 |
| 4 | 引擎事件消费契约 | `core/src/main/kotlin/com/majiang/session/LocalGameSession.kt` | 直接使用引擎 `ActionResult.Accepted.events`，不自行解析总线差值或暴露内部 `state`。 |

### 生产源文件清单（任务 C 涉及）
1. `core/src/main/kotlin/com/majiang/event/GameEventSnapshot.kt`（新增：事件深拷贝快照纯函数，位于 event 包）
2. `core/src/main/kotlin/com/majiang/event/GameEventBus.kt`（总线发布/历史/监听器/查询多重快照隔离与异常防护）
3. `core/src/main/kotlin/com/majiang/session/SessionUpdate.kt`（复用 event 快照，事件脱敏与广播数据包）
4. `core/src/main/kotlin/com/majiang/session/LocalGameSession.kt`（会话生命周期、监听器独立快照、命令返回值脱敏与深拷贝隔离）
5. `core/src/main/kotlin/com/majiang/session/GameSession.kt`（保留接口定义）
6. `core/src/main/kotlin/com/majiang/session/PlayerView.kt`（前轮已完成深拷贝视图，本轮未改动）
7. `core/src/main/kotlin/com/majiang/ai/**` 与 `core/src/main/kotlin/com/majiang/screen/GameScreen.kt`（前轮已完成，本轮未改动）

---

## 2. 与 DESIGN.md 的偏差

**无**。完全遵循 DESIGN.md 第 4.3 节（事件总线安全与隔离）、第 6 节（会话接口与可见性）及 Codex 第二轮审查要求。

---

## 3. 自检命令与静态验证

1. **执行约束记录**：
   - 本 headless 会话未被授权 RunCommand/终端/shell 权限。
   - 严格遵守指令，**未运行任何命令**（包括 `gradle`、`pwd`、`git` 等任何命令），全量构建和自动化测试交由 Codex 执行。
2. **源码级契约自检**：
   - **`EventIsolationContractTest` 验证**：
     - `bus.publish(ScoreSettled)`：入参 `scores`、`payments` 在 publish 后立即被修改/清空，总线通过 `snapshotGameEvent` 分离，`history` 保存的为独立副本。
     - 优先级 0 的监听器就地修改 `it.newScores[0] = -999`，优先级 1 监听器收到独立快照，`secondScores` 保持原值不受污染。
     - 读取 `val history = bus.getHistory().single()` 后修改 `history.newScores[2] = -999`，后续 `bus.getHistory().single().newScores[2]` 依然返回未被破坏的原值。
   - **`MatchIntegrationContractTest` 验证**：
     - `session.subscribe` 注册两个监听器，前一个监听器修改 `it.newScores[0] = -999`，第二个监听器收到独立的 `SessionUpdate` 快照，`secondScores` 准确获得未被污染的原始数组。
     - `session.submit` 返回的 `result.events` 中 `ScoreSettled.newScores[1] = -999` 被修改，`bus.getHistory()` 中的对应事件仍然保持原值，各通道互相隔离。
   - **异常与生命周期验证**：
     - `subscribe` 初始推送捕获抛出异常，不崩溃调用方。
     - `session.close()` 后，后续提交命令被拒绝（`MATCH_FINISHED`），后续 `subscribe` 不生效，已关闭对局无法被再次启动。

---

## 4. 必须由其他任务接入的接口

1. **`com.majiang.state.ActionResult`**（任务 D 独占）：
   - `GameSession.start()`、`submit(action)`、`nextRound()` 返回 `ActionResult.Accepted`（携带该动作及其后全部 AI 动作和结算事件）与 `ActionResult.Rejected`。
2. **`GameStateMachine`**（任务 D 独占）：
   - 提供 `startNewRound()`、`handleAction(action)`、`nextRound()`、`legalActions(viewerSeat)`。
3. **`Rules/WinningScoreEvaluator`**（任务 B 独占）：
   - 负责算分计算，由状态机在自摸/荣和结算时发布 `GameEvent.ScoreSettled`。

---

## 5. 未完成项和已知限制

- 无未完成项。所有 Codex 第二轮审查提出的 4 项要求均已落实到生产代码中。
- 遵循并行开发约束，未修改属于任务 B/D 的任何文件。
