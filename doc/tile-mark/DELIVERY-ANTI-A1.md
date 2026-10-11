# A1 线路契约与编解码交付说明 (DELIVERY-ANTI-A1.md)

- **执行者**: Anti
- **任务编号**: A1 (TASK-ANTI-A1.md)
- **交付目标**: 落汗 (Tile Mark) 独立线路契约、DTO 模型与严密编解码器
- **交付状态**: ready_for_review (已全量通过 Codex 10 项契约测试及 protocol 全量 150 项测试)

---

## 1. 生产代码修改与实现清单

所有实现均严格限定在 `protocol` 生产代码源集，未引入对 `engine`、`server` 或 libGDX 的任何反向依赖。

### 1.1 独立传输层模型 (`com.majiang.transport.api.ProtocolTileMarkModels.kt`)
新建独立的强类型传输 DTO 文件，完全与 engine 隔离，牌面使用 `ProtocolTile`：
- `ProtocolTileMarkConfig(enabled: Boolean = false)`: 默认关闭。
- `ProtocolTileMarkRequest(requestId, sessionId, roundId, decisionId, controlEpoch, tileIds)`:
  - 构造时对 `tileIds` 进行防御性拷贝，暴露 `Collections.unmodifiableList` 只读视图。
  - 请求字段固定为客户端所需参数，座位由认证上下文绑定；客户端严禁提交 `cost`、`expiresAfterRoundOrdinal` 或位置信息。
- `ProtocolRiverPosition(roundId, seat, discardIndex)`:
  - 精确标记牌河位置，校验 `roundId.isNotBlank()`、`seat in 0..3`、`discardIndex >= 0`。
- `ProtocolTileMarkArea`: 枚举区域 `OWN_HAND`、`OPPONENT_HAND`、`DISCARD`、`EXPOSED_MELD`、`EXPOSED_INDICATOR`、`NOT_VISIBLE`。
- `ProtocolMarkedTileView(tileId, tile, area, seat, riverPosition)`:
  - `tileId in 0..135` 且 `tile.tileId == tileId`。
  - 区域为 `DISCARD` 时要求 `seat in 0..3`、`riverPosition != null` 且 `riverPosition.seat == seat`。
  - 区域为非 `DISCARD` 时要求 `riverPosition == null`。
  - 区域为 `EXPOSED_INDICATOR` 或 `NOT_VISIBLE` 时要求 `seat == null`；手牌与副露区域要求 `seat in 0..3`。
- `ProtocolTileMarkGroupView(groupId, createdRoundId, createdRoundOrdinal, expiresAfterRoundOrdinal, futureRoundsRemaining, isLastEffectiveRound, tiles)`:
  - 防御性包装 4 张标记牌 `tiles`（不可变只读）。
  - 要求 `this.tiles.size == 4` 且 4 张牌的 `tileId` 严格互不相同。
  - 局序有效范围 `createdRoundOrdinal in 1..(Int.MAX_VALUE - 3)`，到期局序 64 位校验 `expiresAfterRoundOrdinal.toLong() == createdRoundOrdinal.toLong() + 3L`（严格防止 32 位溢出环绕）。
  - `futureRoundsRemaining in 0..3`，且 `isLastEffectiveRound == (futureRoundsRemaining == 0)`。
- `ProtocolTileMarkView(canApply, disabledReason, cost, activeTileCount, maxActiveTiles, selectableTileIds, decisionId, controlEpoch, stateVersion, activeGroups)`:
  - 权威费用固定为 10（`cost == 10`），最大有效标记上限固定为 12（`maxActiveTiles == 12`）。
  - `activeTileCount in 0..12`，`activeGroups.size in 0..3`。
  - 防御性复制只读 `selectableTileIds` 和 `activeGroups`。
  - 不变量检查：所有 activeGroups 中牌数总和必须精确等于 `activeTileCount`；跨组 `tileId` 互斥无重叠；跨组 `groupId` 互不重复。

