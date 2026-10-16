# T1 交付报告：protocol 帧与握手契约层

- **任务编号**: T1（MJ-TCP/1 线路帧与握手契约层）
- **开发角色**: Anti（仅使用文件工具读写代码与文档；未运行任何 Gradle、git 或 shell 命令；未 commit/push；未调用其他代理；未修改 `tools/architecture/` 下任何文件）
- **基线**: `3a382c6` (分支 `refactor/remove-legacy-cheat-params`, MR-01/MR-02/MR-03 已验收合入并严格保留)
- **验收方**: Claude Code 主会话（独立检视与全量构建/测试验收）
- **交付状态**: **已完成代码与模块测试，等待 Claude Code 主会话检视验收**（不宣称全系统 TCP 迁移通过）

---

## 1. 修改与新增文件清单

### 1.1 生产代码 (protocol 模块)

| 文件路径 | 状态 | 说明 |
|---|---|---|
| `protocol/src/main/kotlin/com/majiang/transport/common/TransportClock.kt` | **新增** | 【契约 5】传输中性时钟 `TransportClock`、任务句柄 `Cancellable`、调度接口 `TransportScheduler` (D10/D11) |
| `protocol/src/main/kotlin/com/majiang/server/api/ApiErrors.kt` | **修改** | 【契约 6】追加 `REQUEST_SCOPE_EXPIRED` 常量并并入 `ApiErrors.ALL` (D3) |
| `protocol/src/main/kotlin/com/majiang/transport/tcp/TcpWire.kt` | **新增** | 【契约 1】`TcpWire` 常量对象（魔数、版本、帧类型 1..10、头大小 12B、载荷 1B..1MiB、暂存 64KiB） |
| `protocol/src/main/kotlin/com/majiang/transport/tcp/TcpCloseReasons.kt` | **新增** | 【契约 6】`TcpCloseReasons` 稳定定义、永久 (PERMANENT) 与临时 (TRANSIENT) 分类与判定 (D4) |
| `protocol/src/main/kotlin/com/majiang/transport/tcp/TcpExceptions.kt` | **新增** | 【D13】`TcpProtocolException` (携带 reasonCode) 与 `TcpConnectionException` (网络/临时类) |
| `protocol/src/main/kotlin/com/majiang/transport/tcp/TcpFrame.kt` | **新增** | 【契约 2/3】`TcpFrame` 实体，重写 `contentEquals` 内容相等性，提供严格 UTF-8 解码方法 |
| `protocol/src/main/kotlin/com/majiang/transport/tcp/TcpFrameEncoder.kt` | **新增** | 【契约 3】`TcpFrameEncoder`，12 字节大端头 + 体，类型/长度/空载荷防御 (UC-F09/F10) |
| `protocol/src/main/kotlin/com/majiang/transport/tcp/TcpFrameDecoder.kt` | **新增** | 【契约 2】`TcpFrameDecoder` 每连接有状态解码器，严格校验顺序、内存有界、终态错误 (UC-F01~F08) |
| `protocol/src/main/kotlin/com/majiang/transport/tcp/TcpMessages.kt` | **新增** | 【契约 4】HELLO/WELCOME/REQ/RSP/PING/PONG/CLOSE DTO、`TcpNonce` 工具、严格 `TcpMessageCodec` (D5~D7) |

### 1.2 单元与模块测试代码 (protocol 模块)

