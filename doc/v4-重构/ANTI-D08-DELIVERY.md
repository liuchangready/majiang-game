# 🀄 D-08 TCP 请求路由与业务回执管理拆分交付报告 (ANTI-D08-DELIVERY.md)

- **任务编号**: D-08 (§9.2, IMPROVEMENT-PLAN.md)
- **开发负责人**: Anti
- **检视与整合负责人**: OpenAI Codex (独立检视与验收)
- **隔离工作区**: `D:\majiang-d08-20261019` (Git worktree，基于 `cca271a0b5c2a257ad7b3affda6bb14eed221ebd`，包含 D-01/D-02 全部未提交基线改动)
- **交付时间**: 2026-10-19
- **状态**: COMPLETED / READY_FOR_CODEX_VERIFICATION (第 5 轮独立复验缺陷返修完成，MR10 载荷采集确定性与锁不变量清理完成，连续串行全量复跑全绿)

---

## 1. 架构目标与职责边界划分

根据 `IMPROVEMENT-PLAN.md` §9.2、§9.3 及任务指示，原先积压在 `TcpTableServer.kt`（超过 2,140 行）中的请求路由、业务身份上下文提取、业务回执/在途管理与连接控制被彻底解耦拆分为具备单一职责、明确所有权与清晰关闭流程的独立组件：

```
                ┌──────────────────────────────────────────────────┐
                │                  TcpTableServer                  │
                │  - Listener / Selector / NIO 事件循环             │
                │  - TcpGlobalBudget (全局网络预算 256 MiB)        │
                │  - 物理连接表 connections (TcpConnection)         │
                │  - 未握手连接桶 (unhandshakedCount, 64)          │
                │  - 心跳/超时代次巡检 (performTimerChecks)        │
                │  - 组合装配与统一关机协调                          │
                └────────┬─────────────────────────────┬───────────┘
                         │                             │
        握手认证与身份上下文提取                            │ 转发请求信封
                         ▼                             ▼
       ┌───────────────────────────────┐     ┌──────────────────────────────────┐
       │     LogicalIdentityContext    │     │         TcpRequestRouter         │
       │  - principal: String          │────>│  - 显式依赖 (conn, identity,      │
       │  - roleState: ServerRoleState │     │    envelope, rawPayload)         │
       │  - requestScope: String       │     │  - withPlayer / withSpectator    │
       │  - 强类型角色/席位/房间派生访问器  │     │    角色专属边界拦截              │
       │  - 不可变值对象，杜绝万能Context  │     │  - 房主专属权限校验 (seat == 0)   │
       │  - 与物理连接安装锁同一原子边界   │     │  - 经 SharedRoom/ServerTableHost │
       │  - 严格校验 principal==角色身份   │     │    提交单桌权威串行队列           │
       └───────────────────────────────┘     │  - ConnectionDisplacer 顶号协调  │
                                             │  - 原子基线推送与出站顺序保证    │
                                             └───────────────┬──────────────────┘
                                                             │ 幂等准入 / 在途关联 / 回执完成
                                                             ▼
                                             ┌──────────────────────────────────┐
                                             │        TcpReceiptManager         │
                                             │  - RequestScopeReceiptStore 封装 │
                                             │  - 跨连接身份与作用域在途所有权  │
                                             │  - 跨连接业务回执安全重放        │
                                             │  - 房间存活新鲜度联动校验        │
                                             │  - 跨 API duplicate requestId 冲突│
                                             │  - GET_REQUEST_SCOPE 限速与凭据  │
                                             │  - 读写锁原子生命周期与条件移除  │
                                             │  - 锁外完成用户 Future 杜绝死锁  │
                                             │  - 严格释放大厅副作用预留配额    │
                                             │  - 读锁不变量保障与不可达死码消除│
                                             └──────────────────────────────────┘
```

---

## 2. 核心组件设计与职责规范

### 2.1 已验证逻辑身份上下文 (`LogicalIdentityContext.kt`)
- **定位**: 纯粹、不可变的逻辑身份上下文值对象。
- **状态所有权**:
  - `val principal: String` (如 `"lobby:<token>"`, `"player:<roomId>:<seat>"`, `"spectator:<roomId>:<specId>"`)；
  - `val roleState: ServerRoleState` (来自 D-02 强类型角色分型树：`LobbyRoleState`, `PlayerState`, `SpectatorState`, `UnauthenticatedRoleState`)；
  - `val requestScope: String` (本次请求出示的作用域字符串)。
