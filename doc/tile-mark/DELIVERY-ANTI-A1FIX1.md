# A1FIX1 交付文档：快照缺失当前局号时严禁携带牌河定位修复

- **任务编号**: A1FIX1 (`doc/tile-mark/TASK-ANTI-A1FIX1.md`)
- **执行角色**: Anti (Protocol 修复实现)
- **审查角色**: Codex (独立检视与验收)
- **修改模块**: `protocol`
- **交付日期**: 2026-10-11

---

## 1. 缺陷根因与复现分析

### 1.1 缺陷现象
Codex 在 `ProtocolTileMarkContractTest` 中追加了第 11 项回归测试 `discard position cannot belong to a snapshot without a current round`：
在快照缺少当前局号（`roundId == null`，或 JSON 缺失/显式为 null）但标记视图中包含 `area == DISCARD` 的定位牌时，期望构造与解码严格拒绝。此前测试复现失败：
- `source.copy(roundId = null)` 未抛出 `IllegalArgumentException`；
- JSON 反序列化未抛出 `JsonParseException`，畸形快照被非法放行。

### 1.2 根因定位
在 `ProtocolPlayerSnapshot` 构造校验与 `ApiMessage` 快照适配器中：
- 牌河位置校验逻辑原为：
  ```kotlin
  // ProtocolModels.kt 原逻辑
  if (roundId != null) {
      require(pos.roundId == roundId) { ... }
  }

  // ApiMessage.kt 原逻辑
  if (roundId != null && pos.roundId != roundId) {
      throw JsonParseException(...)
  }
  ```
- 当快照未处于对局中（`roundId == null`）时，上述条件直接短路跳过局号一致性校验，导致 `DISCARD` 牌河定位逃逸了当前局约束。

---

## 2. 修复方案与代码变更

遵循任务约束，仅在 `protocol` 模块生产环境的上述两处边界进行针对性收紧：

### 2.1 `ProtocolModels.kt`（构造校验）
在 `ProtocolPlayerSnapshot` 的 `init` 块中，对 `tile.area == ProtocolTileMarkArea.DISCARD` 的牌河定位强约束 `roundId` 必须非空且严格等于快照局号：
```kotlin
if (tile.area == ProtocolTileMarkArea.DISCARD) {
    val pos = tile.riverPosition
        ?: throw IllegalArgumentException("Missing riverPosition for DISCARD marked tile")
    require(roundId != null && pos.roundId == roundId) {
        "RiverPosition roundId (${pos.roundId}) does not match snapshot roundId ($roundId)"
    }
    require(pos.seat == tile.seat) { ... }
    ...
}
```

### 2.2 `ApiMessage.kt`（解码适配器校验）
在 `ProtocolPlayerSnapshotAdapter.deserialize` 中，针对 `DISCARD` 定位严格校验快照 `roundId` 非空并一致：
```kotlin
if (tile.area == ProtocolTileMarkArea.DISCARD) {
    val pos = tile.riverPosition
        ?: throw JsonParseException("Missing riverPosition for DISCARD marked tile")
    if (roundId == null || pos.roundId != roundId) {
        throw JsonParseException("RiverPosition roundId (${pos.roundId}) does not match snapshot roundId ($roundId)")
    }
    if (pos.seat != tile.seat) { ... }
    ...
}
```

### 2.3 边界完整性与安全性
- **合法快照不受影响**：大厅（LOBBY）和准备阶段（PREPARING）等合法无局快照在无标记、或标记非 `DISCARD` 区域（如 `NOT_VISIBLE`）时，`roundId == null` 仍按规被合法接受。
- **无跨模块副作用**：未改动 engine、server、client 模块，未触碰正在进行的 A2 服务端任务或 `PROGRESS-ANTI.md`。
- **测试零修改**：严格保留 Codex 编写的所有测试，无任何修改、删除或弱化。

---

## 3. 测试验证与结果

### 3.1 定向合约测试（11 项全通）
```bash
./gradlew :protocol:test --tests "com.majiang.transport.api.ProtocolTileMarkContractTest"
```
测试结果：
- `discard position cannot belong to a snapshot without a current round`: **PASSED**
- `snapshot rejects river positions from another round seat index or physical tile`: **PASSED**
- `private snapshot and PUSH retain exact river position and all groups`: **PASSED**
- `request and decoded snapshot expose immutable defensive collections`: **PASSED**
- `tile mark request survives the real API envelope codec`: **PASSED**
- `unknown area hidden seat and missing discard position are rejected`: **PASSED**
- `wire rejects forged authority fields and noninteger identities`: **PASSED**
- `malformed group sizes duplicates counts and authoritative fee are rejected`: **PASSED**
- `old wire defaults to disabled unsupported and no marks`: **PASSED**
- `enabled configuration and supported capability survive copy and wire`: **PASSED**
- `identity mismatch missing payload and mixed operation payloads are rejected`: **PASSED**

**总计：11 tests completed, 0 failed.**

### 3.2 协议全量测试（16 套件 / 151 项全通）
```bash
./gradlew :protocol:test --rerun-tasks
```
测试报告统计：
- `ApiDtoValidationTest`: 31 tests passed
- `FakeServerConnectionSmokeTest`: 7 tests passed
- `ApiMessageCodecTest`: 11 tests passed
- `ProtocolModelsTest`: 17 tests passed
- `ProtocolPreviewModelsCodecTest`: 8 tests passed
- `ProtocolPublicModelsCodecTest`: 12 tests passed
- `ProtocolRoomModelsCodecTest`: 5 tests passed
- `ProtocolSerializationTest`: 8 tests passed
- `ProtocolTileMarkContractTest`: 11 tests passed
- `FragmenterTest`: 5 tests passed
- `HeartbeatPacerTest`: 4 tests passed
- `PushRetransmitWindowTest`: 5 tests passed
- `PushSequencerTest`: 6 tests passed
- `ReliableRequestorTest`: 5 tests passed
- `ReplayGuardTest`: 4 tests passed
- `UdpFrameCodecTest`: 12 tests passed

**全量汇总：16 Suites, 151 Tests, 0 Failures, 0 Errors, 0 Skipped.**

---

## 4. 交付文件列表

1. `protocol/src/main/kotlin/com/majiang/transport/api/ProtocolModels.kt` - `ProtocolPlayerSnapshot` 构造中 `DISCARD` 必须具备非空一致 `roundId`
2. `protocol/src/main/kotlin/com/majiang/transport/api/ApiMessage.kt` - `ProtocolPlayerSnapshotAdapter` 解码校验中 `DISCARD` 必须具备非空一致 `roundId`
3. `doc/tile-mark/DELIVERY-ANTI-A1FIX1.md` - 本交付报告
4. `doc/tile-mark/CALLBACK-ANTI-A1FIX1.json` - 回调通知元数据文件
