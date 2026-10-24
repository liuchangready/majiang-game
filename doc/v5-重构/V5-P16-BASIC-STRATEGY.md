# V5 P1.6 Basic 策略迁移交付（ENG-013）

日期：2026-10-24
任务：追踪矩阵 ENG-013 / P1.6 的 `BasicAIStrategy` 真实缺口。
范围声明：本任务只迁移 V4 `engine/src/main/kotlin/com/majiang/ai/AIStrategy.kt` 中的 `BasicAIStrategy`，并只交付 `BasicAiContext`、该策略、`AiObservation` 的兼容接入、独立测试与本报告共五个文件。**不宣告 P1.6 整体完成，不宣告 G1/G5 通过；V4 仍是唯一生产基线。** 不做 TargetAware、AIPlayer、千术能力或 actor 接线。

本任务不是 ENG-014/015 的重复：Trivial/Tsumogiri 已在 `1219897` 验收合并，本任务未修改这两个策略及其测试。黄金目录线程 `thread_20261024_055105_a2cb` 的五个在途文件与本任务无关，本任务未读取、未依赖、未纳入其交付。另一个 GLM 任务只写 `AiCheatPolicy.cs` 及其独立测试/报告，本任务未触碰。

---

## 1. 交付结论

**PASS（仅限本组件）**

- `BasicAiStrategy` 以 V4 实际源码（不是类注释）为准，逐分支移植；返回原合法候选实例与原 `OptionId`。
- 稳定独立算法身份：`AlgorithmId = "baseline-basic"`，`AlgorithmVersion = 1`；算法身份进入 `CommandIntent` 规范字节，启发式分数不进入权威哈希。
- 新增 78 项针对性测试；Ai 全量 128 项（原 50 + 新 78）通过。
- 新增强类型上下文与 `AiObservation` 兼容接入；旧构造调用与 Trivial/Tsumogiri 行为不变（既有 50 项全过）。
- 第 2 轮独立复核提出的 D-1-01（未定义枚举值被接受）与 D-1-02（公开 `TryChoose` 绕过本席/候选绑定）已批量修复；新增有效/无效负例矩阵，详见 §5.3/§7.1。

**不包含**

- 不实现 actor 如何产出 `BasicAiContext`；这是后续接线任务。当前 `TableActor.ProcessAiObservation` 仍用旧构造调用，因此真实 actor 观察上运行 Basic 会得到显式的 `BasicAiContextException`，而不是伪造事实或静默降级。
- 不修改 Domain 动作枚举/DTO、`CommandIntent`、既有基线报告、既有探针、fixture、追踪矩阵、统一 verifier。
- 不运行 PG/WSS/Unity/IL2CPP/真机/物理网络；这些为 `NOT_RUN`。

---

## 2. 基线、环境与只读规则

| 项 | 实值 |
|---|---|
| 独占工作树 | `D:/majiang-wt/ai-basic` |
| 分支 | `wt/v5-ai-basic` |
| 起始 HEAD | `597eca1ff4688ff61fa4f5fea700bdf9eb3dd1be`（已验收 PR19 head；与 dispatch 指定基线一致） |
| 第 2 轮返修起点 | `ea69c84ff0dc2361f7df0c932598568665a365c9`（第 1 轮交付，独立复核结论 REQUEST_REWORK） |
| 起始状态 | `git status --porcelain` 为空，工作树 clean |
| .NET SDK | `8.0.426` @ `D:/Unity/dotnet/dotnet.exe` |
| 共享库目标 | `netstandard2.1`，C# 9，`Nullable=enable`，`TreatWarningsAsErrors=true` |
| `Mahjong.Ai` 依赖 | 仅 `Mahjong.Domain`；无 Unity/Contracts/TableRuntime/网络/时钟/RNG 引用 |

只读阅读了：

- `AGENTS.md`、`doc/v5-重构/DEEPSEEK-V5-MODULE-MIGRATION-PLAN.md`、`doc/v5-重构/V5-0-1-IMPLEMENTATION-TASKS.md`。
- 主树未提交执行规则：`D:/majiang/doc/v5-重构/V5-REVIEW-REWORK-PROTOCOL.md`（untracked，只读）及主树 `V5-0-1-IMPLEMENTATION-TASKS.md` 未提交新增的第 6 条“复核必须一次性给全缺陷、返修批量完成”。本报告按该规则一次性列出全部已知限制与未运行项。
- 主树 `D:/majiang` 当前有用户未提交改动（`CLAUDE.md`、`doc/ui/round-preparation/PROMPTS.md`、任务书、协议文档）；本任务未触碰、未覆盖、未提交这些文件。

V4 仍是生产架构；G1/G5 未通过；V5-1 尚未整体验收。

---

## 3. V4 实际源码行为对照

来源（基线 HEAD 处读取，SHA-256 见 §9）：

- `engine/src/main/kotlin/com/majiang/ai/AIStrategy.kt`（`BasicAIStrategy`，`:41-172`）
- `engine/src/main/kotlin/com/majiang/session/PlayerView.kt`（`:22-67, :199`）
- `engine/src/main/kotlin/com/majiang/state/GameAction.kt`（`:25, :35, :39, :45, :52`）
- `engine/src/main/kotlin/com/majiang/rules/HandDecomposer.kt`（`calculateWaits` `:361-378`）
- `engine/src/main/kotlin/com/majiang/state/LegalActionProvider.kt`（`:88-141, :196-224`）

### 3.1 `decideAction` 的逐分支顺序

