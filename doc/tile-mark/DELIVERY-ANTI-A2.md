# A2 服务端映射、本地和 UDP 接线交付说明 (DELIVERY-ANTI-A2.md)

- **执行者**: Anti
- **任务编号**: A2 (TASK-ANTI-A2.md)
- **交付目标**: 落汗 (Tile Mark) 服务端模型双向映射、单桌串行权威队列接线、进程内绑定与原生 UDP 接入
- **交付状态**: ready_for_review (已全量通过 Codex 5 项服务端契约测试及 server 模块全量 14 套件 135 项测试)

---

## 1. 生产代码修改清单

所有修改严格受限于 `server` 模块生产代码，保持 V4 架构分层规范，未修改其他模块，未改动任何测试代码：

1. [`server/src/main/kotlin/com/majiang/server/mapping/EngineProtocolMapper.kt`](../../server/src/main/kotlin/com/majiang/server/mapping/EngineProtocolMapper.kt)
   - 双向映射请求模型：`toEngineTileMarkRequest` 与 `toProtocolTileMarkRequest`。
   - 双向映射配置模型：`toProtocolTileMarkConfig` 与 `toEngineTileMarkConfig`，并在 `toProtocolConfig` / `toEngineConfig` 完整保留 `tileMarkConfig`。
   - 区域与牌河定位：`toProtocolTileMarkArea` / `toEngineTileMarkArea`、`toProtocolRiverPosition` / `toEngineRiverPosition`。
   - 标记牌与组视图：`toProtocolMarkedTile` / `toEngineMarkedTile`、`toProtocolTileMarkGroup` / `toEngineTileMarkGroup`。
   - 私有快照视图：`toProtocolTileMarkView` / `toEngineTileMarkView`，并在 `toProtocolSnapshot` 映射私有 `tileMark`。
   - 公开快照隔离：`toProtocolPublicSnapshot` 完全不包含任何 `tileMark`、`groupId` 等私有字段。
   - 服务端能力宣告：`toProtocolCapabilities` 明确宣告 `tileMarkSupported = true`。

2. [`server/src/main/kotlin/com/majiang/server/host/ServerTableHost.kt`](../../server/src/main/kotlin/com/majiang/server/host/ServerTableHost.kt)
   - 回执键隔离：新增 `HostReceiptKey.Mark(val roundId: String, val seat: Int, val requestId: String)`，确保施术回执与常规动作选择 (`HostReceiptKey.Action`) 和洗牌方案 (`HostReceiptKey.Shuffle`) 的幂等回执缓存互不污染。
   - 串行权威队列入口：实现 `submitTileMark(seat: Int, request: TileMarkRequest, sessionValidator: (() -> Boolean)? = null): CompletableFuture<ActionReceipt>`：
     - 在单桌独占单线程执行器 `serialExecutor` 内部排队执行，严禁在网络 I/O 线程预先判定有效性。
     - 校验 `sessionValidator` 绑定的有效性（若失效返回 `ActionReceipt(..., ReceiptStatus.REJECTED, reason = "STALE_SESSION")`）。
     - 校验网络连接状态（已断线返回 `ActionReceipt(..., ReceiptStatus.REJECTED, reason = "SEAT_DISCONNECTED")`）。
     - 校验控制权归属（托管状态或非真人控制返回 `ActionReceipt(..., ReceiptStatus.REJECTED, reason = "SEAT_NOT_HUMAN")`）。
     - 委托引擎：通过 `authority.submitTileMark(seat, request, clock.nowMillis())` 执行权威判定（含权威截止点 deadline 判定）、扣除筹码与更新标记。
     - 幂等去重与重试：若非首次生效重放请求，优先读取 `issuedReceipts[key]` 返回原回执与原游标，不重复扣减筹码，不产生新游标。
     - 状态同步：若状态发生变化（`res.stateChanged`），调用 `syncDecisionsAndClocks()`、`publishStateChanges(res.events)` 并记录最后动作时间；缓存非 REJECTED 回执。
   - 私有快照推送触发：在 `publishStateChanges()` 中增加 `prevSnap.tileMark != candidateSnap.tileMark` 判定，即使无公开桌级事件，只要私有标记状态变化即保证向客户端推送完整私有快照。

