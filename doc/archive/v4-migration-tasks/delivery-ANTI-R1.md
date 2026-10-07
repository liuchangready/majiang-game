# V4 迁移交付报告：任务 R1 (协议与连接可靠性修复)

**交付人**：Antigravity (Anti)  
**架构与验收负责人**：Codex  
**交付日期**：2026-10-05  
**测试执行声明**：**本交付物中的所有代码与用例均未由 Anti 运行或执行构建**（严格遵守任务约束：Anti 仅使用内置文件读写工具，不执行任何命令行、Gradle 构建、独立测试或进程，所有编译构建、独立测试与 9 项验收均交由 Codex 负责执行）。

---

## 一、修改与新增文件清单

### 1. 协议层 (protocol)
- [`protocol/src/main/kotlin/com/majiang/transport/api/ApiMessage.kt`](file:///D:/majiang/protocol/src/main/kotlin/com/majiang/transport/api/ApiMessage.kt)
- [`protocol/src/main/kotlin/com/majiang/transport/udp/Fragmenter.kt`](file:///D:/majiang/protocol/src/main/kotlin/com/majiang/transport/udp/Fragmenter.kt)
- [`protocol/src/main/kotlin/com/majiang/transport/udp/PushSequencer.kt`](file:///D:/majiang/protocol/src/main/kotlin/com/majiang/transport/udp/PushSequencer.kt)
- [`protocol/src/test/kotlin/com/majiang/transport/api/ApiMessageCodecTest.kt`](file:///D:/majiang/protocol/src/test/kotlin/com/majiang/transport/api/ApiMessageCodecTest.kt) *(新增单元测试)*
- [`protocol/src/test/kotlin/com/majiang/transport/udp/FragmenterTest.kt`](file:///D:/majiang/protocol/src/test/kotlin/com/majiang/transport/udp/FragmenterTest.kt)

### 2. 服务端 (server)
- [`server/src/main/kotlin/com/majiang/server/udp/UdpClientSession.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/udp/UdpClientSession.kt)
- [`server/src/main/kotlin/com/majiang/server/udp/UdpRoom.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/udp/UdpRoom.kt)
- [`server/src/main/kotlin/com/majiang/server/udp/UdpTableServer.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/udp/UdpTableServer.kt)
- [`server/src/main/kotlin/com/majiang/server/inprocess/InProcessBinding.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/inprocess/InProcessBinding.kt)
- [`server/src/test/kotlin/com/majiang/server/udp/UdpTableServerSmokeTest.kt`](file:///D:/majiang/server/src/test/kotlin/com/majiang/server/udp/UdpTableServerSmokeTest.kt)
- [`server/src/test/kotlin/com/majiang/server/inprocess/InProcessBindingTest.kt`](file:///D:/majiang/server/src/test/kotlin/com/majiang/server/inprocess/InProcessBindingTest.kt)

### 3. 客户端 (client)
- [`client/src/main/kotlin/com/majiang/client/transport/udp/UdpClientBinding.kt`](file:///D:/majiang/client/src/main/kotlin/com/majiang/client/transport/udp/UdpClientBinding.kt)
- [`client/src/test/kotlin/com/majiang/client/api/ServerConnectionSessionTest.kt`](file:///D:/majiang/client/src/test/kotlin/com/majiang/client/api/ServerConnectionSessionTest.kt)
- [`client/src/test/kotlin/com/majiang/client/transport/udp/UdpClientBindingTest.kt`](file:///D:/majiang/client/src/test/kotlin/com/majiang/client/transport/udp/UdpClientBindingTest.kt)

---

## 二、缺陷根因分析与修复说明

### 1. 畸形报文/缺失字段引发 NPE 与服务端接收线程退出
- **根因**：Gson 反序列化直接通过 Unsafe 绕过 Kotlin 构造器中的非空检查与 `init` 校验块；缺失 `api` 或 `requestId` 时赋值为 null，后续代码使用其属性时抛出 NPE；服务端 `receiveLoop` 未对单帧处理的异常做全面捕获，导致接收线程意外终止。
- **修复**：
  - 在 `ApiMessageCodec.decodeRequest` 中增加了显式防空与合法性校验：检查 `api` 是否为空或空白、`requestId` 是否为空或空白、`api` 是否存在于 `ApiTags.KNOWN` 集合、`protocolVersion >= 1`，并在不符合时抛出 `IllegalArgumentException`。
  - 在 `UdpTableServer.receiveLoop` 中用 `try-catch` 包裹单帧调度 `handleFrame`，确保任何畸形数据报或单帧解码异常绝不导致服务端监听线程退出。
  - 在 `UdpTableServer.handleReq` 入口处先校验 `connectionId`：若为非零且非活跃会话也非终止会话，静默丢弃，阻断 UDP 反射放大攻击。

### 2. 未绑定连接分片混淆与重组校验漏洞
- **根因**：`Fragmenter` 重组缓冲仅使用 `(connectionId, msgId)` 作为键。大厅未绑定阶段所有客户端 `connectionId == 0`，若不同客户端生成相同 `msgId` 或网络中存在旧分片，会发生交叉串包；同时缺少对分片子头（`fragCount`、`fragIndex`、`originalType` 等）的一致性约束以及报文大小上限保护。
- **修复**：
  - 重组键引入 `InFlightKey(connectionId, msgId, remoteAddress)`：对未绑定阶段 (`connectionId == 0`) 严格按来源 `remoteAddress` 进行物理隔离；对于已绑定连接 (`connectionId != 0`) 忽略地址变更以支持 NAT 漂移/移动端切换。
  - 增加对 `fragCount`（1..256）、`fragIndex`、`originalType`、`connectionId`、`ackSeq`、`version` 的严格跨片一致性比对。
  - 设置总报文上限 `MAX_MESSAGE_BYTES = 256 * 1090` 和最大分片数 `MAX_FRAG_COUNT = 256`。
  - `reassemble` 提供重载方法接收 `remoteAddress`，同时保留无地址重载以兼容单连接测试。

### 3. 大厅建房/加房/重连重发产生重复资源与请求去重
- **根因**：网络抖动或丢包重传导致客户端重发 `CREATE_ROOM` 或 `JOIN_ROOM`，服务端重复创建多个房间或绑定多个席位；普通会话请求未做并发在途去重；普通成员离开后若 RSP 丢失，重试的 `LEAVE_ROOM` 因会话已被移除而被服务端静默忽略，造成客户端超时。
- **修复**：
  - 在 `UdpTableServer` 中引入基于 `(remoteAddress, requestId)` 的有界在途映射表 (`lobbyInFlightRequests`) 与完成结果缓存表 (`lobbyCompletedRequests`)，同 `requestId` 的建房/加房/重连重发必定返回同一绑定结果。
  - 在 `UdpClientSession` 中增加 `inFlightRequests`，在同一会话内并发调用相同 `requestId` 时挂起并复用同一异步响应。
  - 引入有界 `terminatedSessions` 缓冲表（最大 1024 限制）：在玩家离开或超时断开后，将其 `session` 缓存在终止表中；若客户端因丢包重试 `LEAVE_ROOM`，直接重放缓存的离开回执；若在已终止连接上发送其他请求，明确返回 `INVALID_TOKEN`。

### 4. 全量推送自愈循环与重复回调
- **根因**：当服务端下发全量快照自愈推送时（`fromCursor >= snapshot.cursor`），客户端未能识别其全量属性，进入缺口判定并发送 RESEND，进而陷入 RESEND 循环；客户端在构造 `PushSequencer` 时注册了回调，同时在 `handlePushFrame` 中又对 `PushOfferResult` 进行二次派发，导致 RESEND 与 SNAPSHOT 请求双倍触发。
- **修复**：
  - 在 `PushSequencer.offer` 中优先检查 `isFullSnapshot`：若为全量快照，直接将基线跃迁更新至 `cursor` 并返回 `Applied`。
  - 在 `UdpClientBinding.handlePushFrame` 中根据 `update.fromCursor >= update.snapshot.cursor` 识别全量快照并传递 `isFullSnapshot = true`，且仅在非全量快照缺口时才发送 RESEND。
  - 移除 `UdpClientBinding` 中 `PushSequencer` 构造参数的重复回调，统一由 `handlePushFrame` 依据 `PushOfferResult` 结果单独驱动。
  - 在建房、加房、重连成功时执行 `replayGuard.reset(0)` 和 `pushSequencer.adoptFullSnapshot(cursor)`；离房时重置为 0。

### 5. 连接归属、离开与终结生命周期收敛
- **根因**：
  - 房主主动离开时，服务端关闭房间向全员（包括房主自身）广播 `ROOM_CLOSED`，导致房主收到自身被踢通知而非正常的 `APPLIED` 回执；
  - 普通成员显式离开未清理房间内的玩家名称与席位占用，且未注销令牌；
  - 服务端在 `close()` 中先关闭了 DatagramSocket，导致后续遍历发送的 `SERVER_SHUTDOWN` KICK 报文全部被拦截；
  - 客户端已关闭状态下丢弃所有入站帧，导致服务端重传的 KICK 得不到 ACK 确认。
- **修复**：
  - `UdpRoom.close(excludeConnectionId: Int?)`：房主主动离开时解散房间，但排除向房主连接发送 `ROOM_CLOSED`，房主收到正常的 `APPLIED` 回执。
  - 普通成员显式离开时：若在开赛前离开，清理席位占用与玩家名，并生成新 UUID 注销原 `resumeToken`。
  - `UdpTableServer.close()`：在 Socket 仍开启状态下向所有活跃会话直发 `SERVER_SHUTDOWN` KICK，短暂等待 50ms 冲出网卡，随后再关闭 Socket、线程池与各房间。
  - `UdpClientBinding.onInboundFrame`：已处于关闭状态的客户端若再次收到 KICK，回发 ACK 协助服务端完成状态收敛，不重复触发断开事件。
  - 严格连接身份校验：已绑定客户端对所有非 RSP 帧要求其 `connectionId` 严格与本地一致（丢弃 0 及其他号）；未绑定客户端丢弃任何 PUSH 与 KICK。

### 6. 未绑定心跳与防重放序列号
- **根因**：未绑定时服务端回复 PONG 曾使用固定 `seq = 1`，客户端 `ReplayGuard` 判定后续心跳为重放丢弃，导致健康连接心跳失步判死。
- **修复**：
  - 服务端引入单调递增的原子序号 `unattachedOutboundSeq`，使未绑定 PONG 的 `seq` 严格递增。
  - 客户端在建立房间绑定与离房时正确调用 `replayGuard.reset(0)`。

### 7. 测试代码缺失与回环冒烟测试辅助器修复
- **根因**：
  - `ServerConnectionSessionTest.kt` 缺失 `ShuffleSelection` 导致无法通过编译；
  - `UdpClientBindingTest.kt` 缺失 `ApiTags` import；
  - `UdpTableServerSmokeTest.kt` 的辅助函数 `receiveUntil` 在条件检查中提前消费了 `fragmenter.reassemble`，跳出后再次调用返回 null；且在等待 RSP 期间直接丢弃了到达的异步 PUSH/KICK 报文，导致后续断言超时。
- **修复**：
  - 补齐 `ServerConnectionSessionTest.kt` 中的 `ShuffleSelection()` 参数与对应 import。
  - 补齐 `UdpClientBindingTest.kt` 中的 `import com.majiang.transport.api.ApiTags`。
  - 重构 `UdpTableServerSmokeTest.kt` 中的接收与请求辅助器：建立 per-socket 的 `socketInboxes` 未消费帧缓冲队列与独立 `Fragmenter`；单次完成分片重组；按 `requestId` 精确匹配 RSP；将异步到达的 PUSH/KICK 完整保留在收件箱中供后续步骤提取。

### 8. requestControl 语义翻译对齐
- **根因**：V4 统一 API 契约规定 `requestControl(takeover = true)` 代表玩家声明收回控制权（关闭 AI 托管），而底层 engine 的接口语义是 `access.requestControl(auto: Boolean)`（true 代表开启托管）。
- **修复**：在边界适配层 `InProcessBinding.kt` 和 `UdpRoom.kt` 中统一进行布尔值取反翻译：`requestControl(!takeover)`，确保两端对齐。并在 `InProcessBindingTest.kt` 中补充了验证测试。

### 9. 时钟注入与资源清理
- **根因**：`UdpRoom` 和 `UdpClientSession` 原先部分使用系统时钟或漏传注入时钟，导致虚拟时钟单元测试无法可靠控制入站超时。
- **修复**：`UdpRoom` 构造器增加 `udpClock: UdpClock` 参数并完整传递给各 `UdpClientSession`；在 `UdpTableServer.checkSessionTimeouts` 中将清理的断开会话放入终止表以限制缓存规模。

---

## 三、未决事项与后续风险提示

1. **未决事项**：
   - 任务 R1 的全部目标与所有权范围内的生产代码、测试编译与缺陷根因均已完全实现并修复完毕。
   - 本任务范围未进入 R2（即未修改 `requestControl` 为更具体命名的重构，保留既有 V4 契约以防破坏其它模块）。
2. **测试与验收提示**：
   - 依据任务约定，Anti 尚未且不应执行任何测试命令或 Gradle 任务。
   - 请 Codex 运行全量测试套件、独立 9 项验收测试（包括 `tools/architecture/v4-review/V4ReviewAcceptanceTest.kt`）以及桌面/Android APK 编译构建。
