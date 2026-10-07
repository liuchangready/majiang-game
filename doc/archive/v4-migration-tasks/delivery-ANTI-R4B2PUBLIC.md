# 交付记录：R4B2PUBLIC 独立 PUBLIC 领域投影

**开发者**：Antigravity (Anti)  
**阶段**：R4B2 并行 B  
**测试状态说明**：遵循开发规则，Anti 仅使用内置文件读写工具进行生产实现与模块测试编写，**未运行任何终端、Gradle 构建、独立测试或验证指令**，全量构建与验收测试由 Codex 串行执行。

---

## 一、新增与修改文件清单

本任务严格遵守互斥文件所有权，仅在 `com.majiang.projection` 包内新增领域生产代码与单元测试，未修改任何已有生产文件、DTO、协议或 `tools/architecture/v4-review` 审查用例：

1. **`engine/src/main/kotlin/com/majiang/projection/PublicTableProjection.kt`**
   - 定义独立公开领域模型：`PublicTableProjection`、`PublicBoardView`、`PublicSessionSummary`、`PublicScoreRecord`、`PublicMatchResult`。
   - 内部辅助实现 `CopyOnReadEventList`、`sanitizeOutcome`、`eventValueEquals` 与 `eventValueHashCode`，提供深度不可变、防御拷贝与正确值比较语义。
2. **`engine/src/main/kotlin/com/majiang/projection/PublicProjectionFactory.kt`**
   - 纯函数单例对象 `PublicProjectionFactory`，暴露冻结契约方法 `project(...)` 与 `publicEvents(...)`。
3. **`engine/src/test/kotlin/com/majiang/projection/PublicProjectionTest.kt`**
   - 包含准备阶段牌山隐匿、局中公开牌河/副露/表宝、局间 `state == null` 边界、终局结果去私密化、流局嵌套 Set 防御拷贝、事件白名单脱敏、里宝深层 Breakdown 剥除、CopyOnRead 数组防污染、以及列表值语义等 10 项严密测试。
4. **`doc/v4-migration/delivery-ANTI-R4B2PUBLIC.md`**（本文档）

---

## 二、完整最终模型字段契约

### 1. `PublicTableProjection`
- `sessionId: String`：比赛唯一标识（非空白）
- `sessionVersion: Long`：整场版本号（>= 0）
- `roundId: String?`：活动单局标识（局间/终局为 null）
- `roundVersion: Long?`：单局状态机版本号（活动局 >= 0，无局为 null）
- `board: PublicBoardView?`：公开牌桌视图（`state == null` 时为 `null`，不伪造桌面）
- `session: PublicSessionSummary`：整场赛程与公开历史摘要
- `clocks: List<ClockView>`：各座位权威时钟视图（不可修改列表）
- `paused: Boolean`：权威时钟是否暂停
- `settlementRoundId: String?`：刚结束或终局结算的单局标识
- `settlementEvents: List<GameEvent>`：经过白名单过滤与深快照的公开结算事件（`CopyOnReadEventList`）

### 2. `PublicBoardView`
包含 16 个权威公开字段，严格剥离私密手牌、牌山、里宝与出千资源：
- `phase: GamePhase`：当前游戏阶段
- `roundWind: Int`：场风 (1=东..4=北)
- `roundNumber: Int`：局数 (0..3)
- `honbaSticks: Int`：本场棒数 (>= 0)
- `riichiSticks: Int`：供托立直棒数 (>= 0)
- `dealerSeat: Int`：庄家座位 (0..3)
- `currentTurnPlayer: Int`：当前行动玩家座位 (0..3)
- `wallRemaining: Int`：牌山剩余有效摸牌数
- `doraIndicators: List<Tile>`：翻开的表宝牌指示牌列表（不含任何未揭示里宝）
- `scores: List<Int>`：当前四家得分 (size 4)
- `riichiStatus: List<Boolean>`：四家立直宣言状态 (size 4)
- `discards: List<List<Tile>>`：四家公开弃牌堆 (size 4，内部列表不可变)
- `melds: List<List<Meld>>`：四家公开副露列表 (size 4，副露 tiles 内部不可变)
- `handTileCounts: List<Int>`：四家暗牌张数（严格等于 `closedTiles.size + (if (drawnTile != null) 1 else 0)`，绝不泄露具体牌张实体）
- `lastDiscard: Tile?`：最后打出的牌面
- `lastDiscardPlayer: Int?`：最后打出牌的玩家座位 (0..3 或 null)

