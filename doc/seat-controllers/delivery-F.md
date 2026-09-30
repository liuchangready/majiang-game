# 任务 F 交付报告：检视缺陷修复 (Fix Review Findings)

**日期**：2026-09-30  
**开发者**：Antigravity (Task F Production Developer)  
**审查者 / 测试与设计**：Codex  
**工作区**：`D:/majiang`  
**修改范围**：仅修改 `core/src/main/kotlin/com/majiang/state/GameStateMachine.kt`

---

## 1. 任务背景与修复概述

在完成任务 E 与任务 S 后，Codex 执行了 25 项新增验收测试（23 项通过，2 项失败）。针对暴露出的并发配置写入、策略浅层不可变别名逃逸、以及控制者显式配置语义不一致问题，本次任务严格按约定仅对生产代码 `GameStateMachine.kt` 进行针对性修复，未修改任何测试代码、构建脚本或非自身所有权的文件。

---

## 2. 缺陷修复明细

### 2.1 修复项 1：`initializeMatch(List)` 并发原子命令守卫与忙碌拦截

- **问题现象**：
  `initializeMatch(List<SeatController>)` 此前直接写入 `this.controllers` 和 `hasControllerConfiguration = true`，未获取 `isProcessingAction` 原子锁。当验收测试在多线程环境下（例如某个线程正阻塞在 AI 策略决策中）调用 `initializeMatch` 时，导致对局执行中途配置被篡改。
- **修复方案**：
  1. 保留入参合法性前置校验（列表大小非 4 抛 `IllegalArgumentException`），确保非法输入在发生任何状态变动或锁竞争前即刻被拒绝；
  2. 提交配置前调用 `isProcessingAction.compareAndSet(false, true)` 获取原子守卫；
  3. 若状态机处于忙碌状态（`compareAndSet` 返回 `false`），直接抛出 `IllegalStateException("GameStateMachine is busy executing an action")`，保证零状态写入（`controllers` 与 `hasControllerConfiguration` 均不被修改）；
  4. 获取守卫后使用 `try ... finally` 块，确保在 `finally` 中严格释放守卫（`isProcessingAction.set(false)`）；
  5. 旧签名重载 `initializeMatch(humanIndex: Int, aiStrategies: Map<Int, AIPlayer>)` 保持前置参数校验与转换，随后委托调用 `initializeMatch(converted)`，避免二次获取守卫（No double-acquire）。

### 2.2 修复项 2：AI 决策参数深拷贝与深层集合防篡改 (`view.legalActions`)

- **问题现象**：
  `decideAiResponseAction` 与 `decideAiTurnAction` 此前向 `strategy.decideAction(...)` 传入 `Collections.unmodifiableList(rawLegal)`。由于 `unmodifiableList` 仅做浅层包装，列表内部元素如 `DeclarePon.tiles`、`DeclareChi.meld.tiles` 等嵌套列表仍为可变引用，在响应窗口中直接别名（alias）了权威 `eligibleActions` 候选集合。恶意的或带有副作用的策略在决策中修改嵌套集合导致验收测试失败。
- **修复方案**：
  1. 在 `decideAiResponseAction` 中：构造 `val view = PlayerView.fromGameState(state, seat, eligible)` 后，改传入 `view.legalActions`：
     ```kotlin
     chosen = strategy.decideAction(view, view.legalActions)
     ```
  2. 在 `decideAiTurnAction` 中：构造 `val view = PlayerView.fromGameState(state, player, legal)` 后，同样改传入 `view.legalActions`：
     ```kotlin
     chosen = strategy.decideAction(view, view.legalActions)
     ```
  3. `PlayerView.create` 已经对 `DeclareChi.meld.tiles`、`DeclarePon.tiles`、`DeclareKan.tiles`、`StartStacking.stackingPlans`、`UseCheatTechnique.params` 进行了深层 `toList()` 拷贝并包装为 `Collections.unmodifiableList` / `unmodifiableMap`，彻底切断了策略输入与权威候选集合的引用共享；
  4. 策略决策返回后，状态机继续严格依据权威规则集合校验（`isValidAiResponseAction` 校验 `window.findMatchingEligibleAction`；`isValidAiTurnAction` 校验 `validateActionPreconditions`），杜绝信任策略自持列表。

### 2.3 修复项 3：澄清显式配置语义 (`setSeatController` 标记 `hasControllerConfiguration`)

- **问题现象**：
  此前仅 `initializeMatch` 会将 `hasControllerConfiguration` 置为 `true`。若在 `LocalGameSession.start()` 之前通过可信管理接口 `setSeatController` 配置了 `RemoteHuman`，`session.start()` 会误判 `engine.hasControllerConfiguration == false` 并触发默认 `initializeMatch(viewerSeat)` 覆盖已有设置；此外，在未初始化 fixture 上通过 `setSeatController` 切换为 `Takeover` 时，由于 `hasControllerConfiguration == false`，自动调度循环无法激活自主摸打。
- **修复方案**：
  1. 在 `setSeatController(seat, controller)` 成功更新控制器后，同步设置 `hasControllerConfiguration = true`：
     ```kotlin
     val newControllers = controllers.toMutableList()
     newControllers[seat] = controller
     controllers = newControllers
     hasControllerConfiguration = true
     ```
  2. 保持构造函数未配置兼容性：刚构造未调用任何配置 API 时，`hasControllerConfiguration` 仍为 `false`，未配置的 AI fixture 不会自发摸打；
  3. 一旦调用 `initializeMatch` 或成功调用 `setSeatController`，即认定为显式配置，`Takeover` 与 `LocalGameSession.start()` 均能正确识别并保留配置。

---

## 3. 修改文件对照

- **`core/src/main/kotlin/com/majiang/state/GameStateMachine.kt`**：
  - 更新 `hasControllerConfiguration` 文档注释；
  - `initializeMatch(controllers: List<SeatController>)` 增加 `isProcessingAction` 守卫获取、`IllegalStateException` 拦截及 `finally` 释放；
  - `setSeatController` 成功更新后设置 `hasControllerConfiguration = true`；
  - `decideAiResponseAction` 与 `decideAiTurnAction` 改向策略传入 `view.legalActions`；
  - 清理未使用之浅层包装引用。

---

## 4. 交付验证说明

由于无头会话中禁止执行 shell/RunCommand 命令，请 Codex 运行测试套件与全量验收验证：
- 25 项控制者验收测试验证（确认此前失败的并发 initializeMatch 竞争与嵌套列表篡改两项测试恢复通过）；
- 桌面及 Android debug 构建验证；
- 兼容性回归测试验证。
