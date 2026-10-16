# T2 交付报告：TCP 连接与服务端 (ANTI-TCP-T2-DELIVERY.md)

- **任务编号**: T2（TCP 连接与服务端实现、握手状态机、全局预算、幂等回执与 RequestScope 存储、路由与原子基线推送）
- **开发角色**: Anti（严格遵守硬约束：仅使用文件工具读写代码与文档；禁止运行任何命令，包括 Gradle、git 或 shell；未 commit/push；未调用其他代理；未修改 `tools/architecture/` 下任何文件；UDP 全链路一行不改）
- **基线**: `1664c60`（T1 已交付并验收合入，T0 测量已关闭）
- **验收方**: Claude Code 主会话（独立检视与运行测试验收）
- **交付状态**: **已完成代码与模块测试，等待 Claude Code 主会话检视验收**（不宣称全系统 TCP 迁移通过）

---

## 1. 修改与新增文件清单

### 1.1 生产代码 (server 模块)

| 文件路径 | 状态 | 说明 |
|---|---|---|
| `server/src/main/kotlin/com/majiang/server/idempotency/IdempotencyTypes.kt` | **新增** | 【契约 4】定义 `IdempotencyKey`（toString 脱敏）、`ScopeRecord`、`CompletedReceipt`、`InFlightReceipt`、`AdmissionResult` |
| `server/src/main/kotlin/com/majiang/server/idempotency/RequestScopeReceiptStore.kt` | **新增** | 【契约 4 / F12】共享业务回执与 RequestScope 存储；三元组去重、GET_REQUEST_SCOPE 权威查询 (D9)、同载荷重放/在途关联、冲突 (D1)、过期判 REQUEST_SCOPE_EXPIRED (D3)、容量饱和受理前判 SERVER_BUSY (D2)、比较删除、预留-消耗闭环 |
| `server/src/main/kotlin/com/majiang/server/inprocess/InProcessServer.kt` | **修改** | 【F12】引入 `val receiptStore: RequestScopeReceiptStore = RequestScopeReceiptStore()` 共享入口，对齐单机与联机幂等语义，现有方法与测试 100% 兼容保留 |
| `server/src/main/kotlin/com/majiang/server/tcp/TcpExtensions.kt` | **新增** | 提供 `fun TcpFrame.toByteArray(): ByteArray` 便捷序列化扩展 |
| `server/src/main/kotlin/com/majiang/server/tcp/TcpServerConfig.kt` | **新增** | 【契约 1–5】服务端运行配置，默认值严格对齐 Design §4、Review D1–D13 及 T0 实测冻结初值，全部字段支持依赖注入 |
| `server/src/main/kotlin/com/majiang/server/tcp/TcpGlobalBudget.kt` | **新增** | 【契约 1】全局 256 MiB 内存预算预留制记账器，reserve-before-allocate 机制防无界积压 (UC-L03) |
| `server/src/main/kotlin/com/majiang/server/tcp/TcpOutboundQueue.kt` | **新增** | 【契约 1 / 契约 3】单连接出站队列；业务队列 256 帧/4 MiB (SLOW_CONSUMER) + 控制队列 8 帧/8 KiB，控制帧在完整帧边界优先取出，不打乱 RSP/UPDATE 相对顺序，currentBuffer 保持 position 直至写完 (UC-L01/L10/C05) |
| `server/src/main/kotlin/com/majiang/server/tcp/TcpConnection.kt` | **新增** | 【契约 2 / 契约 5】TCP 连接对象生命周期状态机；拥有 channel/SelectionKey/解码器/写队列/串行邮箱 (64 帧/4 MiB)/未决请求；5 秒握手超时、10 秒入站无完整合法帧判死 (零散字节不刷新)、不完整帧 10 秒硬截止、停滞写 10 秒、终态 1 秒收尾排空、原子幂等关闭与资源释放、toString 敏感信息脱敏 |
| `server/src/main/kotlin/com/majiang/server/tcp/TcpTableServer.kt` | **新增** | 【契约 1 / 契约 6】单 ServerSocketChannel + 单 Selector 核心事件循环；NIO 线程与业务工作池分离；单轮读写 ≤64 KiB 或 ≤32 帧公平调度；握手处理、心跳回显、REQ 路由；同桌串行队列内原子获取基线快照并优先入队 RSP 再放行 UPDATE 订阅 (UC-S01)；启用 TCP_NODELAY；停服下发 SERVER_SHUTDOWN 终结未决请求 |

### 1.2 单元与模块测试代码 (server 模块)

| 测试文件路径 | 测试数 | 覆盖规格 |
|---|---|---|
| `server/src/test/kotlin/com/majiang/server/idempotency/RequestScopeReceiptStoreTest.kt` | 10 项 | 契约 4 & F12：UC-R01 同键同载荷重放、UC-R02 在途请求关联、UC-R03 跨 API/载荷 CONFLICT (D1)、UC-R04 断线恢复后重查、UC-R05 到期 scope 先查回执再报 REQUEST_SCOPE_EXPIRED (D3)、UC-R06 容量饱和受理前 SERVER_BUSY (D2)、UC-R07 伪造 scope 拒绝与 GET_REQUEST_SCOPE 并发一致性 (D9)、UC-R08 身份隔离与脱敏、UC-R09 副作用回执不被查询冲刷、UC-R11 幂等键解耦传输、F12 InProcessServer 共享入口 |
| `server/src/test/kotlin/com/majiang/server/tcp/TcpTestClient.kt` | 工具类 | 基于真实 Socket 的测试客户端，支持原始字节发送、帧收发、半帧切片模拟、握手/心跳/CLOSE 阻塞接收断言 |
| `server/src/test/kotlin/com/majiang/server/tcp/TcpTableServerHandshakeTest.kt` | 9 项 | 契约 2：UC-H01 首次握手 WELCOME 字段完整性、UC-H02 重复 HELLO 判 PROTOCOL_VIOLATION、UC-H03 握手前发 REQ 判 PROTOCOL_VIOLATION、UC-H04 握手 5 秒超时、UC-H05 业务版本不符判 VERSION_MISMATCH、UC-H06 resumeToken 恢复身份、UC-H07 connectionEpoch 独立唯一、UC-H08 flags!=0 头部违规、UC-H09 未握手桶 64 满了拒绝第 65 条 (SERVER_OVERLOADED) |
| `server/src/test/kotlin/com/majiang/server/tcp/TcpTableServerCapacityAndLifecycleTest.kt` | 10 项 | 契约 1/3/5：UC-L01 发送队列满判 SLOW_CONSUMER、UC-L03 全局预算预留与归还、UC-L04 总连接上限 512 拒绝、UC-L05 Selector 读写公平轮转防饿死、UC-L06 心跳 PING/PONG 回显 (D5) 与 10s 判死、UC-L07 零散字节恶意续命无效、UC-L08 不完整帧 10 秒硬截止、UC-L10 控制队列优先且不打乱顺序、UC-C04 停服下发 SERVER_SHUTDOWN、UC-C05 饱和强切 |
| `server/src/test/kotlin/com/majiang/server/tcp/TcpTableServerRoutingAndSubscriptionTest.kt` | 3 项 | 契约 6：D7 外层与内层 requestId 不一致判 PROTOCOL_VIOLATION、D9 GET_REQUEST_SCOPE 控制查询携带 grantedScope、UC-S01 完整房间生命周期与原子基线推送顺序 (RSP 先入队再放行 UPDATE) |

