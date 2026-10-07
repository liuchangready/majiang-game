# R2FIX4 交付说明 (delivery-ANTI-R2FIX4.md)

- **开发角色**：Antigravity (Anti)
- **验收角色**：Codex
- **交付日期**：2026-10-05
- **测试执行状态**：**测试未由 Anti 运行**（严格遵循约束：仅使用文件读写工具，不执行 Gradle、构建、Shell、终端或独立审查用例）。

---

## 1. 修改文件清单

1. [`protocol/src/main/kotlin/com/majiang/transport/api/ProtocolModels.kt`](file:///D:/majiang/protocol/src/main/kotlin/com/majiang/transport/api/ProtocolModels.kt)
2. [`protocol/src/main/kotlin/com/majiang/transport/api/ApiMessage.kt`](file:///D:/majiang/protocol/src/main/kotlin/com/majiang/transport/api/ApiMessage.kt)
3. [`protocol/src/test/kotlin/com/majiang/transport/api/ProtocolModelsTest.kt`](file:///D:/majiang/protocol/src/test/kotlin/com/majiang/transport/api/ProtocolModelsTest.kt)

---

## 2. 具体修复内容

### 2.1 ProtocolLegalAction 必填 seat/player 身份校验与默认值消除
- **`ProtocolModels.kt`**：移除了 `data class Pass(val player: Int = 0)` 中遗留的 `= 0` 默认值。现在所有 12 种涉及玩家座位的动作类（`DrawTile`、`DiscardTile`、`DeclareChi`、`DeclarePon`、`DeclareKan`、`DeclareRiichi`、`DeclareTsumo`、`DeclareRon`、`UseCheatTechnique`、`Pass`、`DeclareAbortiveDraw`、`DeclareAgariYame`）均要求在构造与序列化反序列化中显式提供 `player: Int`，并保留 `player in 0..3` 的断言校验。
- **`ApiMessage.kt` (`ProtocolLegalActionAdapter`)**：对所有 12 种动作类型反序列化消除静默 `?: 0` 回退，严格要求 JSON 提供数值型 `player` 并校验 `0..3`；对 `DeclareKan.kanType` 与 `UseCheatTechnique.technique` 增加严格枚举类型校验。

### 2.2 ProtocolGameEvent turnNumber 与各事件字段语义对齐
- **`ApiMessage.kt` (`ProtocolGameEventAdapter`)**：
  - 仅对定义了构造默认值的 3 个事件（`StackingStarted`、`WallBuilt`、`DealingComplete`）允许在缺失时默认 `turnNumber = 0`。
  - 其余 20 种事件（`TileDealt`、`TileDrawn`、`TileDiscarded`、`ChiDeclared`、`PonDeclared`、`KanDeclared`、`RinshanDraw`、`RiichiDeclared`、`TsumoWin`、`RonWin`、`DoraRevealed`、`CheatAttempted`、`SuspicionChanged`、`BustedEvent`、`GazeShifted`、`CheatPointsChanged`、`DrawGame`、`ScoreSettled`、`RoundEnded`、`MatchEnded`）必须在 JSON 中提供显式 `turnNumber`，否则抛出 `JsonParseException`。
  - 严格校验各事件的玩家席位必须在 `0..3`，非默认列表（如 `TsumoWin.yakuList`、`RonWin.yakuList`、`DrawGame.tenpaiPlayers`、`ScoreSettled.newScores`、`MatchEnded.finalScores`）必须存在并校验元素范围。

### 2.3 嵌套复合/结算 DTO 必须字段与防御性不可变强化
- **`ProtocolFuBreakdownAdapter`**：严格要求 `lines`、`subtotal`、`roundedUpTo`、`finalFu` 均不可缺省。
- **`ProtocolDoraIndicatorHitAdapter`**：严格要求 `indicator`、`target`、`tiles` 必须存在且不可变。
- **`ProtocolDoraBreakdownAdapter`**：严格要求 `indicatorCount`、`uraCount`、`redCount`、`totalHan`、`indicatorTiles`、`uraTiles`、`redTiles`、`indicatorHits`、`uraHits` 全部必填并返回防御性快照。
- **`ProtocolScoreBreakdownAdapter`**：严格要求全部 12 个字段，`yakuLines` 采用规范规范对象结构 `{first: String, second: Int}` 反序列化并校验不可变。
- **`ProtocolPublicRoundRecordAdapter`**：严格要求 `roundId`（非空白）、`resultId`（非空白）、`finalScores`、`deltas`、`finalCheatPoints`、`cheatDeltas`。
- **`ProtocolPublicSessionResultAdapter`**：严格要求 `finalScores`、`rankedSeats`、`reason`（非空白）。
- **`ProtocolSessionViewAdapter`**：严格要求 `lifecycle`（非空白）、`scores`（长度为 4）、`roundWind in 1..4`、`roundNumber >= 1`、`dealerSeat in 0..3`、`honba >= 0`。
- **`ProtocolPlayerDecisionAdapter`**：严格要求 `decisionId`（非空白）、`controlEpoch >= 0L`，且 `roundId` 若存在时非空白。

### 2.4 新增完整标量与契约 DTO Adapter 及 Gson 注册
按照审查审计清单，针对此前依赖 Gson Unsafe 反射反序列化绕过构造断言的模型新增了专用 `JsonDeserializer` 并统一注册至 `ApiMessageCodec.gson`：
1. **`ProtocolClockAdapter`**：校验未知字段、`seat in 0..3`、`opensAt`、`deadline >= opensAt`（严格整数/长整数，拒绝小数 deadline）、`reserve >= 0L`、`serverNow`。
2. **`ProtocolSeatControlAdapter`**：校验未知字段、`seat in 0..3`、`connected`、`role in ProtocolSeatControl.VALID_ROLES`（拒绝 `"INVALID"` 等非法角色）、`controlEpoch >= 0L`。
3. **`ProtocolScoreResultAdapter`**：校验未知字段（拒绝如 `"unexpected": 1` 等未知字段）、必填 `fu`、`han`、`basePoints`、`title`、`isYakuman`、`yakumanMultiplier`。
4. **`ProtocolFuLineAdapter`**：校验未知字段、`description`（非空白）、`fu`。
5. **`ProtocolFuritenStatusAdapter`**：校验未知字段与严格布尔值。
6. **`ProtocolActionReceiptAdapter`**：校验未知字段、`requestId`（非空白）、`status`（有效枚举）、`cursor >= 0L`。
7. **`ProtocolAccessCapabilitiesAdapter`**：校验未知字段、`protocolVersion >= 1`、`supportedPlayerCounts`（非空集合）。
8. **`ProtocolActionOptionAdapter`**：校验未知字段、`optionId`（非空白）、合法动作解析。
9. **`ProtocolShufflePreparationAdapter`**：校验未知字段、`roundId`（非空白）、`status`（有效枚举）、时钟时间戳与点数有效性。
10. **`RoomSummaryAdapter`**：校验未知字段、`roomId`/`roomName`/`hostPlayerName` 非空白、`maxPlayers >= 1`、`playerCount in 1..maxPlayers`、`matchStarted`。
11. **`RoomBindingAdapter`**：校验未知字段、`roomId`（非空白）、`seat in 0..3`、`resumeToken`（非空白）、`snapshot` 完整性。
12. **`CreateRoomRequestAdapter`**：校验未知字段、`playerName`（非空白）、`protocolVersion >= 1`。
13. **`JoinRoomRequestAdapter`**：校验未知字段、`roomId`（非空白）、`preferSeat in 0..3`。
14. **`ResumeRequestAdapter`**：校验未知字段、`roomId`（非空白）、`token`（非空白）、`cursor >= 0L`。
15. **`ApiRequestAdapter`**：校验未知 JSON 字段、保留未知 API TAG（用于上层分发器按需拒绝或处理）、严格校验 `requestId`（非空白）、`protocolVersion >= 1`。
16. **`ApiResponseAdapter`**：校验未知字段、**严格要求 `requestId`（非空白）**（修复 `decodeResponse("""{"success":true}""")` 接受缺失 `requestId` 的问题）。

### 2.5 模块级单元测试补充
- **`ProtocolModelsTest.kt`**：新增 8 组针对上述反序列化校验边界的单元测试：
  - `legal actions require explicit player identity`
  - `response envelope requires request identity`
  - `nested control rejects unsupported role`
  - `scalar score rejects unknown fields`
  - `clock rejects fractional deadline and negative reserve`
  - `fu breakdown requires mandatory fields`
  - `game event requires turn number for turn-based events`
  - `room summary validates player count range`

---

## 3. 未解决项 (Unresolved Items)

- **无**。所有在 `TASK-ANTI-R2FIX4.md` 及 `FEEDBACK-R2FIX4.md` 中指出的 4 项具体探针失败和未注册的 DTO 适配器均已完全实现，并遵循构造函数不变量与不可变集合原则。

---

## 4. 验证说明

- **Anti 未运行构建或测试**。请由 Codex 进行独立的编译、回归测试（`tools/architecture/v4-review/` 及全量模块测试）与验收。
