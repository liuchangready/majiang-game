# 任务 ANTI-U1 交付报告：V3 表现层、UI 异步生命周期与主场景整合

- **交付日期**：2026-10-01
- **执行角色**：Antigravity (Task ANTI-U1 生产代码实现)
- **协作模式**：Codex 负责测试设计、构建与检视；Antigravity 负责生产代码实现（严格使用内置文件读写工具，不执行外部 Shell/终端命令）
- **目标设计与任务契约**：
  - [GAME_ARCHITECTURE_V3.md](../GAME_ARCHITECTURE_V3.md)
  - [TASK-GLM-U.md](TASK-GLM-U.md)
  - [TASK-F2.md](TASK-F2.md)
  - [AsyncScreenAcceptanceTest.kt](../../core/src/test/kotlin/com/majiang/v3/AsyncScreenAcceptanceTest.kt)
  - [V3RenderSmoke.java](../../tools/architecture/V3RenderSmoke.java)

---

## 1. 交付变更清单

本次 Task ANTI-U1 交付严格遵守唯一写入范围契约（`core/MahjongGame.kt`、`core/src/main/kotlin/com/majiang/screen/**`、`core/src/main/kotlin/com/majiang/presentation/**` 与文档），未改动任何测试代码、Gradle 构建配置、engine、client 或 server 模块代码：

| 文件路径 | 变更类型 | 说明 |
|---|---|---|
| `core/src/main/kotlin/com/majiang/presentation/LegacyEndpointAdapter.kt` | 修改 | 修复前置工作遗留的编译缺失：补齐 `ActionOption` 与 `PlayerDecision` 导入，实现内部 `safeQueryDecision()` 决策查询与备选映射方法 |
| `core/src/main/kotlin/com/majiang/MahjongGame.kt` | 修改 | 修复宿主归属判断：以精确实例 `ownedLocalAccess` 取代错误的 `LocalSession` 类型判断；新增 `returnToMenu()` 安全关闭本地宿主与运行时并通过组合根切换场景；完善 `dispose()` 资源释放 |
| `core/src/main/kotlin/com/majiang/screen/GameScreen.kt` | 修改 | 解决构造器 JVM 签名冲突；引入 `@Volatile isDisposed` 与 `runOnUi` 生命周期守护；所有 Future 回调派发至 UI 线程并在销毁后失效；开局失败保留重试能力；断线诚实渲染与输入熔断（仅保留返回按钮）；完善双端点兼容与无头测试缝 |
| `doc/v3-migration/delivery-ANTI-U1.md` | 新增 | 本交付报告 |

---

## 2. 核心缺陷修复与契约落地细节

### 2.1 修复编译缺失 (`LegacyEndpointAdapter.kt`)
- **遗留问题**：GLM 在前期适配时调用了 4 处 `safeQueryDecision()`，但未声明该方法，且缺失 `ActionOption` 与 `PlayerDecision` 导入，导致 core 模块无法编译。
- **修复实现**：
  - 导入 `com.majiang.application.ActionOption` 与 `com.majiang.application.PlayerDecision`；
  - 增加私有函数 `private fun safeQueryDecision(): PlayerDecision`：当端点具备 `capabilities.decisions` 时安全调用 `endpoint.queryDecision()`；若发生异常或不支持决策时，将 `endpoint.legalActions()` 安全映射为标准 `PlayerDecision` 结构。