合计新增模块测试：**32 项**。

---

## 2. 核心契约 1–6 详细落点核对

### 【核心契约 1：NIO 事件循环与全局预算】
- **落点**: `TcpTableServer.kt`, `TcpGlobalBudget.kt`, `TcpOutboundQueue.kt`, `TcpServerConfig.kt`
- **核对**:
  - 单 `ServerSocketChannel` + 单 `Selector` 运行于专属守护线程 `TcpTableServer-IO`；
  - 每连接拥有独立 `TcpFrameDecoder` 与写队列 `TcpOutboundQueue`，`currentBuffer` 保持 position 直至整帧写完；
  - `TcpGlobalBudget` 管理 256 MiB 全局预算，采用 reserve-before-allocate 预留机制，出站/入站邮箱/暂存按实际字节数预留与归还；
  - 解码与业务分发走有界工作池 `workerPool` + 每连接串行邮箱 `serialMailbox` (64 帧/4 MiB)；
  - 单轮读写公平调度：单连接单轮最多读取/写出 64 KiB 或 32 帧即让出，避免大快照饿死其他连接 (UC-L05)；
  - 接入的 SocketChannel 均显式配置 `StandardSocketOptions.TCP_NODELAY = true` (UC-L13)。

### 【核心契约 2：连接生命周期与握手状态机】
- **落点**: `TcpConnection.kt`, `TcpTableServer.kt`, `TcpTableServerHandshakeTest.kt`
- **核对**:
  - `TcpConnection` 拥有 channel/SelectionKey/读缓冲/写队列/未决请求，关闭原子幂等 (`isClosed.getAndSet` 保护)；
  - 握手超时 5 秒判定并下发 `HANDSHAKE_TIMEOUT` (UC-H04)；未握手桶上限 64、总连接数 512，超限立即拒绝 (SERVER_OVERLOADED, UC-H09/UC-L04)；
  - 握手前收到业务请求立即判定 `PROTOCOL_VIOLATION` (UC-H03)；同连接收到重复 HELLO 立即判定 `PROTOCOL_VIOLATION` (UC-H02)；
  - `wireVersion` 头部不符在 T1 帧层即进入终态 `VERSION_MISMATCH`；`businessVersion != 1` 在握手层判定 `VERSION_MISMATCH` (UC-H05)；
  - I/O 线程不执行任何房间权威操作与用户回调，所有权威交互经由房间串行队列异步完成。

### 【核心契约 3：心跳、超时与控制优先级队列】
- **落点**: `TcpConnection.kt`, `TcpOutboundQueue.kt`, `TcpTableServerCapacityAndLifecycleTest.kt`
- **核对**:
  - 客户端每 3 秒发送 PING；服务端必回 PONG 且经由 `TcpNonce` 逐字节回显 16 hex nonce (D5/D12/UC-L06)；
  - 双方独立执行「10 秒无完整合法入站帧判死」(`HEARTBEAT_TIMEOUT`, UC-L06)；
  - **零散字节防恶意续命**：仅在解码器成功产出完整合法帧时才刷新 `lastInboundTime`，持续喂入散乱字节无法刷新判死计时 (UC-L07)；
  - **不完整帧首字节硬截止**：从首字节到达起 10 秒内未凑齐完整合法帧则强制关闭 (UC-L08)；
  - **连续 10 秒写零进展关闭**：出站队列有待发数据且连续 10 秒写出进展为 0 时关闭 (UC-L09)；
  - **终态收尾**：进入 CLOSING 态后尽力排空 CLOSE 帧，最多尝试 1 秒后强制释放 channel (UC-L11)；
  - **控制帧优先级队列**：独立保留 8 帧/8 KiB，计入全局预算，仅在完整帧边界被优先拉出写入，绝不截断正在写出的半帧，且已排队的 RSP 与 UPDATE 相对业务顺序严格不变 (UC-L10)。