### 1.2 会话异步接口 (`com.majiang.server.api.SessionApi.kt`)
- 新增 `submitTileMark(request: ProtocolTileMarkRequest): CompletableFuture<ProtocolActionReceipt>`。
- 默认提供明确失败的实现：`failedFuture(UnsupportedOperationException("submitTileMark not implemented"))`，严禁假成功。

### 1.3 核心协议模型扩展 (`com.majiang.transport.api.ProtocolModels.kt`)
- `ProtocolAccessCapabilities`: 追加 `val tileMarkSupported: Boolean = false`，默认 false；手写 `component6`、`copy`、`equals`、`hashCode`、`toString` 保留该字段。
- `ProtocolMatchConfig`: 追加 `val tileMarkConfig: ProtocolTileMarkConfig = ProtocolTileMarkConfig()`，默认关闭；
  - 校验规则：当 `tileMarkConfig.enabled == true` 时，强制校验 `cheatEconomy.enabled == true` 且 `cheatEconomy.costs["TILE_MARK"] == 10`。
  - 手写 `component14`、`copy`、`deepCopy`、`equals`、`hashCode`、`toString` 保留该字段。
- `ProtocolPlayerSnapshot`: 追加末尾私有字段 `val tileMark: ProtocolTileMarkView? = null`，默认 null。
  - 权威快照不变性校验：若快照包含 `tileMark`，其所有标记牌若处于 `DISCARD` 区域，校验 `riverPosition.roundId == this.meta.roundId`、`riverPosition.seat in 0..3`、`riverPosition.discardIndex in 0 until view.discards[seat].size`，且对应牌河该位置的牌实体 ID 与标记牌的 `tileId` 精确一致。
  - 手写 `component18`、`copy`、`equals`、`hashCode` 保留该字段。
  - 公开快照 (`ProtocolPublicSnapshot`)、观战绑定及公开结果响应完全不增加 `tileMark` 私有字段。

### 1.4 API 消息与编解码器 (`com.majiang.transport.api.ApiMessage.kt`)
- `ApiTags`: 追加 `SUBMIT_TILE_MARK = "submitTileMark"`，并加入 `ApiTags.KNOWN` 集合。
- `ApiRequest`: 追加末尾字段 `val tileMark: ProtocolTileMarkRequest? = null`。
- 信封与互斥校验：
  - 拒绝伪造的业务载荷组合（当 `api == ApiTags.SUBMIT_TILE_MARK` 时必须有 `tileMark`，且不得包含 `choice`、`shufflePlan` 等其他载荷；其他 API 严禁夹带 `tileMark`）。
  - 拒绝信封身份不匹配：`envelope.requestId == tileMark.requestId`。
  - 观战会话严禁发起 `SUBMIT_TILE_MARK`。
- 严格 Gson TypeAdapter 集合：
  - `ProtocolTileMarkConfigAdapter`：白名单字段校验，未知字段立即抛 `JsonParseException`。
  - `ProtocolTileMarkRequestAdapter`：白名单校验，伪造字段拒绝（例如 `ownerSeat`, `cost`, `expiresAfterRoundOrdinal`, `discardIndex`），严格整数校验（拒绝浮点数、字符串数字及 64 位溢出）。
  - `ProtocolRiverPositionAdapter`：严格整数与非负校验。
  - `ProtocolMarkedTileViewAdapter`：区域与座位/牌河位置的强契约校验，枚举字符串安全解析。
  - `ProtocolTileMarkGroupViewAdapter`：严格 JSON 对象元素校验、防 null 注入、防整数溢出、剩余局数范围及最后有效局不变量校验。
  - `ProtocolTileMarkViewAdapter`：防御性检查组元素合法性、权威费用 10 校验、总数守恒及无重复牌/组校验。
  - 注册至 `ApiMessageCodec.gson`。