| # | V4 源码 | V4 实际行为 | V5 实现 | 关键测试 |
|---|---|---|---|---|
| 0 | `:47-48` | 空候选 → `null` | `TryChoose` 空列表返回 `null`；取消优先于空列表短路 | `BasicTryChooseHonoursCancellationBeforeTheEmptyListShortCircuit` |
| 1 | `:51-52` | 第一个 `DeclareRon` | 第一个 `Kind==Ron`，列表序 | `BasicSelectsRonBeforeEveryOtherBranch`、`BasicSelectsTheFirstRonAndFirstTsumoInListOrder` |
| 2 | `:54-55` | 第一个 `DeclareTsumo`（仅无 Ron） | 第一个 `Kind==Tsumo` | 同上、`BasicSelectsTsumoWhenRonAbsent` |
| 3 | `:58-59` | 第一个 `DeclareAbortiveDraw` | 第一个 `Kind==Other` 且上下文显式标注 `BasicAbortiveDrawKind.NineTerminals` | `BasicSelectsExplicitNineTerminalsAbortiveBeforeRiichi`、`BasicDoesNotTreatAnOtherCandidateAsNineTerminalsWithoutExplicitMetadata` |
| 4 | `:62-71` | `scores[viewerSeat] >= 1000` 时，立直候选按“打出后 `calculateWaits(...).size` 最大”选择；Kotlin `maxByOrNull` 平局取列表首个 | `scores[Seat] >= 1000L`；`HandDecomposer.CalculateWaits(removed).Count` 最大；严格 `>` 保留首个 | `BasicDeclaresRiichiAtExactlyOneThousandPoints`、`BasicSkipsRiichiBelowOneThousandAndFallsToDiscard`、`BasicRiichiThresholdReadsTheViewerSeatScore`、`BasicRiichiChoosesTheDiscardWithTheMostWaitTypesNotTheFirstRiichi`、`BasicRiichiTieKeepsTheFirstCandidateInListOrder` |
| 5 | `:74-78` | 仅第一个 `kanType == CLOSED_KAN` 的 `DeclareKan`；open/added 不在此分支 | 仅显式 `BasicKanKind.Closed` 的第一个 Kan；open/added 落空 | `BasicClosedKanWinsBeforePonAndResponsePass`、`BasicSelectsTheFirstClosedKanInListOrder`、`BasicNeverTreatsOpenOrAddedKanAsClosed`、`BasicOpenKanFallsThroughToDiscardWhenNoPassExists` |
| 6 | `:81-87` | 有 Pon 且 `lastDiscard` 是役牌时返回 `ponActions.first()` | 第一个 Pon；`LastDiscard` 为三元牌或自风/场风才返回 | `BasicPonsOnlyTheFirstCandidateWhenTheLastDiscardIsAYakuhaiDragon`、`BasicPonsWhenTheLastDiscardIsTheViewersSeatWind`、`BasicPonsWhenTheLastDiscardIsTheRoundWind`、`BasicDeclinesPonForNonYakuhaiWindAndPasses`、`BasicDeclinesPonForANumberDiscardAndPasses`、`BasicLastDiscardNullMeansNoPublicDiscardAndNoPon` |
| 7 | `:90-97` | 存在 Pass 且同时存在 Chi/Pon/Kan/Ron 时返回第一个 Pass；只有 Pass+Discard 时不走此分支 | 同左；`hasMeldOrWinChoice` 严格为 Chi/Pon/Kan/Ron（不含 Tsumo） | `BasicResponsePassPrecedesDiscardWhenAChiChoiceExists`、`BasicPassBesideOnlyDiscardDoesNotPreemptTheDiscard` |
| 8 | `:100-117` | 先看“已立直且摸牌存在”→ 第一个与摸牌实体相等的 Discard；否则对全部 Discard 取 `tileDisposability` 最大，平局取列表首个 | 同左；`TileId` 值相等；严格 `>` 保留首个 | `BasicRiichiTsumogiriChoosesTheExactPhysicalDrawnCopy`、`BasicTsumogiriOnlyAppliesWhenTheViewerIsInRiichi`、`BasicFirstTieKeepsListOrder` |
| 9 | `:120` | `passAction ?: legalActions.first()` | 同左 | `BasicFallbackReturnsPassWhenNothingHigherApplies`、`BasicFallbackReturnsFirstLegalCandidateWhenNoPassExists`、`BasicFallbackCanReturnRiichiWhenTheScoreGateSkippedIt` |

补充：

- V4 的 `DeclareAbortiveDraw` 只由 `RuleEngine.canDeclareNineTerminals` 产出（`LegalActionProvider.kt:88-90`）；V5 没有专用枚举，故用 `Kind.Other` + 显式 `BasicAbortiveDrawKind.NineTerminals`，**不把任何未标注 Other 当作九种九牌**。第 2 轮修复后，策略分支显式比较 `== BasicAbortiveDrawKind.NineTerminals`，不再使用 `HasValue`；构造/验证边界拒绝 `-1`、`1`、`999` 等未定义枚举值。
- V4 的 `DeclareKan` 携带 `MeldType`（`GameAction.kt:35`，`LegalActionProvider.kt:98-113, :204-209`）；V5 的 `LegalActionOption` 没有该字段，故用 `BasicCandidateFacts.KanKind` 显式提供，**不把任何 Kan 当作暗杠**。`BasicKanKind` 只接受 `Closed/Open/Added`；`-1`、`3`、`999` 被拒绝。

### 3.2 立直与待牌

V4：

```kotlin
val remaining = view.myAllClosedTiles.toMutableList()
remaining.remove(action.discardTile)
decomposer.calculateWaits(remaining).size
```

- `myAllClosedTiles` = `myClosedTiles + myDrawnTile`（drawn 存在时追加，`PlayerView.kt:66-67`）。
- `MutableList.remove(element)` 删除**第一个实体相等**元素；V4 `Tile` 是 data class，相等含 `copyIndex`，所以只删一张物理副本，不删同 type 的所有副本。
- `calculateWaits` 返回 `Set<Int>`（typeIndex），`.size` 是**待牌种数**，不是剩余物理张数，也不读取牌山。
- 平局 `maxByOrNull` 保留列表首个。

