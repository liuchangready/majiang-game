# 交付报告：R4B1SERVERFINALFIX (Server UDP 收尾验收返修)

**执行人**：Antigravity (Anti)  
**日期**：2026-10-06  
**对应任务**：`doc/v4-migration/TASK-ANTI-R4B1SERVERFINALFIX.md`  
**反馈依据**：`doc/v4-migration/FEEDBACK-R4B1SERVERFINALFIX.md`  

---

## 1. 任务概述与解决清单

本交付针对 `doc/v4-migration/FEEDBACK-R4B1SERVERFINALFIX.md` 中 Codex 独立验收提出的全部 3 项测试失败与边界缺陷进行彻底返修。所有改动严格限制在 Server UDP 模块范围，未修改 client、protocol、reviewer 测试或 Gradle 构建文件。

---

## 2. 审查项逐项修复说明

### Item 1：补齐单测会话注册前提，保留全部 ACK、重发与版本断言
- **问题反馈**：
  `UdpRoomTest.testRoomUpdateRetransmitAndAckClearing:343 entry为null`；`testConcurrentInterleavedRoomUpdatesDoNotOverwriteNewerVersion:395 expected2 actualnull`。新实现要求会话在 `sessionsByConnectionId` 实际注册，旧两测试未完成前提迁移。
