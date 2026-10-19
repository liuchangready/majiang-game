# MR-09a 立直/一发双份状态治理与字段显式清单 交付说明（第 4 轮返修更新版）

**交付日期**：2026-10-18  
**基线 SHA**：a36de58 (feat/mj-tcp-migration)  
**执行角色**：Anti  
**验收角色**：Claude Code / Codex  

---

## 1. 改动文件清单与意图

本次交付严格受限于任务简报及后续验收特批扩展的所有权清单，包含 3 个修改文件、4 个新建文件与 12 个特批删除的陈旧 UDP 测试文件：

### 1.1 允许修改的文件（3 个）
1. `engine/src/main/kotlin/com/majiang/state/GameState.kt`
   - 类 KDoc 末尾追加 INV1~INV6 立直/一发不变量声明、唯一写入口声明及例外清单（绝无 `*/` 序列）。
   - 在 `=== 立直 ===` 属性区块之后新增 3 个唯一写入口方法：
     - `declareRiichi(seat: Int, turn: Int, isDouble: Boolean)`：一次性写入桌面数组与席位镜像，并置一发标记与参数范围校验。
     - `setIppatsu(seat: Int, active: Boolean)`：同步更新单席桌面数组与席位镜像。
     - `clearAllIppatsu()`：循环对 0..3 席位清除一发标记。
   - 保持 `resetForNewRound` 与 `copyFrom` 直写不动（状态复制管线合法例外）。
2. `engine/src/main/kotlin/com/majiang/model/PlayerHand.kt`
   - 在 3 个镜像字段 `riichiDeclared`、`riichiTurn`、`ippatsu` 的现有 KDoc 中各追加一句同步约束与写入口限制说明；未改变原注释、未改变 `var` 修饰。
3. `engine/src/main/kotlin/com/majiang/round/RoundTransitionPlanner.kt`
   - 将原 8 处直接写入桌面数组与手牌镜像的代码全部迁移至 `candidate.declareRiichi`、`candidate.setIppatsu`、`candidate.clearAllIppatsu()`；其余所有逻辑与读操作原样保留。

### 1.2 允许新建的文件（4 个）
1. `engine/src/test/kotlin/com/majiang/state/RiichiMirrorInvariantTest.kt`
   - 覆盖真实立直流、碰断一发、暗杠断一发、新局重置、深拷贝往返、直接入口单测 6 大场景；提供 `assertRiichiInvariants(s)` 校验 INV1~INV6；并在流程后断言 136 物理守恒。
   - **第 3 轮返修修正**：针对第 3 轮 `:engine:test` 实测中 476/480 项通过但场景 1/1b/2/3 失败的问题，查明根因为 `ApplicationCoordinator.createForTest(s, ...)`（L118-119）在 `initialState.deepCopy()` 深拷贝副本上运行，`RoundStateMachine.readState()`（L108）返回内部状态的防御性副本；测试在 `handleAction` 之后必须重新通过 `coordinator.activeRound!!.readState()` 读取 `post`（或 `post1` / `post2`）状态断言，原断言直接读取未变动的初始对象 `s` 导致陈旧断言失败。已整体替换场景 1/1b/2/3 方法体，所有 `handleAction` 后断言均通过 `post` 检查，彻底消除裸 `s.` 读数；场景 4/5/6 保持不动。
   - **第 4 轮返修修正**：针对第 4 轮 `:engine:test` 实测中 479/480 项通过、仅场景 1 断言失败（`expected: <13> but was: <12>`）的问题，查明根因为响应窗口自动关闭并推进摸牌导致 `post.turnNumber` 推进到 13，而 `riichiTurn` 记录的是宣言时的巡目（12）；将场景 1 第 70-71 行原过度断言 `assertEquals(post.turnNumber, post.riichiTurn[0])` / `assertEquals(post.turnNumber, post.players[0].riichiTurn)` 逐字修改为使用事件宣言巡目对照：`assertEquals(riichiEvent.turnNumber, post.riichiTurn[0])` / `assertEquals(riichiEvent.turnNumber, post.players[0].riichiTurn)`。其余代码一律不动。