- **派生属性与类型安全访问器**:
  - `isLobby`, `isPlayer`, `isSpectator`, `isUnauthenticated`, `role`；
  - `playerState: PlayerState?`, `spectatorState: SpectatorState?`, `lobbyState: LobbyRoleState?`；
  - `room: SharedRoom?`, `seat: Int?` (`0..3`), `spectatorId: String?`, `bindingGeneration: Long?`。
- **原子快照与安装锁一致性保证（第 4 轮复验加固）**:
  - **安装锁原子边界一致**: `LogicalIdentityContext.fromConnection(conn, requestScope)` 严格通过 `tcpConn.snapshotLogicalIdentity()` 在物理连接的安装锁（`synchronized(tcpConn)`）内原子提取 `(principal, roleState)`；
  - **杜绝瞬态交错**: 彻底消除原先分步读取导致的“旧 principal + 新 PlayerState”竞态快照；
  - **不可变强校验 (Invariants)**: 构造方法与快照提取强制核验 `require(principal == roleState.logicalPrincipal)` 与 `require(roleState !is UnauthenticatedRoleState)`；
  - **作用域隔离不可穿透**: 当连接发生角色迁移后（如大厅→玩家、或玩家→大厅），旧 scope 与新 roleState 的 principal 必然不匹配，`RequestScopeReceiptStore.acquireAdmission` 判定 `scopeRecord.principal != key.principal` 返回 `ScopeInvalid`，由 `TcpRequestRouter` 直接拒绝为 `ApiErrors.UNAUTHENTICATED_SESSION`，绝对禁止旧 scope 越权驱动新角色动作。
- **反腐化约束**:
  - **严禁万能 Context**: 不持有任何底层 `SocketChannel`、`Selector`、`TcpGlobalBudget` 或出站缓冲队列等网络传输资源；
  - **无循环父依赖**: 不持有 `TcpTableServer` 或 `TcpRequestRouter`。

### 2.2 业务回执与在途跨连接管理器 (`TcpReceiptManager.kt`)
- **定位**: 独立封装 `RequestScopeReceiptStore`，专职管理业务请求在途（In-Flight）生命周期、跨物理连接的幂等重放与作用域凭据颁发。
- **跨连接在途所有权解耦 (In-Flight Ownership Decoupling)**:
  - 彻底消除了原先将底层在途 `future` 挂载在单次物理连接 `TcpConnection.inFlightRequests` 上的缺陷；
  - 在途条目归属于已认证逻辑身份与 `(principal, scope, requestId)` 幂等三元组；
  - 当物理连接因网络抖动中断时，底层正在执行的单桌权威业务操作不被中断；重连连接使用相同身份和 `requestId` 入队时，命中 `AdmissionResult.WaitInFlight`，无缝挂接到正在执行的同一 `future`，结果完成后双方均可安全消费。
- **回执新鲜度联动 (Receipt Freshness Check, MR-06 / R06-T10)**:
  - 注入 `isRoomOpen: (roomId: String) -> Boolean`；
  - 在重放涉及房间创建或加入的回执时，若目标房间已被回收或关闭，自动转译为 `ApiErrors.ROOM_NOT_FOUND`，杜绝已死房间回执复活。
- **继承 MR-03 完整契约**:
  - **FIFO 淘汰治理**: 普通查询结果按容量有限 FIFO 淘汰，有效期内副作用回执绝对不淘汰；
  - **前置准入预留**: 成员并发在途超限（64 条）或大厅副作用超限时，在受理前原子拒绝并返回 `ApiErrors.SERVER_BUSY`；
  - **跨 API 相同 requestId 冲突拦截**: 严格检测同 `requestId` 但 API 不同的请求，命中返回 `ApiErrors.CONFLICT`；
  - **作用域到期未决对账**: 未经回执记录且已过期的作用域调用直接拦截，返回 `ApiErrors.REQUEST_SCOPE_EXPIRED`；
  - **GET_REQUEST_SCOPE 限速控制**: 每秒至多 1 次限速防护，限速触发返回 `ApiErrors.SERVER_BUSY`。
