# 交付记录：任务 L（立直精确物理弃牌与第 5 张听牌过滤）

- **任务名称**：立直精确物理弃牌与第 5 张听牌过滤 (Task L)
- **负责 Agent**：Antigravity（生产开发）
- **协同与审查**：Codex（设计、测试编写、构建、审查与验收）
- **交付日期**：2026-09-29

---

## 1. 交付文件清单

根据任务契约与并行边界，任务 L 仅修改一个生产源文件并新增本文档：

1. `core/src/main/kotlin/com/majiang/rules/RiichiRuleEngine.kt`
   - 重构 `canRiichi` 方法实现：
     - 保留现有前置条件：有效座位范围校验、门前清 (`hand.isClosed`)、未宣告立直 (`!state.riichiDeclared[player]`)、点数充足 (`state.scores[player] >= 1000`)、牌山剩余巡目足够 (`state.wallRemaining >= 4`)；
     - 去除原按牌种去重 (`distinctBy`) 且仅返回首个物理副本的限制，改为返回所有符合条件的精确物理弃牌（包含同牌种的不同副本及摸牌 `drawnTile` 副本）；
     - 按牌种索引 (`tile.typeIndex`) 缓存待牌判定结果，消除同牌种多次计算的开销；
     - 听牌判定深度适配：排除剩余暗牌 + 自身已有副露（如已有暗杠）中已占满 4 张的第 5 张候选，但不因牌河已见而拒绝空听；
     - 严格保持其余规则引擎方法（如 `isWinningHand`, `getTenpaiWaits`, `isTenpai`, `isFuriten` 等）行为不变。
2. `doc/riichi-choice/delivery-L.md`（本文档）

---

## 2. 契约规范与核心逻辑实现细节

### 2.1 精确物理牌保留与同种摸牌副本支持
- **原实现缺陷**：原 `canRiichi` 使用 `allClosed.distinctBy { Pair(it.suit, it.rank) }`，并在通过听牌判定后仅通过 `allClosed.first { it.sameType(tile) }` 获取第一个物理副本。若玩家摸入同种牌（例如手牌中已有五万 #0，本巡又摸到五万 #1），`canRiichi` 仅返回五万 #0，导致 `LegalActionProvider` 缺失针对摸牌副本的 `DeclareRiichi(player, drawnTile)` 动作，阻碍玩家摸切立直。
- **重构实现**：
  - 遍历手牌持有的全部暗牌集合 `allClosed = hand.allClosedTiles`（包含摸牌 `drawnTile`）；
  - 对每个物理牌对象，若该牌种舍出后能达成听牌，则将该具体物理 `Tile` 实例追加至结果列表中；
  - 同种牌的不同副本（不同 `copyIndex`）均被精确保留并独立生成，确保后续 UI 选择与 `LegalActionProvider` 校验时物理身份严格对应。

### 2.2 牌种缓存策略 (typeTenpaiCache)
- 一手牌中常有 2~4 张同种牌（如对子、刻子、摸牌等），其舍牌后剩余手牌的牌种多重集完全一致；
- 引入局部缓存 `val typeTenpaiCache = mutableMapOf<Int, Boolean>()`，以 0..33 的 `typeIndex` 作为键；
- 首次遇到某牌种时计算舍牌后是否满足听牌条件并存入缓存，同牌种后续副本直接复用计算结果，大幅减少 `calculateWaits` 调用次数，兼顾性能与纯函数式安全性。

### 2.3 待牌第 5 张物理守恒过滤（已有暗杠与手牌互斥）
- 依据麻将物理守恒，任何牌种全场至多 4 张；
- 统计己方手牌中已被占用的牌量：
  - 己方已有副露中的牌量 `meldCounts`（因门清限制，此时副露均为已声明的暗杠 `CLOSED_KAN`）；
  - 舍牌后剩余暗牌中的牌量 `closedCounts`；
- 调用 `decomposer.calculateWaits(remaining)` 获得理论待牌集合 `rawWaits` 后，执行物理守恒有效性过滤：
  ```kotlin
  rawWaits.any { waitType ->
      closedCounts[waitType] + meldCounts[waitType] < 4
  }
  ```
  - 若某待牌在己方剩余手牌 + 已有暗杠中已占满 4 张（合计 `>= 4`），则该待牌为不可能摸到的“第 5 张候选”，予以排除；
  - 只要存在至少 1 种己方占用小于 4 张的理论待牌，即判定舍牌后为合法听牌；
  - **牌河已见与空听处理**：绝不根据弃牌河（公共河或自家历史河）中已见的张数过滤，完全允许因他家舍牌或牌河已见导致的合规空听（符合日麻竞技规则与契约要求）。

---

## 3. 并行与权限边界遵循

1. **严格文件边界**：
   - 本任务仅修改 `core/src/main/kotlin/com/majiang/rules/RiichiRuleEngine.kt` 的 `canRiichi` 函数；
   - 未修改 `RiichiPreviewProvider.kt`、`RiichiSelectionModel.kt`（任务 P 所属）或 `GameScreen.kt`（任务 U 所属）；
   - 未修改任何测试文件、Gradle 构建配置或依赖项。
2. **环境权限遵循**：
   - 本轮全部工作纯粹使用内置文件读取与编辑工具完成；
   - 未执行任何终端命令、shell 或跨代理调用；
   - 所有单测与构建工作交由协同 Agent Codex 独立执行。

---

## 4. 未验证项说明 (Delegated to Codex)

依据无头开发模式与任务分工约定，以下验证项目由 Codex 独立执行：
1. **单测覆盖与断言验证**：
   - 验证同种多张牌（如手牌已有五万 + 摸到五万）立直时，返回全部物理 `Tile`；
   - 验证立直舍牌选择摸牌副本时，生成对应 `DeclareRiichi(player, drawnTile)` 且被合法动作集认可；
   - 验证已有暗杠占用 4 张时的第 5 张假听牌被正确排除（单骑/嵌张该牌时不能宣告立直）；
   - 验证四张已见于牌河时的空听仍被允许立直；
   - 验证七对子、国士无双 13 面听等特殊形的立直判定不受影响。
2. **全套回归与构建门禁**：
   - 运行 `./gradlew test` 验证既有测试与新增契约测试；
   - 编译桌面端与移动端目标 (`:lwjgl3:classes`, `:android:assembleDebug`)。