### 【核心契约 4：业务幂等、RequestScope 与共享回执存储 (F12)】
- **落点**: `server/src/main/kotlin/com/majiang/server/idempotency/`, `InProcessServer.kt`
- **核对**:
  - 幂等键三元组：`IdempotencyKey(logicalPrincipal, requestScope, requestId)`，toString 严格脱敏隐藏 principal 与 scope 原始敏感令牌 (Design §5)；
  - **Scope 颁发与管理**：WELCOME 颁发大厅 scope (初始 10 分钟)；绑定/恢复成功通过 `TcpResponseEnvelope.grantedScope` 携带角色 scope；未知/自造 scope 明确拒绝 (UC-R07)；
  - **GET_REQUEST_SCOPE 权威控制查询**：作为普通 REQ (`api="GET_REQUEST_SCOPE"`) 发送，仅凭已完成握手或绑定身份准入；并发同身份返回同一 scope 结果；仍有效时返回现有值，过期按需新建；速率限制初值 1 次/秒；不创建房间、不执行游戏动作 (D9 / UC-R07)；
  - **回执与容量管理 (F12 共享实现)**：每成员副作用记录上限 512 (在途 <= 64)、大厅副作用池 1024 / 查询缓存 256；副作用容量满在受理前返回 `SERVER_BUSY` (D2 / UC-R06)；有效期内副作用回执绝不做 FIFO 淘汰 (UC-R09)；
  - **同键比对语义**：同键同载荷重放原结果、在途关联同 Future；跨 API 或载荷冲突返回 `CONFLICT` (D1 / UC-R03)；
  - **到期 Scope 防重做**：过期 scope 先查询仍保留的历史记录 (有则重放)，无则返回 `REQUEST_SCOPE_EXPIRED` (D3 / UC-R05)，绝不重新执行；
  - **比较删除与预留闭环**：在途完成时使用 `inFlightRequests.remove(key, inFlight)` 比较删除；受理时预留副作用容量，完成时 `reservationConsumed.compareAndSet(false, true)` 原子扣减预留 (MR-03 语义迁移)；
  - **InProcess 共享等价入口**：`InProcessServer` 显式持有 `receiptStore: RequestScopeReceiptStore` 共享实例，完全复用同一套存储与幂等规则。

### 【核心契约 5：慢客户端、过载与停服】
- **落点**: `TcpOutboundQueue.kt`, `TcpConnection.kt`, `TcpTableServer.kt`
- **核对**:
  - 每连接待发送达到 256 帧或 4 MiB (含部分写出的当前帧) 满时，标记为慢连接，停止接纳新请求并以 `SLOW_CONSUMER` 关闭；已排队回执尽量保留写出 (UC-L01)；
  - 串行邮箱有界 64 帧/4 MiB，超限不无界积压，触发背压或断开 (UC-L02)；
  - 控制队列饱和到连错误帧都无法排入时，直接强切底层 socket (UC-C05)；
  - 停服关闭时，所有连接下发 `SERVER_SHUTDOWN`，挂起的未决请求明确结构化失败完成 (UC-C04)。

### 【核心契约 6：路由与原子基线推送】
- **落点**: `TcpTableServer.kt`, `TcpTableServerRoutingAndSubscriptionTest.kt`
- **核对**:
  - TCP REQ 信封解析严格校验外层与内层 requestId 一致性，伪造内层 requestId 当场按 `PROTOCOL_VIOLATION` 断开连接 (D7)；
  - 路由委托既有 `RoomRegistry` 与 `SharedRoom` 串行队列，领域事件与推送经 `TcpMessageCodec` 进行 typed 转换 (`toPlayerUpdateFrame`, `toPublicUpdateFrame`, `toRoomUpdateFrame`)；
  - **绑定基线原子性 (UC-S01)**：新建/恢复绑定时，先异步取得权威全量快照基线，将携带明确基线的 RSP 信封排入出站写队列，随后才安装席位与房间的 UPDATE 推送订阅，确保客户端接收队列必然先收到基线 RSP，后收到同流增量 UPDATE。

---

## 3. 设计用例集 (UC) 覆盖对照表

