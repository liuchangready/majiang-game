# D-01 客户端同步组件全量交付报告 (第2轮返修完成)

- **基线 HEAD**: `cca271a0b5c2a257ad7b3affda6bb14eed221ebd`
- **实施角色**: Anti (开发实施)
- **复核角色**: Codex (独立检视与验收)
- **交付目标**: 全面完成 [IMPROVEMENT-PLAN.md](IMPROVEMENT-PLAN.md) §9.2 / §9.3 D-01 客户端同步组件提取重构，将庞大的 `ServerConnectionSession` 拆解为三个职责内聚、真实拥有自身状态所有权的同步引擎：`RoomSyncEngine`、`PlayerStreamEngine`、`SpectatorStreamEngine`；彻底解决第1轮与第2轮检视中发现的换绑竞态与早期缓冲原子边界问题；查明并根治 TCP 流水线重放 R04 在多用例重跑环境下的假性 `SLOW_CONSUMER` 缺陷。

---

## 1. 状态所有权与职责划分矩阵

依据计划 §9.2 与 §9.3，严禁仅搬动函数、严禁传递 `ServerConnectionSession` 全量父对象引用、严禁引入万能 Context、严禁扩大 public 状态。每个组件拥有自己的核心锁、缓存、版本/游标、缓冲区与监听器集合。

### 1.1 RoomSyncEngine (房间信息与观察者同步引擎)
| 状态 / 职责 | 原归属 (`ServerConnectionSession`) | 新归属 (`RoomSyncEngine`) | 协调层交互机制 |
|---|---|---|---|
| `cachedRoomInfo: RoomInfo?` | 私有字段 | `RoomSyncEngine` 真实私有持有 | 仅通过只读属性 `cachedInfo` 访问，写回在 `roomLock` 内比对版本与房间 ID |
| `cachedRoomVersion: Long?` | 私有字段 | `RoomSyncEngine` 真实私有持有 | 内部维护严格单调版本校验，防止旧/乱序版本覆盖 |
| `currentRoomEpoch` | `currentRoomEpoch` (AtomicLong) | `roomEpochRef` (AtomicLong) | `ServerConnectionSession` 委托只读引用，支持 `advanceRoomEpoch()` 与 `retireRoom()` |
| `roomLock: Any` | 内部私有锁 | `RoomSyncEngine.roomLock` 真实独占锁 | `ServerConnectionSession` 保留 `private val roomLock = roomSyncEngine.roomLock`，维持反射契约，杜绝跨流反向死锁 |
| `roomObservers` | `CopyOnWriteArrayList<RoomObserver>` | `RoomSyncEngine` 真实拥有 | `onRoomUpdate` 注册返回 `AutoCloseable` 注销句柄 |
| `RoomObserver` 状态 | 内部类 | `RoomSyncEngine.kt` internal 类 | 拥有 `active`、`listenerLock`、`lastDeliveredRoomEpoch`、`lastDeliveredVersion`，严格保证每个观察者递送版本单调 |
| `outstandingRoomFutures` | 私有哈希表 | `RoomSyncEngine.outstandingRoomFutures` | 追踪房间代次关联 Future；代次变更、离房或关闭时批量搜集并由协调层在锁外完成异常回调 |
| 入站房间更新解析与裁决 | 在协调层锁内处理并派发 | `roomSyncEngine.handleInboundRoomUpdate` | 校验合法性与动态 `currentBoundRoomId` 后返回 `RoomUpdateEffect.Notify(roomInfo, connectionEpoch, roomEpoch, observers)`，**协调层在锁外调用 `dispatch` 派发** |
| 权威查询采纳与对齐 | 锁内直接写回 | `roomSyncEngine.adoptAuthoritativeRoomInfo` | 锁内二次校验 `currentBoundRoomId()` 与 `requestRoomEpoch`，保证不回滚本地已知更高版本，不污染新房间 |

