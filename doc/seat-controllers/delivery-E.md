# 任务 E 交付报告：逐座位控制者与统一动作执行 (Seat Controllers & Unified Action Execution)

**日期**：2026-09-30  
**开发者**：Antigravity (Task E Production Developer)  
**审查者 / 测试与设计**：Codex  
**工作区**：`D:/majiang`  

---

## 1. 目标与交付范围

本次任务（Task E）在领域状态层（`com.majiang.state`）完整实现了逐座位控制者体系与统一动作执行/调度模型，支持 0～4 名任意真人玩家（本地与远端），消除了旧有 AI/真人分立路径与递归调度风险：

- **逐座位控制者模型**：定义 `SeatController`（`LocalHuman`、`RemoteHuman`、`Computer`、`Takeover`），由权威状态机严格管理与隔离；
- **统一入口与权限校验**：`submitPlayerAction` 绑定可信会话座位与控制者类型；系统动作、越座提交与非法类型均被安全拒绝；
- **原子互斥与重入防护**：基于 `AtomicBoolean` 提供最外层命令守卫，覆盖外部输入、配置变更、调度执行与事件发布，跨线程或监听器重入立即拒绝（`ACTION_NOT_AVAILABLE`），杜绝死锁与阻塞；
- **顺序自动调度循环**：消除递归 AI 触发与栈溢出风险，采用单向 `while` 循环统一推进未响应 AI、当前回合 AI 摸打及立直自动摸切；
- **安全降级与视图隔离**：AI 仅接收深拷贝 `PlayerView` 与只读合法动作，决策严格绑定调度座位，异常/非法/null 策略均提供单次安全兜底（Pass、合法摸切或首个合法弃牌）；
- **响应窗口规范化与振听防护**：`ResponseWindow.recordResponse` 防御拒绝非法动作与越座动作，对无序组合牌映射到合法列表中的规范动作，非法动作拒绝绝不误触发振听；
- **严格保持物理守恒**：保留 `StateInvariants.assertValid` 审计，绝不吞掉物理守恒破坏异常。

> **边界声明**：本次交付为纯领域与状态机层实现，不实现网络传输、房间广播、请求编号/重连协议、多方下一局准备，亦不改变麻将规则、千术绝技或 UI 布局。`RemoteHuman` 属于领域控制类型，不代表网络联机已上线。

---

## 2. 核心源码变更与新增文件

### 2.1 新增文件：`com.majiang.state.SeatController.kt`

```kotlin
package com.majiang.state

import com.majiang.ai.AIStrategy
import com.majiang.ai.BasicAIStrategy

sealed interface SeatController {
    data class LocalHuman(val autoRiichiDiscard: Boolean = true) : SeatController
    data class RemoteHuman(val autoRiichiDiscard: Boolean = true) : SeatController
    data class Computer(val strategy: AIStrategy = BasicAIStrategy()) : SeatController
    data class Takeover(val strategy: AIStrategy = BasicAIStrategy()) : SeatController
}
```

- **语义说明**：
  - `LocalHuman`：本地真人，等待本地交互；
  - `RemoteHuman`：远端真人，保持等待外部网络会话提交；
  - `Computer`：自治 AI，持独立 `AIStrategy`；
  - `Takeover`：临时托管状态，持独立 `AIStrategy`；
  - `autoRiichiDiscard`：座位级自动摸切偏好，与引擎全局 `autoRiichiDiscard` 均为 `true` 时方执行自动摸切。

### 2.2 扩展文件：`com.majiang.state.ResponseWindow.kt`

- **规范动作映射**：新增 `findMatchingEligibleAction(player, action): GameAction?`，支持吃、碰、大明杠无序牌组在候选合法动作列表中的规范匹配；
- **防御性响应记录**：`recordResponse` 仅在 `player in eligibleSeats`、未曾响应、且通过候选合法动作匹配时记录规范动作；
- **振听安全**：非法/越座动作被拒绝且零副作用，不改变 `responses`，不触发舍牌振听或同巡/立直振听。

### 2.3 重构文件：`com.majiang.state.GameStateMachine.kt`

#### A. 状态与配置管理
- `val hasControllerConfiguration: Boolean`：只读属性，指示是否显式调用过任一 `initializeMatch`；未调用前为 `false`；
- `fun controllerFor(seat: Int): SeatController`：查询座位（0..3）控制器，越界抛出 `IllegalArgumentException`；
- `val humanPlayerIndex: Int`：只读兼容查询。当且仅当恰有 1 名真人（`LocalHuman` 或 `RemoteHuman`）时返回该座位索引；0 名或多名真人时返回 `-1`；
- `fun initializeMatch(controllers: List<SeatController>)`：四座位完整配置入口，防御性复制，严格长度校验（size != 4 抛 `IllegalArgumentException`），全部校验通过后再写入，无部分修改；
- `fun initializeMatch(humanIndex: Int = 0, aiStrategies: Map<Int, AIPlayer> = emptyMap())`：兼容旧签名，支持 `humanIndex in -1..3`，校验座位范围与 `AIPlayer.playerIndex` 严格匹配，转换为四座位配置并保留指定策略。

