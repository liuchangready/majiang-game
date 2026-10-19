# 🀄 D-02 服务端角色分型与物理连接解耦交付报告 (ANTI-D02-DELIVERY.md)

- **任务编号**: D-02 (§9.2, IMPROVEMENT-PLAN.md)
- **开发负责人**: Anti
- **检视与验收负责人**: OpenAI Codex (独立检视)
- **基线 commit**: `cca271a0b5c2a257ad7b3affda6bb14eed221ebd` (HEAD)
- **交付时间**: 2026-10-19
- **状态**: COMPLETED / READY_FOR_CODEX_ROUND5_VERIFICATION (Round 4 返修完成)

---

## 1. 核心需求与架构实现

根据 `IMPROVEMENT-PLAN.md` §9.2 及任务指示，完成服务端物理传输连接资源与逻辑业务角色的彻底解耦：

### 1.1 物理连接资源与逻辑角色彻底分离
- **`TransportConnection` 抽象契约 (`server/.../TransportConnection.kt`)**:
  - 纯粹的传输层连接抽象，只拥有底层传输资源：`connectionId`、`connectionEpoch`、`remoteAddress`、`isClosed`、`isClosingOrClosed`、`sendNormalFrame()`、`sendControlFrame()`、`close()`。
- **`TcpConnection` 传输资源所有权**:
  - `TcpConnection` 实现 `TransportConnection`，唯一持有底层 `SocketChannel`、`SelectionKey`、帧解码器 `TcpFrameDecoder`、出站队列 `TcpOutboundQueue`、接收串行邮箱 `serialMailbox`（有界 64 帧 / 4 MiB 并计入全局预算 `TcpGlobalBudget`）与心跳/超时计时器。
  - **移除了** 原先直接分散挂载在 `TcpConnection` 上的可变业务状态字段（`seatSubscription`、`publicSubscription`、`roomSubscription`、`currentBindingGeneration` 等）。
  - 连接持有且仅持有单个 `@Volatile var roleState: ServerRoleState` 引用，外部通过只读属性获取派生视图（`role`、`playerState`、`spectatorState`、`lobbyState`、`activeRoom`、`viewerSeat`、`spectatorId`），禁止外部随意改写角色内部状态。

### 1.2 逻辑角色类型分型 (`RoleStates.kt`)
定义封闭继承树 `sealed interface ServerRoleState`，明确生命周期与职责边界：
1. **`UnauthenticatedRoleState`**: 连接建立后、握手完成前的初始角色，拒绝任何房间与业务操作。
2. **`LobbyRoleState`**: 握手成功后进入大厅的角色，持有不可变 `lobbyResumeToken`，`logicalPrincipal = "lobby:$lobbyResumeToken"`。
3. **`RoomRoleState`**: 进入房间的角色基类，持有 `room: SharedRoom` 与 `bindingGeneration: Long`。
4. **`PlayerState`**: 强类型玩家角色状态：
   - **座位强校验**: `seat in 0..3`，构造即严格校验，彻底消灭业务逻辑内部出现 `seat = -1` 的隐患。
   - **快照提供者**: 持有 `snapshotSupplier: () -> CompletableFuture<PlayerSnapshot>`，提供权威快照入口。
   - **订阅私有封装**: 内部封装 `seatSubscription: AutoCloseable?` 与 `roomSubscription: AutoCloseable?`，提供 `attachSubscriptions()` 与 `updateRoomSubscription()`，外部无法绕过生命周期直接篡改。
   - **幂等安全清理**: 实现 `detach()` 与 `onConnectionClosed()`，内部通过原子状态确保 `close()` 幂等执行；公开 `isDetached` 只读属性。
   - **会话校验器封装**: `makeSessionValidator(conn)` 统一构造同时校验物理连接未关闭/未处于关闭中（`!transport.isClosingOrClosed`）与房间成员代次 `room.bindingGenerations[seat] == bindingGeneration` 的高可靠校验闭包。
   - **精确一次断线通知**: `notifyDisconnect()` 携带自身代次调用 `room.disconnectSeat(seat, bindingGeneration)`，结合自身 `AtomicBoolean` 确保只通知一次，且顶号换代后绝不误断新连接。返回 `CompletableFuture<Unit>` 供确定性等待。
