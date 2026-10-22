# V5-0 规则预冻结合同（rulesetVersion=1）

日期：2026-10-23
状态：**P0.2 预冻结决策，供 G0/Codex 复核；不是 V5-1 验收，也不代表 G0 已通过。**
基线：`v5` 分支，`48125ff`；V4（Kotlin/JVM + libGDX + MJ-TCP/1）仍是唯一生产基线。
范围：只冻结 **V4 在 `48125ff` 的真实行为**，作为 `rulesetVersion=1` 的迁移对照。本文不修改 V4 规则、不删除 `Yaku` 枚举项、不把“文档写法”当成行为证据。

> 证据标记：`路径:行号` 或 `路径#符号` 均指当前工作树。行号会随未提交改动漂移，复核优先按“文件 + 类/函数 + 行号”定位。`V5 新约定` 表示 V4 没有对应实现，仅为 V5 预冻结建议；`待裁决` 表示产品/规则选择，不由本任务擅自决定。

---

## 0. 结论摘要

| 口径 | V4 实际 | 结论 | 证据 |
|---|---|---|---|
| 役种数 | `Yaku` 枚举 42 项；`ScoreCodesAndPaymentPlanTest` 与 `ProtocolScoreCodes.YAKU_CODES` 均登记 42 个稳定码 | **rulesetVersion=1 按 42 个稳定 code 冻结**；`41` 是旧摘要/注释口径，不是“删掉一项”的依据 | `engine/.../rules/YakuEvaluator.kt:15-68`；`engine/.../rules/ScoreCodesAndPaymentPlanTest.kt:23-32,49-65`；`protocol/.../ProtocolScoreCodes.kt:78-121` |
| 事件数 | `GameEvent` 19 个子类；协议 DTO 与客户端 `GameEventView` 也各 19 个变体 | **rulesetVersion=1 的领域事件为 19 类**；`23` 是旧事件集的过时注释 | `engine/.../event/GameEvent.kt:13-109`；`protocol/.../ProtocolModels.kt:1407-1619`；`client/.../ViewModels.kt:1277-1410`；`git show ef0b0f0:engine/.../GameEvent.kt` 为 23 类，移除 `CheatAttempted/SuspicionChanged/BustedEvent/GazeShifted` 后为 19 |
| 单局阶段 | `GamePhase` 10 个枚举值 | **10 态**；方案 §2.1.8 写“9 态”是文档旧口径 | `engine/.../state/GamePhase.kt:15-61` |
| 合法动作类型 | `GameAction` 13 类；协议 `ProtocolLegalAction` 13 类 | **13 类**；`ProtocolViewMapper` 注释“14 种”是过时注释 | `engine/.../state/GameAction.kt:12-57`；`protocol/.../ProtocolModels.kt:220-276`；`client/.../ProtocolViewMapper.kt:101` |
| 物理牌 | 0..135，`typeIndex=id>>2`，`copy=id&3`，赤五 copy=3 | **冻结**；34 维顺序为万 0-8、索 9-17、筒 18-26、风 27-30、三元 31-33 | `engine/.../model/Tile.kt:19-22,49-52,58-68,112-120` |
| 牌山槽位 | 0..52 起手 53 张；53..121 摸牌 69 张；122..135 王牌 14 张 | **冻结**；合计 136 | `engine/.../stacking/ShuffleSlots.kt:5-19,28-79` |
| 响应优先级 | 荣和 > 碰/大明杠 > 吃 > Pass；同级按打牌者逆时针距离“头跳” | **冻结**；V4 只裁决一个赢家，没有双响/三响 | `engine/.../state/ResponseResolver.kt:33-42,74-121`；`RiichiRuleEngine.kt:417-427` |
| 终局不可复活 | `RoundStateMachine.prepare` 在 `ROUND_END` 或 `result != null` 时抛异常；新局新实例/新 `roundId` | **冻结**；整场结束也不把旧 Round 改成 `MATCH_END` | `engine/.../round/RoundStateMachine.kt:47-56,152-201`；`MatchSession.kt:146-155`；`ApplicationCoordinator.kt:2778` |

---

## 1. `rulesetVersion=1` 的含义

### 1.1 版本定义

- V4 源码中**没有** `rulesetVersion` 字段；它是 V5 新引入的契约版本（方案 §5 F1：`rulesetVersion=1`）。
- `rulesetVersion=1` 的预冻结含义：**以 `48125ff` 的 V4 行为为准，不做规则补强、不做“顺手修正”**。V5 若要改变任何一条规则，必须新立 `rulesetVersion`，不能在同一版本里静默改。
- 活动房间在创建时固定 `protocolMajor`、`rulesetVersion`、`projectionVersion`；活动中不混用（方案 §5 F1）。
- 本文所有“冻结”均是**迁移对照**，不是 V5 已实现，也不是 V4 规则被重新验收。

### 1.2 文档/代码差异的处理原则

发现文档与代码不一致时：
1. 以可执行源码和测试断言为准；
2. 把差异单列为“已核清/待裁决”，不修改 V4；
3. 不用文档数字反推删除枚举、事件或阶段。

本任务实际核清的差异：
- `41` vs `42`：见 §5.1，结论 42。
- `23` vs `19`：见 §8.1，结论 19；23 是 `ef0b0f0` 之前的事件集。
- `9` vs `10` 阶段：见 §4.1，结论 10。
- `14` vs `13` 合法动作：见 §3.1，结论 13。
- `GamePhase.allowedTransitions()` 与实际 `RoundTransitionPlanner` 不完全一致：见 §4.2，实际转移以 planner 为准；该函数在 V4 没有被调用（全仓 grep 只有声明），属于文档性白名单而非运行时门禁。

---

## 2. 牌 ID、34 类牌与赤五

### 2.1 物理牌身份

`Tile` 是不可变值对象：`suit + rank + copyIndex`（`engine/.../model/Tile.kt:19-23`）。

- `copyIndex ∈ 0..3`；每种牌 4 张。
- 34 维 `typeIndex`：
  - 万 `MAN`：0..8（1m..9m）
  - 索 `SOU`：9..17（1s..9s）
  - 筒 `PIN`：18..26（1p..9p）
  - 风 `WIND`：27..30（東南西北）
  - 三元 `DRAGON`：31..33（白發中）
  - 证据：`Tile.kt:58-68,101-110`；`ShuffleTargets.kt:8-13` 重复声明同一顺序。
- 全局物理 ID：`tileId = typeIndex * 4 + copyIndex`，范围 0..135；`fromTileId` 反向拆分为 `typeIndex = id / 4`、`copy = id % 4`（`Tile.kt:112-120`）。
- 注意顺序不是常见的“万筒索”，而是**万索筒**；协议、牌山、UI、黄金样本必须沿用，不可在 V5 重排。

### 2.2 赤五

- 赤五判定：`rank == 5 && copyIndex == 3`，且必须是数牌（万/筒/索）；风牌和三元牌没有赤五（`Tile.kt:49-52`）。
- 因此每种数牌 5 的 4 张物理副本中，`copy=3` 是红五；`copy=0..2` 是普通五。
- 赤五既是“赤宝牌”计 1 翻，也可同时被宝牌指示牌命中而叠加（`WinningScoreEvaluator.kt:221-272`；测试 `ScoreCodesAndPaymentPlanTest`/`WinningScoreContractTest` 覆盖赤五与宝牌叠加的既有行为，后者的具体断言需在 P1.2 差分时逐例导出）。
- `Tile.equals` 包含 `copyIndex`，所以“同种牌”必须用 `sameType()` 忽略副本；任何按 `Tile` 直接去重都会把 4 张同种牌误当 1 张（`Tile.kt:70-74`）。

### 2.3 物理位置与 136 守恒

- `Zone`：`WALL/HAND/DISCARD/MELD/DEAD_WALL/SLEEVE/UNASSIGNED`；`TileLocation` 对 `HAND/DISCARD/MELD/SLEEVE` 强制 owner 0..3，对 `WALL/DEAD_WALL/UNASSIGNED` 强制 owner=-1（`TileLocation.kt:10-34`；`TilePool.kt:7-15`）。
- `StateInvariants` 从实际容器重建全量位置：
  - 活牌山 `[wallPointer, wallEndExclusive)` 且池位置仍为 `WALL`；
  - 额外扫描 `wallPointer` 之前的“洞”（锁定/预留/解锁后待重新摸出），仍属物理活牌山；
  - 王牌 `[rinshanPointer, deadWall.size)` + `deadWallReplacements`，并要求 `deadWall.size == 14`、有效王牌恰好 14；
  - 四家暗牌/摸牌/副露/未叫走弃牌/袖牌逐张记录，重复即抛；
  - 最后断言恰好 136 且标准 136 张无遗漏，并与 `TilePool` 逐张位置完全相等。
  - 证据：`engine/.../state/StateInvariants.kt:239-328`。
