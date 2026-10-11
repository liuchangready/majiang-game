# A3 客户端门面、映射与恢复交付说明 (DELIVERY-ANTI-A3.md)

- **执行者**: Anti
- **任务编号**: A3 (TASK-ANTI-A3.md)
- **交付目标**: 落汗 (Tile Mark) 客户端独立模型、双向协议映射、GameSession 门面在途锁/代次隔离/缓存恢复、原生 UDP 传输绑定接线，以及 A2 交付文档纠偏。
- **交付状态**: ready_for_review (已全量通过 Codex 7 项客户端契约测试、1 项真实 UDP 客户端集成测试、:client:test 全量 7 套件 132 项及 :server:test 全量 15 套件 136 项测试)

---

## 1. 生产代码修改清单

所有修改严格受限于 `client` 模块生产代码，保持 V4 架构单向依赖（client 仅依赖 protocol，严禁引用 engine 或 server 实现），未修改任何测试代码或夹具：

1. [`client/src/main/kotlin/com/majiang/client/api/TileMarkModels.kt`](../../client/src/main/kotlin/com/majiang/client/api/TileMarkModels.kt) (新建)
   - 客户端纯净领域模型：定义独立且不可变的 `TileMarkConfig`、`TileMarkRequest`、`RiverPositionView`、`TileMarkArea`、`MarkedTileView`、`TileMarkGroupView`、`TileMarkView`。
   - 牌面表示采用客户端已有 `TileView`，完全不引用 engine 或 protocol 类型。
   - 集合防御性复制：所有列表/映射均通过 `Collections.unmodifiableList` / `LinkedHashSet` / `LinkedHashMap` 保护。
   - 结构有效性校验：
     - `TileMarkRequest` 仅校验 `requestId`、`sessionId`、`roundId`、`decisionId` 非空、`controlEpoch >= 0`、`tileIds.all { it in 0..135 }`；四张、唯一性与归属由权威校验；
     - `TileMarkArea` 包含 `OWN_HAND`、`OPPONENT_HAND`、`DISCARD`、`EXPOSED_MELD`、`EXPOSED_INDICATOR`、`NOT_VISIBLE`；
     - `RiverPositionView.discardIndex >= 0`、`seat in 0..3`、`roundId` 非空；
     - `MarkedTileView`：处于 `TileMarkArea.DISCARD` 时必须提供 `riverPosition` 且 `riverPosition.seat == seat`；非 `DISCARD` 时 `riverPosition` 必须为 null。

2. [`client/src/main/kotlin/com/majiang/client/api/ClientModels.kt`](../../client/src/main/kotlin/com/majiang/client/api/ClientModels.kt)
   - `MatchConfig`: 增加 `tileMarkConfig: TileMarkConfig = TileMarkConfig()`（默认关闭；启用时费用为权威经济 10，持续当局另3局，上限3组共12张）。保留完整手写 `componentN`、`copy`、`deepCopy`、`equals`、`hashCode`、`toString`。
   - `Capabilities`: 增加 `tileMarkSupported: Boolean = false`（默认 false），并纳入 `componentN`、`copy`、`equals`、`hashCode`、`toString`；该类型没有 `deepCopy` 方法。

3. [`client/src/main/kotlin/com/majiang/client/api/ViewModels.kt`](../../client/src/main/kotlin/com/majiang/client/api/ViewModels.kt)
   - `GameSnapshotView`: 增加可空 `val tileMark: TileMarkView? = null`。
   - 牌河一致性校验：在 `init` 块中加入牌河实体匹配校验——被标记为 `DISCARD` 的牌必须满足 `pos.seat == tile.seat`，且在对应席位当前 `discards[pos.seat]` 的 `pos.discardIndex` 处精确存在且实体 `tileId` 匹配。
   - 保留手写 `copy`、`deepCopy`、`equals`、`hashCode`、`toString`。

4. [`client/src/main/kotlin/com/majiang/client/api/GameSession.kt`](../../client/src/main/kotlin/com/majiang/client/api/GameSession.kt)
   - `GameSession` 接口增加 `fun submitTileMark(request: TileMarkRequest): CompletableFuture<Receipt>`。
   - 默认实现返回以 `UnsupportedOperationException` 失败的 Future，旧实现不会误宣告支持落汗。
   - `SpectatorSession` 维持只读，不增加任何标记方法。

