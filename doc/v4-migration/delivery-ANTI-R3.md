# R3 客户端同步与自动重连 - 交付报告 (Anti)

**交付时间**：2026-10-05  
**交付者**：Antigravity (Anti) - 生产代码开发者  
**验收者**：OpenAI Codex - 架构决策、独立测试、构建与验收负责人  
**依据文档**：`AGENTS.md`、`doc/v4-migration/PLAN.md`、`doc/v4-migration/CONTRACT.md`、`doc/v4-migration/TASK-ANTI-R3.md`、`doc/v4-migration/ACCEPTANCE-R3.md`、`doc/v4-migration/FEEDBACK-R3.md`

---

## 1. 交付声明与测试说明

> [!IMPORTANT]
> **测试执行声明**：
> 本任务 R3 的所有生产代码演进与模块单元测试均由 Anti **仅使用内置文件读写/编辑工具**完成落盘。
> **Anti 未运行任何测试、构建或 Gradle 命令**（未执行 `./gradlew test`、终端或构建脚本），测试执行、真机联调与构建验收工作全权委托并由 Codex 独立运行。

---

## 2. 最终产品使用的唯一客户端运行时

本任务完成将 `client/src/main/kotlin/com/majiang/client/api/ServerConnectionSession.kt` 演进为 **V4 最终产品使用的唯一客户端运行时 (Unified V4 Runtime)**：

- **统一双门面实现**：`ServerConnectionSession` 同时完整实现 `LobbySession`（大厅期接口）与 `GameSession`（对局期接口），包装底层任意 `ServerConnection`（单机进程内绑定或远端 UDP 绑定）；
- **全客户端模型出入参**：严格遵循 V4 契约，入参仅接受客户端模型（`ActionChoice`、`ShufflePlan`、`MatchConfig`），出参通过 `ProtocolViewMapper` 映射为不可变视图模型（`GameSnapshotView`、`DecisionView`、`Receipt`），客户端无任何 `engine` 依赖与直接引用；
- **旧链共存约定**：旧版 `GameClient` 与 `ClientSynchronizer` 遵照任务要求保留共存，等待 R5 展示层接线完成后由 R6 统一清理，本阶段未提前删除仍被引用的展示实现。

---

## 3. 生命周期状态机与状态流转

客户端定义了不可变枚举 `ConnectionState`（位于 `client.api` 包）：
```
[CONNECTING] ──(绑定确立/重连成功)──> [CONNECTED]
     ▲                                   │
     │                           (意外网络掉线)
     │                                   ▼
     └───(指数退避触发)──────────── [RECONNECTING]
                                         │
        (显式close / 顶号 / 房间关闭 / 停服 / 重连耗尽)
                                         ▼
                                     [CLOSED]
```

### 状态定义与行为契约

1. **`CONNECTING`**：
   - **进入条件**：调用 `createRoom`、`joinRoom`、`resumeSession` 或断线重连发起新连接握手；
   - **行为**：置位 `isBindingInProgress = true`，此期间由新连接接收到的所有 PUSH 消息进入 `earlyUpdateBuffer` 有序缓冲，不与旧缓存进行错误对账或丢弃。
2. **`CONNECTED`**：
   - **进入条件**：收到服务端的 `RoomBinding`（建房/加房成功或 `resumeSession` 恢复成功）；
   - **行为**：将新快照确立为基线，按序排出并合并早期缓冲中的推送，对外通知 `ConnectionState.CONNECTED`；此时对局指令（`submit`、`decision`、`snapshot` 等）开放提交。
3. **`RECONNECTING`**：
   - **进入条件**：连接意外断开（非永久原因），且会话持有 `connectionFactory` 与有效 `currentBinding`；
   - **行为**：
     - 使用 CAS 守卫 (`stateRef.compareAndSet(CONNECTED, RECONNECTING)`)，**防止网络重复掉线通知触发并发重连循环**；
     - 保持本地 UI 视图快照与房间身份凭据（`roomId`、`token`、`cursor`）不变，UI 画面不闪烁清空；
     - 通过可注入的 `UdpScheduler` 启动指数退避定时器（500ms、1000ms、2000ms ... 8000ms 上限）；
     - 重新建立底层连接并发送 `ResumeRequest`，握手期间推送缓冲；恢复成功后恢复监听并刷新快照；
     - **控制权不自动抢回**：重连后维持托管状态，真人必须显式调用 `claimControl()` 才能收回控制权，权威端托管动作与计时不回滚。