- 发牌前再次强校验：`wallTiles.size==136`、`toSet().size==136`、`containsAll(standardPool)`，并通过 `StateInvariants.assertValid(candidate)`（`ApplicationCoordinator.kt:2843-3029` 的 `executeDealWithPreparation`）。
- 守恒是**位置表**语义，不是“手牌张数加起来 136”。任何出千/交换都必须同时更新容器与 `TilePool`，否则审计失败。

### 2.4 `SLEEVE` 区域的真实状态（重要）

- `SLEEVE` 区域和 `sleeveTiles` 容器存在（`TileLocation.kt:13,33`；`GameState.kt:309-316`），但当前 V4 的袖里藏花实现**没有把牌放进 `SLEEVE`**。
- 袖里藏花的物理动作是“摸到的牌与活牌山下一张/王牌下一张等额交换”，不是“藏到袖中”：`TileMovement.swapHandWithWall`、`swapDrawnWithNextRinshan`（`ApplicationCoordinator.kt:1421-1453`；`TileMovement.kt` 对应函数）。
- 全仓 `sleeveTiles` 的写入路径只有初始清空/复制/审计；没有业务转移写入。证据：`engine/src/main/kotlin` 中 `sleeveTiles`/`Zone.SLEEVE` 命中仅 `StateInvariants`、`TilePool`、`GameState`、`PlayerView`；`grep -R "sleeveTiles\\|Zone.SLEEVE" engine/src/main/kotlin` 无业务 transfer。
- **V5 预冻结决定**：`rulesetVersion=1` 不把“袖中藏牌”建模为额外隐藏容器；`SLEEVE` 仅保留为未来规则版本的预留区域。若 V5 要真正实现“藏牌”，必须新 ruleset 并重新审计 136 守恒、手牌张数、和牌形与可见性。

---

## 3. 合法动作

### 3.1 动作类型（13 类）

`GameAction` 与协议 `ProtocolLegalAction` 都是 13 类（`GameAction.kt:12-57`；`ProtocolModels.kt:220-276`）：

| # | 动作 | 类别 | 主要合法条件（V4） | 证据 |
|---:|---|---|---|---|
| 1 | `StartStacking` | 系统 | 仅未发牌 `STACKING` 且 `wall` 为空；由协调器/准备阶段触发，不由玩家直接提交 | `RoundStateMachine.kt:159-161` |
| 2 | `DealComplete` | 系统 | 发牌流程内部完成；不作为玩家选项 | `GameAction.kt:18`；`RoundTransitionPlanner.kt:158` |
| 3 | `DrawTile` | 玩家回合 | `DRAW` 阶段、`player == currentPlayer`、`drawnTile == null`、本座可用牌山 > 0 | `LegalActionProvider.kt:53-61` |
| 4 | `DiscardTile` | 玩家回合 | `CHEAT_WINDOW`/`DISCARD`、当前玩家；未立直可打任意暗牌或摸牌（按 `Tile` 去重）；立直后只能摸切 `drawnTile` | `LegalActionProvider.kt:63-144` |
| 5 | `DeclareChi` | 响应 | 仅下家（`(discarder+1)%4`）、数牌、非抢杠窗口、未立直；返回所有可行吃法（最多 3 种） | `RiichiRuleEngine.kt:146-201`；`LegalActionProvider.kt:212-218` |
| 6 | `DeclarePon` | 响应 | 非抢杠、未立直、手中同种 >=2；任意非打牌者 | `LegalActionProvider.kt:195-202` |
| 7 | `DeclareKan` | 回合/响应 | 暗杠（手中 4 张，立直后须过 `RiichiKanValidator`）；加杠（已碰 + 第 4 张）；大明杠（响应，手中 3 张 + 打出牌）；全局 `availableWallTileCount > 0 && rinshanPointer < 4 && totalKanCount < 4` | `LegalActionProvider.kt:92-113,204-210`；`RiichiKanValidatorContractTest` |
| 8 | `DeclareRiichi` | 回合 | 门前、未立直、点数 >=1000、可用牌山 >=4、打出某张后仍听牌且存在未占满 4 张的待牌 | `RiichiRuleEngine.kt:267-325`；`LegalActionProvider.kt:115-121` |
| 9 | `DeclareTsumo` | 回合 | 当前玩家、有真实 `drawnTile`、完成形且至少 1 役/役满 | `LegalActionProvider.kt:71-85` |
| 10 | `DeclareRon` | 响应 | 非振听（舍张/同巡/立直）、完成形且至少 1 役/役满；抢杠窗口同样走荣和 | `LegalActionProvider.kt:168-192`；`RiichiRuleEngine.kt:340-352` |
| 11 | `Pass` | 响应 | 仅当该座位有吃/碰/杠/荣和候选时追加 | `LegalActionProvider.kt:221-225` |
| 12 | `DeclareAbortiveDraw` | 回合 | 第一巡未中断、门前、本人无弃牌、幺九牌种类 >=9 | `RiichiRuleEngine.kt:381-392` |
| 13 | `DeclareAgariYame` | 整场/局间 | 仅 `ROUND_END` 且 `player == dealer` 且 `canDealerDeclareAgariYame`；**`RoundStateMachine.prepare` 明确拒绝**，由 `MatchSession`/协调器处理 | `LegalActionProvider.kt:35-41`；`RoundStateMachine.kt:156-158` |

说明：
- 玩家在对局中真正可提交的是 3–12（以及满足条件时的 13）；1/2 是系统动作。
- `DeclareAgariYame` 不应被送进 `RoundStateMachine`；V5 应把它建模为 **Match 层命令**，而不是 Round 动作。
- 协议注释“14 种合法动作”与源码不符；实际 13 类。不要为凑 14 增加动作。

### 3.2 响应窗口与优先级

- 窗口收集所有有资格座位的候选；每个座位只能记录一次，重复响应无副作用（`ResponseWindow.kt:53-64`）。
- 只有所有有资格座位都提交后窗口才完成（`ResponseResolver.kt:124-131`）。
- 优先级：`Ron=3`、`Pon/OpenKan=2`、`Chi=1`、`Pass=0`；同级按 `(seat - discarder + 4) % 4` 距离近者胜（头跳）（`ResponseResolver.kt:33-42,74-89`）。
- V4 只选出一个赢家；没有双响/三响实现。`DrawReason.TRIPLE_RON` 只是枚举，未在流程中使用（`GameEvent.kt:111-119`；全仓无 `TRIPLE_RON` 业务分支）。这是一个**待裁决产品项**（见 §9 D-03）。
- 抢杠窗口只对“加杠”开放；暗杠不可被抢（`RoundTransitionPlanner.kt:254-258`；`LegalActionProvider.kt:194-219`）。国士无双抢暗杠也未实现。

### 3.3 振听

`FuritenRules` 定义三种：
1. 舍张振听：当前理论待牌与本人完整 `discardHistory`（含被叫走的牌）有交集；
2. 同巡/临时振听：本巡放弃荣和，持续到本人下次实际摸牌（普通/岭上）；
3. 立直振听：立直后放弃荣和，持续到本局结束。

证据：`FuritenRules.kt:13-18,38-88`。`waits()` 只按“暗手 + 副露未占满 4 张”过滤，**不按牌河已见过滤空听**（注释明确，`:30-32`）。V5 不得用“场上已见 4 张”来删待牌。

### 3.4 立直与杠的边界

- 立直条件不要求“已有役”，立直本身是役；但必须门清、点数 >=1000、牌山 >=4、打出后听牌（`RiichiRuleEngine.kt:267-325`）。
- 立直后只能摸切；暗杠必须通过 `RiichiKanValidator.canDeclare`，且摸牌参与、待牌/结构不变（`LegalActionProvider.kt:104-111`；`RiichiKanValidator.kt`）。
- 杠的全局上限：`totalKanCount < 4`、`rinshanPointer < 4`、活牌山至少 1 张；`StateInvariants` 另断言每人杠数 0..4、总杠数一致、宝牌指示 <=5（`StateInvariants.kt:60-79`）。
- 加杠先进入 `CHANKAN_WINDOW`；无人抢杠才补牌/翻杠宝；被抢则这次杠不成立（`RoundTransitionPlanner.kt:254-258,331-348`；`doc/RIICHI_RULES_DESIGN.md:660`）。

