# 交付报告：R4B1SERVERFIX1 (Server UDP & 房间生命周期首轮验收返修)

## 1. 任务概述

本任务为针对 `doc/v4-migration/FEEDBACK-R4B1SERVERFIX1.md` 提出的全部 5 项缺陷与验收建议的专门返修。
严格遵循文件所有权范围：仅修改 Server UDP 模块及其测试，以及必要的进程内/共享房间消费者文件。未修改 client、protocol、reviewer 测试或构建脚本。不包含任何旧 V3 兼容别名。

---

## 2. 反馈问题逐项修复说明

### Item 1：房主离房回执与生命周期时序 (`V4ReviewAcceptanceTest.hostLeaveMustCompleteReceiptAndKeepLobbyConnectionOpen`)
- **根因分析**：
  1. `nextConnectionId()` 使用 `random.nextInt()` 生成任意 32 位有符号非零整数，可能为负数。原 `UdpRoom.closeTransport` 中 `excludeConnectionId < 0` 判定将所有负数连接 ID 错误判定为“未豁免”，导致向主动离房的房主下发了 `ROOM_CLOSED` KICK 数据帧。客户端收到 KICK 后立刻关闭 `ReliableRequestor`，导致房主本地的 `leaveRoom().await()` 抛出 `IOException("ReliableRequestor has been closed")`。
  2. 房主在 `UdpTableServer` 中提交 `ApiTags.LEAVE_ROOM` 时，原实现直接关闭或由底层级联关闭连接，未确保 `APPLIED` RSP 数据包先从 UDP Socket 发出。
  3. `UdpClientSession.close()` 原逻辑清空了 `idempotentCache`，导致网络丢包重传的 `LEAVE_ROOM` 在会话终止后无法命中缓存并被错误回包 `INVALID_TOKEN`。
