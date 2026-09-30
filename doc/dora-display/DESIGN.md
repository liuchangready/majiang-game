# 宝牌与赤宝牌：闪光特效展示 + 赤宝牌计翻接入

Claude 设计与检视；Antigravity 开发。遵循 [architecture-v2/DESIGN.md](../architecture-v2/DESIGN.md) 契约与 [meld-display](../meld-display/DESIGN.md) 的表现层模式。

## 目标

1. **宝牌闪光特效**：当前表宝牌（含杠宝牌指示）在**本人手牌（含摸牌）、本人副露、四家牌河**中以脉动金色闪光标识；**里宝牌在立直和牌揭示前绝不发光**。
2. **赤宝牌（红五）完整接入**（雀魂/关东标准）：赤五万/赤五筒/赤五索各一张，牌面全红渲染 + 持续闪光；**每张计 1 翻**，与指示宝牌翻数**叠加**，不计为起和役。
3. 单批次渲染架构（TileDrawBuffer）与既有 273+ 测试基线不回归。

## 领域层约定（最小侵入）

- `Tile` 增加**派生属性**（不改构造参数，equals/hash/TilePool 守恒零影响）：
  ```kotlin
  /** 赤宝牌：关东三张制——每种数牌 5 的 copyIndex==3 物理副本为红五 */
  val Tile.isRedFive: Boolean
      get() = rank == 5 && copyIndex == 3 &&
              (suit == Suit.MAN || suit == Suit.PIN || suit == Suit.SOU)
  ```
  - 136 张集合结构不变（红五是"第 4 张五"的原地标识，非新增牌）。
  - 全局恰好 3 张（每数牌花色 1 张），有单测断言。

## 计分接入

- 扩展点：`rules/WinningScoreEvaluator.kt` 现有 `countDora(hand, doraIndicators, uraDoraIndicators)`。
- 规则：
  - 现有逻辑保持：每张指示牌对手牌+副露中目标牌计数（红五被指示为五时**照常计入**）。
  - 新增：`+ allTiles.count { it.isRedFive }`（红五每张独立 +1，与指示命中叠加——指示 5 时该红五共 2 翻，标准规则）。
  - 副露中的红五同样计数。
  - 赤宝牌不构成起和役（extraHan 通道，与现有 Dora 一致）。
- 结算展示：若现有结算面板已分列 DORA 翻数，并列展示 `RED DORA n`；若未分列，在番数明细中新增一行 `DORA +n / RED DORA +n`（开发时核查现状，验收时检查）。

## 展示层设计

### 闪光视觉（2026-09-29 V2：提高可见度）
- **整牌金色辉光叠加（主视觉信号）**：overlay 覆盖层 `FilledRectOp` 覆盖整个牌矩形，金色，透明度 = `0.30 × 脉动相位`，随呼吸在约 0.045–0.30 间起伏——整张牌泛金光。
- **四层嵌套光环**：`LineRectOp` 由内向外偏移 0 / 1.5 / 3 / 4.5 px，透明度按 0.95 / 0.70 / 0.45 / 0.25 × 相位递减，形成柔和光晕。
- **右上角十字闪光星**：两道 22×6 金色光臂 + 8×8 近白星核（overlay 层），同相位脉动，星心位于牌右上角内侧。
- **脉动振幅**：`sparkleAlpha` 值域 [0.15, 1.0]（V1 为 [0.35, 1.0]，实际观感不明显，已加强）。
- **TileDrawBuffer 新增 overlay 桶**：filled→【batch：纹理+文字】→overlay→line 四段提交；SpriteBatch 每帧仍恰好一次（单批次原则不变）。
- ⚠️ ShapeRenderer 半透明需先 `Gdx.gl.glEnable(GL20.GL_BLEND)`（flush 内已处理，含 headless 空守卫）。
- 交互高亮（悬停/摸牌/被鸣/加杠）优先于**光环**（抑制之）；整牌辉光与闪光星始终保留。

### 红五渲染
- `WikipediaSkin`：新增 3 张红五纹理映射 `man5r.png / pin5r.png / sou5r.png`（已下载至 `assets/skins/wikipedia/`，同系列风格，授权见 CREDITS.md）。`getTileFileName(tile)` 对 `tile.isRedFive` 返回红五文件名。
- `ClassicSkin`：红五渲染为红色 `0m / 0p / 0s`（日麻标准记法，0 表示赤五）。

### 应用范围
| 位置 | 表宝牌闪光 | 红五渲染+持续闪光 |
|:---|:---|:---|
| 本人暗牌 + 摸牌 | ✅ | ✅ |
| 本人副露（MeldDisplayLayout 输出） | ✅ | ✅ |
| 四家牌河 | ✅ | ✅ |
| HUD 宝牌指示牌区 | 现状保持 | — |
| 里宝牌 | **揭示前不发光**（展示计算只吃 `view.doraIndicators`，天然排除） | — |

## 固定 API（便于独立测试）

```kotlin
// model/Tile.kt（派生属性，无状态）
val Tile.isRedFive: Boolean

// rules/WinningScoreEvaluator.kt
fun countDora(hand, doraIndicators, uraDoraIndicators): Int   // 现签名不变，返回值含红五
data class DoraBreakdown(val indicatorCount: Int, val redCount: Int) // 新增：明细（countDora 委托它实现）
fun breakdownDora(hand, doraIndicators, uraDoraIndicators): DoraBreakdown

// render/DoraSparkle.kt（纯函数，headless 可测）
data class DoraHighlight(val doraTypeIndices: Set<Int>, val redFive: Boolean /* 是否含红五集合*/)
object DoraSparkle {
    /** 由可见指示牌（不含里宝牌）计算高亮目标：复用 Tile.doraIndicatorTarget() */
    fun computeVisibleTargets(doraIndicators: List<Tile>): Set<Int>
    /** 脉动相位 alpha ∈ [0.35, 1.0]，周期 1.2s，纯函数确定性 */
    fun sparkleAlpha(timeSeconds: Float): Float
}
```

## 测试要求（Claude 检视基线）

1. `isRedFive`：全 136 张中恰好 3 张；每数牌花色各 1 张且 copyIndex==3；字牌/非 5 永假。
2. `breakdownDora`/`countDora`：指示 5 时红五计 2 翻（叠加）；无指示时红五计 1；副露红五计数；里宝牌仅立直传入时计入（沿用现状）。
3. `DoraSparkle.computeVisibleTargets`：3万→4万、9万→1万循环、风牌东南西北循环、三元白发中循环（复用既有 `doraIndicatorTarget` 则天然满足，仍需覆盖）；**输入只应来自表指示牌**。
4. `sparkleAlpha`：值域、周期、确定性（同 t 同值）。
5. 皮肤映射：红五 3 文件名映射 + Pixmap 资源健全性 + 联系表含红五（扩展既有 WikipediaSkinPreviewTest/MappingTest）。
6. 全量回归：现有全部测试（~273+）保持通过。

## 范围外（本次不做）

- AI 对赤宝牌的价值偏好（留待 AI 迭代）
- 里宝牌揭示演出（结算文案已有数据）
- 杠宝牌指示揭示流程改动（若当前 FSM 已发杠指示则展示自动跟随 `doraIndicators`）
- 三人麻将拔北宝牌
