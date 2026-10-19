# Anti 开发进度跟踪 (ANTI-PROGRESS.md)

- **状态**: D03_D08_INTEGRATED / CODEX_VERIFIED_ALL_GREEN（已提交并推送）
- **当前批次交付文档**: `doc/v4-重构/ANTI-D03-DELIVERY.md`、`doc/v4-重构/ANTI-D08-DELIVERY.md`；主工作区整合见 `doc/v4-重构/CODEX-D03-D08-INTEGRATION.md`
- **Codex 整合复验（2026-10-19）**: D-08 隔离工作树成果已并入主工作区；D-03/D-08 专项和无缓存全量通过（server 330、client 217、protocol 174，共 721 项），TCP 独立探针通过。代码提交 `e500ce1` 已推送至 `origin/feat/mj-tcp-migration`。D-04～D-07、D-09 与 T4 环境实测仍待完成。
- **基线**: `cca271a0b5c2a257ad7b3affda6bb14eed221ebd` (HEAD)
- **更新时间**: 2026-10-19（Anti 完成 D-03 服务端统一绑定规则第7轮返修：1) 将 `SharedRoom.kt` 中 `SpectatorMember` 的可变代次、令牌及映射写权限彻底收回内部，封装为私有 `InternalSpectatorMember` 与 `spectatorsMap`；对外暴露无任何公共可变字段且彻底剥离 `resumeToken` 的不可变视图 `SpectatorMemberView` 以及 `Collections.unmodifiableMap`；提供强类型只读访问器 `spectatorMemberGeneration`、`isSpectatorConnected`、`spectatorLastDisconnectedGeneration` 等；2) 审计并封装席位令牌与断开代次，`resumeTokens` 与 `seatDisconnectedGenerations` 统一返回防御性副本，提供 `seatResumeToken(seat)` 与 `seatDisconnectedGeneration(seat)`，使用篡改 token 恢复必定被权威拦截（`INVALID_RESUME_TOKEN`）；3) 调整使用方（`InProcessBinding.kt`、`TcpTableServer.kt`、`ServerRoleStateTest.kt`），彻底移除测试中对房间成员的直接越权赋值篡改，一律通过真实准入与恢复接口驱动；4) 扩展防篡改与真实顶号代次失效测试（`DualTransportBindingScenarioParameterizedTest`）；无缓存串行全量通过：双传输参数化 27/27 项全绿；`:server:test` 300/300 项全绿；`:client:test` 217/217 项全绿；Codex 独立探针通过；物理守恒 136 张保持不变，未动 UDP/V3 与 D-08 隔离工作树，无 commit/push）
  - **D-03 返修与交付核心内容**：
    1. **三层代次/纪元所有权与防篡改闭环**：
       - `SharedRoom` 独占拥有席位成员代次、观战者成员代次、断开代次屏障与恢复令牌；内部私有化存储，对外公开字段统一返回防御性副本或只读视图（`Collections.unmodifiableMap`，强转操作抛 `UnsupportedOperationException`）；彻底移除任何绕过准入和权威队列的 public 写入口或代次推进接口；
       - `InProcessBinding` 拥有本地绑定代次 `localBindingGeneration`，通过 `currentBindingContext` 与 `checkCurrentBindingDisplacement()` 在在途恢复完成时补核代次，保证顶号通知绝不遗漏；
       - `TcpConnection` 拥有底层物理 `connectionEpoch`，物理连接的关闭不影响绑定于 `logicalPrincipal` 的业务回执与作用域。
    2. **权威队列执行期终审复核与真实双传输排队交错**：
       - 所有对局、配置、离房操作均在 TableHost 单桌串行队列执行期通过 `sessionValidator` 检验代次与连接状态，代次过时即拒绝（`STALE_SESSION`），绝不污染领域状态或误断新连接；双传输真实顶号排队交错测试严格验证无副作用、旧连接及时断开与新连接保活。
    3. **12 大场景双传输参数化测试覆盖 (`DualTransportBindingScenarioParameterizedTest`)**：
       - 涵盖玩家顶号、观战者顶号、旧命令排队复核、真实传输入口断开与延迟回调屏障、纯领域断开代次屏障防误活、重复离房幂等 APPLIED、观战者重复离房与重加、已顶替旧连接离房被拒、房主离房终结房间生命周期、角色权限严格隔离、业务 requestId 幂等重放与身份隔离、玩家在途恢复与顶号交错受控屏障回归、观战者在途恢复与顶号交错受控屏障回归、代次与成员防篡改回归测试。IN_PROCESS 与 TCP 双传输模式 27 项用例 100% 通过。
    4. **全量测试证据**：
       - `DualTransportBindingScenarioParameterizedTest`：27/27 PASSED (16s with `--rerun-tasks`)
       - `:server:test --rerun-tasks`：300/300 PASSED (0 failures, 0 errors, 0 skipped, BUILD SUCCESSFUL in 46s)
       - `:client:test --rerun-tasks`：217/217 PASSED (0 failures, 0 errors, 0 skipped, BUILD SUCCESSFUL in 32s)
       - Codex TCP 探针独立复验（`tcp-current-review.init.gradle`）：PASSED (5s)
       - `:protocol:test` 与 `:engine:test`：100% PASSED
  - **D-02 第4轮返修与完成内容**：
    1. **领域层断开代次屏障 (`SharedRoom`)**：
       - 在 `SharedRoom` 中维护 `seatDisconnectedGenerations = LongArray(4) { 0L }`，并在 `SpectatorMember` 中新增 `@Volatile var lastDisconnectedGeneration: Long = 0L`。
       - 在 `disconnectSeat`、`disconnectSpectator`、`leaveSpectator` 与 `reconcileConnectivityOnQueue` 中严格记录已断开代次。
       - `markSeatConnected` 与 `markSpectatorConnected` 强制断开代次屏障（`expectedGeneration > seatDisconnectedGenerations[seat]` 与 `expectedGeneration > member.lastDisconnectedGeneration`），即便 validator 缺省或延迟回调排队，也绝对无法反转已断开的连接状态。
    2. **权威队列执行期会话有效性终审**：
       - `markSeatConnected` 与 `markSpectatorConnected` 签名扩展为支持 `sessionValidator: (() -> Boolean)? = null`，在 TableHost 串行队列执行时进行终审判定。
       - `PlayerState.makeSessionValidator` 与 `SpectatorState.makeSessionValidator` 增加 `!disconnectNotified.get()`。一旦断线通知触发，校验器立即且不可逆地返回 `false`。
       - `bindPlayerToRoom` 与 `bindSpectatorToRoom` 传入对应角色的会话校验器。
    3. **增补与全量测试全绿**：
       - `ServerRoleStateTest`：**25/25 项测试通过**（新增 4 项交错与领域代次屏障测试，耗时 12s）。
       - `:server:test --rerun-tasks`：**270/270 项全部通过**（0 failures, 0 skipped，耗时 46s）。
       - `:protocol:test --rerun-tasks`：**174/174 项全部通过**（耗时 9s）。
       - `:client:test --rerun-tasks`：**100% 通过**（耗时 32s，保留 D-01 全部改动）。
       - Codex TCP 探针独立复验（`tcp-current-review.init.gradle`）：**22/22 项全部通过**（耗时 12s）。
       - V4 架构规范复验（`v4-review.init.gradle`）：**全部通过**（耗时 13s）。
  - **D-02 第3轮返修与完成内容**：
    1. **角色安装与连接关闭原子协调**：
       - `TcpConnection.installPlayerRole` 与 `installSpectatorRole` 在 `synchronized(this)` 内前置检查 `isClosingOrClosed`，若已关闭/正在关闭则拒绝安装并返回 `false`。
       - `unbindToLobbyRole` 与 `setHandshakedIdentity` 在同步块内同样杜绝向关闭连接安装新角色。
       - 彻底移除 `TcpConnection.disconnectNotified: AtomicBoolean`，解绑连接级断线门控；由 `PlayerState` 与 `SpectatorState` 自身独立维护的原子标志保证自身代次的精确一次断线通知；`forceCloseChannel()` 在进入 `CLOSED` 态时同步捕获当时的 `roleState` 并触发其 `onConnectionClosed`。
    2. **全链路精确断线补偿 (Exact Disconnect Compensation)**：
       - `bindPlayerToRoom`: 传入 `capturedSeatGen`；若 `conn.isClosingOrClosed` 或 `!conn.installPlayerRole(playerState)`，立即调用 `playerState.detach()` 与 `playerState.notifyDisconnect()`（调用 `room.disconnectSeat(seat, capturedSeatGen)`）；基线快照回调发生错误或处于 closing 时同样执行断线补偿。
       - `handleJoinRoom` 与 `handleResumeSession`: 在 `whenComplete` 阶段，若发现 `conn.isClosingOrClosed`，立即调用 `targetRoom.disconnectSeat(seat, generation)` 补偿。
       - `bindSpectatorToRoom`: 若 `conn.isClosingOrClosed` 或 `!conn.installSpectatorRole(spectatorState)`，调用 `spectatorState.detach()` 与 `spectatorState.notifyDisconnect()`（调用 `room.disconnectSpectator(specId, generation)`）；基线快照回调发生错误或处于 closing 时同样补偿。
       - `handleJoinSpectator` 与 `handleResumeSpectator`: 在 `whenComplete` 阶段，若发现 `conn.isClosingOrClosed`，立即调用 `targetRoom.disconnectSpectator(res.spectatorId, res.generation)` 补偿。
       - `PlayerState.notifyDisconnect()` 与 `SpectatorState.notifyDisconnect()` 返回 `CompletableFuture<Unit>`，支持确定性等待与断言。
  - **D-01 交付记录**: `doc/v4-重构/ANTI-D01-DELIVERY.md`（保留工作区修改）
  - **D-01 第2轮返修与验证内容**：
    1. **TcpClientBinding R04 确定性根因修复**：
       - 根因定位：`pumpReplayQueue()` 原在 `handleResponseEnvelope` 同步直接调用，此时 `currentDispatchFrame` 仍在 `drainInboundMailbox` 的 `dispatchFrame` 临界区内未释放；在重连且并发重放 64 条请求的高吞吐场景下，新请求的 RSP 瞬间返回，导致底层 Selector 线程计算 `heldFrames = inboundMailbox.size (63) + 1 = 64`，直接命中 `>= maxInboundMailboxFrames (64)`，被误判为 `SLOW_CONSUMER` 强制杀连，致使流水线断裂、后续请求无法重放。
       - 修复：将 `pumpReplayQueue()` 移入 `drainInboundMailbox` 的 `finally` 块中，在 `currentDispatchFrame = null` 彻底释放持有帧与字节预算后才触发；移除 `handleResponseEnvelope` 中的过早调度。
       - 验证：`TcpClientBindingRegressionTest.R04` 与 `TcpClientBindingRegressionTest` 全量 45 项单测在无缓存 `--rerun-tasks` 下 100% 稳定通过。
    2. **PlayerStreamEngine 与 SpectatorStreamEngine 换绑原子边界修复**：
       - 根因定位：原实现在 `ServerConnectionSession` 锁外读取 `isBindingInProgress` / `isSpectatorBindingInProgress`，且 engines 在各自锁外向 early buffer 写入。若入站推送在读取标记为 true 后被抢占，主线程持锁安装快照并排水后清标记，入站线程随后将推送压入 early buffer，该推送将永久滞留早期缓冲无法推进。
       - 修复：将 `_isBindingInProgress` 与 `_isSpectatorBindingInProgress` 彻底收归各自引擎所有，由 `syncLock` 与 `publicSyncLock` 原子保护。提供 `beginBindingUnderLock()`、`cancelBindingUnderLock()`，在 `installInitialSnapshotUnderLock` 内部原子排空早期缓冲并将标记置 false；`handleInboundUpdate` 与 `handleInboundPublicUpdate` 的标记检查、缓冲写入、快照匹配与递增推进全流程收入同一把锁内。
       - 补齐交错回归测试：
         - `PlayerStreamEngineTest.testInterleavedBindingInstallationAndInboundPushDoesNotStrandEarlyBuffer`
         - `SpectatorStreamEngineTest.testInterleavedSpectatorBindingInstallationAndInboundPushDoesNotStrandEarlyBuffer`
    3. **全量无缓存测试验证 (--rerun-tasks)**：
       - `.\gradlew.bat :client:test --rerun-tasks --tests "com.majiang.client.transport.tcp.TcpClientBindingRegressionTest.R04*"` (PASSED)
       - `.\gradlew.bat :client:test --rerun-tasks --tests "com.majiang.client.session.*" --tests "com.majiang.client.api.ServerConnectionSessionTest"` (PASSED)
       - `.\gradlew.bat :client:test --rerun-tasks --tests "com.majiang.client.transport.tcp.TcpClientBindingRegressionTest"` (45 passed)
       - `.\gradlew.bat :client:test :protocol:test :server:test --rerun-tasks --console=plain` (17 tasks executed, 100% PASSED in 49s)
  - **历史阶段记录**:
    - **MR-01**: 已提交 `35a87c6` 并验收通过（已推送）。
    - **MR-02**: 已提交 `8a4b59f` 并验收通过（已推送）。
    - **MR-03**: 已提交 `3a382c6` 并验收通过（未推送）；验收记录 `doc/v4-重构/CLAUDE-MR03-REVIEW.md`。
    - **TCP-T1**: 开发+一轮返修后验收通过，提交 `1664c60`，见验收记录 `doc/v4-重构/CLAUDE-TCP-T1-REVIEW.md`。
    - **MR-06/07/09**: 返修完成，交付报告 `doc/v4-重构/ANTI-MR06-MR09-REWORK-DELIVERY.md`。
    - **MR-10**: 测量驱动优化完成，交付报告 `doc/v4-重构/ANTI-MR10-DELIVERY.md`。
- **基线SHA**: `cca271a0b5c2a257ad7b3affda6bb14eed221ebd` (HEAD)
