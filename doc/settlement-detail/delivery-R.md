# 交付报告：任务 R — 结算明细可读性与可核对性领域层实现 (delivery-R.md)

- **任务代号**：R (结算明细领域层与物理/数值契约)
- **开发代理**：Antigravity
- **审查与验证**：Codex
- **交付日期**：2026-09-30
- **工作区**：`D:/majiang`

---

## 1. 交付目标与完成源文件清单

本次任务在原有 343 项测试全绿基线上，为和牌结算明细（役种、表/里/赤宝牌逐指示牌命中、符数构成、基本点安全大数计算、结算上下文快照与事件隔离）补齐领域模型与服务逻辑，严格保证物理守恒、大番防溢出与订阅者深度隔离。

### 生产文件清单（独占修改，未改动测试、构建或 UI 文件）：
1. [`core/src/main/kotlin/com/majiang/rules/ScoreCalculator.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/rules/ScoreCalculator.kt)
   - 修正类注释中关于“4翻30符满贯”的历史笔误为“4翻40符以上 / 3翻70符以上”；
   - `ScoreCalculator.ScoreResult` 末尾新增字段 `val rawBasePointsExact: String = rawBasePoints.toString()`；
   - 内部公式计算引入 `java.math.BigInteger` 精确运算 `fu × 2^(2 + han)`，彻底杜绝 32-bit `Int` 位移在高番（0..80 番）时的截断、符号翻转与负数折返；
   - 旧 `rawBasePoints: Int` 保持类型与小数值语义，超过 `Int.MAX_VALUE` 时安全饱和在 `Int.MAX_VALUE` 并辅以注释说明，仅供旧接口兼容，不用于大番公式文本展示与支付计算。
2. [`core/src/main/kotlin/com/majiang/rules/WinningScoreEvaluator.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/rules/WinningScoreEvaluator.kt)
   - 顶层新增数据类 `data class DoraIndicatorHit(val indicator: Tile, val target: Tile, val tiles: List<Tile>)`；
   - `DoraBreakdown` 末尾新增默认字段 `val indicatorHits: List<DoraIndicatorHit> = emptyList()` 与 `val uraHits: List<DoraIndicatorHit> = emptyList()`；
   - `ScoreBreakdown` 末尾新增默认字段 `val uraEligible: Boolean = false`、`val isDealer: Boolean = false` 与 `val isTsumo: Boolean = false`；
   - `breakdownDora(...)` 针对每个表/里指示牌独立生成 `DoraIndicatorHit` 项（即便命中张数为 0 也保留条目，多指示牌重复计次）；`target` 采用 `Tile.fromTypeIndex(targetIndex)` 标准牌面（非赤牌）；`tiles` 保留手牌与副露中的原始物理牌（保留 `copyIndex`）；赤宝牌与表/里宝牌命中可正常叠加；手牌含荣和牌恰好一次，包含全部副露与杠子的四张物理牌；
   - `explain(...)` 严格限定 `val ura = if (context.isRiichi) uraDoraIndicators else emptyList()`；非立直状态下绝不向 `uraHits`、`uraTiles` 泄漏任何里指示牌与命中牌；`uraEligible = context.isRiichi`；`isDealer` 与 `isTsumo` 严格取自结算时刻上下文，不依赖后续局状态反推；
   - 役满逻辑遵循现有规则：役满不加宝牌番，不使用符数公式，`ScoreResult.formulaText` 明确以役满标题与基本点形式输出；手牌已公开的宝牌命中仍在 `indicatorHits` 中如实保留供展示层注明“命中但不计番”；普通役继续从全部合法拆解中筛选出得点最高的单一合法拆解，绝不混加互斥拆解役种。