### 3.5 超时默认动作（权威兜底）

- 默认 `baseMillis=3000`、`reserveMillis=20000`，按局重置（`TimeControlConfig.kt:17-27`；`DecisionClock.beginRound`）。
- 超时策略优先级：`Pass` > 摸切 `drawnTile` > 合法弃牌中 `Tile` 自然序最小 > `null`；**绝不自动**自摸/荣和/吃/碰/杠/立直/千术（`TimeoutPolicy.kt:12-66`）。
- 这属于“合法动作”的权威兜底，不是玩家可选动作；V5 必须保留，否则超时可能自动和牌或自动出千。

---

## 4. 局与整场生命周期

### 4.1 三层生命周期

| 层 | 状态 | 证据 |
|---|---|---|
| 房间 `ProtocolRoomLifecycle` | `LOBBY, PREPARING, PLAYING, SETTLED, FINISHED, CLOSED`（6） | `protocol/.../ProtocolRoomModels.kt:8-15`；`server/.../SharedRoom.kt:350-355` |
| 整场 `SessionLifecycle` | `CREATED, WAITING_START, ROUND_ACTIVE, ROUND_RESULT, WAITING_DEALER_CHOICE, WAITING_READY, FINISHED, PAUSED, CLOSED`（9） | `MatchSession.kt:17-27` |
| 单局 `GamePhase` | `STACKING, DEALING, DRAW, CHEAT_WINDOW, DISCARD, RESPONSE_WINDOW, CHANKAN_WINDOW, KAN_DRAW, ROUND_END, MATCH_END`（10） | `GamePhase.kt:15-61` |

重要区分：
- `MATCH_END` 是**视图层/整场终局**覆盖：`ApplicationCoordinator.queryView` 在 `SessionLifecycle.FINISHED` 时把视图 phase 改为 `MATCH_END`；旧 `RoundStateMachine` 仍停留在 `ROUND_END`，不被改写（`ApplicationCoordinator.kt:301-352`；`MatchSession.kt:146-155`）。
- 房间 `SETTLED` 对应整场 `WAITING_READY/ROUND_RESULT` 等局间态，不是单局 `ROUND_END` 的同义词（`SharedRoom.kt:350-355`）。

### 4.2 单局实际转移

`GamePhase.allowedTransitions()` 是声明性白名单，V4 没有调用它；实际转移以 `RoundTransitionPlanner` 为准。两者至少有一处不一致：发牌完成后代码直接 `DEALING -> CHEAT_WINDOW`，而白名单写 `DEALING -> DRAW`（`RoundTransitionPlanner.kt:138-160`；`GamePhase.kt:50-61`）。

实际主路径（按代码）：

```
STACKING --StartStacking--> DEALING --发牌(庄家14张)--> CHEAT_WINDOW
CHEAT_WINDOW/DISCARD --出牌--> RESPONSE_WINDOW
RESPONSE_WINDOW --无人响应--> 下家摸牌(DRAW) --> CHEAT_WINDOW
RESPONSE_WINDOW --吃/碰--> DISCARD（该座位继续出牌）
RESPONSE_WINDOW --大明杠--> KAN_DRAW --> CHEAT_WINDOW
CHEAT_WINDOW/DISCARD --加杠--> CHANKAN_WINDOW
CHANKAN_WINDOW --无人抢杠--> KAN_DRAW --> CHEAT_WINDOW
CHANKAN_WINDOW --抢杠荣和--> ROUND_END
DRAW/KAN_DRAW --自摸/荒牌--> ROUND_END
ROUND_END --下一局(新实例/新roundId)--> STACKING
```

说明：
- 普通摸牌路径在代码内部是 `DRAW` 后立刻 `CHEAT_WINDOW`；`DrawTile` 是显式动作时，`RoundTransitionPlanner` 先 `drawNormal` 再置 `CHEAT_WINDOW`（`RoundTransitionPlanner.kt:165-179`）。
- 发牌后庄家已有第 14 张，直接进入 `CHEAT_WINDOW`，不单独停在 `DRAW`（`RoundTransitionPlanner.kt:153-160`）。
- 加杠先 `CHANKAN_WINDOW`；大明杠/暗杠直接 `KAN_DRAW` 后 `CHEAT_WINDOW`（`RoundTransitionPlanner.kt:229-258`）。
- 自摸/荣和/荒牌/途中流局均到 `ROUND_END`；`ROUND_END` 是固定终态，`prepare` 拒绝任何复活（`RoundStateMachine.kt:152-155`）。

### 4.3 新局与新 `roundId`

- 每个 `RoundStateMachine` 构造时默认 `roundId = UUID.randomUUID().toString()`；下一局由 `ApplicationCoordinator` 创建全新 `GameState` 和全新 `RoundStateMachine`（`RoundStateMachine.kt:47-50`；`ApplicationCoordinator.kt:2767-2778`）。
- 连庄也创建新实例/新 `roundId`；`roundWind/roundNumber/dealer/honba` 可保持不变或按推进规则变化。
- `RoundStateMachine.version` 从 0 开始，每次 `install` 成功 +1；`prepare` 不改状态/版本（`RoundStateMachine.kt:54-56,152-201`）。
- 下一局发牌（包括连庄）不得复用旧状态机；这是 V5 P1.3 的硬约束。

### 4.4 整场推进

`MatchProgression.nextRound` 的规则（`MatchProgression.kt:211-341`）：

| 单局结果 | 连庄/换庄 | 局号/本场 |
|---|---|---|
| 庄家和牌（`DealerWin`/`Ron`/`Tsumo` 且 winner=dealer） | 连庄 | 局号不变，`honba+1` |
| 闲家和牌 | 换庄（`(dealer+1)%4`） | 局号 +1；`honba=0` |
| 荒牌流局，庄家听牌 | 连庄 | `honba+1` |
| 荒牌流局，庄家未听 | 换庄 | 局号 +1；`honba+1` |
| 途中流局（四风/四杠/四立直/九种九牌） | 连庄 | `honba+1` |
| 尾局庄家连庄且满足 Agari-Yame 并选择终了 | 终局 | `AGARI_YAME` |
| 尾局换庄且有人 >= targetScore，或不允许西入 | 终局 | `NORMAL_COMPLETION` |
| 尾局换庄且无人达到 targetScore 且允许西入 | 西入 | 场风 +1，局号 0，换庄 |
| 西入中有人 >= targetScore | 终局 | `WEST_COMPLETION` |
| 破产（`allowBankruptcy` 且分数低于阈值） | 终局 | `BANKRUPTCY`，优先于和牌推进检查 |

默认规则（`MatchRuleConfig`，`MatchProgression.kt:28-39`）：
- `length=HANCHAN`（东+南），`handsPerWind=4`，`winds=[1,2]`；
- `targetScore=30000`，`allowWestExtension=true`，`allowAgariYame=true`；
- `allowBankruptcy=true`，`bankruptcyThreshold=0`，`bankruptcyInclusive=false`（严格 `<0`）；
- `awardRemainingRiichiSticksToFirst=true`；
- 同分排名按 `tieBreakSeats=[0,1,2,3]`（起家优先）。

终局：
- 终局剩余供托按配置归第一名；`finalScores` 加上 `remainingRiichiSticks * 1000`；排名重算（`MatchProgression.kt:410-433`）。
- `SessionResult` 保存 `finalScores/rankings/reason/finalCheatPoints/cheatSummary`；`RoundRecord` 保存 `finalCheatPoints/cheatDeltas/cheatEntries`（`MatchSession.kt:32-55`）。
- 没有 uma/oka、烧鸡、流局满贯、责任支付、双倍役满、切上满贯、三麻（方案 §2.1.5；源码也没有对应分支）。V5 不得顺手补。

### 4.5 计时与控制代次