4. **`CLOSED`**：
   - **进入条件**：
     - 调用方显式调用 `session.close()`；
     - 收到永久终结原因通知：被新连接顶号 (`DISPLACED_BY_NEW_CONNECTION`)、房间关闭 (`ROOM_CLOSED`)、服务端关机 (`SERVER_SHUTDOWN`)、被踢 (`KICKED`)、令牌失效 (`INVALID_TOKEN`) 或房间不存在 (`ROOM_NOT_FOUND`)；
     - 无重连工厂（单机进程内连接）掉线；
     - 重连达到最大重试上限或遇到不可恢复错误。
   - **一致终态处理 (`terminateSession`)**：
     - 原子标记 `isClosed = true`，记录稳定 `disconnectReason`；
     - 取消所有活动的重连定时器；
     - **全量在途 Future 以异常终结**：遍历追踪的 `outstandingFutures`，全部通过 `dispatch` 派发 `IOException("Session closed: $reason")`，彻底杜绝调用方无限挂起；
     - 安全释放 `isPending` 状态；
     - 注销底层推送与断线监听，关闭底层连接；
     - 清理乱序缓冲与早期推送缓冲；
     - 若会话拥有独立调度器，优雅关闭调度器；
     - 派发状态变更通知至 `stateListeners` 与 `disconnectListeners`。

### 显式退出房间 (`leaveRoom`) 语义

- 调用 `leaveRoom()` 时，**立即清空 `currentBinding` 与本地对局缓存**；
- 若离房请求在途期间发生网络异常掉线，因 `currentBinding` 已清空，**坚决不触发重连抢占旧座**；
- 显式退出房间成功后，当前连接在大厅继续保持 `CONNECTED`，可继续调用 `listRooms()`、`createRoom()` 或 `joinRoom()`，不会将退出旧房误当关闭整个客户端连接。

---

## 4. 关键可靠性与同步自愈机制实现

### 4.1 早期推送缓冲与对账 (Early Push Buffering)
- 在建房、加房与重连握手前即行绑定推送监听器，先于服务端回包捕获推送；
- 握手期间的推送消息暂存 `earlyUpdateBuffer`；
- 收到 `RoomBinding` 后，按 `fromCursor` 升序排列，逐一与绑定初始快照对账推进；若早期推送版本较绑定响应更新（例如握手响应晚于新动作推送），最终视图以对账后的最新快照为准，绝不倒退回滚。

### 4.2 明确全量标记贯穿 (Explicit `isFullSnapshot` Flag)
- 协议层与线格式：`ProtocolPlayerUpdate` 增加 `isFullSnapshot: Boolean = false` 字段；
- `ApiMessage`：在 UDP PUSH 信封的编码与解码中保留并透传 `isFullSnapshot`；
- `UdpClientBinding`：在接收解析更新帧时完整保留 `isFullSnapshot` 并派发；
- `GameUpdateView` 与 `ProtocolViewMapper`：将全量标记准确投影到客户端视图模型；
- **同步规则**：
  - 只有明确携带 `isFullSnapshot = true` 且同 `syncEpoch` 的更新才作为窗口外全量替换，重置基线并清理已落后重排缓冲；
  - **旧/异 epoch PUSH 永远无法替换当前绑定**：非当前 `syncEpoch` 的 PUSH 更新一律丢弃，不同代次只能由有效绑定/恢复响应确立；
  - 普通普通缺口（未标全量）坚决不跳过，严格触发恢复流程。

### 4.3 缺口自愈与去重重试 (GAP Recovery & Reorder Buffer)
- 收到超前游标更新（`fromCursor > current.cursor`）时，更新暂存入 `reorderBuffer`（有序红黑树），**拦截暴露未就绪的未来决策**；
- 通过 `isResuming.compareAndSet(false, true)` 原子置位触发 `resume(current.cursor)`；
- 在途期间收到的其他缺口包暂存缓冲，不重复发起冗余恢复请求（**在途严格去重**）；
- 恢复成功后：安装恢复快照，按序排出重排缓冲区内连续的后续更新；排出后若发现缓冲区后部仍存在缺口，**级联触发下一次缺口修复**；
- 恢复失败或异常：在同步 `catch` 与异步 `whenComplete` 中**均安全释放 `isResuming.set(false)`**，确保后续到包可再次重试，杜绝永久假死。