V5 对应：

- `WaitsAfterDiscard` 复制 `allClosed`，按 `TileId` 实体相等删除第一个匹配项，再调用既有 `HandDecomposer.CalculateWaits`；未复制/另造和牌规则。
- 选择比较为严格 `>`，平局保留首个。
- 测试 `BasicRiichiRemovesOnlyOnePhysicalCopyOfTheDiscard` 用“两张同 type 5p，弃一张后剩 3p4p5p6p，待 3p/6p 两种；若误删全部同 type 则 0 种”的输入固定“只删一次”。
- 测试 `BasicRiichiWaitTypeCountDoesNotReadRemainingWallCount` 用同一手牌、`RemainingWallCount=0` 与 `136` 两个观察，结果一致，固定“不读牌山/剩余张数”。
- 测试 `BasicRiichiChoosesTheDiscardWithTheMostWaitTypesNotTheFirstRiichi` 固定“待牌种数 3 对 1”，而不是先出现的立直候选。

### 3.3 `tileDisposability` 整数公式

| V4 条件（`:135-171`） | 分值 | V5 |
|---|---|---|
| 字牌孤张且非役牌 | `+15` | 同 |
| 字牌孤张且役牌 | `+5` | 同 |
| 幺九数牌孤张 | `+10` | 同 |
| `sameTypeCount >= 2` | `-20` | 同 |
| `sameTypeCount >= 3` | 再 `-40` | 同 |
| 数牌有同花色邻牌：`it.suit == tile.suit && it != tile && abs(rank差) <= 2` | `-10` | 同；`it != tile` 用 `TileId` 实体不等 |
| 数牌 rank ∈ {1,2,8,9} | `+3` | 同 |

- `sameTypeCount` 按 typeIndex 统计全部 `myAllClosedTiles`，红五与普通五同 type 都计入。
- 邻牌判断的 `it != tile` 是**物理副本不等**：同 type 的另一张 copy 距离为 0，仍算邻牌。测试 `BasicCountsAnotherPhysicalCopyOfTheSameTypeAsANeighbour` 固定这一点。
- 分值全为整数；V5 只把最终整数作为 `AiStrategicChoice.HeuristicScore`（double 承载），不进入 `CommandIntent`。

### 3.4 `isYakuhai` 与双风

V4 `:123-130`：

- 三元牌恒为役牌；
- 风牌仅当 `rank == mySeatWind` 或 `rank == roundWind`；
- `mySeatWind = ((viewerSeat - dealerSeat + 4) % 4) + 1`（东 1、南 2、西 3、北 4）。

V5：

- 三元牌：`TileSuit.Dragon`。
- 风牌：`Rank` 与计算出的 `SeatWind(context.Seat, dealerSeat)` 或 `roundWind` 比较。
- 测试同时覆盖自风、场风、非役风、数牌、三元牌；`BasicSeatWindYakuhaiUsesTheDealerRelativeSeat` 与 `BasicRoundWindYakuhaiIsRecognised` 分别用“若风识别错误则会平局并选中错误首个候选”的输入固定。

### 3.5 牌实体、红五与手牌移除

- V4 `Tile` data class 相等含 `suit/rank/copyIndex`；赤五是数牌 5 的 `copyIndex==3` 物理副本。
- V5 `TileId` 值 `0..135`，`typeIndex=id>>2`，`copyIndex=id&3`，值相等即实体相等；红五 `copyIndex==3`。
- 所有“摸切/待牌删除/邻牌排除”均用 `TileId` 实体相等，不用 typeIndex 代替。
- 测试：`BasicRiichiTsumogiriChoosesTheExactPhysicalDrawnCopy`、`BasicTsumogiriOnlyAppliesWhenTheViewerIsInRiichi`、`BasicRiichiWaitCountTreatsSameTypeCopiesAsOneTypeAndKeepsTheSelectedInstance`、`BasicCountsAnotherPhysicalCopyOfTheSameTypeAsANeighbour`。

### 3.6 手牌表示差异（重要）

V4 `PlayerView.myClosedTiles` **不含**摸牌，`myAllClosedTiles` 才在末尾追加 `myDrawnTile`。

V5 运行时当前 `TableActor.ProcessAiObservation` 传入 `_round.Tiles.TilesInZone(TileZone.Hand, seat)`（`TableActor.cs:4179`），而 Domain 的 `DrawnTileEvidence` 要求摸牌就在 Hand zone（`TilePositionMap.cs:1584-1589`），因此 **`AiObservation.MyHandTileIds` 实际可能已经包含摸牌**，与 V4 `myClosedTiles` 不同。

本任务的处理：

- `BasicAiStrategy.AllClosedTiles` 采用“drawn 不在 hand 中才追加”的规范化，两种表示都得到 V4 的 `myAllClosedTiles` 且不会重复一张物理牌。
- 测试 `BasicNormalisesAClosedOnlyHandPlusDrawnTileForDisposability` 用同一逻辑手牌的“closed-only + drawn”和“hand 已含 drawn”两种输入，断言同一选择。
- **未修改**既有 Trivial/Tsumogiri。它们及其测试仍把 `MyHandTileIds` 当作 V4 closed hand 使用；这是本任务范围外的既有契约风险，见 §7-D2。

### 3.7 计划文本与 V4 源码的差异

`DEEPSEEK-V5-MODULE-MIGRATION-PLAN.md` §2.1.7 称 `BasicAIStrategy` “含 Double 启发式”。实际 V4 `BasicAIStrategy` 的 `tileDisposability` 返回 `Int`、立直排序用 `calculateWaits(...).size`（`Int`），没有 Double 参与 Basic 的选择。Double 启发式属于 `TargetAwareAIStrategies` / `AiDecisionModels`，本任务未迁移。本移植以 V4 源码为准，不据计划文本引入浮点。