- **生命周期门控与锁外 Future 完成机制（第 3/5 轮复验加固）**:
  - **原子状态变更与比较删除**: 在 `lifecycleLock` 保护下完成 `activeInFlightEntries` 按 `(key, inFlight)` 身份条件移除与 `RequestScopeReceiptStore` 状态归档/配额释放；
  - **锁外执行用户回调 (R12)**: `inFlight.future.complete` 与 `completeExceptionally` 绝不处于 `lifecycleLock` 读锁或写锁内执行；读锁/写锁先行释放后再触发 Future 完成，彻底根除当外部回调同步调用 `close()` 时因 `ReentrantReadWriteLock` 无法读锁升级导致的永久死锁；
  - **读锁不变量与不可达死码消除（第 5 轮复验）**: `acquireAdmission()` 全程受 `lifecycleLock.readLock()` 保护，而 `close()` 独占 `writeLock`。在读写锁互斥语义下，进入读锁后 `isClosed.get()` 绝不可能变为 `true`，`activeInFlightEntries.putIfAbsent` 登记与准入严格原子生效。移除了原先不可达的 `rollbackInFlight?.future?.completeExceptionally(...)` 死码，代码边界清晰严谨；
  - **唯一胜者保护**: 若在途条目已被 `close()` 处理，后续迟到完成直接放弃并不修改 store，绝不向 store 归档关闭后的副作用；
  - **精准容量扣减**: `InFlightReceipt` 固化 `val isLobby: Boolean`，并在 `failInFlight` / `completeReceipt` 中自动推导 `effectiveIsLobby = isLobby || inFlight.isLobby`，保证关机后大厅预留计数严格归零。

### 2.3 TCP 请求路由调度器 (`TcpRequestRouter.kt`)
- **定位**: 纯粹的业务路由调度器，将入站请求信封解析并向权威单桌串行队列或大厅调度提交。
- **路由入口**:
  - `fun routeRequest(connection: TransportConnection, identity: LogicalIdentityContext, envelope: TcpRequestEnvelope, rawPayload: String)`。
- **强类型角色边界隔离 (D-02)**:
  - 内部高阶函数 `withPlayer(connection, identity, key, req, inFlight, isLobby) { player -> ... }`：未绑定席位（包括观战者或大厅连接）非法调用对局动作直接拦截，返回 `ApiErrors.UNAUTHENTICATED_SESSION`；`player.seat` 保证严格处于 `0..3`，`seat = -1` 哨兵值绝不流入业务层；
  - 内部高阶函数 `withSpectator(connection, identity, key, req, inFlight, isLobby) { spectator -> ... }`：观战者专属操作严格核验；
  - 房主专属权限校验：`UPDATE_CONFIG` 与 `START_MATCH` 强校验 `player.seat == 0`，非房主调用返回 `ApiErrors.UNAUTHENTICATED_SESSION`。
- **原子基线推送保证 (UC-S01)**:
  - 在单桌权威队列内原子完成席位/观战绑定与基线快照获取；
  - 携带有明确 `isFullSnapshot = true` 的初始响应信封优先入队出站队列，然后再安装事件更新监听器，确保首包必为全量基线，后续帧顺序绝对单调。
- **顶号与旧连接卸载 (Displacement)**:
  - 经由注入的 `ConnectionDisplacer` 接口，在席位或观战恢复（RESUME）成功时，向被顶旧连接发送 `DISPLACED_BY_NEW_CONNECTION` 关闭帧并清理旧订阅，不持有外部父对象的私有引用。

### 2.4 轻量化的宿主装配 (`TcpTableServer.kt`)
- **大幅瘦身**: `TcpTableServer.kt` 源码由原先的 2,143 行减少至 940 余行（缩减逾 1,200 行）；
- **装配责任**:
  - 构造并持有 `receiptManager = TcpReceiptManager(receiptStore, registry, config.clock)`；
  - 构造并持有 `router = TcpRequestRouter(...)`；
  - `handleRequest` 仅剩 20 余行：完成外层/内层 `requestId` 一致性核验后，通过 `LogicalIdentityContext.fromConnection(conn, envelope.requestScope)` 构建上下文，直接委托给 `router.routeRequest()`；
  - 关机流程 `close()` 顺序调用 `router.close()`、`receiptManager.close()`、`registry.close()`，并释放底层 Selector 与工作池。

---

## 3. 状态所有权矩阵 (State Ownership Matrix)