### 4.4 待确认状态守卫 (`isPending`)
- `submit(ActionChoice)` 与 `submitShufflePlan(ShufflePlan)` 在调用时通过 `pendingState.compareAndSet(false, true)` 检验并置位；
- 若已有动作在途，立刻返回带 `status = REJECTED`、`reason = "ACTION_IN_FLIGHT"` 的回执，防止连击与并发重入；
- 无论网络成功、服务端拒绝、传输异常、会话关闭还是调用方手动 `future.cancel(true)`，均通过 `whenComplete` 钩子安全释放 `isPending = false`。

### 4.5 可注入线程派发与锁安全 (`dispatch`)
- 所有外部监听器（`onUpdate`、`onDisconnect`、`onStateChange`）与 `CompletableFuture` 消费回调均通过注入的 `dispatch` 派发；
- **严格移出临界区**：持有 `syncLock` 或 `stateLock` 期间绝不调用 `dispatch` 或执行用户代码，杜绝死锁风险；
- **代次与关闭防护**：派发任务执行时校验 `!closed && epoch == currentEpoch.get()`，关闭或换房后的陈旧回调自动失效；
- **异常隔离**：单监听器抛出异常通过独立 `try-catch` 捕获，不中断其他监听器的正常执行。

### 4.6 传输层与会话调度器所有权解耦
- 为 `UdpClient.connect` 增加可选参数 `closeSchedulerOnShutdown: Boolean = false`；
- 当 `ServerConnectionSession.connectUdp` 使用会话级共享调度器时，底层单一 UDP 连接断开仅延迟关闭其自身 Socket，**不关闭会话共享调度器**，确保指数退避定时器持续有效；
- 会话生命周期终结（`close` / `terminateSession`）时，若会话拥有该调度器，再行彻底关闭。

---

## 5. 修改与新增文件清单

| 文件路径 | 状态 | 职责说明 |
|---|---|---|
| `client/src/main/kotlin/com/majiang/client/api/ConnectionState.kt` | 新建 | 定义客户端标准连接状态枚举 (`CONNECTING`, `CONNECTED`, `RECONNECTING`, `CLOSED`) |
| `client/src/main/kotlin/com/majiang/client/api/LobbySession.kt` | 修改 | 大厅门面补齐 `connectionState`、`closed`、`disconnectReason`、`onStateChange` 与 `resumeSession` 规范契约 |
| `client/src/main/kotlin/com/majiang/client/api/GameSession.kt` | 修改 | 对局门面补齐 `connectionState`、`isPending`、`currentSnapshot`、`onStateChange` 规范契约 |
| `protocol/src/main/kotlin/com/majiang/transport/api/ProtocolModels.kt` | 修改 | `ProtocolPlayerUpdate` 增加 `isFullSnapshot: Boolean = false` 全量标记字段 |
| `protocol/src/main/kotlin/com/majiang/transport/api/ApiMessage.kt` | 修改 | UDP PUSH 报文编码与解码中保留并透传 `isFullSnapshot` |
| `client/src/main/kotlin/com/majiang/client/transport/udp/UdpClientBinding.kt` | 修改 | UDP 客户端绑定层在接收解析更新帧时完整透传 `isFullSnapshot` |
| `client/src/main/kotlin/com/majiang/client/transport/udp/UdpClient.kt` | 修改 | 增加 `closeSchedulerOnShutdown` 参数，解耦单连接 Socket 释放与会话共享调度器生命周期 |
| `client/src/main/kotlin/com/majiang/client/api/ViewModels.kt` | 修改 | `GameUpdateView` 增加 `isFullSnapshot: Boolean = false` |
| `client/src/main/kotlin/com/majiang/client/api/ProtocolViewMapper.kt` | 修改 | 更新与快照模型映射中正确传递 `isFullSnapshot` |
| `client/src/main/kotlin/com/majiang/client/api/ServerConnectionSession.kt` | 修改/重构 | 演进为唯一 V4 统一运行时，实现不可变视图缓存、乱序重排自愈、握手缓冲、pending 守卫、锁隔离派发、自动重连与优雅终态 |
| `client/src/test/kotlin/com/majiang/client/api/ServerConnectionSessionTest.kt` | 修改/扩展 | 新增覆盖 15 项以上单测场景：握手早包、缺口乱序、恢复去重重试、全量标记、旧 epoch 隔离、并发 pending、关闭在途终结、异常隔离、派发时序、指数退避、永久断网、离房在途不重连、重复掉线通知去重、取消释放 pending 等 |