| 测试文件路径 | 测试数 | 覆盖规格 |
|---|---|---|
| `protocol/src/test/kotlin/com/majiang/transport/common/TransportClockTest.kt` | 4 用例 | 契约 5 (D10/D11)：系统时钟单调性、FakeTransportClockAndScheduler 虚拟时间前进、Cancellable 取消、纳秒单调推进 |
| `protocol/src/test/kotlin/com/majiang/transport/tcp/TcpWireTest.kt` | 3 用例 | 契约 1 & 契约 6 (D3/D4)：TcpWire 常量完整性、TcpCloseReasons 分类与全集、ApiErrors.REQUEST_SCOPE_EXPIRED 并入 ALL |
| `protocol/src/test/kotlin/com/majiang/transport/tcp/TcpFrameCodecTest.kt` | 13 用例 | 契约 2 & 契约 3：严格覆盖 UC-F01 (逐字节切分)、UC-F02 (混合半帧)、UC-F03 (单次多帧)、UC-F04 (长度边界 1/1MiB/0/超限/0xFFFFFFFF)、UC-F05 (6 类非法各一例：bad magic, bad wireVersion, bad flags, bad type, bad UTF-8, bad JSON)、UC-F06 (部分 EOF)、UC-F07 (慢速灌入)、UC-F08 (缓冲有界防预分配)、UC-F09 (编码防护)、UC-F10 (控制帧同路) |
| `protocol/src/test/kotlin/com/majiang/transport/tcp/TcpMessagesTest.kt` | 11 用例 | 契约 4 (D5/D6/D7)：HELLO/WELCOME 握手 (UC-H01)、TcpNonce 16 hex 与 PING/PONG 回显 (D5)、REQ 信封与外层 ID 一致性校验及伪造拒绝 (D7)、RSP 信封与 grantedScope (D7)、CLOSE 消息、确定性坏 JSON 语法与未知字段拒绝 (HELLO 与 REQ 信封各一例)、推送更新帧双向转换 |

合计新增模块测试：**31 项**。

---

## 2. 契约 1–6 详细落点核对

### 【契约 1：TcpWire 常量对象】
- **落点**: `protocol/src/main/kotlin/com/majiang/transport/tcp/TcpWire.kt`
- **常量实现**:
  - `MAGIC_ASCII = "MJTC"`, `MAGIC_INT = 0x4D4A5443`, `MAGIC_BYTES = [0x4D, 0x4A, 0x54, 0x43]`
  - `WIRE_VERSION = 1`, `BUSINESS_VERSION = 1`
  - 帧类型表: `FRAME_TYPE_HELLO=1`, `FRAME_TYPE_WELCOME=2`, `FRAME_TYPE_REQ=3`, `FRAME_TYPE_RSP=4`, `FRAME_TYPE_PLAYER_UPDATE=5`, `FRAME_TYPE_PUBLIC_UPDATE=6`, `FRAME_TYPE_ROOM_UPDATE=7`, `FRAME_TYPE_PING=8`, `FRAME_TYPE_PONG=9`, `FRAME_TYPE_CLOSE=10` (`FRAME_TYPES = 1..10`)
  - `HEADER_SIZE = 12`
  - `MIN_PAYLOAD_BYTES = 1`, `MAX_PAYLOAD_BYTES = 1_048_576` (1 MiB)
  - `DECODE_SCRATCH_LIMIT = 64 * 1024` (64 KiB)
  - `FLAGS_DEFAULT = 0`
- **合规**: 所有常量集中于此，禁止任何硬编码散写字面量。

### 【契约 2：有状态帧解码器】
- **落点**: `protocol/src/main/kotlin/com/majiang/transport/tcp/TcpFrameDecoder.kt`
- **实现细节**:
  - 输入支持 `ByteBuffer` 与 `ByteArray` 切片；输出为 `List<TcpFrame>` 或 `TcpDecodeResult`。
  - **校验顺序**:
    1. 魔数 `0x4D4A5443` ("MJTC") -> 失败报 `PROTOCOL_VIOLATION`
    2. `wireVersion == 1` -> 失败报 `VERSION_MISMATCH`
    3. `type in 1..10` -> 失败报 `PROTOCOL_VIOLATION`
    4. `flags == 0` -> 失败报 `PROTOCOL_VIOLATION`
    5. `payloadLength` 用 `(rawInt.toLong() and 0xFFFFFFFFL)` 读取为无符号 Long，判 `1..1,048,576`（严禁先转 Int） -> 失败报 `PROTOCOL_VIOLATION`
  - **终态错误与不找同步**: 遇到六类非法输入立即 `markFatal`，清空所有在组装暂存，`isFatalError = true`，抛出 `TcpProtocolException`；后续输入立即返回空列表，不扫描流寻找同步。
  - **内存有界约束 (UC-F08)**: 头部暂存固定 12 字节；载荷以实际到达的分片列表 (`payloadChunks`) 收集，收到 12 字节头部时**绝不**预分配 1 MiB 载荷内存，直至数据完全到达才组装单帧。
  - **严格 UTF-8 校验**: 解码完成帧载荷时调用 `CharsetDecoder.onMalformedInput(REPORT).onUnmappableCharacter(REPORT)`，坏 UTF-8 判定为 `PROTOCOL_VIOLATION` 终态。
  - **部分 EOF (UC-F06)**: `onEndOfStream()` 检测到半头或半体未完成帧时，释放缓冲并抛出 `TcpProtocolException`，不派发未完整帧。

