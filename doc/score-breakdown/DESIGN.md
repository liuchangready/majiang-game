# 算番透明化：结算完整推导链（役种 / 三类宝牌 / 符数构成 / 基本点公式 / 支付分配）

Claude 设计与检视；Antigravity 开发。遵循 [architecture-v2/DESIGN.md](../architecture-v2/DESIGN.md)。

## 目标

和牌结算时完整展示**可核对的计算方法**，杜绝"黑盒给点数"：

1. **所有命中的役种**逐项列出（名称 + 翻数，役满特殊标注），并注明已自动选择最优拆解；
2. **宝牌三类分列**：表宝牌 / 里宝牌 / 赤宝牌各自张数 + **命中的具体牌**（tile 列表）；
3. **符数构成表**：底符 + 各面子符 + 雀头符 + 听牌符 + 自摸加符 + 副露符 → 切上取整 →（荣和 30 符保底）；
4. **基本点公式**：`基本点 = 符 × 2^(翻+2)`，展示封顶前原值与封顶原因（满贯/跳满/倍满/三倍满/役满阶梯）；
5. **支付分配**：谁付谁多少（自摸分庄闲 / 荣和单付），含本场棒与立直棒加算。

## 领域层扩展（全部为**增量**，不破坏现有 320 项测试）

### 1. 符数构成 — `rules/FuCalculator.kt`
```kotlin
data class FuBreakdown(
    val lines: List<FuLine>,        // (说明, 符)：底符20 / 各面子("暗刻 8s ×8") / 雀头 / 听牌 / 自摸+2 / 副露
    val subtotal: Int,              // 切上前小计
    val roundedUpTo: Int,           // 切上到 10 的倍数后
    val finalFu: Int,               // 最终符（含荣和 30 符保底规则时注明）
    val specialNote: String?        // "平和自摸 20符" / "七对子 25符" / "荣和 30符保底" 等
)
fun calculateBreakdown(...): FuBreakdown   // 现有 calculate 委托之，行为不变
```

### 2. 宝牌三类明细 — `rules/WinningScoreEvaluator.kt`
`DoraBreakdown` 扩展：
```kotlin
data class DoraBreakdown(
    val indicatorCount: Int,        // 表宝牌命中张数（不含里）
    val uraCount: Int,              // 里宝牌命中张数（仅立直时非零）
    val redCount: Int,              // 赤宝牌张数
    val totalHan: Int,              // = indicator + ura + red
    val indicatorTiles: List<Tile>, // 命中的具体牌（表）
    val uraTiles: List<Tile>,       // 命中的具体牌（里）
    val redTiles: List<Tile>        // 命中的红五
)
```
现有 `countDora` 委托新实现，返回值语义不变（总数）。

### 3. 基本点推导 — `rules/ScoreCalculator.kt`
`ScoreResult` 增加：`rawBasePoints`（封顶前）、`capReason: String?`（"満貫封頂" / "跳滿" / … / null）、`formulaText`（"40符 × 2^(2+4) = 2560 → 封顶 2000"）。

### 4. 汇总结构 — `rules/WinningScoreEvaluator.kt`
```kotlin
data class ScoreBreakdown(
    val yakuLines: List<Pair<String, Int>>,   // (役名, 翻)，最优拆解下全部命中的役
    val yakumanCount: Int,
    val dora: DoraBreakdown,
    val fu: FuBreakdown,
    val score: ScoreCalculator.ScoreResult,
    val paymentLines: List<String>,           // "自摸：庄家付 2000、闲家各付 1000" / "荣和：P2 支付 8000"
    val honbaAddition: Int, val riichiAddition: Int,
    val totalPoints: Int
)
fun explain(hand, context, doraIndicators, uraDoraIndicators, ...): ScoreBreakdown
```

### 5. 事件携带 — `event/GameEvent.kt`
`TsumoWin` / `RonWin` 增加可选字段 `breakdown: ScoreBreakdown? = null`（向后兼容，构造处由 GameStateMachine 和牌路径填充；SettlementService 提供支付行文本）。

## 表现层 — GameScreen 结算弹窗改版

弹窗高度沿用，内容分区紧凑排版（字号降至 0.9~1.0，行距 18px，必要时两栏）：

```
WINNER: P0 [YOU]          TSUMO
Yaku: 立直 1翻 | 断幺九 1翻 | 一发 1翻      ← 所有命中役种
  (已自动选择最优拆解)
Dora: 表 DORA 5m,5m,9p (+3) | 里 DORA 1s (+1) | 赤 RED 0p (+1)
Fu:   底符20 + 暗刻8s×8 + 明刻中×4 + 听牌嵌张2 + 自摸2 = 36 → 切上40
      4翻40符
Base: 40 × 2^(2+4) = 2560 → 満貫封頂 2000
Pay:  自摸 庄家付4000 闲家各付2000  (+本场300×3 +立直棒1000×2)
      TOTAL 11600
```
- 无宝牌/特殊符数路径：省略对应行（不留空行）；役满走役满倍数行；
- 弹窗放不下时优先保证 Fu/Base/Pay 可读，役种行可并排压缩；
- 断线/旧回放无 breakdown 字段时回退现有精简显示。

## 测试要求（Claude 检视基线）

1. **符数构成**：既有符数用例全部改为断言 `calculateBreakdown().finalFu == 旧值`（等价回归），并新增：各构成行之和 == 小计、切上正确、七对 25 笔记、荣和 30 符保底注记；
2. **宝牌三类**：表+里+赤分列计数；里仅立直传入时计入；命中的具体牌列表正确（含副露中的）；`totalHan` 与旧 `countDora` 全等（等价回归）；
3. **基本点链**：rawBasePoints 公式精确、封顶阶梯各档（満貫/跳滿/倍滿/三倍満/役满）注记、paymentLines 与 `ScoreResult` 各支付函数输出一致；
4. **事件携带**：TsumoWin/RonWin 的 breakdown 字段在自摸/荣和两条路径填充完整；旧构造（不传）编译兼容；
5. **UI 冒烟**：结算弹窗无 breakdown 回退正常、有 breakdown 渲染不越界不崩溃（headless 契约测试：文本行组装函数为纯函数并单测）；
6. **全量回归**：320 项基线全部保持通过。

## 范围外

- 多个拆解候选的并列展示（只注明"已选最优"）
- 流局听牌罚符的推导展示（保持现状）
- 结算弹窗滚动条（用紧凑排版解决）