- 每座每局 20s 储备 + 每次 3s 基础；超时由权威 deadline 裁决（`TimeControlConfig.kt:17-27`；`DecisionClock`）。
- 控制者 `LocalHuman/RemoteHuman/Computer/Takeover`；`ControllerRegistry.setController/initialize` 每次 +1 `controlEpoch`；旧 `controlEpoch` 的提交必须拒绝（`ControllerRegistry.kt:11-44`；`SeatController.kt:18-23`）。
- 真人重连不自动收回；`claimControl` 才收回并 +1 epoch；托管期间已生效动作不回滚（`server/.../ConnectionControl.kt:280-365`；AGENTS.md §6）。
- 超时先于同 tick AI：`ServerTableHost.tick` 顺序见 AGENTS.md §5.2；V4 测试 `TableAuthority`/`ServerTableHost` 覆盖，V5 P3.3 必须保留。

---

## 5. 役种清单（42 个稳定 code）

### 5.1 41/42 口径

**结论：42。** 证据链：
1. `Yaku` 枚举逐项为 42 项：1 翻 15、2 翻 11、3 翻 3、6 翻 1、役满 12（`YakuEvaluator.kt:15-68`）。
2. `ScoreCodesAndPaymentPlanTest.expectedYakuCodes` 显式列出 42 个 code，并断言 `Yaku.values().size == expectedYakuCodes.size`（`ScoreCodesAndPaymentPlanTest.kt:23-32,49-65`）。
3. `ProtocolScoreCodes.YAKU_CODES` 镜像 42 个 code（`ProtocolScoreCodes.kt:78-121`）。
4. `doc/RIICHI_RULES_DESIGN.md` 的役种表本身也是 15+11+3+1+12=42（`:355-422`）；同文件 `:750` 的“41种”是旧摘要。
5. `AGENTS.md:49`、`CLAUDE.md:21`、`README.md:31` 的 41 是文档旧口径；没有源码证据表明某一项“不计/不可产出”。

因此：**不删除任何枚举项，不把 41 作为 rulesetVersion=1 的计数**。`41` 只能作为历史文档口径记录。此结论仍需 G0/Codex 复核；若复核认为某项不可产出，应提供具体不可达证明，而不是改枚举凑 41。

### 5.2 完整清单

“门前/副露”为 `Yaku.hanClosed/hanOpen`；役满两项为 0/0 + `isYakuman=true`，实际按 `yakumanCount` 计 8000/倍数。

| # | code | 展示名 | 门前 | 副露 | 类型 |
|---:|---|---|---:|---:|---|
| 1 | `RIICHI` | 立直 | 1 | 0 | 1翻 |
| 2 | `IPPATSU` | 一发 | 1 | 0 | 1翻 |
| 3 | `MENZEN_TSUMO` | 门前清自摸和 | 1 | 0 | 1翻 |
| 4 | `TANYAO` | 断幺九 | 1 | 1 | 1翻 |
| 5 | `PINFU` | 平和 | 1 | 0 | 1翻 |
| 6 | `IIPEIKO` | 一杯口 | 1 | 0 | 1翻 |
| 7 | `YAKUHAI_HAKU` | 役牌 白 | 1 | 1 | 1翻 |
| 8 | `YAKUHAI_HATSU` | 役牌 发 | 1 | 1 | 1翻 |
| 9 | `YAKUHAI_CHUN` | 役牌 中 | 1 | 1 | 1翻 |
| 10 | `YAKUHAI_BAKAZE` | 场风牌 | 1 | 1 | 1翻 |
| 11 | `YAKUHAI_JIKAZE` | 自风牌 | 1 | 1 | 1翻 |
| 12 | `HAITEI` | 海底摸月 | 1 | 1 | 1翻 |
| 13 | `HOUTEI` | 河底捞鱼 | 1 | 1 | 1翻 |
| 14 | `RINSHAN` | 岭上开花 | 1 | 1 | 1翻 |
| 15 | `CHANKAN` | 抢杠 | 1 | 1 | 1翻 |
| 16 | `DOUBLE_RIICHI` | 两立直 | 2 | 0 | 2翻 |
| 17 | `CHANTA` | 混全带幺九 | 2 | 1 | 2翻 |
| 18 | `ITTSU` | 一气通贯 | 2 | 1 | 2翻 |
| 19 | `SAN_SHOKU_DOUJUN` | 三色同顺 | 2 | 1 | 2翻 |
| 20 | `SAN_SHOKU_DOUKOU` | 三色同刻 | 2 | 2 | 2翻 |
| 21 | `TOITOI` | 对对和 | 2 | 2 | 2翻 |
| 22 | `SAN_ANKOU` | 三暗刻 | 2 | 2 | 2翻 |
| 23 | `SAN_KANTSU` | 三杠子 | 2 | 2 | 2翻 |
| 24 | `SHOUSANGEN` | 小三元 | 2 | 2 | 2翻 |
| 25 | `HONROUTOU` | 混老头 | 2 | 2 | 2翻 |
| 26 | `CHIITOITSU` | 七对子 | 2 | 0 | 2翻 |
| 27 | `HONITSU` | 混一色 | 3 | 2 | 3翻 |
| 28 | `JUNCHAN` | 纯全带幺九 | 3 | 2 | 3翻 |
| 29 | `RYANPEIKOU` | 两杯口 | 3 | 0 | 3翻 |
| 30 | `CHINITSU` | 清一色 | 6 | 5 | 6翻 |
| 31 | `KOKUSHI` | 国士无双 | 0 | 0 | 役满 |
| 32 | `SUUANKOU` | 四暗刻 | 0 | 0 | 役满 |
| 33 | `DAISANGEN` | 大三元 | 0 | 0 | 役满 |
| 34 | `SHOUSUUSHII` | 小四喜 | 0 | 0 | 役满 |
| 35 | `DAISUUSHII` | 大四喜 | 0 | 0 | 役满 |
| 36 | `TSUUIISOU` | 字一色 | 0 | 0 | 役满 |
| 37 | `RYUUIISOU` | 绿一色 | 0 | 0 | 役满 |
| 38 | `CHINROUTOU` | 清老头 | 0 | 0 | 役满 |
| 39 | `CHURENPOUTOU` | 九莲宝灯 | 0 | 0 | 役满 |
| 40 | `SUUKANTSU` | 四杠子 | 0 | 0 | 役满 |
| 41 | `TENHOU` | 天和 | 0 | 0 | 役满 |
| 42 | `CHIIHOU` | 地和 | 0 | 0 | 役满 |

### 5.3 产出路径与限制

- `YakuEvaluator.evaluate` 遍历所有 `HandDecomposer` 拆解，役满优先；非役满取最高翻（`YakuEvaluator.kt:112-218`）。
- 所有 42 项在 `evaluateDecomposition` 中都有检查分支（`YakuEvaluator.kt:149-217`），没有“声明但无分支”的项。
- `TENHOU/CHIIHOU` 由 `RiichiRuleEngine.buildWinContext` 设置：`isTenhou = 自摸 && 庄家 && isFirstUninterruptedGo && turnNumber<=1`；`isChiihou = 自摸 && 非庄家 && isFirstUninterruptedGo`（`RiichiRuleEngine.kt:82-100`）。这两项目前**没有专用测试**逐例证明；P1.2 必须补黄金样本，不能以“有分支”当已验收。
- 役满都是单倍；`yakumanCount` 可累计（如大四喜+字一色=2 倍），但没有“双倍役满”独立项；`ScoreCalculator` 按 `8000 * yakumanCount`（`ScoreCalculator.kt:129-145`）。
- 13 翻以上是数え役满（`KAZOE_YAKUMAN`），不是真的役满枚举；`ScoreCalculator.kt:156-160`。
- 没有责任支付、没有包牌、没有流局满贯、没有切上满贯、没有三麻、没有烧鸡、没有 uma/oka。V5 要补必须新 ruleset。

---

## 6. 符与支付

### 6.1 符

`FuCalculator.calculateBreakdown`（`FuCalculator.kt:41-166`）：

