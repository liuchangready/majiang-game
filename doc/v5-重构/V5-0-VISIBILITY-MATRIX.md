# V5-0 可见性矩阵（预冻结）

日期：2026-10-23
状态：**P0.2 可见性预冻结，供 G0/Codex 复核；不是 V5-1 验收。**
基线：`v5` 分支，`48125ff`；V4 仍是生产基线。
范围：为 `rulesetVersion=1` 的 19 类事件、玩家快照字段、公共快照字段、观战与日志列出可见性；新增信息默认私有。
原则：**服务端按流投影，绝不“先序列化完整状态再让客户端删字段”**；观战只消费公开流；日志不是第二个客户端，不能成为暗牌/种子/私有出千流水的旁路。

> 证据标记：`路径:行号` / `路径#符号`。V4 没有统一的逐事件“游戏日志”实现，因此“日志”列是 **V5 新约定**，不是 V4 已有能力；V4 的服务器日志只作为反例/边界参考。

---

## 0. 结论与图例

### 0.1 三条流

| 流 | 身份 | 内容 | 证据 |
|---|---|---|---|
| 玩家流 `PlayerStream(seat)` | `seat=0..3`，一人一条 | 该座位的 `PlayerSnapshot` + 该座位可见事件 | `ServerTableHost.kt:391-397`；`TableAuthority.querySnapshot` |
| 观战流 `PublicStream` | 独立身份，不属于任何座位 | `ProtocolPublicSnapshot` + 公开白名单事件 | `ServerTableHost.kt:395-397,2132-2173`；`PublicProjectionFactory` |
| 日志/审计 | 运维/审计，不是玩家 | 生产日志只允许元数据；私有数据只能进受限加密审计存储 | 方案 §10.1；本文 §7 |

### 0.2 图例

- `公`：P0–P3 四席都收到完整公开字段。
- `自`：只有事件 `owner/seat` 对应的那一席收到；其他三席**不收到该事件**（不是收到后遮字段）。
- `公*`：V4 实际四席都收到，但与“私有”目标冲突；V5 目标标在备注/§8。
- `表公`：表宝牌公开；`里授`：里宝仅在结算授权批次公开。
- `—`：不投递/不记录。
- `元`：日志只记录元数据（事件类型、turn、seat、state_version/event_seq、错误码、耗时），以及公开结算的数值（若有）；不记录牌面/手牌/牌山/种子/余额。
- `结算公`：只在和牌/流局结算批次公开；进行中不公开。

### 0.3 默认规则

1. 新增字段/事件默认**私有**；要公开必须显式加入白名单并补测试。
2. 公开流与玩家流使用不同的投影入口；公共快照不得复用玩家快照再裁剪。
3. 观战不含：暗牌、摸牌、岭上牌、袖牌、牌山顺序、种子、里宝实体、决策/候选、出千点数/流水、offer/预留、控制代次细节。
4. 日志不含：上述私有字段；令牌/票据；玩家私有出千原因。
5. 每个流有独立 `streamVersion`/`projectionSeq`/基线；`streamVersion` 可滞后/重复，`projectionSeq` 每次发射 +1；旧 `bindingEpoch`/`syncEpoch`/`streamVersion`/`projectionSeq` 的快照必须丢弃（V5 语义见协议文档）。

---

## 1. 事件可见性（19 类）

> 事件字段见 `engine/.../event/GameEvent.kt:19-109`。玩家可见性由 `SessionUpdate.filterVisibleEvents` 实现（`engine/.../session/SessionUpdate.kt:66-93`）；公共可见性由 `PublicProjectionFactory.publicEvents` 实现（`engine/.../projection/PublicProjectionFactory.kt:109-154`），协议侧还有 `validatePublicGameEvent` 二次白名单（`protocol/.../ProtocolPublicModels.kt:422-447`）。

