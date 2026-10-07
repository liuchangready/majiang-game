# 任务 GLM-P 交付报告：网络协议编解码（N2）

- **交付日期**：2026-10-01
- **工作区**：`D:/majiang`
- **执行角色**：GLM-P（Claude Code + GLM 模型，生产开发）
- **任务定义**：[TASK-GLM-P.md](TASK-GLM-P.md)（实现 [TASK-N2.md](TASK-N2.md)）
- **验收方**：Codex（测试设计、编译构建与端到端验证）

---

## 1. 所有权边界

本次交付**只写入**以下路径，未触碰 engine、server、network 传输层、UI、Gradle 与任何测试文件：

| 路径 | 变更 |
|---|---|
| `client/src/main/kotlin/com/majiang/network/protocol/NetworkMessages.kt` | 修改：`NetworkRequest` 增加 `protocolVersion` 字段与常量/白名单 |
| `client/src/main/kotlin/com/majiang/network/protocol/ProtocolJson.kt` | 重写：修复编译错误、结算链无损编解码、严格读取校验 |
| `doc/v3-migration/delivery-GLM-P.md` | 新增：本报告 |

`NetworkFrameCodec.kt`（长度前缀帧 + 4MB 上限）无需改动，保持原样。

## 2. 修复的静态缺陷（对照 TASK-N2）

1. **编译错误 `DecisionOption`**：实际声明在 `engine/.../application/ActionDecisionDto.kt` 中为 `ActionOption(optionId, action)`，已改为真实类型（import 与 `PlayerDecisionAdapter` 构造处）。
2. **`FuritenStatus` 包名**：实际声明于 `engine/.../rules/FuritenRules.kt`（`com.majiang.rules.FuritenStatus(discard, temporary, riichi)`），视图适配器按真实构造器读写三布尔字段。
3. **事件字段名与构造顺序**：逐一对照 `engine/.../event/GameEvent.kt` 的全部 23 个密封变体（含 `CheatPointsChanged(turnNumber, seat, amount, newBalance)`、`GazeShifted(observer, newTarget, oldTarget)`、`RonWin(turnNumber, winner, loser, winTile, yakuList, han, fu, score, breakdown)` 等），无臆造 API。
4. **`StartStacking`/`DealComplete` 曾被写成 `"UNSUPPORTED"`**：现在 `GameAction` 全部 14 个变体均有固定标签往返（`START_STACKING`/`DEAL_COMPLETE` 等），`when` 为穷尽分支；非空 `stackingPlans`（`List<Any>`，非白名单类型）在读写两侧均显式抛 `JsonParseException` 拒绝，绝不静默丢弃或降级。
5. **`UseCheatTechnique.params`（`Map<String, Any>`）曾被整体丢弃**：新增显式标签编码（`s/i/l/b/d/f/t/list`，仅 String/Int/Long/Boolean/Double/Float/Tile/列表），未知类型/未知标签/多标签/空标签均拒绝，读写对称无损。
6. **`TsumoWin`/`RonWin` 曾完全丢弃 `ScoreBreakdown`**：新增四个完整适配器并接入事件适配器（见 §3）。
7. **读取侧捏造默认值**：删除 `safeScores=25000`、`PlayerDecision("initial")` 伪造回退、`requestId` 空则随机 UUID 等行为；改为必填字段存在性校验（`required(...)`），缺失即抛 `JsonParseException`。

## 3. 结算链无损编解码（新增适配器）

对照实际声明逐字段实现（字段顺序与默认值均以源码为准）：