5. **`SpectatorState`**: 强类型观战者角色状态：
   - **标识符强校验**: `spectatorId.isNotBlank()`。
   - **公开快照提供者**: 持有 `publicSnapshotSupplier: () -> CompletableFuture<ProtocolPublicSnapshot>`。
   - **订阅私有封装**: 内部私有封装 `publicSubscription: AutoCloseable?` 与 `roomSubscription: AutoCloseable?`，与玩家私有流彻底物理隔离。
   - **会话校验器封装**: `makeSessionValidator(conn)` 严格校验 `!isDetached && (transport == null || !transport.isClosingOrClosed) && !room.closed && (room.spectators[spectatorId]?.generation == bindingGeneration)`。
   - **精确一次断线通知**: 封装 `notifyDisconnect()`（通知 `room.disconnectSpectator(spectatorId, bindingGeneration)`）；返回 `CompletableFuture<Unit>`；公开 `isDetached` 只读属性。

### 1.3 业务回执归属于逻辑身份 (TcpScope)
- 服务端 `RequestScopeReceiptStore`、`IdempotencyKey` 与 `TcpScope` 均以 `logicalPrincipal`（`player:<roomId>:<seat>` / `spectator:<roomId>:<specId>` / `lobby:<token>`）为核心凭据，与底层可替换的物理 `TcpConnection` 完全解耦。
- 物理连接发生重连、顶号或网络断开时，只要新连接出示合法的 `token` / `grantedScope`，即可无缝重放或查询已完成的业务副作用，保证跨连接请求幂等性。

### 1.4 强类型玩家命令入口与权限隔离
- 在 `TcpTableServer` 中引入内联高阶函数 `withPlayer(conn, key, req, inFlight, isLobby) { player -> ... }` 与 `withSpectator(conn, key, req, inFlight, isLobby) { spectator -> ... }`：
  - 玩家专属对局命令全部通过 `withPlayer` 接入，直接获得非空的 `player: PlayerState`，参数中严格使用 `player.seat`（`0..3`）。
  - 若未绑定席位（包括观战者或大厅连接）非法调用玩家命令，直接拦截并返回 `ApiErrors.UNAUTHENTICATED_SESSION`，杜绝未授权访问。
  - `seat = -1` 哨兵值被彻底驱逐出服务端内部业务调度，仅在协议 DTO 反序列化与缺省构造边界存在。

### 1.5 严格去除 UDP 遗产
- 生产代码与状态机中无任何 UDP `seq`、`ACK`、重传索引、防重放窗口或 `pushWindow` / `publicWindow` 字段，纯粹遵循 TCP (MJ-TCP/1) 可靠流契约。

---

## 2. 第1轮独立复验发现与返修 (Codex Round 1 Remediation)

- **问题**: `SpectatorState.makeSessionValidator()` 未核对 `room.spectators[spectatorId]?.generation == bindingGeneration`，亦未拒绝处于 `CLOSING` 态的传输；旧连接排队的 `leaveSpectator` 会误删新连接顶号后的席位。
- **修复**:
  - `makeSessionValidator(transport)` 补齐当前成员代次核对与 `!transport.isClosingOrClosed`。
  - `bindSpectatorToRoom` 基线回调若发现顶号换代立即关闭新订阅。
  - 增补领域层与真实 TCP 两项排队顶号退房竞态回归测试。

---

## 3. 第2轮独立复验发现与返修 (Codex Round 2 Remediation)