| # | 事件 | P0 | P1 | P2 | P3 | 观战 | 日志 | 理由/备注 |
|---:|---|---|---|---|---|---|---|---|
| 1 | `StackingStarted` | 公 | 公 | 公 | 公 | 公 | 元 | 只表示阶段开始，不含牌序 |
| 2 | `WallBuilt` | 公 | 公 | 公 | 公 | 公 | 元 | `wallSize=136` 可公开；不得带牌山顺序/种子 |
| 3 | `TileDealt` | 自 | 自 | 自 | 自 | — | 元 | 只有收牌者知道具体牌；其他席与观战不得收到 |
| 4 | `DealingComplete` | 公 | 公 | 公 | 公 | 公 | 元 | 只表示发牌完成，不含牌面 |
| 5 | `TileDrawn` | 自 | 自 | 自 | 自 | — | 元 | 摸牌牌面私有；`wallRemaining` 可公开但不要绑定具体牌 |
| 6 | `TileDiscarded` | 公 | 公 | 公 | 公 | 公 | 元 | 弃牌是公开信息；`isRiichiDiscard/isTsumogiri` 也公开 |
| 7 | `ChiDeclared` | 公 | 公 | 公 | 公 | 公 | 元 | 副露牌面公开 |
| 8 | `PonDeclared` | 公 | 公 | 公 | 公 | 公 | 元 | 同上 |
| 9 | `KanDeclared` | 公 | 公 | 公 | 公 | 公 | 元 | 杠类型/牌面公开；杠后宝牌另行 `DoraRevealed` |
| 10 | `RinshanDraw` | 自 | 自 | 自 | 自 | — | 元 | 岭上牌面私有；观战只知道“发生了岭上摸牌”也不应给牌面 |
| 11 | `RiichiDeclared` | 公 | 公 | 公 | 公 | 公 | 元 | 立直宣言与弃牌公开；不公开待牌/手牌 |
| 12 | `TsumoWin` | 公 | 公 | 公 | 公 | 结算公 | 元 | 和牌牌、役、番/符/点数公开；**不包含完整手牌**；`breakdown` 里宝实体按 §5 授权 |
| 13 | `RonWin` | 公 | 公 | 公 | 公 | 结算公 | 元 | 同上；`winner/loser` 公开 |
| 14 | `DoraRevealed` | 表公/里授 | 表公/里授 | 表公/里授 | 表公/里授 | 表公/里授 | 元 | 表宝指示牌公开；里宝 `isUraDora=true` 仅结算授权批次公开；未授权时该事件对该流不投递 |
| 15 | `CheatPointsChanged` | 公* | 公* | 公* | 公* | — | — | **V4 玩家过滤实际四席都收到**；V5 目标 `自`（仅 owner）。见 §8.1 |
| 16 | `DrawGame` | 公 | 公 | 公 | 公 | 公 | 元 | 流局原因与听牌座位公开；不公开未和手牌 |
| 17 | `ScoreSettled` | 公 | 公 | 公 | 公 | 公 | 元 | `payments/newScores` 公开；不公开出千流水 |
| 18 | `RoundEnded` | 公 | 公 | 公 | 公 | 公 | 元 | 局号/场风公开 |
| 19 | `MatchEnded` | 公 | 公 | 公 | 公 | 公 | 元 | 最终分数/胜者公开；出千总结按 §8 另行决定 |

### 1.1 私有事件逐席路由

对 `TileDealt / TileDrawn / RinshanDraw / CheatPointsChanged`（目标版本），正确行为是：

| 事件 owner | P0 | P1 | P2 | P3 | 观战 |
|---|---|---|---|---|---|
| P0 | 收到 | 不收到 | 不收到 | 不收到 | 不收到 |
| P1 | 不收到 | 收到 | 不收到 | 不收到 | 不收到 |
| P2 | 不收到 | 不收到 | 收到 | 不收到 | 不收到 |
| P3 | 不收到 | 不收到 | 不收到 | 收到 | 不收到 |

测试锚点：`SessionSnapshotContractTest.private dealt drawn and rinshan tiles only reach their owner` 对 3 个私有事件逐席断言（`SessionSnapshotContractTest.kt:63-75`）。

### 1.2 已核清的 V4 过滤行为

- `SessionUpdate.redactForViewer`：
  - `TileDealt`：仅 `player == viewerSeat`；
  - `TileDrawn`：仅 `player == viewerSeat`；
  - `RinshanDraw`：仅 `player == viewerSeat`；
  - `DoraRevealed`：`!isUraDora || isUraDoraAuthorized`；
  - 其他事件（包括 `CheatPointsChanged`）走 `else -> event`。
  - 证据：`SessionUpdate.kt:80-93`。