### 【契约 3：帧编码器】
- **落点**: `protocol/src/main/kotlin/com/majiang/transport/tcp/TcpFrameEncoder.kt`
- **实现细节**:
  - `encode(type, payload, flags = 0)`: 产出 12 字节大端头 + 体。
  - 防御校验：`type !in 1..10`、`payload.isEmpty()`、`payload.size > MAX_PAYLOAD_BYTES`、`flags != 0` 当场抛出 `IllegalArgumentException` (UC-F09)。
  - 控制帧（PING/PONG/CLOSE）与数据帧统一调用此编码器，无旁路机制 (UC-F10)。

### 【契约 4：握手与信封 DTO 及严格编解码】
- **落点**: `protocol/src/main/kotlin/com/majiang/transport/tcp/TcpMessages.kt`
- **DTO 结构与冻结字段 (D5/D6/D7/D9)**:
  - `TcpHello`: `wireVersion`, `businessVersion`, `lobbyResumeToken?`
  - `TcpScope`: `scope`, `expiresAtMillis`
  - `TcpLimits`: `maxInFlightRequests=64`, `requestTimeoutMillis=10000`, `heartbeatIntervalMillis=3000`, `idleDeadMillis=10000`
  - `TcpWelcome`: `wireVersion`, `businessVersion`, `connectionEpoch`, `lobbyResumeToken`, `lobbyScope: TcpScope`, `limits: TcpLimits`
  - `TcpRequestEnvelope`: `requestScope`, `requestId`, `request: ApiRequest`。提供 `isRequestIdConsistent()` 与 `validateRequestIdConsistency()` 入口，外层不一致抛出 `PROTOCOL_VIOLATION`。
  - `TcpResponseEnvelope`: `requestScope`, `requestId`, `response: ApiResponse`, `grantedScope?: TcpScope`
  - `TcpPing` & `TcpPong`: `nonce`。`TcpNonce` 工具严格校验 16 个 hex 字符（8 字节随机数），非法长度或字符判定为 `PROTOCOL_VIOLATION`。
  - `TcpClose`: `reason`, `message?`
- **严格编解码 (`TcpMessageCodec`)**:
  - 沿用 `ApiMessage.kt` 的 `checkUnknownFields`、`validateStrictInt`、`validateStrictLong`、`validateStrictString` 严格模式。
  - 未知字段、类型不符、缺失必填字段、非法 JSON 语法均抛出 `TcpProtocolException(PROTOCOL_VIOLATION)`。
  - 单元测试明确包含 HELLO 与 REQ 信封各一例坏 JSON 语法与未知字段确定性用例。

### 【契约 5：中性时钟 (D10/D11)】
- **落点**: `protocol/src/main/kotlin/com/majiang/transport/common/TransportClock.kt`
- **接口契约**:
  - `fun interface TransportClock`: `currentTimeMillis(): Long`, `nanoTime(): Long = System.nanoTime()`, `SYSTEM` 默认实现。
  - `fun interface Cancellable`: `cancel()`
  - `interface TransportScheduler`: `schedule(delayMillis: Long, task: Runnable): Cancellable`
- **依赖隔离**: TCP 包内代码只依赖 `transport.common` 接口；`UdpClock.kt` 及其全仓现有 273 处引用原地不动（留待 T5 收编）。

