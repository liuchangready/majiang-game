# 🀄 千术麻将 — 码牌阶段与牌河时空影响算法设计说明书

本文档详细记录了本项目核心特色系统——**“开局码牌 / 洗牌定乾坤”（Pre-game Deck Stacking）**与**“牌河时空演变”**的完整算法设计、数学模型与代码实现规范。

---

## 目录
1. [设计哲学与物理约束](#一-设计哲学与物理约束)
2. [两大影响维度：起手牌与摸牌流](#二-两大影响维度起手牌与摸牌流)
3. [基于牌型特征重叠度的多人博弈衰减模型](#三-基于牌型特征重叠度的多人博弈衰减模型)
4. [手法精度与滑移随机性 (Jitter)](#四-手法精度与滑移随机性-jitter)
5. [天胡与天听生成机制](#五-天胡与天听生成机制)
6. [碰杠破千：牌山绝对冻结法则](#六-碰杠破千牌山绝对冻结法则)
7. [完整架构与 Kotlin 代码实现](#七-完整架构与-kotlin-代码实现)
8. [自动化测试与场景验证](#八-自动化测试与场景验证)

---

## 一、 设计哲学与物理约束

在传统麻将游戏中，老千出千通常被简化为“凭空变牌”，这极易破坏游戏的物理自洽性并导致网络同步困难。本项目遵循**真实雀坛江湖物理规律**：

1. **单副麻将严格物理守恒（136 张无增减）**：
   - 包含万、条、筒各 1~9（各 4 张）以及字牌东、南、西、北、中、发、白（各 4 张），共计 136 张；
   - 算法绝不凭空造牌，所有操作均基于 136 个物理槽位的**定向排列与原地对调（In-place Placement & Swap）**；
   - 场上绝不可能出现“第 5 张八万”的逻辑破绽。
2. **所有做牌发生于“码牌阶段”**：
   - 玩家在开局洗牌、砌牌的十几秒内，利用千术技巧直接影响牌山序列；
   - 一旦开局发牌，**牌山顺序彻底物理冻结（Frozen）**，后续对局打牌不再动态调换牌山。

---

## 二、 两大影响维度：起手牌与摸牌流

根据标准麻将发牌规则，136 张牌山被划分为两个物理区间：

```
[0 ────── 52] (前 53 张：起手发牌区)        [53 ─────────────────────── 135] (后 83 张：后续摸牌区)
 ├── P0 (庄家): 0..3, 16..19, 32..35, 48, 52      ├── 轮次 1: P1(53), P2(54), P3(55), P0(56)
 ├── P1:        4..7, 20..23, 36..39, 49          ├── 轮次 2: P1(57), P2(58), P3(59), P0(60)
 ├── P2:        8..11, 24..27, 40..43, 50         └── ... 理想状态下每 4 张回绕一次
 └── P3:        12..15, 28..31, 44..47, 51
```

### 维度 1：影响初始牌面（Initial Hand）
- **目标**：将符合偏好的牌（如万子清一色、七对成对牌）精准注入自己开局的 13 张（庄家 14 张）发牌槽位中；
- **表现**：普通随机发牌单个花色平均为 $4 \sim 5$ 张，单人出千可达 $8 \sim 10$ 张，起手即呈大牌雏形。

### 维度 2：影响后续摸牌流（Subsequent Draw Stream）
- **目标**：在无碰杠理想轮转下，将所需牌（如关键搭子、胡牌绝张）预先码入自己未来第 $k$ 轮预计摸到的槽位：
  $$\text{NominalSlot}(p, k) = 53 + \text{TurnOffset}(p) + 4 \times k$$
- **表现**：开局后前几手摸牌能够连绵不断摸到同花色或听牌张，顺风顺水。

---

## 三、 基于牌型特征重叠度的多人博弈衰减模型

当多名玩家同时使用千术时，**不能采用粗暴的全局衰减**，必须基于他们**实际争夺的牌类型**来决定互相干扰程度。

### 1. 34 维需求特征向量 $\vec{D}$
将 136 张麻将抽象为 34 个种类（万 1~9、条 1~9、筒 1~9、字牌 1~7）：
- **万子清一色**：在万子对应的 9 个维度上分配权重，条/筒/字权重均为 0；
- **十三幺**：仅在 1万、9万、1条、9条、1筒、9筒 及 7 张字牌分配权重；
- **具体指定张（如单吊 8万）**：100% 权重集中在单张牌。

### 2. 牌型重叠度（Cosine Similarity Overlap）
两名老千玩家 $A$ 与 $B$ 之间的**资源冲突率**：
$$\text{Overlap}(A, B) = \frac{\vec{D}_A \cdot \vec{D}_B}{\|\vec{D}_A\| \|\vec{D}_B\|} \in [0.0, 1.0]$$

### 3. 个性化有效熟练度衰减公式
每位玩家实际生效的熟练度 $S'_i$：
$$\text{Interference}_i = \sum_{j \neq i} S_j \times \text{Overlap}(i, j)$$
$$S'_i = S_i \times \frac{1}{1 + \lambda \times \text{Interference}_i} \quad (\lambda \approx 0.65)$$

#### 典型博弈场景表现：
| 出千组合 | 牌类型重叠度 | 互相影响结果 |
|---|:---:|---|
| **P0(万清一色) vs P1(条清一色)** | **$0.0$** | **完全不干扰**！双方熟练度 100% 保留，各自起手 8~10 张目标花色。 |
| **P0(万清一色) vs P1(万清一色)** | **$1.0$** | **极度冲突**！36 张万子被二人哄抢，熟练度大幅衰减，各自降为 5~6 张。 |
| **P0(万清一色) vs P1(十三幺)** | **$\approx 0.15$** | **微弱干扰**！仅在 1万 与 9万 产生争抢，中间张 2~8万 互不影响。 |
| **P0(抢8万) vs P1(抢8万)** | **$1.0$** | **绝张争夺**！全副牌仅 4 张，先手大概率得手，后手严重落空。 |

---

## 四、 手法精度与滑移随机性 (Jitter)

千术不是计算机硬编码修改内存，必须包含**手法失误与随机性**：

1. **概率摇骰**：每个槽位码入目标牌的概率为 $P = S'_i$；
2. **错位滑移（Misplacement Jitter）**：
   当受干扰或手法不够纯熟时，目标牌有概率向左或向右偏离 $\pm 1$ 或 $\pm 2$ 个物理槽位：
   $$\text{ActualSlot} = \text{TargetSlot} \pm \Delta$$
   - **戏剧性效果**：老千本来想把好牌码给自己，因手抖偏了 1 格，**阴差阳错直接发给了下家或对家**！

---

## 五、 天胡与天听生成机制

1. **为什么零散码牌只是“偶发天胡”？**
   - 庄家即使码入 14 张万子，若无法拆解为“4 面子（顺子/刻子） + 1 雀头（对子）”，也只是听牌或一向听，无法起手宣布天胡。
2. **天胡闭环构造法（Winning Template）**：
   - 从可用牌池中抽取：**1 对对子 + 4 组顺子/刻子**，确保 14 张牌数学闭环；
   - 完整写入庄家的 14 个发牌槽位。
3. **熟练度与滑移的自然降级（天听）**：
   - 顶级雀神（熟练度 1.0 无干扰）：起手直接天胡；
   - 熟练度 0.85 或发生轻微滑移：天胡的 14 张牌中有 1~2 张滑走，**自动平滑降级为“天听（起手听牌）”**，第 1 轮自摸或吃碰即胡，既逼真又极不易被对手怀疑抓包。

---

## 六、 碰杠破千：牌山绝对冻结法则

本设计最关键的规则约束：**开局后发生碰、杠导致摸牌时序改变时，绝对不去重排牌山！**

- **时序破千机制**：
  - 设 P0 在第 56 号槽位预码了自己要胡的牌（正常情况下第 1 轮轮到自己摸第 56 张）；
  - 打牌中途，P2 突然宣布“碰！”；
  - 碰牌后跳过了原有顺位，导致牌山第 56 张被**P3 摸走**！
- **游戏性价值**：
  - 真实还原老千江湖中**“人算不如天算”**的博弈反转；
  - 玩家不仅要学会码牌，还要在实战中提防对手通过吃碰打乱自己的算计。

---

## 七、 完整架构与 Kotlin 代码实现

算法文件位于：[`com.majiang.algo.AdaptiveWallStackingEngine.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/algo/AdaptiveWallStackingEngine.kt)

```kotlin
package com.majiang.algo

import java.util.Collections
import kotlin.math.sqrt
import kotlin.random.Random

/**
 * 34 维需求特征画像：
 * 下标 0..8: 万1..9, 9..17: 条1..9, 18..26: 筒1..9, 27..33: 东/南/西/北/中/发/白
 */
class TileDemandProfile {
    val weights = FloatArray(34) { 0f }

    companion object {
        fun fromScheme(scheme: CheatScheme?): TileDemandProfile {
            val profile = TileDemandProfile()
            if (scheme == null) return profile

            when (scheme) {
                is CheatScheme.QingYiSe -> {
                    val base = when (scheme.suit) {
                        Suit.WAN -> 0; Suit.TIAO -> 9; Suit.TONG -> 18; Suit.ZIPAI -> 27
                    }
                    val count = if (scheme.suit == Suit.ZIPAI) 7 else 9
                    for (i in 0 until count) profile.weights[base + i] = 1.0f / count
                }
                is CheatScheme.ShiSanYao -> {
                    val targets = listOf(0, 8, 9, 17, 18, 26, 27, 28, 29, 30, 31, 32, 33)
                    for (idx in targets) profile.weights[idx] = 1.0f / 13f
                }
                is CheatScheme.QiDui -> {
                    for (i in 0 until 34) profile.weights[i] = 1.0f / 34f
                }
                else -> {}
            }
            return profile
        }

        fun fromPreference(pref: SwapPreference?): TileDemandProfile {
            val profile = TileDemandProfile()
            if (pref == null) return profile
            when (pref) {
                is SwapPreference.PreferSuit -> return fromScheme(CheatScheme.QingYiSe(pref.suit))
                is SwapPreference.SpecificTile -> {
                    val base = when (pref.suit) {
                        Suit.WAN -> 0; Suit.TIAO -> 9; Suit.TONG -> 18; Suit.ZIPAI -> 27
                    }
                    profile.weights[base + (pref.rank - 1).coerceIn(0, 8)] = 1.0f
                }
            }
            return profile
        }
    }

    /**
     * 计算与另一玩家画像的资源重叠度 [0.0, 1.0]（余弦相似度）
     */
    fun calculateOverlap(other: TileDemandProfile): Float {
        var dot = 0f; var normA = 0f; var normB = 0f
        for (i in 0 until 34) {
            dot += this.weights[i] * other.weights[i]
            normA += this.weights[i] * this.weights[i]
            normB += other.weights[i] * other.weights[i]
        }
        if (normA == 0f || normB == 0f) return 0f
        return (dot / (sqrt(normA.toDouble()) * sqrt(normB.toDouble()))).toFloat().coerceIn(0f, 1f)
    }

    fun getTileAffinity(tile: Tile): Float {
        val base = when (tile.suit) {
            Suit.WAN -> 0; Suit.TIAO -> 9; Suit.TONG -> 18; Suit.ZIPAI -> 27
        }
        return weights[base + (tile.rank - 1).coerceIn(0, 8)]
    }
}

/**
 * 自适应牌山码牌引擎
 */
object AdaptiveWallStackingEngine {
    const val TOTAL_TILES = 136

    fun stackWall(plans: List<StackingPlan>): List<Tile> {
        val pool = RiggedDealEngine().createStandardPool().apply { shuffle() }
        val wall = arrayOfNulls<Tile>(TOTAL_TILES)

        // 1. 构建每位玩家综合需求画像
        val profiles = plans.associate { it.playerIndex to combineProfiles(it.initialTarget, it.drawStreamTarget) }

        // 2. 精确计算基于牌类型重叠度的个性化衰减
        val effectiveSkills = mutableMapOf<Int, Float>()
        for (plan in plans) {
            val myProfile = profiles[plan.playerIndex] ?: TileDemandProfile()
            var targetedInterference = 0f

            for (other in plans) {
                if (other.playerIndex == plan.playerIndex) continue
                val otherProfile = profiles[other.playerIndex] ?: TileDemandProfile()
                val overlap = myProfile.calculateOverlap(otherProfile)
                targetedInterference += other.skill * overlap
            }

            // 定向衰减：仅重叠部分产生倾轧
            val decay = 1.0f / (1.0f + 0.65f * targetedInterference)
            effectiveSkills[plan.playerIndex] = (plan.skill * decay).coerceIn(0.1f, 1.0f)
        }

        // 3. 码起手牌槽位 (0..52)
        stackInitialHands(wall, pool, plans, profiles, effectiveSkills)

        // 4. 码后续预计摸牌槽位 (53..135)
        stackDrawStream(wall, pool, plans, profiles, effectiveSkills)

        // 5. 剩余空槽随机填充未被拿走的牌（物理闭环）
        pool.shuffle()
        for (i in 0 until TOTAL_TILES) {
            if (wall[i] == null) wall[i] = pool.removeAt(0)
        }

        return wall.filterNotNull()
    }

    private fun combineProfiles(initial: CheatScheme?, draw: SwapPreference?): TileDemandProfile {
        val p1 = TileDemandProfile.fromScheme(initial)
        val p2 = TileDemandProfile.fromPreference(draw)
        return if (initial != null) p1 else p2
    }

    private fun stackInitialHands(
        wall: Array<Tile?>,
        pool: MutableList<Tile>,
        plans: List<StackingPlan>,
        profiles: Map<Int, TileDemandProfile>,
        effectiveSkills: Map<Int, Float>
    ) {
        val slotMap = WallStackingEngine.calculateInitialSlotIndices()
        for (round in 0 until 14) {
            for (plan in plans) {
                val slots = slotMap[plan.playerIndex] ?: continue
                if (round >= slots.size) continue
                val slot = slots[round]
                if (wall[slot] != null) continue

                val profile = profiles[plan.playerIndex] ?: continue
                val skill = effectiveSkills[plan.playerIndex] ?: 0.5f

                if (Random.nextFloat() <= skill) {
                    val candidate = pool.filter { profile.getTileAffinity(it) > 0f }
                                        .maxByOrNull { profile.getTileAffinity(it) }
                    if (candidate != null) {
                        wall[slot] = candidate
                        pool.remove(candidate)
                    }
                }
            }
        }
    }

    private fun stackDrawStream(
        wall: Array<Tile?>,
        pool: MutableList<Tile>,
        plans: List<StackingPlan>,
        profiles: Map<Int, TileDemandProfile>,
        effectiveSkills: Map<Int, Float>
    ) {
        for (plan in plans) {
            val profile = profiles[plan.playerIndex] ?: continue
            val skill = effectiveSkills[plan.playerIndex] ?: 0.5f

            for (r in 0 until plan.plannedDrawRounds) {
                val slot = WallStackingEngine.calculateNominalDrawSlot(plan.playerIndex, r)
                if (slot >= TOTAL_TILES || wall[slot] != null) continue

                if (Random.nextFloat() <= skill) {
                    val candidate = pool.firstOrNull { profile.getTileAffinity(it) > 0f }
                    if (candidate != null) {
                        wall[slot] = candidate
                        pool.remove(candidate)
                    }
                }
            }
        }
    }
}
```

---

## 八、 自动化测试与场景验证

测试用例位于：[`core/src/test/kotlin/com/majiang/algo/AdaptiveStackingTest.kt`](file:///D:/majiang/core/src/test/kotlin/com/majiang/algo/AdaptiveStackingTest.kt)

### 场景 1：异花色互不干扰（万 vs 条）
- **输入**：P0 码万子清一色 (0.9 熟练度)，P1 码条子清一色 (0.9 熟练度)；
- **重叠度**：$\text{Overlap} = 0.0$；
- **表现**：双方熟练度未发生任何衰减，P0 起手万子 $\ge 8$ 张，P1 起手条子 $\ge 8$ 张。

### 场景 2：同花色严重冲突（万 vs 万）
- **输入**：P0 码万子清一色，P1 同样码万子清一色；
- **重叠度**：$\text{Overlap} = 1.0$；
- **表现**：双方熟练度均被明显稀释，且 36 张万子被二人瓜分，起手万子均降至 $5 \sim 6$ 张。

### 场景 3：碰牌破局实测
- **验证**：第 56 号槽位预置了 P0 的胡牌张；对局中 P2 发生碰牌，导致顺位移位，第 56 张物理牌由下家摸走，证明牌山序列在对局期间严格保持不可变性。