- `isUraDoraAuthorized` 的判定是“本批事件里出现 `TsumoWin/RonWin/ScoreSettled/RoundEnded/MatchEnded`”，因此同批结算可以授权里宝；单独 `RoundEnded` 在 `PublicProjectionFactory` 里**不能**授权（测试 `uraDoraRevealedOnlyWhenExplicitlyAuthorized` 明确 `RoundEnded` 不够，需要 `revealSettlementUra=true`）。两个入口的授权条件不完全一致，V5 应统一为“结算事件 + 显式授权”。
- 公共白名单显式丢弃 `TileDealt/TileDrawn/RinshanDraw/CheatPointsChanged`（`PublicProjectionFactory.kt:147-152`；`ProtocolPublicModels.kt:440-445`）。所以观战不会收到这四类事件。
- 玩家过滤没有丢弃 `CheatPointsChanged`，这是 §8.1 的冲突。

---

## 2. 玩家快照字段可见性

`PlayerSnapshot` 字段见 `engine/.../access/PlayerSnapshot.kt:66-87`；映射到协议见 `server/.../mapping/EngineProtocolMapper.kt:1341-1362`。

| 字段 | P0–P3 | 观战 | 日志 | 说明/遮蔽理由 |
|---|---|---|---|---|
| `sessionId` | 公 | 公 | 是 | 会话标识；非秘密 |
| `roundId` | 公 | 公 | 是 | 当前/刚结束局标识；非秘密 |
| `syncEpoch` / `streamVersion` / `projectionSeq` | 各流自有 | 自有 | 否 | `streamVersion` 是该流上次发出时的桌 `state_version`（可滞后/重复）；`projectionSeq` 每次发射 +1；不能跨席位比较 |
| `cursor`（V4） | 各流自有 | 自有 | 否 | V4 每次可见更新 +1；V5 对应物是 `projectionSeq`，不是 `streamVersion`；见协议文档 |
| `sessionVersion` | 公 | 公 | 是 | 整场版本；可用于排序，不泄露牌面 |
| `roundVersion` | 公 | 公 | 是 | 单局版本；V5 是否保留待 P2.1（不要与 `state_version` 混用） |
| `view` | 本人完整；他家仅公开字段 | 无 `view`，用 `board` | 元 | 见 §3 |
| `decision` | 仅本人 | — | — | 合法候选/optionId 是私有决策；他家不得看到 |
| `clocks` | 四席都可见 | 四席都可见 | 元 | 截止/储备是公开计时；不要带决策私货 |
| `cheatPoints` | **V4 四席余额全可见（冲突）**；V5 目标仅本人 | — | — | 出千资源是私有经济；见 §8.2 |
| `session` | 四席可见；含 `roundRecords` 的 `finalCheatPoints/cheatDeltas`（冲突） | 公开摘要，无出千字段 | 仅公开摘要 | 见 §8.2 |
| `controls` | 四席可见（role/connected/controlEpoch） | — | 元 | 连接/托管状态可给同桌；观战不需要 |
| `paused` | 公 | 公 | 是 | 全局暂停 |
| `settlementRoundId` | 公 | 公 | 是 | 刚结束局标识 |
| `settlementEvents` | 按事件过滤 | 公共白名单 | 仅公开 | 见 §1/§5 |
| `preparation` | 仅本人 | — | — | 洗牌目标/看破/预留/可用点数；私有 |
| `presentationPreview` | 仅本人 | — | — | 待牌、立直弃牌、鸣牌方案；私有 |
| `tileMark` | 仅本人 | — | — | 标记组包含实体牌/对手暗牌牌面；私有 |
| `sleeveBloom` | 仅本人 | — | — | offer/候选/预留；私有 |
| `beamSwap` | 仅本人 | — | — | 候选槽位/手牌 ID/牌面；私有 |

补充：
- `PlayerSnapshot` 的 `view` 本身是“该观察者座位”的投影，不是“四席共用对象”。V5 不得缓存一个完整 `PlayerView` 再按座位裁剪。
- `settlementEvents` 是已按座位过滤后的事件列表；不是“全量事件再由客户端隐藏”。
- `preparation`、`presentationPreview`、`tileMark`、`sleeveBloom`、`beamSwap` 在非活动/非本人时为 `null`，不是空对象占位（`PlayerSnapshot` 构造校验也要求 `pendingOffer` 与 `decisionId/controlEpoch` 一致，见 `PlayerSnapshot.kt:138-201`）。

---

## 3. `PlayerView` 子字段矩阵

`PlayerView` 字段见 `engine/.../session/PlayerView.kt:22-45`。每个座位拿到的是 `viewerSeat = 自己` 的实例；下表中“他家”指该实例中表示其他座位的字段。