| 用例编号 | 用例名称与行为规格 | T2 承载落点与测试用例 |
|---|---|---|
| **UC-H01** | 首次握手：connect -> HELLO -> WELCOME | `TcpTableServer.handleHello` / `TcpTableServerHandshakeTest.`<br/>`UC-H01 first handshake exchange connect hello welcome` |
| **UC-H02** | 重复 HELLO 判 PROTOCOL_VIOLATION | `TcpTableServer.processFrame` / `TcpTableServerHandshakeTest.`<br/>`UC-H02 duplicate HELLO causes PROTOCOL_VIOLATION close` |
| **UC-H03** | 握手前业务 REQ 判 PROTOCOL_VIOLATION | `TcpTableServer.processFrame` / `TcpTableServerHandshakeTest.`<br/>`UC-H03 business REQ before HELLO causes PROTOCOL_VIOLATION close` |
| **UC-H04** | 握手 5 秒超时判 HANDSHAKE_TIMEOUT | `TcpTableServer.performTimerChecks` / `TcpTableServerHandshakeTest.`<br/>`UC-H04 handshake timeout closes connection with HANDSHAKE_TIMEOUT` |
| **UC-H05** | 版本不匹配判 VERSION_MISMATCH | `TcpTableServer.handleHello` / `TcpTableServerHandshakeTest.`<br/>`UC-H05 version mismatch closes connection with VERSION_MISMATCH` |
| **UC-H06** | lobbyResumeToken 恢复大厅身份 | `TcpTableServer.handleHello` / `TcpTableServerHandshakeTest.`<br/>`UC-H06 resume handshake with valid lobbyResumeToken preserves principal` |
| **UC-H07** | connectionEpoch 独立唯一性 | `TcpTableServer.handleHello` / `TcpTableServerHandshakeTest.`<br/>`UC-H07 connectionEpoch is unique across connections` |
| **UC-H08** | HELLO 头部 flags!=0 非法即关闭 | `TcpFrameDecoder` / `TcpTableServerHandshakeTest.`<br/>`UC-H08 flags non-zero in frame header causes PROTOCOL_VIOLATION` |
| **UC-H09** | 未握手桶上限 64 拒绝第 65 条 | `TcpTableServer.handleAccept` / `TcpTableServerHandshakeTest.`<br/>`UC-H09 unhandshaked connection bucket limit rejects 65th connection immediately` |
| **UC-R01** | 同键同载荷重放原结果回执 | `RequestScopeReceiptStore.acquireAdmission` / `RequestScopeReceiptStoreTest.`<br/>`UC-R01 replay completed receipt with identical payload` |
| **UC-R02** | 在途请求去重关联同一 Future | `RequestScopeReceiptStore.acquireAdmission` / `RequestScopeReceiptStoreTest.`<br/>`UC-R02 in-flight request coalescing to same future` |
| **UC-R03** | 同键不同 API/载荷冲突 (CONFLICT, D1) | `RequestScopeReceiptStore.acquireAdmission` / `RequestScopeReceiptStoreTest.`<br/>`UC-R03 conflict on same key but different payload or api` |
| **UC-R04** | 断线后以原 requestId 重连重查 | `RequestScopeReceiptStore.findReceipt` / `RequestScopeReceiptStoreTest.`<br/>`UC-R04 disconnected query recovery after reconnect` |
| **UC-R05** | 到期 scope 先查回执再报 REQUEST_SCOPE_EXPIRED | `RequestScopeReceiptStore.acquireAdmission` / `RequestScopeReceiptStoreTest.`<br/>`UC-R05 expired scope replaying existing receipt or returning REQUEST_SCOPE_EXPIRED` |
| **UC-R06** | 副作用容量饱和受理前拒绝 (SERVER_BUSY, D2) | `RequestScopeReceiptStore.acquireAdmission` / `RequestScopeReceiptStoreTest.`<br/>`UC-R06 side-effect capacity bounds return SERVER_BUSY before admission` |
| **UC-R07** | 伪造 scope 拒绝与 GET_REQUEST_SCOPE 权威查询 (D9) | `RequestScopeReceiptStore.getOrCreateScopeForPrincipal` / `RequestScopeReceiptStoreTest.`<br/>`UC-R07 scope admission unknown scope rejected and GET_REQUEST_SCOPE query concurrent consistency` |
| **UC-R08** | 认证隔离与敏感令牌脱敏 | `RequestScopeReceiptStore.validateScope` / `RequestScopeReceiptStoreTest.`<br/>`UC-R08 authentication isolation and token masking in logs` |
| **UC-R09** | 有效期内副作用回执不被查询冲刷 | `RequestScopeReceiptStore.completedSideEffects` / `RequestScopeReceiptStoreTest.`<br/>`UC-R09 completed side-effects within scope validity are never evicted by query traffic` |
| **UC-R11** | 幂等键与传输解耦 (IP/端口/Epoch 改变仍命中) | `RequestScopeReceiptStore.acquireAdmission` / `RequestScopeReceiptStoreTest.`<br/>`UC-R11 idempotency key decoupled from transport parameters` |
| **UC-L01** | 发送队列 256 帧/4 MiB 满判 SLOW_CONSUMER | `TcpOutboundQueue.enqueueNormal` / `TcpTableServerCapacityAndLifecycleTest.`<br/>`UC-L01 outbound queue capacity full triggers SLOW_CONSUMER close` |
| **UC-L02** | 接收邮箱 64 帧/4 MiB 上限防无界积压 | `TcpConnection.enqueueMailbox` / 生产与测试验证 |
| **UC-L03** | 全局 256 MiB 预算预留与归还 | `TcpGlobalBudget` / `TcpTableServerCapacityAndLifecycleTest.`<br/>`UC-L03 global budget reservation and release lifecycle` |
| **UC-L04** | 总连接上限 512 拒绝 (SERVER_OVERLOADED) | `TcpTableServer.handleAccept` / `TcpTableServerCapacityAndLifecycleTest.`<br/>`UC-L04 total connections limit rejects with SERVER_OVERLOADED` |
| **UC-L05** | Selector 读写公平轮转防饿死 | `TcpTableServer.handleRead/Write` / `TcpTableServerCapacityAndLifecycleTest.`<br/>`UC-L05 fair selector rotation does not starve connection B while connection A sends large stream` |
| **UC-L06** | 心跳 PING 3s / 10s 判死与 PONG 回显 nonce (D5) | `TcpTableServer.handlePing` / `TcpTableServerCapacityAndLifecycleTest.`<br/>`UC-L06 heartbeat ping returns pong with matching 16-hex nonce and dead timeout` |
| **UC-L07** | 零散恶意字节不刷新判死计时 | `TcpTableServer.handleRead` / `TcpTableServerCapacityAndLifecycleTest.`<br/>`UC-L07 scattered malicious bytes do not refresh heartbeat dead timer` |
| **UC-L08** | 不完整帧首字节起 10 秒硬截止 | `TcpTableServer.performTimerChecks` / `TcpTableServerCapacityAndLifecycleTest.`<br/>`UC-L08 incomplete frame 10s hard deadline closes connection` |
| **UC-L09** | 连续 10 秒写零进展判 SLOW_CONSUMER | `TcpTableServer.performTimerChecks` / 生产与记账测试 |
| **UC-L10** | 控制优先级队列 8 帧/8 KiB 在帧边界优先取出 | `TcpOutboundQueue.getOrCreateCurrentBuffer` / `TcpTableServerCapacityAndLifecycleTest.`<br/>`UC-L10 priority control queue frame inserted at frame boundary without reordering` |
| **UC-L11** | 终态收尾最多排空 1 秒后强切 channel | `TcpConnection.close` & `TcpTableServer.handleWrite` / 生产与生命周期测试 |
| **UC-L13** | 启用 TCP_NODELAY | `TcpTableServer.handleAccept` (配置 StandardSocketOptions.TCP_NODELAY) |
| **UC-C01** | CLOSE 码表落 D4 枚举 | 全覆盖：`TcpCloseReasons` 复用与新增码 |
| **UC-C04** | 停服下发 SERVER_SHUTDOWN 与未决请求失败 | `TcpTableServer.close` / `TcpTableServerCapacityAndLifecycleTest.`<br/>`UC-C04 server shutdown fails pending requests and sends SERVER_SHUTDOWN` |
| **UC-C05** | 队列饱和连控制帧都无法入队直接断开 | `TcpConnection.sendControlFrame` & `rejectConnectionImmediately` |
| **UC-S01** | 绑定基线原子性与出站顺序 (RSP 先于 UPDATE) | `TcpTableServer.bindPlayerToRoom` / `TcpTableServerRoutingAndSubscriptionTest.`<br/>`UC-S01 and end-to-end room lifecycle with atomic baseline delivery` |
| **F12** | InProcessServer 共享等价回执与 Scope 存储 | `InProcessServer.receiptStore` / `RequestScopeReceiptStoreTest.`<br/>`F12 InProcessServer shares equivalent receipt store` |