3. [`server/src/main/kotlin/com/majiang/server/room/SharedRoom.kt`](../../server/src/main/kotlin/com/majiang/server/room/SharedRoom.kt)
   - 增加 `submitTileMark(seat: Int, request: TileMarkRequest, sessionValidator: (() -> Boolean)? = null)`，透明委托给 `tableHost.submitTileMark`。
   - 保持边界：对局进行中不允许新玩家加入占座（`join` 仅在 `RoomLifecycle.LOBBY` 允许），断线重连 (`resumeSession`) 仅恢复原席位私有标记视图，杜绝换人占座继承他人暗记的可能。

4. [`server/src/main/kotlin/com/majiang/server/inprocess/InProcessBinding.kt`](../../server/src/main/kotlin/com/majiang/server/inprocess/InProcessBinding.kt)
   - 能力宣告：`capabilities()` 返回 `tileMarkSupported = true`。
   - 接口实现：实现 `submitTileMark(request: ProtocolTileMarkRequest): CompletableFuture<ProtocolActionReceipt>`：
     - 校验连接状态，关闭状态直接构造并返回 `ProtocolActionReceipt(request.requestId, ProtocolReceiptStatus.REJECTED, reason = "SESSION_CLOSED")`。
     - 观战会话严格拒绝：`role == InProcessRole.SPECTATOR` 时直接构造并返回 `ProtocolActionReceipt(request.requestId, ProtocolReceiptStatus.REJECTED, reason = "SPECTATOR_READ_ONLY")`。
     - 通过 `EngineProtocolMapper` 映射为领域 `TileMarkRequest` 并调用 `room.submitTileMark`。
     - 席位来源严格采用连接所绑定的 `seat`（`viewerSeat`），不从载荷读取。

5. [`server/src/main/kotlin/com/majiang/server/udp/UdpRoom.kt`](../../server/src/main/kotlin/com/majiang/server/udp/UdpRoom.kt)
   - 增加 `submitTileMark(session: UdpClientSession, request: ProtocolTileMarkRequest): CompletableFuture<ProtocolActionReceipt>`：
     - 通过 `commandTarget(session)` 校验当前为已加入对局的合法玩家会话。
     - 传入原生方法引用 `sessionValidator(session)` 防旧会话重放。
     - 席位固定为 `session.seat`，委托给 `sharedRoom.submitTileMark`。

6. [`server/src/main/kotlin/com/majiang/server/udp/UdpTableServer.kt`](../../server/src/main/kotlin/com/majiang/server/udp/UdpTableServer.kt)
   - 观战会话防护：将 `ApiTags.SUBMIT_TILE_MARK` 加入 `spectatorForbiddenApis` 集合，观战端提交直接返回 `ApiErrors.UNAUTHENTICATED_SESSION`。
   - 会话路由：在 `handleSessionReq` 中拦截 `ApiTags.SUBMIT_TILE_MARK`，委托给 `room.submitTileMark(session, requireNotNull(request.tileMark))`。
   - 能力宣告：建房前及入座后能力查询均返回 `tileMarkSupported = true`。

---

## 2. 关键设计与安全边界说明

1. **席位绑定与权限隔离 (Authentication & Seat Source)**:
   - 所有的施术请求席位均直接读取认证会话的 `viewerSeat` 或 `session.seat`，彻底杜绝从客户端载荷中读取伪造席位的可能。
   - 观战者发起施术在进程内会话返回 `SPECTATOR_READ_ONLY`，在 UDP 传输层直接被 `spectatorForbiddenApis` 拦截并返回 `UNAUTHENTICATED_SESSION`。
   - 公开快照完全抹除 `tileMark`，非施术者座位的私有快照中 `activeGroups` 恒为空。