- **问题 1**: `markSpectatorConnected` 缺代次校验致旧代次篡改新成员连接状态。
- **问题 2**: 连接恢复/加入路径缺少 CLOSING 拦截。
- **问题 3**: 快照提供者动态读取连接角色状态。
- **修复**:
  - `SharedRoom.markSpectatorConnected` 增加 `expectedGeneration: Long` 参数并核验代次。
  - 全面阻断 CLOSING 连接完成恢复与角色安装。
  - 快照提供者改为纯净闭包捕获（Capture-Only Snapshot Suppliers）。
  - 增补 3 项测试：旧代次延后 mark 不误活、CLOSING 态连接无法反顶、快照供给者纯闭包绑定。

---

## 4. 第3轮独立复验发现与返修 (Codex Round 3 Remediation)

### 4.1 发现的缺陷与审查分析
1. **角色安装与物理连接关闭的原子协调缺陷**:
   - `bindPlayerToRoom` 与 `bindSpectatorToRoom` 在 `isClosingOrClosed` 检查与 `conn.installPlayerRole()` / `installSpectatorRole()` 之间可能发生并发关闭；
   - `TcpConnection.forceCloseChannel()` 将连接置为 `CLOSED` 后，此前以连接级 `disconnectNotified` 一次性标志向当时的 `roleState`（通常还是旧的大厅状态 `LobbyRoleState`）发通知；
   - 若关闭恰在检查后、安装前发生，通知发送给了 `LobbyRoleState`（无动作），新 `PlayerState` / `SpectatorState` 随后装入 `CLOSED` 连接，但连接级的 `disconnectNotified` 已为 true，新的代次永远收不到断线通知；
   - 而在 `SharedRoom.join` 或 `resumeSession` 中，该席位已被标记为 `connected = true`，导致连接已关闭但房间成员连接状态残留为 true，空闲回收与接管受到破坏。
2. **已入队/成功 join/resume 的断线补偿缺失**:
   - 在 `handleJoinRoom`、`handleResumeSession`、`handleJoinSpectator`、`handleResumeSpectator` 中，底层 `SharedRoom` 操作已在单桌串行队列成功执行（`connected = true`、代次推进），如果回调时物理连接关闭，未调用对应代次的 `disconnectSeat` / `disconnectSpectator` 做断线补偿。

### 4.2 针对性修复方案
1. **原子化角色安装并禁止向关闭连接安装新角色**:
   - `TcpConnection.installPlayerRole(playerState): Boolean`: 在 `synchronized(this)` 内检查 `if (isClosingOrClosed) return false`。已处于 `CLOSING` 或 `CLOSED` 态的连接严禁安装任何新角色，调用立即返回 `false`。
   - `TcpConnection.installSpectatorRole(spectatorState): Boolean`: 同样在同步块内原子检查并返回 `Boolean`。
   - `TcpConnection.unbindToLobbyRole()` 与 `setHandshakedIdentity()`: 在同步块内同样检查 `isClosingOrClosed`，严防已关闭连接被反向改写为大厅角色。
2. **移除连接级 `disconnectNotified` 标志，由具体角色状态自主保证断线幂等**:
   - 彻底移除 `TcpConnection.disconnectNotified: AtomicBoolean`。
   - `PlayerState` 与 `SpectatorState` 内部各自持有独立的 `disconnectNotified = AtomicBoolean(false)`，天然支持自身代次的精确一次断线通知。
   - `TcpConnection.forceCloseChannel()` 在进入 `CLOSED` 态时，在同步块内捕获当时的 `roleToNotify = roleState`，并在释放物理资源后调用 `roleToNotify.onConnectionClosed(this)`。
   - `TcpConnection.notifyRoomDisconnect()` 委托给当前 `synchronized(this) { roleState }.onConnectionClosed(this)`。
