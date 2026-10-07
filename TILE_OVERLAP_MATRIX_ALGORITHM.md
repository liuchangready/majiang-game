# 🀄 千术麻将 — 牌型重叠度矩阵算法设计说明书
(Tile Pattern Overlap Matrix Algorithm Specification)

---

## 目录
1. [背景与设计定位](#一-背景与设计定位)
2. [34 维牌型需求向量空间建模](#二-34-维牌型需求向量空间建模)
3. [重叠度公式与典型牌型交叉矩阵表](#三-重叠度公式与典型牌型交叉矩阵表)
4. [多人博弈定向衰减机制](#四-多人博弈定向衰减机制)
5. [物理牌池硬约束与安全余量](#五-物理牌池硬约束与安全余量)
6. [完整 Kotlin 算法实现](#六-完整-kotlin-算法实现)
7. [场景算例与单元测试验证](#七-场景算例与单元测试验证)

---

## 一、 背景与设计定位

### 1. 传统机制的痛点（全局粗暴衰减）
在早期的多人出千模型中，通常采用简单的“人数衰减系数”：
$$\text{Decay} = \frac{1}{1 + \lambda (M - 1)}$$
只要场上有其他人出千，所有人的成功率均被同等削弱。这种机制存在明显的**逻辑荒谬性**：
- **玩家 A** 想要做**“万子清一色”**；
- **玩家 B** 想要做**“条子清一色”**。

在物理麻将中，万子与条子是完全相互独立的牌种集合（万子有 36 张，条子也有 36 张），两人在物理资源上**毫无交集**。强制削弱双方会导致游戏失去逻辑自洽性与策略趣味。

### 2. 本算法的核心定位
通过将所有麻将千术目标投影到 **34 维离散特征空间**，利用**向量内积（余弦相似度）**构建对局中所有出千玩家的**重叠度冲突矩阵（Overlap Matrix）**：
- **正交独立（Overlap = 0）**：互不干涉，双方均享受完整的技能收益；
- **完全冲突（Overlap = 1.0）**：剧烈踩踏，熟练度与牌库资源同时均分稀释；
- **部分重叠（0 < Overlap < 1.0）**：按重叠比例精准计算定向干扰。

---

## 二、 34 维牌型需求向量空间建模

一副标准麻将共有 34 种牌型（每种 4 张，共 136 张）。我们将牌型需求定义为一个 34 维实数向量 $\vec{D} \in \mathbb{R}^{34}$：

$$
\vec{D} = [w_0, w_1, \dots, w_8 \mid w_9, \dots, w_{17} \mid w_{18}, \dots, w_{26} \mid w_{27}, \dots, w_{33}]
$$

- 维度 $0 \sim 8$：**万子（1万 ~ 9万）**
- 维度 $9 \sim 17$：**条子（1条 ~ 9条）**
- 维度 $18 \sim 26$：**筒子（1筒 ~ 9筒）**
- 维度 $27 \sim 33$：**字牌（东、南、西、北、中、发、白）**

### 常见牌型的标准化需求向量定义
需求向量满足归一化条件 $\|\vec{D}\|_2 = 1$ 或 $\sum w_i = 1$。以下采用**模长归一化（Unit Vector）**以便于计算余弦相似度：

| 牌型方案 | 关注区间与权重分布 | 模长归一化系数 |
|---|---|---|
| **清一色（万）** | $w_0 \sim w_8 = 1$，其余为 0 | 乘以 $\frac{1}{\sqrt{9}} = \frac{1}{3}$ |
| **清一色（条）** | $w_9 \sim w_{17} = 1$，其余为 0 | 乘以 $\frac{1}{\sqrt{9}} = \frac{1}{3}$ |
| **混一色（万+字）** | $w_0 \sim w_8 = 1, w_{27} \sim w_{33} = 1$（共 16 种） | 乘以 $\frac{1}{\sqrt{16}} = 0.25$ |
| **字一色** | $w_{27} \sim w_{33} = 1$（共 7 种字牌），其余为 0 | 乘以 $\frac{1}{\sqrt{7}} \approx 0.378$ |
| **十三幺** | 1万、9万、1条、9条、1筒、9筒 及 7 张字牌（共 13 种牌各为 1） | 乘以 $\frac{1}{\sqrt{13}} \approx 0.277$ |
| **断幺九** | 万/条/筒的 2~8（共 $7 \times 3 = 21$ 种牌各为 1） | 乘以 $\frac{1}{\sqrt{21}} \approx 0.218$ |
| **单张听牌（如 8万）** | 仅 $w_7 = 1$，其余 33 维全部为 0 | 模长天然为 $1.0$ |

---

## 三、 重叠度公式与典型牌型交叉矩阵表

### 1. 冲突系数计算公式（余弦相似度）
对于玩家 $A$ 的需求向量 $\vec{D}_A$ 与玩家 $B$ 的需求向量 $\vec{D}_B$：

$$
\text{Overlap}(A, B) = \frac{\vec{D}_A \cdot \vec{D}_B}{\|\vec{D}_A\|_2 \cdot \|\vec{D}_B\|_2} = \sum_{k=0}^{33} \hat{D}_{A, k} \cdot \hat{D}_{B, k} \quad \in [0.0, 1.0]
$$

### 2. 典型牌型交叉重叠度矩阵速查表

下表为 7 种经典牌型两两之间的精确冲突系数 $\text{Overlap}(Row, Col)$：

| 牌型 | 清一色(万) | 清一色(条) | 混一色(万) | 十三幺 | 断幺九 | 字一色 | 听8万 |
|---|:---:|:---:|:---:|:---:|:---:|:---:|:---:|
| **清一色(万)** | **1.000** | 0.000 | 0.750 | 0.185 | 0.577 | 0.000 | 0.333 |
| **清一色(条)** | 0.000 | **1.000** | 0.000 | 0.185 | 0.577 | 0.000 | 0.000 |
| **混一色(万)** | 0.750 | 0.000 | **1.000** | 0.625 | 0.433 | 0.661 | 0.250 |
| **十三幺** | 0.185 | 0.185 | 0.625 | **1.000** | 0.000 | 0.734 | 0.000 |
| **断幺九** | 0.577 | 0.577 | 0.433 | 0.000 | **1.000** | 0.000 | 0.218 |
| **字一色** | 0.000 | 0.000 | 0.661 | 0.734 | 0.000 | **1.000** | 0.000 |
| **听8万** | 0.333 | 0.000 | 0.250 | 0.000 | 0.218 | 0.000 | **1.000** |

#### 表项解读与博弈体现：
1. **清一色(万) vs 清一色(条) = 0.000**：两者正交，零干扰，双方均无损耗。
2. **十三幺 vs 断幺九 = 0.000**：十三幺只要 1、9 与字牌，断幺九只要 2~8 中张，两者无任何重叠，即使两人同时出千，也完全互不影响！
3. **字一色 vs 十三幺 = 0.734**：十三幺包含全部 7 种字牌，与字一色高度重叠，两人会在东、南、西、北、中、发、白上产生激烈抢夺。
4. **清一色(万) vs 十三幺 = 0.185**：仅在 1万 和 9万 两张牌上产生竞争，因此只有轻微干扰（约 18.5%）。

---

## 四、 多人博弈定向衰减机制

设牌桌共有 $N$ 名玩家，出千配置为 $\{ (\text{Player}_i, S_i, \vec{D}_i) \}$，其中 $S_i \in [0.0, 1.0]$ 为其基础千术熟练度。

### 1. 定向干扰总值（Targeted Interference）
对玩家 $i$ 而言，其他人对其产生的有效干扰为所有人干扰在该玩家需求方向上的投影总和：

$$
\text{Interference}_i = \sum_{j \neq i} S_j \times \text{Overlap}(i, j)
$$

### 2. 有效熟练度衰减模型
$$
S'_i = S_i \times \frac{1}{1 + \lambda \times \text{Interference}_i} \quad (\lambda \in [0.5, 0.8], \text{默认取 } 0.65)
$$

- 若无人与玩家 $i$ 争抢同类牌（$\text{Interference}_i = 0$）：$S'_i = S_i$（无衰减）；
- 若有另 1 名高玩（$S_j = 1.0$）与玩家 $i$ 做**相同清一色**（$\text{Overlap} = 1.0$）：
  $$S'_i = \frac{S_i}{1 + 0.65 \times 1.0} \approx 0.606 \times S_i$$
- 若有 3 人同时竞争相同花色：$S'_i \approx 0.435 \times S_i$。

---

## 五、 物理牌池硬约束与安全余量

即使出千判定成功，真实牌池中的**物理张数仍然是刚性约束**：

```
[总牌池 Pool: 136张]
  ├── 每种牌限量 4 张 (如一万最多 4 张)
  ├── 每花色限量 36 张 (序数牌)
  └── 字牌限量 28 张
```

### 1. 牌山安全保留底线（Safe Reserve）
为了防止出千将某一花色在起手发牌阶段彻底抽空（导致开局后牌山无牌可摸），系统设定安全阈值：
- **花色安全保留**：$\text{Reserve}_{\text{suit}} \ge 10$ 张（每花色最多允许被老千起手拿走 $36 - 10 = 26$ 张）；
- **单种牌安全保留**：$\text{Reserve}_{\text{single}} \ge 1$ 张（最多允许起手拿走 3 张，必须留至少 1 张在牌山中供打牌自摸）。

### 2. 轮转交替争抢（Round-Robin Draft）
当 $M$ 个出千者发生牌型冲突时，采用**轮转机制交替从真实池中抽取**，绝不允许座位靠前的玩家一次性拿光所有目标牌，保障博弈的公平与戏剧性。

---

## 六、 完整 Kotlin 算法实现

实现文件位于：`com.majiang.algo.TileOverlapMatrixEngine.kt`

```kotlin
package com.majiang.algo

import kotlin.math.sqrt
import kotlin.random.Random

/**
 * 34 维离散牌型需求向量与相似度计算器
 */
class TileDemandVector private constructor(val rawWeights: FloatArray) {

    // 模长归一化后的单位向量
    val normalizedWeights = FloatArray(34)

    init {
        var normSq = 0f
        for (w in rawWeights) normSq += w * w
        val norm = sqrt(normSq.toDouble()).toFloat()
        if (norm > 0f) {
            for (i in 0 until 34) normalizedWeights[i] = rawWeights[i] / norm
        }
    }

    /**
     * 计算与另一个向量的余弦重叠度 [0.0, 1.0]
     */
    fun overlapWith(other: TileDemandVector): Float {
        var dot = 0f
        for (i in 0 until 34) {
            dot += this.normalizedWeights[i] * other.normalizedWeights[i]
        }
        return dot.coerceIn(0f, 1f)
    }

    /**
     * 获取指定麻将牌与本画像的亲和度 [0.0, 1.0]
     */
    fun affinityOf(tile: Tile): Float {
        val idx = tileToIndex(tile)
        return normalizedWeights[idx]
    }

    companion object {
        fun tileToIndex(tile: Tile): Int {
            return when (tile.suit) {
                Suit.WAN -> tile.rank - 1
                Suit.TIAO -> 9 + (tile.rank - 1)
                Suit.TONG -> 18 + (tile.rank - 1)
                Suit.ZIPAI -> 27 + (tile.rank - 1).coerceIn(0, 6)
            }
        }

        /** 清一色向量构建 */
        fun ofFlush(suit: Suit): TileDemandVector {
            val arr = FloatArray(34)
            val start = when (suit) {
                Suit.WAN -> 0; Suit.TIAO -> 9; Suit.TONG -> 18; Suit.ZIPAI -> 27
            }
            val count = if (suit == Suit.ZIPAI) 7 else 9
            for (i in 0 until count) arr[start + i] = 1f
            return TileDemandVector(arr)
        }

        /** 混一色向量构建 (序数牌 + 字牌) */
        fun ofHalfFlush(suit: Suit): TileDemandVector {
            val arr = FloatArray(34)
            val start = when (suit) { Suit.WAN -> 0; Suit.TIAO -> 9; Suit.TONG -> 18; else -> 0 }
            for (i in 0 until 9) arr[start + i] = 1f
            for (i in 27 until 34) arr[i] = 1f
            return TileDemandVector(arr)
        }

        /** 十三幺向量构建 (1,9与7字牌) */
        fun ofThirteenOrphans(): TileDemandVector {
            val arr = FloatArray(34)
            val orphans = listOf(0, 8, 9, 17, 18, 26, 27, 28, 29, 30, 31, 32, 33)
            for (idx in orphans) arr[idx] = 1f
            return TileDemandVector(arr)
        }

        /** 断幺九向量构建 (2~8序数牌) */
        fun ofAllSimples(): TileDemandVector {
            val arr = FloatArray(34)
            for (suitOffset in listOf(0, 9, 18)) {
                for (r in 1..7) arr[suitOffset + r] = 1f // rank 2~8 对应下标 1~7
            }
            return TileDemandVector(arr)
        }

        /** 字一色向量构建 (纯字牌) */
        fun ofAllHonors(): TileDemandVector {
            val arr = FloatArray(34)
            for (i in 27 until 34) arr[i] = 1f
            return TileDemandVector(arr)
        }

        /** 指定单张牌向量构建 (精确单吊/自摸) */
        fun ofSpecificTile(suit: Suit, rank: Int): TileDemandVector {
            val arr = FloatArray(34)
            val dummy = Tile(0, suit, rank)
            arr[tileToIndex(dummy)] = 1f
            return TileDemandVector(arr)
        }
    }
}

/**
 * 玩家出千决策与向量封装
 */
data class PlayerCheatProfile(
    val playerIndex: Int,
    val skill: Float, // 基础熟练度 [0.0, 1.0]
    val demandVector: TileDemandVector
)

/**
 * 重叠度矩阵计算与多千术自适应发牌引擎
 */
object TileOverlapMatrixEngine {

    const val LAMBDA = 0.65f // 冲突衰减敏感系数

    /**
     * 计算 N 名玩家的两两冲突矩阵 C[N][N]
     */
    fun computeOverlapMatrix(profiles: List<PlayerCheatProfile>): Array<FloatArray> {
        val n = profiles.size
        val matrix = Array(n) { FloatArray(n) }

        for (i in 0 until n) {
            matrix[i][i] = 1.0f
            for (j in i + 1 until n) {
                val overlap = profiles[i].demandVector.overlapWith(profiles[j].demandVector)
                matrix[i][j] = overlap
                matrix[j][i] = overlap
            }
        }
        return matrix
    }

    /**
     * 计算每位玩家经由重叠度修正后的实际有效熟练度 S'
     */
    fun computeEffectiveSkills(
        profiles: List<PlayerCheatProfile>,
        overlapMatrix: Array<FloatArray>
    ): Map<Int, Float> {
        val n = profiles.size
        val result = mutableMapOf<Int, Float>()

        for (i in 0 until n) {
            var interference = 0f
            for (j in 0 until n) {
                if (i != j) {
                    interference += profiles[j].skill * overlapMatrix[i][j]
                }
            }
            val decay = 1.0f / (1.0f + LAMBDA * interference)
            val effectiveSkill = (profiles[i].skill * decay).coerceIn(0.05f, 1.0f)
            result[profiles[i].playerIndex] = effectiveSkill
        }
        return result
    }

    /**
     * 基于重叠矩阵的物理发牌码牌执行入口
     */
    fun stackWallWithMatrix(
        profiles: List<PlayerCheatProfile>,
        pool: MutableList<Tile>
    ): Array<Tile?> {
        val wall = arrayOfNulls<Tile>(136)
        val matrix = computeOverlapMatrix(profiles)
        val effectiveSkills = computeEffectiveSkills(profiles, matrix)
        val initialSlotsMap = WallStackingEngine.calculateInitialSlotIndices()

        // 轮转抓牌（交替为各老千码牌，防先手垄断）
        for (round in 0 until 14) {
            for (profile in profiles) {
                val pIdx = profile.playerIndex
                val slots = initialSlotsMap[pIdx] ?: continue
                if (round >= slots.size) continue
                val slot = slots[round]
                if (wall[slot] != null) continue

                val effSkill = effectiveSkills[pIdx] ?: 0.5f

                // 按有效熟练度判定出千成功率
                if (Random.nextFloat() <= effSkill) {
                    // 从真实池中挑选与需求向量亲和度最高的可用牌
                    val bestTile = pool.filter { profile.demandVector.affinityOf(it) > 0f }
                                       .maxByOrNull { profile.demandVector.affinityOf(it) }
                    if (bestTile != null) {
                        wall[slot] = bestTile
                        pool.remove(bestTile)
                    }
                }
            }
        }
        return wall
    }
}
```

---

## 七、 场景算例与单元测试验证

### 测试用例源码

位于：`core/src/test/kotlin/com/majiang/algo/TileOverlapMatrixTest.kt`

```kotlin
package com.majiang.algo

import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class TileOverlapMatrixTest {

    @Test
    fun testMathematicalMatrixValues() {
        val flushWan = TileDemandVector.ofFlush(Suit.WAN)
        val flushTiao = TileDemandVector.ofFlush(Suit.TIAO)
        val halfFlushWan = TileDemandVector.ofHalfFlush(Suit.WAN)
        val thirteen = TileDemandVector.ofThirteenOrphans()
        val simples = TileDemandVector.ofAllSimples()

        // 1. 异花色必须严格为 0.0 (正交)
        assertEquals(0.0f, flushWan.overlapWith(flushTiao), 0.001f)

        // 2. 十三幺与断幺九没有一张牌重叠，必须为 0.0
        assertEquals(0.0f, thirteen.overlapWith(simples), 0.001f)

        // 3. 清一色(万) 与 混一色(万) 重叠度 = 9 / sqrt(9 * 16) = 9/12 = 0.75
        assertEquals(0.75f, flushWan.overlapWith(halfFlushWan), 0.001f)

        // 4. 清一色(万) 与 十三幺重叠度 = 2 / sqrt(9 * 13) ≈ 0.185
        assertEquals(0.185f, flushWan.overlapWith(thirteen), 0.005f)
    }

    @Test
    fun testMultiPlayerEffectiveSkillDecay() {
        val p0 = PlayerCheatProfile(0, skill = 0.9f, demandVector = TileDemandVector.ofFlush(Suit.WAN))
        val p1 = PlayerCheatProfile(1, skill = 0.9f, demandVector = TileDemandVector.ofFlush(Suit.TIAO))
        val p2 = PlayerCheatProfile(2, skill = 0.9f, demandVector = TileDemandVector.ofFlush(Suit.WAN))

        // 场景 A: P0 (万) 与 P1 (条) 对局（正交）
        val matrixA = TileOverlapMatrixEngine.computeOverlapMatrix(listOf(p0, p1))
        val skillsA = TileOverlapMatrixEngine.computeEffectiveSkills(listOf(p0, p1), matrixA)
        assertEquals(0.9f, skillsA[0]!!, 0.001f, "异花色 P0 熟练度完全不衰减")
        assertEquals(0.9f, skillsA[1]!!, 0.001f, "异花色 P1 熟练度完全不衰减")

        // 场景 B: P0 (万) 与 P2 (万) 对局（同向完全冲突）
        val matrixB = TileOverlapMatrixEngine.computeOverlapMatrix(listOf(p0, p2))
        val skillsB = TileOverlapMatrixEngine.computeEffectiveSkills(listOf(p0, p2), matrixB)
        // 衰减公式: 0.9 / (1 + 0.65 * 0.9 * 1.0) = 0.9 / 1.585 ≈ 0.5678
        assertTrue(skillsB[0]!! < 0.60f, "同花色 P0 熟练度明显衰减至 0.57 左右")
        assertTrue(skillsB[2]!! < 0.60f, "同花色 P2 熟练度明显衰减至 0.57 左右")
    }
}
```

---

## 八、 总结

牌型重叠度矩阵算法的核心价值：
1. **数学自洽性**：从物理底层证明了“井水不犯河水”与“同室操戈”的真实反馈差异；
2. **无限扩展性**：后续新增任意复杂番型（国士无双、字一色、九莲宝灯、清老头），只需给出 34 维的权重分布，算法即可自适应推导出与场上其他所有千术的冲突影响；
3. **真实江湖感**：让玩家深刻体会到“观察对手做牌倾向并避开热门花色”的高阶雀神策略。