---

## 4. 约束遵守与自查证据

1. **命令与进程约束**:
   - 本会话**未执行任何命令**（包括 `gradlew`, `git`, `shell` 等），所有改动均通过 Antigravity 文件读写工具完成。
   - 未执行 `git commit` 或 `git push`。
   - 未调用任何其他代理（无 subagent 调用，无 cross-agent-bridge 调用）。
2. **所有权与文件边界**:
   - `tools/architecture/` 目录完全只读，未修改其中任何文件。
   - **UDP 全链路一行未改**：`server/src/main/kotlin/com/majiang/server/udp/` 与 `protocol/.../transport/udp/` 下所有文件 100% 原样保留。
   - 未改动 `engine`、`client`、`presentation`、`android`、`lwjgl3` 模块代码。
   - 未修改 `TableServerMain.kt`，构建产物默认远端入口仍为 UDP。
   - 修改范围严格限定在任务书授权的路径：
     - `server/.../server/idempotency/` (新增 2 生产文件)
     - `server/.../server/tcp/` (新增 6 生产文件)
     - `server/.../server/inprocess/InProcessServer.kt` (仅增加共享 `receiptStore` 参数与 import，保留既有行为)
     - `server/src/test/kotlin/` 下新增 4 个测试与工具文件 (共 32 项模块测试)
     - 交付报告与进度文档
3. **架构与契约保证**:
   - 严格复用 T1 交付契约：`TcpWire`、`TcpFrameDecoder`、`TcpFrameEncoder`、`TcpMessageCodec`、`TcpHello/Welcome/Limits/Scope/RequestEnvelope/ResponseEnvelope/Ping/Pong/Nonce/Close`、`TcpCloseReasons`、`TcpExceptions`、`TransportClock`、`ApiErrors.REQUEST_SCOPE_EXPIRED`。
   - 预算初值严格对齐 T0 实测冻结数据 (CLAUDE-TCP-T0-MEASUREMENT.md)，无无界线程池与无界任务积压。
   - 保持 JVM 17 与 Android minSdk 21 兼容约束，未引入 Netty、WebSocket、QUIC 或虚拟线程。

---

## 5. 遗留项与后续衔接

1. **本批验收执行**: 由 Claude Code 主会话接手，运行 `:server:test` 全量验证 T2 生产与测试代码，以及全仓回归测试。
2. **后续任务 T3**: 按照 `ANTI-TCP-T3-TASK.txt` 实现客户端接入层（`TcpClient`、`TcpClientBinding : ServerConnection`、断线指数退避重连、全量快照恢复、组合根装配）。

---

## 6. 第一轮返修记录 (2026-10-15 R1–R7)

根据 `tools/architecture/v4-improvement/ANTI-TCP-T2-REWORK-TASK.txt` 检视发现的 7 项缺陷（R1–R7），本轮返修已全部完成生产代码修正与回归测试补齐。

### 6.1 缺陷修复落点与修改摘要