3. **严格的全链路精确断线补偿 (Exact Disconnect Compensation)**:
   - **`bindPlayerToRoom`**:
     - 显式传入 `capturedSeatGen: Long`。
     - 若入参前 `conn.isClosingOrClosed` 为 true，立即调用 `room.disconnectSeat(seat, capturedSeatGen)`。
     - 若 `!conn.installPlayerRole(playerState)`，立即调用 `playerState.detach()` 与 `playerState.notifyDisconnect()`（执行 `room.disconnectSeat(seat, capturedSeatGen)`），并以 `ROOM_NOT_FOUND` 完成回执。
     - 在 `room.snapshotAndSubscribe` 回调中，若发生错误或发现 `conn.isClosingOrClosed || !playerState.makeSessionValidator(conn)()`，立即调用 `playerState.detach()` 与 `playerState.notifyDisconnect()`。
   - **`handleJoinRoom` 与 `handleResumeSession`**:
     - 在 `whenComplete` 回调中，若 `conn.isClosingOrClosed`，立即调用 `targetRoom.disconnectSeat(seat, seatGen)` 补偿。
   - **`handleCreateRoom`**:
     - 传入 `room.bindingGenerations[0]` 作为 `capturedSeatGen` 驱动 `bindPlayerToRoom`。
   - **`bindSpectatorToRoom`**:
     - 若入参前 `conn.isClosingOrClosed` 为 true，立即调用 `room.disconnectSpectator(specId, generation)`。
     - 若 `!conn.installSpectatorRole(spectatorState)`，立即调用 `spectatorState.detach()` 与 `spectatorState.notifyDisconnect()`（执行 `room.disconnectSpectator(specId, generation)`）。
     - 在 `room.publicSnapshotAndSubscribe` 回调中，若发生错误或连接关闭/会话失效，立即调用 `spectatorState.detach()` 与 `spectatorState.notifyDisconnect()`。
   - **`handleJoinSpectator` 与 `handleResumeSpectator`**:
     - 在 `whenComplete` 回调中，若 `conn.isClosingOrClosed`，立即调用 `targetRoom.disconnectSpectator(res.spectatorId, res.generation)` 补偿。
4. **增补 5 项确定性交错与断线补偿测试 (`ServerRoleStateTest`)**:
   - `atomic coordination - player binding rejected when connection closed before role install with compensation disconnect`: 模拟关闭发生在 `installPlayerRole` 之前，断言安装被拒绝返回 false、角色未附着、补偿断线成功触发且席位 `seatConnected == false`、代次未被污染。
   - `atomic coordination - player binding disconnect notified when connection closed after role install`: 模拟关闭发生在 `installPlayerRole` 之后，断言 `forceCloseChannel` 捕获已安装的 `PlayerState` 并调用 `onConnectionClosed`，席位断开。
   - `atomic coordination - spectator binding rejected when connection closed before role install with compensation disconnect`: 模拟关闭发生在 `installSpectatorRole` 之前，断言安装被拒绝、观战成员补偿断开 `isConnected == false`。
   - `atomic coordination - spectator binding disconnect notified when connection closed after role install`: 模拟关闭发生在 `installSpectatorRole` 之后，断言观战成员断开。
   - `real TCP client closed right after join receives compensation disconnect and does not leak connected state`: 真实 TCP 客户端发送 `JOIN_ROOM` 立即强关 Socket，断言服务端正确触发断线补偿，席位不残留连接态。

---

## 5. 第4轮独立复验发现与返修 (Codex Round 4 Remediation)

### 5.1 发现的缺陷与审查分析
1. **同代次断线与 delayed mark 竞态 (Same-Generation Disconnect vs Delayed Mark Race)**:
   - 在 `TcpTableServer.bindPlayerToRoom()` 中，快照与订阅建立回调在通过 `conn.isClosingOrClosed` / `playerState.makeSessionValidator(conn)` 检查后，向权威队列调用 `room.markSeatConnected(seat, capturedSeatGen)`；
   - 若在检查之后、`markSeatConnected` 执行之前，并发触发 `conn.forceCloseChannel()`，后者会立即调用 `playerState.notifyDisconnect()` 并将 `room.disconnectSeat(seat, capturedSeatGen)` 入队；
   - 权威队列上 `disconnectSeat` 先行执行，将席位置为 `seatConnected = false` 并重算空闲；随后旧回调中的 `markSeatConnected` 入队执行；
   - 既有 `SharedRoom.markSeatConnected` 仅检查 `bindingGenerations[seat] == expectedGeneration`。由于断线操作并不自增代次（仅重连、加入或离开自增代次），旧 mark 在同代次下仍匹配代次，从而将已断开的席位错误反转为 `seatConnected = true`，破坏了断线真值与空闲回收计时！
   - 观战路径 `bindSpectatorToRoom()` 与 `markSpectatorConnected()` 存在同构竞态缺陷，`expectedGeneration` 仅能防御旧代次，无法防御同代次在基线回调期间关断。