---

## 4. V5 实现与逐文件清单

### 4.1 expectedFiles（共五个）

| # | 文件 | 内容 | SHA-256（提交前） |
|---|---|---|---|
| 1 | `dotnet/src/Mahjong.Ai/BasicAiContext.cs` | 新增：`BasicAiContext`、`BasicCandidateFacts`、`BasicKanKind`、`BasicAbortiveDrawKind`、`BasicAiContextException`；第 2 轮加入枚举有效性/共享候选绑定验证 | `5c0c5b740282304333e5d41e2b4cd133373145c96857a081bf893163e2296ebc` |
| 2 | `dotnet/src/Mahjong.Ai/BasicAiStrategy.cs` | 新增：`BasicAiStrategy : IAiStrategy`（ENG-013 移植）；第 2 轮显式 `NineTerminals` 比较与 raw 入口契约 | `89b644656350bbdd6e9900d2fb65e383093d6a3d1518254cb6cfa81309332663` |
| 3 | `dotnet/src/Mahjong.Ai/AiObservation.cs` | 修改：可选 `BasicAiContext` 参数、只读属性、本席/候选绑定验证；旧构造调用兼容 | `e5285f0a6214056b7187773430a82e21246cabb5f6eaad228f9e800af11bd35f` |
| 4 | `dotnet/tests/Mahjong.Ai.Tests/BasicAiStrategyTests.cs` | 新增：78 项行为矩阵/元数据/取消/真实服务接线/无效枚举与 raw 契约负例 | `d038535337301a5a153e4afcd8de0bfbd088a673bb3a337a6eebbcece511aa0f` |
| 5 | `doc/v5-重构/V5-P16-BASIC-STRATEGY.md` | 本报告 | 提交后由最终回执给出（报告不自引用自身哈希） |

> 行尾说明：仓库 `core.autocrlf=true`；`AiObservation.cs` 在工作树为 CRLF、在提交 blob 中为 LF。上表为工作树字节哈希；该文件提交 blob 的 SHA-256 与最终 HEAD/commit 一并由最终回执给出。其余四个文件工作树与 blob 均为 LF，哈希一致。

无其他生产/测试/文档文件被修改或删除。构建产物与原始日志在 ignored 目录 `artifacts/p16-basic/`，不入库。

### 4.2 `BasicAiContext`：最小、强类型、不可变

```csharp
public sealed class BasicAiContext
{
    public int Seat { get; }
    public bool IsRiichi { get; }
    public TileId? LastDiscard { get; }
    public IReadOnlyList<BasicCandidateFacts> CandidateFacts { get; }
    public bool TryGetFacts(LegalActionOption candidate, out BasicCandidateFacts facts);
}
```

- 仅包含：本席座位、本人立直状态、公开最后弃牌、与合法候选绑定的显式 Kan/流局语义。**不含** `GameState`、牌山顺序、他家暗牌、seed、全位置图、时钟、权威回调对象。
- 不可变；构造时防御复制 `CandidateFacts` 列表。
- 仅允许 Kan/Other 候选携带 facts：
  - Kan 必须给出 `BasicKanKind.Closed/Open/Added`；`-1`、`3`、`999` 等未定义值在构造与 `Validate()` 边界抛 `ArgumentOutOfRangeException`；
  - Other 必须给出 facts；`AbortiveDraw = null` 表示“显式确认不是流局”，`NineTerminals` 表示九种九牌；`-1`、`1`、`999` 被拒绝；
  - 非 Kan/Other 携带 facts 直接 `ArgumentException`。
- 事实候选必须属于 context 座位；同一候选最多一条 facts。

`AiObservation` 的接入验证（`ValidateBasicContext`）：

1. `context.Seat` 必须等于 observation 座位；
2. 每条 fact 的候选必须按 `(Kind, Seat, Tile, OptionId)` 出现在该 observation 的冻结候选列表中；
3. 每个 Kan 候选必须有 `KanKind`；
4. 每个 Other 候选必须有显式流局分类；
5. 第 2 轮起与 `BasicAiStrategy.TryChoose` 共用 `BasicAiContext.ValidateCandidateList`：同一套 null/错席/重复候选/facts 归属/enum 有效性校验，不再存在 raw 入口绕过；
6. 失败抛 `ArgumentException`/`ArgumentOutOfRangeException`，不静默补齐。

既有构造调用因可选参数保持源码兼容；不传 context 时 `BasicContext == null`，Trivial/Tsumogiri 行为不变。

### 4.3 `BasicAiStrategy`

- `IAiStrategy` 实现；`AlgorithmId="baseline-basic"`、`AlgorithmVersion=1`；`ToString()` 为常量，不含 observation/手牌/牌山。
- `Decide`：入口检查取消 → 要求 `BasicContext`（缺失抛带诊断的 `BasicAiContextException`）→ `TryChoose`。
- `TryChoose`：V4 形状的可空入口，空列表返回 `null`；覆盖全部十个分支；每轮候选扫描检查 `CancellationToken`。第 2 轮起在分支前调用共享 `ValidateCandidateList`：拒绝 null/错席/重复候选、孤儿 facts、同 OptionId 但 Kind/Tile/Seat 不同的错绑定；合法列表不重排、不克隆。
- 返回输入列表中的**原实例**；`OptionId`/`Tile` 原样保留。
- 立直排序只调用既有 `HandDecomposer.CalculateWaits`；不复制另一套和牌/待牌规则。
- 纯整数 `tileDisposability`；返回的 double 分数仅用于非权威比较，`CommandIntent` 不编码分数。
- 对手牌表示做“drawn 不在 hand 才追加”的规范化，见 §3.6。
- 缺 Riichi/Discard 候选的 `Tile` 时抛 `BasicAiContextException`，不凭空造牌。

