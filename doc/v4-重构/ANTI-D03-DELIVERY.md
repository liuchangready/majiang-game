# 🀄 D-03 服务端统一绑定规则与场景交付与第7轮返修报告 (ANTI-D03-DELIVERY.md)

- **任务编号**: D-03 (§9.2 & §9.3, IMPROVEMENT-PLAN.md)
- **开发负责人**: Anti
- **检视与验收负责人**: OpenAI Codex (独立检视与最终验收)
- **工作区基线**: `D:\majiang` (HEAD: `cca271a0b5c2a257ad7b3affda6bb14eed221ebd`)
- **交付时间**: 2026-10-19
- **状态**: COMPLETED / ROUND_7_REWORK_VERIFIED_ALL_GREEN

---

## 1. 核心需求与返修目标

根据 `doc/v4-重构/IMPROVEMENT-PLAN.md` §9.2、§9.3、`doc/v4-重构/TCP-MIGRATION-DESIGN.md` 与 Codex 第 1 轮至第 7 轮独立检视返修要求，D-03 实现了服务端物理传输连接与逻辑房间绑定的统一规则并完成深度返修：

1. **清晰区分并规范三层代次/纪元所有权，彻底移除生产代次写后门与成员篡改接口**：
   - **`SharedRoom` 成员代次 (`memberGeneration`)**：由房间与权威队列严格独占所有，内部封装为 `private val seatMemberGenerationsArray = LongArray(4) { 1L }`，单调递增。外部完全只读（`seatMemberGeneration(seat)`），任何公开的测试专用写入口（如 `advanceSeatMemberGenerationForTest` / `advanceSpectatorMemberGenerationForTest`）已彻底从生产代码中移除；测试一律通过真实准入/重连流程或受控调度屏障驱动代次演进与终审核验。外部公开字段 `seatMemberGenerations` 与旧 `@Deprecated bindingGenerations` 统一返回不可影响内部状态的防御性副本（`copyOf()`）。
   - **`SharedRoom` 观战者成员与令牌所有权闭环 (Round 7 强化)**：
     彻底收回 `SpectatorMember` 的可变状态与 `spectators` 映射的外部写权限。观战者内部真实状态封装于私有 `InternalSpectatorMember` 与 `private val spectatorsMap`；外部统一暴露包含纯只读 `val` 属性的 `SpectatorMemberView` 以及 `Collections.unmodifiableMap`。视图模型彻底剥离敏感的 `resumeToken`，杜绝令牌外泄；任何对 `spectators` 视图强转并调用的 `clear()` / `remove()` 均抛出 `UnsupportedOperationException`。
   - **`SharedRoom` 席位令牌与断开代次所有权收口 (Round 7 强化)**：
     将 `seatResumeTokensArray` 与 `seatDisconnectedGenerationsArray` 私有化封装；外部公开的 `resumeTokens` 与 `seatDisconnectedGenerations` 统一返回防御性副本（`copyOf()`），并提供类型化只读访问器 `seatResumeToken(seat)` 与 `seatDisconnectedGeneration(seat)`。外部尝试直接修改返回数组元素绝不污染内部真值，使用篡改的 token 发起恢复必定被权威队列拒绝为 `INVALID_RESUME_TOKEN`。
   - **`InProcessBinding` 本地绑定代次 (`localBindingGeneration`)**：由进程内传输适配器所有，在每次重绑、离房或关闭时递增，用于使本地过时入站回调迅速失效。
   - **TCP 连接纪元 (`connectionEpoch`)**：由网络层连接适配器（`TcpConnection` / `TransportConnection`）独占所有，标识底层物理 SocketChannel 的单次生命周期。
2. **准入与成员规则严格划分所有权与执行期校验**：
   - 房间侧（`SharedRoom`）负责准入许可、身份令牌（`seatResumeToken` / `spectatorResumeToken`）校验及代次分配；
   - 权威调度队列（`ServerTableHost.enqueueOperation`）在串行执行期最终复核（通过 `sessionValidator` 闭包），若代次漂移或连接关闭，立即拒绝过时命令（`STALE_SESSION`），绝不损坏领域状态；
   - 明确区分“执行期校验单元测试”（`SharedRoomTest.kt`）与“真实双传输顶号交错测试”（`DualTransportBindingScenarioParameterizedTest.kt`）；
   - 底层网络 Socket 资源与缓冲完全归属于传输适配器（`TcpConnection` / `TcpTableServer` / `InProcessBinding`），领域层及房间层严禁触碰底层网络句柄。