- **修复方案**：
  - 在 [`server/src/main/kotlin/com/majiang/server/udp/UdpRoom.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/udp/UdpRoom.kt) 中：
    - 将 `leaveExclusion` 改为 `AtomicReference<Int?>(null)`，彻底消除与负数连接 ID 的哨兵冲突。
    - 引入 `leavingConnectionIds = ConcurrentHashMap.newKeySet<Int>()`，在 `leave()` 调用之初立即登记连接 ID，并在 `closeTransport` 中豁免 KICK 下发与会话关闭。
  - 在 [`server/src/main/kotlin/com/majiang/server/udp/UdpTableServer.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/udp/UdpTableServer.kt) 中：
    - `ApiTags.LEAVE_ROOM` 回调中，必须先调用 `replySession(resp)` 将 `APPLIED` RSP 数据包发出并缓存，随后再执行安全 `session.close()`。
  - 在 [`server/src/main/kotlin/com/majiang/server/udp/UdpClientSession.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/udp/UdpClientSession.kt) 中：
    - `close()` 不再清除已有的 `idempotentCache`（已有 60s TTL 及最大 512 条目限制自动过期与淘汰），确保终止连接记录 `terminatedSessions` 能够正确重放重试的 `LEAVE_ROOM` 响应。

---

### Item 2：顶号异步踢除与代次作废断言 (`UdpRoomTest.testResumeSessionDisplacesOldConnection`)
- **根因分析**：
  - `UdpRoom.resume` 将 `kickSender` 与 `oldSession.close()` 卸载至 `asyncExecutor` 后台线程执行以避免阻塞席位锁。原测试在主线程中未等待后台任务调度，直接同步断言 `oldSession.isClosed`，造成测试竞态。
- **修复方案**：
  - 在 [`server/src/test/kotlin/com/majiang/server/udp/UdpRoomTest.kt`](file:///D:/majiang/server/src/test/kotlin/com/majiang/server/udp/UdpRoomTest.kt) 中：
    - 为 `kickSender` 注入 `CountDownLatch(1)`，测试使用 `kickLatch.await(3, TimeUnit.SECONDS)` 确定性等待 KICK 触发。
    - 增加有界轮询等待 `oldSession.isClosed` 完成（超时 3 秒）。
    - 增加新旧代次断言：`assertTrue(oldSession.generation < room.getConnectionGeneration(1))`，确保旧代次已被作废。

---

### Item 3：冒烟测试联机开赛前置准备状态 (`UdpTableServerSmokeTest.testFullLoopbackLifecycle`)
- **根因分析**：
  - 服务端 `SharedRoom.startMatch` 严格遵循联机规则，要求所有在座真人必须处于 `isReady == true` 状态。原测试在 Client 1 与 Client 2 加入后未调用 `SET_READY` 直接调用 `START_MATCH`，导致权威规则按契约返回 `REJECTED("NOT_READY")`。
- **修复方案**：
  - 在 [`server/src/test/kotlin/com/majiang/server/udp/UdpTableServerSmokeTest.kt`](file:///D:/majiang/server/src/test/kotlin/com/majiang/server/udp/UdpTableServerSmokeTest.kt) 的步骤 4 前增加：
    - Client 1 准备：`SET_READY(ready = true)`（`requestId = "req-3-1"`, `seq = 3`），断言 `APPLIED`。
    - Client 2 准备：`SET_READY(ready = true)`（`requestId = "req-3-2"`, `seq = 2`），断言 `APPLIED`。
    - 相应递增后续请求序号（Client 1 的 `START_MATCH` 为 `seq = 4`，`SNAPSHOT` 为 `seq = 5`，`LEAVE_ROOM` 为 `seq = 6`；Client 2 的失效 `SNAPSHOT` 为 `seq = 3`）。

---

### Item 4：房间更新 Watermark CAS 与 In-Flight Put 并发原子化与回归测试
- **根因分析**：
  - `UdpTableServer.sendRoomUpdate` 中版本号 watermark 比较/更新与 `inFlightRoomUpdates.put` 分离，若线程 A (v2) 推进 watermark 后发生上下文切换，线程 B (v3) 紧随其后推进 watermark 并完成 put，之后线程 A 苏醒并执行 put(v2)，旧版本待发条目将覆盖新版本并取消其重发定时器。
- **修复方案**：
  - 在 [`server/src/main/kotlin/com/majiang/server/udp/UdpTableServer.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/udp/UdpTableServer.kt) 中：
    - 引入 `private val roomUpdateLocks = ConcurrentHashMap<Int, Any>()`。
    - 在 `synchronized(roomUpdateLocks.computeIfAbsent(connId) { Any() })` 临界区内原子化完成 watermark 检查、watermark 推进以及 `inFlightRoomUpdates.put`。
    - 外部网络 Socket 发送 `send()`、退避重试调度 `scheduleRoomUpdateRetry()` 以及旧条目取消 `cancel()` 保持在锁临界区之外执行。
    - 在会话离开、KICK 踢出、超时淘汰及服务器关闭时清理 `roomUpdateLocks`。
  - 在 [`server/src/test/kotlin/com/majiang/server/udp/UdpRoomTest.kt`](file:///D:/majiang/server/src/test/kotlin/com/majiang/server/udp/UdpRoomTest.kt) 中：
    - 新增并发交错回归测试 `testConcurrentInterleavedRoomUpdatesDoNotOverwriteNewerVersion`：
      - 显式验证 v2 -> v3 后，滞后的 v1 / v2 到达时被静默丢弃，在途记录严格保持 v3。
      - 启动 20 个并发线程同时打乱提交版本 10L..60L，验证最终在途记录与 watermark 严格收敛至最高版本 60L。

---

### Item 5：移除 SharedRoom.ResumeSessionResult 别名访问器
- **修复方案**：
  - 在 [`server/src/main/kotlin/com/majiang/server/room/SharedRoom.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/room/SharedRoom.kt) 中：
    - 彻底移除 `ResumeSessionResult` 上的 `val first`, `val second`, `val third` 兼容访问器，保留纯净字段 `(val seat: Int, val snapshot: PlayerSnapshot, val generation: Long)`。
  - 在 [`server/src/main/kotlin/com/majiang/server/inprocess/InProcessBinding.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/inprocess/InProcessBinding.kt) 中：
    - 消费者直接访问 `res.seat` 与 `res.snapshot`。
  - 在 [`server/src/test/kotlin/com/majiang/server/room/SharedRoomTest.kt`](file:///D:/majiang/server/src/test/kotlin/com/majiang/server/room/SharedRoomTest.kt) 中：
    - 测试代码直接断言 `res.seat`, `res.snapshot`, `res.generation > 1L`。

---

## 3. 修改文件清单

| 文件路径 | 修改性质 | 主要说明 |
|---|---|---|
| `server/src/main/kotlin/com/majiang/server/room/SharedRoom.kt` | 生产代码 | 移除 `ResumeSessionResult` 的 `first`/`second`/`third` 别名 |
| `server/src/main/kotlin/com/majiang/server/inprocess/InProcessBinding.kt` | 生产代码 | 迁移 `resumeSession` 调用者访问 `res.seat` 与 `res.snapshot` |
| `server/src/test/kotlin/com/majiang/server/room/SharedRoomTest.kt` | 测试代码 | 迁移测试访问 `res.seat`, `res.snapshot`, `res.generation` |
| `server/src/main/kotlin/com/majiang/server/udp/UdpRoom.kt` | 生产代码 | 修正 `leaveExclusion` 为引用避免负数冲突；`leavingConnectionIds` 保护离房者免受 KICK；`resume` 异步 kick 隔离 |
| `server/src/main/kotlin/com/majiang/server/udp/UdpClientSession.kt` | 生产代码 | `close()` 保留 `idempotentCache` 供终止会话重传 LEAVE_ROOM 回执 |
| `server/src/main/kotlin/com/majiang/server/udp/UdpTableServer.kt` | 生产代码 | 引入 `roomUpdateLocks` 原子化 watermark 与 in-flight put；`LEAVE_ROOM` 先发送回执再关闭会话；清理锁资源 |
| `server/src/test/kotlin/com/majiang/server/udp/UdpRoomTest.kt` | 测试代码 | `testResumeSessionDisplacesOldConnection` 引入信号与有界等待；新增 `testConcurrentInterleavedRoomUpdatesDoNotOverwriteNewerVersion` |
| `server/src/test/kotlin/com/majiang/server/udp/UdpTableServerSmokeTest.kt` | 测试代码 | `testFullLoopbackLifecycle` 开赛前补齐真人 `SET_READY` 及其递增 sequence 映射 |

---

## 4. 未解决项与遗留风险

- 本次返修完全覆盖了反馈文档中列出的全部 5 个缺陷项。
- 业务范围严格限制在 Server UDP 与相关单元测试，未跨越到客户端 UDP 或上层业务。

---

## 5. 测试执行声明

> **说明**：按照本次协同工作流与授权约定，开发端（Anti）仅使用内置文件读写/编辑工具进行源码修改，**未在本地执行 Gradle 构建与自动化测试**。
> 所有构建、编译与自动化验收测试统一由监管端（Codex）独立串行执行与验证。