| 组成 | 规则 | 证据 |
|---|---|---|
| 副底 | 自摸 20；门前荣和 30；非门前荣和 20 | `FuCalculator.kt:90-98` |
| 七对子 | 固定 25 符 | `:50-59` |
| 国士 | 固定 30 符（展示用；役满按基本点算，不按符） | `:62-72` |
| 平和自摸 | 固定 20 符（不加自摸 2 符） | `:76-86` |
| 中张明刻 | 2 | `:194-202` |
| 中张暗刻 | 4 | 同上 |
| 幺九明刻 | 4 | 同上 |
| 幺九暗刻 | 8 | 同上 |
| 中张明杠 | 8 | `:207-226` |
| 中张暗杠 | 16 | 同上 |
| 幺九明杠 | 16 | 同上 |
| 幺九暗杠 | 32 | 同上 |
| 加杠 | 按明杠 8/16 | 同上 |
| 役牌雀头 | 三元 +2；自风 +2；场风 +2 | `:232-246` |
| 连风雀头 | 自风=场风时两项各 +2，实际 **+4** | `:240-242`；注释 `:17` 说“取 +2”与代码不符，实际代码为 +4 |
| 待ち | 两面/双碰 0；嵌张/边张/单骑 +2 | `:252-276` |
| 自摸符 | 门前自摸 +2（平和自摸除外） | `:144-147` |
| 取整 | 向上到 10 的倍数 | `:341` |
| 荣和保底 | 荣和最终 <30 时取 30 | `:152-157` |

注意：
- 代码中 `calculateWaitFu` 未使用；待ち符通过 `determineWaitDescription` 直接加 2，不是两套规则。
- `FuCalculator` 的注释“连风取 +2”与实现不符；以实现 +4 为准，标为**文档差异**，不改 V4。
- `FuCalculator.isPinfu` 与 `YakuEvaluator.checkPinfu` 是两套判定；V5 差分时要用同一拆解和上下文，不能只比最终符。

### 6.2 基本点与封顶

- 基本点 = `fu * 2^(2+han)`，用 `BigInteger` 精确计算；`rawBasePointsExact` 保留精确值（`ScoreCalculator.kt:148-152`）。
- 封顶（`ScoreCalculator.kt:156-188`）：

| 条件 | 基本点 | capCode |
|---|---:|---|
| `han >= 13` | 8000 | `KAZOE_YAKUMAN` |
| `han 11..12` | 6000 | `SANBAIMAN` |
| `han 8..10` | 4000 | `BAIMAN` |
| `han 6..7` | 3000 | `HANEMAN` |
| `han == 5` 或 `han==4 && fu>=40` 或 `han==3 && fu>=70` | 2000 | `MANGAN` |
| 其他，`raw > 2000` | 2000 | `MANGAN` |
| 其他，`raw <= 2000` | `raw` | null |
| 役满 | `8000 * yakumanCount` | `YAKUMAN` |

- **没有切上满贯**：4 翻 30 符 = 1920，非庄家荣和 7700，不按满贯 8000；3 翻 60 符同理。V5 不得“顺手”加切上。
- `rawBasePoints` 是兼容字段，超过 `Int.MAX_VALUE` 时饱和；支付计算使用封顶后的 `basePoints`，不是 `rawBasePoints`（`ScoreCalculator.kt:150-152`）。

### 6.3 支付

`ScoreCalculator.ScoreResult` 四个支付函数（`ScoreCalculator.kt:51-68`）：

| 场景 | 公式（基本点 `b`） | 取整 |
|---|---|---|
| 庄家自摸，每家闲家 | `b * 2` | 向上百位 |
| 闲家自摸，庄家 | `b * 2` | 向上百位 |
| 闲家自摸，每位闲家 | `b * 1` | 向上百位 |
| 庄家荣和，放铳者 | `b * 6` | 向上百位 |
| 闲家荣和，放铳者 | `b * 4` | 向上百位 |

`PaymentPlan` 只含基础支付，不含本场/供托：
- `DEALER_TSUMO`: `NON_DEALER` × 3；
- `NON_DEALER_TSUMO`: `DEALER` ×1 + `NON_DEALER` ×2；
- `DEALER_RON`: `DISCARDER` ×1；
- `NON_DEALER_RON`: `DISCARDER` ×1。
证据：`ScoreCalculator.kt:75-111`；`ScoreCodes.kt:28-91`；测试 `ScoreCodesAndPaymentPlanTest.kt:111-225`。

### 6.4 结算、本场、供托与守恒

`SettlementService`（`SettlementService.kt:55-200`）：

- **荣和**：放铳者付 `基础支付 + 300 * honba`；和牌者收该金额 + `1000 * riichiSticks`；供托清零。
- **自摸**：每位付款者付 `基础支付 + 100 * honba`；和牌者收全部 + `1000 * riichiSticks`；供托清零。
- **荒牌流局**：
  - 1 听：听牌者 +3000，其他三家各 -1000；
  - 2 听：各 +1500，其他两家各 -1500；
  - 3 听：各 +1000，未听者 -3000；
  - 0/4 听：无收支；
  - 供托保留。
- **途中流局**：无罚符，供托保留。
- **守恒**：`sum(newScores) + remainingRiichiSticks*1000 == sum(scores) + riichiSticks*1000`；并逐座校验 `delta == new-old`（`SettlementService.kt:229-246`）。
- `RoundResult.totalDeltas` 从本局起始分数计算，含立直扣点与供托；`startingRiichiSticks`/`remainingRiichiSticks` 单独保存（`RoundResult.kt:30-43`）。
- `ScoreBreakdown.totalPoints` = 基础支付总额 + `300*honba` + `1000*riichiSticks`；`SettlementService` 另行按付款方拆分。V5 必须让“展示总额”和“实际转账”使用同一基础支付函数，避免双套。
- 出千点数与麻将点数完全独立；`CheatLedger` 的 `entryId` 幂等，不参与麻将分数守恒（`CheatLedgerIntegrationAcceptanceTest`）。

---

## 7. 三大绝技与码牌

### 7.1 共同经济与幂等

- 经济默认关闭：`CheatEconomyConfig.enabled=false`；初始点数默认 `[0,0,0,0]`，可配置 4 个非负值（`CheatEconomyConfig.kt:10-27`）。
- `CheatLedger` 按 `entryId` 幂等：同 id 同载荷返回原 entry，不同载荷冲突；负余额/越界/Int 溢出拒绝且零副作用（`CheatLedger.kt:70-124`；`CheatLedgerIntegrationAcceptanceTest`）。
- 扣费与状态变更在同一候选事务中：先算 `candidateState`，审计通过后 `round.install`；失败不留痕迹（如 `ApplicationCoordinator.submitSleeveBloom` 的 `ledgerConfig`/`charge`/`install` 路径）。
- `requestId` 与账本 `entryId` 分离：如 `bloom-$seat-${request.requestId}`、`beam-$seat-${request.requestId}`、`bloom-offturn-$seat-${request.requestId}`；同 requestId 重放返回原回执，不重复扣费（`ApplicationCoordinator.kt:1253,1718,2148`；测试 `SleeveBloomContractTest`/`BeamSwapContractTest` 的 replay 用例）。

### 7.2 落汗（TileMark）

| 项 | 规则 | 证据 |
|---|---|---|
| 费用 | 配置 `cheatEconomy.costs["TILE_MARK"]`，默认 10，必须 >0；投影与扣费共用 `TileMarkCost.of` | `TileMarkCost.kt:10-13`；`TileMarkViews.kt:171-176` |
| 窗口 | 当前玩家、`CHEAT_WINDOW` 或 `DISCARD`、无响应窗口、非准备阶段、未立直 | `TileMarkSubmissionHandler.kt:92-95` |
| 选择 | 恰好 4 张、互不重复、0..135、全部属于本人手牌、未重复标记 | `TileMarkSubmissionHandler.kt:97-106` |
| 容量 | 最多 3 组，累计有效标记 <=12 张 | `:107-109` |
| 过期 | `expiresAfterRoundOrdinal = createdRoundOrdinal + 3`；当前局起算，当前 + 3 个未来局；`completeRound` 过滤 `> dealtRoundOrdinal` | `TileMarkModels.kt:102-125`；`TileMarkState.kt:120-137`；测试 `TileMarkModelContractTest` |
| 物理效果 | **不移动牌**；记录 4 张实体牌组，只对 owner 投影（含对手暗牌牌面/位置） | `TileMarkProjectionFactory`；`TileMarkViews.kt:19-50`；测试 `TileMarkAuthorityContractTest` |
| 看破清标 | 准备阶段看破在发牌时结算：单人看破保留自己的组；两人及以上互相看破则所有组清除；非看破者的组清除 | `ApplicationCoordinator.kt:3002-3012`；`TileMarkState.kt:167-192` |
| 死亡/终局 | 整场终局 `clearAll`；关闭/离开清标，不退款 | `MatchSession.kt:446,554,662`；`TileMarkState.kt:194-209` |