- **`ScoreCalculator.ScoreResult`**（rules/ScoreCalculator.kt）：全部 9 字段（`fu/han/basePoints/title/isYakuman/yakumanMultiplier/rawBasePoints/capReason/formulaText/rawBasePointsExact`）逐一读写，含两个派生默认字段（`rawBasePoints`、`rawBasePointsExact`）。
- **`FuLine` / `FuBreakdown`**（rules/FuCalculator.kt）：`lines(description, fu)`、`subtotal`、`roundedUpTo`、`finalFu`、`specialNote?`。
- **`DoraIndicatorHit` / `DoraBreakdown`**（rules/WinningScoreEvaluator.kt）：`indicatorCount/uraCount/redCount/totalHan` + 三个命中物理牌列表 + `indicatorHits`/`uraHits`（每项 `indicator/target/tiles`，物理 `copyIndex` 全保留）。
- **`ScoreBreakdown`**：`yakuLines`（`(役名,翻数)` 对编码为 `{name,han}`）、`yakumanCount`、`dora`、`fu`、`score`、`paymentLines`、`honbaAddition`、`riichiAddition`、`totalPoints`、`uraEligible`、`isDealer`、`isTsumo`。

`TsumoWin`/`RonWin` 的 `breakdown` 现在始终写出（可空为 null），读取缺失时按构造器默认 null 还原；`ScoreSettled.newScores`/`MatchEnded.finalScores` 解码为**新分配的 `IntArray`**（`toIntArray()`），与原数组引用独立且内容相等；`payments` 键为座位整数字符串，非法键拒绝。

## 4. 协议版本与请求严格校验

`NetworkRequest` 新增（追加在参数表尾部，全部既有调用点均为具名参数，外部 API 兼容）：

- `protocolVersion: Int = PROTOCOL_VERSION`（`PROTOCOL_VERSION = 3`）；
- `KNOWN_TYPES` 白名单（14 个请求类型常量）与 `NetworkResponse.KNOWN_TYPES`（4 个响应/推送类型）。

`ProtocolJson.parseRequest/parseResponse` 改为**严格解码管线**（`decodeStrict`）：非 lenient `JsonReader`（拒绝非标准字面量与尾部垃圾；`toJson` 输出均为标准 JSON，含 `\uXXXX` 转义，严格模式可读）+ 读后 `END_DOCUMENT` 断言。请求读取校验顺序：

1. `protocolVersion` 必填且必须等于 3（缺失/`999` 等一律拒绝）；
2. `type` 必填且必须在 `KNOWN_TYPES` 白名单内；
3. `requestId` 必填、非空白、≤128；
4. 可选字段边界：`roomId`≤64、`token`≤256、`playerName`≤64、`roundId`≤128（非空白）、`cursor >= 0`；
5. 按类型必填：`CREATE_ROOM→playerName`；`JOIN_ROOM→roomId+playerName`；`RESUME_SESSION→roomId+token`；`SUBMIT_CHOICE→choice`（内部 `requestId/decisionId/optionId` 非空白≤128）；`RESUME_CURSOR→cursor`；`REQUEST_CONTROL→takeover`；`QUERY_ROUND_RESULT→roundId`；其余（`LIST_ROOMS/PING/GET_*`/`START_MATCH/NEXT_ROUND`）会话身份由连接层绑定，无额外必填字段；
6. 请求对象内的未知字段一律拒绝（客户端无法借字段走私原始 `GameAction`/`GameState`——DTO 本身也无此类字段）。

服务器侧 `ClientConnectionHandler.readLoop` 已有 try/catch 将解析异常转为 `MALFORMED_REQUEST` 错误响应，严格化抛错不会断开合法连接。

## 5. 读取侧统一严格化（响应与嵌套 DTO）