| 字段 | 本人 | 他家 | 观战 | 日志 | 说明 |
|---|---|---|---|---|---|
| `viewerSeat` | 是 | — | — | 否 | 观战没有座位 |
| `phase` | 是 | 是 | `board.phase` | 是 | 公开阶段 |
| `roundWind/roundNumber` | 是 | 是 | 是 | 是 | 公开场况 |
| `honbaSticks/riichiSticks` | 是 | 是 | 是 | 是 | 公开供托/本场 |
| `dealerSeat/currentTurnPlayer` | 是 | 是 | 是 | 是 | 公开 |
| `wallRemaining` | 是 | 是 | 是 | 是 | 只给剩余张数，不给牌山顺序 |
| `doraIndicators` | 是 | 是 | 是 | 元 | 只含已翻开的表宝指示牌；里宝不放这里 |
| `scores` | 是 | 是 | 是 | 是 | 公开点数 |
| `riichiStatus` | 是 | 是 | 是 | 是 | 公开立直状态 |
| `discards` | 是 | 是 | 是 | 元 | 四家牌河公开 |
| `melds` | 是 | 是 | 是 | 元 | 四家副露公开 |
| `handTileCounts` | 是 | 是 | 是 | 元 | 只给张数，不给牌面 |
| `myClosedTiles` | 是 | — | — | — | 仅本人暗牌 |
| `myDrawnTile` | 是 | — | — | — | 仅本人摸牌 |
| `legalActions` | 是 | — | — | — | 仅本人候选；他家候选不得出现 |
| `lastDiscard/lastDiscardPlayer` | 是 | 是 | 是 | 元 | 公开最近弃牌 |
| `mySleeveTiles` | 是 | — | — | — | 仅本人袖牌；当前 V4 业务不写入（见规则合同 §2.4） |
| `myFuriten` | 是 | — | — | — | 仅本人振听状态 |
| `myDiscardHistory` | 是 | — | — | — | 仅本人完整舍牌历史（含被叫走） |

关键：
- 他家 `myClosedTiles`/`myDrawnTile` 不是“空列表”，而是该实例根本不表示他家手牌；协议映射只给 `handTileCounts`。
- `legalActions` 只返回该座位自己的合法动作；`LegalActionProvider` 的契约明确“不返回非法动作或为他家生成动作”（`LegalActionProvider.kt:9-16`）。
- `mySleeveTiles` 是 V4 遗留字段，当前不承载袖里藏花业务；不要因为字段存在就认为袖牌会被公开/私有投影。

---

## 4. 公共快照/观战字段

`ProtocolPublicSnapshot` 字段见 `protocol/.../ProtocolPublicModels.kt:454-467`；`PublicBoardView` 字段见 `engine/.../projection/PublicTableProjection.kt:328-505`。

### 4.1 公共快照顶层

| 字段 | 观战 | 玩家公共部分 | 日志 | 说明 |
|---|---|---|---|---|
| `sessionId` | 是 | 是 | 是 | 非秘密 |
| `sessionVersion` | 是 | 是 | 是 | 整场版本 |
| `roundId` | 是 | 是 | 是 | 当前/刚结束局 |
| `roundVersion` | 是 | 是 | 是 | 单局版本；V5 待定是否保留 |
| `syncEpoch`/`streamVersion`/`projectionSeq` | 是 | 是 | 否 | 观战独立流；`streamVersion` 可滞后/重复，`projectionSeq` 每次发射 +1；不与玩家流共用 |
| `cursor`（V4） | 是 | 是 | 否 | 观战独立游标；V5 对应物是 `projectionSeq`，不是 `streamVersion` |
| `board` | 是（可为 null） | 是 | 元 | 无活动局时 `null`，不伪造桌面 |
| `session` | 是 | 是 | 是 | 公开摘要；不含出千余额/流水 |
| `clocks` | 是 | 是 | 元 | 四席计时 |
| `paused` | 是 | 是 | 是 | 全局暂停 |
| `settlementRoundId` | 是 | 是 | 是 | 结算局 |
| `settlementEvents` | 公共白名单 | 公共白名单 | 元 | 里宝按 §5 授权 |

### 4.2 `PublicBoardView`

| 字段 | 观战 | 日志 | 说明 |
|---|---|---|---|
| `phase` | 是 | 是 | 公开阶段 |
| `roundWind/roundNumber` | 是 | 是 | 公开 |
| `honbaSticks/riichiSticks` | 是 | 是 | 公开 |
| `dealerSeat/currentTurnPlayer` | 是 | 是 | 公开 |
| `wallRemaining` | 是 | 是 | 只给数量 |
| `doraIndicators` | 是 | 元 | 只含表宝；不含里宝 |
| `scores` | 是 | 是 | 公开 |
| `riichiStatus` | 是 | 是 | 公开 |
| `discards` | 是 | 元 | 公开牌河 |
| `melds` | 是 | 元 | 公开副露 |
| `handTileCounts` | 是 | 元 | 只给张数 |
| `lastDiscard/lastDiscardPlayer` | 是 | 元 | 公开 |

