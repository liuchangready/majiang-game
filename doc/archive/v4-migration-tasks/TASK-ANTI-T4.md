# 任务 T4：UDP 服务端接入（UdpTableServer）

你是 Anti，负责本任务的纯代码开发。**只用文件读写工具，禁止运行任何命令、构建或测试**——由主会话执行。完成后列出你创建/修改的全部文件路径清单。

必读（按序）：
- `doc/SERVER_CLIENT_API.md` §5（接口）、§7（UDP 协议全部——尤其 §7.4 可靠性、§7.5 会话生命周期、§7.6 参数）
- `protocol/src/main/kotlin/com/majiang/server/api/`（T1 契约：错误码、ServerConnection 语义）
- `protocol/src/main/kotlin/com/majiang/transport/udp/`（T3 协议栈：UdpFrame/UdpPacketType/ReliableRequestor/PushSequencer/PushRetransmitWindow/Fragmenter/HeartbeatPacer/ReplayGuard/UdpClock/UdpScheduler/UdpProtocol）
- `protocol/src/main/kotlin/com/majiang/transport/api/ApiMessage.kt`（V4 消息信封与编解码——只消费，不修改）
- `server/src/main/kotlin/com/majiang/server/ServerRoom.kt`、`TableServer.kt`、`ClientConnectionHandler.kt`（旧 TCP 实现，**只读参考**其房间语义/串行模式/断线清理异步化）
- `server/src/main/kotlin/com/majiang/server/inprocess/`（T2 参考：TableHost 装配方式）
- `engine/src/main/kotlin/com/majiang/host/TableHost.kt`（connect/tick/queryRoundResultEvents 等）

## 目标

UDP 版权威房间服务器：监听 DatagramSocket，按 V4 协议提供全部 15 个接口 + 2 类推送，支持多 client 会话、顶号、掉线托管。

## 文件所有权（独占）

```
server/src/main/kotlin/com/majiang/server/udp/
server/src/test/kotlin/com/majiang/server/udp/
```

其余只读。protocol 模块、engine、client、inprocess 一律不改。

## 交付物

1. **`UdpClientSession`**：每个已连接客户端的状态——connectionId（32bit 随机）、绑定身份 (roomId/seat/token/generation)、双向 `ReplayGuard`、`PushRetransmitWindow`（注入按座位快照供应者 `() -> PlayerSnapshot` 供窗口外全量自愈）、`requestId → 回执` 幂等缓存、最近入站时间。
2. **`UdpRoom`**：房间语义镜像 ServerRoom（只读参考）：8 位随机 roomId、座位分配（preferSeat 被占则顺延）、resumeToken 签发、TableHost 装配（空座 AI 托管策略与 ServerRoom 一致）、开赛校验（房主/人数）、游标推送、`onConnectionClosed` 掉线→按 DisconnectPolicy 托管。
3. **`UdpTableServer`**：
   - `start()`：DatagramSocket 绑定（host/port，port 0=临时端口）、接收线程循环、50ms ticker 线程驱动各房间 `TableHost.tick()`（单房间异常隔离）；
   - 帧分发：REQ（未绑定→大厅接口；已绑定→connectionId 校验失败**静默丢弃**，通过则按 api 标签分发，经 ApiMessageCodec 编解码，JSON 异常回 `MALFORMED_REQUEST` RSP 带 requestId）；PING→PONG（回显 clientMillis + serverMillis）；RESEND→会话窗口处理；PUSH（房间权威更新→按座位脱敏→经 `PushRetransmitWindow` + 必要时 `Fragmenter` 分片发送）；KICK（顶号时**先** KICK 旧 connectionId 再建新绑定）；
   - RESUME_SESSION：校验 token→换发新 connectionId/代次→旧连接失效→返回全量快照 RoomBinding；
   - 入站超时（10s，`UdpProtocol.INBOUND_TIMEOUT_MILLIS`）→判掉线→异步清理（参照 TableServer 的 teardownExecutor 模式，绝不在 TableHost 串行锁内同步进房间锁）；
   - 房间上限 100、已关闭房间回收、`close()` 幂等释放（socket/线程/ticker/全部房间）。
4. **单元/集成测试**（你写文件，主会话跑）：
   - `UdpClock`/`UdpScheduler` 接口在 protocol 主源集，测试内自实现轻量假时钟（不要依赖 protocol 测试源集）；
   - 会话逻辑（幂等缓存、顶号序列、超时判定）用假时钟确定性测试；
   - 真实回环冒烟（127.0.0.1 + 临时端口，原生 DatagramSocket 当客户端）：CREATE_ROOM→LIST_ROOMS→第二 socket JOIN_ROOM→START_MATCH→SNAPSHOT→收 PUSH→RESUME_SESSION 顶号（旧 socket 收 KICK）→LEAVE_ROOM→close 后再 REQ 得 ROOM_NOT_FOUND。

## 陷阱清单

1. **禁止修改 protocol 模块任何文件**（含 ApiMessage.kt、T3 组件）——发现缺口写进交付说明。
2. RSP 依赖 UDP 丢包下的客户端重传覆盖：服务端不做 REQ 重发，但**幂等缓存必须命中重放**（(seat, requestId)）。
3. PUSH 快照通常 >1090B，必须走 Fragmenter；分片 msgId 单调。
4. 收到 KICK 前的旧连接请求：代次不符一律 `INVALID_TOKEN`/静默，绝不能操作新绑定座位。
5. 大厅接口（CREATE_ROOM 等）不带 connectionId（connectionId=0）；绑定后不得再调用（`ALREADY_BOUND`）。
6. TableHost 的串行锁语义：参照 ServerRoom 的 withSerialLock 用法，避免锁反转；房间清理走异步执行器。
7. 时间：server 侧可用真实 `SystemMonotonicClock`（服务端是常驻进程），但判定逻辑要可注入时钟以便测试。
8. 不引入新依赖；JSON 全部经 ApiMessageCodec/ProtocolJson.gson。

## 完成标准

主会话执行 `:server:test` 与全量 `test`，并对真实回环冒烟抽检。除独占路径外无其它改动。