### 3. `PublicSessionSummary`
- `lifecycle: String`：整场生命周期名称（如 ROUND_ACTIVE, WAITING_READY, FINISHED 等）
- `scores: List<Int>`：权威公开点数（size 4，局中来自当前局点数，终局来自终局结算点数）
- `roundWind: Int`、`roundNumber: Int`、`dealerSeat: Int`、`honba: Int`：赛程场况
- `roundRecords: List<PublicScoreRecord>`：单局公开历史记录列表（不可修改）
- `finalResult: PublicMatchResult?`：整场终局排名与结果（未终局为 null）

### 4. `PublicScoreRecord`
- `roundId: String`：单局标识
- `resultId: String`：结算结果标识
- `finalScores: List<Int>`：单局各家结束点数 (size 4)
- `deltas: List<Int>`：单局各家净胜变动 (size 4)
- `remainingRiichiSticks: Int`：残余供托立直棒数
- `outcome: RoundOutcome?`：单局胜负类型（若为 `ExhaustiveDraw`，其 `tenpaiSeats` 已防御性拷贝并包装为不可修改集合；杜绝 `finalCheatPoints`、`cheatDeltas`、`cheatEntries`）

### 5. `PublicMatchResult`
- `finalScores: List<Int>`：最终各家总得分 (size 4)
- `rankedSeats: List<Int>`：依权威名次从 1 到 4 排列的座位号
- `reason: String`：终局原因（如 NORMAL_COMPLETION, BANKRUPTCY 等）
- `remainingRiichiSticksTransferredTo: Int?`：供托棒归属座位（若有；杜绝 `finalCheatPoints`、`cheatSummary`）

---

## 三、公开事件白名单机制

`PublicProjectionFactory.publicEvents` 采用明确的白名单设计，杜绝使用 `filterVisibleEvents(events, -1)` 造成的默认放行和 ura 泄露：

| 事件类型 | 判定 | 处理行为 |
|---|---|---|
| `StackingStarted`, `WallBuilt`, `DealingComplete` | 允许 | 流程事件，深快照后保留 |
| `TileDiscarded` | 允许 | 公开舍牌，保留 |
| `ChiDeclared`, `PonDeclared`, `KanDeclared` | 允许 | 公开副露，副露 tiles 内部不可修改深拷贝 |
| `RiichiDeclared` | 允许 | 公开立直宣言，保留 |
| `TsumoWin`, `RonWin` | 允许 | 和牌事件，保留公开役种/符/支付/总分；若 `revealSettlementUra=false`，深度剥除 `breakdown.dora.uraTiles` 与 `uraHits`；若 `true` 则保留完整明示结算 |
| `DoraRevealed` | 条件允许 | `!isUraDora`（表宝）始终保留；`isUraDora`（里宝）仅在调用方显式传入 `revealSettlementUra=true` 时放行，否则严格过滤丢弃 |
| `BustedEvent` | 允许 | 公开抓包事件，保留 |
| `DrawGame` | 允许 | 流局事件，`tenpaiPlayers` 列表不可修改深拷贝 |
| `ScoreSettled` | 允许 | 点数结算，payments 包装为只读 Map，newScores 数组由 `CopyOnReadEventList` 隔离 |
| `RoundEnded`, `MatchEnded` | 允许 | 终局事件，finalScores 数组由 `CopyOnReadEventList` 隔离 |
| `TileDealt` | **丢弃** | 私密发牌，全员过滤 |
| `TileDrawn`, `RinshanDraw` | **丢弃** | 私密摸牌，防透视作弊 |
| `CheatAttempted` | **丢弃** | 千术尝试（无论成败），防意图窥视；公开抓包仅走 `BustedEvent` |
| `SuspicionChanged`, `GazeShifted` | **丢弃** | 私人警戒与视线状态，属于 AI/防作弊内部机制 |
| `CheatPointsChanged` | **丢弃** | 出千点数流水，禁止作为观战公开事件流暴露 |
| 任何其他未知事件 | **丢弃** | 严格白名单 fallback |

---

## 四、针对 FEEDBACK-R4B2PUBLIC.md 的专项治理