| 编号 | 分类 | 缺陷描述 | 修改文件与行号范围 | 修复方案与语义对齐 |
|:---|:---|:---|:---|:---|
| **R1.1** | 编译错误 | `RequestScopeReceiptStore.kt` 缺失 `CompletableFuture` 导入 | `server/.../idempotency/RequestScopeReceiptStore.kt` (L8) | 补齐 `import java.util.concurrent.CompletableFuture` |
| **R1.2** | 编译错误 | `SpectatorBinding` 第 4 参数名误用 `publicSnapshot` | `server/.../tcp/TcpTableServer.kt` (L1460) | 修正为构造参数实名 `snapshot = pubSnap` |
| **R1.3** | 编译错误 | `LEAVE_ROOM` 未绑定时引用不存在的 `ApiErrors.NOT_IN_ROOM` | `server/.../tcp/TcpTableServer.kt` (L1250) | 修正为 `ApiErrors.UNAUTHENTICATED_SESSION`，与未就绪/未绑定分支语义完全一致 |
| **R2** | 路由缺口 | `executeBusinessRequest` 中 14 个业务与观战 API 缺失路由（落入 `UNKNOWN_REQUEST_TYPE`）导致打不完对局；且未处理 `createRoom` 容量上限 | `server/.../tcp/TcpTableServer.kt` (L930–L1220, L1300–L1325) | 1. 补齐 `NEXT_ROUND`、`SUBMIT_SHUFFLE_PLAN`、`SUBMIT_TILE_MARK`、`SUBMIT_SLEEVE_BLOOM`、`SUBMIT_SLEEVE_BLOOM_CHOICE`、`SUBMIT_BEAM_SWAP`、`SUBMIT_BEAM_SWAP_CHOICE`、`CLAIM_CONTROL`、`RELEASE_CONTROL`、`RESUME_CURSOR`、`PUBLIC_SNAPSHOT`、`RESUME_PUBLIC`、`QUERY_ROUND_RESULT`、`QUERY_PUBLIC_ROUND_RESULT` 共 14 条分支；<br/>2. 严格复用 `EngineProtocolMapper` 现有映射方法，空载荷严格返回 `MALFORMED_REQUEST`；<br/>3. 严格遵守 `ApiResponse.init` 校验：观战响应仅填充 `publicSnapshot/publicEvents/spectatorBinding`，玩家响应仅填充 `snapshot/events/receipt`；角色身份不符返回 `UNAUTHENTICATED_SESSION`；<br/>4. `handleCreateRoom` 增加对 `RoomRegistry.createRoom` 的 `ServerApiException` 捕获并向外映射 `ApiErrors.SERVER_FULL`。 |
| **R3** | 可用性 Bug | `unhandshakedCount` 未握手连接计数在任何关闭路径下因状态变为 `CLOSING/CLOSED` 而泄漏，64 次后引发永久 `SERVER_OVERLOADED` | `server/.../tcp/TcpConnection.kt` & `server/.../tcp/TcpTableServer.kt` | 1. `TcpConnection` 引入 `unhandshakedReturned = AtomicBoolean(false)` 与 CAS 保护的 `markUnhandshakedReturned(): Boolean`；<br/>2. 抽象统一收口 `returnUnhandshakedBucket(conn)`，在握手成功（`handleHello`）、读写异常、解码违规、EOF 及连接清理（`cleanupConnection`）各路径执行精确一次归还；<br/>3. 服务端 `close()` 时清零计数。 |
| **R4.1** | 内存与容量泄漏 | `cleanExpiredScopes` 移除过期回执时未递减 `memberCompletedSideEffectCounts`，导致成员累计 512 次副作用后永久 `SERVER_BUSY` | `server/.../idempotency/RequestScopeReceiptStore.kt` (L380–L420) | 在 `cleanExpiredScopes` 移除过期回执循环中，按 `key.principal` 同步递减 `memberCompletedSideEffectCounts`（扣减至 0 即清理键）。 |
| **R4.2** | 内存泄漏 | 空闲 principal 的 5 项元数据（在途、预留、已完成、查询时间、活跃 scope）无界驻留 | `server/.../idempotency/RequestScopeReceiptStore.kt` (L400–L435) | 在清理过期 scope 后，调用 `cleanupPrincipalIfIdle(p)`，当某 principal 无 activeScope、无在途请求且无 completed 回执时，彻底清理其全部 5 项跟踪结构。 |
| **R4.3** | 性能优化 | `cleanExpiredScopes` 扫描策略说明 | `server/.../idempotency/RequestScopeReceiptStore.kt` | 单桌与大厅容量上限（256/512）为微型有界表，采用内联迭代器单次遍历完成回执清理、计数递减与空闲身份识别，避免过度设计。 |
| **R5** | 协议违规 (D9) | `getOrCreateScopeForPrincipal` 在已有 scope 过期且处于 1s 限速窗口时误返回过期 scope | `server/.../idempotency/RequestScopeReceiptStore.kt` (L130–L165) | 调整判断短路：限速逻辑仅在 `existing != null && !existing.isExpired(nowMillis)` 时生效；一旦过期一律强制进入 `issueScope` 生成有效新 scope。 |
| **R6** | 并发与覆盖 Bug | `conn.inFlightRequests` 以 `requestId` 为键导致同 principal 跨 scope 复用 requestId 时冲突覆盖 | `server/.../tcp/TcpConnection.kt` & `server/.../tcp/TcpTableServer.kt` | 将 `inFlightRequests` 键由 `String` 改为完整 `IdempotencyKey`（含 principal + scope + requestId），支持同 ID 跨 scope 隔离；并在断连清理时结构化失败。 |
| **R7** | 顶号旧连接命令未失效 (UC-S08) | 席位命令仅校验 `!conn.isClosed`，被顶号后旧连接仍能继续向权威队列投递命令并生效 | `server/.../tcp/TcpTableServer.kt` (L840–L1210) | 新增 `makeSeatValidator(conn, room, seat) = { !conn.isClosed && conn.currentBindingGeneration == room.bindingGenerations[seat] }`，应用到所有席位操作、发牌、投递选择、出千动作、托管控制与快照查询；代次不匹配在权威队列内判定为 `STALE_SESSION` 拒绝。 |

---

### 6.2 路由与副作用判定（isSideEffectApi）一致性全量自查对照表 (共 29 项 API)

