# 任务 ANTI-V2FIX-E3 交付报告：最后两个展示工具与 Helper 完善

- **交付日期**：2026-10-05
- **执行角色**：Antigravity (Task ANTI-V2FIX-E3 生产与冒烟测试工具实现)
- **调用链路**：codex -> antigravity
- **协作模式**：Codex 负责设计、审查与实测；Antigravity 负责独立文件实现（严格仅使用内置文件读/写/改工具，无终端/命令行执行）
- **目标设计与任务契约**：
  - [GAME_ARCHITECTURE_V3.md](../GAME_ARCHITECTURE_V3.md)
  - [STATUS.md](STATUS.md)
  - [SmokeScreenClient.java](../../tools/architecture/SmokeScreenClient.java)
  - [DiscardInputGateRenderSmoke.java](../../tools/architecture/DiscardInputGateRenderSmoke.java)
  - [SettlementDetailRenderSmoke.java](../../tools/architecture/SettlementDetailRenderSmoke.java)
  - [discard-input-gate-smoke.init.gradle](../../tools/architecture/discard-input-gate-smoke.init.gradle)
  - [settlement-detail-smoke.init.gradle](../../tools/architecture/settlement-detail-smoke.init.gradle)

---

## 1. 唯一写入范围与交付清单

严格遵循任务所有权契约，仅修改授权的 5 个文件并新增本交付报告；未改动 core/engine 生产源码，未删除或弱化任何断言测试，未触碰其他任务工具：

| 文件路径 | 变更类型 | 说明 |
|---|---|---|
| `tools/architecture/SmokeScreenClient.java` | 修改 | 1. 完善 Round Identity：引入自增 `roundId`（`"smoke-round-" + (++roundCounter)`），在接收 `StackingStarted` 时创建新局 ID 并清空旧结算。<br>2. 完善结算事件生命周期：`update(view, events)` 保留终局事件到后续无事件的普通刷新；独立 `update(events)` 在新 Win/Draw 到达时生成新 `settlementRoundId` 并替换旧事件，而单一 `ScoreSettled` 到达时合并保留当前 ID。<br>3. 完善 `nextRound` 行为：发布无结算 ID 与无结算事件的全新干净快照，清空事件流，不携带旧事件。<br>4. 防御性深拷贝：调用 `PlayerSnapshot.deepCopy()` 与 `GameEventSnapshotKt.snapshotGameEvent` 对快照、集合与事件全量深拷贝。<br>5. 严格动作校验：`choose(GameAction a)` 校验当前 `view.legalActions`，非法动作返回 `REJECTED (ACTION_NOT_AVAILABLE)`。<br>6. 保持既有全部公共字段与方法 API，保障已有已迁工具二进制与源码级完全兼容。 |
| `tools/architecture/DiscardInputGateRenderSmoke.java` | 修改 | 1. 架构升级：`PreviewSession extends SmokeScreenClient` 替换 `GameSession`，使用 `game.changeScreen(new GameScreen(game, session, null))` 替换 `launchGame(session)`。<br>2. 动作流转：`choose` 保留原四家牌河真实变更视图，通过 `super.update(view())` 派发广播，返回 `APPLIED` 回执；`submitted` 仅自增一次。<br>3. 异步帧对齐：同帧双击由 `GameScreen.isSubmitting` 门控拦截，快照驱动的 `screen.isDiscardPlaybackBusy()` 调整至下一帧断言；保留全部 4 次飞行动画等待、飞行中手牌与理牌拦截、解锁后第二次打牌及重新加锁断言，保留 4 张全量截图与通过输出。 |
| `tools/architecture/SettlementDetailRenderSmoke.java` | 修改 | 1. 架构升级：`PreviewSession extends SmokeScreenClient` 替换 `GameSession`，使用 `game.changeScreen(new GameScreen(game, session, null))` 替换 `launchGame(session)`。<br>2. 算分事件发射：`emit`、`emitRon`、`emitLegacyLong` 直接使用 helper 的 `session.update(List<GameEvent>)` 派发。<br>3. 异步帧对齐：`emitRon` 触发后，下一帧再点击 `CALC` 标签，确保详情数据已加载；点击 `NEXT ROUND` 推进后，下一帧断言 `screen.isSettlementShown() == false`。<br>4. 保留全部 17 张截图、中文字体 Glyph 检查、文本边界折行检查、多页翻页、役满、满贯和旧记录回退覆盖。 |
| `tools/architecture/discard-input-gate-smoke.init.gradle` | 修改 | `compileDiscardInputGateSmoke` 任务 source 中加入 `tools/architecture/SmokeScreenClient.java`。 |
| `tools/architecture/settlement-detail-smoke.init.gradle` | 修改 | `compileSettlementDetailSmoke` 任务 source 中加入 `tools/architecture/SmokeScreenClient.java`。 |
| `doc/v3-migration/delivery-ANTI-V2FIX-E3.md` | 新增 | 本交付报告。 |

