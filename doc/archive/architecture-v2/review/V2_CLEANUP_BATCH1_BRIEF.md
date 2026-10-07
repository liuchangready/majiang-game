# V2 兼容接口清理 · 批 1 简报（core 侧退场，anti 执行）

> 总计划：doc/architecture-v2/review/ 下 V2 清理三批方案（已用户批准）。本批只动 core 模块 + 新建 1 个测试辅助文件，**不动 engine 任何文件**。
> 验收方：主会话。完成后由验收方跑 `./gradlew test`（基线 518 项 0 失败）。

## 目标

core 模块彻底脱离 V2 兼容面：17 个测试迁移到 V3 构造器，GameScreen 删 3 个 V2 构造器与兼容属性，MahjongGame 删 @Deprecated 入口与 LocalTableRuntime 驱动，删除 LegacyEndpointAdapter 与 V3EndpointCompat 两个文件。

**本批不删除** engine 的 LocalGameSession/GameSession/PlayerEndpoint/LocalPlayerEndpoint/LocalTableRuntime（批 2 处理）。**必须保留** engine 的 SessionUpdate/PlayerView（V3 在用）。

## 第一步：新建共享测试辅助（唯一新文件）

文件：`core/src/test/kotlin/com/majiang/testing/FakeScreenClient.kt`（core 测试模块内共享，engine testFixtures 不能引用 core 类）。这是 FakeGameSession 的 V3 等价物——view-only 假 GameScreenClient：

```kotlin
package com.majiang.testing

import com.majiang.access.ActionReceipt
import com.majiang.access.PlayerSnapshot
import com.majiang.access.ReceiptStatus
import com.majiang.application.PlayerDecision
import com.majiang.event.GameEvent
import com.majiang.presentation.GameScreenClient
import com.majiang.session.PlayerView
import com.majiang.state.ActionResult  // 不需要则去掉
import com.majiang.state.GameAction
import java.util.concurrent.CompletableFuture

/** View-only UI double（替代旧 FakeGameSession）。GameScreen 只见到快照与回执，不见引擎。 */
class FakeScreenClient(initial: PlayerView) : GameScreenClient {
    private var cursorSeq: Long = 0L
    override var snapshot: PlayerSnapshot = initial.toSnapshot()
    val submitted = mutableListOf<GameAction>()
    var started = false
    var closed = false
    var nextRoundCalls = 0
    private val listeners = mutableListOf<(PlayerSnapshot, List<GameEvent>) -> Unit>()

    override val viewerSeat: Int get() = snapshot.viewerSeat
    override val isPending = false
    override val isConnected = true
    override val isClosed: Boolean get() = closed
    override val isTakeover = false

    override fun connect() = CompletableFuture.completedFuture(snapshot)
    override fun start(): CompletableFuture<ActionReceipt> {
        started = true
        return applied("start")
    }
    override fun nextRound(): CompletableFuture<ActionReceipt> {
        nextRoundCalls++
        return applied("next")
    }
    override fun choose(action: GameAction): CompletableFuture<ActionReceipt> {
        if (action !in snapshot.view.legalActions) return rejected(action)
        submitted.add(action)
        return applied("choose")
    }
    override fun requestControl(takeover: Boolean) = applied("control")
    override fun addListener(listener: (PlayerSnapshot, List<GameEvent>) -> Unit): AutoCloseable {
        listeners.add(listener)
        return AutoCloseable { listeners.remove(listener) }
    }
    override fun close() { closed = true; listeners.clear() }

    /** 等价旧 FakeGameSession.update(view, events)：换视图并广播 */
    fun update(view: PlayerView, events: List<GameEvent> = emptyList()) {
        snapshot = view.toSnapshot(events)
        notify(events)
    }
    /** 直接推送完整快照（settlement/preparation 等字段需要时用） */
    fun publish(snap: PlayerSnapshot) { snapshot = snap; notify(emptyList()) }

    private fun notify(events: List<GameEvent>) {
        listeners.toList().forEach { it(snapshot, events) }
    }
    private fun applied(id: String) =
        CompletableFuture.completedFuture(ActionReceipt(id, ReceiptStatus.APPLIED))
    private fun rejected(action: GameAction) =
        CompletableFuture.completedFuture(ActionReceipt("choose:${action::class.simpleName}", ReceiptStatus.REJECTED, "ACTION_NOT_AVAILABLE"))
}

fun PlayerView.toSnapshot(events: List<GameEvent> = emptyList()): PlayerSnapshot = PlayerSnapshot(
    sessionId = "fake-session", roundId = "fake-round", syncEpoch = "fake-epoch",
    cursor = 0L, sessionVersion = 0L, roundVersion = 0L,
    view = this, decision = PlayerDecision("fake-round", "fake-decision", 0L, emptyList())
)
```