### 1.2 PlayerStreamEngine (玩家流状态同步引擎)
| 状态 / 职责 | 原归属 (`ServerConnectionSession`) | 新归属 (`PlayerStreamEngine`) | 协调层交互机制 |
|---|---|---|---|
| `syncLock: Any` | `ServerConnectionSession` 私有字段 | `PlayerStreamEngine.syncLock` 真实独占锁 | 保护玩家快照、重排缓冲、早期缓冲、游标去重集合 |
| `_isBindingInProgress` | `ServerConnectionSession` 字段 (AtomicBoolean) | `PlayerStreamEngine._isBindingInProgress` 真实持有 | 受 `syncLock` 严格原子保护，提供 `beginBindingUnderLock()`、`cancelBindingUnderLock()` |
| `cachedSnapshot: GameSnapshotView?` | 私有字段 | `PlayerStreamEngine._cachedSnapshot` 真实持有 | 外部通过 `cachedSnapshot` 只读属性访问，仅通过引擎方法（安装、入站处理、权威查询采纳）在锁内修改 |
| `reorderBuffer` | 私有 `ConcurrentSkipListMap` | `PlayerStreamEngine.reorderBuffer` 真实持有 | 乱序到达 updates 暂存并自动排水向前推进 |
| `isResuming` | `AtomicBoolean(false)` | `PlayerStreamEngine.isResuming` 真实持有 | 缺口恢复状态在途并发标记，避免重复触发恢复 |
| `earlyUpdateBuffer` | 私有 `ConcurrentLinkedQueue` | `PlayerStreamEngine.earlyUpdateBuffer` 真实持有 | 握手建立前暂存推送，与 `_isBindingInProgress` 同锁原子保护 |
| `earlyUpdateOverflowed` | `AtomicBoolean(false)` | `PlayerStreamEngine.earlyUpdateOverflowed` 真实持有 | 早期缓冲超限标记 |
| `appliedBatchCursors` | 私有 `LinkedHashSet<Long>` | `PlayerStreamEngine.appliedBatchCursors` 真实持有 | 游标去重与环形有界窗口维护 |
| `updateListeners` | 私有 `CopyOnWriteArrayList` | `PlayerStreamEngine.updateListeners` 真实持有 | 注册与注销观察者，锁外异常隔离派发 |
| 入站推送与恢复处理 | 在会话层持锁直接修改状态并网络请求 | `handleInboundUpdate` / `handleResumeResponse` | **纯状态计算，返回 `PlayerUpdateEffect` / `PlayerResumeEffect`，协调层锁外执行 UI 派发与网络请求** |

### 1.3 SpectatorStreamEngine (观战流公开状态同步引擎)
| 状态 / 职责 | 原归属 (`ServerConnectionSession`) | 新归属 (`SpectatorStreamEngine`) | 协调层交互机制 |
|---|---|---|---|
| `publicSyncLock: Any` | `ServerConnectionSession` 私有字段 | `SpectatorStreamEngine.publicSyncLock` 真实独占锁 | 保护公开快照、公开重排缓冲与早期缓冲 |
| `_isSpectatorBindingInProgress` | `ServerConnectionSession` 字段 (AtomicBoolean) | `SpectatorStreamEngine._isSpectatorBindingInProgress` 真实持有 | 受 `publicSyncLock` 严格原子保护，提供 `beginBindingUnderLock()`、`cancelBindingUnderLock()` |
| `cachedPublicSnapshot` | 私有字段 | `SpectatorStreamEngine._cachedPublicSnapshot` 真实持有 | 外部只读访问，锁内由初始安装、入站更新、权威采纳和公开恢复修改 |
| `publicReorderBuffer` | 私有 `ConcurrentSkipListMap` | `SpectatorStreamEngine.publicReorderBuffer` 真实持有 | 公开更新乱序暂存与单调排水 |
| `activePublicRecoveryToken` | `AtomicReference<PublicRecoveryToken?>` | `SpectatorStreamEngine.activePublicRecoveryToken` 真实持有 | 代次与序列号严格绑定的恢复令牌，防旧回调污染 |
| `earlyPublicUpdateBuffer` | 私有 `ConcurrentLinkedQueue` | `SpectatorStreamEngine.earlyPublicUpdateBuffer` 真实持有 | 观战建立前早期推送缓冲，与 `_isSpectatorBindingInProgress` 同锁原子保护 |
| `earlyPublicUpdateOverflowed` | `AtomicBoolean(false)` | `SpectatorStreamEngine.earlyPublicUpdateOverflowed` 真实持有 | 观战早期缓冲溢出标记 |
| `publicUpdateListeners` | 私有 `CopyOnWriteArrayList` | `SpectatorStreamEngine.publicUpdateListeners` 真实持有 | 观战观察者注册与异常隔离派发 |
| 入站公开推送与恢复响应 | 在会话层持锁直接修改状态并网络请求 | `handleInboundPublicUpdate` / `handleResumePublicResponse` | **纯状态计算，返回 `SpectatorUpdateEffect` / `SpectatorResumeEffect`，协调层锁外派发** |