- 旧线向下兼容性：
  - 旧配置、旧能力、旧快照 JSON 缺少新增字段时，均平滑反序列化为 `enabled = false`、`tileMarkSupported = false`、`tileMark = null`。

---

## 2. 测试执行与验证结果

### 2.1 Codex 定向契约测试 (`ProtocolTileMarkContractTest`)
执行指令：
```powershell
.\gradlew :protocol:test --tests "com.majiang.transport.api.ProtocolTileMarkContractTest" --info
```
测试报告 XML (`protocol/build/test-results/test/TEST-com.majiang.transport.api.ProtocolTileMarkContractTest.xml`)：
- **用例总数**: 10
- **通过**: 10
- **失败**: 0
- **错误**: 0
- **跳过**: 0
- **用例清单**:
  1. `snapshot rejects river positions from another round seat index or physical tile` - PASSED
  2. `private snapshot and PUSH retain exact river position and all groups` - PASSED
  3. `request and decoded snapshot expose immutable defensive collections` - PASSED
  4. `tile mark request survives the real API envelope codec` - PASSED
  5. `unknown area hidden seat and missing discard position are rejected` - PASSED
  6. `wire rejects forged authority fields and noninteger identities` - PASSED
  7. `malformed group sizes duplicates counts and authoritative fee are rejected` - PASSED
  8. `old wire defaults to disabled unsupported and no marks` - PASSED
  9. `enabled configuration and supported capability survive copy and wire` - PASSED
  10. `identity mismatch missing payload and mixed operation payloads are rejected` - PASSED

### 2.2 Protocol 模块全量测试
执行指令：
```powershell
.\gradlew :protocol:test
```
XML 统计结果：
- `ApiDtoValidationTest`: 31 tests, 0 failures, 0 errors
- `FakeServerConnectionSmokeTest`: 7 tests, 0 failures, 0 errors
- `ApiMessageCodecTest`: 11 tests, 0 failures, 0 errors
- `ProtocolModelsTest`: 17 tests, 0 failures, 0 errors
- `ProtocolPreviewModelsCodecTest`: 8 tests, 0 failures, 0 errors
- `ProtocolPublicModelsCodecTest`: 12 tests, 0 failures, 0 errors
- `ProtocolRoomModelsCodecTest`: 5 tests, 0 failures, 0 errors
- `ProtocolSerializationTest`: 8 tests, 0 failures, 0 errors
- `ProtocolTileMarkContractTest`: 10 tests, 0 failures, 0 errors
- `FragmenterTest`: 5 tests, 0 failures, 0 errors
- `HeartbeatPacerTest`: 4 tests, 0 failures, 0 errors
- `PushRetransmitWindowTest`: 5 tests, 0 failures, 0 errors
- `PushSequencerTest`: 6 tests, 0 failures, 0 errors
- `ReliableRequestorTest`: 5 tests, 0 failures, 0 errors
- `ReplayGuardTest`: 4 tests, 0 failures, 0 errors
- `UdpFrameCodecTest`: 12 tests, 0 failures, 0 errors
- **protocol 模块合计**: **150 项测试全数通过**（Tests=150, Failures=0, Errors=0, Skipped=0）。

### 2.3 全系统冒烟测试
执行指令：
```powershell
.\gradlew test
```
- 结果: `BUILD SUCCESSFUL in 20s` (全部 65 项 Gradle 任务顺利完成，全项目测试零回归)。

---

## 3. 规范约束复核

- [x] 未修改任何测试代码（`protocol/src/test` 保持纯净，由 Codex 独占维护）。
- [x] 未跳过或删除任何测试。
- [x] 仅修改 `protocol` 模块生产代码，未修改 `engine`、`server`、`client`、`presentation`。
- [x] 未执行任何 Git commit。
- [x] 未启动其他代理进程。
- [x] 真实链路与 UI 明确声明尚未接线（待后续阶段完成）。