3. **保持 D-02 角色分型与 MR-02 错误契约**：
   - 严格沿用 `PlayerState`（强约束 `seat in 0..3`）与 `SpectatorState`（强约束 `spectatorId.isNotBlank()`）；
   - 保持 MR-02 统一错误码映射：过时会话或已被顶替抛出 `ServerApiException(ApiErrors.INVALID_TOKEN, "STALE_SESSION")` 或返回 `REJECTED`(`STALE_SESSION`)，房间已关闭抛出 `ServerApiException(ApiErrors.ROOM_NOT_FOUND, "ROOM_CLOSED")`，冲突请求返回 `ApiErrors.CONFLICT`。
4. **同一套参数化业务场景全量覆盖双传输实现**：
   - 引入 `DualTransportBindingScenarioParameterizedTest`，以参数化驱动 `TransportMode.IN_PROCESS` 与 `TransportMode.TCP`，覆盖 12 大核心业务场景（共 27 项测试，含防篡改测试），双传输语义 100% 绿色通过。
5. **UDP 遗产归档与守恒律保持**：
   - 严禁恢复 UDP、旧 TCP/V3、ACK、KICK 或 seq 机制；
   - 保持麻将 136 张物理守恒律不变。

---

## 2. Codex 独立复验问题深度定位与返修历程

### 2.1 第 4 轮缺陷现象与根因定位
- **复验失败现象**:
  在全量或反复运行 `DualTransportBindingScenarioParameterizedTest` 时，场景 11（玩家在途恢复与顶号交错）或场景 12（观战者在途恢复与顶号交错）的 TCP 参数偶发报错：
  `AssertionFailedError: Old connection must be closed/displaced within 3000ms (closed=false, reason=HEARTBEAT_TIMEOUT) ==> expected: <true> but was: <false>`
- **根本原因深度剖析**:
  1. **`TcpConnection.close` 中状态流转与控制帧入队的原子性漏洞 (微秒级竞态)**:
     原实现中在 `synchronized(this)` 内将状态原子 CAS 变更为 `ConnectionState.CLOSING`，但在同步块之外才尝试将 `CLOSE(DISPLACED_BY_NEW_CONNECTION)` 控制帧压入 `outboundQueue`。并发运行的 Selector 线程在同一时刻执行 `handleWrite` 时，由于 `conn.state` 已经变成 `CLOSING`，而 `closeFrame` 尚未入队，`outboundQueue.hasPendingData` 为 `false`，`handleWrite` 误以为所有终态数据均已写完，立即调用了 `sc.shutdownOutput()` 向对端发出 TCP FIN。随后入队的 `CLOSE` 帧被阻断在已半关闭的通道中无法发出，客户端直接读到了 EOF (`read == -1`)，被动触发了 `handleFailure(TcpCloseReasons.HEARTBEAT_TIMEOUT, "Server EOF")`。
  2. **`TcpTableServer.handleRead` 在 CLOSING 态下的 Windows TCP RST 冲刷**:
     在旧逻辑中，`handleRead` 在 `conn.isClosingOrClosed` 时，若捕获到任何读取异常，直接调用 `conn.forceCloseChannel()` 关闭 socket。在 Windows 操作系统中，若此时客户端仍有在途请求未被服务端完全读尽（例如被顶号连接正在发送的无效 token 恢复请求），服务端过早调用 `channel.close()` 会促使 OS 协议栈向对端发送 TCP RST 重置包。TCP RST 会瞬间抹除客户端 OS 接收缓冲区中尚未被应用程序读取的 `CLOSE` 帧，导致客户端 `sc.read()` 直接抛出 `IOException: Connection reset by peer`，同样回退为 `HEARTBEAT_TIMEOUT`。
  3. **`TcpClientBinding.handleFailure` 的断连原因升级与永久状态保护**:
     客户端在捕获到网络 EOF 或暂态读取失败时，曾可能将 `disconnectReasonRef` 固定为暂态的 `HEARTBEAT_TIMEOUT` 且 `isClosed` 维持 `false`。若顶号 `CLOSE` 帧因网络交错稍后送达或正在就绪队列，必须确保永久性断连原因能够通过 CAS 升级覆盖 `HEARTBEAT_TIMEOUT`，并立即使 `isClosed.set(true)` 生效。

