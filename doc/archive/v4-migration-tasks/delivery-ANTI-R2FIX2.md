# Delivery Report: R2FIX2

## 1. Summary of Changes

In accordance with [TASK-ANTI-R2FIX2.md](file:///D:/majiang/doc/v4-migration/TASK-ANTI-R2FIX2.md) and [FEEDBACK-R2FIX2.md](file:///D:/majiang/doc/v4-migration/FEEDBACK-R2FIX2.md), all identified wire-boundary, behavioral, and schema-fidelity issues have been resolved across production files without introducing V3 compatibility shims or touching existing test files.

### 1. Request Envelope Pre-validation & Parse Exception Integrity
- **`protocol/.../ApiMessage.kt`**:
  - In `decodeRequest`: Added pre-validation of `rootObj.get("api")` and `rootObj.get("requestId")` to strictly ensure both exist as non-blank string primitives prior to Gson unsafe reflection allocation, eliminating `NullPointerException` and cleanly throwing `JsonParseException("missing or blank api")` and `JsonParseException("missing or blank requestId")`.
  - Added strict numeric validation for `protocolVersion`, `preferSeat`, and `cursor` (rejecting fractional numbers, quoted strings, and overflow values).
  - Ensured `JsonParseException` is rethrown directly in `decodeRequest` and `decodeResponse` without extraneous message-wrapping prefixes.
  - Added `ApiResponseAdapter` to ensure incoming `ApiResponse` objects strictly validate integer fields (`seat`) and wrap all nested collections (`rooms`, `events`) into unmodifiable defensive lists.

### 2. Legitimate Concealed Kan & Meld Boundaries
- **`protocol/.../ProtocolModels.kt`**:
  - Updated `ProtocolMeld.init` validation: `type == ProtocolMeldType.CLOSED_KAN` permits `fromPlayer in -1..3` (preserving the domain model's legitimate absent source state for concealed kan), while all other meld types strictly require `fromPlayer in 0..3`.
- **`protocol/.../ApiMessage.kt`**:
  - Updated `ProtocolMeldAdapter` to strictly validate required fields (`type`, `tiles`, `fromPlayer`), reject fractional numbers, and validate `fromPlayer` boundaries accordingly (`-1..3` for `CLOSED_KAN`, `0..3` otherwise).

### 3. Strict Numeric Validation & Model Boundary Checks
- **`protocol/.../ApiMessage.kt`**:
  - `validateControlEpochElement`: Enforced primitive number kind (`isJsonPrimitive && isNumber`), strictly rejecting quoted string numbers (e.g. `"1"`), floating/scientific notation (e.g. `1.25`, `1e3`), and negative values.
  - `ProtocolTileAdapter`: Registered custom serializer and deserializer validating `tileId in 0..135`, rejecting non-integers, and cross-checking that `suit` and `rank` (when present in payload) do not contradict the canonical physical tile identity, otherwise throwing `JsonParseException`.

### 4. Recursive Immutable DTOs & Boundary Protection
- **`protocol/.../ProtocolModels.kt`**:
  - Ensured all collection properties across DTOs (`ProtocolShuffleSelection`, `ProtocolCheatEconomyConfig`, `ProtocolMatchConfig`, `ProtocolDoraIndicatorHit`, `ProtocolDoraBreakdown`, `ProtocolScoreBreakdown`, `ProtocolPublicRoundRecord`, `ProtocolPublicSessionResult`, `ProtocolSessionView`, `ProtocolPlayerView`, `ProtocolPlayerSnapshot`, `ProtocolPlayerUpdate`) defensively wrap collections in `Collections.unmodifiableList`, `unmodifiableSet`, and `unmodifiableMap`.
  - Removed `toRaw(): Any` and `fromRaw(Any)` from `ProtocolParamValue` to prevent exposing domain/Any conversion logic in protocol models; added strongly-typed accessors (`asInt()`, `asLong()`, `asDouble()`, `asString()`, `asBoolean()`).
- **`server/.../EngineProtocolMapper.kt`**:
  - Enforced strict boundary check for `StartStacking`: both `toProtocolAction(GameAction.StartStacking)` and `toEngineAction(ProtocolLegalAction.StartStacking)` strictly require `stackingPlans.isEmpty()`, preventing unsupported legacy plans from bypassing or escaping as string representations.
  - Implemented server-owned `toProtocolParamValue(raw: Any?): ProtocolParamValue` and `toEngineParamValue(param: ProtocolParamValue): Any` with strict type checking (supporting `Int`, `Long`, `Double`, `Float`, `Short`, `Byte`, `String`, `Boolean`) and explicitly throwing `IllegalArgumentException` on unsupported types rather than falling back to loose stringification.

### 5. Client Facade Type Safety & Settlement Projection
- **`client/.../ViewModels.kt`**:
  - Introduced client-owned `ClientParamValue` sealed class (`IntValue`, `LongValue`, `DoubleValue`, `StringValue`, `BooleanValue`) with strongly-typed accessors and equality support against boxed primitives.
  - Updated `LegalActionView.UseCheatTechnique.params` to `Map<String, ClientParamValue> = emptyMap()`, providing complete end-to-end type safety without exposing protocol or engine types.
- **`client/.../ProtocolViewMapper.kt`**:
  - Implemented `mapParamValue` and `toProtocolParamValue` bidirectional mappings.
  - Updated `mapLegalAction` for `UseCheatTechnique` to map directly to `ClientParamValue`.
  - Added `toProtocolLegalAction(LegalActionView)` with empty `stackingPlans` enforcement and typed parameter mapping.
  - Implemented `projectSettlementDetail(events: List<ProtocolGameEvent>): SettlementDetailView?` to project terminal settlement details from protocol events (`TsumoWin`, `RonWin`, `DrawGame`, `ScoreSettled`), populating title (`"TSUMO WIN!"`, `"RON WIN!"`, `"DRAW GAME"`), winner/loser seats, win tile, yaku list, han/fu/score, payments, and breakdown (fixing the hardcoded `settlementDetail = null`).
  - Ensured all collection properties published to client views (`PlayerView`, `GameSnapshotView`, `SessionProgressView`, `PublicRoundRecordView`, `PublicSessionResultView`) are wrapped in `Collections.unmodifiableList` / `Collections.unmodifiableMap`.

---

## 2. Modified Files

1. [`protocol/src/main/kotlin/com/majiang/transport/api/ProtocolModels.kt`](file:///D:/majiang/protocol/src/main/kotlin/com/majiang/transport/api/ProtocolModels.kt)
2. [`protocol/src/main/kotlin/com/majiang/transport/api/ApiMessage.kt`](file:///D:/majiang/protocol/src/main/kotlin/com/majiang/transport/api/ApiMessage.kt)
3. [`server/src/main/kotlin/com/majiang/server/mapping/EngineProtocolMapper.kt`](file:///D:/majiang/server/src/main/kotlin/com/majiang/server/mapping/EngineProtocolMapper.kt)
4. [`client/src/main/kotlin/com/majiang/client/api/ViewModels.kt`](file:///D:/majiang/client/src/main/kotlin/com/majiang/client/api/ViewModels.kt)
5. [`client/src/main/kotlin/com/majiang/client/api/ProtocolViewMapper.kt`](file:///D:/majiang/client/src/main/kotlin/com/majiang/client/api/ProtocolViewMapper.kt)

---

## 3. Explicit Statement on Tests

**Tests and Gradle compilation builds were NOT run by Antigravity (Anti).**
In strict adherence to the project instructions, Anti used built-in file inspection, editing, and authoring tools only. Anti did not invoke shell commands, RunCommand, background tasks, test execution, or build tasks. All builds, test runs, test adjustments (e.g. test parameter unwrapping in existing test files), and acceptance verifications are delegated to Codex.

---

## 4. Unresolved Items / Pending Verification

- Verification of full module test execution across `:protocol`, `:server`, and `:client` remains with Codex.
- Codex will adjust `testGameActionAllVariantsMapping` to unwrap or assert against `ClientParamValue` as noted in `FEEDBACK-R2FIX2.md`.
- Review-owned tests under `tools/architecture/v4-review` were not touched.