注意：落汗的“透视”只对 owner；观战/其他玩家不得出现 `tileMark` 字段（测试 `TileMarkServerContractTest`）。

### 7.3 袖里藏花（SleeveBloom）

#### 即时路径

| 项 | 规则 | 证据 |
|---|---|---|
| 费用 | 必须启用经济且 `costs["SLEEVE_BLOOM"] == 10`（不是“默认 10”，是**恰好 10**）；激活时扣费 | `SleeveBloomSubmissionHandler.kt:431`；`ApplicationCoordinator.kt:1165-1172,1249-1265` |
| 窗口 | 当前玩家、`CHEAT_WINDOW`、无响应窗口、摸牌已在手、非 `DEAL_EXTRA`、无其他 offer、本巡未用过袖/梁 | `SleeveBloomSubmissionHandler.kt:248-260`；`ApplicationCoordinator.kt:1183-1208` |
| 候选第一张 | 当前 `drawnTile` | `ApplicationCoordinator.kt:1191-1195` |
| 候选第二张 | 普通摸牌：`nextAvailableWallIndex()` 的牌（可跳过锁定/预留洞）；岭上：`deadWall[rinshanPointer]` | `ApplicationCoordinator.kt:1210-1243` |
| FIRST | 保留第一张（原摸牌）；**不移动牌** | `ApplicationCoordinator.kt:1421-1453`（仅 SECOND 分支移动） |
| SECOND | 普通：`swapHandWithWall`；岭上：`swapDrawnWithNextRinshan`；手牌数/牌山数不变 | 同上 |
| 扣费时点 | 激活时扣 10；二选一不再扣费；FIRST/SECOND 都不退款 | `ApplicationCoordinator.kt:1249-1265,1456-1472` |
| 事件 | 激活产生 `CheatPointsChanged`；二选一 **eventless**（无事件） | `ApplicationCoordinator.kt:1298-1303,1465`；`SleeveBloomContractTest`/`SleeveBloomServerContractTest` |
| 超时 | 到截止先按 FIRST 保留，再进入正常出牌；不卡 pending | `SleeveBloomContractTest` 的 deadline 用例 |

#### 他家回合预定路径

| 项 | 规则 | 证据 |
|---|---|---|
| 窗口签发 | 本人弃牌后 `openSleeveBloomWindow(seat)` 生成 `bloom-window-$seat-$turnNumber-$seq`；重复签发覆盖旧 windowId | `GameState.kt:525-535` |
| 激活条件 | 非当前玩家、当前 phase ∈ `DRAW/CHEAT_WINDOW/DISCARD/RESPONSE_WINDOW/CHANKAN_WINDOW`、windowId 匹配、无 offer/reservation、活牌山 >=2 张、余额 >=10 | `ApplicationCoordinator.kt:1666-1712`；`SleeveBloomSubmissionHandler.kt:352-384` |
| 费用 | 激活时扣 10；二选一不重复扣 | `ApplicationCoordinator.kt:1714-1730` |
| 候选 | 当前可用活牌山前两张（`availableWallIndices()[0..1]`），两张不同且在 `WALL` | `ApplicationCoordinator.kt:1698-1709` |
| 选择 | FIRST/SECOND 只锁定其中一张为 `SleeveBloomReservation`，另一张解除锁定留在原位；**不立即移动牌** | `GameState.kt:635-648` |
| 交付 | 本人下一次普通摸牌时，预留牌直接从原槽进入 `drawnTile`，`fromReservation=true`；不额外再摸一张，`wallEndExclusive--`，`wallPointer` 按删除位置修正 | `TileMovement.kt:74-98`；`GameState.kt:597-628` |
| 超时/截止 | `offTurnChoiceMillis=10s`；到期自动 FIRST；选择请求在截止后返回 `DEADLINE_EXPIRED`，但已按 FIRST 结算；不退款 | `SleeveBloomConfig.kt:20-29`；`ApplicationCoordinator.kt:1840-1855`；`resolveExpiredSleeveBloomOffTurnOffers` |
| 失效 | 下一次普通摸牌/局终失效； stale offer 清除，不退款 | `GameState.kt:650-656`；`SleeveBloomSubmissionHandler.kt:403-406` |

注意：`SleeveBloomOffer` 的 `firstTile/secondTile` 对 owner 是可见的（这是千术收益）；观战和其他玩家不得看到。`SLEEVE` 区域不参与（见 §2.4）。

### 7.4 偷梁换柱（BeamSwap）

| 项 | 规则 | 证据 |
|---|---|---|
| 费用 | 必须启用经济且 `costs["BEAM_SWAP"] == 10`；激活时扣费 | `ApplicationCoordinator.kt:2036-2043,2144-2160`；`BeamSwapSubmissionHandler.kt:325` |
| 窗口 | 当前玩家、`CHEAT_WINDOW`、无响应窗口、未立直、摸牌在手、非 `DEAL_EXTRA`、无其他 offer、本巡未用过袖/梁、至少 1 张可换手牌 | `ApplicationCoordinator.kt:2054-2086`；`BeamSwapSubmissionHandler.kt:165-185` |
| 窗口类型 | 普通摸牌 `WALL`；岭上 `DEAD_WALL` | `ApplicationCoordinator.kt:2088-2092` |
| WALL 候选 | `[wallPointer, min(wallPointer+8, wallEndExclusive))`，排除被袖里藏花锁定/预留的牌；每张必须在 `WALL` | `ApplicationCoordinator.kt:2096-2109` |
| DEAD_WALL 候选 | `deadWall[rinshanPointer..3]` + `deadWall[4..13]` 中非宝牌指示牌 + `deadWallReplacements`；每张必须在 `DEAD_WALL` | `ApplicationCoordinator.kt:2111-2133` |
| 候选冻结 | `BeamSwapOffer` 冻结 `candidates`、`eligibleHandTileIds`、`drawnTileId`、`createdTurnNumber` | `BeamSwapModels.kt:256-275` |
| SWAP | 手牌（非摸牌）与指定槽位等额互换；`swapHandWithWall`/`swapHandWithDeadWall`；手牌数、牌山/王牌数不变 | `ApplicationCoordinator.kt:2336-2383` |
| KEEP_ORIGINAL | 不移动牌，只消费本次机会并清 offer；不退款 | `ApplicationCoordinator.kt:2384-2397`；`BeamSwapContractTest` |
| 漂移 | offer 的摸牌来源/手牌集合/候选任一漂移：清 offer、拒绝、不移动、不退款、不延长 | `ApplicationCoordinator.kt:2289-2334` |
| 事件 | 激活产生 `CheatPointsChanged`；选择 **eventless** | `ApplicationCoordinator.kt:2173-2185,2395`；`BeamSwapHostContractTest` |
| 超时 | 到截止按 `KEEP_ORIGINAL` 清理，不卡 pending | `BeamSwapContractTest` deadline 用例 |

注意：`BeamSwapCandidateView` 只有被 owner 的落汗标记命中时才携带 `tile` 牌面；未标记候选 `tile=null`（`BeamSwapModels.kt:331-349`）。这是“落汗 + 偷梁”组合的信息边界，V5 必须保留。

### 7.5 码牌（统一洗牌千术）

| 项 | 规则 | 证据 |
|---|---|---|
| 准备窗口 | 默认 10s（`selectionMillis=10000`）；超时未确认按跳过（0 点）；四座都确认可提前结束 | `ShuffleConfig.kt:46-49`；`ShufflePreparationHandler.kt:164-207` |
| 目标 | 6 选 1：`CHINITSU_MAN/SOU/PIN`、`DAISANGEN`、`KOKUSHI`、`DAISUUSHII`；`null`=跳过 | `ShuffleTarget.kt`；`ShuffleTargets.kt:33-65` |
| 费用 | 目标 30 点/局；看破 10 点/局；可叠加 40；跳过 0 | `ShuffleSelection.kt:18-40` |
| 看破 | 抵消“其他看破者”的整局码牌目标；自己仍可做牌；两人及以上互相看破时所有目标都被抵消；费用仍按原始方案扣 | `ShufflePreparationHandler.kt:210-232`；`ApplicationCoordinator.kt:3002-3012` |
| 槽位 | 0..52 起手（53 张）；53..121 摸牌（69 张）；122..135 王牌（14 张，绝不做目标/供体） | `ShuffleSlots.kt:5-19,28-79` |
| 四人恒等 | 有效施术者 n=4 时返回同 seed 基础洗牌，不做交换 | `ShuffleWallGenerator.kt:82-87` |
| 起手锁定 | 起手区 0..52 在发牌后锁定，后续不再优化 | 生成器 KDoc `:19-27` |
| 随机源 | `SecureRandom.nextLong()` 生成 seed；`Random(seed)` + `wall.shuffle(rng)`；不是跨运行时规范 | `ShufflePreparationHandler.kt:54,69`；`ShuffleWallGenerator.kt:62-65` |
| 守恒 | 仍从 `TilePool.buildStandardSet()` 的 136 张生成；不增不减 | `ShuffleWallGenerator.kt:14,62-65` |
| 事后校验 | 生成结果必须 `size==136`，随后发牌前 `StateInvariants.assertValid` | `ShufflePreparationHandler.kt:234-244`；`ApplicationCoordinator.kt:2995-2996` |