### 2.2 第 4 轮生产代码与测试代码返修方案
1. **`TcpConnection.close` 实现原子控制帧入队与状态推进**:
   在 `TcpConnection.kt` 的 `synchronized(this)` 内部，先将 `CLOSE` 控制帧压入 `outboundQueue` 并调用 `enableWriteInterest()`，随后才将连接状态推进至 `ConnectionState.CLOSING`。
   **效果与边界限制**: 在控制队列未满且全局内存预算充足的前提下，物理上保证任何 Selector 线程观察到连接处于 `CLOSING` 态时，`outboundQueue.hasPendingData` 必然为 `true`，`CLOSE` 控制帧优先于后续半关闭输出发出，杜绝了提前 `shutdownOutput(FIN)` 的微秒级竞态。但该交付保证受限于底层环境与队列容量：若控制队列饱和（8 帧/8 KiB）或全局预算耗尽，控制帧将拒绝入队并直接执行 `forceCloseChannel()`；若底层套接字发生物理 I/O 故障或达到 1 秒终态收尾超时，连接同样会被强制释放。
2. **`TcpTableServer.handleRead` 杜绝过早强切通道与防 RST 保护**:
   重构 `handleRead` 中针对 `conn.isClosingOrClosed` 的处理：
   - 出站队列未排空时立即 return，等待 `handleWrite` 顺利写出；
   - 读取客户端残留输入进行排空；若捕获到读取异常，直接 return，不提前调用 `forceCloseChannel()`，彻底避免触发 Windows Winsock TCP RST 抹除 `CLOSE` 帧；
   - 仅当对端明确送达 EOF (`read == -1`) 或 `performTimerChecks` 的 1 秒超时 (`terminalFlushTimeoutMillis`) 达到时，才最终释放 channel。
3. **`TcpClientBinding` 完善 Fast-Path 与永久原因升级覆盖**:
   - `handleRead` 解码出 `FRAME_TYPE_CLOSE` 控制帧时，直接在 I/O 线程同步处理并调用 `handleFailure`，避免受到 worker 派发邮箱的排队阻塞；
   - `handleFailure` 的 CAS 判定扩展为：
     `disconnectReasonRef.compareAndSet(null, reason) || (isPermanent && disconnectReasonRef.compareAndSet(TcpCloseReasons.HEARTBEAT_TIMEOUT, reason))`
     并在 `isPermanent` 为 true 时可靠执行 `isClosed.set(true)`；
   - `handleRead` 与 `drainInboundMailbox` 严格守护：一旦已确立永久断连原因（如 `DISPLACED_BY_NEW_CONNECTION`），后续无论发生 Read failure 还是 EOF，均绝对不再调用 `handleFailure(HEARTBEAT_TIMEOUT)`。

### 2.3 第 5 轮缺陷深度定位与生产代码根治
1. **控制队列饱和与预算耗尽的明确切断语义**:
   - 检查 `outboundQueue.enqueueControl(closeFrameBytes)` 的返回值。若返回 `false`（队列饱和或预算耗尽）或在序列化时捕获到异常，**绝不调用 `enableWriteInterest()`，绝不推进至 `CLOSING` 态**；
   - 设置局部标志 `shouldForceClose = true`，安全退出 `synchronized(this)`；
   - 在退出同步块后立即调用 `forceCloseChannel()`，执行底层 channel 物理切断、所有队列与预算释放以及在途请求结构化失败。连接直接从 `HANDSHAKED` / `AWAITING_HELLO` 跃迁为 `CLOSED`。
2. **同步与重入安全性彻底保障**:
   - `forceCloseChannel()` 的调用全部移至 `synchronized(this)` 同步块外部执行；
   - `forceCloseChannel()` 内部仅在提取 `roleState` 并原子 CAS `state.getAndSet(ConnectionState.CLOSED)` 的微小临界区内持有 `this` 锁；后续的房间订阅解除、SelectionKey 取消、SocketChannel 关闭排空、出站队列清空、邮箱预算释放与 `roleToNotify.onConnectionClosed` 均在无锁环境下串行完成，从根源上杜绝了持锁 I/O 与死锁风险。
3. **`TcpOutboundQueue` 增加控制队列可观测性**:
   - 新增 `controlQueueSize: Int` 与 `controlQueueBytes: Long` 线程安全只读属性，便于精确断言控制帧水位。