- **类型不符即拒绝**：`stringField/intField/longField/doubleField/booleanField/enumField` 先校验 JSON token 类型再取值（lenient 模式下的字符串↔数字强转不再可能）；
- **未知字段即拒绝**：所有 DTO 的 `else -> rejectUnknown(...)`，替代旧 `skipValue()` 静默跳过；
- **必填存在性**：所有无默认值字段（如 `PlayerView` 18 项状态、`PlayerSnapshot` 标识/游标/视图/决策、事件各必填字段）缺失即拒绝；有默认值字段（如 `isRiichiDiscard=false`、`wallSize=136`、`breakdown=null`、`stackingPlans=[]`）缺失按构造器默认还原，与写侧全量输出保持无损往返；
- **集合内 null 元素拒绝**（牌列表/副露/事件/房间等），不再静默过滤；
- **解码态范围校验**：`Tile`（rank/copyIndex 由构造器）、`PlayerView`（viewerSeat 0..3、四元列表长度，经 `PlayerView.create` 构造器）、`ClockView`/`PlayerSnapshot`/`PlayerUpdate`/`ActionReceipt`/`AccessCapabilities`（构造器不变量）、`Meld.fromPlayer ∈ -1..3` 且 `tiles` 非空、`NetworkResponse.seat ∈ 0..3`、`RoomSummary.maxPlayers ∈ 1..4` 且 `playerCount ∈ 0..maxPlayers`、`MatchConfig`（四人制、`handsPerWind 1..4`、风 1..4、四份非负起始分/出千点、`tieBreakSeats` 为 0..3 的排列、`targetScore >= 0`）；
- **`CheatEconomyConfig.costs` 不再丢失**：`MatchConfig` 线格式补充 `cheatCosts` 对象，读写对称（旧实现完全丢弃该映射）。

## 6. 保持不变的外部契约

- `NetworkRequest`/`NetworkResponse` 全部既有字段与类型、各 `TYPE_*` 常量、`toString` 的 token 脱敏（`token=***`/`resumeToken=***`）不变；
- `ProtocolJson.toJson/fromJson/parseRequest/parseResponse` 签名不变；`gson` 属性仍注册全部白名单适配器（新增 5 个：`FuBreakdown`、`FuLine`（经内嵌实例）、`ScoreResult`、`DoraBreakdown`、`ScoreBreakdown`）；
- `NetworkFrameCodec` 未改动。

## 7. 已知边界与说明（如实声明）

1. **未执行任何编译或测试**：本任务限定 Read/Edit/Write/Glob/Grep 工具，构建与测试由 Codex 执行（`./gradlew :client:test` 等）。上述内容均为对照实际源码的静态实现，不构成“编译/测试通过”的证据。
2. `GameAction.StartStacking` 的非空 `stackingPlans`（`List<Any>`）被设计性拒绝：当前引擎仅构造空计划（`ApplicationCoordinator` 的 `opt-continue`），且 `RoundRuleService` 对非空计划返回 `UNSUPPORTED_ACTION`；若未来需要传输码牌计划，应先在 engine 定义可白名单化的具体 DTO。
3. `PlayerSnapshot`/`PlayerView` 等嵌套读取现为必填校验：线上双方都使用同一 `ProtocolJson` 产出（写侧始终全量输出），不存在旧格式兼容负载；`gson.fromJson(String, Class)` 泛型入口保持 Gson 宽松语义未被内部使用。
4. 服务端写出的错误串（`MALFORMED_REQUEST: ...`）读取侧上限 512 字符；当前服务器错误码均为短常量，不受影响。
5. `parseResponse` 亦走严格管线（响应类型白名单 + 未知字段拒绝）：两端同版本编解码，`protocolVersion` 协商由请求侧强制（服务端能力声明中的 `protocolVersion` 为信息性字段，由 `AccessCapabilities` 构造器保证 ≥1）。

## 8. 建议的 Codex 验收点

对应 `client/src/test/kotlin/com/majiang/v3/NetworkCodecAcceptanceTest.kt`（已通读、未修改）：

1. 结算事件往返：`TsumoWin/RonWin.breakdown` 全量相等、`ScoreSettled.newScores` 内容相等且引用独立、`CheatPointsChanged` 相等；
2. 快照往返：物理选项 ID、`FuritenStatus`、时钟（含负单调原点）、出千点数余额逐项相等；
3. 请求校验：合法 `LIST_ROOMS` 含 `protocolVersion` 且可回读；`{}`、缺 `protocolVersion`、`protocolVersion:999`、`SUBMIT_CHOICE` 缺 `choice`、`JOIN_ROOM` 空 `roomId`、空白 `requestId`、尾部垃圾七类全部抛异常；
4. 回环（配合 N3）：真实 TCP 帧往返 + 服务端 `MALFORMED_REQUEST` 错误响应路径不崩溃。