5. [`client/src/main/kotlin/com/majiang/client/mapping/ProtocolViewMapper.kt`](../../client/src/main/kotlin/com/majiang/client/mapping/ProtocolViewMapper.kt)
   - 配置与能力映射：`mapMatchConfig`、`toProtocolMatchConfig`、`mapCapabilities`、`toProtocolCapabilities` 双向映射 `tileMarkConfig` 与 `tileMarkSupported`。
   - 快照映射：`mapSnapshot` 将 `snap.tileMark` 映射为 `mapTileMark(snap.tileMark)`。
   - 独立映射辅助函数：实现 `mapTileMarkConfig` / `toProtocolTileMarkConfig`、`mapTileMarkArea` / `toProtocolTileMarkArea`、`mapRiverPosition` / `toProtocolRiverPosition`、`mapMarkedTile` / `toProtocolMarkedTile`、`mapTileMarkGroup` / `toProtocolTileMarkGroup`、`mapTileMark` / `toProtocolTileMark`、`toProtocolTileMarkRequest`（方法无 View 后缀）。保持在 `mapping` 包中，不泄露至 `client.api`。

6. [`client/src/main/kotlin/com/majiang/client/session/ServerConnectionSession.kt`](../../client/src/main/kotlin/com/majiang/client/session/ServerConnectionSession.kt)
   - 门面实现：实现 `GameSession.submitTileMark(request: TileMarkRequest): CompletableFuture<Receipt>`。
   - 观战隔离：观战端调用时，经 `dispatch` 用 `IllegalStateException` 异常完成返回的 Future。
   - 调度与原子在途锁 (`pendingState` & `activeActionId`)：
     - 使用原子状态 `pendingState.compareAndSet(false, true)` 与 `activeActionId` 实施在途锁，通过可注入的 `dispatch` 调度回调；
     - 若已有在途动作，直接返回 `ReceiptStatus.REJECTED` 且 `reason = "ACTION_IN_FLIGHT"`；
     - 递增 `actionSubmissionSeq` 并记录本地 `actionId`；
     - 追踪生命周期：通过 `trackFuture(resultFuture, epoch)` 与 `trackRoomFuture(resultFuture, roomEpoch)` 注册代次；当请求完成、被拒、异常或取消时，通过 `activeActionId.compareAndSet(actionId, 0L)` 释放 `pendingState`；
     - 旧代次或关闭状态到达的回执无法干扰新会话。
   - 完整快照替换与状态缓存：
     - 私有标记状态严格来自权威完整快照（`tileMark`）。
     - 空组、单组过期消失、`tileMark == null` 均由新快照完整替换，不合并不复活已过期组。
     - 不做客户端乐观计数或主动扣费，完全交由权威快照驱动。
     - 离房、转观战或会话关闭时彻底清除私有快照缓存。

7. [`client/src/main/kotlin/com/majiang/client/transport/udp/UdpClientBinding.kt`](../../client/src/main/kotlin/com/majiang/client/transport/udp/UdpClientBinding.kt)
   - 真实覆盖 `SessionApi.submitTileMark(request: ProtocolTileMarkRequest): CompletableFuture<ProtocolActionReceipt>`。
   - 构造 `ApiRequest(api = ApiTags.SUBMIT_TILE_MARK, requestId = request.requestId, tileMark = request)`。
   - 通过 `sendRequest` 接入原生可靠请求重传窗口、关闭保护及失败回执回退。

---

## 2. A2 交付文档纠偏说明 (DELIVERY-ANTI-A2.md)

根据 Codex 审查反馈及实际服务端生产源码核对，已同步更正 [`doc/tile-mark/DELIVERY-ANTI-A2.md`](DELIVERY-ANTI-A2.md) 中与源码不符的表述：
1. **回执键命名**：更正为实际的 `HostReceiptKey.Action`、`HostReceiptKey.Shuffle`、`HostReceiptKey.Mark`（原误记为 `HostReceiptKey.Choice` / `ShufflePlan`）。
2. **错误原因码**：更正为实际的 `STALE_SESSION`、`SEAT_DISCONNECTED`、`SEAT_NOT_HUMAN`（原误记为 `DISCONNECTED` 与 `TAKEOVER_ACTIVE`）。
3. **消除虚构 Helper 描述**：删除了原文档中提及的 `ActionReceipt.staleSession`、`ActionReceipt.deadlineExpired`、`updateDecisionClock` 等不存在的辅助函数，如实描述为直接构造 `ActionReceipt` 以及调用 `syncDecisionsAndClocks()`。
4. **权威校验与游标机制**：明确描述为由 `authority.submitTileMark(seat, request, clock.nowMillis())` 执行包含截止期 (deadline) 在内的权威判定，幂等重放时从 `issuedReceipts[key]` 读取并保持原游标。
5. **UdpRoom 验证器传递**：更正为使用原生的 `sessionValidator(session)` 方法引用，移除文档中伪造的 lambda 描述。

