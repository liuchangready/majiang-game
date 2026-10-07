# R4B1UDPCLIENT 客户端UDP可靠房间推送 - 交付报告 (Anti)

**交付时间**：2026-10-06  
**交付者**：Antigravity (Anti) - 生产代码开发者  
**验收者**：OpenAI Codex - 架构决策、独立测试、构建与验收负责人  
**依据文档**：`AGENTS.md`、`doc/v4-migration/PLAN.md`、`doc/v4-migration/CONTRACT.md`、`doc/v4-migration/TASK-ANTI-R4B1UDPCLIENT.md`、`doc/v4-migration/FEEDBACK-GLM-R4B1UDP.md`、`doc/v4-migration/FEEDBACK-R4B1UDPRESUME.md`

---

## 1. 交付声明与测试执行说明

> [!IMPORTANT]
> **测试执行声明（显式声明）**：
> 本任务 R4B1UDPCLIENT 的所有代码演进与模块单元测试均由 Anti **仅使用内置文件读写/编辑工具**完成落盘。
> **Anti 未运行任何测试、构建或终端命令**（未执行 `./gradlew test`、shell、终端或构建脚本），测试执行、编译检查与最终验收全权委托并由 Codex 独立运行。

---

## 2. 独占修改文件清单

按照 `TASK-ANTI-R4B1UDPCLIENT.md` 的独占范围授权，未修改 `ServerConnectionSession`、`client.api` 契约/mapper、`protocol`、`server`、`reviewer` 或构建配置。

| 文件路径 | 状态 | 职责说明 |
|---|---|---|
| `client/src/main/kotlin/com/majiang/client/transport/udp/UdpClientBinding.kt` | 修改 | 实现可靠 ROOM_UPDATE 推送识别、ReplayGuard 前回发 ACK、UI 去重与版本单调性、跳版本合并、代次与房间隔离、早期缓冲与旧房清理 |
| `client/src/test/kotlin/com/majiang/client/transport/udp/UdpClientBindingTest.kt` | 修改/扩展 | 新增 9 项假时钟/假调度器模块单元测试，覆盖丢更新重传、丢首次 ACK 重传收敛、乱序旧版本、跳版本、外来 roomId 隔离、换房重置、早期缓冲回放、延迟查询隔离及畸形信封拦截 |

---

## 3. 核心行为变化与契约实现细节

### 3.1 识别在 ReplayGuard 之前与回发 ACK（解决丢首个 ACK 无法收敛问题）
- **契约规则**：服务端以 `PUSH` 完整 `ROOM_UPDATE` 信封发送 `ProtocolRoomInfo`，重试使用稳定消息 `seq`，客户端回发 `ACK(ackSeq = frame.seq)`。
- **ReplayGuard 与 ACK 解耦**：在 `onInboundFrame` 的 `UdpPacketType.PUSH` 分支中，先由 `handleInboundPush` 解析信封载荷：
  - 若为 `ROOM_UPDATE` 且 `roomId` 匹配当前绑定的 `boundRoomId`，**在调用 `replayGuard.accept(frame.seq)` 之前立即回发 `ACK`**；
  - 随后调用 `replayGuard.accept(frame.seq)` 登记防重放窗口；
  - 即使该 `frame.seq` 已经存在于防重放窗口（重传包），**ACK 也必然回发**；服务端收到 ACK 后即可成功清理在途记录（`purgeRoomUpdate`），彻底消除了因首个 ACK 丢失导致服务端无休止重传或客户端永久抑制确认的问题。

### 3.2 ACK 确认与 UI 去重分离，乱序与重复版本不回退
- **版本单调控制**：客户端维护 `@Volatile var lastRoomInfoVersion: Long`；
- 收到合法的 `ROOM_UPDATE` 时：
  - 只要身份与信封合法，**传输层 ACK 一律回发**；
  - 仅当 `roomInfo.version > lastRoomInfoVersion` 时，推进 `lastRoomInfoVersion` 并将完整状态分发至 `onRoomUpdate` 监听器；
  - 若 `roomInfo.version <= lastRoomInfoVersion`（旧版本乱序到达或重复重传包），仅回发传输 ACK，**不分发至 UI 监听器，版本绝不倒退**。

### 3.3 完整房间视图跳版本合并，不误作 Game Cursor 缺口
- `ProtocolRoomInfo` 本身是权威房间完整视图；
- 当 `roomInfo.version > lastRoomInfoVersion + 1`（跳版本，例如从 1 直接跃升至 5）时，直接更新版本并通知 UI；
- **移除了旧代码中的 `triggerRoomInfoSelfHeal()` 轮询触发**，不把房间版本跳跃当作对局推送的游标缺口，不发起冗余查询或网络请求。

### 3.4 严格身份验证与外来 roomId 隔离（不给非法身份续命）
- 检查 `boundRoomId`：
  - 若客户端未绑定房间（`boundRoomId == null`，例如在大厅或已离房），直接忽略，**不回发 ACK**；
  - 若推送中的 `roomInfo.roomId != boundRoomId`（外来房间消息），直接忽略，**不回发 ACK，不通知 UI，不更新版本**；
  - 若信封畸形、缺少 `roomInfo` 或字段非法（如非 `ROOM_UPDATE` 类型），直接丢弃，**不回发 ACK**。