### 5.2 针对性多层防护修复方案
1. **领域层断开代次屏障 (Domain-Level Disconnected Generation Guard in `SharedRoom`)**:
   - 在 `SharedRoom` 中新增 `val seatDisconnectedGenerations = LongArray(4) { 0L }`。
   - 在 `SpectatorMember` 中新增 `@Volatile var lastDisconnectedGeneration: Long = 0L`。
   - `disconnectSeat(seat, expectedSeatGeneration)` 在权威队列执行时，严格记录 `seatDisconnectedGenerations[seat] = expectedSeatGeneration`。
   - `disconnectSpectator(spectatorId, expectedGeneration)` 在权威队列执行时，严格记录 `member.lastDisconnectedGeneration = expectedGeneration`。
   - `leaveSpectator` 移除成员前同样记录 `lastDisconnectedGeneration`。
   - `reconcileConnectivityOnQueue` 发现席位或观战离线清除连接真值时，同样记录对应的断开代次。
   - **核心屏障判定**:
     - `markSeatConnected`: 增加强校验 `expectedGeneration > seatDisconnectedGenerations[seat]`。只要该代次已处理过断线，无论任何延迟回调，均绝对无法反转 `seatConnected`！
     - `markSpectatorConnected`: 增加强校验 `expectedGeneration > member.lastDisconnectedGeneration`，同样杜绝反转断线真值。
2. **权威队列执行期会话有效性终审 (Queue Execution-Time Session Validation)**:
   - `SharedRoom.markSeatConnected` 与 `markSpectatorConnected` 签名扩展为 `(seat: Int, expectedGeneration: Long, sessionValidator: (() -> Boolean)? = null)`，默认值为 `null` 保留已有调用点兼容。
   - 在 TableHost 串行队列内执行时，优先审判 `if (sessionValidator?.invoke() == false) return@enqueueOperation`。
3. **角色层会话校验器细化 (`RoleStates.kt`)**:
   - `PlayerState.makeSessionValidator(transport)` 与 `SpectatorState.makeSessionValidator(transport)` 增加 `!disconnectNotified.get()`。
   - 一旦物理连接或绑定层触发 `notifyDisconnect()`，会话校验器立即且不可逆地返回 `false`，即便通道状态尚未完全传播，队列执行时亦立即拒绝标记。
4. **绑定链路接入会话终审 (`TcpTableServer.kt`)**:
   - `bindPlayerToRoom`: 调用 `room.markSeatConnected(seat, capturedSeatGen, playerState.makeSessionValidator(conn))`。
   - `bindSpectatorToRoom`: 调用 `room.markSpectatorConnected(specId, capturedGen, spectatorState.makeSessionValidator(conn))`。
5. **增补 4 项确定性交错、领域屏障与真实 TCP 回归测试 (`ServerRoleStateTest`)**:
   - `deterministic queue interleaving - player disconnect before delayed mark keeps seat disconnected and idle tracking active`: 确定性模拟玩家连接关闭、`disconnectSeat` 先执行、旧 `markSeatConnected` 后执行，断言 `seatConnected == false`、`seatDisconnectedGenerations` 正确记录、`idleSinceMillis` 活跃生效。
   - `deterministic queue interleaving - spectator disconnect before delayed mark keeps spectator disconnected and idle tracking active`: 确定性模拟观战连接关闭、`disconnectSpectator` 先执行、旧 `markSpectatorConnected` 后执行，断言 `isConnected == false`、观战计数为 0。
   - `domain guard strictly prevents markSeatConnected and markSpectatorConnected from reversing disconnect even with null validator`: 测试即便传入 `sessionValidator = null`，领域代次屏障依然绝对阻止旧 mark 反转断开状态。
   - `real TCP spectator closed right after join receives compensation disconnect and does not leak connected state`: 覆盖真实 TCP 观战客户端建立后立即强关底层 Socket，断言服务端断线补偿与队列排空后观战状态不残留。