注意：上式 `cursor` 固定 0 已被 EndTenpaiScreenAcceptanceTest 的既有假实现证明可行；若某测试出现"推送未生效"，把 cursorSeq 自增后传入。`PlayerSnapshot` 构造约束：sessionId/syncEpoch 非空、cursor/sessionVersion ≥0（PlayerSnapshot.kt:86-97）。`ActionReceipt` 构造签名以 access 包实际为准（先读该文件再写）。

## 第二步：迁移 17 个测试文件

### A. view-only 型（原 FakeGameSession，10 文件）

改写模式（断言全部保留，只换搭建）：

```kotlin
// 旧
val fake = FakeGameSession(view)
val screen = GameScreen(MahjongGame(), fake)
fake.update(newView, listOf(event))
fake.submitted.single()
// 新
val fake = FakeScreenClient(view)
val screen = GameScreen(MahjongGame(), fake)
fake.update(newView, listOf(event))
fake.submitted.single()
```

- `object : GameSession by fake { ... }` 拒绝型委托 → `object : GameScreenClient by fake { override fun choose(...) = ... }`（接口委托语法相同）
- `fake.started` → `fake.started`；`session.submit(...)` 返回 ActionResult 断言 → 改断言 `fake.submitted` 或 client.choose 的回执状态（断言语义保持：原来断言 Rejected 的，现在断言回执 REJECTED 或 submitted 不含该动作）
- 文件：GameScreenSessionContractTest、DiscardAnimationIntegrationTest、DiscardInputGateAcceptanceTest、GameplayLoopRegressionTest、FuritenScreenContractTest(FakeGameSession 部分)、MeldDisplayContractTest(Fake 部分)、OpponentMeldDisplayAcceptanceTest(Fake 部分)、MeldChoiceScreenContractTest(Fake 部分)、RiichiChoiceScreenContractTest(Fake 部分)、RiichiDiscardOrderAcceptanceTest、SettlementDetailPresentationAcceptanceTest、ClientSelectionAcceptanceTest

### B. 真引擎型（原 LocalGameSession 同步驱动，6 文件）

这些测试用 TestTableBuilder 建真实牌局 + ApplicationCoordinator 驱动屏幕。两种迁移路径，按用例语义选：

**路径 1（优先，改动最小）**：coordinator + FakeScreenClient 手动同步——测试自己持有 coordinator，动作经 `coordinator.submitPlayerAction(...)` 提交，随后 `fake.update(coordinator.queryView(seat), events)` 推给屏幕。原测试经 LocalGameSession 包装做的就是这个（session.submit + 订阅推送），现在测试显式做。

**路径 2**：完整 V3 真实链路——照抄 ScreenRecoveryAcceptanceTest.kt:27-48：
```kotlin
val host = TableHost(initialState = s, clock = MonotonicClock { 0L },
    controllers = List(4) { SeatController.RemoteHuman(false) })
host.use { h ->
    val client = GameClient(h.connect(seat)).also { it.connect().join() }
    val screen = GameScreen(MahjongGame(), V3GameClientAdapter(client) { it() })
    // flushUi()：Gdx.app.postRunnable + future.get，见 ScreenRecoveryAcceptanceTest.kt:35-39
}
```
- 文件：FuritenScreenContractTest、MeldDisplayContractTest、OpponentMeldDisplayAcceptanceTest、MeldChoiceScreenContractTest、RiichiChoiceScreenContractTest、RiichiScreenIntegrationTest
- 对照样板：OpponentMeldDisplayAcceptanceTest.kt 145-162(V2) vs 164-177(V3) 同断言双链路，逐用例照着改

### C. endpoint 型（2 文件）

- ClientSelectionAcceptanceTest：`PlayerEndpoint by FakeGameSession` 委托 → FakeScreenClient 直用（同 A 模式）
- RiichiPacingAcceptanceTest（**本批最微妙，逐用例处置**）：
  - 用例 1 `legacy eager mode...` 与用例 2 `paced submission and each host step...`：纯 coordinator，**零改动**
  - 用例 3 `local runtime waits for playback...`、用例 6 `runtime never discards for an ordinary human...`：主体是 LocalTableRuntime 心跳语义（批 2 删类）；TableHost 等价覆盖已存在于 engine 的 HostRhythmAcceptanceTest（间隔推进不追账/截止保护/暂停冻结）→ **这两个用例随批 2 类删除退场，本批先不动**（仍引用 LocalTableRuntime，等批 2 一并处理）
  - 用例 4 `paced riichi self draw...`、用例 5 `paced automatic discard retains another human ron window`：改用 `c.advanceAutomaticActions()` / `c.queryView()/legalActions()` 直达 coordinator（它们实质测 coordinator 的暂停保护，runtime 只是驱动壳）——去掉 runtime，直接断言 coordinator 行为，断言不变
  - 用例 7 `real endpoint and screen play riichi laps one discard at a time...`：**必须迁移**（唯一验证屏幕动画门控 isDiscardPlaybackBusy 对宿主反馈的端到端用例）。迁移为：fixture 改建 TableHost（clock 用可变假时钟，参考 HostRhythmAcceptanceTest.kt:14 的 `Clock` + 推进方式），`GameClient(h.connect(0))` + `V3GameClientAdapter(client){ it() }` 建 screen；循环里 `runtime.update(1f, screen.isDiscardPlaybackBusy)` → `host.tick(screen.isDiscardPlaybackBusy)`（配合假时钟推进到决策可执行），动画断言（currentDiscardAnim/discardQueue/updateDiscardAnimation）全部保留