**不得出现**：`myClosedTiles`、`myDrawnTile`、`legalActions`、`myFuriten`、`mySleeveTiles`、`myDiscardHistory`、`decision`、`cheatPoints`、`controls`、`preparation`、`presentationPreview`、`tileMark`、`sleeveBloom`、`beamSwap`、牌山顺序、种子、里宝实体、出千流水。

测试锚点：
- `PublicProjectionTest.activeRoundOmitsUraDoraAndIncludesDiscardsAndMelds`；
- `PublicProjectionTest.sessionSummaryOmitsCheatPointsFromRecordsAndFinalResult`；
- `PublicProjectionTest.preparationPhaseHidesPrivateWallOrderAndClosedTiles`；
- `ProtocolPublicModelsCodecTest.public snapshot rejects unknown fields and private nested supersets at every boundary`；
- 服务器 `PublicBroadcastEquivalenceTest` 验证多观战者收到字节一致的公开更新；“无私有字段”由 `ProtocolPublicModelsCodecTest` 的公共 DTO 白名单/未知字段测试与专用 scan 覆盖，不能只靠该等价性测试。

---

## 5. 结算与里宝授权

### 5.1 公共结算事件

- `TsumoWin/RonWin` 公开 `winTile`、`yakuList`、`han/fu/score`、`breakdown`；**不含完整手牌**（`GameEvent.kt:57-79`）。
- `ScoreSettled` 公开 `payments/newScores`；不含出千流水（`GameEvent.kt:101-102`）。
- `DrawGame` 公开 `reason/tenpaiPlayers`；不公开未和手牌。
- `RoundEnded/MatchEnded` 公开局/场结果。

### 5.2 里宝

V4 的公共投影对 `ScoreBreakdown.dora` 执行：
- 未授权：清空 `uraTiles`、`uraHits`；
- 授权：保留完整里宝实体/命中；
- **`uraCount` 数值仍保留**（测试 `deepBreakdownUraDetailsStrippedWhenUnauthorizedAndPreservedWhenAuthorized` 明确断言 `uraCount==1`）。
- 证据：`PublicTableProjection.kt:740-756`；`PublicProjectionTest.kt:365-404`。

这是一个需要产品裁决的边界：`uraCount` 不是牌面，但它泄露“里宝命中张数”。V5 预冻结默认：
- 结算时若已公开和牌，`uraCount` 可作为计分结果公开；
- 里宝**实体**（`uraTiles/uraHits`）只在显式 `revealSettlementUra=true` 且结算批次公开；
- 进行中/非结算批次：`DoraRevealed(isUraDora=true)` 一律不投递。
- 若产品要求连 `uraCount` 也不公开，需新投影版本，不改 V4 行为。

### 5.3 和牌手牌

V4 公共事件不含“和牌者完整手牌”。若 V5 要在结算展示手牌，必须新增字段/事件，默认私有/待裁决；不能把 `PlayerView.myClosedTiles` 塞进公共快照。

---

## 6. 私有千术视图

### 6.1 落汗 `tileMark`

- `TileMarkView` 含 `selectableTileIds`、`activeGroups`、每张 `MarkedTileView` 的 `tileId/tile/area/seat/riverPosition`；`area` 可为 `OPPONENT_HAND` 并携带牌面（`TileMarkViews.kt:19-50,156-169`）。
- 这是 owner 的“透视”收益，**只能给 owner**；其他玩家/观战/日志不得出现。
- 服务器测试：`TileMarkServerContractTest.private effect survives binding replacement while guest and spectator receive no owner marks`。

### 6.2 袖里藏花 `sleeveBloom`

- `SleeveBloomOfferView` 含 `firstTile/secondTile` 两张牌面、`offerId`、`deadline`、`mode`、`windowId`；`reservation` 含预留牌（`SleeveBloomModels.kt:452-582`）。
- 只有 owner 能看到；观战/其他玩家不得看到两张候选牌或预留牌。
- 服务器测试：`SleeveBloomServerContractTest.off-turn host wiring keeps private offer reservation and reconnection snapshot`；`PublicSnapshot` 无 `sleeveBloom` 字段。