---

## 6. 状态所有权矩阵 (State Ownership Matrix)

| 状态对象 / 资源 | 唯一所有者 | 安装 / 转换入口 | 替换与清理责任 | 职责范围 |
|---|---|---|---|---|
| 底层 SocketChannel / SelectionKey | `TcpConnection` | 构造注入 | `TcpConnection.forceCloseChannel()` | 物理字节流传输与 NIO 事件注册 |
| 帧解码器 `TcpFrameDecoder` | `TcpConnection` | 构造创建 | 连接关闭时随连接释放 | 粘包拆包与协议帧解码 |
| 出站队列 `TcpOutboundQueue` | `TcpConnection` | 构造创建 | `TcpConnection.close()` 释放预算并清空 | 有界出站与优先级控制帧缓冲 |
| 串行接收邮箱 `serialMailbox` | `TcpConnection` | 构造创建 | 连接关闭时清空并释放全局预算 | 有界 64 帧 / 4 MiB 接收背压队列 |
| 角色状态 `roleState: ServerRoleState` | `TcpConnection` | `installPlayerRole` / `installSpectatorRole` / `unbindToLobbyRole` | `TcpConnection` 同步块内原子检查并替换；关闭态拒绝安装 | 物理连接与当前逻辑身份的映射锚点 |
| 玩家私有流与房间订阅 | `PlayerState` | `PlayerState.attachSubscriptions()` | `PlayerState.detach()` 负责关闭全部订阅 | 仅对指定席位推送私有手牌与房间更新 |
| 观战公开流与房间订阅 | `SpectatorState` | `SpectatorState.attachSubscriptions()` | `SpectatorState.detach()` 负责关闭全部订阅 | 仅对观战者推送不可变公开更新与房间信息 |
| 成员代次 `bindingGeneration` | `SharedRoom` (权威) / `PlayerState` / `SpectatorState` (捕获) | 房间绑定席位/观战时递增 | 顶号断线时由状态携带比对；会话校验器核对；mark 确认连接核对 | 区分新旧物理连接，防止旧连接断开、旧 mark 或旧操作误杀/误活新连接 |
| 业务请求回执与在途锁 | `RequestScopeReceiptStore` | `TcpTableServer` 业务调度 | 随房间生命周期或超时回收，不随物理断线清除 | 保证业务操作幂等性与跨物理重连回执重放 |

---

## 7. 改动与新增文件清单

1. **`server/src/main/kotlin/com/majiang/server/tcp/TcpConnection.kt` (修改)**
   - 移除连接级 `disconnectNotified: AtomicBoolean`。
   - `installPlayerRole` / `installSpectatorRole` 返回 `Boolean`，并在 `synchronized(this)` 内前置拦截 `isClosingOrClosed`。
   - `unbindToLobbyRole` 与 `setHandshakedIdentity` 增加 `isClosingOrClosed` 检查。
   - `close()` 在 `synchronized(this)` 内原子推进至 `CLOSING`。
   - `forceCloseChannel()` 在 `synchronized(this)` 内原子捕获 `roleToNotify`，并在通道与资源清理完成后调用 `roleToNotify.onConnectionClosed(this)`。
   - `notifyRoomDisconnect()` 委托给当前角色的 `onConnectionClosed(this)`。
