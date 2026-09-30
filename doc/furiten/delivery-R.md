# 🀄 任务 R 交付文档：振听统一判定、无役完成形记录与荣和拦截

> 交付时间：2026-09-29  
> 任务代码：Task R（规则层与状态机核心）  
> 开发者：Antigravity  
> 审查/测试/构建执行方：Codex  

---

## 1. 交付概览

根据振听设计契约与 WRC 2022 §8.11.2（沿用本项目既有规则：同巡振听到本人下次实际摸牌解除，含普通摸牌与岭上摸牌；吃/碰不清除；立直振听持续本局并在新局重置），已完成 Task R 的全部生产代码实现：
1. 新增纯函数式待牌与振听规则服务 [`FuritenRules.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/rules/FuritenRules.kt) 与状态数据类 [`FuritenStatus`](file:///D:/majiang/core/src/main/kotlin/com/majiang/rules/FuritenRules.kt#L13-L19)；
2. 统一 [`RiichiRuleEngine.isFuriten`](file:///D:/majiang/core/src/main/kotlin/com/majiang/rules/RiichiRuleEngine.kt#L340-L352) 转发至 `FuritenRules.status`，杜绝多套散落算法；
3. 在 [`GameStateMachine.openResponseWindow`](file:///D:/majiang/core/src/main/kotlin/com/majiang/state/GameStateMachine.kt#L428-L449) 中为无役完成形（及已处振听的完成形）旁家自动记录相应临时/立直振听，无需强迫玩家点击无意义 Pass；
4. 严格保护合法 Ron 座位，绝不提前标记振听，仅在玩家实际选择非 Ron 操作（Pass/吃/碰/杠）时记录；
5. 严格遵守并行开发文件所有权，未触碰 Task V 文件、测试文件、Gradle 与依赖配置。

---

## 2. 变更文件清单

| 文件路径 | 变更类型 | 核心职责与变更说明 |
|:---|:---|:---|
| [`core/src/main/kotlin/com/majiang/rules/FuritenRules.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/rules/FuritenRules.kt) | **新建** | 振听核心领域服务与数据模型。提供 `waits`、`status`、`completesHand` 纯函数，不带任何内部状态与副作用。 |
| [`core/src/main/kotlin/com/majiang/rules/RiichiRuleEngine.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/rules/RiichiRuleEngine.kt) | **修改 (仅 isFuriten)** | 移除原本内联算法，转调 `FuritenRules.status(...).isFuriten`，保持原 API 签名完全兼容。 |
| [`core/src/main/kotlin/com/majiang/state/GameStateMachine.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/state/GameStateMachine.kt) | **修改** | 在 `openResponseWindow` 计算出 `eligible` 候选动作后，对所有数学完成形但没有合法 Ron 的旁家记相应振听（立直者记 `riichiFuriten`，非立直者记 `sameTurnFuriten`）。 |

---

## 3. 详细规则契约与实现映射

### 3.1 舍牌振听 (Discard Furiten)
- **计算逻辑**：
  - 基于有效 13 张等效闭手调用 `HandDecomposer.INSTANCE.calculateWaits(closedTiles)`；
  - 统计闭手与本人副露中各牌种张数，**严格排除已占满 4 张的第 5 张牌候选**；
  - **不按牌河公开河或牌山过滤空听**（即使场上已见 4 张只要本人手中+副露未占满 4 张依然算听）；
  - 将所有待牌牌种集合与玩家完整的 `hand.discardHistory` 求交（若 `discardHistory` 为空则安全回退到 `hand.discards`）；
  - 只要历史舍牌中曾打出过**任一待牌**（包括已被他家吃碰大明杠叫走的历史牌），即判定为舍牌振听 (`discard = true`)；
  - 改听后若新待牌集合与舍牌历史无交集，舍牌振听**自动解除**。
- **拦截范围**：任一待牌处于舍牌振听时，**所有待牌均禁止荣和与抢杠和**，但合法自摸不受任何限制。

### 3.2 临时振听 / 同巡振听 (Temporary / Same-Turn Furiten)
- **触发时机**：
  1. **有合法 Ron 时放过**：在响应窗口中具有合法 `DeclareRon` 机会但选择了非 Ron 操作（Pass、Chi、Pon、OpenKan）时，记录 `sameTurnFuriten = true`；
  2. **无役完成形隐式过牌**：他人打出的牌使得旁家在数学上构成完成形 (`completesHand == true`) 但因无役等原因没有合法 Ron 选项时，在 `openResponseWindow` 创建窗口时自动记录 `sameTurnFuriten = true`。玩家若没有其他副露动作，不会被弹窗强迫点击 Pass。
- **解除时机**：
  - 持续直到**本人下次实际摸牌**（普通摸牌 `executeDrawInternal` 或岭上摸牌 `TileMovement.drawRinshan`）；
  - 他家摸牌、他家副露以及本人吃碰不解除该标记。

### 3.3 立直振听 (Riichi Furiten)
- **触发时机**：立直宣言生效后，他人打出符合手牌完成形的牌（无论是有合法 Ron 时选择放弃，还是无役/振听下的完成形），记录 `riichiFuriten = true`；
- **持续时间**：**持续本局**。普通摸牌与岭上摸牌均不能解除；
- **重置时机**：仅在每局结束进入新一局调用 `GameState.resetForNewRound()` 时清空。

### 3.4 三因并存与荣和/自摸权能
- `FuritenStatus(discard, temporary, riichi)` 允许三者独立为 true 并存；
- 聚合属性 `isFuriten = discard || temporary || riichi`；
- 任一原因成立，`LegalActionProvider.buildEligibleResponseActions` 均禁止生成 `GameAction.DeclareRon`（包含抢杠 `isChankan = true` 场景）；
- 自摸入口 `DeclareTsumo` 位于摸牌阶段，完全不受振听影响，仍可正常自摸和牌；
- 真人立直后的自动摸切在满足自摸时保持暂停，等待玩家决策。

### 3.5 人类与 AI 响应同源与原子拒绝
- 人类与 AI 在响应窗口流转中完全共用 `handleResponseWindowAction` 与振听记录逻辑；
- 非法命令（非当前回合、阶段不符、重复响应）在预校验阶段直接返回 `ActionResult.Rejected`，零状态副作用，不记录振听。

---

## 4. 共享 API 签名说明

```kotlin
package com.majiang.rules

import com.majiang.model.PlayerHand
import com.majiang.model.Tile

data class FuritenStatus(
    val discard: Boolean = false,
    val temporary: Boolean = false,
    val riichi: Boolean = false
) {
    val isFuriten: Boolean get() = discard || temporary || riichi
}

object FuritenRules {
    /** 计算手牌全部理论待牌（排除占满4张后的第5张牌，不按牌河过滤空听） */
    fun waits(hand: PlayerHand, closedTiles: List<Tile> = hand.closedTiles): Set<Int>

    /** 计算当前综合振听状态 */
    fun status(
        hand: PlayerHand,
        closedTiles: List<Tile> = hand.closedTiles,
        temporary: Boolean = false,
        riichi: Boolean = false
    ): FuritenStatus

    /** 判定目标牌是否能使 13 张等效手牌达成完成形（不查役、不查振听） */
    fun completesHand(hand: PlayerHand, tile: Tile): Boolean
}
```

---

## 5. 交付结论

Task R 生产代码已全部落地，严格遵循无状态纯函数设计、物理守恒与日麻规则契约。交付物等待 Codex 运行全量测试套件、构建校验与回归审查。