2. `engine/src/test/kotlin/com/majiang/state/GameStateFieldChecklistTest.kt`
   - 仅使用 `java.lang.reflect.*`，不引入 `kotlin-reflect`，不修改构建脚本。
   - 显式声明 32 个 `RESET_BY_RESET_FOR_NEW_ROUND` 字段与 8 个 `PRESERVED_ACROSS_ROUND` 字段；双向反射校验无多漏；断言 `resetForNewRound()` 与 `copyFrom()` 行为。
   - **第 1 轮返修修正**：针对第 1 轮编译错误，将误调用的 `tilePool.getZone(tile)` 全部替换为真实存在的 `tilePool.getLocation(tile)?.zone`（L170、L239 两处断言共 3 个调用点），全文检索 `getZone` 实现 0 残留。
3. `tools/architecture/v4-review/Mr09RiichiWriteFunnelScanTest.kt`
   - 继承 `V4AuthorityBoundariesTest` 的扫描范式与 import 集合（包名 `com.majiang.mr09scan`）；静态扫描 `engine/src/main` 下所有非豁免 Kotlin 源文件，禁止出现直接赋值模式（第 1 轮验收方注入探针验证有效，待验收方全量运行）。
4. `doc/v4-重构/ANTI-MR09A-DELIVERY.md`
   - 本交付报告。

### 1.3 特批删除的陈旧 UDP 测试文件（12 个，依据第 1 轮验收反馈与 T5 提交 07d6aae）
由于 T5 阶段（提交 `07d6aae`）全面删除了 UDP 生产链路及类型（`com.majiang.server.udp.*`、`UdpTableServer`、`UdpClient`、`UdpRoom`、`UdpFrameException`），但历史引用审计当时仅覆盖了 `src/main` 与 `src/test`，遗漏了 `tools/architecture/v4-review/` 目录。这导致自 T5 起任何 `-I` 套件编译均因找不到已删除的 UDP 类而报错。依据验收方第 1 轮特批授权，已将以下 12 个含死引用的文件彻底移除：
1. `tools/architecture/v4-review/Mr02ErrorMappingReviewTest.kt`
2. `tools/architecture/v4-review/Mr02ReconnectResourceBaselineTest.kt`
3. `tools/architecture/v4-review/V4AuthorityAcceptanceTest.kt`
44. `tools/architecture/v4-review/V4DatagramLossAcceptanceTest.kt`
5. `tools/architecture/v4-review/V4LobbyAcceptanceTest.kt`
6. `tools/architecture/v4-review/V4ReviewAcceptanceTest.kt`
7. `tools/architecture/v4-review/V4RoomDatagramAcceptanceTest.kt`
8. `tools/architecture/v4-review/V4RuntimeAcceptanceTest.kt`
9. `tools/architecture/v4-review/V4RuntimeUdpAcceptanceTest.kt`
10. `tools/architecture/v4-review/V4SpectatorUdpAcceptanceTest.kt`
11. `tools/architecture/v4-review/V4SpectatorUdpBindingRaceTest.kt`
12. `tools/architecture/v4-review/V4SpectatorUdpClientIsolationTest.kt`

**保留文件验证**：`tools/architecture/v4-review/` 目录中其余全部有效文件均完好保留（共 12 个）：
`Mr09RiichiWriteFunnelScanTest.kt`、`V4AuthorityBoundariesTest.kt`、`V4FinalArchitectureTest.kt`、`V4LobbyConfigurationAcceptanceTest.kt`、`V4ModelAcceptanceTest.kt`、`V4PreviewAcceptanceTest.kt`、`V4PublicProjectionAcceptanceTest.kt`、`V4PublicWireAcceptanceTest.kt`、`V4RoomProtocolAcceptanceTest.kt`、`V4ServerHostAcceptanceTest.kt`、`V4SpectatorBindingAcceptanceTest.kt`、`V4SpectatorRuntimeIsolationTest.kt`。

---

## 2. 8 处写点迁移对照表

