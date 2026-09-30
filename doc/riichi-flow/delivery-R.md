# 交付文档 - Task R: 立直后自动摸切与可选暗杠推进 (Riichi Auto-Discard & Closed Kan)

## 1. 概述与任务定位

- **任务编号**：Task R
- **开发代号**：Antigravity
- **协作模式**：Codex 负责设计、测试编写、构建与代码审查；Antigravity 负责生产实现。
- **任务目标**：根据 [DESIGN.md](file:///D:/majiang/doc/riichi-flow/DESIGN.md) 契约规范，在日麻核心状态机与合法动作生成器中实现立直后的自动摸切与合规暗杠暂停机制，取代 architecture-v2 中对立直暗杠的临时限制。

---

## 2. 修改文件清单

严格遵守并行开发任务边界，未修改任何测试套件、构建脚本、其他文档或 Task K 所属文件 (`RiichiKanValidator.kt`)：

1. [`core/src/main/kotlin/com/majiang/state/LegalActionProvider.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/state/LegalActionProvider.kt)
   - 引入 `com.majiang.rules.RiichiKanValidator`。
   - 在牌山容量及岭上指针限制内 (`state.wallRemaining > 0 && state.rinshanPointer < 4`)，对已立直玩家开放合规暗杠：
     - 要求手牌门清 (`hand.isClosed`) 且当前持摸牌 (`currentDrawn != null`)；
     - 仅对闭手牌中与摸牌相同类型的牌（恰好 3 张）组合成本次候选 4 张牌；
     - 经由 `RiichiKanValidator.canDeclare(hand, kanTiles)` 严格判定待牌集合与标准形拆分不变后，生成 `GameAction.DeclareKan(player, kanTiles, MeldType.CLOSED_KAN)`；
     - 未立直玩家原全量暗杠/加杠逻辑保持不变。

2. [`core/src/main/kotlin/com/majiang/state/GameStateMachine.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/state/GameStateMachine.kt)
   - 构造器末尾增加参数 `val autoRiichiDiscard: Boolean = true`，独立于 `autoPlayAi`，保持向后兼容，默认在 `LocalGameSession` 及普通实例中开箱生效。
   - 增加辅助方法 `private fun isHumanPlayer(player: Int): Boolean`，精确以有效席位匹配 `humanPlayerIndex`，支持 0 号及非 0 号人类座位（经 Task F 修正，不根据缺失 AI 控制器回退推断）。
   - 统一在三大摸牌时机接入自动摸切 / 暂停策略：
     1. 显式 `GameAction.DrawTile` 回合动作；
     2. 轮转自动摸牌（`advanceToNextDrawInternal` → `executeDrawInternal`）；
     3. 暗杠（`MeldType.CLOSED_KAN`）后的岭上摸牌。
   - 实现核心判定与自动摸切逻辑 `checkAndExecuteAutoRiichiDiscard`：
     - 若已立直真人摸牌后存在合法自摸 (`DeclareTsumo`) 或合法暗杠 (`DeclareKan`)，暂停并维持阶段，等待真人玩家决策；
     - 若两者皆无，通过现有事务深拷贝、状态不变量物理审计 (`StateInvariants.assertValid`)、事件收集及开启他家响应窗口 (`openResponseWindow`) 自动打出摸牌，绝不跳过他家荣和/副露；
     - 自动打出的所有事件完整注入原 `ActionResult.Accepted.events`，全程处于 `isProcessingAction` 单一守卫保护下，零后台计时器，无重复 render 提交；
     - 声明立直时的宣言牌打出不受影响；成功暗杠后保留立直状态，清除全员一发，翻杠宝牌并从王牌补牌，补牌后递归重新执行暂停/摸切判定。

3. [`core/src/main/kotlin/com/majiang/screen/GameScreen.kt`](file:///D:/majiang/core/src/main/kotlin/com/majiang/screen/GameScreen.kt)
   - 在操作区域增加真人立直暂停时的简短说明提示：`RIICHI: TSUMO / KAN OR CLICK DRAW TILE TO PASS`。
   - 保留既有自摸按钮 (`TSUMO`)、暗杠按钮 (`ANKAN`) 以及点击摸牌进行摸切并放弃可选动作的交互方式。

---

## 3. 核心契约与行为实现细节

### 3.1 自动摸切与暂停时机（行为契约 1 & 4）
- 真人玩家宣告立直后，每次摸得新牌（包括普通摸牌与岭上补牌）：
  - 调用 `legalActions(player)` 检索当前合法动作集；
  - 若包含 `GameAction.DeclareTsumo` 或 `GameAction.DeclareKan`，判定为有可选动作，状态机暂停推进，停留在 `CHEAT_WINDOW` 或 `DISCARD` 阶段等待用户输入；
  - 若两者皆无，状态机自动调用 `executeAutoRiichiDiscardInternal`：
    - 构造 `GameAction.DiscardTile(player, drawnTile)`；
    - 在 `candidate` 副本上执行打牌，`isTsumogiri` 判定为 true；
    - 经 `StateInvariants.assertValid(candidate)` 审计物理 136 张牌严格守恒；
    - 提交回 `state` 并将 `TileDiscarded` 事件加入当前命令的 `allEvents`；
    - 开启响应窗口 `openResponseWindow`，供他家判定荣和 (`Ron`)、吃、碰、大明杠；
    - 若他家全员 Pass 且无人鸣牌，平滑推进至下一玩家摸牌。

### 3.2 暂停交互与放弃机制（行为契约 2）
- 暂停时，`legalActions` 仅包含 `DeclareTsumo`（若有）、`DeclareKan`（若有）以及 `DiscardTile(currentDrawn)`。
- 玩家可选择：
  - 点击 `TSUMO (WIN)` 按钮宣告自摸和牌，进入结算；
  - 点击 `ANKAN` 按钮宣告暗杠，成立暗杠并补摸岭上牌；
  - 点击当前摸牌（`currentDrawn`）执行摸切，主动放弃本次自摸或暗杠机会，并照常向场上打出摸牌进入响应窗口。
- 绝不自动代替玩家选择和牌、杠牌或 Pass。未立直真人保持手动打牌；AI 策略保持不变。

### 3.3 严格暗杠合法性（严格暗杠契约）
- 在 `LegalActionProvider` 中仅在 `wallRemaining > 0 && rinshanPointer < 4` 条件下开放暗杠。
- 仅当本巡摸牌 `currentDrawn != null` 且闭手持有同种牌 3 张时，组合成 4 张物理牌候选。
- 委托纯函数 `RiichiKanValidator.canDeclare(hand, kanTiles)` 进行严苛校验：
  - 门前清保持；
  - 本巡摸牌必须参与；
  - 其余 3 张必须确切来自闭手；
  - 排除第 5 张牌后的待牌集合 `waitsOrig == waitsPost` 且非空；
  - 原手牌所有分解必须为包含该刻子的标准形，移除该刻子后的规范化分解集合与杠后分解集合严格一致；
  - 听牌改变、待牌拆法改变、第 4 张非本巡摸牌、无补牌容量等均直接拒绝。
- 提交校验统一经由 `validateActionPreconditions` 调用 `legalActions(player)`，非法暗杠请求直接返回 `ActionResult.Rejected(ACTION_NOT_AVAILABLE)`，零副作用。

### 3.4 引擎推进与非零真人座位（行为契约 3）
- `GameStateMachine(..., autoRiichiDiscard: Boolean = true)` 独立于 `autoPlayAi`：
  - 无论 `autoPlayAi` 开关状态如何，只要 `autoRiichiDiscard == true`，真人立直玩家均享受自动摸切/暂停机制；
  - 当需要单步逐步控制的测试场景下，可显式传入 `autoRiichiDiscard = false` 关闭该自动推进。
- `isHumanPlayer(player)` 经过 Task F 修正，严格以有效席位精确匹配 `humanPlayerIndex`（`player in 0..3 && player == humanPlayerIndex`），不再将未配置 AI 控制器的席位回退推断为人类席位。默认 0 号人类席位，保留显式指定的非 0 号人类席位，并支持 `initializeMatch(-1)` 全 AI / 无人类模式。

---

## 4. 未验证项说明 (Delegated to Codex)

依据无头桥接协议要求，当前会话未执行终端命令、未修改 Gradle、未运行测试套件：
1. **验收测试套件验证**：由 Codex 运行 `./gradlew test` 或对应的立直摸切与暗杠验收套件，重点覆盖：
   - 普通摸牌自动摸切；
   - 非立直玩家手动出牌；
   - 立直后自摸暂停并可正常结算；
   - 立直后合法暗杠暂停、可杠、可放弃摸切；
   - 非法暗杠（待牌变化、拆法变化、第四张非摸牌、无补牌容量）不暂停并直接自动摸切；
   - 已有暗杠后的再次合规暗杠；
   - 岭上补牌可和（岭上开花）暂停 / 不可和自动摸切；
   - 非 0 号真人座位；
   - 自动摸切出牌正常触发他家响应窗口与荣和；
   - 非法命令零副作用；
   - `autoRiichiDiscard = false` 下的单步控制。
2. **全工程构建验证**：由 Codex 验证桌面端 (`:lwjgl3`) 与 Android 端 (`:android`) 的编译与打包。