### 6.3 偷梁换柱 `beamSwap`

- `BeamSwapOfferView` 含候选槽位、`selectableHandTileIds`、`deadline`；`BeamSwapCandidateView` 只有被 owner 落汗标记命中时才带 `tile` 牌面（`BeamSwapModels.kt:331-495`）。
- 只有 owner 能看到；观战/其他玩家不得看到候选/手牌 ID/牌面。
- 服务器测试：`BeamSwapHostContractTest` 的 `assertPublicHasNoOffer` 扫描 `beamSwap/pendingOffer/candidates/selectableHandTileIds/targetSlot` 均不得出现在公共 JSON。

### 6.4 准备阶段 `preparation`

- `ShufflePreparationView` 含本人的 target/seeThrough/confirmed/reserved/available/seeThroughEnabled/seeThroughCost（`ShufflePreparationView.kt`）。
- 只有本人能看到；观战/其他玩家不得看到目标、看破选择、预留点数。
- 但 `seeThroughEnabled`/`seeThroughCost` 是规则能力，若要全局显示，应由房间配置公开，而不是从私有快照泄露。

---

## 7. 日志与审计

### 7.1 V4 现状

- V4 没有统一的“逐事件游戏日志/回放流”；服务器日志主要是连接、错误、房间/回执、性能。
- 现有 `toString` 对 `resumeToken/token/principal/scope` 做了脱敏（如 `RoomBinding.toString`、`IdempotencyKey.toString`、`ApiRequest.toString`）。
- 但 V4 没有强制的“事件日志字段白名单”，不能把“日志”当作已实现的可见性边界。

### 7.2 V5 预冻结日志规则

生产日志（可被运维/集中采集）只允许：
- 事件类型、`turnNumber`、`seat/owner`、`state_version/command_version/event_seq/streamVersion`、`requestId/roomId/sessionId/roundId`（ID 可记录，token 不可）、错误码、耗时、字节数；
- 公开结算的数值：`han/fu/score/payments/newScores`、和牌者/放铳者、流局原因；
- 连接/重连/托管/控制代次变更的元数据。

生产日志禁止：
- `TileDealt/TileDrawn/RinshanDraw` 的 `tile` 牌面或 `tileId`；
- 任何 `myClosedTiles/myDrawnTile`、牌山顺序、`seed`、`wall`、`deadWall`、里宝实体；
- `tileMark` 的对手暗牌牌面/位置、`sleeveBloom` 候选牌、`beamSwap` 候选/手牌 ID；
- `cheatPoints` 余额、`CheatPointsChanged.amount/newBalance`、私有 `cheatEntries` 原因；
- token/resumeToken/scope/principal 原文。

审计存储（若需要）：
- 与生产日志分离，加密、最小权限、按 `roomId/sessionId` 访问控制；
- 记录完整私有事件/牌山/种子时必须标注“含隐藏信息”，不得被普通日志/APM 采集；
- 任何导出/回放工具必须遵守同一可见性矩阵。

---

## 8. V4 已发现冲突与 V5 目标

### 8.1 `CheatPointsChanged` 被玩家过滤放行

- V4 `SessionUpdate.redactForViewer` 的 `else -> event` 使 `CheatPointsChanged` 对**四席**都可见（`SessionUpdate.kt:85-91`）。
- 公共投影/协议白名单明确丢弃它（`PublicProjectionFactory.kt:147-152`；`ProtocolPublicModels.kt:440-445`）。
- 因此 V4 的“玩家可见、观战不可见”是**不对称**的；AGENTS.md 说的是“出千变动私有同步与局末汇总”，不是“四席实时看到”。
- **V5 目标**：`CheatPointsChanged` 仅 owner；其他三席不收到；观战/日志不收到。若产品希望“局末公开汇总”，用独立的 `CheatSettlementSummary`（仅聚合、无逐笔原因）并单独裁决。

### 8.2 玩家快照含四席出千余额/局末 delta

- `TableAuthority.querySnapshot` 的 `cheatPoints` 直接取 `state.cheatPoints.toList()`，是四席余额（`TableAuthority.kt:2554-2563`）。
- `PlayerSnapshot.session.roundRecords` 的 `PublicRoundRecord` 含四席 `finalCheatPoints/cheatDeltas`（`PublicRoundRecord.kt:18-37`；`MatchSession.kt:370-379`）。
- 公共观战的 `PublicSessionSummary/PublicScoreRecord` 不含这些字段（`PublicTableProjection.kt:69-78,148-215`），且测试 `sessionSummaryOmitsCheatPointsFromRecordsAndFinalResult` 断言不泄露。
- **V5 目标**：玩家只看到自己的余额/自己的局末 delta；其他席只看到“是否使用过千术/次数”这类公开计数（若产品需要）。观战默认无出千经济信息。该条是产品裁决 D-12。