---

## 3. 测试验证与命令统计

所有测试和夹具代码均由 Codex 独立维护，未做任何修改、弱化或跳过：
- `client/src/test/kotlin/com/majiang/client/api/TileMarkClientContractTest.kt` (7 项)
- `server/src/test/kotlin/com/majiang/server/udp/TileMarkClientUdpIntegrationTest.kt` (1 项)

### 3.1 定向契约与集成测试
1. **客户端契约测试** (`TileMarkClientContractTest`)：
   - 执行命令：`./gradlew :client:test --tests "com.majiang.client.api.TileMarkClientContractTest" --info`
   - 结果：**7 项全数通过**（0 失败，0 错误，0 跳过）
     - `mapping retains private river position config and immutable collections`
     - `out of order updates and explicit resume replace mark state without resurrecting groups`
     - `push before receipt stays current and full snapshots remove expired groups and null effects`
     - `sync failure async failure cancellation and close release only their own pending action`
     - `room replacement retires old request and spectator never retains private marks`
     - `automatic reconnect restores all groups and old connection completion cannot unlock the new action`
     - `mark shares pending lock with discard and shuffle and does not update cache optimistically`

2. **UDP 真实客户端服务端端到端集成测试** (`TileMarkClientUdpIntegrationTest`)：
   - 执行命令：`./gradlew :server:test --tests "com.majiang.server.udp.TileMarkClientUdpIntegrationTest" --info`
   - 结果：**1 项全数通过**（0 失败，0 错误，0 跳过）
     - `real client facade casts over UDP receives private push and restores exact river position`

### 3.2 完整模块测试
1. **Client 模块全量测试**：
   - 执行命令：`./gradlew :client:test`
   - 结果：**7 套件，132 项测试全数通过**（0 失败，0 错误，0 跳过）
     - `ProtocolViewMapperTest`: 19
     - `ProtocolViewMappingRegressionTest`: 6
     - `ProtocolViewPreviewMapperTest`: 7
     - `ServerConnectionSessionTest`: 63
     - `TileMarkClientContractTest`: 7
     - `UdpClientBindingTest`: 27
     - `UdpClientFailureTest`: 3

2. **Server 模块全量测试**：
   - 执行命令：`./gradlew :server:test`
   - 结果：**15 套件，136 项测试全数通过**（0 失败，0 错误，0 跳过）
     - `ConnectionControlTest`: 9
     - `ServerTableHostCoverageTest`: 26
     - `ServerTableHostTest`: 7
     - `V4RecoveryProjectionPreservationTest`: 4
     - `InProcessBindingTest`: 23
     - `EngineProtocolMapperTest`: 8
     - `EngineProtocolPreviewMapperTest`: 4
     - `TileMarkServerContractTest`: 4
     - `SharedRoomTest`: 13
     - `TileMarkClientUdpIntegrationTest`: 1
     - `TileMarkUdpContractTest`: 1
     - `UdpClientSessionTest`: 6
     - `UdpRoomTest`: 18
     - `UdpTableServerRegressionTest`: 4
     - `UdpTableServerSmokeTest`: 8

---

## 4. 边界与约束确认

1. **零跨层污染**：生产代码修改仅限 `client` 模块；未触碰 `engine`、`protocol` 或 `server` 生产源码；未接入 UI/presentation 表现层。
2. **测试零修改**：严格未修改、未删除、未跳过任何由 Codex 维护的测试或测试夹具。
3. **工作区整洁**：未执行 `git commit`，未启动额外代理，未修改 `lwjgl3/build.gradle`、`SnapshotLauncher.kt`、`snapshot/`。
4. **回调单一性**：仅在 `doc/tile-mark/` 下输出回调，未在根目录重复创建回调文件。