| 状态对象 / 资源 | 唯一所有者 | 构造与注入入口 | 转换与消费方式 | 清理与关闭责任 |
|---|---|---|---|---|
| 底层 SocketChannel / Selector / 全局预算 | `TcpTableServer` | `start()` 启动创建 | NIO 事件循环轮转与有界出站发送 | `TcpTableServer.close()` 释放 |
| 物理连接表 `connections` | `TcpTableServer` | `handleAccept()` | 通过 connectionId 索引维护物理连接映射 | 连接断开注销或关机时清空 |
| 业务身份上下文 `LogicalIdentityContext` | 短生命周期栈帧值对象 | `LogicalIdentityContext.fromConnection()` | 传递给 `router.routeRequest` 只读消费 | GC 自动回收，无状态保留 |
| 幂等作用域与回执存储 `RequestScopeReceiptStore` | `TcpReceiptManager` | 构造注入/创建 | 准入判定、副作用归档与 FIFO 淘汰 | `TcpReceiptManager.close()` |
| 跨连接在途条目 `activeInFlightEntries` | `TcpReceiptManager` | `acquireAdmission` (Admitted) | 多个连接挂接同一在途 Future | 条件移除 `(key, inFlight)`，或 `close()` 独占写锁以 `SERVER_SHUTDOWN` 失败 |
| 房间权威调度与状态机 | `SharedRoom` / `ServerTableHost` | `RoomRegistry` | `TableHost` 独占单桌串行操作入队 | `SharedRoom.close()` |
| 玩家专属流与房间订阅 | `PlayerState` | `bindPlayerToRoom` 构造 | `PlayerState.attachSubscriptions()` | `PlayerState.detach()` 幂等关闭 |
| 观战专属流与房间订阅 | `SpectatorState` | `bindSpectatorToRoom` 构造 | `SpectatorState.attachSubscriptions()` | `SpectatorState.detach()` 幂等关闭 |

---

## 4. 独立复验缺陷返修说明

### 4.1 第 1 轮复验缺陷返修回顾
1. **准入/登记与关闭原子边界**: 引入 `lifecycleLock: ReentrantReadWriteLock`，使准入判断、登记与关闭排它互斥；
2. **大厅在途副作用预留容量释放**: `InFlightReceipt` 固化 `val isLobby: Boolean = false`，`failInFlight` 自动推导 `effectiveIsLobby`，验证关机后大厅预留计数归零。

### 4.2 第 2 轮复验缺陷返修回顾
1. **完成/失败与关机共用读写锁原子生命周期边界**: 读锁覆盖 map 移除与 store 调用全生命周期；
2. **按 `(key, inFlight)` 实例身份条件原子移除**: 引入 `compute(key)` 条件移除，防止迟到回调误删新请求条目；
3. **增加受控 Latch 并发交错测试**: 覆盖关闭与完成/失败在途互斥及条件隔离。

### 4.3 第 3 轮复验缺陷返修回顾 (锁外 Future 回调彻底根除死锁)
1. **状态变更与 Future 触发解耦**: `RequestScopeReceiptStore` 增加 `completeFuture` 开关，在持锁内仅执行 store 内部比较删除与配额释放；
2. **锁外触发 Future 完成 (R12)**: `completeReceipt`、`failInFlight` 与 `close()` 均先彻底释放读锁/写锁，再在锁外触发 Future 回调，彻底根除当外部回调同步调用 `close()` 时因无法读锁升级导致的永久死锁；
3. **死锁回归测试**: 增补 2 项带 `@Timeout(5)` 硬断言的同步 `whenComplete` 联动 `close()` 测试。

### 4.4 第 4 轮复验缺陷返修回顾 (身份快照原子安装锁与旧 scope 越权拦截)
1. **安装锁原子边界一致**: `LogicalIdentityContext.fromConnection()` 通过 `conn.snapshotLogicalIdentity()` 在 `synchronized(conn)` 锁内原子读取 `(principal, roleState)`，消除分步读取导致的身份错配；
2. **强约束 Invariants**: 构造与快照强制核验 `require(principal == roleState.logicalPrincipal)` 与 `require(roleState !is UnauthenticatedRoleState)`；
3. **旧 Scope 越权拦截**: 角色切换后旧 scope 与新 principal 必然不匹配，`RequestScopeReceiptStore` 判定 `ScopeInvalid`，由路由层拦截为 `UNAUTHENTICATED_SESSION`；
4. **增补 5 项测试**: 覆盖非法的未握手连接拒绝、原子安装锁交错、解绑交错，以及大厅转玩家、玩家转大厅旧 scope 越权拦截端到端测试。