### 3.5 换房/离房代次隔离与版本重置（解决低版本新房无法更新问题）
- 引入 `@Volatile var bindingEpoch: Long`：
  - 每次 `createRoom`、`joinRoom`、`resumeSession` 确立新绑定，或 `leaveRoom` 成功脱离房间时，`bindingEpoch++`；
  - 同步重置 `lastRoomInfoVersion = 0L`、`latestRoomInfo = null` 并清空 `earlyRoomUpdateBuffer`；
  - 解决了新加入的房间其 `version`（例如 1）低于此前已离开房间的 `version`（例如 10）时导致新房更新被永久抑制的缺陷。

### 3.6 延迟查询与新房隔离
- `getRoomInfo()` 发起异步请求时捕获当前 `currentEpoch = bindingEpoch` 与 `currentRoomId = boundRoomId`；
- 响应返回时，只有当 `bindingEpoch == currentEpoch && boundRoomId == currentRoomId && info.roomId == currentRoomId` 时，才允许更新 `lastRoomInfoVersion` 和派发给当前监听器；若在查询途中发生换房或退房，旧房延迟响应被安全隔离，绝不污染新房。

### 3.7 早期房间推送缓冲回放 (Early Room Update Buffering)
- 若在 `onRoomUpdate` 监听器注册之前（例如建房/加房握手早期）到达合法的 `ROOM_UPDATE` 推送，消息暂存入 `earlyRoomUpdateBuffer` 并回发 ACK；
- 当表现层或上层注册 `onRoomUpdate(listener)` 时，立即将缓冲中的早期更新按序回放给新监听器，确保不丢失握手期最终状态。

### 3.8 保持既有行为
- **对局推送**：保持 `UdpPacketType.PUSH` 对 `ProtocolPlayerUpdate` 的 `replayGuard.accept`、游标缺口 `PushSequencer`、`RESEND` 与全量快照自愈逻辑不变；
- **连接断开与销毁**：保持 `KICK` 终端 ACK 回发、顶号关闭通知与 2000ms socket linger 延迟释放机制；
- **分片与请求重传**：保持 `Fragmenter` 隔离与 `ReliableRequestor` 指数退避重试。

---

## 4. 模块单元测试覆盖清单

在 `UdpClientBindingTest.kt` 中补齐了 9 项假时钟（`FakeUdpClock` / `FakeUdpScheduler`）模块测试：

1. `testRoomUpdateAckSentAndRetransmissionRecoversLostAck`：验证房间更新推送触发 ACK 回发；首次 ACK 丢失后，服务端重传相同帧到达客户端，ReplayGuard 不拦截并再次回发 ACK，UI 监听器去重仅触发一次；
2. `testLostFinalRoomUpdateRetransmissionRecoversWithoutUserOperation`：验证最后一条配置更新首包丢失后，服务端重发包到达能自主被客户端识别并应用，回发 ACK，不依赖任何用户操作或轮询；
3. `testOutOfOrderOldRoomVersionDoesNotRegress`：验证先收到高版本（version 3）后收到滞后旧版本（version 2）时，旧版本回发 ACK 但版本不倒退，UI 不重复通知；
4. `testVersionJumpMergesWithoutTreatingAsCursorGap`：验证版本从 1 跳跃至 5 时完整全量合并，不误判为游标缺口，不发起 RESEND 或多余查询；
5. `testForeignRoomIdUpdateIgnoredWithoutAck`：验证非本端绑定房间的外来推送被安全丢弃，不回发 ACK，不污染当前房间状态；
6. `testRoomSwitchResetsVersionAndBuffersAndLowerVersionAccepted`：验证离开旧房（version 10）并加入新房（version 2）后版本与代次正确重置，新房较低版本被正常接收并回发新 connectionId 的 ACK；
7. `testEarlyRoomUpdateBufferedAndDeliveredOnSubscription`：验证握手期在 listener 注册前到达的早期房间更新被安全缓冲，并在 listener 注册时按序回放，且已先行回发 ACK；
8. `testDelayedGetRoomInfoQueryDoesNotContaminateNewRoom`：验证在旧房中发起的 `getRoomInfo` 查询延迟回包时，由于代次与 roomId 已变更，不会污染新房的监听器或版本；
9. `testMalformedRoomUpdateEnvelopeRejectedWithoutAck`：验证畸形或非法信封被直接拦截，不回发 ACK，不影响连接健康度。

---

## 5. 未验证项与交接事项 (Unresolved Items)

- **测试未运行**：所有测试由 Anti 通过内置文件工具落盘，Anti 本地未运行任何构建/测试。
- **独立验收用例**：独立用例 `tools/architecture/v4-review/V4RoomDatagramAcceptanceTest.kt`（丢最后配置更新真实 UDP 验收测试）保持只读未修改，等待 Codex 串行执行 Gradle 构建与验收。
- **范围恪守**：未修改 `ServerConnectionSession` 运行时及其他模块，后续由负责的 agent 或小批接续。
