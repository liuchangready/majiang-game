# 任务 F4 交付报告：立直连续出牌与弃牌时序动画修复

日期：2026-09-30  
开发者：Antigravity (Task F4)  
协作角色：Codex（测试设计、执行验证、代码检视）  
关联任务：立直连续出牌修复设计与开发任务 (Task F4)

---

## 1. 任务背景与核心问题根因

在麻将状态机重构分层演进中，出现以下两个耦合导致的画面表现异常：
1. **自动动作失控连击（Runaway Loop）**：`ApplicationCoordinator.runAutomaticActionLoop` 在一次玩家顶层操作（如出牌或立直）中，持续在同步内循环中自动执行所有后续 AI 摸切、AI 响应以及真人立直后的自动摸切，直到真人遇到自摸/暗杠/副露等关键选择点或流局。这导致单次顶层命令可能跨越多巡甚至直接走完整局，且在单次广播中打出大量牌。
2. **牌河弃牌动画按座位聚集（Seat Clustering）**：`GameScreen.checkDiscardsDiff` 在收到更新时未按事件时序处理，而是通过 `for (seat in 0..3)` 遍历四家最终牌河，导致在包含多巡自动摸打的批次更新中，同一座位的多次出牌被先入队，画面表现为座位 0 连续出牌多次，再轮到座位 1 连续出牌，完全破坏了麻将 0→1→2→3→0 顺位时间顺序。

---

## 2. 交付与修改文件清单

本次交付严格遵守生产边界：**仅修改生产 Kotlin 代码，不修改测试代码、构建脚本或非任务所有文件**。

### 1. `core/src/main/kotlin/com/majiang/application/ApplicationCoordinator.kt` (修改)
- 构造函数末尾添加 `val deferAutomaticActions: Boolean = false`，默认值为 `false`，完全保持既有无头自动化测试及兼容同步行为不变；
- 抽取原循环中的单步决策与执行逻辑为 `private fun stepAutomaticAction(allEvents: MutableList<GameEvent>): Boolean`，统一管理 AI 响应、AI 回合出牌及真人立直摸切的单次判定；原 `runAutomaticActionLoop` 简化为 `while (stepAutomaticAction(allEvents)) {}`；
- 在启用延迟模式 (`deferAutomaticActions = true`) 时，顶层命令（`setSeatController`、`submitChoice`、`submitPlayerAction`、`handleAction`、`start`、`nextRound`）不再附带自动循环；
- 公开宿主推进接口 `advanceAutomaticActions()`：在延迟模式下每次最多执行一个自动动作（AI 响应、AI 回合或真人立直摸切三者之一），完成后原子提交并触发只读事件广播；在非延迟模式下保持全量执行；
- 单个动作内部由于规则引擎自然触发的摸牌、响应窗口展开等仍为不可分割的原子状态转移。

### 2. `core/src/main/kotlin/com/majiang/application/LocalTableRuntime.kt` (新增)
- 独立于客户端的表现层调度器，作为本地对局运行时的驱动核心；
- 公开 `val coordinator: ApplicationCoordinator` 与 `val endpoint: LocalPlayerEndpoint`；
- 构造器默认 `coordinator = ApplicationCoordinator(deferAutomaticActions = true)`，并显式断言 `require(coordinator.deferAutomaticActions)`；
- 公开 `update(delta: Float, presentationBusy: Boolean): ActionResult?` 与 `close()`：
  - **动画感知**：当 `presentationBusy == true` 时立即将空闲计时器复位为 `0f`，不推进也不累积欠账；
  - **稳态心跳**：仅在牌桌空闲且累积空闲时间至少达到 `0.15f` 秒时，才调用一次 `coordinator.advanceAutomaticActions()`；
  - **抗扰与单帧单次**：忽略 `<= 0f`、NaN、无限等异常 delta；单帧最多推进一次，触发后计时器归零，绝不使用 while 循环追赶帧率，大 delta 也只触发一次；
  - **生命周期**：`close()` 后设置原子标记，立即停止推进并关闭内部端点；
  - **线程与框架无关**：无后台计时线程，零 libGDX/GL 依赖；
  - **合规暂停**：普通真人选择、响应等待、自摸和合法立直暗杠由 coordinator 原样保持暂停，绝不越权自动代选或 Pass。

### 3. `core/src/main/kotlin/com/majiang/MahjongGame.kt` (修改)
- 应用组合根持有本地宿主调度器：`var localRuntime: LocalTableRuntime? = null; private set`；
- 新增 `createLocalEndpoint(): LocalPlayerEndpoint`：销毁可能存在的旧 runtime，实例化全新的 `LocalTableRuntime`，注册并返回其绑定的 `endpoint`；
- 重构 `launchGame(endpoint: PlayerEndpoint = createLocalEndpoint())`：默认路径强制使用本地调度器；显式传入外部 endpoint 时关闭旧 runtime 且不创建调度器；
- 覆写 `render()`：当且仅当当前场景是匹配 `localRuntime.endpoint` 的 `GameScreen` 时，先以 `Gdx.graphics.deltaTime` 与 `screen.isDiscardPlaybackBusy` 驱动 `localRuntime.update`，再执行 `super.render()`；
- 在替换场景、重新开桌及 `dispose()` 时安全关闭旧 runtime。