### 4.5 第 5 轮复验缺陷返修说明 (MR10 载荷采集确定性与 TcpReceiptManager 锁不变量清理)
1. **MR10 载荷采集非确定性根因与修复**:
   - **根因**: `Mr10PayloadCaptureTest.capture real phase payloads from InProcessServer authority` 在全套串行执行时，局内玩家可能在牌山处于 `28..35` 张时因摸到和牌（自摸或荣和）过早宣布和牌 (`DeclareRon` / `DeclareTsumo`) 终局，导致牌局在未进入 `wallRemaining in 5..25` 晚局区间即行结算，使 `lateGameUpdate` 遗漏为 `null`；
   - **修复**: 在决策选择器 `primaryOption(snap, allowWin = (lateGameUpdate != null))` 中，当 `lateGameUpdate == null` 时显式禁止声明自摸、荣和与立直，强制对局自然消耗牌山直至进入晚局区间；主循环中增加直接快照捕获 `if (lateGameUpdate == null && snap.view.wallRemaining in 5..25) lateGameUpdate = snap`，且门控结算终态 `if (settled && lateGameUpdate != null) break`；
   - **验证**: 单测运行采集到全部 6 个阶段真实载荷（EARLY 6486, MID 4259, LATE 9006, TILE_MARK 6398, CANDIDATES 6486, SETTLEMENT 7326 字节），且在多轮串行全量复跑中 100% 稳定通过。
2. **`TcpReceiptManager.acquireAdmission()` 读锁不变量与死码清理**:
   - **分析**: `acquireAdmission()` 在进入后即持有 `lifecycleLock.readLock()`，而 `close()` 执行必须获取 `lifecycleLock.writeLock()`。根据 `ReentrantReadWriteLock` 的互斥语义，在任何线程持有读锁期间，写锁绝对无法被获取；
   - **清理**: 移除了 `store.acquireAdmission()` 后原先防御性的 `if (isClosed.get())` 分支及其死码 `rollbackInFlight?.future?.completeExceptionally(...)`，并添加详尽锁不变量注释，确保代码路径与锁保证完全一致。

---

## 5. 与 D-03 整合边界与零冲突说明

- **严格未动范围**:
  - 未修改 `server/src/main/kotlin/com/majiang/server/inprocess/InProcessBinding.kt`；
  - 未动 `RoleStates.kt`（除 D-02 既有改动外保持不动）；
  - `SharedRoom.kt` 仅包含 D-02 阶段已验收的 `seatDisconnectedGenerations` 与代次屏障，未新增任何 D-08 专属修改；
- **D-03 整合点**:
  - D-08 的 `LogicalIdentityContext` 与 `TcpReceiptManager` 完全基于纯净的 `ServerRoleState` 与 `RequestScopeReceiptStore`，不与 `SharedRoom` 绑定层产生任何私有耦合；
  - D-03 统一绑定规则与参数化测试可以直接复用 `LogicalIdentityContext` 的强类型角色分型与 `TcpReceiptManager` 的统一准入契约。

---

## 6. 自动化验证结果与完整证据

所有测试均在隔离工作区 `D:\majiang-d08-20261019` 中单进程串行、带 `--rerun-tasks` 重新执行通过，无任何偶发与时序失败：