### 【契约 6：错误码与 CLOSE 码 (D3/D4)】
- **落点**:
  - `protocol/src/main/kotlin/com/majiang/server/api/ApiErrors.kt`: 追加 `REQUEST_SCOPE_EXPIRED = "REQUEST_SCOPE_EXPIRED"` 并入 `ALL` 集合（集合大小由 16 扩展至 17）。
  - `protocol/src/main/kotlin/com/majiang/transport/tcp/TcpCloseReasons.kt`: 集中定义复用码 (`SERVER_SHUTDOWN`, `ROOM_CLOSED`, `DISPLACED_BY_NEW_CONNECTION`, `LOCAL_CLOSED`, `HEARTBEAT_TIMEOUT`) 与新增码 (`VERSION_MISMATCH`, `PROTOCOL_VIOLATION`, `HANDSHAKE_TIMEOUT`, `SERVER_OVERLOADED`, `SLOW_CONSUMER`)，并划分 `PERMANENT` 与 `TRANSIENT` 集合。

---

## 3. UDP 纯传输测试 → TCP 对应测试映射表初版 (对照设计 §7 删除清单)

依据 `doc/v4-重构/TCP-MIGRATION-DESIGN.md` §7 与检视报告 F15 要求，逐文件列出后续实施 T5 时待删除的 UDP 专用组件及其测试映射：

| 被删 UDP 组件 / 文件 | 纯传输可删断言 (无须替代) | 需 TCP 等价测试及 T1-T3 承载落点 |
|---|---|---|
| `transport/udp/UdpFrame.kt` | 22 字节 UDP 头、CRC32 校验和、`flagZlib`、`flagEncrypted`、MTU 1200B 限制、`seq`/`ackSeq` 报文头字段 | 12 字节 TCP 头、魔数 `MJTC`、`wireVersion`、`flags==0`、1 MiB 载荷上限、大端序编解码 (`TcpFrameCodecTest`, 已在 T1 覆盖) |
| `transport/udp/UdpPacketType.kt` | UDP 专有类型包：`FRAG`、`ACK`、`RESEND`、`PING_PONG` 单包复用 | TCP 帧类型表 1..10：`HELLO`、`WELCOME`、`REQ`、`RSP`、`UPDATE`、独立 `PING`/`PONG`、`CLOSE` (`TcpWireTest`, 已在 T1 覆盖) |
| `transport/udp/UdpProtocol.kt` | UDP 专用常量：`MTU=1200`、`HEADER_SIZE=22`、`RTO=200ms`、`MAX_RESEND_ATTEMPTS=5` | TCP 线路常量：`TcpWire`、`TcpLimits`、超时初值 (`TcpWireTest`、`TcpMessagesTest`, 已在 T1 覆盖) |
| `transport/udp/Fragmenter.kt` | 报文分片切分、分片子头重组、乱序分片拼接、分片超时丢弃 | TCP 内核流式传输无应用层分片；由应用层 12 字节头部+体长度有状态组装承接 (`TcpFrameDecoder` 任意切片测试 UC-F01~F03, 已在 T1 覆盖) |
| `transport/udp/ReplayGuard.kt` | 滑动位图防重放、`seq` 回绕防护、过期 `seq` 丢弃 | TCP 内核保证有序无重报；业务请求幂等由 `(principal, requestScope, requestId)` 回执承接 (T2 `TcpTableServer` 承接) |
| `transport/udp/PushRetransmitWindow.kt` | 服务端 256 条 UDP 数据报网络重传、客户端 `RESEND` 请求响应、滑动窗口滑动 | TCP 传输层保证字节可靠投递；状态中断通过全量快照恢复与 `fromCursor` 重叠去重承接 (T3 `TcpClientBinding` 承接) |
| `transport/udp/ReliableRequestor.kt` | UDP 逐请求 ACK、超时重传定时器、`rspSeq` 映射 | TCP 请求关联与有界 pending Future 超时、业务 FIFO 回执 (T2 `TcpTableServer` 承接) |
| `transport/udp/PushSequencer.kt` | UDP 报文网络乱序排队、补包重排缓冲、丢包触发 RESEND | TCP 单流有序投递；保留客户端视图游标缺口检测并触发全量快照 (T3 承接) |
| `transport/udp/HeartbeatPacer.kt` | UDP 专用单报文携带 PING/PONG、心跳丢包 RTO 探测 | TCP 独立 PING/PONG 帧、16 hex nonce 回显、本地单调时钟 RTT 测量 (T1 D5 已测，T2 心跳循环承接) |
| `transport/udp/UdpClock.kt` | 无（接口本身由 `transport.common` 收编） | 中性时钟 `TransportClock`、`TransportScheduler`、`Cancellable` (已在 T1 `TransportClockTest` 覆盖) |
| `client/.../UdpClient.kt` | UDP DatagramChannel 绑定、网络报文重发循环、UDP 选路 | TCP `SocketChannel` 连接建立、NIO selector 事件循环、TCP_NODELAY (T3 承接) |
| `client/.../UdpClientBinding.kt` | UDP 握手、seq 高水位重绑、网络重传处理 | `TcpClientBinding`：HELLO/WELCOME 握手、断线退避重连、全量恢复对账 (T3 承接) |
| `server/.../UdpTableServer.kt` | UDP 端口监听、客户端 socket 地址路由、未连接报文缓冲 | `TcpTableServer`：`ServerSocketChannel` 监听、单连接状态机、大厅与房间路由 (T2 承接) |
| `server/.../UdpClientSession.kt` | UDP 会话代次与 seq 计数器、UDP 连接关闭通知重发 | TCP 连接对象生命周期、每连接待发队列 256 帧预算、慢客户端 SLOW_CONSUMER 判定 (T2 承接) |
| `server/.../UdpRoom.kt` | 房间内 UDP 座位重传映射 | 共享房间权威订阅，业务基线原子获取与同流 UPDATE 排队 (T2 承接) |
| `ReconnectErrorClassifier` 的 `UdpFrameException` 分支 | `UdpFrameException` 异常分类 | `TcpProtocolException` (根据 reasonCode 分类) 与 `TcpConnectionException` (T3 承接) |