| 编号 | 业务分支 | 基线行号 (a36de58) | 改前代码 | 改后代码 | 语义与等价性说明 |
|---|---|---|---|---|---|
| C1 | `DiscardTile` | L180-181 | `candidate.ippatsuActive[action.player] = false`<br/>`candidate.players[action.player].ippatsu = false` | `candidate.setIppatsu(action.player, false)` | 弃牌后解除本席一发，两侧同清等价 |
| C2 | `DeclareRiichi` (初始双写) | L200-205 & L210-211 | `candidate.riichiDeclared[action.player] = true`<br/>`candidate.riichiTurn[action.player] = candidate.turnNumber`<br/>`candidate.ippatsuActive[action.player] = true`<br/>`candidate.players[action.player].riichiDeclared = true`<br/>`candidate.players[action.player].riichiTurn = candidate.turnNumber`<br/>`candidate.players[action.player].ippatsu = true`<br/>...<br/>`val isDoubleRiichi = candidate.isFirstUninterruptedGo && candidate.players[action.player].discards.isEmpty()`<br/>`candidate.doubleRiichiDeclared[action.player] = isDoubleRiichi` | `val isDoubleRiichi = candidate.isFirstUninterruptedGo && candidate.players[action.player].discards.isEmpty()`<br/>`candidate.declareRiichi(action.player, candidate.turnNumber, isDoubleRiichi)` | 提前计算只读属性 `isDoubleRiichi` 并作为参数传入 `declareRiichi` 入口，一次性原子完成 4 桌面数组 + 3 镜像写入；后续 `scores -= 1000` / `riichiSticks++` / `isRinshanDraw = false` 原序保留 |
| C3 | `DeclareRiichi` (立直出牌后) | L217 | `candidate.ippatsuActive[action.player] = true` | `candidate.setIppatsu(action.player, true)` | 桌面侧与镜像侧同步置 true；手侧在 C2 中已为 true，两侧同写等价 |
| C4 | `DeclareKan` (`CLOSED_KAN`) | L238-241 | `for (i in 0 until 4) {`<br/>`    candidate.ippatsuActive[i] = false`<br/>`    candidate.players[i].ippatsu = false`<br/>`}` | `candidate.clearAllIppatsu()` | 暗杠破除四席一发，调用 `clearAllIppatsu()` 等价 |
| C5 | `executePonCommit` (`Pon`) | L613-616 | `for (i in 0 until 4) {`<br/>`    candidate.ippatsuActive[i] = false`<br/>`    candidate.players[i].ippatsu = false`<br/>`}` | `candidate.clearAllIppatsu()` | 碰牌破除四席一发，调用 `clearAllIppatsu()` 等价 |
| C6 | `executeChiCommit` (`Chi`) | L633-636 | `for (i in 0 until 4) {`<br/>`    candidate.ippatsuActive[i] = false`<br/>`    candidate.players[i].ippatsu = false`<br/>`}` | `candidate.clearAllIppatsu()` | 吃牌破除四席一发，调用 `clearAllIppatsu()` 等价 |
| C7 | `executeOpenKanCommit` (`OpenKan`) | L656-659 | `for (i in 0 until 4) {`<br/>`    candidate.ippatsuActive[i] = false`<br/>`    candidate.players[i].ippatsu = false`<br/>`}` | `candidate.clearAllIppatsu()` | 大明杠破除四席一发，调用 `clearAllIppatsu()` 等价 |
| C8 | `commitAddedKanAndRinshan` (`AddedKan`) | L686-689 | `for (i in 0 until 4) {`<br/>`    candidate.ippatsuActive[i] = false`<br/>`    candidate.players[i].ippatsu = false`<br/>`}` | `candidate.clearAllIppatsu()` | 加杠破除四席一发，调用 `clearAllIppatsu()` 等价 |

---

## 3. 不变量清单与读方审计表

### 3.1 立直/一发不变量（INV1～INV6）
- **INV1**：对任意席位 $i \in [0, 3]$：`riichiDeclared[i] == players[i].riichiDeclared`
- **INV2**：对任意席位 $i \in [0, 3]$：`riichiTurn[i] == players[i].riichiTurn`
- **INV3**：对任意席位 $i \in [0, 3]$：`ippatsuActive[i] == players[i].ippatsu`
- **INV4**：`riichiDeclared[i]` 为真蕴含 `riichiTurn[i] >= 0`
- **INV5**：`doubleRiichiDeclared[i]` 为真蕴含 `riichiDeclared[i]`
- **INV6**：`ippatsuActive[i]` 为真蕴含 `riichiDeclared[i]`

### 3.2 生产唯一写入口
- `GameState.declareRiichi(seat: Int, turn: Int, isDouble: Boolean)`
- `GameState.setIppatsu(seat: Int, active: Boolean)`
- `GameState.clearAllIppatsu()`