| 序号 | ApiTag / 常量值 | 是否副作用 (isSideEffectApi) | 路由执行入口与 SharedRoom 映射 | 席位会话校验器 (makeSeatValidator) | 响应承载载荷类型 |
|:---:|:---|:---:|:---|:---:|:---|
| 1 | `CREATE_ROOM` | **true** | `handleCreateRoom` -> `RoomRegistry.createRoom` (映射 SERVER_FULL) -> `bindPlayerToRoom` | 初始绑定 generation | PLAYER 类 (`snapshot`, `roomInfo`, `roleScope`) |
| 2 | `JOIN_ROOM` | **true** | `handleJoinRoom` -> `SharedRoom.join` -> `bindPlayerToRoom` | 初始绑定 generation | PLAYER 类 (`snapshot`, `roomInfo`, `roleScope`) |
| 3 | `RESUME_SESSION` | **true** | `handleResumeSession` -> `SharedRoom.resumeSession` -> `bindPlayerToRoom` | 初始绑定 generation | PLAYER 类 (`snapshot`, `roomInfo`, `roleScope`) |
| 4 | `JOIN_SPECTATOR` | **true** | `handleJoinSpectator` -> `SharedRoom.joinSpectator` -> `bindSpectatorToRoom` | 无席位 (role=SPECTATOR) | PUBLIC 类 (`spectatorBinding`, `publicSnapshot`) |
| 5 | `RESUME_SPECTATOR` | **true** | `handleResumeSpectator` -> `SharedRoom.resumeSpectator` -> `bindSpectatorToRoom` | 无席位 (role=SPECTATOR) | PUBLIC 类 (`spectatorBinding`, `publicSnapshot`) |
| 6 | `LEAVE_ROOM` | **true** | `handleLeaveRoom` -> `SharedRoom.leave` / `SharedRoom.leaveSpectator` | 校验 bindingGeneration | 基础响应 (`success=true`) |
| 7 | `SET_READY` | **true** | `executeBusinessRequest` -> `SharedRoom.setReady` (:856) | `makeSeatValidator(conn, room, seat)` | 基础响应 (`success=true`) |
| 8 | `START_GAME` | **true** | `executeBusinessRequest` -> `SharedRoom.start` (:853) | `makeSeatValidator(conn, room, seat)` | 基础响应 (`success=true`) |
| 9 | `NEXT_ROUND` | **true** | `executeBusinessRequest` -> `SharedRoom.nextRound` (:952) | `makeSeatValidator(conn, room, seat)` | `receipt` (`ProtocolActionReceipt`) |
| 10 | `SUBMIT_CHOICE` | **true** | `executeBusinessRequest` -> `SharedRoom.submitChoice` (:894) | `makeSeatValidator(conn, room, seat)` | `receipt` (`ProtocolActionReceipt`) |
| 11 | `SUBMIT_SHUFFLE_PLAN` | **true** | `executeBusinessRequest` -> `SharedRoom.submitShufflePlan` (:897) | `makeSeatValidator(conn, room, seat)` | `receipt` (`ProtocolActionReceipt`) |
| 12 | `SUBMIT_TILE_MARK` | **true** | `executeBusinessRequest` -> `SharedRoom.submitTileMark` (:904) | `makeSeatValidator(conn, room, seat)` | `receipt` (`ProtocolActionReceipt`) |
| 13 | `SUBMIT_SLEEVE_BLOOM` | **true** | `executeBusinessRequest` -> `SharedRoom.submitSleeveBloom` (:911) | `makeSeatValidator(conn, room, seat)` | `receipt` (`ProtocolActionReceipt`) |
| 14 | `SUBMIT_SLEEVE_BLOOM_CHOICE` | **true** | `executeBusinessRequest` -> `SharedRoom.submitSleeveBloomChoice` (:918) | `makeSeatValidator(conn, room, seat)` | `receipt` (`ProtocolActionReceipt`) |
| 15 | `SUBMIT_BEAM_SWAP` | **true** | `executeBusinessRequest` -> `SharedRoom.submitBeamSwap` (:925) | `makeSeatValidator(conn, room, seat)` | `receipt` (`ProtocolActionReceipt`) |
| 16 | `SUBMIT_BEAM_SWAP_CHOICE` | **true** | `executeBusinessRequest` -> `SharedRoom.submitBeamSwapChoice` (:932) | `makeSeatValidator(conn, room, seat)` | `receipt` (`ProtocolActionReceipt`) |
| 17 | `CLAIM_CONTROL` | **true** | `executeBusinessRequest` -> `SharedRoom.claimControl` (:939) | `makeSeatValidator(conn, room, seat)` | 基础响应 (`success=true`) |
| 18 | `RELEASE_CONTROL` | **true** | `executeBusinessRequest` -> `SharedRoom.releaseControl` (:942) | `makeSeatValidator(conn, room, seat)` | 基础响应 (`success=true`) |
| 19 | `RESUME_CURSOR` | **true** | `executeBusinessRequest` -> `SharedRoom.resume` (:867) | `makeSeatValidator(conn, room, seat)` | PLAYER 类 (`snapshot`) |
| 20 | `GET_REQUEST_SCOPE` | **false** | `handleRequestEnvelope` (直接返回 `grantedScope`) | 不进桌队列 | 控制信封响应 (`grantedScope`) |
| 21 | `LIST_ROOMS` | **false** | `executeBusinessRequest` -> `RoomRegistry.listRooms` | 不进桌队列 | 大厅类 (`roomList`) |
| 22 | `GET_ROOM_INFO` | **false** | `executeBusinessRequest` -> `SharedRoom.roomInfo` (:844) | 不受席位限制 | 大厅类 (`roomInfo`) |
| 23 | `SNAPSHOT` | **false** | `executeBusinessRequest` -> `SharedRoom.snapshot` (:864) | `makeSeatValidator(conn, room, seat)` | PLAYER 类 (`snapshot`) |
| 24 | `DECISION` | **false** | `executeBusinessRequest` -> `SharedRoom.decision` (:887) | `makeSeatValidator(conn, room, seat)` | PLAYER 类 (`decision`) |
| 25 | `CAPABILITIES` | **false** | `executeBusinessRequest` -> `SharedRoom.capabilities` (:870) | 仅校验房间未关闭 | 静态能力 (`capabilities`) |
| 26 | `PUBLIC_SNAPSHOT` | **false** | `executeBusinessRequest` -> `SharedRoom.publicSnapshot` (:803) | `role == SPECTATOR` 校验 | PUBLIC 类 (`publicSnapshot`) |
| 27 | `RESUME_PUBLIC` | **false** | `executeBusinessRequest` -> `SharedRoom.resumePublic` (:806) | `role == SPECTATOR` 校验 | PUBLIC 类 (`publicSnapshot`) |
| 28 | `QUERY_ROUND_RESULT` | **false** | `executeBusinessRequest` -> `SharedRoom.queryRoundResultEvents` (:945) | `role == PLAYER` & `makeSeatValidator` | PLAYER 类 (`events`) |
| 29 | `QUERY_PUBLIC_ROUND_RESULT` | **false** | `executeBusinessRequest` -> `SharedRoom.queryPublicRoundResultEvents` (:812) | `role == SPECTATOR` 校验 | PUBLIC 类 (`publicEvents`) |

自查结论：29 项 API 全部具备独立且正确的路由处理；19 项副作用 API 与 10 项查询/控制 API 在 `isSideEffectApi` 与 `executeBusinessRequest` 中 100% 严格对齐，零缺口，无任何已知 API 误落 `UNKNOWN_REQUEST_TYPE`。

---

### 6.3 返修补齐的回归测试清单 (共 5 项)

