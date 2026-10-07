# Delivery Report: R2FIX1

## 1. Summary of Changes

In accordance with [TASK-ANTI-R2FIX1.md](file:///D:/majiang/doc/v4-migration/TASK-ANTI-R2FIX1.md) and [FEEDBACK-R2FIX1.md](file:///D:/majiang/doc/v4-migration/FEEDBACK-R2FIX1.md), all compilation issues identified by Codex in `build/v4-migration/R2-delivery-compile.log` and `build/v4-migration/R2FIX1-interim-compile.log` have been resolved.

### Production Fixes
1. **`protocol/.../ApiMessage.kt`**:
   - Corrected `ProtocolLegalAction.StartStacking` deserialization to consume `stackingPlans: List<String>` without `player`.
   - Corrected `ProtocolLegalAction.DrawTile` deserialization to pass only `player: Int`, removing the invented `tile` parameter.
2. **`protocol/.../ProtocolModels.kt`**:
   - Removed secondary compatibility constructor in `ProtocolMeld`. Only the canonical constructor `(type, tiles, fromPlayer = 0, calledTile = null)` remains.
3. **`client/.../ProtocolViewMapper.kt`**:
   - Added `mapRoomSummary(room: RoomSummary): RoomSummaryView`, `toProtocolRoomSummary(view: RoomSummaryView): RoomSummary`, and `mapRoomSummaries(rooms: List<RoomSummary>): List<RoomSummaryView>` with exact 1-to-1 field mappings (`roomId`, `roomName`, `hostPlayerName`, `playerCount`, `maxPlayers`, `matchStarted`, `configSummary`).

### Test Call-Site Fixes
4. **`protocol/.../ApiDtoValidationTest.kt`**:
   - Replaced engine `MatchConfig` with `ProtocolMatchConfig`.
   - Replaced obsolete `binding.deepCopy()` with `binding.copy(snapshot = binding.snapshot.copy())`, maintaining snapshot isolation assertions without introducing unnecessary compatibility methods.
5. **`protocol/.../FakeServerConnectionSmokeTest.kt`**:
   - Replaced old domain imports (`ActionChoice`, `ShufflePlanRequest`, `PlayerUpdate`, `ReceiptStatus`, `ShuffleSelection`) with protocol models (`ProtocolActionChoice`, `ProtocolShufflePlan`, `ProtocolPlayerUpdate`, `ProtocolReceiptStatus`, `ProtocolShuffleSelection`).
   - Replaced obsolete `requestControl` call with `claimControl()` and `releaseControl()`, asserting `ProtocolReceiptStatus.APPLIED` and recording status `conn.lastClaimed`.
6. **`protocol/.../ProtocolSerializationTest.kt`**:
   - Added missing import `com.majiang.server.api.FakeServerConnection`.
   - Typed cheat technique parameters with `ProtocolParamValue.StringValue("0")`.
   - Replaced obsolete `safeStartingScores` and `safeInitialTargets` references with public defensive properties `startingScores` and `initialTargets`.
7. **`client/.../ProtocolViewMapperTest.kt`**:
   - Added unit test coverage for `mapRoomSummary`, `toProtocolRoomSummary`, and `mapRoomSummaries`.

---

## 2. Modified Files

- [`protocol/src/main/kotlin/com/majiang/transport/api/ApiMessage.kt`](file:///D:/majiang/protocol/src/main/kotlin/com/majiang/transport/api/ApiMessage.kt)
- [`protocol/src/main/kotlin/com/majiang/transport/api/ProtocolModels.kt`](file:///D:/majiang/protocol/src/main/kotlin/com/majiang/transport/api/ProtocolModels.kt)
- [`client/src/main/kotlin/com/majiang/client/api/ProtocolViewMapper.kt`](file:///D:/majiang/client/src/main/kotlin/com/majiang/client/api/ProtocolViewMapper.kt)
- [`client/src/test/kotlin/com/majiang/client/api/ProtocolViewMapperTest.kt`](file:///D:/majiang/client/src/test/kotlin/com/majiang/client/api/ProtocolViewMapperTest.kt)
- [`protocol/src/test/kotlin/com/majiang/server/api/ApiDtoValidationTest.kt`](file:///D:/majiang/protocol/src/test/kotlin/com/majiang/server/api/ApiDtoValidationTest.kt)
- [`protocol/src/test/kotlin/com/majiang/server/api/FakeServerConnectionSmokeTest.kt`](file:///D:/majiang/protocol/src/test/kotlin/com/majiang/server/api/FakeServerConnectionSmokeTest.kt)
- [`protocol/src/test/kotlin/com/majiang/transport/api/ProtocolSerializationTest.kt`](file:///D:/majiang/protocol/src/test/kotlin/com/majiang/transport/api/ProtocolSerializationTest.kt)

---

## 3. Explicit Statement on Tests

**Tests and Gradle compilation builds were NOT run by Antigravity (Anti).**
In compliance with the project operating guidelines, Anti used built-in file inspection, editing, and authoring tools only. All builds, test runs, and verification are delegated exclusively to Codex.

---

## 4. Unresolved Items / Pending Verification

- Verification of full test execution and compilation across `:protocol`, `:server`, and `:client` remains with Codex.
- Review-owned tests under `tools/architecture/v4-review` were not modified.
- Deeper codec/immutability/model items from [FEEDBACK-R2.md](file:///D:/majiang/doc/v4-migration/FEEDBACK-R2.md) remain tracked for subsequent review stages.