2. **`server/src/main/kotlin/com/majiang/server/tcp/TcpTableServer.kt` (修改)**
   - `handleCreateRoom` 传递 `capturedSeatGen` 给 `bindPlayerToRoom`。
   - `handleJoinRoom` 与 `handleResumeSession` 在 `whenComplete` 遭遇连接关闭时，立即调用 `disconnectSeat` 补偿。
   - `bindPlayerToRoom` 增加 `capturedSeatGen: Long` 参数；连接关闭或 `installPlayerRole` 失败时立即执行 `playerState.notifyDisconnect()` 补偿；基线快照错误或关闭时同样断线补偿；向 `room.markSeatConnected` 传入 `playerState.makeSessionValidator(conn)`。
   - `handleJoinSpectator` 与 `handleResumeSpectator` 在 `whenComplete` 遭遇连接关闭时，立即调用 `disconnectSpectator` 补偿。
   - `bindSpectatorToRoom` 在连接关闭或 `installSpectatorRole` 失败时立即执行 `spectatorState.notifyDisconnect()` 补偿；基线快照错误或关闭时同样补偿；向 `room.markSpectatorConnected` 传入 `spectatorState.makeSessionValidator(conn)`。
3. **`server/src/main/kotlin/com/majiang/server/tcp/RoleStates.kt` (修改)**
   - `PlayerState.makeSessionValidator` 与 `SpectatorState.makeSessionValidator` 增加 `!disconnectNotified.get()`。
   - `PlayerState.notifyDisconnect()` 与 `SpectatorState.notifyDisconnect()` 返回 `CompletableFuture<Unit>`，支持确定性等待与断言。
4. **`server/src/main/kotlin/com/majiang/server/room/SharedRoom.kt` (修改)**
   - 增加 `seatDisconnectedGenerations = LongArray(4) { 0L }`。
   - `SpectatorMember` 增加 `@Volatile var lastDisconnectedGeneration: Long = 0L`。
   - `disconnectSeat`、`disconnectSpectator`、`leaveSpectator`、`reconcileConnectivityOnQueue` 记录断开代次。
   - `markSeatConnected` 与 `markSpectatorConnected` 接收可选 `sessionValidator`，在队列执行时校验 `sessionValidator?.invoke() != false` 并强制断开代次屏障（`expectedGeneration > seatDisconnectedGenerations[seat]` / `expectedGeneration > member.lastDisconnectedGeneration`），彻底杜绝反转断开真值。
5. **`server/src/test/kotlin/com/majiang/server/tcp/ServerRoleStateTest.kt` (修改)**
   - 新增 4 项确定性队列交错、领域代次屏障与真实 TCP 观战补偿测试，测试用例数扩充至 **25 项**。
6. **保留的未提交工作区文件**:
   - D-01 客户端文件（`ServerConnectionSession.kt`、`TcpClientBinding.kt`、`PlayerStreamEngine.kt`、`RoomSyncEngine.kt`、`SpectatorStreamEngine.kt` 及相应测试）完整保留，未作任何修改或回滚。
   - `tools/architecture/` 目录探针未受任何修改。

---

## 8. 自动化验证结果与用例统计

所有 Gradle 测试均采用单进程串行并带有 `--rerun-tasks` 执行：