---

## 2. 缺陷根因分析与彻底修复

### 2.1 TcpClientBinding R04 假性 SLOW_CONSUMER 缺陷根因与修复
- **缺陷现象**: 在全量无缓存测试跑动时，`TcpClientBindingRegressionTest.R04 replay of unconfirmed side effects strictly respects negotiated in flight limit with more than 64 records` 偶尔在第 633 行断言 `All 70 replayed requests must be delivered and processed` 超时失败。
- **根本原因**:
  1. 在 `TcpClientBinding.kt` 中，入站帧纳入 64 帧 / 4 MiB 串行接收邮箱双限治理；
  2. 当 Selector 线程解析入站帧时，检查持有帧数：`val heldFrames = inboundMailbox.size + (if (currentDispatchFrame != null) 1 else 0)`，若 `heldFrames >= maxInboundMailboxFrames (64)` 则判为 `SLOW_CONSUMER` 并关闭连接；
  3. 原实现中，`handleResponseEnvelope` 在处理完 RSP 信封后**立即在自身方法内同步调用 `pumpReplayQueue()`**；
  4. 此时，当前正在派发的帧 `currentDispatchFrame` 仍在 `drainInboundMailbox` 的 `dispatchFrame` 临界区内尚未释放（`currentDispatchFrame = null` 原只在 `dispatchFrame` 之后的 `finally` 中清空）；
  5. `pumpReplayQueue()` 见在途请求数已释放 1 个，立即将第 65 个请求发出；服务端极速回复 RSP 到达 Selector 线程；
  6. Selector 线程读取到第 65 帧时，邮箱内恰好排队 63 帧，且 `currentDispatchFrame != null`（仍为未退出的前一帧），算得 `heldFrames = 63 + 1 = 64`，直接命中 `heldFrames >= 64`，误触发 `SLOW_CONSUMER` 杀掉连接；
  7. 连接被关闭后，剩余请求无法重放，导致 `allReplayedLatch` 停滞在 64，测试超时失败。
- **修复方案**:
  1. 移除 `handleResponseEnvelope` 内部提前调用的 `pumpReplayQueue()`；
  2. 将 `pumpReplayQueue()` 移入 `drainInboundMailbox` 的 `finally` 块中，**严格在 `currentDispatchFrame = null` 与邮箱字节扣减完成后才触发重放流水线**；
  3. 此时新请求发出的 RSP 到达时，前一帧已完全归还占用，持有帧数永远受控在不超过 63，绝不会因微秒级极速回包与当前帧产生虚假叠加。

### 2.2 换绑与早期缓冲原子边界竞态修复
- **缺陷机理**:
  1. `ServerConnectionSession.kt` 原在锁外读取 `isBindingInProgress.get()` / `isSpectatorBindingInProgress.get()`；
  2. `PlayerStreamEngine.kt` 与 `SpectatorStreamEngine.kt` 亦在同步锁外向 early buffer 写入；
  3. 若入站线程读到 true 后发生上下文切换，绑定线程持锁调用 `installInitialSnapshotUnderLock` 安装快照、排空早期缓冲并将标记置 false；随后入站线程才将更新写入 early buffer，该更新将永久滞留早期缓冲，既不派发也无法触发恢复，造成状态不一致。