### 3.3 状态复制管线合法例外
1. `GameState.resetForNewRound()`：单局初始清零直写。
2. `GameState.copyFrom()`：快照深拷贝字段直写。
3. `PlayerHand.clear()`：手牌重置直写。
4. `PlayerHand.deepCopy()`：手牌深拷贝直写。
5. `WinningScoreEvaluator.prepareEvaluationHand()`（L331-333）：临时评估手牌副本镜像传播，不落盘主状态。

### 3.4 读方审计表
| 字段类型 | 目标字段 | 生产读方位置 | 审计结论 |
|---|---|---|---|
| 桌面权威数组 | `riichiDeclared` | `RiichiRuleEngine` (L91, L274), `RoundRuleService`, `LegalActionProvider`, `PublicTableProjection`, `PlayerView`, `RoundTransitionPlanner` (L184), `TileMarkProjectionFactory`, `BeamSwapProjectionFactory`, `SleeveBloomProjectionFactory`, `TableAuthority`, `ApplicationCoordinator` | 权威数据源，供规则判断、合法动作、投影快照直接读取 |
| 桌面权威数组 | `doubleRiichiDeclared` | `RiichiRuleEngine` (L92), `PublicTableProjection`, `PlayerView`, 各绝技 ProjectionFactory | 权威两立直数据源 |
| 桌面权威数组 | `riichiTurn` | `RoundTransitionPlanner` (L185), `PublicTableProjection`, `PlayerView` | 权威立直巡目 |
| 桌面权威数组 | `ippatsuActive` | `RiichiRuleEngine` (L93), `PublicTableProjection`, `PlayerView` | 权威一发状态 |
| 席位镜像 | `PlayerHand.riichiDeclared` | `RoundTransitionPlanner` (L551 里宝牌展示可见性判定), `WinningScoreEvaluator.prepareEvaluationHand` (L331) | 生产仅此两处读取；由 INV1 保证与桌面数组严格等价 |
| 席位镜像 | `PlayerHand.riichiTurn` | `WinningScoreEvaluator.prepareEvaluationHand` (L332) | 仅用于算分副本构造；由 INV2 保证等价 |
| 席位镜像 | `PlayerHand.ippatsu` | `WinningScoreEvaluator.prepareEvaluationHand` (L333) | 仅用于算分副本构造；由 INV3 保证等价 |

---

## 4. 字段两桶清单与 PRESERVED 桶局间写方证据

### 4.1 RESET_BY_RESET_FOR_NEW_ROUND（32 个字段）
```
wall, deadWall, deadWallReplacements, wallPointer, wallEndExclusive,
rinshanPointer, players, riichiDeclared, doubleRiichiDeclared, riichiTurn,
ippatsuActive, sameTurnFuriten, riichiFuriten, doraIndicators,
uraDoraIndicators, pendingKanDoraCount, lastDiscard, lastDiscardPlayer,
isFirstUninterruptedGo, isRinshanDraw, totalKanCount, playerKanCounts,
sleeveTiles, cheatEntries, sleeveBloomOffer, sleeveBloomUsedOnTurn,
beamSwapOffer, beamSwapUsedOnTurn, lastDrawKind, turnNumber, phase, tilePool
```

### 4.2 PRESERVED_ACROSS_ROUND（8 个字段）与写方引用
| 字段名 | 类型 | 语义 | 生产局间写方引用与业务依据 |
|---|---|---|---|
| `scores` | `IntArray(4)` | 四人点棒分数（初始 25000） | `SettlementService.tsumo/ron` 结算写入；`MatchSession` 在整场多局流转中保持点棒守恒，不随单局清零 |
| `cheatPoints` | `IntArray(4)` | 出千点数余额（初始 0） | `MatchSession.betweenRoundCheatPoints` 在局间交接写入，整场结转不随单局重置清零 |
| `riichiSticks` | `Int` | 场上沉淀立直棒数 | 和牌结算时转给和牌者，流局时累积至下一局；由 `RoundTransitionPlanner` 与 `SettlementService` 维护 |
| `honbaSticks` | `Int` | 本场数 | 连庄/流局累加，庄家下庄和牌清零；由 `SettlementService` 维护 |
| `roundWind` | `Int` | 场风 (1=東, 2=南, 3=西, 4=北) | `match/MatchProgression.kt` L255-335 在推进下一局时根据局数计算并写入 |
| `roundNumber` | `Int` | 局数 (0=東一局...) | `match/MatchProgression.kt` L255-335 在推进下一局时更新写入 |
| `dealerIndex` | `Int` | 庄家席位 (0..3) | `TableAuthority.kt` L110-112 初始写入；`MatchProgression.kt` 在轮庄时更新 |
| `currentPlayer` | `Int` | 当前行动玩家 (0..3) | `TableAuthority.kt` L110-112 初始写入；`RoundTransitionPlanner.kt` L159 开局发牌后置为 `candidate.currentPlayer = candidate.dealerIndex` |