---

## 4. 约束遵守与自查证据

1. **命令与进程约束**:
   - 本会话**未执行任何命令**（包括 `gradlew`, `git`, `shell` 等），所有改动均通过 Antigravity 文件读写工具完成。
   - 未执行 `git commit` 或 `git push`。
   - 未调用任何其他代理（无 subagent 调用）。
2. **所有权与文件边界**:
   - `tools/architecture/` 目录完全只读，未修改其中任何文件。
   - 未修改 `engine`、`client`、`server`、`lwjgl3`、`presentation` 及 `doc/` 下与 T1 无关的任何文件。
   - 修改范围严格限定在任务书授权的 4 个位置：
     - `protocol/.../transport/tcp/` (全部新增文件)
     - `protocol/.../transport/common/` (全部新增文件)
     - `protocol/.../server/api/ApiErrors.kt` (仅按契约 6 追加 `REQUEST_SCOPE_EXPIRED`)
     - `protocol/src/test/kotlin/` 下模块测试
     - 交付与进度文档
3. **既有契约与架构保证**:
   - 保留基线 `3a382c6` 上 MR-01/MR-02/MR-03 的全部行为，未回退任何逻辑。
   - 未改动 `UdpClock.kt` 及其全仓现有 273 处引用，符合 D10 两步迁移策略。
   - 未引入 Netty、WebSocket、虚拟线程或任何新第三方依赖，严格保持 JVM 17 与 Android minSdk 21 兼容。

---

## 5. 遗留项与后续批次衔接