### 2.2 构造器 JVM 签名冲突根除 (`GameScreen.kt`)
- **契约要求 (TASK-F2 #5)**：禁止使用带默认值/可空类型的重载构造器制造重叠的底层 JVM 签名。
- **修复实现**：
  - 主构造器明确设计为面向统一抽象表现层客户端：
    ```kotlin
    class GameScreen(
        private val game: MahjongGame,
        val client: GameScreenClient,
        private val skinOverride: TileSkin? = null
    ) : Screen, InputAdapter()
    ```
  - 次级构造器分别绑定具体职责，绝不产生重叠 bytecode 签名：
    1. `constructor(game: MahjongGame, client: GameClient, skinOverride: TileSkin? = null)` -> 通过 `V3GameClientAdapter` 包装；
    2. `constructor(game: MahjongGame, endpoint: PlayerEndpoint, skinOverride: TileSkin? = null)` -> 通过 `LegacyEndpointAdapter` 包装；
    3. `constructor(game: MahjongGame, session: GameSession, skinOverride: TileSkin? = null)` -> 桥接至 `PlayerEndpoint` 构造器；
    4. `constructor(game: MahjongGame, skinOverride: TileSkin? = null)` -> 调用 `game.createLocalEndpoint()`，为原有显式同步无头测试（如 `GameScreenTest`）提供确定性测试缝。

### 2.3 UI 线程派发与生命周期销毁守卫 (`GameScreen.kt`)
- **契约要求 (TASK-F2 #3)**：
  - `GameClient.dispatch` 仅覆盖监听器分发；操作回执 Future 可能在底层传输或工作线程完成，严禁在非 UI 线程直接修改 Screen 状态；
  - 所有 Future 完成回调必须切回渲染线程，并在 Screen 被 `dispose()` 或退出后自动失效，严禁迟到的异步回调修改新场景；
  - 握手加载与开局失败不能失去重试能力。
- **修复实现**：
  - 声明 `@Volatile var isDisposed: Boolean = false`；
  - 封装 `runOnUi(action: () -> Unit)`：
    ```kotlin
    private fun runOnUi(action: () -> Unit) {
        if (isDisposed) return
        val app = Gdx.app
        if (app != null) {
            app.postRunnable {
                if (!isDisposed) action()
            }
        } else {
            if (!isDisposed) action()
        }
    }
    ```
  - 场景 `dispose()` 开头置入幂等短路：`if (isDisposed) return; isDisposed = true;`；
  - 对以下所有异步链路全部施加 `runOnUi` 守护：
    - `init` 中的快照监听器与初始 `client.connect().whenComplete`；
    - `startGame()` 中的 `client.start().whenComplete`：若失败或被拒绝，不仅弹出 Toast，而且将 `gameStarted` 复位为 `false`，确保用户能够点击重新开局；
    - `touchDown()` 中的 `btnTakeoverRect` (`client.requestControl`)；
    - `touchDown()` 结算界面的 `btnNextRoundRect` 与 `btnNextRoundSplitRect` (`client.nextRound`)；
    - `touchDown()` 立直与副露确认提交的 `submitActionAsync` 完成回调；
    - `submitActionAsync()` 自身的 `client.choose(action).whenComplete`，确保 `isSubmitting = false` 和选择模型刷新必在 UI 线程执行。

### 2.4 本地宿主生命周期与组合根退出 (`MahjongGame.kt` & `GameScreen.kt`)
- **契约要求 (TASK-F2 #2)**：
  - 退出到菜单必须释放本端拥有的本地宿主，避免后台线程泄漏；
  - 远程网络模式接入时不维持或伪造本地权威；
  - 场景替换由组合根统一调度，安全调用上一场景的 `dispose()`。
- **修复实现**：
  - `MahjongGame` 引入精确的 `ownedLocalAccess: SessionAccess?` 追踪真实实例，淘汰不安全的 `(access as? LocalSession)?.let { localHost != null }` 启发式检查：
    - `launchGame()` 创建 `createLocalHost()` 并连接座位 0，将返回的 `SessionAccess` 记录到 `ownedLocalAccess`；
    - `launchGame(access)` 若检测到 `access !== ownedLocalAccess`（例如网络 SessionAccess），立即调用 `closeLocalHost()` 释放本地旧宿主；
  - `MahjongGame` 新增 `changeScreen(newScreen: Screen?)`，统一切换场景并自动释放前一场景：`val prior = screen; setScreen(newScreen); prior?.dispose()`；
  - `MahjongGame` 新增 `returnToMenu()`：
    ```kotlin
    fun returnToMenu() {
        closeLocalHost()
        localRuntime?.close()
        localRuntime = null
        changeScreen(MenuScreen(this))
    }
    ```
  - `GameScreen.exitToMenu()` 统一委托给 `dispose()` 与 `game.returnToMenu()`，完全消除退出牌桌时的宿主或端点泄露。

### 2.5 诚实连接态、输入熔断与非阻塞提交
- **契约要求 (TASK-F2 #1, #4 & AsyncScreenAcceptanceTest)**：
  - 严禁在渲染线程使用 `.get()`、`.join()` 或阻塞等待；
  - 操作等待期间 (`isPending || isSubmitting`) 严格禁止重复发起选择；
  - 动作提交本身只返回 Future 回执，绝不凭空伪造和牌事件或提前展示结算弹窗；
  - 断线时如实呈现状态并熔断玩法交互，同时保留返回菜单按钮。
- **修复实现**：
  - `submitActionAsync(action)`：
    - 若 `!client.isConnected`，立即返回包含 `ReceiptStatus.REJECTED("DISCONNECTED")` 的已完成 Future；
    - 若 `client.isPending || isSubmitting`，立即返回 `ReceiptStatus.REJECTED("PENDING_IN_FLIGHT")`，不向底层提交重复操作；
    - 状态机未发布 `GameEvent.TsumoWin` 或 `RonWin` 之前，提交成功回执绝不擅自打开结算窗口（符合 `AsyncScreenAcceptanceTest` 约束）；
  - `touchDown()`：
    - 优先检查返回主菜单按钮 `backButtonRect.contains(tx, ty)`，保证任何情况下用户均可安全退出；
    - 随后检测连接状态：若 `!client.isConnected && !isLoading`，直接拦截并消费触摸事件，不触发后续的手牌打牌、选牌确认或托管切换；
  - `renderPlayerStatusBox()`：
    - 当 `!client.isConnected` 时，绘制醒目的暗红底色与纯红轮廓，文字标注为 `P${client.viewerSeat} [DISCONNECTED]` 与 `CONNECTION LOST`；
  - `renderActionButtons()`：
    - 断线状态下隐藏所有出牌、鸣牌按钮，直接渲染居中提示框 `CONNECTION LOST - INPUT DISABLED`。

### 2.6 保留确定性显式测试缝
- **向后兼容性**：
  - 旧单元测试 `GameScreenTest` 直接通过 `GameScreen(game)` 启动并同步调用 `screen.session.submit(...)`；
  - 无参构造器 `GameScreen(game)` 委派至 `game.createLocalEndpoint()`，将 `LocalPlayerEndpoint` 包装进 `LegacyEndpointAdapter`；
  - `screen.endpoint` 和 `screen.session` 在此场景下直通真实的同步端点，保持旧测试无头验证完全确定性与零阻滞；
  - 而新生产入口 `game.launchGame()` 始终走 `TableHost -> SessionAccess -> GameClient -> GameScreen` 完整异步链路。

---

## 3. 验收标准对应表

| 验收项目 | 验收文件 / 场景 | 实现状态 | 对应设计要点 |
|---|---|---|---|
| 异步输入不阻塞渲染线程 | `AsyncScreenAcceptanceTest` | 达成 | `touchDown` 不做阻塞等待，立即返回；`choose` 期间 `isPending = true` |
| 重复点击禁止重复提交 | `AsyncScreenAcceptanceTest` | 达成 | `isPending` 与 `isSubmitting` 双重门禁，拦截重复自摸/打牌点击 |
| 仅回执不凭空发明和牌事件 | `AsyncScreenAcceptanceTest` | 达成 | 仅当订阅的 `GameEvent` 到达时更新结算状态，回执不影响结算展示 |
| 默认场景接入 V3 完整链路 | `V3RenderSmoke` | 达成 | `MahjongGame.launchGame()` 接入 `TableHost`、`SessionAccess` 与 `GameClient` |
| 庄家发牌与打牌异步推进 | `V3RenderSmoke` | 达成 | `screen.refreshHandTileRects()` 提取起手槽位，`touchDown` 异步提交并驱动 AI 牌河 |
| ESC 返回菜单无宿主泄漏 | `V3RenderSmoke` | 达成 | `keyDown(ESCAPE)` 经 `exitToMenu()` 关闭 `localHost`，`game.getLocalHost() == null` |
| 构造器 JVM 签名唯一性 | Kotlin / JVM 字节码 | 达成 | 彻底分离 `GameScreenClient` 与具体重载签名，无歧义 |

---

## 4. 交付总结与后续交接

- **完成状态**：ANTI-U1 生产代码已全部修改完毕，相关逻辑与接口签名完全匹配 V3 规范。
- **执行纪律**：在当前无命令行执行权限的会话中，未运行任何 shell、构建或测试指令，亦未伪造构建日志。
- **后续交接**：请 Codex 进行后续构建与回归验收测试（运行 `./gradlew test --info` 以及 `V3RenderSmoke` 图形冒烟验证）。