### 6.1 D-08 专属路由与业务回执测试 (`TcpRequestRouterAndReceiptTest`)
```powershell
.\gradlew.bat :server:test --tests "com.majiang.server.tcp.TcpRequestRouterAndReceiptTest" --rerun-tasks --console=plain
```
- **测试结果**: **30/30 PASSED** (100% 通过，耗时 15s)
- **全部用例清单**:
  - `whenComplete callback on failInFlight synchronizing close completes without deadlock` (PASSED)
  - `controlled interleaving - close blocks until active failInFlight releases atomic read lock` (PASSED)
  - `LogicalIdentityContext derives spectator role state accessors correctly` (PASSED)
  - `LogicalIdentityContext enforces non-blank principal and scope` (PASSED)
  - `real TCP identity boundary - non-host player cannot invoke host commands` (PASSED)
  - `real TCP cross-API duplicate requestId collision triggers CONFLICT error` (PASSED)
  - `LogicalIdentityContext derives player role state accessors correctly` (PASSED)
  - `controlled interleaving - stale callback cannot remove new in-flight of same key or leak reservations` (PASSED)
  - `TcpReceiptManager manages in-flight ownership across connections and completes receipt` (PASSED)
  - `deterministic concurrency test between acquireAdmission and close enforces atomic boundary` (PASSED)
  - `controlled interleaving - completeReceipt after close is rejected without mutating store` (PASSED)
  - `real TCP identity boundary - forged or unknown scope returns UNAUTHENTICATED_SESSION` (PASSED)
  - `real TCP identity boundary - spectator cannot invoke player action` (PASSED)
  - `controlled atomic interleaving during lobby to player role transition prevents stale principal with new roleState` (PASSED)
  - `real TCP cross-connection receipt replay on room creation avoids duplicate room` (PASSED)
  - `controlled atomic interleaving during player to lobby unbind prevents stale principal with new roleState` (PASSED)
  - `whenComplete callback on completeReceipt synchronizing close completes without deadlock` (PASSED)
  - `controlled interleaving - close blocks until active completeReceipt releases atomic read lock` (PASSED)
  - `TcpReceiptManager replay returns ROOM_NOT_FOUND if target room has been recycled` (PASSED)
  - `real TCP server shutdown closes connections and sends SERVER_SHUTDOWN close frame` (PASSED)
  - `LogicalIdentityContext derives lobby role state accessors correctly` (PASSED)
  - `TcpReceiptManager returns ScopeExpired when scope has expired and no receipt exists` (PASSED)
  - `real TCP identity boundary - lobby connection cannot invoke player action` (PASSED)
  - `TcpReceiptManager detects cross-API duplicate requestId collision as Conflict` (PASSED)
  - `TcpReceiptManager close fails active in-flight entries with SERVER_SHUTDOWN` (PASSED)
  - `real TCP identity boundary - after transitioning to player, old lobby scope cannot authorize player actions` (PASSED)
  - `TcpReceiptManager GET_REQUEST_SCOPE query rate limiting returns SERVER_BUSY` (PASSED)
  - `store lobby reservation count returns to zero on shutdown with capacity 1 store` (PASSED)
  - `LogicalIdentityContext fromConnection rejects unauthenticated or unhandshaked or closed connection` (PASSED)
  - `real TCP identity boundary - after leaving room and unbinding to lobby, old player scope cannot authorize player actions` (PASSED)

### 6.2 服务端全量连续串行复跑 (`:server:test`)
```powershell
.\gradlew.bat :server:test --rerun-tasks --console=plain
```
- **第 1 次复跑结果**: **300/300 PASSED, 0 failures, 0 skipped** (耗时 48s)
- **第 2 次复跑结果**: **300/300 PASSED, 0 failures, 0 skipped** (耗时 46s)
- 覆盖：
  - 新增 `TcpRequestRouterAndReceiptTest` 30 项全部通过；
  - D-02 `ServerRoleStateTest` 25 项全部通过；
  - `Mr10PayloadCaptureTest` 100% 确定性通过（6 阶段载荷全量捕获）；
  - `TcpTableServerCapacityAndLifecycleTest`、`TcpTableServerHandshakeTest`、`TcpTableServerRegressionTest`、`TcpTableServerRoutingAndSubscriptionTest` 全部通过；
  - 三大绝技（落汗/藏花/换柱）端到端集成与 InProcess 双传输测试全部通过。

### 6.3 三模块全套连续串行复跑 (`:server:test :client:test :protocol:test`)
```powershell
.\gradlew.bat :server:test :client:test :protocol:test --rerun-tasks --console=plain
```
- **第 1 次全套复跑结果**: **BUILD SUCCESSFUL in 53s** (17 actionable tasks, server 300 / client 217 / protocol 174 全部通过)
- **第 2 次全套复跑结果**: **BUILD SUCCESSFUL in 1m 3s** (17 actionable tasks, server 300 / client 217 / protocol 174 全部通过)

### 6.4 Codex TCP 独立复验探针 (`tcp-current-review.init.gradle`)
```powershell
.\gradlew.bat -I tools/architecture/tcp-current-review.init.gradle :server:test :client:test --rerun-tasks --console=plain
```
- **测试结果**: **22/22 PASSED** (耗时 12s)