---

## 2. 详细改造设计与实现对齐

### 2.1 SmokeScreenClient 完善

1. **Round Identity 管理**：
   - 增加 `roundCounter` 与 `roundId` 属性，初始为 `"smoke-round-0"`。
   - 当接收到 `GameEvent.StackingStarted` 时（包括 `start()` 触发或显式 `update`），判定为新局开始，自增生成新 `roundId = "smoke-round-" + (++roundCounter)`，同时重置 `settlementRoundId = null` 与 `settlementEvents = Collections.emptyList()`。
   - 使得快照在进入新局时不会遗留旧局的结算弹窗状态。

2. **结算事件流合并与独立场景支持**：
   - `update(PlayerView v, List<GameEvent> events)`：
     - 若包含 `StackingStarted`：换局并清空结算。
     - 若包含 Win/Draw 事件：新生成 `settlementRoundId` 并更新结算事件。
     - 若仅包含 `ScoreSettled`：合并到现有结算事件中（替换或追加），不更换 `settlementRoundId`。
     - 若无结算事件且无 `StackingStarted`（如出牌后视图刷新、摸牌刷新等）：**保留原有的 `settlementRoundId` 与 `settlementEvents`**，防止后续视图刷新冲掉正在展示的结算弹窗。
   - `update(List<GameEvent> events)`：
     - 作为独立展示结算场景的快捷入口：遇到 `TsumoWin`、`RonWin` 或 `DrawGame` 时，视为新结算场景，生成新的 `settlementRoundId = "settlement-round-" + (++settlementCounter)` 并全量替换 `settlementEvents`。
     - 单独追加到达的 `ScoreSettled` 则合并替换旧的得分结算事件，保持现有 `settlementRoundId` 不变。

3. **`nextRound` 行为规范**：
   - 显式清理 `settlementRoundId = null` 与 `settlementEvents = Collections.emptyList()`。
   - 构造无结算的新快照，并向监听器广播空事件列表 `Collections.emptyList()`，坚决不向新局复制或泄漏上一局的旧事件。

4. **防御性深拷贝与合法动作校验**：
   - 快照生成通过 `PlayerSnapshot.deepCopy()` 完成深拷贝，事件列表均经 `GameEventSnapshotKt.snapshotGameEvent` 转换。
   - `choose(GameAction a)` 校验 `view.legalActions`，非法动作返回 `REJECTED`，杜绝假成功。

---

### 2.2 DiscardInputGateRenderSmoke 迁移

1. **会话与接入层契约**：
   - `PreviewSession` 继承自 `SmokeScreenClient`，重写 `choose`，维护四家牌河真实数据，通过 `super.update(view())` 派发。
   - 动作提交只加入一次 `submitted.add(a)`，返回 `CompletableFuture.completedFuture(new ActionReceipt("choose", ReceiptStatus.APPLIED, null, null))`。
   - `MahjongGame.changeScreen(new GameScreen(game, session, null))` 替换原兼容方法 `launchGame(session)`。

2. **异步输入门控与动画帧对齐**：
   - **Stage 0**：同帧连续调用两次 `clickTile()`。由于 `GameScreen.submitActionAsync` 同步置位 `isSubmitting = true`，第二次点击被同步门控拦截，断言 `session.submitted.size() == 1` 在同帧立即通过。
   - **Stage 1**：由于 V3 快照订阅经由 LibGDX 主循环 `postRunnable` 在下一帧派发，牌河差异检测 `checkDiscardsDiff` 在此帧执行完毕。在此帧断言 `screen.isDiscardPlaybackBusy()` 为 true，完成截图 `waiting-for-discards` 并推进 0.3 秒动画。
   - **Stage 2**：循环推进 4 次弃牌动画，在每一张牌飞行中点击手牌和理牌按钮，验证 `session.submitted.size() == 1` 且 `!screen.isAutoSort()`（输入完全锁定）。4 次动画全部播放完毕后解锁。
   - **Stage 3**：截图 `playback-complete-unlocked`，执行第二次出牌点击 `clickTile()`，验证 `session.submitted.size() == 2`（已成功解锁）。
   - **Stage 4**：下一帧检查快照驱动的弃牌动画再次入队，断言 `screen.isDiscardPlaybackBusy()` 再次为 true，截图 `next-round-of-discards-locked` 并正常退出。