### D. 无参构造器型（3 文件）

GameScreenTest、GameScreenSkinSmokeTest、MenuScreenTest：`GameScreen(game)` → `GameScreen(MahjongGame(), FakeScreenClient(<所需初始视图>))`；`screen.session.view()/submit()` → 直接用测试自持的 fake（`fake.snapshot.view`、`fake.submitted`）。需要初始视图时用 `PlayerView.fromGameState(GameState(), seat, emptyList())` 或照抄 EndTenpaiScreenAcceptanceTest 的工厂。

## 第三步：GameScreen.kt 手术

删除（行号为当前参考，以实际内容为准）：
- 3 个 V2 构造器：L84 endpoint 版、L91 session 版、L98 无参版（保留 L70 主构造器与 L77 GameClient 版）
- `endpoint` 属性（L105-106）与 `session` 属性（L109-110）
- `sessionSubscription` 死字段（L124，从未赋值只被置 null；连带 L3511/3532 的置 null 语句）
- `isLegacySeam`（L136-137）、`syncLegacyEndpointIfPresent()`（L143-154）及其 **16 处调用**（L906、2679、2698、2702、2706、2710、2714、2718、2722、2726、2730、3066、3120、3308、3352——以 grep 为准全部删除）
- `runOnUi`（L166-180）简化：删掉 legacy 分支，保留 Gdx.app.postRunnable 路径（若 Gdx.app 为 null 仍需直执行——保留 null 检查，只删 `!isLegacySeam` 条件）
- 清理不再使用的 import：com.majiang.session.GameSession、com.majiang.client.LocalPlayerEndpoint、com.majiang.client.PlayerEndpoint（engine 的）、以及 V2 专用其它符号
- 类注释 L63 中 "GameStateMachine" 字样可顺手改为 "旧 V2 状态机"
- **谨慎项**：L2679-2730 密集调用区多为各交互入口的"静默刷新"，删除后 view-only 测试的推送语义由 fake.update 显式覆盖；若某测试依赖"无事件也能刷视图"，改该测试用 fake.update 推送（不许加回轮询）

## 第四步：MahjongGame.kt 手术

- 删 `createLocalEndpoint()`（L140-146）、`launchGame(endpoint: PlayerEndpoint)`（L151-159）
- 删 `localRuntime` 字段（L52）及 render() 中的 `localRuntime?.update(...)`（L167-171）、close/dispose 中的清理（L187-188 附近）
- 清理 import（LocalTableRuntime、LocalPlayerEndpoint、PlayerEndpoint）
- 主路径 launchGame()/launchGame(access)/createLocalHost() **零改动**

## 第五步：删除 2 个文件

- core/src/main/kotlin/com/majiang/presentation/LegacyEndpointAdapter.kt
- core/src/main/kotlin/com/majiang/presentation/V3EndpointCompat.kt

## 硬性约束

- 断言只许换表达不许削弱：原断言 N 条，迁移后同语义断言 ≥N 条（验收方做断言计数比对）
- 禁止删除任何测试方法（本批一个都不删；RiichiPacing 用例 3/6 留给批 2）
- 不动 engine/ 任何文件；不动 core/src/main 除 GameScreen.kt、MahjongGame.kt、两个适配器文件之外的部分
- 不跑 shell 命令、不 git commit（验收方跑测试）
- Kotlin 语法精确：每改一个文件核对 import；GameScreenClient/ActionReceipt/PlayerSnapshot 以实际源码签名为准（先读后写）

## 完成标准（自检）

- `grep -rn "LegacyEndpointAdapter\|V3EndpointCompat\|FakeGameSession\|LocalGameSession\|LocalPlayerEndpoint\|createLocalEndpoint" core/src` 除 RiichiPacingAcceptanceTest 的 LocalTableRuntime 用例外零命中
- GameScreen.kt 仅剩 2 个构造器（主 + GameClient 版）
- 报告：逐文件一句话改动摘要 + 新建的 FakeScreenClient 位置 + RiichiPacing 逐用例处置说明
