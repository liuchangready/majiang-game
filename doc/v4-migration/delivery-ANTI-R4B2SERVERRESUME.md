# 交付报告：R4B2SERVERRESUME (Anti)

## 1. 任务概述
接续并完整交付 V4 迁移任务 `R4B2SERVERRESUME`（服务端观战功能完整支持）。严格遵守用户授权及开发边界：独占修改 `server/src/main` 与相应 server 模块测试，保持 `protocol` 冻结；仅使用内置文件读写/编辑工具，不执行任何 Shell、Gradle 构建或测试命令。

---

## 2. 实际修改文件清单
1. [`server/src/main/kotlin/com/majiang/server/udp/UdpRoom.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/udp/UdpRoom.kt)
2. [`server/src/main/kotlin/com/majiang/server/udp/UdpTableServer.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/udp/UdpTableServer.kt)
3. [`server/src/main/kotlin/com/majiang/server/room/SharedRoom.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/room/SharedRoom.kt)
4. [`server/src/main/kotlin/com/majiang/server/host/ServerTableHost.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/host/ServerTableHost.kt)
5. [`server/src/main/kotlin/com/majiang/server/inprocess/InProcessBinding.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/inprocess/InProcessBinding.kt)
6. [`server/src/test/kotlin/com/majiang/server/inprocess/InProcessBindingTest.kt`](file:///D:/majiang/server/src/test/kotlin/com/majiang/server/inprocess/InProcessBindingTest.kt)
7. [`server/src/test/kotlin/com/majiang/server/udp/UdpRoomTest.kt`](file:///D:/majiang/server/src/test/kotlin/com/majiang/server/udp/UdpRoomTest.kt)

---

## 3. 核心修复与生产接线实现

### 3.1 编译与常量修复
- **`UdpRoom` AtomicReference 读取**：修复 `lastPublishedPublicSnapshot.get()` 读取与 `updatePublishedPublicSnapshot` CAS 逻辑。
- **`UdpTableServer` PUBLIC 重试常量**：补齐 `PUBLIC_UPDATE_RETRY_BASE_MILLIS = 100L`、`PUBLIC_UPDATE_MAX_ATTEMPTS = 3`、`PUBLIC_UPDATE_REFRESH_MILLIS = 3000L`。

### 3.2 UdpTableServer 会话路由、权限隔离与生命周期管理
- **代次核对分派**：`handleSessionReq` 中按角色分别核对 `room.getSpectatorGeneration(session.spectatorId)` 与 `room.getConnectionGeneration(session.seat)`，彻底根除观战 `seat = -1` 被误判为 `INVALID_TOKEN` 的问题。
- **权限先于缓存与防重放**：
  - 观战会话禁止调用玩家私有 API（`SET_READY`, `UPDATE_CONFIG`, `SNAPSHOT`, `DECISION`, `SUBMIT_CHOICE`, `SUBMIT_SHUFFLE_PLAN`, `START_MATCH`, `NEXT_ROUND`, `RESUME_CURSOR`, `CLAIM_CONTROL`, `RELEASE_CONTROL`, `QUERY_ROUND_RESULT`），一律返回 `UNAUTHENTICATED_SESSION`；
  - 玩家会话禁止调用观战 PUBLIC API（`PUBLIC_SNAPSHOT`, `RESUME_PUBLIC`, `QUERY_PUBLIC_ROUND_RESULT`），一律返回 `UNAUTHENTICATED_SESSION`；
  - 已绑定会话禁止调用大厅 API（`CREATE_ROOM`, `JOIN_ROOM`, `RESUME_SESSION`, `JOIN_SPECTATOR`, `RESUME_SPECTATOR`, `LIST_ROOMS`），返回 `ALREADY_BOUND`；
  - 所有校验前置在幂等缓存命中之前，杜绝非法/被顶替 session 凭历史缓存回执非法操作。
- **完整会话期路由分派**：
  - `LEAVE_ROOM` 分派给 `room.leaveSpectator(session)`（观战）与 `room.leave(session)`（玩家）；
  - 新增 `PUBLIC_SNAPSHOT` 路由（返回 `publicSnapshot` 字段）；
  - 新增 `RESUME_PUBLIC` 路由（严格校验 `publicCheckpoint` 并经 `room.resumePublicFor` 返回全量基线）；
  - 新增 `QUERY_PUBLIC_ROUND_RESULT` 路由（经 `room.queryPublicRoundResult` 返回 `publicEvents` 字段，严格禁止混用玩家 `events` 字段）。
- **终结清理与装配**：
  - `createRoom` 完整装配 `publicUpdateSender = { session, update -> sendPublicUpdate(session, update) }`；
  - `tickRooms()` 周期兜底调用 `refreshPendingPublicPushes(clock.currentTimeMillis())`；
  - `sendKick`、`terminateSession` 以及 `close()` 中均对 `inFlightPublicPushes` 和 `highestSentPublicCursor` 进行确定性清理。

### 3.3 严格 Checkpoint 与 PUBLIC UDP 可靠恢复
- **三字段严格校验**：`ServerTableHost.performValidatePublicCheckpointOnQueue` 与 `UdpRoom.handlePublicResend` 严格校验 `sessionId`、`syncEpoch` 和 `cursor`：拒绝 `cursor < 0L`、`cursor > published.cursor` 以及 `cursor == Long.MAX_VALUE` 溢出；
- **256 窗口重传与全量自愈**：窗口内按序补发，窗口外全量 FullSnapshot；补发帧注册至 `inFlightPublicPushes` 并启用 `useStableSeq = true` 与退避重传；
- **ACK 精确收敛**：`handleAck` 按稳定消息 seq 单独收敛 `inFlightPublicPushes`，绝不误清 ROOM_UPDATE、KICK 或玩家推送；
- **注册后下发 PUBLIC 基线 (反馈点 1)**：`UdpRoom.sendSpectatorBaseline` 在发送房间信息后，主动提取最新不可变基线并通过 `publicUpdateSender` 下发，防止快照获取与全局注册窗口间的更新丢失；
- **防关房复活与乱序覆盖 (反馈点 2)**：
  - `UdpRoom.installSpectatorBinding` 和 `resumeSpectator` 锁内核对 `closed` 以及 `sharedRoom.spectators[spectatorId]?.generation == generation`；失败时撤销加入成员并抛出异常；
  - `UdpTableServer` 在 `JOIN_SPECTATOR` 与 `RESUME_SPECTATOR` 的异步完成回调中，前置校验 `isClosed.get() || room.closed || !room.isSpectatorBindingCurrent(session)`，失败时安全关闭 session 并返回 `ROOM_NOT_FOUND` / `INVALID_TOKEN`，绝不注册孤儿连接。

### 3.4 InProcessBinding 异步提交边界与互斥保护
- **`spectatorRequestSeq` 认领与代次防覆盖**：`joinSpectator` 与 `resumeSpectator` 在 `stateLock` 锁内递增排他请求代次，前置校验角色未绑定与非玩家身份；
- **`tryClaimSpectatorBinding` 原子认领**：异步完成时在 `stateLock` 锁内重新核对本地代次、本地请求代次、未绑定状态、非玩家状态以及 `targetRoom.spectators[specId]?.generation == specGen`；迟到回调若被超前则直接抛出 `DISPLACED_BY_NEW_CONNECTION`，若为 join 则主动撤销在 SharedRoom 注册的成员杜绝泄漏；
- **锁外安装订阅与门控兜底**：订阅在锁外安全安装，安装后立即做当前代次一致性门控核对；
- **角色互斥与不可静默抢占**：观战连接调用 `createRoom`, `joinRoom`, `resumeSession` 明确拒绝；玩家连接调用 `joinSpectator`, `resumeSpectator` 明确拒绝；
- **`SpectatorMember` 跨线程可见性**：`resumeToken` 与 `generation` 增加 `@Volatile`，代次自增后重新发布回 `spectators` ConcurrentHashMap。

---

## 4. 补充模块回归测试
1. **[`InProcessBindingTest.kt`](file:///D:/majiang/server/src/test/kotlin/com/majiang/server/inprocess/InProcessBindingTest.kt)**：
   - `spectator join and public snapshot and role isolation`：满 4 席加入观战、PUBLIC 只读快照、私有接口全面拒绝、大厅接口互斥拒绝、显式离开回到 UNBOUND；
   - `spectator resume displaces old connection actively without next game actions`：恢复观战主动终结旧连接并收到 `DISPLACED_BY_NEW_CONNECTION`，旧连接 close 不影响新连接；
   - `concurrent resume spectator deterministic race rejects superseded request`：利用 `onSpectatorDisplaced` 门闩阻塞第一次恢复，待第二次恢复完成后释放，验证旧完成被安全拒绝且不破坏新代次。
2. **[`UdpRoomTest.kt`](file:///D:/majiang/server/src/test/kotlin/com/majiang/server/udp/UdpRoomTest.kt)**：
   - `testSpectatorJoinResumeAndCheckpointResend`：满 4 席加入观战、严格 checkpoint 全三字段、超前/负数/溢出游标拒绝、合规重发与全量自愈、合法 token 恢复与非法 token 拒绝；
   - `testUdpTableServerSpectatorEndToEnd`：真实 UDP 数据报 REQ/RSP 端到端测试观战加入、PUBLIC 快照获取、玩家/观战角色权限隔离拦截、LEAVE_ROOM 正常注销。

---

## 5. 未完成项
- **无**。本任务分配的服务端所有技术指标与反馈问题（含 FEEDBACK-R4B2SERVERRESUME.md 两项竞态）均已全部落盘实现。

---

## 6. 测试运行声明
- **根据 Anti 开发者角色分工与严格限制指令，本地未执行任何 Gradle 构建与测试命令（未运行 `./gradlew test` 或终端进程）**。
- 代码完全落盘交付，由 Codex 统一执行后续构建、模块回归与整体验收。