- **修复方案**:
  1. 将 `isBindingInProgress` 与 `isSpectatorBindingInProgress` 的所有权完全移入 `PlayerStreamEngine` 与 `SpectatorStreamEngine` 内部，分别受 `syncLock` 与 `publicSyncLock` 保护；
  2. 提供 `beginBindingUnderLock()` 与 `cancelBindingUnderLock()`；
  3. `installInitialSnapshotUnderLock` 在锁内原子完成：设置快照、排空并应用早期缓冲、将 `_isBindingInProgress = false`；
  4. `handleInboundUpdate` 与 `handleInboundPublicUpdate` 去除锁外传参，入站检查 `_isBindingInProgress`、早期缓冲入队以及正常推进更新**全部收归同一锁内原子执行**；
  5. 彻底消除了标记检查与缓冲写入之间的交错窗口。

---

## 3. 新增回归测试

1. **`PlayerStreamEngineTest.testInterleavedBindingInstallationAndInboundPushDoesNotStrandEarlyBuffer`**:
   - 验证：在开始绑定期间到达的推送进入早期缓冲；在锁内原子安装快照并排空；安装后紧接着到达的 Live PUSH 绝不滞留早期缓冲，直接派发并使游标前进到最新版本。
2. **`SpectatorStreamEngineTest.testInterleavedSpectatorBindingInstallationAndInboundPushDoesNotStrandEarlyBuffer`**:
   - 验证：观战绑定期间到达的公开推送进入早期缓冲；在锁内安装初始公开快照并排空；安装后到达的公开推送绝不滞留早期缓冲，直接派发并单调推进公开游标。
3. **`TcpClientBindingRegressionTest.R04 replay of unconfirmed side effects strictly respects negotiated in flight limit with more than 64 records`**:
   - 验证：重连后 70 条在途副作用请求在 64 并发上限流控下完整重放并全部成功确认，连接不中断，邮箱双限不误报。

---

## 4. 自动化验证矩阵与执行日志

### 4.1 R04 专项无缓存重跑
```powershell
.\gradlew.bat :client:test --rerun-tasks --tests "com.majiang.client.transport.tcp.TcpClientBindingRegressionTest.R04 replay of unconfirmed side effects strictly respects negotiated in flight limit with more than 64 records" --console=plain
```
- **执行结果**: 10 actionable tasks, 10 executed, PASSED in 10s。

### 4.2 核心会话与同步组件全量无缓存重跑
```powershell
.\gradlew.bat :client:test --rerun-tasks --tests "com.majiang.client.session.*" --tests "com.majiang.client.api.ServerConnectionSessionTest" --console=plain
```
- **执行结果**: 10 actionable tasks, 10 executed, 74 tests completed, 0 failed, BUILD SUCCESSFUL in 11s。
  - `ServerConnectionSessionTest`: 44 passed
  - `PlayerStreamEngineTest`: 6 passed (含新建交错回归测试)
  - `SpectatorStreamEngineTest`: 8 passed (含新建交错回归测试)
  - `RoomSyncEngineTest`: 11 passed (含换绑交错回归测试)
  - `ReconnectErrorClassifierTest`: 8 passed
  - `ServerConnectionSessionReconnectClassificationTest`: 4 passed

### 4.3 TCP 传输全量回归无缓存重跑
```powershell
.\gradlew.bat :client:test --rerun-tasks --tests "com.majiang.client.transport.tcp.TcpClientBindingRegressionTest" --console=plain
```
- **执行结果**: 10 actionable tasks, 10 executed, 45 tests completed, 0 failed, BUILD SUCCESSFUL in 26s。

### 4.4 跨模块全量无缓存重跑 (计划与检视要求)
```powershell
.\gradlew.bat :client:test :protocol:test :server:test --rerun-tasks --console=plain
```
- **执行结果**: 17 actionable tasks, 17 executed, 100% 通过，0 failed，BUILD SUCCESSFUL in 49s。

---

## 5. 守则承诺遵守核对

- [x] 未修改 `tools/architecture/` 目录下的任何探针；
- [x] 未弱化已有任何测试断言；
- [x] 用户所有未跟踪历史文件完好保留；
- [x] 未执行 `git commit` 或 `git push`；
- [x] 所有 Gradle 验证指令均严格串行执行；
- [x] 完整交付三大流同步引擎真实状态所有权与协调层装配；
- [x] 修复换绑竞态与 TCP 邮箱双限时序缺陷。