**V5 待决**：是否精确复刻 V4 的 Kotlin `Random(seed)`/`shuffle` 分布。若不复刻，必须把新算法写进新的 `rulesetVersion`，并记录实际牌山/抽牌序列，而不是只存 seed（方案 §D9/F2）。P1.5 负责。

### 7.6 三绝技守恒检查清单

V5 P1.4 至少要对每个成功路径断言：

1. 136 张 `tileId` 唯一，`TilePool` 与实际容器逐张一致；
2. 手牌总数不变（交换/保留都不改变张数）；
3. 活牌山/王牌有效张数不变；王牌仍 14；
4. 扣费恰好一次，余额 = 旧余额 - cost；同 requestId 重放不重复扣；
5. 失败/过期/漂移/拒绝：牌位置、余额、账本、手牌、牌山均不变（“零副作用”）；
6. 私有 offer 不进入公共快照/观战/日志；
7. `entryId` 与 `requestId` 的幂等语义分别成立。

---

## 8. 事件类型（V4 19 类）

### 8.1 23/19 口径

**结论：19。** 证据：
- 当前 `GameEvent` 有 19 个子类（`GameEvent.kt:13-109`）；
- 协议 `ProtocolGameEvent` 有 19 个子类（`ProtocolModels.kt:1407-1619`）；
- 客户端 `GameEventView` 有 19 个子类（`ViewModels.kt:1277-1410`）。
- `git show ef0b0f0:engine/src/main/kotlin/com/majiang/event/GameEvent.kt` 有 23 个子类；当前相对它移除了 `CheatAttempted`、`SuspicionChanged`、`BustedEvent`、`GazeShifted` 四个旧“怀疑度/出千暴露”事件。23 = 19 + 4。
- 因此 `ProtocolViewMapper.kt:101,455`、`ProtocolModels.kt:1405`、`ViewModels.kt:1275` 的“23 种”是**过时注释**；不是当前事件数。

### 8.2 19 类事件与字段

| # | 事件 | 字段 | 可见性类别 |
|---:|---|---|---|
| 1 | `StackingStarted` | `turnNumber` | 公开 |
| 2 | `WallBuilt` | `turnNumber, wallSize=136` | 公开（不含牌序） |
| 3 | `TileDealt` | `turnNumber, player, tile` | 仅收牌者 |
| 4 | `DealingComplete` | `turnNumber` | 公开 |
| 5 | `TileDrawn` | `turnNumber, player, tile, wallRemaining` | 仅摸牌者 |
| 6 | `TileDiscarded` | `turnNumber, player, tile, isRiichiDiscard, isTsumogiri` | 公开 |
| 7 | `ChiDeclared` | `turnNumber, player, meld, fromPlayer` | 公开 |
| 8 | `PonDeclared` | `turnNumber, player, meld, fromPlayer` | 公开 |
| 9 | `KanDeclared` | `turnNumber, player, meld, kanType` | 公开 |
| 10 | `RinshanDraw` | `turnNumber, player, tile` | 仅摸牌者 |
| 11 | `RiichiDeclared` | `turnNumber, player, discardTile, isDoubleRiichi` | 公开 |
| 12 | `TsumoWin` | `turnNumber, player, winTile, yakuList, han, fu, score, breakdown` | 结算公开（不含完整手牌） |
| 13 | `RonWin` | `turnNumber, winner, loser, winTile, yakuList, han, fu, score, breakdown` | 结算公开（不含完整手牌） |
| 14 | `DoraRevealed` | `turnNumber, indicator, isUraDora` | 表宝公开；里宝仅结算授权 |
| 15 | `CheatPointsChanged` | `turnNumber, seat, amount, newBalance` | 目标私有（V4 玩家过滤未做到，见可见性文档） |
| 16 | `DrawGame` | `turnNumber, reason, tenpaiPlayers` | 公开 |
| 17 | `ScoreSettled` | `turnNumber, payments, newScores` | 公开 |
| 18 | `RoundEnded` | `turnNumber, roundWind, roundNumber` | 公开 |
| 19 | `MatchEnded` | `turnNumber, finalScores, winner` | 公开 |

事件字段来源：`GameEvent.kt:19-109`。

### 8.3 V4 的“eventless 状态变化”与 V5 冲突

V4 至少有以下已接受且改变状态、但 `events=emptyList()` 的命令：
- 袖里藏花即时 FIRST/SECOND 二选一：`ApplicationCoordinator.kt:1459-1467`，`events=emptyList()`；测试 `SleeveBloomContractTest`/`SleeveBloomServerContractTest` 明确 “eventless”。
- 偷梁换柱 SWAP/KEEP 二选一：`ApplicationCoordinator.kt:2389-2397`，`events=emptyList()`；测试 `BeamSwapHostContractTest` 明确 “eventless swap”。
- 部分准备/ ready/ lobby 状态也有无事件路径（需 P3.1 逐项列出）。

这与 V5 P3.1 的硬规则冲突：**每个已接受且改变权威状态的命令必须至少产生 1 个事件；否则 `STATE_EVENT_MISSING` 内部错误并回滚**（方案 §3.2.2）。V5 必须新增私有事件（名称/字段由 P1.4/P2.1 冻结），例如：
- `SleeveBloomResolved`（private，含 offerId/keep/结果位置）；
- `BeamSwapResolved`（private，含 offerId/action/handTileId/targetSlot）；
- `ResponseRecorded`（若记录响应改变 window 状态）；
- 其他无事件状态变更的明确事件。

这些是 **V5 新约定**，不加入 V4 的 19 类计数；新增事件默认私有（见可见性矩阵）。

---

## 9. 待裁决项（不阻塞 V4 规则冻结，但阻塞 V5 实现）

| ID | 问题 | 当前证据/默认 | 责任 |
|---|---|---|---|
| D-01 | 41 还是 42 役种 | 已核清为 42；41 是旧摘要。若 G0 复核有反证，需给出具体不可达项 | P0.2/G0（本文件结论待复核） |
| D-02 | 23 还是 19 事件 | 已核清为 19；23=19+4 个已移除旧事件 | P0.2/G0 |
| D-03 | 双响/三响 | V4 只取一个赢家；`TRIPLE_RON` 枚举未实现 | 产品 + P1.2/P1.3 |
| D-04 | 切上满贯、双倍役满、责任支付、流局满贯、烧鸡、uma/oka、三麻 | V4 均未实现 | 产品；若要，新 ruleset |
| D-05 | 连风雀头 +2 还是 +4 | 代码实际 +4；注释写 +2 | P1.2 按代码 +4 差分；若产品要 +2，新 ruleset |
| D-06 | 国士抢暗杠 | V4 未实现；只有加杠抢杠 | 产品 + P1.2/P1.3 |
| D-07 | `SLEEVE` 区域是否启用 | 当前业务不写入；袖里藏花是等额交换，不是藏牌 | P1.1/P1.4；若要启用，新 ruleset |
| D-08 | 落汗费用可配置 vs 袖/梁固定 10 | 落汗读 `costs["TILE_MARK"]` 默认 10；袖/梁必须恰好 10 | P1.4/产品；V5 若统一可配置，需定义新规则 |
| D-09 | 码牌 RNG 是否复刻 | V4 `Random(seed)+shuffle` 不是跨运行时规范 | P1.5/产品；不复刻则新 ruleset + 记录实际牌山 |
| D-10 | `roundVersion` 是否保留到 V5 线路 | V4 每局 `version` 每次 install +1；V5 计划用 table `state_version` | P2.1/P3.1；建议 Domain 保留、Wire 只暴露 `state_version`/`streamVersion`/`projectionSeq`，避免混用 |
| D-11 | 无事件状态变化如何补事件 | V4 袖/梁二选一 eventless；V5 要求每状态变更至少 1 事件 | P1.4/P3.1；新增私有事件 |
| D-12 | 出千余额/流水对其他玩家可见性 | V4 玩家快照含四座余额/局末出千 delta；公共观战不含 | P0.2 可见性 + 产品；V5 默认私有 |