4. **增补 UC-C05 确定性回归测试**:
   - 在 `TcpTableServerCapacityAndLifecycleTest` 中增补两项确定性可复现回归单测：
     1) `UC-C05 close when control queue saturated immediately force-closes without entering CLOSING`；
     2) `UC-C05 close when global budget exhausted immediately force-closes without entering CLOSING`。

### 2.4 第 6 轮架构缺陷定位与生产/测试代码返修
1. **缺陷定位与契约违反**:
   - 生产代码 `SharedRoom.kt` 在 public API 中暴露了 `advanceSeatMemberGenerationForTest` 与 `advanceSpectatorMemberGenerationForTest`。这破坏了“成员代次由 SharedRoom/房间侧受控分配，由权威队列在串行执行期终审”的任务契约，允许调用者绕过准入许可、token 校验和权威队列直接篡改代次；
   - 参数化场景 3 与场景 7 原先依靠人工代次推进接口及手动调用 `notifySeatDisplaced`，随后才执行真实 `resumeSession`，未真正测试真实双传输顶号与排队旧命令在权威队列上的交错排队。
2. **生产代码与测试代码返修方案**:
   - **彻底移除生产代码中的测试专用代次篡改入口**:
     从 `SharedRoom.kt` 彻底删除了 `advanceSeatMemberGenerationForTest` 与 `advanceSpectatorMemberGenerationForTest`，生产公开 API 不留任何可绕过准入的代次写入口；
   - **重构场景 3 与场景 7 为真实的双传输顶号交错测试**:
     - 在 `DualTransportBindingScenarioParameterizedTest.kt` 中：
       新连接 `replacementConn` 在屏障前先行创建并握手完毕；
       通过 `room.tableHost.enqueueOperation` 植入受控挂起屏障，确保权威队列完全冻结；
       在新连接上发起真实的 `replacementConn.resumeSession`，排在屏障之后，通过 `queuedOperationsCount >= 2` 确定性断言其已入队；
       在旧连接上发起准备命令（场景 3）或离房命令（场景 7），排在 `resumeSession` 之后，通过 `queuedOperationsCount >= 3` 确定性断言其在队列中紧随 `resumeSession`；
       释放屏障：`resumeSession` 先执行，合法完成准入并原子推进代次至 3L，主动触发顶号通知；旧命令在紧随其后执行时，其 `sessionValidator` 执行期终审查出代次漂移（2L != 3L），返回明确的 `REJECTED (STALE_SESSION)` 或因顶号连接中断抛出结构化异常；
       核心断言：命令未生效、席位状态未受污染（无副作用，就绪态不变/席位未释放）、旧连接收到 `DISPLACED_BY_NEW_CONNECTION` 关闭、新连接保持存活且可正常继续执行后续操作。
   - **清晰区分“执行期校验单元测试”与“真实双传输顶号交错”**:
     - 单元测试（`SharedRoomTest.kt`）：直接在房间层单测中验证 `sessionValidator` 返回 false 时，玩家命令与观战者离房均被拒绝为 `STALE_SESSION` 且无副作用（增补了 `stale queued spectator leave rejected by session validator`）；
     - 场景集成测试（`DualTransportBindingScenarioParameterizedTest.kt`）：在真实 `IN_PROCESS` 和 `TCP` 传输层下验证完整的“新连接真实 resume 顶号推进代次 + 旧连接在途命令排队交错执行期拦截 + 旧连接主动切断新连接保活”。

### 2.5 第 7 轮观战者与房间所有权缺陷定位与生产代码根治
1. **缺陷定位与所有权越权**:
   - 生产代码 `SharedRoom.kt` 中 `data class SpectatorMember` 暴露了 `@Volatile var generation`、`resumeToken` 与 `lastDisconnectedGeneration`，且公开字段 `val spectators = ConcurrentHashMap<String, SpectatorMember>()` 允许任何持有 `SharedRoom` 的调用方直接执行 `room.spectators[id]!!.generation++`、改写 `resumeToken` 或调用 `room.spectators.remove(...)`；
   - 此外，`resumeTokens` 与 `seatDisconnectedGenerations` 数组在公开字段中直接返回内部引用，存在外部通过索引赋值修改内部状态的越权漏洞。