### 4.3 任务 E 中未构造非默认值的聚合字段说明
- **字段**：`sleeveBloomOffer` (`com.majiang.cheat.bloom.SleeveBloomOffer?`) 与 `beamSwapOffer` (`com.majiang.cheat.beam.BeamSwapOffer?`)。
- **原因**：此两字段为二选一或牌山偷换的运行时挂起决策聚合对象，其生命周期为瞬态交互窗口，单局重置 `resetForNewRound()` 与全新 `GameState()` 初始化默认值均为 `null`。在 `GameStateFieldChecklistTest` 中已通过反射证实其确属 `RESET_BY_RESET_FOR_NEW_ROUND` 桶，并断言其在重置后恢复为 `null`、在 `copyFrom()` 往返中等价复制。

---

## 5. R09a-T1～T7 验收标准对照

| 标准编号 | 验收项 | 实现位置 | 对应测试 | 结果判定 |
|---|---|---|---|---|
| **R09a-T1** | 唯一写入口 | `RoundTransitionPlanner.kt` 8 处迁移；`GameState.kt` 3 入口方法 | `Mr09RiichiWriteFunnelScanTest` | 待验收方运行（第 1 轮验收方注入探针验证有效，静态扫描规则完备） |
| **R09a-T2** | 不变量成立 | `GameState.kt` `declareRiichi` / `setIppatsu` / `clearAllIppatsu` | `RiichiMirrorInvariantTest` 全部用例 | 待验收方运行（第 3 轮已修正 coordinator readState 读方，等待验收方运行验证） |
| **R09a-T3** | 行为等价 | `RoundTransitionPlanner.kt` L180, L200, L209, L230, L602, L619, L639, L666 | 既有 `:engine:test` 全套套件 | 待验收方运行（第 3 轮前 476 项既有测试已绿，等待验收方运行全量 480 项验证） |
| **R09a-T4** | 字段清单守护 | `GameStateFieldChecklistTest.kt` 显式 32+8 双桶定义 | `GameStateFieldChecklistTest` | 待验收方运行（API 修正为 `getLocation(tile)?.zone` 消除编译错误，等待验收方运行验证） |
| **R09a-T5** | 复制管线 | `GameState.copyFrom`, `PlayerHand.deepCopy` | `GameStateFieldChecklistTest`, `RiichiMirrorInvariantTest` | 待验收方运行（场景 5 与 Checklist 用例已覆盖，等待验收方运行验证） |
| **R09a-T6** | 静态扫描套件 | `tools/architecture/v4-review/Mr09RiichiWriteFunnelScanTest.kt` | `Mr09RiichiWriteFunnelScanTest` | 待验收方运行（清除 12 个陈旧 UDP 文件解除套件编译阻断，等待验收方运行验证） |
| **R09a-T7** | 零外溢 | 严格限制在 7 个所有权文件 + 特批 12 个陈旧 UDP 测试文件删除 | `git status` / `git diff` 对照基线 `a36de58` | 其余生产代码、规则、构建脚本 0 污染 |

---

## 6. 陷阱清单与返修自查结论