1. **数组事件读取污染隔离 (`CopyOnReadEventList`)**
   - 领域事件 `ScoreSettled.newScores` 与 `MatchEnded.finalScores` 为可变 `IntArray`，原有单纯不可修改 List 只能锁定集合引用，无法阻止外部读取事件后就地修改数组。
   - 实现 `CopyOnReadEventList : List<GameEvent>`，在 `get(i)` 及迭代器遍历时，对事件执行深度快照并复制数组（clone）。外部修改读取到的数组不会污染底层存储，再次调用 `list[i]` 始终返回原始未被破坏的权威数值。
2. **数组数据类值语义比较 (`eventValueEquals`)**
   - 由于 Kotlin 数据类对原生数组字段的默认 `equals` 使用引用比较（`===`），多次 `get(i)` 产生的克隆实例若直接使用默认比较会导致 `list.contains(list[0])` 或 `projection.deepCopy() == projection` 意外返回 false。
   - 在 `CopyOnReadEventList` 中实现 `eventValueEquals` 与 `eventValueHashCode`，针对 `ScoreSettled` 与 `MatchEnded` 比较 `newScores.contentEquals(...)` 与 `finalScores.contentEquals(...)`。确保 `contains`、`indexOf`、`equals` 与 `PublicTableProjection` 的 `copy`/`deepCopy` 保持严谨的值语义一致性。
3. **里宝牌深层 Breakdown 边界**
   - 严格落实反馈：即使事件中没有独立的 `DoraRevealed(isUra=true)`，未授权的里宝也不得通过和牌结算事件泄漏。
   - 当 `revealSettlementUra=false` 时，`TsumoWin`/`RonWin` 的 `breakdown.dora.uraTiles` 与 `uraHits` 被清空（`emptyList()`），仅保留 `yakuLines`、`fu`、`score`、`paymentLines`、`totalPoints`、`uraCount` 及 `totalHan` 等公开计分数值；
   - 仅当调用方证明真实公开结算并传入 `revealSettlementUra=true` 时，保留完整的 `uraTiles` 与 `uraHits` 物理牌与指示牌命中详情。
4. **`RoundOutcome.ExhaustiveDraw` 嵌套别名治理 (`sanitizeOutcome`)**
   - 新增 `sanitizeOutcome` 函数，对 `RoundOutcome.ExhaustiveDraw.tenpaiSeats: Set<Int>` 执行 `Collections.unmodifiableSet(LinkedHashSet(...))` 防御性隔离，杜绝外部传入可变 Set 别名，且通过 getter 尝试修改该 Set 时立即抛出 `UnsupportedOperationException`。

---

## 五、后续宿主与网络层调用方式建议

本任务为纯领域投影实现，未接入任何宿主或网络层。后续权威宿主集成观战流时，推荐在 `TableAuthority` 或服务端调度队列中按如下方式调用：

```kotlin
// 在同一权威状态队列临界区内提取只读快照：
val sessionSnapshot = coordinator.session.readState()
val roundState = coordinator.activeRound?.readState() // 局间为 null
val roundId = coordinator.activeRound?.roundId
val roundVersion = coordinator.activeRound?.version
val clocks = authority.buildClockViews()
val paused = authority.isPaused
val isPublicSettlement = ... // 权威证明当前发生公开和牌结算
val settlementEvents = ... // 最近一局结算事件

// 生成纯领域公开投影：
val publicProjection: PublicTableProjection = PublicProjectionFactory.project(
    session = sessionSnapshot,
    state = roundState,
    roundId = roundId,
    roundVersion = roundVersion,
    clocks = clocks,
    paused = paused,
    settlementRoundId = lastSettledRoundId,
    settlementEvents = settlementEvents,
    revealSettlementUra = isPublicSettlement
)

// 服务端包装独立观战网络推送流（携带 spectator 独立的 syncEpoch 与 cursor）：
// val spectatorUpdate = ProtocolSpectatorUpdate(publicProjection, cursor = nextSpectatorCursor(), syncEpoch = spectatorEpoch)
```

---

## 六、未决与待办事项

1. **测试未运行**：Anti 未执行 Gradle 编译或测试，需由 Codex 串行执行 `:engine:test` 及相关审查测试验收。
2. **权威宿主未接线**：`TableAuthority` 内部尚未添加公共观战查询入口；观战流网络打包与 UDP 路由留待后续 R4B 统一批次接入。
3. **不推进后续阶段**：不提前进入 R5/R6。