### 6.5 V4 架构规范与边界复验 (`v4-review.init.gradle`)
```powershell
.\gradlew.bat -I tools/architecture/v4-review.init.gradle :server:test :client:test --rerun-tasks --console=plain
```
- **测试结果**: **189/189 ALL PASSED, 0 failures** (耗时 56s)

---

## 7. 改动与新增文件清单

1. **`server/src/main/kotlin/com/majiang/server/tcp/LogicalIdentityContext.kt` (新增/返修)**:
   - 已验证逻辑身份上下文，封装 `principal`、`roleState` 与 `requestScope`，派生强类型访问器；
   - `init` 强制核验 `principal == roleState.logicalPrincipal` 与 `roleState !is UnauthenticatedRoleState`；
   - `fromConnection` 严格经由 `conn.snapshotLogicalIdentity()` 在物理连接安装锁原子边界内提取快照。
2. **`server/src/main/kotlin/com/majiang/server/tcp/TcpConnection.kt` (修改/返修)**:
   - 新增 `snapshotLogicalIdentity()` 方法，持有 `synchronized(this)` 读取 `(logicalPrincipal, roleState)`；
   - 握手 `setHandshakedIdentity`、安装玩家/观战角色及解绑回退大厅均统一在 `synchronized(this)` 内将 `logicalPrincipal` 赋值为对应角色的 `logicalPrincipal`，严格消除瞬态身份错配。
3. **`server/src/main/kotlin/com/majiang/server/tcp/TcpReceiptManager.kt` (新增/返修)**:
   - 业务回执与跨连接在途管理器，封装 `RequestScopeReceiptStore`，管理跨连接在途所有权与新鲜度重放；
   - 增加 `lifecycleLock: ReentrantReadWriteLock` 原子生命周期边界，覆盖准入、完成、失败与关机全路径；
   - `completeReceipt` 与 `failInFlight` 处于读锁内进行条件移除与 store 更新，并在**锁外**触发 Future 用户回调；
   - `close()` 在持有写锁排空与释放配额后，在**锁外**触发 Future 异常完成，根绝死锁；
   - 清理 `acquireAdmission()` 中不可达的死码分支，明确读锁与写锁互斥保证。
4. **`server/src/main/kotlin/com/majiang/server/tcp/TcpRequestRouter.kt` (新增)**:
   - TCP 业务请求路由调度器，执行角色专属边界过滤、原子基线推送与单桌权威调度。
5. **`server/src/main/kotlin/com/majiang/server/tcp/TcpTableServer.kt` (修改)**:
   - 剥离路由、回执与在途管理逻辑，源码缩减 1,200 余行；
   - 汇聚装配 `receiptManager` 与 `router`，`handleRequest()` 精简为直接委托。
6. **`server/src/main/kotlin/com/majiang/server/idempotency/IdempotencyTypes.kt` (修改/返修)**:
   - `InFlightReceipt` 增加 `val isLobby: Boolean = false` 字段。
7. **`server/src/main/kotlin/com/majiang/server/idempotency/RequestScopeReceiptStore.kt` (修改/返修)**:
   - 重构 `completeReceipt` 与 `failInFlight` 增加 `completeFuture: Boolean = true` 选项，支持 store 状态与外部回调解耦；
   - `acquireAdmission` 构造带有 `isLobby` 的 `InFlightReceipt`；
   - `completeReceipt` 与 `failInFlight` 自动推导 `effectiveIsLobby = isLobby || inFlight.isLobby`，自愈预留扣减；
   - 增加 `getLobbySideEffectReserved()` 状态查询接口。
8. **`server/src/test/kotlin/com/majiang/server/mr10/Mr10PayloadCaptureTest.kt` (修改/返修)**:
   - 优化 `primaryOption` 抑制晚局前过早和牌，并在晚局区间确定性捕获 `lateGameUpdate` 载荷，消除测试偶发时序失败。
9. **`server/src/test/kotlin/com/majiang/server/tcp/TcpRequestRouterAndReceiptTest.kt` (新增/返修)**:
   - 增补 5 项身份快照、并发交错与角色切换旧 scope 拦截测试，测试总数达到 30 项全部通过。
10. **未提交的基线文件完整保留**:
    - 工作区继承自基线未提交的 D-01 与 D-02 改动全部原样保留，未执行任何跨工作树操作。
