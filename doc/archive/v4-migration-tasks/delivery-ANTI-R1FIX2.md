# V4 迁移交付报告：任务 R1FIX2 (生产代码精准缺陷修复)

**交付人**：Antigravity (Anti)  
**架构与验收负责人**：Codex  
**交付日期**：2026-10-05  
**测试执行声明**：**本交付物中的所有修改均由 Anti 仅通过内置文件读写工具完成，未运行任何命令行、Gradle 任务、独立测试或构建命令**。所有构建验证、单元测试与架构审查测试全权交由 Codex 执行验收。

---

## 一、修改文件清单

### 1. 协议层 (protocol)
- [`protocol/src/main/kotlin/com/majiang/transport/api/ApiMessage.kt`](file:///D:/majiang/protocol/src/main/kotlin/com/majiang/transport/api/ApiMessage.kt)
  - `encodePush` 仅在 `isFullSnapshot == true` 时在线上 JsonObject 中添加规范显式布尔标记 `"full": true`。
  - `decodePushEnvelope` 严格提取并校验规范布尔标记（同时兼容容错 `"isFullSnapshot"` 布尔原语，非法非布尔抛出 `JsonParseException`；无游标形态回退）；在将 JsonObject 传给严格白名单的 `ProtocolJson.PlayerUpdateAdapter` 前，通过 `deepCopy()` 副本并移除 `"full"` 与 `"isFullSnapshot"` 属性，彻底根治 `JsonParseException: Unknown field 'full' in PlayerUpdate`。保留原 `encodePush(update, isFullSnapshot = ...)` 签名。
- [`protocol/src/main/kotlin/com/majiang/transport/udp/ReplayGuard.kt`](file:///D:/majiang/protocol/src/main/kotlin/com/majiang/transport/udp/ReplayGuard.kt)
  - 在 `init` 中校验 `require(windowSize > 0)`。
  - 维护独立的 `baseline` 基线底标（与已见最高序 `maxSeen` 区分）；初始 `baseline = initialMaxSeen`。
  - `accept(seq)` 对 `seq <= baseline` 或 `seq <= 0` 严格拒绝；对 `seq > baseline` 的未见乱序包在 256 滑动窗口内接纳，重复包或窗口左侧外老包拒绝；未回退到最高序拒绝。
  - `reset(newMaxSeen)` 同时将 `baseline` 与 `maxSeen` 重置为 `newMaxSeen` 并清空重置窗口位图。

### 2. 服务端 (server)
- [`server/src/main/kotlin/com/majiang/server/udp/UdpTableServer.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/udp/UdpTableServer.kt)
  - **优雅关闭生命周期**：`close()` 改用 `isClosing.compareAndSet(false, true)` 独占关闭流程，并发/重复调用安全；在 `isClosed` 仍为 `false`、Socket 和接收线程与调度器完全正常运转状态下，向全部未关闭活跃会话发起 `sendKick(session, ApiProtocol.SERVER_SHUTDOWN)`；采用有界 1000ms 预算（完整覆盖 0/100/300ms 重试及网络 ACK 延迟）等待 `CompletableFuture.allOf(*kickFutures)` 完成；排空完成后再置 `isClosed.set(true)`、停止 ticker、关闭会话与 Socket；接收线程 join 增加 `Thread.currentThread() != receiverThread` 防自加入死锁保护。
  - **请求准入与拦截**：`createRoom`、`tickRooms`、`checkSessionTimeouts` 与 `handleReq` 均检查 `isClosing.get()`，关闭过程中拒绝新业务请求并回发 `SERVER_SHUTDOWN`。
  - **KICK 确认与重传排查**：`handleAck` 增加精确匹配 `kick.seq == frame.ackSeq && kick.address == remoteAddress`，杜绝旧业务响应迟到的 ACK 误取消终端 KICK 重传；`sendKick` 替换既有条目时主动取消旧定时器并完成旧 Future，捕获 `RejectedExecutionException` 调度拒绝并安全清理条目与完成 Future。
  - **资源与墓碑缓存 TTL 淘汰**：`terminatedSessions` 升级为 `TerminatedSessionEntry(session, timestamp)`，基于注入时钟 `clock.currentTimeMillis()` 超过 60s 淘汰，上限 1024；大厅在途请求基于时钟淘汰 >15s 超时条目并限制 256 容量，超出回发 `SERVER_BUSY`；`handleSessionReq` 与 `handleLobbyReq` 内的业务派发增加 `try-catch (t: Throwable)` 兜底，发生未捕获异常时主动清理在途请求并向等待者回发 `INTERNAL_ERROR`，杜绝挂死。
- [`server/src/main/kotlin/com/majiang/server/udp/UdpClientSession.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/udp/UdpClientSession.kt)
  - `putInFlightEntry`：基于注入 `clock` 遍历清理 >15s 的超时请求并异常完成其 Future；在途请求达到 `MAX_IN_FLIGHT_ENTRIES` (256) 严格上限时**直接拒绝准入，不突破上限**，并以 `ApiErrors.SERVER_BUSY` 完成 Future。
  - `CachedSessionEntry` 与 `InFlightSessionEntry` 全面统一使用注入时钟时间戳；`cacheResponse` 与 `getCachedEntry` 执行 60s TTL 超时淘汰与 512 上限淘汰。
  - `close()` 释放时主动移除全部在途请求并以 `IllegalStateException("Session closed")` 异常完成其 Future，清空幂等缓存。

### 3. 客户端 (client)
- [`client/src/main/kotlin/com/majiang/client/transport/udp/UdpClient.kt`](file:///D:/majiang/client/src/main/kotlin/com/majiang/client/transport/udp/UdpClient.kt)
  - `onCloseCallback` 延迟关闭底层 Socket 的宽限时间由 500ms 调整为 2000ms，确保能够覆盖服务端 0/100/300ms 的重传调度预算。
- [`client/src/main/kotlin/com/majiang/client/transport/udp/UdpClientBinding.kt`](file:///D:/majiang/client/src/main/kotlin/com/majiang/client/transport/udp/UdpClientBinding.kt)
  - 收到 `UdpPacketType.KICK` 时，**无论是否被 replayGuard 判定为重复帧，均无条件回发 ACK 确认帧**，随后才执行 `replayGuard.accept` 过滤断线重复通知。确保在首个终端 ACK 丢包场景下，服务端重传的同一 seq KICK 能够收到重发的终端 ACK。

---

## 二、5 个缺陷修复对照

| 编号 | 缺陷项 | 根因与修复措施 |
| :--- | :--- | :--- |
| **1** | `decodePushEnvelope` 导致 `JsonParseException: Unknown field 'full'`，全量 PUSH 被丢弃 | 根因：`PlayerUpdateAdapter` 有严格字段白名单，线上 envelope 附加的 `"full"` 导致其抛错。<br/>修复：读取并校验显式布尔 marker 后，从 `JsonObject.deepCopy()` 副本中删除 `"full"` 与 `"isFullSnapshot"`，再交给 `PlayerUpdate` 反序列化。 |
| **2** | `UdpTableServer.close` 提前置 `isClosed` 并停止 ticker 导致无法发送 KICK 与重试 | 根因：`close()` 开头执行了 `isClosed.set(true)` 和 `ticker.shutdownNow()`，导致 `send()` 与接收循环及重传调度立即中断。<br/>修复：使用 `isClosing` CAS 独占关闭，拒绝新业务；保持 Socket、接收线程与重传调度器活跃；等待 1000ms 排空预算后再置 `isClosed` 并关闭资源；防范接收线程 join 死锁。 |
| **3** | `handleAck` 仅按 `connectionId` 移除导致误取消 KICK 重传；终端重传未获 ACK | 根因：服务端的 `handleAck` 未比对 `ackSeq == kick.seq`；客户端在收到重传 KICK 时因防重放提前 return 未回 ACK；Socket 保持时间（500ms）不足。<br/>修复：服务端校验 `ackSeq` 与源地址；客户端收到 KICK 无条件回发 ACK；客户端 Socket 保留宽限期提升至 2000ms。 |
| **4** | `ReplayGuard` reset 后基线错误及缺少 `windowSize` 正数校验 | 根因：未区分 reset 建立的 floor 基线与最大接收序，导致 `initialMaxSeen=50` 时 `accept(40)` 误接纳。<br/>修复：加入 `require(windowSize > 0)`；维护 `baseline` 底标；严格拒绝 `<= baseline`；接纳窗口内乱序新包；`reset` 同步更新 `baseline` 与 `maxSeen`。 |
| **5** | `UdpClientSession` 在途请求突破 256 上限，缺少严格容量与时钟过期淘汰 | 根因：先前仅尝试淘汰一个最老条目后仍无条件 `putIfAbsent`，且缓存使用了 `System.currentTimeMillis()`。<br/>修复：注入时钟过期扫描；容量满 256 时拒绝准入并以 `SERVER_BUSY` 完成 Future；会话关闭清理挂起 Future；异常分支统一兜底回复。 |

---

## 三、未决事项说明

- 本任务已完整修复 TASK-ANTI-R1FIX2 指出的全部 5 项缺陷，代码修改严格限制在授权范围（protocol、client、server），未触碰 `tools/architecture/v4-review`，未修改 Codex 测试夹具。
- 依据工作分工，Anti 未执行任何测试与构建，因此不做出“构建或测试必然全部通过”的无根据保证。交付物已就绪，请 Codex 运行独立构建与测试验证。