### 8.3 `uraCount` 与里宝实体

见 §5.2。V5 默认保留 `uraCount`（计分结果），清空实体；若要更严格，新投影版本。

### 8.4 角色切换/重连的私有缓存

- V4 有 `bindingEpoch` 与 `applyPlayerBindingState`，绑定变化会清空/替换玩家状态（`TcpClientBinding.kt:2290-2299`）。
- 但“玩家 -> 观战”或“观战 -> 玩家”的角色切换是否清空 `tileMark/sleeveBloom/beamSwap/preparation` 缓存，需要 V5 专门测试；不能只依赖 `syncEpoch`。
- V5 要求：任何 `bindingEpoch/streamVersion/projectionSeq` 变化或基线重置，客户端清空旧私有快照/重排缓冲；服务端对新身份重新投影，不复用旧对象。

### 8.5 控制信息

- V4 `controls` 含 `role/connected/controlEpoch`，只给玩家快照，观战没有（`SeatControlView.kt:11-27`；`TableAuthority.querySnapshot`）。
- `controlEpoch` 是权威控制代次，不是网络连接代次；不要把 `controls` 当公开聊天/观战信息。
- V5 目标：同桌玩家可见“谁被托管/是否连接”的最小状态；观战默认不显示；日志只记录变更事件，不记录私有决策。

---

## 9. 测试映射与 V5 必补测试

### 9.1 已有可复用测试

| 测试 | 覆盖 | 结果 |
|---|---|---|
| `SessionSnapshotContractTest.private dealt drawn and rinshan tiles only reach their owner` | 3 个私有事件逐席 | 通过 |
| `SessionSnapshotContractTest.unpublished ura dora cannot use the default public event branch` | 未授权里宝不公开 | 通过 |
| `SessionSnapshotContractTest.public discard remains visible to all seats` | 公开弃牌四席可见 | 通过 |
| `PublicProjectionTest.publicEventsDropsPrivateEventsAndRetainsPublicEvents` | 公共白名单丢弃 4 类私有事件 | 通过 |
| `PublicProjectionTest.uraDoraRevealedOnlyWhenExplicitlyAuthorized` | 里宝显式授权 | 通过 |
| `PublicProjectionTest.deepBreakdownUraDetailsStrippedWhenUnauthorizedAndPreservedWhenAuthorized` | breakdown 实体剥离/`uraCount` 保留 | 通过 |
| `PublicProjectionTest.sessionSummaryOmitsCheatPointsFromRecordsAndFinalResult` | 公共摘要无出千 | 通过 |
| `PublicProjectionTest.preparationPhaseHidesPrivateWallOrderAndClosedTiles` | 准备阶段隐藏牌山/暗牌 | 通过 |
| `ProtocolPublicModelsCodecTest.public snapshot rejects unknown fields and private nested supersets at every boundary` | 公共 DTO 拒绝私有嵌套 | 通过 |
| `ProtocolPublicModelsCodecTest.public events whitelist applies to snapshot update and standalone result response` | 公共事件白名单 | 通过 |
| `TileMarkServerContractTest.private effect survives binding replacement while guest and spectator receive no owner marks` | 落汗私有/观战无 | 通过 |
| `SleeveBloomServerContractTest.off-turn host wiring keeps private offer reservation and reconnection snapshot` | 袖 offer/预留私有 | 通过 |
| `BeamSwapHostContractTest` `assertPublicHasNoOffer` | 梁公共 JSON 无私有字段 | 通过 |
| `PublicBroadcastEquivalenceTest` | 多观战者字节一致 | 通过 |

### 9.2 V5 必补