### 4. `core/src/main/kotlin/com/majiang/screen/GameScreen.kt` (修改)
- 便利构造器 `GameScreen(game, skinOverride)` 改为委托 `endpoint = game.createLocalEndpoint()`，使得新调度器与 GameScreen 一致绑定；
- 重构 `checkDiscardsDiff(events: List<GameEvent> = emptyList())`：
  - **副露调和先行**：从当前待播队列中移除已被吃碰杠取走的牌，若正在播放的动画已被副露取走立即停止动画；
  - **事件时序驱动**：遍历 `visibleEvents` 中的 `GameEvent.TileDiscarded`，严格按照事件在广播中的先后顺序排队；
  - **牌张物理身份去重**：通过 `(seat, tile)` 唯一物理身份（依托 `Tile` 的 `copyIndex` 守恒）在 `discardQueue`、`currentDiscardAnim` 及 `lastDiscards` 中去重，防止事件与快照重叠入队；
  - **副露防复活**：核对 `tile in currRiver`，已被副露取走的弃牌绝不入队；
  - **牌河差异兜底**：处理纯快照更新（无事件列表或兼容调用），保证与既有快照模式完全兼容；
- 订阅回调中传递 `update.visibleEvents` 至 `checkDiscardsDiff(update.visibleEvents)`；
- 完整保留结算清空、打牌演出繁忙禁止玩家操作等既有表现层机制。

---

## 3. 设计契约与约束核对

| 契约项 | 规格要求 | 实现状态 | 说明 |
|---|---|---|---|
| **可选延迟模式** | `deferAutomaticActions: Boolean = false` | ✅ 完全满足 | 默认 false，无头兼容层零侵入；启用时顶层命令不循环推进 |
| **单步推进接口** | `advanceAutomaticActions` 单次最多一步 | ✅ 完全满足 | 延迟模式每次至多推进 1 个自动动作并广播，复用原有判定逻辑 |
| **本地运行时** | `LocalTableRuntime` 独立调度 | ✅ 完全满足 | 负责 0.15s 空闲驱动，presentationBusy 复位计时，大 delta 不追赶 |
| **组合根宿主** | `MahjongGame` 驱动与生命周期 | ✅ 完全满足 | 覆写 render 先 update 再 super.render，dispose 与换桌正常 close |
| **端点权限边界** | GameScreen 不持有 coordinator | ✅ 完全满足 | GameScreen 仅消费/提交 PlayerEndpoint，无权推进 authority |
| **弃牌动画时序** | 按 visibleEvents 顺序入队 | ✅ 完全满足 | 消除 seat 循环分组排序，物理 Tile 去重，被副露牌不入队 |
| **规则与领域隔离** | 不修改 Round/MatchSession | ✅ 完全满足 | 领域状态机与规则校验组件 100% 保持原样，不引入客户端时钟 |

---

## 4. 提交物与 Codex 验收指引

Codex 可基于本实现执行以下设计规格用例验证：

1. **单次显式弃牌无自动动作越权**：
   - 配置 0 号为真人，1/2/3 为 AI，使用 `deferAutomaticActions = true` 的 Coordinator；
   - 0 号出牌后，`submitPlayerAction` 仅返回并广播 0 号自身的弃牌事件，AI 不在当前调用中连击出牌；
2. **宿主单步 Tick 节奏与时序验证**：
   - 调用 `LocalTableRuntime.update`，空闲至少 0.15 秒后触发一次 `advanceAutomaticActions`，AI 1 出牌；
   - 在随后多次 tick 中，依次产生 AI 2 出牌、AI 3 出牌，最终摸牌给真人 0 号并保持暂停等待输入；
   - 各弃牌事件严格按真实时间戳推进，无重复物理牌；
3. **动画繁忙阻断与无欠账验证**：
   - 传入 `presentationBusy = true`，无论推进多长时间，牌山剩余与版本号严格不变；
   - 动画结束后重置空闲，单次传入大 delta（如 10.0f）仅触发一次推进，不产生循环追赶；
   - 调用 `runtime.close()` 后，再次调用 `update` 不推进；
4. **真人关键分支等待验证**：
   - 真人立直自摸、合法暗杠时，自动摸切判定暂停，不代选和了或杠；
   - 他家打出枪杠/荣和牌时，真人 Ron / Pass 窗口保持等待，不被 AI 自动跳过；
5. **批次更新时序与副露调和验证**：
   - 模拟单次 `SessionUpdate` 包含跨多家多巡的 `TileDiscarded` 事件；
   - 验证 `discardQueue` 严格按事件出现先后入队，而非 0..3 分组；
   - 若某张被弃牌在同批次或后续被鸣牌吃碰取走，该牌从待播队列中移除且不重新在牌河落地。