#### B. 统一命令与动作提交入口
- `fun submitPlayerAction(seat: Int, action: GameAction): ActionResult`：
  - 会话端绑定座位的外部动作提交通道；
  - 拒绝系统动作（`StartStacking`, `DealComplete` -> `UNSUPPORTED_ACTION`）；
  - 拒绝越座提交（`getActionPlayer(action) != seat` -> `INVALID_PLAYER`）；
  - 校验控制者权限（非 `LocalHuman`/`RemoteHuman` -> `ACTION_NOT_AVAILABLE`）；
  - 校验前置条件并经候选深拷贝事务提交，随后运行顺序自动循环推进后置事件。
- `fun setSeatController(seat: Int, controller: SeatController): ActionResult`：
  - 原子热切换指定座位的控制器；
  - 非法座位返回 `INVALID_PLAYER`；正在处理返回 `ACTION_NOT_AVAILABLE`；对局已终了返回 `MATCH_FINISHED`；
  - 不重设积分/立直/振听/牌局，不重置已提交响应；恢复真人不撤销已执行动作；
  - 切换后顺次推进当前可自动执行的动作，并返回产生的全部事件。
- `fun advanceAutomaticActions(): ActionResult`：
  - 无头驱动与恢复/调度入口，以相同互斥守卫运行自动循环；
  - 对局终了返回 `MATCH_FINISHED`。
- `fun handleAction(action: GameAction): ActionResult`：
  - 保持为可信系统/无头测试仿真通道，可为任意合法座位注入动作；
  - 采用相同互斥守卫、事务试探与后置自动循环。

#### C. 统一顺序自动循环与防死锁设计
- 最外层使用 `AtomicBoolean.compareAndSet(false, true)` 提供不可重入守卫，监听器并发/递归即刻拒绝；
- 内部动作执行函数 `executeActionInternal` 仅执行单步事务并发布事件，**不再递归触发 AI**；
- 顶层由 `runAutomaticActionLoop` 统一接管后续调度：
  1. **响应窗口阶段**：遍历 `eligibleSeats` 中尚未响应的 `Computer`/`Takeover` 座位，顺序收集策略响应（失败时以 `Pass` 安全兜底）；若仍有未响应真人座位，循环终止并等待真人输入，AI 先应答绝不提前裁决；收齐后自动裁决结算；
  2. **自家回合阶段**：
     - 若当前出牌者为真人：检查全局与座位级 `autoRiichiDiscard`；若有自摸或暗杠则必须暂停等待真人，否则自动摸切；
     - 若当前出牌者为 AI/托管：在 `hasControllerConfiguration == true` 且 `autoPlayAi == true` 时调用策略；若策略返回 null、非法动作或抛普通异常，仅尝试一次，并使用合法摸切（或首个合法弃牌）安全兜底；若无安全动作可用则停止推进；
  3. **终了阶段**：遇到 `ROUND_END` 或 `MATCH_END` 即刻退出循环。

---

## 3. 对接契约（供 Task S 使用）

在 `session/LocalGameSession.kt` 中：
- 构造函数末尾新增可选 `controllers: List<SeatController>? = null`；
- `start()`：若传入 `controllers` 则调用 `engine.initializeMatch(controllers)`；若未传入但 `engine.hasControllerConfiguration == true` 则保留已有配置；若未配置则调用 `engine.initializeMatch(viewerSeat)` 启动默认单人局；
- `submit()`：将原 `engine.handleAction(action)` 替换为 `engine.submitPlayerAction(viewerSeat, action)`；
- `legalActions()` / `view().legalActions`：当当前会话对应座位为 `Computer` 或 `Takeover` 时返回空列表（避免前端误展示操作面板），真人座位返回引擎合法动作；
- 本任务未修改 `LocalGameSession.kt`，Task S 可独立遵循此契约进行更新。

---

## 4. 验收要点对照表

| 验收项 | 实现机制与保障 |
| :--- | :--- |
| **两名及四名真人不被 AI 抢操作** | 响应窗口收录 AI 动作后若仍有资格真人未响应，窗口保持开启，自动循环停止，绝不提前结算 |
| **混合响应待齐与头跳** | 统一在所有有资格座位收齐后调用 `ResponseResolver.resolveWinner`，严格按放铳者顺位头跳裁决 |
| **控制者配置隔离与非法配置** | 严格 4 项校验，防御性复制，参数校验失败抛 `IllegalArgumentException`，无部分状态污染 |
| **托管切入、恢复与已响应不撤销** | `setSeatController` 原子替换配置并触发自动推进；恢复真人不撤销已执行/提交的动作 |
| **独立立直自动摸切偏好与自摸/杠暂停** | `autoRiichiDiscard && seatPref` 双重判定；检测到 `DeclareTsumo` 或 `DeclareKan` 必定中断循环并等待输入 |
| **全座位非法动作统一校验** | 无论 AI 还是真人，均进入 `validateActionPreconditions` 与候选事务校验，非法操作零副作用拒绝 |
| **AI 冒充他人、null 与异常安全兜底** | 严格校验 `action.player == seat`；策略异常隔离，合法窗口 Pass 兜底，自家回合摸切/首个弃牌兜底 |
| **响应记录拒绝非法且不误触发振听** | `recordResponse` 预先验证候选动作匹配；仅在合法记录且放弃和了时登记同巡/立直振听 |
| **跨线程并发与监听器重入** | `AtomicBoolean.compareAndSet` 原子互斥守卫，重入立即返回 `ACTION_NOT_AVAILABLE` |
| **无头测试与默认行为兼容** | 未 `initializeMatch` 前，座位 0 默认为真人，AI 保持静默（不主动摸打），支持 `handleAction` 逐步步进 |
