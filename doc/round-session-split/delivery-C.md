# 任务 C 交付报告：客户端接口、默认接入与兼容适配

日期：2026-09-30  
开发者：Antigravity (Task C)  
协作角色：Codex（设计、测试、构建、检视）

---

## 1. 完成项与源文件清单

### 新增与修改源文件
1. `core/src/main/kotlin/com/majiang/client/PlayerEndpoint.kt` (新增)
   - 定义统一客户端端点契约 `PlayerEndpoint : AutoCloseable`。
   - 定义能力说明结构 `EndpointCapabilities(local, remoteTransport, persistence, decisions)`。
   - 保留 `viewerSeat`, `view()`, `legalActions()`, `start()`, `submit(action)`, `nextRound()`, `subscribe(listener)`, `close()`。
   - 增加候选决策查询与提交接口：`queryDecision(): PlayerDecision`, `submitChoice(choice: ActionChoice): ActionResult`。
   - 为 FakeGameSession 等测试替身提供显式不支持的默认实现 (`throw UnsupportedOperationException` / `Rejected(UNSUPPORTED_ACTION)`)，能力中显式声明 `decisions = false`，不伪装可用。

2. `core/src/main/kotlin/com/majiang/client/LocalPlayerEndpoint.kt` (新增)
   - 本地进程内端点实现，构造参数固定为 `(viewerSeat: Int = 0, coordinator: ApplicationCoordinator = ApplicationCoordinator(), controllers: List<SeatController>? = null)`。
   - 协作 `ApplicationCoordinator`，通过 `coordinator.subscribe` 接收领域广播并向本地订阅者推送脱敏的 `SessionUpdate`。
   - 严格维护单端点隔离：端点关闭 (`close()`) 注销协调器监听与本地订阅，不影响协调器、其他已连接端点或比赛生命周期；单个监听器回调异常被安全隔离。
   - 控制权与参数安全：非真人座位（Computer / Takeover）或端点关闭时 `legalActions()` 与 `queryDecision().options` 均安全清空；系统动作 (`StartStacking` / `DealComplete`) 与跨座位提交在端点层严格拒绝。
   - 线程与重入安全：使用内部监视器锁保证 `view()` 与 `queryDecision()` 一致性读取，防止版本撕裂。

3. `core/src/main/kotlin/com/majiang/session/GameSession.kt` (修改)
   - 改造为兼容接口：`interface GameSession : PlayerEndpoint`。
   - 继承 `PlayerEndpoint` 全部方法与默认实现，使得已有测试替身（如 `FakeGameSession`）与既有调用者零侵入迁移。

4. `core/src/main/kotlin/com/majiang/session/LocalGameSession.kt` (修改)
   - 改造为轻量薄适配层：继承 `LocalPlayerEndpoint` 并实现 `GameSession`。
   - 保持原有构造参数兼容：`(viewerSeat: Int = 0, engine: GameStateMachine = GameStateMachine(GameState(), GameEventBus()), eventBus: GameEventBus = engine.eventBus, controllers: List<SeatController>? = null)`。
   - 将底层调用完整转发至 `engine.coordinator`。

5. `core/src/main/kotlin/com/majiang/screen/GameScreen.kt` (修改)
   - 面向 `PlayerEndpoint` 编程：主构造器持有 `val endpoint: PlayerEndpoint`，提供面向旧调用的 `GameSession` 兼容构造器及 `val session: GameSession` 属性。
   - 默认构造器 `GameScreen(game)` 默认使用 `LocalPlayerEndpoint()`，确保新 `Coordinator + MatchSession + Round` 路径成为默认生产路径。
   - 统一动作提交辅助 `submitAction(action: GameAction)`：当端点具备 `capabilities.decisions` 时，优先匹配当前 `queryDecision()` 选项，构造带有唯一 `requestId`、`roundId`、`decisionId`、`controlEpoch` 的 `ActionChoice` 提交 `endpoint.submitChoice(choice)`；若端点不支持决策或未找到选项，平滑回退至兼容的 `endpoint.submit(action)`。
   - 完整保留既有打牌动画流水线闸门 (`isDiscardPlaybackBusy`)、手牌包围盒命中、立直选牌与待牌预览模型 (`RiichiSelectionModel`)、副露牌组选择与高亮模型 (`MeldSelectionModel`)、HUD 振听预判以及分页结算详情展示。

---

## 2. 与设计契约的偏差

无。严格遵循本次并行设计契约：
- 未修改 `round`、`application`、`match`、`state/GameStateMachine.kt` 或测试文件；
- 保持了所有公开符号的签名、默认参数及可见性；
- 端点不包含任何本地自造的麻将规则或仲裁算法，完整依赖 `ApplicationCoordinator`。

---

## 3. 自检命令与构建状态

- **自检状态**：未测试（本会话处于无头受控环境，严格遵守“禁止运行 Shell、终端、构建与测试指令，禁止修改测试”的开发规则）。
- **静态核对**：
  - 对新增和修改的代码进行了语法、包路径、符号导入与类型系统核对；
  - 检查了对 Task S 提供的 `ApplicationCoordinator`, `PlayerDecision`, `ActionOption`, `ActionChoice` 类型的依赖；
  - 检查了对既有 `PlayerView`, `SessionUpdate`, `ActionResult`, `GameAction`, `RejectionReason`, `SeatController` 类型的适配与兼容。

---

## 4. 必须由其他任务接入或协同的接口

1. **Task S 提供的应用层组件**：
   - `com.majiang.application.ApplicationCoordinator`：提供 `coordinator.subscribe`, `coordinator.queryView`, `coordinator.legalActions`, `coordinator.queryDecision`, `coordinator.start`, `coordinator.nextRound`, `coordinator.submitPlayerAction`, `coordinator.submitChoice`, `coordinator.controllerFor`, `coordinator.hasControllerConfiguration`, `coordinator.initializeMatch`。
   - `com.majiang.application.PlayerDecision`、`ActionOption`、`ActionChoice` 数据类。
   - `GameStateMachine.coordinator` 兼容属性，供 `LocalGameSession` 转发使用。

2. **Codex 验收与测试协同**：
   - 验证验收用例 #5：同一 Coordinator 的两端点同步更新，端点关闭与回调异常不影响另一端点；
   - 验证验收用例 #6：旧局/旧窗口/旧控制代次动作拒绝；
   - 验证验收用例 #8：默认 `GameScreen` 走新路径，快照和候选不可改动权威数据；
   - 验证既有 200+ 项测试与平台构建无回归。

---

## 5. 未完成项和已知限制

1. **网络传输与远端服务**：本轮仅提供进程内端点 `LocalPlayerEndpoint`。基于网络协议的 `RemotePlayerEndpoint`、WebSocket/TCP 服务器与会话序列化不在此次实现范围。
2. **账户与持久化存储**：端点能力声明 `persistence = false`，不提供跨进程事务、数据库存档及断线恢复服务。
3. **测试覆盖**：所有新增契约测试与旧用例迁移均由 Codex 统一编写与执行。
