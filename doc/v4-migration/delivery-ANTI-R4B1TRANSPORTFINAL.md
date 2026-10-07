# 交付报告：R4B1TRANSPORTFINAL 客户端 UDP 通知保序与并发边界收尾

## 1. 任务概述与文件修改清单

本任务针对 [`doc/v4-migration/TASK-ANTI-R4B1TRANSPORTFINAL.md`](file:///D:/majiang/doc/v4-migration/TASK-ANTI-R4B1TRANSPORTFINAL.md) 与 [`doc/v4-migration/FEEDBACK-R4B1TRANSPORTFINAL.md`](file:///D:/majiang/doc/v4-migration/FEEDBACK-R4B1TRANSPORTFINAL.md) 反馈，彻底修复了客户端 UDP 在多线程并发通知、锁外监听器保序、代次边界隔离以及慢监听器场景下的关键并发缺陷。

### 修改文件范围（独占范围）
- [`client/src/main/kotlin/com/majiang/client/transport/udp/UdpClientBinding.kt`](file:///D:/majiang/client/src/main/kotlin/com/majiang/client/transport/udp/UdpClientBinding.kt)
- [`client/src/test/kotlin/com/majiang/client/transport/udp/UdpClientBindingTest.kt`](file:///D:/majiang/client/src/test/kotlin/com/majiang/client/transport/udp/UdpClientBindingTest.kt)

### 严格遵守的边界与约束
- 未修改 `ServerConnectionSession`、`client.api` 契约或 mapper。
- 未修改冻结的 `protocol` 消息契约。
- 未修改 `server` 模块实现或任何 reviewer 测试（如 `tools/architecture/v4-review/**`）。
- 未修改任何项目构建配置，未执行终端构建或测试命令。

---

## 2. 核心架构重构与技术方案

### 2.1 基于 Work-in-Progress (WIP) 的有界串行排水器 (Serial Drainer)
- **问题分析**：原先将更新移出锁外直接循环触发外部 listener，在多线程环境（如接收线程与公开 `getRoomInfo` 完成线程并发，或多个更新并发）下，若线程 A 处理 v2 时被 slow listener 阻塞，线程 B 处理 v3 并率先向后续 listener 分发，会导致观察者看到倒序的 `[3, 2]`。
- **解决方案**：
  1. 引入等待无关的队列排水模式：`drainWip = AtomicInteger(0)` 与 `roomDispatchQueue = ConcurrentLinkedQueue<RoomDispatchItem>()`。
  2. 提交更新时封装携带接受时快照的 `RoomDispatchItem(epoch, roomId, version, roomInfo)`。
  3. `enqueueAndDrainRoomUpdate`：若 `drainWip.getAndIncrement() != 0`，提交线程立即无阻塞返回，**绝不在每个通知上新建线程**。
  4. 唯有胜出的 drainer 线程在锁外依次串行排水，确保全局只有一个线程在遍历 listener 列表，从数学和架构上根除 `[3, 2]` 乱序交错。
  5. 引入 `listenerMaxVersions: ConcurrentHashMap`，确保每个有效观察者在当前 epoch 下观察到的版本单调递增，且最新视图最终可达。

### 2.2 原子校验连接身份代次与缓存提交 (Atomic Identity & Cache Mutation)
- **问题分析**：之前在锁外校验 `boundRoomId`，而在锁内只比较 `version`，导致网络包在离开旧房间、进入新房间的间隙可穿透写入新房间缓存；且 `create/join/resume/leave` 的身份修改与缓存清空不在同一临界区。
- **解决方案**：
  1. 引入全局内部状态锁 `stateLock = Any()`。
  2. 在 `handleInboundPush` 与 `getRoomInfo` 的响应映射中，**在 `stateLock` 临界区内部原子校验** `closed`、`boundRoomId`、`bindingEpoch` 与 `version`：
     - 若属于外来/旧房间或已关闭，立即拒绝，不更新缓存，不给非法数据续命，不回发 ACK。
     - 仅当完全匹配时才在锁内原子推进 `lastRoomInfoVersion`、`latestRoomInfo` 并生成携带当前 `(epoch, roomId)` 的 `RoomDispatchItem`。
  3. 在 `createRoom`、`joinRoom`、`resumeSession`、`leaveRoom` 以及 `close` 中，将身份修改（`connectionId`、`boundRoomId`、`boundSeat`、`resumeToken`、`bindingEpoch++`）与缓存及分发队列清空（`lastRoomInfoVersion = 0L`、`latestRoomInfo = null`、`earlyRoomUpdateBuffer.clear()`、`roomDispatchQueue.clear()`、`listenerMaxVersions.clear()`）**全部收拢到同一 `stateLock` 原子临界区**。
  4. 彻底杜绝旧房间在途包穿透写入新房间缓存的任何竞态窗口。

### 2.3 早期缓冲与慢监听器隔离 (Non-blocking Slow Listeners)
- **慢监听器不阻塞生命周期**：
  - `close()` 与 `leaveRoom()` 只在 `stateLock` 内原子修改标志（`closed = true` 或 `bindingEpoch++`），耗时微秒级，**绝不等待外部 listener 完成**。
  - drain 循环在每次分发前后均检测 `closed || bindingEpoch != nextItem.epoch || boundRoomId != nextItem.roomId`，在慢 listener 返回后立即断路退出并丢弃旧代次积压。
- **早期缓冲与 live updates 统一保序**：
  - 在 `onRoomUpdate` 注册时，将 `earlyRoomUpdateBuffer` 内属于当前代次的早期更新转移至 `roomDispatchQueue` 并调用 `drainRoomUpdates()`，保证早期缓冲回放与新到达推送严格串行、同一契约、不发生倒序。

---

## 3. 新增高并发与多线程交错回归测试

在 [`client/src/test/kotlin/com/majiang/client/transport/udp/UdpClientBindingTest.kt`](file:///D:/majiang/client/src/test/kotlin/com/majiang/client/transport/udp/UdpClientBindingTest.kt) 中补充了以下真实多线程交错用例（全部采用有界 `CountDownLatch` 与超时 `await`）：

1. **`testConcurrentUpdateSubmissionWhileFirstListenerBlockedNeverDeliversOutOfOrderToSecondListener`**：
   - 线程 1 提交 v2，第一 listener 收到 v2 后在 `CountDownLatch` 上阻塞；
   - 线程 2 在第一 listener 阻塞期间并发提交 v3；
   - 释放第一 listener 后，断言第二 listener **绝未收到 [3, 2]**，观察到的序列严格单调递增，且最终可达 v3。
2. **`testInFlightOldRoomUpdateAcrossLeaveAndNewBindDoesNotPolluteCacheOrNotifyNewRoom`**：
   - 客户端离开 ROOM1 并加入 ROOM2（分配新 connectionId 67890）；
   - 迟到的旧房间 ROOM1 推送（带有高版本 version = 99L）在换房后到达；
   - 断言原子身份守卫拦截旧包，不回发 ACK，ROOM2 缓存版本保持 0L，ROOM2 监听器未收到任何旧房消息，且随后 ROOM2 的合法低版本更新（version = 1L）顺利被接收。
3. **`testSlowListenerDoesNotBlockCloseOrLeaveFromAnotherThread`**：
   - 监听器在回调中模拟慢阻塞；
   - 另一线程调用 `binding.close()`，断言 `close()` 在 1000ms 内瞬间完成，`closed` 为 true，未被慢 listener 阻断。
4. **`testSlowListenerDoesNotBlockLeaveFromAnotherThread`**：
   - 监听器在回调中模拟慢阻塞；
   - 另一线程调用 `binding.leaveRoom()`，断言请求瞬间发起，并在收到模拟响应后立即完成离房，连接 ID 清零，房间清空。

---

## 4. 验证与测试声明

- **测试执行状态**：根据本阶段协作规范与用户指示，所有单元测试与集成验收均由 Codex 独立构建并运行，**本开发者（Anti）未直接执行 Gradle 构建或测试**。
- **未决事项**：客户端 transport UDP 的房间更新保序、原子代次隔离与慢监听器非阻塞支持已全部就绪，无遗留未决项。不向 B2/R5/R6 阶段推进。