2. **生产代码所有权收拢与防篡改重构方案**:
   - **观战者成员与令牌所有权闭环**:
     - 将观战者真实内部状态收敛为私有数据类 `InternalSpectatorMember` 以及私有并发映射 `private val spectatorsMap = ConcurrentHashMap<String, InternalSpectatorMember>()`；
     - 对外暴露无任何 public `var` 的只读数据视图 `data class SpectatorMemberView(val spectatorId: String, val name: String, val generation: Long, val isConnected: Boolean, val lastDisconnectedGeneration: Long)`，并且**彻底不包含 `resumeToken` 属性**，杜绝令牌外泄；
     - 公开属性 `val spectators: Map<String, SpectatorMemberView> get() = Collections.unmodifiableMap(...)`，强转修改直接抛出 `UnsupportedOperationException`；
     - 对外提供强类型查询接口：`spectatorMemberGeneration(id)`、`spectatorLastDisconnectedGeneration(id)`、`isSpectatorConnected(id)`、`hasSpectator(id)`、`spectatorCount`；
     - 令牌仅通过模块受限方法 `internal fun spectatorResumeToken(spectatorId: String): String?` 授权给传输层（`TcpTableServer` / `InProcessBinding`）使用。
   - **席位令牌与断开代次防御性拷贝与收口**:
     - 席位令牌数组内部私有化为 `private val seatResumeTokensArray = Array(4) { UUID.randomUUID().toString() }`，外部公开字段 `resumeTokens: Array<String>` 返回防御性副本（`copyOf()`），提供类型化访问器 `seatResumeToken(seat: Int): String`；
     - 席位断开代次数组内部私有化为 `private val seatDisconnectedGenerationsArray = LongArray(4) { 0L }`，外部公开字段 `seatDisconnectedGenerations: LongArray` 返回防御性副本（`copyOf()`），提供类型化访问器 `seatDisconnectedGeneration(seat: Int): Long`。
   - **测试代码规范化与防篡改验证**:
     - `ServerRoleStateTest.kt`：彻底移除对 `room.spectators` 的直接写与篡改操作，改用真实 `joinSpectator`、`resumeSpectator` 与 `leaveSpectator` 测试会话校验器，完全遵循准入与权威队列规约；
     - `DualTransportBindingScenarioParameterizedTest.kt`：扩展防篡改验证单测，涵盖 `seatMemberGenerations`、`seatDisconnectedGenerations`、`resumeTokens` 防御性副本、篡改 token 恢复被拒绝（`INVALID_RESUME_TOKEN`）、`spectators` 视图不可变性断言（抛出 `UnsupportedOperationException`）、真实玩家与观战者 resume 顶号推进代次以及过时代次执行离房被权威队列拦截为 `STALE_SESSION` 的完整闭环。

---

## 3. 状态所有权与代次演进矩阵 (State Ownership & Generation Matrix)