1. **本批验收执行**: 由 Claude Code 主会话接手，运行 `:protocol:test` 全量验证 T1 生产与测试代码，以及全仓 975 项现有回归测试。
2. **T0 剩余测量项**: 最大快照 JSON 载荷测量、网络与邮箱全局预算复核、`GET_REQUEST_SCOPE` 速率限制复核，由 Claude Code 主会话在 T2 验收前执行并关闭。
3. **后续任务 T2**: 在 T1 契约与 DTO 基础上，按 `ANTI-TCP-T2-TASK.txt` 实现 selector 事件循环、每连接发送/接收队列、`TcpTableServer` 路由、业务请求幂等回执与 `requestScope` 存储。

---

## 6. 第一轮返修记录 (2026-10-15)

在 Claude Code 主会话独立检视与运行 `:protocol:compileTestKotlin` 暴露的 3 项问题已严格按返修简报修复完成：

### R1. TcpCloseReasons 分类修正与测试同步
- **修改文件**:
  - `protocol/src/main/kotlin/com/majiang/transport/tcp/TcpCloseReasons.kt`
  - `protocol/src/test/kotlin/com/majiang/transport/tcp/TcpWireTest.kt`
- **修改点与原因**:
  - 原实现中将 `SERVER_SHUTDOWN` 与 `LOCAL_CLOSED` 归入 `TRANSIENT`，与 MR-02 `ReconnectErrorClassifier.PERMANENT_ERROR_CODES` 及设计 §6「保留 MR-02 分类器」不一致（会导致停服后退避重试或主动断开后重连）。
  - 已将 `SERVER_SHUTDOWN` 和 `LOCAL_CLOSED` 移入 `PERMANENT` 集合（`PERMANENT` 由 4 项调整为 6 项，`TRANSIENT` 由 6 项调整为 4 项），同步更新文件头 KDoc 分类说明。
  - 同步更新 `TcpWireTest.kt` 中对应的集合容量与 `isPermanent` / `isTransient` 断言，保留互斥与全集并集契约校验。

### R2. 测试代码：推送/房间 DTO 按真实定义重写
- **修改文件**:
  - `protocol/src/test/kotlin/com/majiang/transport/tcp/TcpMessagesTest.kt`
- **修改点与原因**:
  - 原第 7 节「更新帧」测试用例中使用了猜测的构造字段（如 `events`、`roomId/roomVersion`、`publicSnapshot/publicEvents`、`memberCount/readyCount/status`），与实际 DTO 契约不符导致编译失败。
  - 按真实模型重写用例：
    1. `ProtocolPlayerUpdate` 使用真实字段 `visibleEvents = emptyList()`；
    2. `ProtocolPublicSnapshot` 改用 `FakeServerConnection.createDefaultPublicSnapshot(cursor = 15L)` 构造合法实例；
    3. `ProtocolPublicUpdate` 使用 `snapshot = pubSnapshot, fromCursor = 14L, visibleEvents = emptyList(), isFullSnapshot = false`，严格契合 `snapshot.cursor == fromCursor + 1L` 的增量推送校验；
    4. `ProtocolRoomInfo` 改用 `FakeServerConnection.createDefaultRoomInfo` 构造，断言校验真实字段 `lifecycle` 与 `version`；
    5. 导入并断言 `ProtocolRoomLifecycle.PLAYING`，保持原有 PLAYER/PUBLIC/ROOM 三种更新帧 encode→parse 双向一致的完整验证意图。

### R3. 测试代码：Throwable.message 可空性修复
- **修改文件**:
  - `protocol/src/test/kotlin/com/majiang/transport/tcp/TcpFrameCodecTest.kt`
  - `protocol/src/test/kotlin/com/majiang/transport/tcp/TcpMessagesTest.kt`
- **修改点与原因**:
  - Kotlin 中 `Throwable.message` 为可空类型 `String?`，原单测中直接调用 `.contains(...)` 导致编译失败。
  - 全局搜索并统一修复为安全调用 `ex.message?.contains(...) == true`：
    - `TcpFrameCodecTest.kt`: 修复 2 处（第 175 行 `exUintMax.message`、第 287 行 `ex.message`）；
    - `TcpMessagesTest.kt`: 修复 5 处（第 191 行 `ex3.message`、第 267 行、第 284 行、第 314 行、第 334 行 `ex.message`）。