1. **逐字阅读检查**：已逐字核实全部涉及文件，字段名、行号与反射签名无虚构。
2. **问题 1（API 修正）逐字回读自查**：
   - 检查 `engine/src/test/kotlin/com/majiang/state/GameStateFieldChecklistTest.kt` 全文；
   - 全文搜索 `getZone` 关键字：**命中数 = 0**；
   - L170 为 `assertEquals(Zone.UNASSIGNED, s.tilePool.getLocation(tile)?.zone, "tilePool zone for $tile reset to UNASSIGNED")`；
   - L239 为 `assertEquals(src.tilePool.getLocation(tile)?.zone, dst.tilePool.getLocation(tile)?.zone)`；
   - 彻底解决由于不存在的 API 导致的编译错误。
3. **问题 2（陈旧 UDP 文件删除）自查**：
   - 特批删除列表中的 12 个文件已全部移除；
   - 目录 `tools/architecture/v4-review/` 当前保留 `.kt` 文件列表（恰好 12 个保留文件）：
     1. `Mr09RiichiWriteFunnelScanTest.kt`（MR-09a 扫描守卫）
     2. `V4AuthorityBoundariesTest.kt`
     3. `V4FinalArchitectureTest.kt`
     4. `V4LobbyConfigurationAcceptanceTest.kt`
     5. `V4ModelAcceptanceTest.kt`
     6. `V4PreviewAcceptanceTest.kt`
     7. `V4PublicProjectionAcceptanceTest.kt`
     8. `V4PublicWireAcceptanceTest.kt`
     9. `V4RoomProtocolAcceptanceTest.kt`
     10. `V4ServerHostAcceptanceTest.kt`
     11. `V4SpectatorBindingAcceptanceTest.kt`
     12. `V4SpectatorRuntimeIsolationTest.kt`
   - 上述 12 个有效文件完好保留，未发生多删或错删。
4. **问题 3（第 3 轮 RiichiMirrorInvariantTest 断言深拷贝过期数据修复）逐字回读自查**：
   - 检查 `engine/src/test/kotlin/com/majiang/state/RiichiMirrorInvariantTest.kt` 全文；
   - 确认场景 1/1b/2/3 在 `handleAction` 之后已全部使用 `val post = coordinator.activeRound!!.readState()`（及 `var post`、`post1`/`post2`）；
   - 在 `coordinator.handleAction` 之后的断言中，无任何裸 `s.` 访问，所有断言（双立直判定、立直/巡目/一发标记、不变量校验 `assertRiichiInvariants(post)`、136 物理守恒断言 `post.tilePool.assertConservation()`）均在新鲜的 `post` 快照上执行；
   - 确认场景 4/5/6、`assertRiichiInvariants`、辅助函数未被修改；
   - 确认无 rogue `*/` 字符序列或括号失衡。
5. **问题 4（第 4 轮场景 1 riichiTurn 宣言巡目过度断言修复）逐字回读自查**：
   - 逐字回读 `RiichiMirrorInvariantTest.kt` 第 60-76 行；
   - 确认 `riichiEvent` 声明（第 63 行 `val riichiEvent = (result as ActionResult.Accepted).events.filterIsInstance<GameEvent.RiichiDeclared>().single()`）在断言之前；
   - 确认仅修改了第 70-71 两行：由原 `assertEquals(post.turnNumber, post.riichiTurn[0])` / `assertEquals(post.turnNumber, post.players[0].riichiTurn)` 替换为 `assertEquals(riichiEvent.turnNumber, post.riichiTurn[0])` / `assertEquals(riichiEvent.turnNumber, post.players[0].riichiTurn)`；其余代码一律未动；
   - 断言语义修正为与立直宣言事件的巡目严格对齐，彻底消除由于响应窗口自动关闭并推进摸牌导致的当前巡目（13）与宣言巡目（12）不匹配的问题。
6. **命令边界自查**：全流程坚决未执行任何 `./gradlew` 构建、测试或 git commit 命令，严格将构建与测试留给验收方。

---

## 7. 验收方运行指令清单

请验收方代跑以下两条关键指令以完成最终验收：

```bash
# 1. 运行 engine 模块所有单元测试（验证修复后的 GameStateFieldChecklistTest 与 RiichiMirrorInvariantTest）
./gradlew :engine:test --info

# 2. 运行 v4-review 架构扫描套件（陈旧 UDP 测试已清空，验证 Mr09RiichiWriteFunnelScanTest 与保留的架构用例通过）
./gradlew test -I tools/architecture/v4-review/init.gradle.kts --info

# 3. 校验 git 变动范围
git status --short
git diff --stat a36de58
```