---

### 2.3 SettlementDetailRenderSmoke 迁移

1. **会话与接入层契约**：
   - `PreviewSession extends SmokeScreenClient` 构造时直接传入 `SettlementDetailRenderSmoke.view()`。
   - `MahjongGame.changeScreen(new GameScreen(game, session, null))` 替换 `launchGame(session)`。
   - 各类算分事件（自摸、役满、荣和、旧记录）直接调用 `session.update(List<GameEvent>)`，自动触发 helper 结算场景更新。

2. **异步输入与结算模态帧对齐**：
   - **Stage 9 -> Stage 10**：`case 9` 截图 `legacy-fallback` 并调用 `emitRon()`；下一帧（`case 10`）快照更新生效并展示和了明细后，再点击 `CALC` 标签（坐标 590, 530）。
   - **Stage 11**：截图 `mangan-ron-calc-0`，随后点击 `DORA` 标签切到宝牌页。
   - **Stage 12**：截图 `zero-dora`，执行 `checkWrapBoundary()`，发射长文本旧记录 `emitLegacyLong()`。
   - **Stage 13**：遍历长文本翻页，最后点击 `NEXT ROUND`（坐标 640, 165）。
   - **Stage 14**：下一帧（此时 `client.nextRound()` 的快照派发已生效，`settlementRoundId == null`，`showSettlement == false`）断言 `!screen.isSettlementShown()`，输出通过日志并安全退出。

---

### 2.4 Gradle Init 脚本接入

- `tools/architecture/discard-input-gate-smoke.init.gradle`：
  ```groovy
  source gradle.rootProject.files('tools/architecture/DiscardInputGateRenderSmoke.java', 'tools/architecture/SmokeScreenClient.java')
  ```
- `tools/architecture/settlement-detail-smoke.init.gradle`：
  ```groovy
  source gradle.rootProject.files('tools/architecture/SettlementDetailRenderSmoke.java', 'tools/architecture/SmokeScreenClient.java')
  ```
- 确保执行 `./gradlew -I tools/architecture/...` 编译时能够同时编译并链接 `SmokeScreenClient`。

---

## 3. 验收对照与交付确认

| 检查项 | 契约要求 | 交付状态 |
|---|---|---|
| 修改文件范围 | 仅修改指定的 3 个 Java 文件与 2 个 init.gradle，不改动其他文件 | 严格遵循 |
| Helper API 兼容 | 保持已有公共方法、字段与构造器签名，方便已迁工具编译 | 严格遵循 |
| Round Identity | StackingStarted 触发新 Round ID，清理旧结算 | 已实现 |
| 结算状态保持 | `update(view, events)` 普通更新保持终局结算状态，不被后续空白事件冲掉 | 已实现 |
| 结算场景独立发射 | 独立 `update(events)` Win/Draw 生成新结算 ID，ScoreSettled 单独到达时合并 | 已实现 |
| NextRound 快照规范 | 发布无结算快照，清空事件，不携带旧事件 | 已实现 |
| 防御性深拷贝 | 使用 `PlayerSnapshot.deepCopy()` 与 `GameEventSnapshotKt.snapshotGameEvent` | 已实现 |
| 合法动作校验 | `choose` 校验 `legalActions`，非法动作返回 REJECTED | 已实现 |
| DiscardInputGate 回归断言 | 保留全部 4 次飞行动画、输入锁定、双击拦截与解锁再锁定断言 | 严格保留 |
| DiscardInputGate 截图 | 保留全部 4 张指定名称截图与通过标准输出 | 严格保留 |
| SettlementDetail 异步适配 | emitRon 后下一帧点击 CALC 标签，下一局点击后下一帧断言 settlement 隐藏 | 已实现 |
| SettlementDetail 覆盖与截图 | 保留全部 17 张截图、中文字体 Glyph、折行边界、翻页与 legacy 覆盖 | 严格保留 |
| 生产源码与架构规范 | 不改动 engine/core 生产代码，不执行 shell 命令，不读取旧架构历史 | 严格遵循 |