---

## 10. 实际读取、验证命令与限制

### 10.1 实际读取（本任务）

核心源码：
- `engine/.../model/{Tile,TileLocation,TilePool,TileMovement}.kt`
- `engine/.../state/{GameAction,GamePhase,LegalActionProvider,ResponseWindow,ResponseResolver,GameState,StateInvariants,SeatController}.kt`
- `engine/.../rules/{YakuEvaluator,FuCalculator,ScoreCalculator,WinningScoreEvaluator,RiichiRuleEngine,FuritenRules,RiichiKanValidator,ScoreCodes}.kt`
- `engine/.../round/{RoundStateMachine,RoundTransitionPlanner,RoundResult}.kt`
- `engine/.../match/{MatchSession,MatchProgression}.kt`
- `engine/.../settlement/SettlementService.kt`
- `engine/.../event/GameEvent.kt`
- `engine/.../projection/{PublicProjectionFactory,PublicTableProjection}.kt`
- `engine/.../session/{PlayerView,SessionUpdate}.kt`
- `engine/.../access/{PlayerSnapshot,ActionReceipt,ReceiptStatus,SeatControlView,SessionView,PublicRoundRecord,PublicSessionResult}.kt`
- `engine/.../cheat/{mark,bloom,beam}/*`、`engine/.../authority/cheat/*`、`engine/.../economy/*`、`engine/.../stacking/*`
- `engine/.../authority/TableAuthority.kt`、`engine/.../application/ApplicationCoordinator.kt`（相关段落）
- `protocol/.../{server/api, transport/api, transport/tcp}/*`（版本、错误码、DTO、TCP 帧）
- `server/.../{mapping,host,idempotency,inprocess,room,tcp}/*`（快照/推送/回执/错误映射）
- `client/.../{api,mapping,session,transport}/*`（事件视图/流/重放）

测试：本文引用的 `SessionSnapshotContractTest`、`PublicProjectionTest`、`ScoreCodesAndPaymentPlanTest`、`SettlementServiceContractTest`、`MatchProgressionContractTest`、`RoundBoundaryAcceptanceTest`、`TileMarkModelContractTest`、`SleeveBloomContractTest`、`BeamSwapContractTest`、`CheatLedgerIntegrationAcceptanceTest`、`DualTransportErrorCodeConsistencyTest`、`RequestScopeReceiptStoreTest`、`PublicBroadcastEquivalenceTest`、`TcpWireTest`、`TcpMessagesTest`、`PlayerStreamEngineTest`、`SpectatorStreamEngineTest`。

### 10.2 验证命令

在 `D:\majiang` 下执行（JAVA_HOME 指向 Android Studio JBR，JDK 25；Gradle wrapper 9.7.1）：

```powershell
# 计数：19 事件、23 旧事件、42 code、10 阶段、13 合法动作
# 说明：用 grep/Select-String 静态计数；不修改任何文件。
$ge = Get-Content engine/src/main/kotlin/com/majiang/event/GameEvent.kt
($ge | Select-String '^\s*(?:data class|class|data object|object)\s+([A-Za-z0-9_]+)').Count   # 19
$old = git show ef0b0f0:engine/src/main/kotlin/com/majiang/event/GameEvent.kt
($old | Select-String '^\s*(?:data class|class|data object|object)\s+([A-Za-z0-9_]+)').Count # 23
# Yaku code 由测试/协议镜像计数为 42；GamePhase 10；ProtocolLegalAction 13（命令与结果见 §10.3）
```

```powershell
# 定向测试（实际执行；全部通过）
.\gradlew.bat :engine:test --tests "com.majiang.rules.ScoreCodesAndPaymentPlanTest" `
  --tests "com.majiang.settlement.SettlementServiceContractTest" `
  --tests "com.majiang.match.MatchProgressionContractTest" `
  --tests "com.majiang.architecture.RoundBoundaryAcceptanceTest" `
  --tests "com.majiang.session.SessionSnapshotContractTest" `
  --tests "com.majiang.projection.PublicProjectionTest" `
  --tests "com.majiang.cheat.mark.TileMarkModelContractTest" `
  --tests "com.majiang.cheat.bloom.SleeveBloomContractTest" `
  --tests "com.majiang.cheat.beam.BeamSwapContractTest" `
  --tests "com.majiang.v3.CheatLedgerIntegrationAcceptanceTest" --console=plain

.\gradlew.bat :protocol:test --tests "com.majiang.transport.tcp.TcpWireTest" `
  --tests "com.majiang.transport.tcp.TcpMessagesTest" `
  --tests "com.majiang.transport.api.ApiMessageCodecTest" `
  --tests "com.majiang.transport.api.ProtocolPublicModelsCodecTest" `
  --tests "com.majiang.transport.api.ScoreWireBoundaryTest" `
  --tests "com.majiang.transport.api.StructuredScoreCodecTest" --console=plain

.\gradlew.bat :server:test --tests "com.majiang.server.idempotency.RequestScopeReceiptStoreTest" `
  --tests "com.majiang.server.idempotency.ReceiptStoreRegressionTest" `
  --tests "com.majiang.server.inprocess.DualTransportErrorCodeConsistencyTest" `
  --tests "com.majiang.server.tcp.PublicBroadcastEquivalenceTest" `
  --tests "com.majiang.server.mark.TileMarkServerContractTest" `
  --tests "com.majiang.server.bloom.SleeveBloomServerContractTest" `
  --tests "com.majiang.server.beam.BeamSwapHostContractTest" --console=plain

.\gradlew.bat :client:test --tests "com.majiang.client.session.PlayerStreamEngineTest" `
  --tests "com.majiang.client.session.SpectatorStreamEngineTest" `
  --tests "com.majiang.client.api.PublicSettlementDetailProjectionTest" --console=plain
```

### 10.3 实际结果

| 命令 | 结果 |
|---|---|
| `:engine:test`（10 个测试类） | XML：10 files / **110 tests / 0 failures / 0 errors / 0 skipped**；`BUILD SUCCESSFUL` |
| `:protocol:test`（6 个测试类） | XML：6 files / **56 tests / 0 failures / 0 errors / 0 skipped**；`BUILD SUCCESSFUL` |
| `:server:test`（7 个测试类） | XML：7 files / **43 tests / 0 failures / 0 errors / 0 skipped**；`BUILD SUCCESSFUL` |
| `:client:test`（3 个测试类） | XML：3 files / **17 tests / 0 failures / 0 errors / 0 skipped**；`BUILD SUCCESSFUL` |

**合计 226 项定向测试通过**。这不是全量测试，也不是 V5 验收；没有运行 `gradlew test` 全量、没有 Android/Unity/IL2CPP、没有跨物理网络。

计数命令输出（摘要）：
- 当前 `GameEvent`：19；`ef0b0f0`：23；移除 4 项：`CheatAttempted, SuspicionChanged, BustedEvent, GazeShifted`。
- 测试 `expectedYakuCodes`：42；`ProtocolScoreCodes.YAKU_CODES`：42。
- `GamePhase`：10；`ProtocolLegalAction`：13；`ApiErrors.ALL`：17；`TcpWire` 帧类型：10；`TcpCloseReasons`：10。
- `RECEIPT_QUERY|receiptQuery|queryReceipt` 在 V4 源码：**0 命中**（V5 新能力，不能写成 V4 已有）。

### 10.4 限制

- 未修改 V4 生产代码/测试，未提交/推送。
- 未把“有分支”写成“已验收”：`TENHOU/CHIIHOU`、`RYUUIISOU` 等边界役仍缺逐例黄金样本；P1.2 必须补。
- 未做全量 `gradlew test`；未做 Android/Unity/IL2CPP/真机/跨物理网络。
- 本文件不构成 G0 通过；G0 需 P0.1/P0.2/P0.3 齐全并由 Codex 复核。
```