### 4.4 actor 接线边界（本任务不做）

`TableActor.ProcessAiObservation` 仍以旧构造调用创建 `AiObservation`，未传 `BasicAiContext`。因此：

- 当前真实 actor 观察上运行 `BasicAiStrategy` 会抛 `BasicAiContextException`；
- 这是刻意的 fail-closed，不是静默伪造；
- 后续接线任务的最小依赖：
  1. `riichiDeclared[seat]`（仅本席）；
  2. `state.lastDiscard`（必须是打开当前响应窗口的那张公开弃牌）；
  3. 每个 Kan 候选的 `MeldType`（CLOSED/OPEN/ADDED）；
  4. 每个 Other 候选的显式分类（九种九牌 / 非流局）；
  5. 不把牌山、他家暗牌、seed 或权威对象放入 context。
- actor 接线不属于本任务的 expectedFiles；本任务不修改 `TableActor`/Domain 枚举/DTO。

---

## 5. 测试矩阵

新增 `BasicAiStrategyTests` 共 **78 项**，均为可观察决策/合同断言，不以内部打分自洽代替行为。

### 5.1 覆盖分组

- **分支优先级**：Ron > Tsumo > 显式九种九牌 > 立直 > 暗杠 > 役牌碰 > 响应 Pass > 立直摸切 > 整数不需要度 > Pass/首合法；含“首个 Ron/首个 Tsumo/首个暗杠”的列表序。
- **立直**：`>=1000` 与 `999` 阈值、按本席分数索引、待牌种数最大、平局首个、物理副本只删一次、双副本/红五、待牌种数不读剩余牌山、选中实例与 `OptionId` 保真。
- **杠**：显式 closed 才选；open/added 不选；多个 closed 取首个；open 在无 Pass 时落到弃牌；缺 Kan facts 拒绝。
- **碰**：三元牌、自风、场风、非役风、数牌、`LastDiscard=null`；多个 Pon 取首个。
- **响应 Pass**：有 Chi/Pon/Kan/Ron 时 Pass 优先于 Discard；Pass+Discard 且无鸣牌/荣和时不提前 Pass；仅 Pass 时最终 Pass。
- **摸切/实体**：立直+摸牌才摸切；红五与普通五不互混；不立直时按不需要度；hand 已含 drawn 与 closed-only+drawn 两种表示一致。
- **不需要度**：字牌孤张（役牌/非役牌）、幺九孤张、边张、对子 -20、刻子额外 -40、同花色距离 ≤2 邻牌、同 type 另一 copy 算邻牌、首个平局；自风/场风对字牌分值的实际影响。
- **最终兜底**：无 Pass 时首个合法（含立直/开放杠）；999 分时立直分支被跳过但最终兜底仍可返回立直（V4 源码行为）。
- **取消**：空列表前取消、`Decide` 在 context 校验前取消、真实 `ReasonAsync` 在策略执行前取消。
- **typed metadata 错绑定拒绝**：context 他席、fact 候选不在列表、Kan 缺 kind、Other 缺分类、fact 放在 Discard、Kan 带 abortive、Kan 缺 kind 构造、重复 fact、他席 fact；context 列表防御复制。
- **D-1-01 无效枚举负例**：`BasicKanKind` 的 `-1/3/999`、`BasicAbortiveDrawKind` 的 `-1/1/999` 在构造边界抛 `ArgumentOutOfRangeException`；有效 `Closed/Open/Added`、`NineTerminals`、Other `null` 矩阵保持不变。
- **D-1-02 raw 入口契约负例**：错席 Ron、错席 Discard、孤儿 facts、同 OptionId 但不同 Tile/Kind/Seat 的错绑定、null 候选、重复候选；空列表仍返回 `null`，取消优先，合法候选保持原实例与 OptionId。
- **无隐藏输入**：`BasicAiContext`/`BasicCandidateFacts` 公开属性名扫描；`Mahjong.Ai` 程序集既有引用边界测试继续通过；`ToString` 恒定且不含牌数据。
- **真实服务**：`AiDecisionService.ReasonAsync` → `CommandIntent`（真实算法身份）→ `Submit` 接受 → Domain `DeadlineArbiter` 同 tick 接受；stale round/stale epoch/`deadline` 到点拒绝且无 `Submission`；`Replay` 注入不重跑策略、哈希一致、篡改/收窄候选拒绝；heuristic 不同但 `AuthorityHash` 一致。
- **旧基线回归**：不传 `BasicContext` 时 Trivial 选 Ron、Tsumogiri 选 Pass；既有 50 项全过。

### 5.2 历史失败（保留记录）

- 第 1 轮新增测试首轮出现 4 项失败：两项是测试手牌/候选列表写错导致待牌数不是预期的平局/大小关系；一项是期望实例写成了列表中的另一项；一项是异常消息断言缺少类型名。随后追加的顺序测试又有 2 项把“列表首个”写成了变量名语义上的“第一个”。这些均为**测试输入/断言编写错误**；其中异常消息补上了 `BasicAiContext` 类型名，使诊断更明确。修正后 66/66 通过。
- 第 2 轮复核发现 D-1-01/D-1-02 两组 MAJOR（真实 probe 可复现）：未定义枚举值被接受；公开 `TryChoose` 接受错席候选/孤儿 facts。修复后新增 12 项负例/有效矩阵，最终 78/78 通过。此前测试绿没有覆盖这两个反例，这是本轮的证据缺口，已保留。
- 没有为了变绿而删除/弱化任何断言、没有修改既有探针/verifier/fixture/矩阵。

### 5.3 第 2 轮缺陷修复映射

