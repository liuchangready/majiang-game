# 任务 S 交付文档：本地会话逐座位控制者适配 (LocalGameSession)

日期：2026-09-30  
负责 Agent：Antigravity (Task S 生产开发)  
相关代码文件：[`com/majiang/session/LocalGameSession.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/session/LocalGameSession.kt)  
参考设计契约：`doc/seat-controllers/` 契约规范与任务划分

---

## 1. 交付目标与职责范围

根据多座位控制者与统一动作执行设计，任务 S 负责客户端/表现层会话门面 [`LocalGameSession`](file:///D:/majiang/core/src/main/kotlin/com/majiang/session/LocalGameSession.kt) 的兼容性升级与控制者适配：
- 严格遵循跨 Agent 分工边界：仅修改 `session/LocalGameSession.kt`，不触碰 `state/` 领域核心及测试套件；
- 支持 0~4 个真人的多座位控制者配置注入；
- 将玩家动作提交入口从原 `engine.handleAction` 切换为带座位权限校验的 `engine.submitPlayerAction`；
- 根据当前座位控制者类型（`LocalHuman` / `RemoteHuman` vs `Computer` / `Takeover`）动态过滤合法动作与视图展现；
- 完整保留既有的深拷贝脱敏、事件过滤快照、异常隔离与关闭生命周期。

---

## 2. 具体变更实现清单

### 2.1 构造函数扩展与防御性校验
在 [`LocalGameSession`](file:///D:/majiang/core/src/main/kotlin/com/majiang/session/LocalGameSession.kt) 构造函数末尾新增可选参数 `controllers: List<SeatController>? = null`，保留原参数顺序与默认值，确保源码与二进制向后兼容：
```kotlin
class LocalGameSession(
    override val viewerSeat: Int = 0,
    private val engine: GameStateMachine = GameStateMachine(GameState(), GameEventBus()),
    private val eventBus: GameEventBus = engine.eventBus,
    controllers: List<SeatController>? = null
) : GameSession
```
- **防御性复制与长度校验**：构造期若传入 `controllers`，立即执行 `ArrayList(list)` 独立深拷贝并包装为不可变集合 `Collections.unmodifiableList`，且通过 `require(copy.size == 4)` 强制校验必须恰好包含 4 个座位的控制者配置，否则立即抛出 `IllegalArgumentException`，防止外部并发修改穿透。

### 2.2 对局启动流 (`start`) 适配
在 `start()` 方法中按优先级确定引擎对局初始化策略：
1. **显式提供 `controllers`**：调用 `engine.initializeMatch(configuredControllers)` 初始化四座位控制者；
2. **未提供但引擎已配置 (`engine.hasControllerConfiguration == true`)**：保留引擎已有的控制者配置与 AI 策略，不覆写；
3. **未提供且引擎未配置**：回退调用传统 `engine.initializeMatch(viewerSeat)`，保持单机 1 真人 3 AI 的默认对局兼容。

### 2.3 玩家动作提交 (`submit`) 权限切换
- 保留 `isClosed` 状态校验与 `StartStacking` / `DealComplete` 系统动作拒绝（`UNSUPPORTED_ACTION`）；
- 保留本地观察者座位绑定校验：提交动作的玩家编号必须严格与 `viewerSeat` 一致，非本座位直接拒绝（`INVALID_PLAYER`）；
- **核心切换**：将底层执行委托由 `engine.handleAction(action)` 切换至 `engine.submitPlayerAction(viewerSeat, action)`。引擎侧权威校验该座位当前控制者是否为真人（`LocalHuman` 或 `RemoteHuman`），非真人提交将由引擎返回 `ACTION_NOT_AVAILABLE`；
- 动作成功执行后，依既有流程对事件列表执行 `SessionUpdate.filterVisibleEvents` 过滤、`snapshotGameEvent` 深拷贝快照，并派发订阅通知。

### 2.4 合法动作与视图投影 (`legalActions` / `view`)
- 内部引入 `currentController()` 解析当前有效控制者：
  - 若游戏尚未启动且构造函数提供了 `configuredControllers`，以配置列表对应座位为准；
  - 游戏启动后或未显式配置时，委托 `engine.controllerFor(viewerSeat)` 动态获取当前控制者状态（支持对局中途的托管 `Takeover` 与恢复真人切换）；
- **动作列表过滤**：
  - 当控制者为 `SeatController.Computer` 或 `SeatController.Takeover` 时，`legalActions()` 始终返回 `emptyList()`，防止客户端或托管状态下错误展示交互操作按钮；
  - 当控制者为 `SeatController.LocalHuman` 或 `SeatController.RemoteHuman` 时，返回引擎计算的 `engine.legalActions(viewerSeat)`；
- **视图手牌脱敏**：`view()` 依据 `legalActions()` 的计算结果调用 `PlayerView.fromGameState(engine.state, viewerSeat, actions)`，严格保证他家手牌暗牌脱敏。

---

## 3. 边界与未实施功能声明

1. **未修改 `GameSession` 接口**：`setSeatController` / `advanceAutomaticActions` 等对局管理与驱动方法保留在领域层 `GameStateMachine` 权威入口，不在本地会话接口增加管理方法。
2. **纯逻辑适配**：本次交付不包含网络传输层、WebSocket 房间广播、多端断线重连协议或 UI 渲染代码；`RemoteHuman` 仅作为领域类型就绪。
3. **所有权隔离**：本任务未修改 `state/SeatController.kt`、`state/GameStateMachine.kt` 或任何测试套件，完全交由 Task E 与 Codex 构建检视。
