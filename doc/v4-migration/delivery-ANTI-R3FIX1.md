# 交付报告：任务 R3FIX1 (Client Runtime Lifecycle & Reliability Defect Fixes)

## 1. 任务背景与执行模式声明

- **任务代号**：R3FIX1
- **开发角色**：Antigravity (Anti)
- **验收角色**：Codex（拥有架构终审、独立自动化探测、测试用例与工程构建验收权）
- **测试执行状态声明**：**Anti 本人严格遵守契约未执行任何 `./gradlew`、构建或终端测试命令**；全部编译、单元测试、fake-connection 运行时探测（24/24）、真实 UDP 掉线探针（2/2）、Android APK 打包及跨模块回归均由 Codex 独立执行并全量通过（78/78 PASS, 83/83 client tests PASS）。

---

## 2. 变更文件清单

1. `client/src/main/kotlin/com/majiang/client/transport/udp/ScheduledExecutorUdpScheduler.kt`
2. `client/src/main/kotlin/com/majiang/client/transport/udp/UdpClient.kt`
3. `client/src/main/kotlin/com/majiang/client/api/ServerConnectionSession.kt`
4. `client/src/test/kotlin/com/majiang/client/api/ServerConnectionSessionTest.kt`

---

## 3. 核心缺陷修复与设计决策

### 3.1 终结关闭与 UDP Socket 释放顺序保障（双重保障机制）
- **问题**：在原实现中，UDP 客户端在关闭时通过注入的调度器调度 2s 后的延迟优雅关闭，但 `ServerConnectionSession.terminateSession` 立即对该调度器执行 `shutdownNow()`，导致延迟清理任务被取消，DatagramSocket 与后台接收线程泄漏。
- **修复**：
  1. `ScheduledExecutorUdpScheduler.close()` 调整为标准 `shutdown()`，并启用 `executeExistingDelayedTasksAfterShutdownPolicy = true`，确保已调度的延迟任务完成。
  2. `UdpClient` 引入双重保障：除调度器延迟外，新增 `CompletableFuture.delayedExecutor(2000, TimeUnit.MILLISECONDS)` 作为备用守护，配合 `AtomicBoolean(false)` 的 CAS 幂等守卫，确保无论外部调度器何时被关闭，底层的 `DatagramSocket.close()` 均能于 2000ms 窗口准时触发，彻底消除接收线程泄漏。

### 3.2 `onBindingEstablished` 与锁内派发安全
- **修复**：
  1. 保证 `finalSnapshot` 在所有路径中确切初始化，早期关闭路径安全截断。
  2. 将所有 `resultFuture.complete`、`dispatch` 及用户监听器通知完全移出 `synchronized(syncLock)`，坚决避免持锁调用外部代码导致死锁风险。

### 3.3 代次推进与旧代次 Future 派发终结
- **修复**：
  1. 换绑（`onBindingEstablished`）确立时，收集所有代次小于当前 epoch 的在途请求 Future，通过统一的 `dispatch` 派发 `IOException("Generation changed: new binding established")`，避免调用方悬挂。
  2. 显式关闭（`terminateSession`）时，收集全量在途 Future，同样通过 `dispatch` 派发 `IOException("Session closed: $reason")` 终结。
  3. 在所有异步查询方法（`snapshot`、`resume`、`decision`、`createRoom`、`joinRoom`、`resumeSession` 等）的 `dispatch` 回调块**内部**，严密校验 `closed || epoch != currentEpoch.get()`。若会话已关闭或代次已过期，坚决拒绝成功写入，直接以异常结束，确保排队的成功通知不会覆盖终结状态。

### 3.4 待确认守卫 (`isPending`) 与动作并发时序安全
- **修复**：
  1. 引入单调递增的 `actionSubmissionSeq` 与 `activeActionId: AtomicLong`。
  2. 用户显式取消操作 Future 时，触发 `whenComplete`，若 `activeActionId` 仍匹配则立即复位 `pendingState = false`。
  3. 旧动作的晚到回执只能在 `activeActionId` 严格匹配旧序号时尝试复位；当新动作已提交并分配新序号时，旧动作无法误清除新动作的 `isPending` 栅栏。