| ID | 修复位置 | 修复内容 | 验收负例/证据 |
|---|---|---|---|
| D-1-01 | `BasicCandidateFacts.Validate()`、构造器；`BasicAiStrategy` 流局分支；`BasicAiContext.ValidateCandidateList` | `KanKind` 仅允许 Closed/Open/Added，`AbortiveDraw` 仅允许 NineTerminals 或 null；非法值抛 `ArgumentOutOfRangeException`；流局分支改为 `== NineTerminals`；构造与验证边界都调用 `Validate()` | `BasicCandidateFactsRejectsUnknownKanKindValues`、`BasicCandidateFactsRejectsUnknownAbortiveDrawValues`、`BasicCandidateFactsAcceptsOnlyDefinedKanAndAbortiveValues`；probe 的 `-1/1/999` 均在构造处 REJECTED |
| D-1-02 | `BasicAiContext.ValidateCandidateList()`；`BasicAiStrategy.TryChoose`；`AiObservation.ValidateBasicContext` | raw 入口与 observation 共用同一契约：拒绝 null/错席/重复候选、孤儿 facts、同 OptionId 不同 Kind/Tile/Seat 的错绑定；空列表返回 null、取消优先、合法候选原实例保真 | `BasicTryChooseRejectsForeignSeatRon`、`BasicTryChooseRejectsForeignSeatDiscard`、`BasicTryChooseRejectsOrphanFacts`、`BasicTryChooseRejectsFactWithSameOptionIdButDifferentTile/Kind/Seat`、`BasicTryChooseRejectsNullAndDuplicateCandidates`、`BasicTryChoosePreservesTheCorrectEmptyListAndCancellationSemantics`、`BasicTryChooseReturnsTheOriginalCandidateInstance` |

复核方原 probe 已用第 2 轮产物重跑（`artifacts/p16-basic/round2-probe.log`，SHA-256 `9ca02ccc419a20d57c73201f88c5d615c5b9ac9ae8a3743a109368ce82ace7d4`）：`INVALID_ABORTIVE=-1/1/999`、`INVALID_KAN=999`、`FOREIGN_SEAT`、`GHOST_FACT` 全部 `REJECTED`。

---

## 6. 命令、计数、exit 与日志 SHA-256

全部命令 `cwd = D:/majiang-wt/ai-basic`，使用 `D:/Unity/dotnet/dotnet.exe`（8.0.426）。日志在 ignored 目录 `artifacts/p16-basic/`。

| # | 命令 | exit | 结果 | 日志 SHA-256 |
|---|---|---|---|---|
| 1 | `dotnet build dotnet/Mahjong.sln -c Release --no-incremental --nologo -v:minimal` | 0 | **0 警告 / 0 错误** | `81ea71297b3ca3c214bd059a2407f2d4f77b8f24423dd1b23e4698f61763d28a` |
| 2 | `dotnet test dotnet/tests/Mahjong.Ai.Tests/Mahjong.Ai.Tests.csproj -c Release --no-build --nologo -v:minimal` | 0 | **128 通过 / 0 失败 / 0 跳过**（原 50 + 新 78） | `e8e96b7e06ca0cf883a3af92ffb72c0f2b36e65e3cdcbf42ce4d51f7bb7ca0fb` |
| 3 | `dotnet test dotnet/tests/Mahjong.Ai.Tests/Mahjong.Ai.Tests.csproj -c Debug --nologo -v:minimal` | 0 | **128 通过 / 0 失败 / 0 跳过** | `01e85aadc0e3feed244c4575893e34b1c1e5f57b52f565f9e36e419e2dc581d7` |
| 4 | `dotnet test dotnet/tests/Mahjong.Ai.Tests/Mahjong.Ai.Tests.csproj -c Release --no-build --nologo -v:minimal -- --filter-class "Mahjong.Ai.Tests.BasicAiStrategyTests"` | 0 | **78 通过 / 0 失败 / 0 跳过** | `1f0502694470d07432d332d6499ccd4db850ec9f0638a34ef62225939ee466bb` |
| 5 | `dotnet test dotnet/tests/Mahjong.Domain.Tests/Mahjong.Domain.Tests.csproj -c Release --no-build --nologo -v:minimal` | 0 | **322 通过 / 0 失败 / 0 跳过** | `f1603f8ff8c2d20d7668c294c7cea77ed216fa04d0bd4cf73cf897466bec6497` |
| 6 | `dotnet test dotnet/tests/Mahjong.Contracts.Tests/Mahjong.Contracts.Tests.csproj -c Release --no-build --nologo -v:minimal` | 0 | **104 通过 / 0 失败 / 0 跳过** | `97fa1c31ce21fd988f72cf53e96814efa4ff3a01d0679e8abced6e34d187e3be` |
| 7 | `dotnet test dotnet/tests/Mahjong.Client.Tests/Mahjong.Client.Tests.csproj -c Release --no-build --nologo -v:minimal` | 0 | **132 通过 / 0 失败 / 0 跳过** | `f92ad806bc8c4e3ac87eaf145fb3d83fade4642b8c97b17f0420242846b6c8a4` |
| 8 | `dotnet test dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --no-build --nologo -v:minimal` | 0 | **313 通过 / 0 失败 / 0 跳过**（含 AI 观察/批量/恢复/控制时序） | `cde4ae952c19904f0625e7538de768b1e731e5527ee870e9c01fbb41af4a0e94` |
| 9 | `& 'D:/Unity/dotnet/dotnet.exe' run --project 'C:/Users/sncdc/AppData/Local/Temp/codex-basic-review-e26d5d01cb2843c6ad4dbfc9aa45ded5/Probe.csproj' -c Release` | 0 | 复核方原 probe 重跑：6 行全部 **REJECTED**（`INVALID_ABORTIVE=-1/1/999`、`INVALID_KAN=999`、`FOREIGN_SEAT`、`GHOST_FACT`） | `9ca02ccc419a20d57c73201f88c5d615c5b9ac9ae8a3743a109368ce82ace7d4` |