---

## 6. 模块单元测试范围

在 `ServerConnectionSessionTest.kt` 中设计并实现了覆盖 R3 全部核心契约与边缘场景的单元测试：

1. `testLobbyFacadeOperations`: 验证大厅期建房、加房、恢复、离房与列表查询接口及绑定映射；
2. `testGameSessionFacadeOperations`: 验证对局期快照、决策、动作提交、码牌提交、控制权切换与监听注销；
3. `testEarlyPushBufferHandshake`: 验证建房握手延迟时，早到 PUSH 正确缓冲并在绑定后按序合流，不被低游标覆盖；
4. `testReorderBufferAndGapHealing`: 验证缺口包到达时拦截暴露未来决策，前序包补齐后按序排出；
5. `testGapRecoveryFailureAllowsRetry`: 验证恢复请求失败后安全释放标记，后续收包可正常发起重试；
6. `testFullReplacementPushEstablishesNewBaseline`: 验证携带 `isFullSnapshot = true` 的推送直接建立新基线并清除旧缓冲；
7. `testOldEpochPushCannotReplaceCurrentBindingEvenIfFull`: 验证旧代次与异代次推送（即使伪造 full）坚决不覆盖当前有效绑定；
8. `testCrossSessionAndEpochIsolation`: 验证异 `sessionId` 或旧代次普通推送被安全过滤；
9. `testPendingStateGuardsAgainstConcurrentActions`: 验证动作在途期间重复提交返回 `ACTION_IN_FLIGHT`，完成后恢复；
10. `testActionCancelledOrFailedReleasesPendingState`: 验证 Future 异常或调用方主动 `cancel` 均能安全释放 `isPending`；
11. `testCloseTerminatesPendingFutures`: 验证关闭会话时，所有底层挂起的外部 Future 均以 `IOException` 异常完成；
12. `testListenerExceptionDoesNotAffectOtherListeners`: 验证单监听器异常被隔离，不阻断后续监听器接收通知；
13. `testExplicitFullReplacementPushFlag`: 验证显式全量标记透传机制；
14. `testManualDispatcherDefersAndSuppressesCallbacks`: 验证自定义调度器按序排队派发，且会话关闭后待派发旧回调自动失效；
15. `testAutoReconnectWithExponentialBackoff`: 验证意外掉线时保留 UI 快照与凭据，假时钟调度器驱动指数退避并成功恢复绑定与订阅，不自动抢回控制权；
16. `testDuplicateDisconnectDoesNotTriggerConcurrentReconnectLoops`: 验证重复掉线通知在重连中状态下不会重复调度退避任务；
17. `testLeaveRoomInFlightPreventsReconnectOnDisconnect`: 验证离房在途时清空席位，底层随后掉线不触发重连抢座；
18. `testPermanentDisconnectDoesNotReconnect`: 验证 `DISPLACED_BY_NEW_CONNECTION` 顶号等永久原因不重连，直接进入 `CLOSED`；
19. `testInProcessConnectionDoesNotReconnect`: 验证单机进程内连接掉线不创建网络重连；
20. `testEventDeduplicationAndCollectionIsolation`: 验证跨局同内容事件不误去重，集合副本安全隔离。

---

## 7. 未解决事项 (Unresolved Items)

- **无未解决事项**：R3 范围内所有规范接口、同步重排算法、全量透传、pending 守卫、可注入派发、指数退避自动重连及模块单元测试已全部完成。
- 等待 Codex 执行独立构建、单元测试、`tools/architecture/v4-review/V4RuntimeAcceptanceTest.kt` 验收测试及全量测试套件。