| 代次/标识 | 所属宿主 | 变化时机 | 比较与校验机制 | 作用与职责 |
| :--- | :--- | :--- | :--- | :--- |
| **`seatMemberGeneration`** (`Long`) | `SharedRoom`<br/>(`private val seatMemberGenerationsArray`) | 席位 `join` (初始化为 1L) 或 `resumeSession` (顶号/重连单调递增 +1L) | 权威队列操作前执行 `sessionValidator`：`room.seatMemberGeneration(seat) == capturedGen` | 标识房间席位逻辑成员代次。内部私有数组，完全只读导出防御性副本；若发生顶号，旧代次立即失效，防止旧命令污染新代次。无任何公共 bypass 写接口。 |
| **`spectatorMemberGeneration`** (`Long`) | `SharedRoom`<br/>(`private val spectatorsMap`) | 观战者 `joinSpectator` (初始化为 1L) 或 `resumeSpectator` (+1L) | 权威队列操作前执行 `sessionValidator`：`room.spectatorMemberGeneration(id) == capturedGen` | 标识房间观战者成员代次。顶号时递增，防止旧观战连接误解绑新观战者。对外仅提供只读视图与访问器。 |
| **`seatResumeTokens`** (`String`) | `SharedRoom`<br/>(`private val seatResumeTokensArray`) | 席位加入时生成，显式退出或房间生命周期终结时轮转 | 权威队列准入核验：`seatResumeTokensArray[seat] == resumeToken` | 席位断线重连/顶号凭证。内部私有数组，公开字段仅返回防御性副本，提供 `seatResumeToken(seat)` 访问器。 |
| **`spectatorResumeToken`** (`String`) | `SharedRoom`<br/>(`InternalSpectatorMember.resumeToken`) | 观战者加入时生成，离房时轮转 | 权威队列准入核验：`member.resumeToken == resumeToken` | 观战者断线重连/顶号凭证。完全封装在内部结构中，对外只读视图 `SpectatorMemberView` 绝不暴露，仅模块内传输层授权读取。 |
| **`seatDisconnectedGenerations`** (`Long`) | `SharedRoom`<br/>(`private val seatDisconnectedGenerationsArray`) | 席位传输断开时记录当前断开代次 | 权威队列内屏障核验：`expectedGeneration > seatDisconnectedGenerations[seat]` | 领域层断开代次屏障。内部私有数组，公开字段返回防御性副本，防止迟到的延迟回调误活已断开席位。 |
| **`localBindingGeneration`** (`Long`) | `InProcessBinding` | 每次调用 `bindRoom`、`leaveRoom` 或 `close` 时单调递增 (+1L) | 每次派发本地回调前核验：`bindingGeneration == ctx.generation` | 本地门控。使处于队列中的旧房间回调、旧席位事件在换房或离房后直接丢弃，不污染新绑定。 |
| **`connectionEpoch`** (`Long`) | `TcpConnection` | 物理 SocketChannel 建立连接握手分配（原子单调递增） | 帧分派时比对当前通道与关闭状态 | 物理传输连接纪元。用于区分同一客户端重连前后不同的底层 Socket 连接，底层 IO 关闭不影响逻辑会话回执。 |
| **`logicalPrincipal`** (`String`) | `RoleStates`<br/>(`ServerRoleState`) | 角色安装时确立（如 `player:room1:0` 或 `spectator:room1:spec1`） | `RequestScopeReceiptStore` 键匹配 | 逻辑身份标识。业务回执 (`IdempotencyKey`) 与作用域 (`TcpScope`) 绑定在此身份上，跨 TCP 连接重连后可安全重放已完成副作用。 |

---

## 4. 全模块测试验证证据

按项目规范执行严格串行全量测试（所有任务均使用 `--rerun-tasks --console=plain` 纯净执行）：

1. **双传输参数化测试套件 (12 大业务场景 + 纯领域测试 + 强化防篡改测试，27 项全绿)**:
   - 指令: `.\gradlew.bat :server:test --tests "com.majiang.server.binding.DualTransportBindingScenarioParameterizedTest" --rerun-tasks --console=plain`
   - 结果: **27/27 PASSED** (耗时 16s, BUILD SUCCESSFUL)
     - `scenario 1` (IN_PROCESS & TCP): PASSED (玩家顶号代次递增与旧连接关闭)
     - `scenario 2` (IN_PROCESS & TCP): PASSED (观战者顶号代次递增与旧连接关闭)
     - `scenario 3` (IN_PROCESS & TCP): PASSED (双传输真实 resume 顶号交错与 STALE_SESSION 拒绝)
     - `scenario 4` (IN_PROCESS & TCP): PASSED (真实传输断开与延迟 mark 拦截，动态视图校验)
     - `scenario 4b` (IN_PROCESS & TCP): PASSED (纯领域断开代次屏障防误活)
     - `scenario 5` (IN_PROCESS & TCP): PASSED (玩家重复离房幂等 APPLIED)
     - `scenario 6` (IN_PROCESS & TCP): PASSED (观战者重复离房与重加)
     - `scenario 7` (IN_PROCESS & TCP): PASSED (双传输真实 resume 顶号旧离房排队被拒与新成员防误解绑)
     - `scenario 8` (IN_PROCESS & TCP): PASSED (房主解散终态关闭与全员 ROOM_NOT_FOUND)
     - `scenario 9` (IN_PROCESS & TCP): PASSED (玩家与观战者接口边界严格隔离)
     - `scenario 10` (IN_PROCESS & TCP): PASSED (业务 requestId 跨重连幂等重放、冲突与跨主体隔离)
     - `scenario 11` (IN_PROCESS & TCP): PASSED (玩家在途恢复与顶号交错受控屏障回归)
     - `scenario 12` (IN_PROCESS & TCP): PASSED (观战者在途恢复与顶号交错受控屏障回归)
     - `seatMemberGenerations and bindingGenerations return defensive copies and cannot tamper internal room generation state()`: PASSED (全维度防篡改单测)
2. **`:server:test` 全量重跑 (无缓存全绿)**:
   - 指令: `.\gradlew.bat :server:test --rerun-tasks --console=plain`
   - 结果: **300/300 PASSED** (0 failures, 0 errors, 0 skipped, 耗时 46s, BUILD SUCCESSFUL)