Release 五个测试工程合计：`128 + 322 + 104 + 132 + 313 = 999` 通过 / 0 失败 / 0 跳过。

> 第 9 行 exit 0 只表示 probe 进程完成；负例是否通过以输出逐行的 `REJECTED` 为准。复核方第 1 轮 probe 原始证据为 `artifacts/codex-basic-review/probe.log`（SHA-256 `40821EDE7F5D990149D6576296DA61DAE9A30EF4C4380C18BE0F00AED144F6D5`），本轮已用同一输入重跑并全部拒绝。

口径：

- 全部 `Total != 0`、`Failed=0`、`Skipped=0`；没有 skip、空测试、旧 DLL 凑绿；测试在最终 `--no-incremental` solution build 产物上以 `--no-build` 运行。
- `AiReplayGoldenTests` 在同一 AI 工程内随 128 项运行通过；未重写/刷新 `tests/v5-golden/ai-v1.json`。
- 未修改统一 verifier、既有探针、fixture、追踪矩阵、既有基线报告。
- 提交前另行运行 `git diff --check` / `git diff --cached --check`，无空白错误；最终 `git status --porcelain` clean（见最终回执）。

### 6.1 NOT_RUN（如实声明）

| 套件/平台 | 状态 | 理由/最小补验 |
|---|---|---|
| PostgreSQL / `Mahjong.LobbyService.Tests` / PostgresProbe / 真实 PG | **NOT_RUN** | 与纯 AI 策略无编译/行为耦合；按任务不跑大套件。补验：启动测试容器后跑 LobbyService/PG 套件。 |
| WSS / `Mahjong.Client.Wss.Tests` / 真实远端 | **NOT_RUN** | 同上；本任务未改传输。 |
| Unity Editor 导入/编译、PlayMode、IL2CPP、Android/iOS 真机、物理网络 | **NOT_RUN** | 无 Unity/IL2CPP/设备资源；仅验证 netstandard2.1/C#9 共享库编译。 |
| V4 Gradle 全量 / V4 Kotlin 差分执行 | **NOT_RUN** | 本任务以 V4 源码逐分支对照；未运行 Kotlin 侧生成式差分。 |
| actor 真实端到端（TableActor 产出 BasicAiContext → Basic → TableRuntime） | **NOT_RUN** | actor metadata 接线不属于本任务；当前 actor 观察无 context，Basic 会显式报错。 |
| 远端 CI / PR19 PR 构建 | **NOT_RUN** | 本任务不 push、不建 PR、不触发 CI；本地通过不等于远端通过。 |
| P1.6 整体、G1、G5 | **NOT_RUN / 未宣告** | 本任务只交付 Basic 组件。 |

---

## 7. 已知缺陷/限制/风险（一次性）

### 7.1 第 2 轮复核发现并修复的缺陷

- **D-1-01（MAJOR，已修复）未定义枚举值被接受。** 位置：`BasicCandidateFacts` 构造器/`Validate()`、`BasicAiStrategy` 流局分支。原因：`BasicKanKind`/`BasicAbortiveDrawKind` 是 enum，C# 允许 `(BasicKanKind)999`；旧构造器只检查 HasValue，旧流局分支用 `HasValue`。修复：显式白名单 `Closed/Open/Added` 与 `NineTerminals`，非法值抛 `ArgumentOutOfRangeException`；流局分支改 `== NineTerminals`；构造、`BasicAiContext`、`AiObservation`、`TryChoose` 都经过 `Validate()`/`ValidateCandidateList`。负例：`-1/3/999`（Kan）、`-1/1/999`（abortive）均 REJECTED；有效三 Kan、NineTerminals、Other null 保持。
- **D-1-02（MAJOR，已修复）公开 `TryChoose` 绕过本席/候选绑定。** 位置：`BasicAiStrategy.TryChoose`。原因：旧实现只检查 Kan/Other 缺 facts，未检查候选 Seat、重复/null、孤儿 facts。修复：新增 `BasicAiContext.ValidateCandidateList`，由 `AiObservation` 与 `TryChoose` 共用；拒绝错席 Ron/Discard、孤儿 facts、同 OptionId 不同 Tile/Kind/Seat、null/重复候选；空列表仍返回 null，取消优先，合法候选原实例保真。负例见 §5.3。

**修复后未发现新的已知可复现行为缺陷。** 以下为仍需在后续接线/验收中处理的限制与范围外缺口：