### 8.1 新增角色测试专项 (`ServerRoleStateTest`)
```powershell
.\gradlew.bat :server:test --tests "com.majiang.server.tcp.ServerRoleStateTest" --rerun-tasks --console=plain
```
- **测试结果**: **25/25 PASSED** (100% 通过，耗时 12s)
- **全部用例清单**:
  - `delayed markSpectatorConnected from old generation does not reconnect displaced disconnected spectator` (PASSED)
  - `real TCP spectator cannot execute player-only actions` (PASSED)
  - `deterministic queue interleaving - player disconnect before delayed mark keeps seat disconnected and idle tracking active` (PASSED)
  - `domain guard strictly prevents markSeatConnected and markSpectatorConnected from reversing disconnect even with null validator` (PASSED)
  - `PlayerState subscription lifecycle and idempotent detach` (PASSED)
  - `TcpConnection role transitions from unauthenticated to lobby to player and back to lobby` (PASSED)
  - `atomic coordination - player binding rejected when connection closed before role install with compensation disconnect` (PASSED)
  - `real TCP client closed right after join receives compensation disconnect and does not leak connected state` (PASSED)
  - `TcpTableServer preserves spectator and rejects stale leave when displaced connection executes queued leave` (PASSED)
  - `SpectatorState makeSessionValidator checks closing transport and member generation` (PASSED)
  - `atomic coordination - spectator binding rejected when connection closed before role install with compensation disconnect` (PASSED)
  - `queued old leaveSpectator then new resumeSpectator preserves spectator and rejects stale leave` (PASSED)
  - `PlayerState makeSessionValidator checks generation and transport state` (PASSED)
  - `SpectatorState subscription lifecycle and idempotent detach` (PASSED)
  - `idempotency receipts are attributed to logical identity scope across reconnections` (PASSED)
  - `PlayerState enforces seat bounds 0 to 3` (PASSED)
  - `closing connection cannot resume spectator and displace healthy connection` (PASSED)
  - `real TCP spectator closed right after join receives compensation disconnect and does not leak connected state` (PASSED)
  - `real TCP displacement detaches old role subscriptions without disconnecting new generation` (PASSED)
  - `PlayerState notifyDisconnect is idempotent and generational` (PASSED)
  - `atomic coordination - player binding disconnect notified when connection closed after role install` (PASSED)
  - `atomic coordination - spectator binding disconnect notified when connection closed after role install` (PASSED)
  - `SpectatorState enforces non-blank spectatorId` (PASSED)
  - `snapshotSupplier and publicSnapshotSupplier bind to captured scope without reading mutated conn role` (PASSED)
  - `deterministic queue interleaving - spectator disconnect before delayed mark keeps spectator disconnected and idle tracking active` (PASSED)

### 8.2 服务端全量测试回归 (`:server:test`)
```powershell
.\gradlew.bat :server:test --rerun-tasks --console=plain
```
- **测试结果**: **270/270 PASSED, 0 failures, 0 skipped** (耗时 46s)
- 包含所有 TCP 握手、容量控制、心跳保活、房间生命周期、绝技（落汗/藏花/换柱）端到端集成、InProcess 双传输与连接控制回归测试。

### 8.3 协议层全量测试 (`:protocol:test`)
```powershell
.\gradlew.bat :protocol:test --rerun-tasks --console=plain
```
- **测试结果**: **174/174 PASSED, 0 failures, 0 skipped** (耗时 9s)

### 8.4 客户端测试健康核验 (`:client:test`)
```powershell
.\gradlew.bat :client:test --rerun-tasks --console=plain
```
- **测试结果**: **100% PASSED** (耗时 32s，保留 D-01 全部改动无衰退)

### 8.5 Codex TCP 当前探针独立复验 (`tools/architecture/tcp-current-review.init.gradle`)
```powershell
.\gradlew.bat -I tools/architecture/tcp-current-review.init.gradle :server:test :client:test --rerun-tasks --console=plain
```
- **测试结果**: **22/22 PASSED** (耗时 12s)

### 8.6 V4 架构规范与边界复验 (`tools/architecture/v4-review.init.gradle`)
```powershell
.\gradlew.bat -I tools/architecture/v4-review.init.gradle -PreviewOnly=true :server:test --tests "com.majiang.v4*.*" --tests "com.majiang.mr09scan.*" --rerun-tasks --console=plain
```
- **测试结果**: **ALL PASSED, 0 failures** (耗时 13s)

---

## 9. 未完成事项与后续风险说明

1. **未完成事项**:
   - D-03（服务端绑定规则统一与 InProcess/TCP 参数化场景复用）及 D-08（请求路由拆分与会话上下文）按计划属于后续任务批次，本次除 D-02 角色分型所必需的改动外未作任何扩大化重构。
2. **风险与注意事项**:
   - 工作区包含 D-01 与 D-02 改动，尚未 commit/push。请 Codex 在独立检视时保留现有工作区所有文件。
   - 所有物理连接与角色状态的生命周期边界现已完全封闭，后续 D-03 可平滑接入统一绑定规则。