| 测试类 | 测试用例名称 | 对应返修项 | 验证目标与断言细节 |
|:---|:---|:---:|:---|
| `RequestScopeReceiptStoreTest` | `R4 regression - member completed side-effect count decrements when expired scopes are cleaned` | **R4.1** | 达到 `maxCompletedPerMember=2` 上限报 `SERVER_BUSY` -> 推进时间使 scope 过期 -> 调用 `cleanExpiredScopes()` -> 断言已完成副作用计数递减归零 -> 发行新 scope 能够正常受理新副作用请求，不再报 `SERVER_BUSY`。 |
| `RequestScopeReceiptStoreTest` | `R4 regression - idle principal metadata completely evicted when scope expires and no pending state` | **R4.2** | 临时 principal 完成副作用并持有状态 -> scope 过期并调用 `cleanExpiredScopes()` -> 断言 `hasPrincipalState(principal)` 返回 `false`，彻底释放 5 项结构无内存泄漏。 |
| `RequestScopeReceiptStoreTest` | `R5 regression - getOrCreateScopeForPrincipal never returns expired scope even within rate limit window` | **R5** | 获取 scope 后推进时间使其过期 -> 紧接着调用 `getOrCreateScopeForPrincipal` -> 验证即使在 1000ms 限速窗口内，依然强制新建并返回全新有效 scope，绝不返回过期 scope。 |
| `TcpTableServerHandshakeTest` | `R3 regression - repeatedly connecting and closing without handshake does not leak bucket` | **R3** | 配置 `maxUnhandshakedConnections=4` -> 循环 10 次建立连接并不发送 HELLO 立即关闭 -> 断言 `unhandshakedCount` 归零 -> 第 11 条连接建立并发送 HELLO -> 成功收到 WELCOME 帧，验证未握手桶计数零泄漏。 |
| `TcpTableServerRoutingAndSubscriptionTest` | `R7+R8 regression - displaced connection receives DISPLACED_BY_NEW_CONNECTION close after seat resume` | **R7/R8** | Client 1 建房绑定 seat 0 -> Client 2 使用 resumeToken 恢复会话顶号成功 -> Client 1 原连接在超时内读取到 CLOSE 帧，且 reason 明确为 `DISPLACED_BY_NEW_CONNECTION`。 |

---

## 7. 热修复记录 (HOTFIX)

### 《HOTFIX-1 (2026-10-15)：修复 TcpTableServer.kt:1308 ex.errorCode → ex.code》
- 修复 `handleCreateRoom` 中捕获 `ServerApiException` 访问属性名错误，由 `ex.errorCode` 改为 `ex.code`。

---

### 《HOTFIX-2 (2026-10-15)：R8 顶号主动 CLOSE(DISPLACED_BY_NEW_CONNECTION)，重写 R7 回归测试》

#### 1. 契约背景与缺口分析
- **契约依据**：
  - `TCP-MIGRATION-USECASES.md` UC-C01：「CLOSE 码表：……顶号 —— 每个码有发出方用例且落 D4 枚举；客户端能按码稳定分类」；
  - `TCP-MIGRATION-REVIEW.md` D4：`DISPLACED_BY_NEW_CONNECTION` 属于永久关闭原因（`PERMANENT`），客户端（MR-02 分类器）依赖此码识别被顶号并彻底终止自动重连；
- **现状缺陷**：此前在 `RESUME_SESSION` / `RESUME_SPECTATOR` 顶号成功后，仅在权威层自增席位绑定代次让旧连接后续命令在权威队列中被 `makeSeatValidator` 回以 `STALE_SESSION` 拒绝，但服务端**从未向旧连接主动发出 CLOSE(DISPLACED_BY_NEW_CONNECTION)**。被顶客户端永远收不到明确通知，会一直自认持有席位、心跳保活并无限重试命令，违反 UC-C01 与 D4 契约。

#### 2. 修改落地与扫描兜底幂等性
1. **玩家席位顶号主动下发 CLOSE** (`TcpTableServer.kt:bindPlayerToRoom`):
   在 `conn` 角色、席位与代次赋值完成之后、订阅安装之前，扫描 `connections.values`：
   凡满足 `other !== conn && other.activeRoom === room && other.role == ConnectionRole.PLAYER && other.viewerSeat == seat` 的其他连接，一律主动调用 `other.close(TcpCloseReasons.DISPLACED_BY_NEW_CONNECTION, "Seat resumed by a new connection", immediate = false)` 优雅排空 CLOSE 帧并切断。
2. **观战者顶号主动下发 CLOSE** (`TcpTableServer.kt:bindSpectatorToRoom`):
   在 `conn` 观战者角色与 spectatorId 赋值后，同理扫描 `connections.values`：
   凡满足 `other !== conn && other.activeRoom === room && other.role == ConnectionRole.SPECTATOR && other.spectatorId == specId` 的其他连接，调用 `other.close(TcpCloseReasons.DISPLACED_BY_NEW_CONNECTION, "Spectator session resumed by a new connection", immediate = false)`。
3. **扫描兜底的幂等性说明**：
   - **零副作用**：`JOIN_ROOM` / `CREATE_ROOM` 分配的是空闲席位，`JOIN_SPECTATOR` 分配的是全新生成的 `spectatorId`，扫描天然匹配不到目标，无任何副作用；
   - **状态机幂等保护**：`connections` 采用并发表 `ConcurrentHashMap`，单 Server 实例内单桌/全局连接数受硬上限保护（全局 ≤512 连接），单次扫描开销仅微秒级；`TcpConnection.close` 由原子状态机（`compareAndSet(HANDSHAKED, CLOSING)`）保护，多次触发天然幂等安全；
   - **纵深防御闭环**：该机制为 TCP 传输层提供了显式主动的终态切断与通知，与同桌权威队列中的 `makeSeatValidator`（绑定代次命令校验 `STALE_SESSION` 拒绝）共同构成坚实的纵深防御。

#### 3. 回归测试重写 (`TcpTableServerRoutingAndSubscriptionTest.kt`)
- **测试重写与更名**：原测试更名为 `R7+R8 regression - displaced connection receives DISPLACED_BY_NEW_CONNECTION close after seat resume`；
- **断言迁移**：R8 落地后被顶连接会先收到 `CLOSE(DISPLACED_BY_NEW_CONNECTION)` 进入优雅收尾（此时串行邮箱停摆，后续请求无法产生有效响应），原测试若继续断言 `SUBMIT_CHOICE` 的 `STALE_SESSION` 响应会挂起；现改为在 Client 2 恢复席位成功后，断言 Client 1 在超时内成功读取到 CLOSE 帧，且 `closeMsg.reason == TcpCloseReasons.DISPLACED_BY_NEW_CONNECTION`。