- **D1（范围边界，非代码缺陷）actor 未接线。** `TableActor.ProcessAiObservation` 不产出 `BasicAiContext`；真实 actor 观察上运行 Basic 会抛 `BasicAiContextException`。最小依赖见 §4.4。组件本身可由调用方显式构造 context 使用，测试已验证；但“生产 actor 端到端可用”未完成、未宣称。
- **D2（既有契约风险）手牌表示不一致。** 当前 `TableActor` 传入的 `MyHandTileIds` 是 Hand zone（含摸牌），而 V4 `myClosedTiles` 不含摸牌；既有 Trivial/Tsumogiri 代码/测试仍按 closed hand 解释。Basic 已显式规范化两种表示并通过测试；未修改既有两个策略，也不能据本任务宣称它们不受该差异影响。后续 actor 接线应统一该表示或在文档/测试中明确。
- **D3（刻意严格）metadata 不完整即拒绝。** `AiObservation`/`TryChoose` 要求每个 Kan/Other 都有显式 facts；即使更高优先级分支（如 Ron）会先返回，也不会忽略缺失 facts。这是 fail-closed，避免把 open kan 当暗杠、把任意 Other 当九种九牌；代价是投影必须完整。
- **D4（语义约定）`LastDiscard = null` 表示“没有公开最后弃牌”。** 没有额外 `HasLastDiscard` 标志；actor 必须真实填写，不能把“未提供”混同为 null。
- **D5（类型映射约定）** 九种九牌必须映射为 `Kind.Other` + `BasicAbortiveDrawKind.NineTerminals`；Kan kind 必须显式；facts 只允许 Kan/Other。若 actor 用其他 Kind 表示流局，会被拒绝。未改 Domain 枚举/DTO 来逃避该边界。
- **D6（证据口径）黄金 fixture 的历史 source hash 已过期。** `tests/v5-golden/ai-v1.json` 记录了旧 `AiObservation.cs` 的 `sourceFileSha256`；本任务按授权修改了该文件，但未改 fixture（禁止）。fixture 测试只校验 hash 长度为 64 并回放记录意图，不校验当前源文件 hash，因此回放仍通过；不得把该历史 hash 当作本交付的当前完整性证明。
- **D7（证据口径）无 V4 Kotlin 差分执行。** 行为矩阵来自 V4 源码逐分支阅读与 C# 手工/定向测试，没有运行 Kotlin `BasicAIStrategy` 对同一输入的生成式差分；V4 Gradle 全量为 `NOT_RUN`。
- **D8（覆盖边界）78 项为定向矩阵，不是穷举证明。** 未对全部手牌/候选排列做 property-based/随机搜索；`HandDecomposer` 的既有 322 项 Domain 测试继续通过，但其全部边界不由本任务重证。
- **D9（取消粒度）** 取消在每个候选扫描/每次 `CalculateWaits` 前后检查；`HandDecomposer.CalculateWaits` 单次调用内部不接收 token。14 张手牌的有界搜索很快，但这是协作式取消而非抢占式中断。
- **D10（平台）** 只验证 netstandard2.1/C#9 的 .NET 8 编译/测试；Unity、IL2CPP、Android/iOS 未验证。代码未引入 Unity/网络/时钟/RNG 依赖，但这不等于 IL2CPP 已通过。
- **D11（策略范围）** 未迁移 `TargetAwareAIStrategies`、`AIPlayer`、`AiCheatPolicy`/`cheat/*`、actor 调度、TimeoutPolicy 接线；V4 Basic 不使用的 `myFuriten`、袖牌、`lastDiscardPlayer` 等也未放入 context，这是按最小可见信息约束的刻意取舍。
- **D12（分数口径）** 立直分支返回待牌种数、弃牌分支返回整数不需要度作为 `HeuristicScore`；`CommandIntent` 不编码该分数，测试固定不同分数同哈希。调用方若把该分数用于权威判断，属于越界使用。

---

## 8. PASS / FAIL / BLOCKED / NOT_RUN 口径

| 口径 | 内容 |
|---|---|
| **PASS** | 五个 expectedFiles 的组件/测试/文档；V4 分支顺序、列表序、物理副本、红五、手牌移除一次、双风、1000 分阈值、待牌种数、取消、候选身份、metadata 绑定拒绝、无效枚举/raw 独占契约拒绝、真实 `ReasonAsync→Submit→Replay` 迟到/代次拒绝；Release solution build 0/0；Ai 128、Domain 322、Contracts 104、Client 132、TableRuntime 313。 |
| **FAIL** | 第 2 轮复核发现 D-1-01/D-1-02 两组 MAJOR（已修复，见 §7.1）；最终交付无 FAIL。历史失败还包括第 1 轮新增测试编写错误（首轮 4 项、追加顺序 2 项），已在 §5.2 如实保留；不是产品行为失败。 |
| **BLOCKED** | 本组件无 BLOCKED。actor 端到端是**后续接线任务**的依赖，不是本任务无法形成组件的阻塞；本报告不把它写成已通过。 |
| **NOT_RUN** | PG/WSS/Lobby/Unity/IL2CPP/Android/iOS/真机/物理网络/V4 Gradle/远端 CI/actor e2e/P1.6 整体/G1/G5；理由见 §6.1。 |

---

## 9. 交付与后续

- 交付方式：本工作树**本地 commit**；不转派、不 push、不建 PR、不触发 CI、不改全局环境/信任、不纳入其他工作树未验收代码。第 2 轮 D-1-01/D-1-02 修复以新的本地 commit 追加在 `ea69c84ff0dc2361f7df0c932598568665a365c9` 之上，保留第 1 轮历史，不改写旧提交。
- 提交后工作树 `clean`；最终 HEAD、commit、五个文件的最终 SHA-256 与报告自身 SHA-256 见最终回执（报告不内嵌自身提交哈希，避免自引用）。
- 独立复核由 caller 负责；唯一 PR19 仍为 `feat/v5-1-integration -> v5` 及 merge-back，由 caller 执行。
- 本任务**不宣告** P1.6 整体、G1、G5 完成；V4 仍是唯一生产基线。

---

## 附录：V4 源文件 SHA-256（基线 HEAD）

| 文件 | SHA-256 |
|---|---|
| `engine/src/main/kotlin/com/majiang/ai/AIStrategy.kt` | `ff42ea79a743542c9cc6c818a29fa3c4c0534f87cfc2154925bc6d164fb2958c` |
| `engine/src/main/kotlin/com/majiang/session/PlayerView.kt` | `2398b660f3f2ef64427b2be2b3eb6d162a3bfdfda92af0150b1df3cc3645b07d` |
| `engine/src/main/kotlin/com/majiang/state/GameAction.kt` | `94cc1eca043bb6dfd4fbe05db24d841f34e11a99686ab9add98ef878b97b243c` |
| `engine/src/main/kotlin/com/majiang/rules/HandDecomposer.kt` | `7a5a84ced89ce0cce85fa044b40ad7373374721aaf26852bd3e65b52d99c988e` |
| `engine/src/main/kotlin/com/majiang/state/LegalActionProvider.kt` | `2505c715537e15d22913189e82dd4fa2445f397163ab3b6c51774ab9a37165a0` |