- **修复方案**：
  在 [`server/src/test/kotlin/com/majiang/server/udp/UdpRoomTest.kt`](file:///D:/majiang/server/src/test/kotlin/com/majiang/server/udp/UdpRoomTest.kt) 中：
  - 在 `testRoomUpdateRetransmitAndAckClearing` 中，`room.bindHost(801, address).get()` 后补调 `server.registerSession(session)`；
  - 在 `testConcurrentInterleavedRoomUpdatesDoNotOverwriteNewerVersion` 中，`room.bindHost(901, address).get()` 后补调 `server.registerSession(session)`；
  - 生产代码的会话存活校验及水印单调递增逻辑保持严格生效，未做任何放宽；
  - 完整保留既有的所有 ACK 匹配/非匹配在途清理、版本覆盖丢弃、以及多线程并发乱序收敛至最高版本的全部断言。

---

### Item 2：完善 KICK 定时重试清理与迟注册竞态防御，排空关闭定时任务
- **问题反馈**：
  `testRoomUpdateCancellationAndNoOrphanRetryOnClose:585 scheduler pending expected0 actual1`。`server.close()` 先向活跃会话下发 `SERVER_SHUTDOWN` KICK 触发了 `sendKick` 内部重试调度，但 `inFlightKicks.clear()` 仅清空映射未逐个取消任务句柄 `Cancellable`；外部调度器不能被关闭，停服后自身定时任务必须彻底取消并完成 future。
- **修复方案**：
  在 [`server/src/main/kotlin/com/majiang/server/udp/UdpTableServer.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/udp/UdpTableServer.kt) 中：
  1. 补齐 `InFlightKick` 的生命周期清理与 CAS 移除辅助方法：
     - `terminateKick(entry)`：原子取消 `retryCancellable` 并触发 `future.complete(Unit)`；
     - `purgeKick(entry)`：在匹配条目下从 `inFlightKicks` 移除并触发 `terminateKick`，供 `handleAck` 与重发耗尽分支使用；
     - `cancelAndPurgeKick(connectionId)`：原子移除指定连接在途 KICK 并触发 `terminateKick`。
  2. 会话销毁时清理 KICK：在 `terminateSession(session)` 内同步调用 `cancelAndPurgeKick(session.connectionId)`。
  3. 停服清理：在 `close()` 中，于停服有界等待（1000ms ACK linger）结束后，遍历所有未确认的 `inFlightKicks` 并逐个调用 `cancelAndPurgeKick(connId)`，确保 `FakeUdpScheduler`（及外部传入调度器）中的所有待发任务句柄均被原子 `.cancel()`，且外部调度器实例本身不被强行 shutdown。
  4. 迟注册与关闭竞态防护：
     - `sendKick` 入口若检测到 `isClosed.get()` 或 `session.isClosed` 立即完成 future 返回；
     - `sendKick` 内在调度到 `effectiveScheduler` 后复核 `isClosed.get() || future.isDone || inFlightKicks[session.connectionId] !== kickEntry`，若命中竞态立即取消新句柄；
     - 同步在 `scheduleRoomUpdateRetry` 中增加 `|| isClosed.get()` 检查防御迟注册。

---

### Item 3：真实覆盖 LEAVE_ROOM 服务端拒绝回执链路，消除伪造会话与异步竞态
- **问题反馈**：
  `testLeaveRoomRejectedPreservesActiveBindingAndSession:673 cachedReject为null`。`handleFrame` 底层业务为异步，不能立即同步读缓存；断言混用领域 `ReceiptStatus`；不使用伪造代次的脱节会话，确保测试真实覆盖服务端拒绝回执路径且保留合法 guest 的可读性、席位完整性与最终合法离房注销。
- **修复方案**：
  1. 规范断言类型：
     - 在 [`server/src/test/kotlin/com/majiang/server/udp/UdpRoomTest.kt`](file:///D:/majiang/server/src/test/kotlin/com/majiang/server/udp/UdpRoomTest.kt) 中将别名导入统一替换为直接导入 `com.majiang.transport.api.ProtocolReceiptStatus`，断言全部显式使用 `ProtocolReceiptStatus.REJECTED` 与 `ProtocolReceiptStatus.APPLIED`，杜绝与领域层同名 enum 混淆。
  2. 线程安全有界响应等待：
     - 引入 `waitForResponse(session, requestId, timeoutMs = 3000L)` 工具方法，通过有界轮询 `session.getCachedResponse(requestId)` 确定性等待异步操作在权威执行队列完成并缓存。
  3. 真实覆盖拒绝路径与席位保持：
     - 摒弃构造未绑定的 `staleSession`，使用已合法加入并在 `sessionsByConnectionId` 注册的真人在座会话 `guestSession`；
     - 触发权威 SharedRoom 拒绝离房条件（模拟席位非正常置空 `room.sharedRoom.seatOccupied[1] = false`），使 `guestSession` 的合法代次请求顺利穿透网关代次校验，进入 `room.leave` 并经由 SharedRoom 权威队列裁决返回 `ProtocolReceiptStatus.REJECTED`（原因 `NOT_MEMBER`）；
     - 验证服务端执行了 `receipt.status == ProtocolReceiptStatus.REJECTED` 路径：返回拒绝回执，**绝未注销会话，绝未关闭 session，绝未标记 displaced**；
     - 恢复席位真实状态后，验证该活跃成员仍可继续正常发送业务请求（`GET_ROOM_INFO` 成功返回最新房间信息）；
     - 最终该成员发送合法的 `LEAVE_ROOM` 请求，顺利获得 `ProtocolReceiptStatus.APPLIED` 回执，并完成会话的注销与关闭。

---

## 3. 修改文件清单

| 文件路径 | 修改性质 | 主要说明 |
|---|---|---|
| [`server/src/main/kotlin/com/majiang/server/udp/UdpTableServer.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/udp/UdpTableServer.kt) | 生产代码 | 增加 KICK 生命周期清理与 CAS 移除（`terminateKick`, `purgeKick`, `cancelAndPurgeKick`）；在 `terminateSession` 与 `close()` 中排空并取消所有在途 KICK 定时任务；增加迟注册关闭防御 |
| [`server/src/test/kotlin/com/majiang/server/udp/UdpRoomTest.kt`](file:///D:/majiang/server/src/test/kotlin/com/majiang/server/udp/UdpRoomTest.kt) | 测试代码 | 在重发与并发更新用例中补齐 `server.registerSession` 前提；统一使用 `ProtocolReceiptStatus`；引入 `waitForResponse` 观察异步回执；真实覆盖服务端拒绝离房回执与合法成员会话保持 |

---

## 4. 未解决项

- `FEEDBACK-R4B1SERVERFINALFIX.md` 列出的全部 3 项问题均已解决。
- 无遗留未解决项。

---

## 5. 测试执行声明

> **声明**：按照本次协同工作流与授权约定，开发端（Anti）仅使用内置文件读写/编辑工具进行源码修改，**未在本地执行 Gradle 构建与自动化测试**。
> 所有编译、构建、静态检查与自动化验收测试统一由监管端（Codex）独立串行执行与验证。