3. **`:client:test` 全量重跑 (无缓存全绿)**:
   - 指令: `.\gradlew.bat :client:test --rerun-tasks --console=plain`
   - 结果: **217/217 PASSED** (0 failures, 0 errors, 0 skipped, 耗时 32s, BUILD SUCCESSFUL)
4. **Codex 独立架构探针复验**:
   - 指令: `.\gradlew.bat -I tools/architecture/tcp-current-review.init.gradle :server:test --console=plain`
   - 结果: **PASSED** (探针 100% 绿灯通过, 耗时 5s)
5. **`:protocol:test` 与 `:engine:test`**:
   - 结果: **100% PASSED** (领域物理守恒律与协议 DTO 纯净，未受任何污染)

---

## 5. 对 D-08 隔离工作树的整合说明与交接边界

D-08 专注于 `TcpTableServer` 路由拆分与 `RequestScopeReceiptStore` 重构，由另一个 Anti 工作树独立进行。本 D-03 交付在 `TcpTableServer`、`SharedRoom` 和 `RoleStates` 中引入了规范的封装与只读访问器，供后续合并：

1. **`SharedRoom.kt`**:
   - `resumeTokens` 与 `seatDisconnectedGenerations` 改为返回防御性副本，提供 `seatResumeToken(seat)` 与 `seatDisconnectedGeneration(seat)`；
   - `spectators` 改为返回不可变 `SpectatorMemberView` 映射，内部状态收敛于 `spectatorsMap`；
   - 新增 `spectatorResumeToken(id)` 模块内部访问器；
   - 合并指导：直接采用当前 `SharedRoom.kt`，接口向下兼容且完全防篡改。
2. **`RoleStates.kt`**:
   - 新增 `RoomRoleState.memberGeneration: Long` 属性；
   - `PlayerState` 与 `SpectatorState` 增加了 `displacedSubscription: AutoCloseable?` 与 `attachSubscriptions` 的第三参数，以及 `inFlightLeave` 字段；
   - 合并指导：直接采用当前 `RoleStates.kt`，接口向下兼容。
3. **`TcpTableServer.kt`**:
   - `runEventLoop` 中分离 `isReadable` 与 `isWritable` 的独立 `if` 分支；
   - `handleWrite` 中在出站队列排空时调用 `sc.shutdownOutput()` 发出 FIN 半关闭；
   - `handleRead` 中在出站队列排空后检测 EOF (`read == -1`) 收尾释放 channel，残留读取异常不提前强切 channel 避免 RST；
   - `bindPlayerToRoom` 与 `bindSpectatorToRoom` 中增加了 `room.onSeatDisplaced` / `room.onSpectatorDisplaced` 回调注册，并在顶号时代次推进时主动触发 `conn.close(ApiProtocol.DISPLACED_BY_NEW_CONNECTION)`；
   - `handleLeaveRoom` 中在大厅角色解绑后，将大厅 scope 写入回执与响应作为 `grantedScope`；
   - `SharedRoom.bindingGenerations[...]` 调用收敛替换为 `SharedRoom.seatMemberGeneration(...)`；
   - `room.resumeTokens[seat]` 替换为 `room.seatResumeToken(seat)`，`targetRoom.spectators[id]?.resumeToken` 替换为 `targetRoom.spectatorResumeToken(id)`；
   - 合并指导：D-08 在拆分 Handler 类时，只需将上述优雅半关闭和监听/scope/访问器返回保留在对应的 Handler 中即可。
4. **`TcpConnection.kt`**:
   - `close()` 在 `synchronized(this)` 内部原子先排入 `CLOSE` 控制帧并启用写兴趣，再流转至 `CLOSING` 态；若控制队列饱和或预算耗尽则直接切断（`forceCloseChannel()`），无持锁 I/O。

---

## 6. 风险评估与边界确认

1. **UDP 与旧协议**: 确认代码库中无任何 UDP `seq`/`ACK`/`KICK` 遗留，MJ-TCP/1 契约保持完好。
2. **物理守恒律**: 136 张牌严格闭环（0..135），出千与牌山操作物理守恒未受影响。
3. **版本控制纯洁性**: 未执行任何 `git commit` 或 `git push`；现有 D-01/D-02 与历史未提交修改全部完整保留。