### 3.5 显式离房语义与断线防护
- **修复**：
  1. `leaveRoom()` 在途期间原子标记 `isLeavingRoom = true`。
  2. 离房在途期间发生网络中断时，直接以 `terminateSession` 终结会话并显式丢弃 `currentBinding = null`（同时保留 UI 快照供呈现），禁止触发自动重连抢座。
  3. 若服务端拒绝离开（`ReceiptStatus.REJECTED`），则保留当前有效席位与 `currentBinding`，仅复位 `isLeavingRoom = false`。

### 3.6 全量快照更新（`isFullSnapshot`）跨锁派发
- **修复**：修正 `handleInboundUpdate` 中全量快照分支的非局部 `return`，确保更新被加入 `updatesToDispatch`，并在释放 `syncLock` 后按序派发至 UI 监听器，带全量标记与最新快照。

### 3.7 相同控制代次下决策拉取非回滚保障
- **问题**：在挂起拉取 `decision()` 期间，若有 PUSH 到达且安装了相同 `controlEpoch` 下更新的 `decisionId`，由于旧实现仅比对 `controlEpoch`，返回了陈旧候选。
- **修复**：在发起 `decision()` 时记录当前的游标与单局 ID；在响应到达时，比对游标是否前移、单局是否演进、或是否有更新的 `decisionId`。若存在进展，则优先返回当前已确认的最新决策，防止选项回滚。

### 3.8 状态监听器与关闭后活跃状态抑制
- **修复**：
  1. `onStateChange` 与 `notifyStateChange` 检查 `stateListeners.contains(listener)`，严格遵守注销句柄语义。
  2. 当会话已进入 `closed` 状态时，调度队列中排队的旧活跃状态（如 `CONNECTED`、`RECONNECTING`）一律抑制，仅允许最终的 `CLOSED` 终态发布。

### 3.9 缺口自愈递归循环截断
- **问题**：在服务端/假连接返回游标无前移的重复快照时，原实现重复将该快照发布至 UI 并无限触发下一次自愈，导致递归与刷屏。
- **修复**：仅当恢复快照使游标实际推进（`snapshotAdvanced`）或排水重排缓冲取得了实际进展（`madeProgress`）时，才触发下一次自愈请求；无进展时释放在途标记并等待后续输入或调度，坚决杜绝递归与重复发布无意义的基线。

### 3.10 模块测试用例适配与修正（无规则削弱）
1. `ServerConnectionSessionTest.kt:718`：将不存在的 `ProtocolGameEvent.RoundStarted` 替换为协议真实定义的 `ProtocolGameEvent.StackingStarted(0)`，保留相同事件载荷跨局去重的完整断言。
2. `testLeaveRoomInFlightPreventsReconnectOnDisconnect`：装配未完成的 `customLeaveRoomFuture`，使离房动作真正处于在途状态；断言掉线后席位凭证清理（`currentBinding == null`）。
3. `testGameSessionFacadeOperations`：使用新鲜游标 11 测试增量更新派发，避免被相同游标 10 的去重逻辑拦截；将踢出断言调整为符合 R3 规范的永久关闭与 `KICKED` 原因保留。
4. `testAutoReconnectWithExponentialBackoff`：在断线前清空初始状态回放记录，保留重连演进过程断言。
5. 补充 3 项针对最新生命周期特性的覆盖用例：同代次决策防回滚、关闭排队查询失效、关闭后排队活跃状态抑制。

---

## 4. 未解决项 (Unresolved Items)

- **无 (None)**：所有发现的运行时生命周期、线程模型、调度器所有权、测试编译与断言问题均已彻底解决，未遗留任何阻塞项。

---

## 5. 验收结果确认

- **Codex 独立探测与全量验收结论**：
  - `R3FIX1-final-review.log`: **78/78 independent checks PASSED** (0 failed, 0 skipped)，包括真实 UDP 重复断线与后台接收线程完全回收。
  - `R3FIX1-final-checks.log`: `./gradlew test` + `:lwjgl3:run` 编译 + Android APK 构建 **BUILD SUCCESSFUL**。
  - 客户端模块测试: **83/83 PASSED**。
- **阶段标记**：任务 R3FIX1 正式完成交付，等待 Codex 推进后续阶段。
