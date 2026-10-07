# 任务 A 交付报告：物理身份、实际容器审计与原子移动（审查修订版）

- **任务编号**：A
- **开发者**：Antigravity
- **审查与验收方**：Codex
- **交付日期**：2026-09-29
- **版本**：R2（已按 Codex 审查意见完成必须修正）
- **状态**：DELIVERED（等待 Codex 审查与验收测试）

---

## 1. Codex 审查意见修正说明

本轮修订严格针对 Codex 提出的 5 项审查意见完成定向修复，所有修复均在任务 A 独占文件范围内：

1. **`drawRinshan` 限制岭上槽位与原子失败保证**：
   - 修复前仅检查 `rinshanPointer < deadWall.size`，存在允许第 5 次从宝牌指示区域摸牌的漏洞。
   - 现已严格约束 `require(state.rinshanPointer in 0..3)`（最多 4 次杠/4 个岭上槽位 0..3）；
   - 前置检查 `require(state.deadWall.size == 14)`、活牌山未耗尽与 TilePool 物理归属；
   - 所有检查均在任何可变操作之前执行，任何失败保证零副作用，所有容器与 TilePool 账本在失败前保持完全不变。

2. **`StateInvariants` 补全 `deadWallReplacements` 空检查**：
   - 初始态 `containersEmpty` 判断已补全 `state.deadWallReplacements.isEmpty()`，彻底杜绝初始态藏有替补牌时被误判为合法 `UNASSIGNED` 的安全漏洞。

3. **已开局有效王牌严格为 14 张**：
   - 废除原有的宽松条件 `if (state.deadWall.isNotEmpty() && state.deadWall.size >= 14)`；
   - 现严格断言 `require(state.deadWall.size == 14)` 以及 `effectiveDeadWallCount == (deadWall.size - rinshanPointer) + deadWallReplacements.size == 14`；
   - 长度为 0..13 或大于 14 的损坏牌桌在开局状态下将立即被强拒绝。

4. **严格分阶段手牌等效计数审计**：
   - 不再使用笼统的 `13..14` 范围，完全落地 Codex 规范的手牌等效计数规则：
     - **`DRAW` 阶段**：全员等效 13 张，且全员 `drawnTile` 必须为 `null`；
     - **`DISCARD` / `CHEAT_WINDOW` 阶段**：行动者 (`currentPlayer`) 等效 14 张，其余 3 位玩家等效 13 张，非行动者 `drawnTile` 必须为 `null`；
     - **`RESPONSE_WINDOW` 阶段**：打牌后等待响应，全员等效 13 张，全员 `drawnTile` 必须为 `null`；
     - **`KAN_DRAW` 阶段**：声明杠后待岭上摸牌者（及全员）等效 13 张，全员 `drawnTile` 必须为 `null`；
     - **`ROUND_END` / `MATCH_END` 阶段**：允许和牌者/自摸者/流局各家等效张数为 13 或 14；
     - **`STACKING` / `DEALING` 阶段**：发牌与码牌事务态不执行上述巡目张数审计。

5. **移除生产包中的 `TestTableBuilder` 夹具**：
   - `core/src/main/kotlin/com/majiang/model/TestTableBuilder.kt` 已清除所有生产类与方法实现，不再向生产源码集暴露任何夹具符号；
   - 夹具将由 Codex 在 `core/src/test` 源集中编写与维护；
   - 夹具所需的构造接口与推荐设计规范详见本报告第 4 节。

---

## 2. 完成项与源文件清单

所有文件均在 Task A 独占文件范围内：

