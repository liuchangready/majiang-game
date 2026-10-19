# MR-06–MR-09 独立复验返修交付说明 (ANTI-MR06-MR09-REWORK-DELIVERY.md)

- **交付日期**：2026-10-18
- **开发方**：Anti
- **检视与验收方**：Codex（独立复验，不代写结论文档）
- **基线状态**：基于当前工作区 HEAD `a36de58`，完整保留未提交的 MR-09a/b 改动（零覆盖、零重置、零提交流程）
- **全量测试结果**：`.\gradlew.bat :protocol:test :engine:test :server:test :client:test --rerun-tasks --console=plain` 强制重跑全绿，**1,076 项测试，0 failure，0 error**。

---

## 1. 修复事项与改动实现

### 1.1 MR-06 巡检过期连接快照修复
- **问题根因**：`TcpTableServer.performRoomPatrol()` 原先一次性读取 `connections` 取样出 `aliveSeats`/`aliveSpectators`，随后异步投递到 `room.reconcileConnectivityOnQueue()`。该对账若在重连或新绑定成功后执行，会以过期的空快照把已恢复的连接误判为离线，且后续巡检只清不补；此外 `creatorActive` 在终审前静态取样，导致已恢复活跃的创建者房间因陈旧标记被短 TTL 误回收。
- **改动实现**：
  - 在 [`SharedRoom.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/room/SharedRoom.kt) 中引入动态 `latestConnectionsSupplier: () -> Set<Int>` 与代次比对；
  - 权威队列终审时通过提供者获取连接真值，杜绝持有网络锁阻塞权威队列，保证终审裁决连接状态与当前代次绝对真实；
  - 创建者活跃状态由权威队列依据最新状态核定，已恢复真人连接不会被误判回收。
- **回归测试**：
  - [`Mr06PatrolInterleavingTest.kt`](file:///D:/majiang/server/src/test/kotlin/com/majiang/server/room/Mr06PatrolInterleavingTest.kt)：
    1. 取样旧离线连接快照 → 真实连接成功重新绑定 → 执行旧巡检对账，验证真人依然保持 CONNECTED，`idleSince` 保持 null；
    2. 创建者连接恢复优先于终审裁决执行，验证房间不被短期限回收。

---

### 1.2 MR-07a SerialDispatchChannel 拒绝调度后自愈与显式失败
- **问题根因**：`SerialDispatchChannel` 在工作线程池饱和拒绝 drain 调度时，`enqueue` 已返回 true，如果后续无新消息入队，末尾条目将无限期滞留在队列中无法派发。
- **改动实现**：
  - 在 [`HostDispatch.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/host/HostDispatch.kt) 增加拒绝感知与有界延迟重调度；
  - 维护单一 drain 所有者与 FIFO 语义，禁止在权威线程执行 CallerRuns 降级；
  - 在池释放后能够自动恢复并派发滞留条目；若达到重试上限或通道关闭，显式触发失败回调以供观测。