1. **19×4+观战** 的逐事件矩阵测试：每个事件在每个流上“有/无 + 字段完整/遮蔽”。
2. 公共 JSON 扫描：禁止键 `wall/seed/hand/myClosedTiles/myDrawnTile/uraTiles/uraHits/cheatPoints/cheatEntries/preparation/presentationPreview/tileMark/sleeveBloom/beamSwap/pendingOffer/candidates`。
3. 观战扫描：不得出现任何 `my*`、`decision`、`legalActions`、`controls`、`cheat*`。
4. 玩家跨席扫描：P0 快照不得包含 P1 的暗牌/摸牌/候选/offer/标记。
5. 角色切换：玩家 -> 观战 -> 玩家，旧私有缓存不得复用；旧 `bindingEpoch/streamVersion/projectionSeq` 快照必须丢弃。
6. 结算：未授权里宝实体不出现；`uraCount` 按产品决定；和牌完整手牌默认不出现。
7. 日志：对含私有事件的批次做日志快照扫描，断言无牌面/种子/余额/token。
8. 新增事件默认私有：任何新事件若未显式加入白名单，公共流必须拒绝。

---

## 10. 证据、验证命令与限制

### 10.1 实际读取

- `engine/.../session/{PlayerView,SessionUpdate}.kt`
- `engine/.../projection/{PublicProjectionFactory,PublicTableProjection}.kt`
- `engine/.../access/{PlayerSnapshot,PublicRoundRecord,PublicSessionResult,SessionView,SeatControlView}.kt`
- `engine/.../cheat/{mark,bloom,beam}/*`（视图/投影工厂）
- `engine/.../authority/TableAuthority.kt`（`querySnapshot/queryPublicProjection/buildSessionView`）
- `server/.../host/ServerTableHost.kt`（`publishStateChanges/publishPublicChanges/subscribe*`）
- `server/.../mapping/EngineProtocolMapper.kt`（快照/事件映射）
- `protocol/.../ProtocolPublicModels.kt`、`ProtocolModels.kt`、`ApiMessage.kt`
- `client/.../api/{ViewModels,PublicViewModels}.kt`、`client/.../mapping/ProtocolViewMapper.kt`、`client/.../session/{PlayerStreamEngine,SpectatorStreamEngine}.kt`
- 相关测试：`SessionSnapshotContractTest`、`PublicProjectionTest`、`ProtocolPublicModelsCodecTest`、`PublicSettlementDetailProjectionTest`、`TileMarkServerContractTest`、`SleeveBloomServerContractTest`、`BeamSwapHostContractTest`、`PublicBroadcastEquivalenceTest`、`PlayerStreamEngineTest`、`SpectatorStreamEngineTest`。

### 10.2 验证命令

```powershell
.\gradlew.bat :engine:test --tests "com.majiang.session.SessionSnapshotContractTest" `
  --tests "com.majiang.projection.PublicProjectionTest" --console=plain

.\gradlew.bat :protocol:test --tests "com.majiang.transport.api.ProtocolPublicModelsCodecTest" --console=plain

.\gradlew.bat :server:test --tests "com.majiang.server.tcp.PublicBroadcastEquivalenceTest" `
  --tests "com.majiang.server.mark.TileMarkServerContractTest" `
  --tests "com.majiang.server.bloom.SleeveBloomServerContractTest" `
  --tests "com.majiang.server.beam.BeamSwapHostContractTest" --console=plain

.\gradlew.bat :client:test --tests "com.majiang.client.session.PlayerStreamEngineTest" `
  --tests "com.majiang.client.session.SpectatorStreamEngineTest" `
  --tests "com.majiang.client.api.PublicSettlementDetailProjectionTest" --console=plain
```

### 10.3 实际结果

| 命令 | 结果 |
|---|---|
| `:engine:test`（上述 2 类，属于本任务 10 类批次的一部分） | 110 tests / 0 failures / 0 errors / 0 skipped（整批） |
| `:protocol:test`（上述 1 类，属于 6 类批次） | 56 tests / 0 failures / 0 errors / 0 skipped（整批） |
| `:server:test`（上述 4 类，属于 7 类批次） | 43 tests / 0 failures / 0 errors / 0 skipped（整批） |
| `:client:test`（上述 3 类） | 17 tests / 0 failures / 0 errors / 0 skipped |

本任务定向测试合计 **226 项通过**；不是全量，不是 V5 验收。

### 10.4 限制

- 未修改 V4 生产代码/测试；未提交/推送。
- 未做 Unity/Android/IL2CPP/真机/跨物理网络。
- V4 没有统一的逐事件日志系统；§7 是 V5 新约定，不是 V4 已实现。
- 出千余额/流水可见性是产品未决项；本文给出默认私有建议，不替产品做最终承诺。
- 本文件不构成 G0 通过；G0 需 P0.1/P0.2/P0.3 齐全并由 Codex 复核。
```