| 文件路径 | 状态 | 职责与变更说明 |
|---|---|---|
| [`core/src/main/kotlin/com/majiang/model/TileLocation.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/model/TileLocation.kt) | 新增 | 物理牌位置值对象，精确定义区域与座位归属约束（`WALL`/`DEAD_WALL`/`UNASSIGNED` 座位为 -1；`HAND`/`DISCARD`/`MELD`/`SLEEVE` 座位必须在 0..3）。提供常量工厂方法。 |
| [`core/src/main/kotlin/com/majiang/model/Tile.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/model/Tile.kt) | 修改 | 增加 `tileId`（0..135 全局物理唯一标识 `typeIndex * 4 + copyIndex`）及伴生工厂 `fromTileId(tileId)`，保留既有 34 维索引与全部不可变属性。 |
| [`core/src/main/kotlin/com/majiang/model/TilePool.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/model/TilePool.kt) | 修改 | 升级为使用 `TileLocation` 强类型物理位置；绑定 `GameState` 时 `assertConservation()` 委托调用 `StateInvariants.assertValid` 执行真实容器全量审计，独立使用时执行 136 张标准合法身份全覆盖断言；支持智能座位推断与深拷贝。 |
| [`core/src/main/kotlin/com/majiang/model/PlayerHand.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/model/PlayerHand.kt) | 修改 | 新增 `discardHistory`（物理被叫走后仍完整保留，供舍张振听判定，不计入物理守恒）；更新 `addDiscard` 同时记录物理弃牌与历史；新增 `contains`、`clear` 与 `deepCopy` 方法。 |
| [`core/src/main/kotlin/com/majiang/model/TileMovement.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/model/TileMovement.kt) | 修改 | 物理牌原子移动服务，提供 `deal`、`dealExtra`、`draw`、`discard`、`chi`、`pon`、`openKan`、`closedKan`、`addedKan`、`drawRinshan`、`swapHandWithWall`、`swapHandWithSleeve` 具名操作。全量前置校验，失败零副作用；`drawRinshan` 严格限制 0..3 槽位，deadWall 严格 14 张；暗杠/加杠未参杠 drawnTile 自动合并闭手；岭上摸牌从活牌山末端补入王牌区替补池，有效王牌恒为 14。 |
| [`core/src/main/kotlin/com/majiang/model/TestTableBuilder.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/model/TestTableBuilder.kt) | 已清空 | 已从生产源码集中移除 `TestTableBuilder` 类与方法，生产代码不再声明该夹具。 |
| [`core/src/main/kotlin/com/majiang/state/GameState.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/state/GameState.kt) | 修改 | 活牌山明确为 `[wallPointer, wallEndExclusive)`，增加 `deadWallReplacements` 王牌替补列表；`sleeveTiles` 改为按座位存储的 `List(4) { mutableListOf() }`；初始化绑定 `tilePool.gameState = this`；新增完整的 `deepCopy()`。 |
| [`core/src/main/kotlin/com/majiang/state/StateInvariants.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/state/StateInvariants.kt) | 修改 | 强状态不变量审计器：检查合法身份恰好 136 张无重复且与 `TilePool` 逐张相等；指针界限与 `rinshanPointer in 0..4`；`containersEmpty` 补全 `deadWallReplacements`；开局有效王牌严格为 14 张；严格分阶段手牌等效计数审计；副露结构合法性。 |
| [`core/src/main/kotlin/com/majiang/cheat/CheatActionExecutor.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/cheat/CheatActionExecutor.kt) | 修改 | 牌张移动适配：`sleeveSwap` 改用 `state.sleeveTiles[player]` 与 `TileMovement.swapHandWithSleeve`；`wallSteal` 改用 `TileMovement.swapHandWithWall` 与 `state.wallEndExclusive`；`divineSight` 使用 `wallEndExclusive` 界限。 |

---

## 3. 与 DESIGN.md 的偏差

**无**。完全遵循 `DESIGN.md` 第 3 节所有规范，包括：
- 136 张牌严格物理闭环与唯一身份标识；
- `TileLocation` 精确到座位（WALL/DEAD_WALL/UNASSIGNED 为 -1，HAND/DISCARD/MELD/SLEEVE 为 0..3）；
- 活牌山 `[wallPointer, wallEndExclusive)` 与有效王牌 14 张守恒（`deadWall[rinshanPointer, size) + deadWallReplacements`）；
- 未参杠 `drawnTile` 先合并回闭手再放置岭上摸牌；
- 真实物理容器重建审计，不自动修复账本。

---

## 4. 必须由其他任务接入的接口与测试夹具规范

### 4.1 供任务 D（状态机集成）接入
1. **原子移动操作**：
   - `TileMovement.deal(state, player): Tile`
   - `TileMovement.dealExtra(state, player): Tile`
   - `TileMovement.draw(state, player): Tile`
   - `TileMovement.discard(state, player, tile): Tile`
   - `TileMovement.chi(state, player, calledTile, fromPlayer, handTiles): Meld`
   - `TileMovement.pon(state, player, calledTile, fromPlayer, handTiles): Meld`
   - `TileMovement.openKan(state, player, calledTile, fromPlayer, handTiles): Meld`
   - `TileMovement.closedKan(state, player, tiles): Meld`
   - `TileMovement.addedKan(state, player, tile): Meld`
   - `TileMovement.drawRinshan(state, player): Tile`（注意：最多 4 次，超出直接拒绝）
   - `TileMovement.swapHandWithWall(state, player, handTile, wallIndex): Tile`
   - `TileMovement.swapHandWithSleeve(state, player, handTile, sleeveTile)`
2. **状态审计与快照事务**：
   - `StateInvariants.assertValid(state: GameState)`：在动作前后调用，确保不变量始终成立；
   - `GameState.deepCopy()`：候选状态试探执行与回滚。

### 4.2 供任务 B（结算与规则）接入
- `PlayerHand.discardHistory`：用于舍张振听判定（即使弃牌被他人吃碰杠叫走，历史仍完整保留，不参与物理计数）。

### 4.3 供任务 C（会话与视图）接入
- 视图投影可安全读取 `PlayerHand.closedTiles`、`drawnTile`、`melds`、`discards`、`discardHistory`、`state.sleeveTiles[viewerSeat]`，并做不可变拷贝。

### 4.4 供 Codex 在 `core/src/test` 编写的 `TestTableBuilder` 夹具接口契约
为了方便 Codex 在测试套件中快速搭建符合 Task A 强不变量审计的测试牌桌，推荐 `TestTableBuilder` 实现以下链式构造接口：

```kotlin
package com.majiang.test

class TestTableBuilder {
    fun hand(player: Int, vararg tiles: Tile): TestTableBuilder
    fun hand(player: Int, tiles: Collection<Tile>): TestTableBuilder
    fun drawnTile(player: Int, tile: Tile?): TestTableBuilder
    fun meld(player: Int, meld: Meld): TestTableBuilder
    fun discard(player: Int, tile: Tile): TestTableBuilder
    fun sleeve(player: Int, tile: Tile): TestTableBuilder
    fun deadWall(vararg tiles: Tile): TestTableBuilder
    fun wall(vararg tiles: Tile): TestTableBuilder
    fun phase(phase: GamePhase): TestTableBuilder
    fun currentPlayer(player: Int): TestTableBuilder
    fun dealer(dealer: Int): TestTableBuilder
    fun scores(scores: IntArray): TestTableBuilder
    
    /**
     * 构建满足全部物理守恒律与状态不变量的 GameState：
     * 1. 自动收集各家手牌、摸牌、副露、牌河、袖牌、指定王牌与指定牌山牌；
     * 2. 剩余未分配牌自动填满王牌区（必须恰好 14 张）与活牌山；
     * 3. 同步 TilePool 中所有 136 张牌的确切 TileLocation（Zone + Seat）；
     * 4. 构造完成后调用 StateInvariants.assertValid(state) 做强断言后返回。
     */
    fun build(): GameState
}
```

---

## 5. 自检命令、结果与日志路径

- **自检状态**：未执行测试（严格遵循 headless 会话未授权命令行工具之约束）
- **约束说明**：本次会话未被授权调用 `RunCommand`、终端或 shell（严禁执行 pwd、Get-Content、rg、mkdir、gradle 等系统命令）。代码经过纯代码级静态语法分析、空安全、边界值及不变量逻辑自检。
- **构建与测试执行方**：交由 Codex 执行 `:core:test` 验证与验收。

---

## 6. 未完成项和已知限制

- **未完成项**：无。Codex 审查 A 提出的 5 项要求已全量修复。
- **已知限制**：
  - `GameStateMachine.kt` 与响应窗口/抢杠窗口属于任务 D 独占范围，当前尚未替换为全量 `TileMovement` 调用，待任务 D 完成集成。