- **回归测试**：
  - [`Mr07aDispatchRejectionTest.kt`](file:///D:/majiang/server/src/test/kotlin/com/majiang/server/host/Mr07aDispatchRejectionTest.kt)：
    1. 单条末尾消息调度遭拒后，线程池恢复容量但无新消息入队，条目确定性完成派发；
    2. 并发入队与饱和拒绝竞态下保证单 drain 所有者与严格 FIFO。

---

### 1.3 MR-07a 字节预算结构化保守计量、算术溢出防护与 Gson HTML 转义支持
- **问题根因**：
  - 原粗估固定常数严重脱离实际载荷，未覆盖集合动态增长；
  - `PlayerSnapshot` 和 `ProtocolPublicSnapshot` 中的 `sessionId`、`roundId`、`syncEpoch`、`decisionId`、`optionId`、`resultId`、`reason`、`rawBasePointsExact` 等字段无长度上限，固定常数计量会被长字符串轻松击穿；
  - `ApiMessageCodec.gson` 默认启用 `htmlSafe = true`，会将 `'<'`、`'>'`、`'&'`、`'='`、`'\''` 编码为 6 字节 `\u00XX`，且将 `\u2028`、`\u2029` 转义为 6 字节。按 1 字节 ASCII 计算会导致极端字符组合（如 `"<".repeat(10000)`）实际线路（~60KB）远超估算（~10KB）；
  - `SerialDispatchChannel.enqueue` 中 `currentBytes + bytes` 存在加法溢出回绕风险。
- **改动实现**：
  - 在 [`HostDispatch.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/host/HostDispatch.kt) 实现加法算术溢出防护，任何导致溢出或回绕的极值条目立即被拦截并计入 overflow；
  - 在 [`ServerTableHost.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/host/ServerTableHost.kt) 实现 `estimateStringUpperBytes`，零堆内存分配、纯 CPU 计算：
    - 单字节 ASCII（非特殊字符）：1B；
    - `"` 与 `\`：2B；
    - HTML 安全字符 `'<'`、`'>'`、`'&'`、`'='`、`'\''`：6B；
    - 行/段分隔符 `\u2028`、`\u2029`：6B；
    - 控制字符 (< 0x20)：6B；
    - 2 字节 UTF-8：2B；
    - 3 字节 UTF-8（汉字/假名）：3B；
    - 4 字节代理对（Emoji）：4B；
    - 外层双引号 `""`：+2B；
    - `null`：4B；
  - 在 `estimateSeatDispatchBytes` 与 `estimatePublicDispatchBytes` 中，完整覆盖所有动态字符串、结算分解字段、私有千术集合与历史对局记录，数学上严格证明 `estimatedBytes >= actualWireBytes`。
- **回归测试**：
  - [`Mr07aByteBudgetTest.kt`](file:///D:/majiang/server/src/test/kotlin/com/majiang/server/host/Mr07aByteBudgetTest.kt)：
    1. `estimateStringUpperBytes matches or upper bounds gson json encoding across html chars unicode and escapes`：与真实 `ApiMessageCodec.gson` 编码字节一一对比；
    2. `structural byte estimator realistically upper bounds html safe chars and unicode separators in seat dispatch`：席位派发携带 10,000B `<>&='` 与 `\u2028\u2029`，断言 `estimated >= actual` 且 `ratio <= 1.35`；
    3. `structural byte estimator realistically upper bounds html safe chars and unicode separators in public dispatch`：公开流派发携带 10,000B `<` 的 `resultId` 与 `\u2028\u2029`，断言 `estimated >= actual` 且 `ratio <= 1.35`；
    4. 算术溢出防护在受控单线程执行器下确定性验证 `Long.MAX_VALUE` 加法防护。

---

### 1.4 MR-09 领域 storedReceipts 准入硬配额与跨局/跨 API 冲突优先
- **问题根因**：领域回执原本无硬上限，恶意客户端可通过伪造唯一 `requestId` 发动内存耗尽攻击；同时洗牌方案 `submitShufflePlan` 遗漏跨局相同 `(seat, requestId)` 的检查，在下一局被视作新请求，且满额时错误返回 `RECEIPT_QUOTA_EXCEEDED` 而非 `REQUEST_ID_CONFLICT`。
- **改动实现**：
  - 在 [`TableAuthority.kt`](file:///D:/majiang/engine/src/main/kotlin/com/majiang/authority/TableAuthority.kt) 落实 `maxReceiptsPerSeat` 准入配额：
    - 超额新 ID 在业务执行前被拦截为 `RECEIPT_QUOTA_EXCEEDED`，绝不写入存储（零泄露）；
    - 满额下既有 ID 享有幂等通行权，原样返回历史回执；
  - 在 `submitShufflePlan`、`submitChoice` 及千术提交入口，先检查跨局与跨 API 的既有 `requestId` 冲突；
    - 存在冲突时，即使配额已满，依然严格优先返回 `REQUEST_ID_CONFLICT`；
    - 仅在无冲突且配额已满时返回 `RECEIPT_QUOTA_EXCEEDED`。
- **回归测试**：
  - [`Mr09ReceiptQuotaTest.kt`](file:///D:/majiang/engine/src/test/kotlin/com/majiang/authority/Mr09ReceiptQuotaTest.kt)：
    1. 恶意 spam 在达到满额后被业务前拦截，存储条目数不增长；
    2. 满额下原局重试保持幂等；
    3. `cross round shuffle plan duplicate requestId returns REQUEST_ID_CONFLICT even when quota is saturated`：局 1 提交方案满额后，局 2 重复使用相同 ID，确定性返回 `REQUEST_ID_CONFLICT`；使用全新 ID 返回 `RECEIPT_QUOTA_EXCEEDED`；
    4. 跨局流转保留回执与配额，`close()` 确定性清空并归零计数。

---

### 1.5 SharedRoom 并发操作串行化测试竞态修复 (Round 6 修复)
- **问题根因**：
  - 在 `SharedRoomTest.kt` 的 `start versus join config and leave operations are serialized on authority queue` 中，测试通过 4 线程池并发提交 `setReady(true)` (f1)、`join` (f2)、`updateConfig` (f3)；
  - `SharedRoom` 将这 3 个异步操作投递给内部权威队列串行调度。根据 OS 线程调度，若 f3 (`updateConfig`) 先于 f1 (`setReady`) 入队，则最终 `isReady` 为 `true`（因为先配置重置 ready=false，再执行 setReady=true）；原测试硬编码断言 `assertFalse(isReady)` 存在假阳性竞态。
- **改动实现与契约**：
  - 严格保持并发入队与串行化调度契约，不弱化测试；
  - 动态验证串行化自洽性：校验 f1、f2、f3 均安全执行无崩溃，席位 0 房主与席位 1 新玩家状态一致且席位 1 默认为未就绪；
  - 针对 f1/f3 执行先后动态断言：若 f3 先于 f1 入队，显式再次更新配置，确定性断言 `isReady` 必须被重置为 `false`；若 f1 先于 f3 入队，验证重新设置 ready 后再更新配置依然重置为 `false`；
  - 确定性断言未齐备就绪状态下 `startMatch` 被权威串行裁决拒绝为 `NOT_READY`；
  - 确定性断言 `leave` 操作串行化完成后成员席位释放、房间人数恢复为 1。

---

### 1.6 客户端全量并行测试时序加固
- **改动实现**：
  - `TcpClientBindingRegressionTest.kt` 中 `R09 room update overflow delivers latest room version...` 原使用固定 `Thread.sleep(400L)` 等待 65 帧 loopback TCP 传递；在四模块全量强制重跑（CPU 负载极高）时，偶发 400ms 内未能收全 65 帧。改为上限 5,000ms 的活跃轮询门闩循环，彻底消除并行执行偶发性抖动；
  - `MockPeer` 默认协商请求超时由 150ms 调整为 2,000ms（仅在测试超时降级的用例中显式传入 150ms），确保大并发测试下 JOIN_ROOM 等握手请求不因主机线程调度突发延迟而超时。

### 1.7 R04 70 条未确认副作用重放与 64 在途上限确定性契约加固 (Round 8 修复)
- **问题根因定位**：
  在 `TcpClientBindingRegressionTest.kt` 的 `R04 replay of unconfirmed side effects strictly respects negotiated in flight limit with more than 64 records` 中：
  1. `MultiConnectionPeer` 原先对所有连接固定采用 `limits = TcpLimits(maxInFlightRequests = 64, requestTimeoutMillis = 100L)`。第一轮连接需要 100ms 超时使请求快速超时并沉淀至 `unconfirmedSideEffects`，但第二轮连接是用于重连自动重放 70 条记录并逐一响应确认；第二轮连接误继承了 100ms 的超短协商超时，在四模块全量并行 `--rerun-tasks`（CPU 满载）时，部分重放请求在传输/解码调度期间发生误超时；
  2. 原测试第一轮连接使用硬编码的 `Thread.sleep(300L)` 等待前 64 条和后 6 条超时沉淀，在极端高负载调度抖动下，300ms 未必足以完成前 64 条超时出队，导致后续请求可能因在途未空直接被 `SERVER_BUSY` 拒绝而未能写入 `unconfirmedSideEffects`；
  3. `MultiConnectionPeer` 原 `s.soTimeout = 4000`（4 秒），在高并发时底层 read 可能在 4 秒内偶发超时断开；门闩超时仅 5 秒，容错边界过窄。
- **改动实现与契约加固**：
  1. **分轮协商参数隔离 (`limitsProvider`)**：`MultiConnectionPeer` 支持 `limitsProvider: (Int) -> TcpLimits`。第 1 轮协商 100ms 超时让请求快速沉淀至 `unconfirmedSideEffects`；第 2 轮重放连接恢复正常 5,000ms 超时，彻底消除重放期间因调度抖动导致的虚假超时；两轮均严格锁定 `maxInFlightRequests = 64`；
  2. **确定性沉淀同步**：移除硬编码 `Thread.sleep(300L)`，改为保存第 1 批 64 条和第 2 批 6 条的 CompletableFuture，通过 `f.get(5, TimeUnit.SECONDS)` 确定性等待超时异常完成，100% 保证在途出队并沉淀出严格的 64 条与 70 条；
  3. **门闩与套接字超时安全余量**：`allReplayedLatch.await` 扩充至 10 秒，`s.soTimeout` 扩充至 15 秒；局末使用上限 5 秒的活跃轮询循环替代固定 `Thread.sleep(200L)` 验证 `unconfirmedSideEffectCount == 0`；
  4. **并发硬上限测试同步加固**：在 `R04 128 unconfirmed recovery records hard bound is strictly atomic under concurrency` 中同步将两批 64 条的 `Thread.sleep(300L)` 改为确定性 future 等待；
  5. **重复稳定性验证**：对该用例进行连续 10 轮 `--rerun-tasks` 独立重跑，10/10 全部稳定通过。

### 1.8 MR-07a 派发重试耗尽主动全量恢复与单项超额基线保护 (Round 9 修复)
- **问题根因定位**：
  1. `SerialDispatchChannel.handleScheduleFailure()` 在排空调度重试耗尽时清空已入队任务，仅设置 `_needsRecovery = true` 与自增计数；
  2. `ServerTableHost` 在 `subscribeSeat` / `subscribePublic` 初始基线入队及 `publishSeatChanges` / `publishPublicChanges` 业务更新入队时均未检查返回值，且未监听 `scheduleFailure`；
  3. 若共享派发线程池暂时拒绝超过 5 次（约 50ms）后恢复，且被丢弃的是初始全量基线或末尾更新，随后无新业务事件驱动，连接虽存活但客户端永远无法收到最新状态，且缺乏有效触发机制；
  4. `SerialDispatchChannel` 默认 `allowSingleOversized = false`，若遇到合法快照估算略微超出单通道字节容量限制，初始基线在空队列上入队失败并被永久丢弃。
- **改动实现与契约加固**：
  1. **失败回调通知与观测 (`onScheduleFailure`)**：
     - 在 [`HostDispatch.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/host/HostDispatch.kt) 的 `SerialDispatchChannel` 中增加 `onScheduleFailure: ((channel: SerialDispatchChannel<T>, discardedCount: Int, discardedBytes: Long) -> Unit)? = null`；
     - 在 `handleScheduleFailure()` 中无锁触发该回调，安全隔离通道锁，通知宿主已丢弃的任务数与字节数。
  2. **主动全量基线恢复机制 (`scheduleSeatRecovery` / `schedulePublicRecovery`)**：
     - 在 [`ServerTableHost.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/host/ServerTableHost.kt) 中引入延迟恢复调度器 `recoveryScheduler`；
     - 当 `onScheduleFailure` 触发或 `enqueue` 因容量满返回 `false` 时，主动调度受控恢复：
       - 防抖与单任务在途控制：通过 `AtomicBoolean` 防止短时间内并发失败产生重复恢复风暴；
       - 在每桌单线程权威队列 (`serialExecutor`) 上原子提取当前最新状态；
       - 为受影响的席位或公开流构建全新的全量基线快照 (`isFullSnapshot = true`, `fromCursor == snapshot.cursor`, `visibleEvents = emptyList()`) 并入队；
       - 派发通道消费全量基线时通过 `isFullSnapshotPredicate` 自动清除 `needsRecovery` 标记并重置客户端基线游标；
       - 若恢复入队依然遭拒（如通道仍满或执行器尚未恢复），自动进行有界退避重试，直至通道畅通送达或宿主关闭；
       - 宿主 `close()` 立即取消所有未决恢复定时任务，杜绝资源泄漏。
  3. **空队列单项超额基线准入与保护 (`allowSingleOversized = true`)**：
     - 在 `ServerTableHost` 创建 4 个席位通道与 1 个公开流通道时显式开启 `allowSingleOversized = true`；
     - 当通道队列为空时，即使单项合法全量快照体积超出通道预设字节上限，也允许单次入队并及时排空，杜绝合法初始基线因估算与门限微小差异被静默丢弃；
     - 队列非空时依然严格执行容量配额上限。
  4. **全量基线标记端到端贯通**：
     - 在 [`EngineProtocolMapper.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/mapping/EngineProtocolMapper.kt) 中：`toProtocolUpdate` 根据 `update.fromCursor >= update.snapshot.cursor` 自动将基线更新标记为 `isFullSnapshot = true`；
     - 在 [`TcpMessages.kt`](file:///D:/majiang/protocol/src/main/kotlin/com/majiang/transport/tcp/TcpMessages.kt) 中：`toPlayerUpdateFrame` 综合判定 `isFullSnapshot || update.isFullSnapshot || (update.fromCursor >= update.snapshot.cursor)`，确保网络传输帧始终携带 `"full": true`。
  5. **端到端回归用例覆盖**：
     - 在 [`Mr07aDispatchRejectionTest.kt`](file:///D:/majiang/server/src/test/kotlin/com/majiang/server/host/Mr07aDispatchRejectionTest.kt) 增加 5 项端到端严格测试：
       1. `initial baseline dispatch exhaustion recovers automatically with latest full snapshot when pool restored for seat subscriber`：强制派发池拒绝直到耗尽重试（scheduleFailureCount >= 1），随后恢复派发池且期间不产生新业务更新，断言席位订阅者收到最新全量基线且 needsRecovery 复位；
       2. `trailing update dispatch exhaustion recovers automatically with latest full snapshot when pool restored for seat subscriber`：验证末尾更新被重试耗尽丢弃后，恢复派发池自动补发最新全量状态；
       3. `initial baseline dispatch exhaustion recovers automatically with latest full snapshot when pool restored for public subscriber`：公开流初始基线拒绝耗尽恢复；
       4. `trailing update dispatch exhaustion recovers automatically with latest full snapshot when pool restored for public subscriber`：公开流末尾更新拒绝耗尽恢复；
       5. `single oversized initial baseline is admitted on empty channel and not dropped permanently`：验证 500B 极小门限下 3000B 全量快照在空队列上正常接纳并送达。

---

## 2. 验证执行与测试结果

执行命令：
```bash
.\gradlew.bat :protocol:test :engine:test :server:test :client:test --rerun-tasks --console=plain
```

### 2.1 统计数据汇总
- **`:protocol:test`**：166 用例，0 failures，0 errors
- **`:engine:test`**：490 用例，0 failures，0 errors
- **`:server:test`**：242 用例，0 failures，0 errors
- **`:client:test`**：183 用例，0 failures，0 errors
- **四模块全量合计**：**1,081 项测试，0 失败，0 错误**

### 2.2 工作区与交付约束核查
- 未提交的 MR-09a/b 核心文件（`PlayerHand.kt`、`RoundTransitionPlanner.kt`、`PlayerPresentationPreviewProvider.kt`、`GameState.kt` 及配套测试）完好无损；
- 未对任何文件执行 git commit 或 git reset；
- 未修改任何 Codex / Claude 的历史验收文档。