2. **权威单线程队列与重放幂等 (Serial Authority & Deduplication)**:
   - 所有施术操作排队进入 `ServerTableHost` 单线程串行执行器。
   - `HostReceiptKey.Mark` 确保对同一 `(roundId, seat, requestId)` 的多次重试直接返回原有回执，不会二次扣减千术点，且不生成新游标。
   - 截止期过后重放先命中幂等缓存返回旧回执；新请求在截止期过后则返回 `DEADLINE_EXPIRED` 拒绝。

3. **打出标记牌与牌河精确定位 (Discard Tracking & River Position)**:
   - 施术后打出被标记的牌时，快照更新为 `area = ProtocolTileMarkArea.DISCARD`，并附带精确的 `ProtocolRiverPosition(roundId, seat, discardIndex)`。

4. **UDP 分片与断线重连恢复 (Fragmentation & Resume)**:
   - 真实 UDP 回环中施术产生 12 张牌 3 个标记组的大快照时，平稳穿透 UDP 分片与重组通道（`fragmentsReceived > 0`）。
   - 客户端携带 `resumeToken` 调用 `RESUME_SESSION` 恢复连接后，私有标记组与千术点数无损恢复。

5. **换人占座与生命周期边界**:
   - `SharedRoom.join` 仅允许在 `LOBBY` 阶段加入。对局开始后不支持中途换人占座，因此不存在新玩家继承旧玩家私有暗记的可能。

---

## 3. 测试验证与命令统计

Codex 维护的测试文件保持 100% 原始完整性，未做任何修改、删除或弱化：
- `server/src/test/kotlin/com/majiang/server/mark/TileMarkServerContractTest.kt` (SHA-256: `3B6209A6BC35BB43F53B803100725954AE718E7F77A8F28A62058FB2AE51B5D2`)
- `server/src/test/kotlin/com/majiang/server/udp/TileMarkUdpContractTest.kt` (SHA-256: `25CC362FE8EDD5A697896387D45E8E455F34B2418820CE57442FFBAA5DA2EC10`)

### 3.1 定向契约测试
执行命令：
```powershell
.\gradlew :server:test --tests "com.majiang.server.mark.TileMarkServerContractTest" --tests "com.majiang.server.udp.TileMarkUdpContractTest" --info
```
结果：
- `TileMarkServerContractTest`：**4 项全数通过**（耗时 0.050s，0 失败，0 错误，0 跳过）
- `TileMarkUdpContractTest`：**1 项全数通过**（耗时 1.092s，0 失败，0 错误，0 跳过）
- **合计：5 项测试，100% 通过**

### 3.2 Server 模块全量测试
执行命令：
```powershell
.\gradlew cleanTest :server:test --info
```
结果（全量 14 个套件，共计 135 项测试，零失败、零错误、零跳过）：
1. `com.majiang.server.control.ConnectionControlTest`: 9 tests
2. `com.majiang.server.host.ServerTableHostCoverageTest`: 26 tests
3. `com.majiang.server.host.ServerTableHostTest`: 7 tests
4. `com.majiang.server.host.V4RecoveryProjectionPreservationTest`: 4 tests
5. `com.majiang.server.inprocess.InProcessBindingTest`: 23 tests
6. `com.majiang.server.mapping.EngineProtocolMapperTest`: 8 tests
7. `com.majiang.server.mapping.EngineProtocolPreviewMapperTest`: 4 tests
8. `com.majiang.server.mark.TileMarkServerContractTest`: 4 tests
9. `com.majiang.server.room.SharedRoomTest`: 13 tests
10. `com.majiang.server.udp.TileMarkUdpContractTest`: 1 test
11. `com.majiang.server.udp.UdpClientSessionTest`: 6 tests
12. `com.majiang.server.udp.UdpRoomTest`: 18 tests
13. `com.majiang.server.udp.UdpTableServerRegressionTest`: 4 tests
14. `com.majiang.server.udp.UdpTableServerSmokeTest`: 8 tests
**总计：14 套件，135 项测试全部通过。**