3. [`core/src/main/kotlin/com/majiang/event/GameEventSnapshot.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/event/GameEventSnapshot.kt)
   - 新增 `snapshotScoreBreakdown(breakdown: ScoreBreakdown?): ScoreBreakdown?` 与 `GameEventSnapshot.snapshotBreakdown(...)`；
   - 对 `ScoreBreakdown` 内部所有嵌套集合实施全量深拷贝并使用 `Collections.unmodifiableList` 包装（包括：`yakuLines`、`paymentLines`、`fu.lines`、`dora.indicatorTiles`、`dora.uraTiles`、`dora.redTiles`、`dora.indicatorHits` 及其中每个 hit 的 `tiles`、`dora.uraHits` 及其中每个 hit 的 `tiles`）；
   - `snapshotGameEvent(...)` 中 `GameEvent.TsumoWin` 与 `GameEvent.RonWin` 接入 `snapshotScoreBreakdown`，保证事件总线与会话订阅者既不能修改原列表，也不能被外部恶意变更污染，且领域事件不反向依赖会话包。

---

## 2. 新增公共 API 契约一览

供并行任务 U (`SettlementDetailPresenter.kt` / `GameScreen.kt`) 与外部测试直接调用：

```kotlin
package com.majiang.rules

// com.majiang.rules，WinningScoreEvaluator.kt 顶层
data class DoraIndicatorHit(val indicator: Tile, val target: Tile, val tiles: List<Tile>)

// DoraBreakdown 末尾新增默认字段：
data class DoraBreakdown(
    val indicatorCount: Int,
    val uraCount: Int = 0,
    val redCount: Int,
    val totalHan: Int = indicatorCount + uraCount + redCount,
    val indicatorTiles: List<Tile> = emptyList(),
    val uraTiles: List<Tile> = emptyList(),
    val redTiles: List<Tile> = emptyList(),
    val indicatorHits: List<DoraIndicatorHit> = emptyList(),
    val uraHits: List<DoraIndicatorHit> = emptyList()
)

// ScoreBreakdown 末尾新增默认字段：
data class ScoreBreakdown(
    val yakuLines: List<Pair<String, Int>>,
    val yakumanCount: Int,
    val dora: DoraBreakdown,
    val fu: FuBreakdown,
    val score: ScoreCalculator.ScoreResult,
    val paymentLines: List<String>,
    val honbaAddition: Int,
    val riichiAddition: Int,
    val totalPoints: Int,
    val uraEligible: Boolean = false,
    val isDealer: Boolean = false,
    val isTsumo: Boolean = false
)

// ScoreCalculator.ScoreResult 末尾新增默认字段：
data class ScoreResult(
    val fu: Int,
    val han: Int,
    val basePoints: Int,
    val title: String,
    val isYakuman: Boolean,
    val yakumanMultiplier: Int,
    val rawBasePoints: Int = basePoints,
    val capReason: String? = null,
    val formulaText: String = "",
    val rawBasePointsExact: String = rawBasePoints.toString()
)
```

---

## 3. 边界与设计决策对齐

1. **逐指示牌命中与零命中保留**：
   - 遍历传入的 `doraIndicators` 与 `ura` 指示牌时，每个指示牌都会映射到其顺位目标 `doraIndicatorTarget()`；
   - 即使手牌中目标牌为 0 张，也会生成对应的 `DoraIndicatorHit(indicator, target, emptyList())`，供 UI 层在宝牌标签页逐行展示“指示牌 → 宝牌：命中 0 张”；
   - 若场上出现重复指示牌（例如两张东风作为指示牌），将生成两个独立的 `DoraIndicatorHit`，命中牌独立计次并累计翻数。
2. **手牌与副露牌物理真实性**：
   - 统计牌源由 `evalHand.allClosedTiles + evalHand.melds.flatMap { it.tiles }` 构成；
   - 自摸时 `drawnTile` 包含在暗牌中；荣和时由 `prepareEvaluationHand` 补齐荣和牌（且仅补齐一次，若手牌模 3 为 2 则不重复添加）；
   - 副露暗杠/明杠/加杠中的 4 张物理牌全量参与宝牌命中统计；
   - 命中的牌对象完整保留原始 `copyIndex`（包括 `isRedFive = true` 的数牌 5 物理副本 3）。
3. **里宝牌信息屏障**：
   - 在 `WinningScoreEvaluator.explain` 与 `evaluate` 中，里宝牌指示牌生效的唯一前置条件为 `context.isRiichi == true`；
   - 若未立直，`ura` 集合被强制置为空列表，`doraBreakdown.uraHits` 与 `doraBreakdown.uraTiles` 必然为空列表，`doraBreakdown.uraCount == 0`，`ScoreBreakdown.uraEligible == false`；绝不在任何快照、事件或解释结构中泄漏隐藏的里宝牌信息。
4. **大番安全与封顶**：
   - 当 `han` 达到 30、50、80 等极端番数时，`BigInteger.pow(2 + han)` 计算绝对精确，输出纯整数字符串 `rawBasePointsExact`，`formulaText` 格式形如 `30符 × 2^(2+80) = <精确大数> → 数え役満 8000`；
   - `rawBasePoints` 被安全限制在 `[0, Int.MAX_VALUE]`，避免溢出为负数破坏既有依赖；
   - 满贯阶梯判断严格基于日麻规则封顶（13 翻以上数え役満 8000 基本点；役满 8000 × N 基本点），规则配置未作变更。
5. **事件快照深层不可变性**：
   - `GameEventSnapshot.snapshotScoreBreakdown` 递归处理所有 `List`，使用 `ArrayList` 新建容器后包装 `Collections.unmodifiableList`；
   - 外部监听器试图调用 `.add()` 或 `.clear()` 会直接触发 `UnsupportedOperationException`；若上游持有者修改原列表，快照亦不受影响。

---

## 4. 与设计契约的偏差

- **偏差**：**无**。严格遵守全部接口定义、默认参数与物理守恒约定。

---

## 5. 验证与构建说明

- 本人（Antigravity）在本次会话中**未执行任何 shell、终端、Gradle、构建命令或 MCP 调用**，严格遵循无头会话仅使用内置文件读写/编辑工具的约定；
- 未修改任何测试文件、未修改构建脚本或依赖；
- 全部编译构建、测试用例回归与审查由 Codex 统一执行与判定。
