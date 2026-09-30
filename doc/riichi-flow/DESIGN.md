# 立直后的自动摸切与可选暗杠

Codex 负责设计、独立验收测试与审查；Antigravity 负责生产实现。此设计取代 architecture-v2 中暂时禁用立直暗杠的限制。

## 行为契约

1. 真人立直后，每次正常摸牌、岭上补牌后：有合法自摸或合法暗杠时暂停；两者皆无时自动打出本次摸牌。自动出牌必须经过现有事务、物理审计、事件及他家响应窗口，不得跳过荣和。
2. 暂停时保留自摸、暗杠、摸切动作；点击摸牌即放弃本次可选动作并继续。不能自动选择和牌、杠或 Pass。未立直真人继续手动出牌；AI 原策略不变。
3. 自动行为位于引擎统一推进逻辑，UI 仅展示 legalActions。GameStateMachine 构造器末尾增加 autoRiichiDiscard: Boolean = true，独立于 autoPlayAi；关闭仅用于需要逐步控制的测试或调用者。默认 LocalGameSession 无需额外开关即生效。所有真人座位均适用。
4. 覆盖显式 DrawTile、自动轮转摸牌、暗杠岭上摸牌。自动动作应包含于原 Accepted.events，保留重入守卫；没有后台计时器和重复 render 提交。声明立直时选定的宣言牌不受此机制改变。

## 严格暗杠契约

规则选择参考 [WRC 2022 §8.9.1](https://www.worldriichi.org/s/WRC_Rules_2022_20220708_site.pdf)：本巡摸牌参与，听牌与原手牌结构均保持。只采用该节的暗杠限制，不变更项目其他规则。

新增纯函数对象 `com.majiang.rules.RiichiKanValidator`，接口 `fun canDeclare(hand: PlayerHand, kanTiles: List<Tile>): Boolean`。

- hand 必须门清，drawnTile 非空；kanTiles 是己方确切持有、互异的四张同种牌，包含该 drawnTile，另外三张来自 closedTiles。四张已经在手内、摸入别种牌时不可杠。只用于 CLOSED_KAN，立直后不允许 OPEN/ADDED_KAN。
- 以摸牌前 closedTiles 为原手牌，剔除原刻子后的 closedTiles 为杠后手牌。两边均枚举 34 种候选和牌；排除算上已有副露（含暗杠）后的第五张同种牌；不根据他家不可见持牌或牌山剩余张数过滤。
- 原听牌集合非空，杠前后集合相等。每一张原听牌的所有和牌分解都必须是标准形，且含该完整刻子；任何顺子/雀头的替代解释都拒绝。移除此刻子的正规分解集合，与杠后分解集合一致（面子顺序规范化）。不能只判断杠后仍然听牌或只比较待牌集合。
- 特殊形、非听牌、错误物理副本、已副露非门清等拒绝；纯校验不修改手牌。
- LegalActionProvider 仅在 wallRemaining > 0 且 rinshanPointer < 4 的现有容量限制内开放合规暗杠。提交校验必须沿用同一合法动作源，非法请求零副作用。
- 成功杠保留立直状态，取消所有一发，翻杠宝牌，从王牌补牌并保留岭上上下文，牌总量严格 136；补牌后重新执行上述暂停/摸切策略。

## 并行任务边界

- K：只新增 rules/RiichiKanValidator.kt；独立纯规则模块。不要修改 HandDecomposer、测试、构建、其他生产文件。
- R：仅修改 state/LegalActionProvider.kt、state/GameStateMachine.kt；必要时 screen/GameScreen.kt 增加立直暂停选择的简短说明。依赖 K 的固定接口，不修改其文件；保留现有按钮与摸牌点击放弃方式。不要修改测试、依赖、其他文档。
- 各任务写 doc/riichi-flow/delivery-K.md 或 delivery-R.md，列出修改与未验证项。无须运行命令：测试、构建由 Codex 完成。

## 验收重点

普通摸牌自动摸切；非立直手动；自摸暂停并可结算；合法暗杠暂停、可杠可摸切；非法暗杠不暂停；相同听牌但拆法变化拒绝；听牌变化拒绝；第四张并非本巡摸牌拒绝；已有暗杠的再次暗杠；无补牌容量拒绝；岭上可和暂停/不可和自动摸切；真人非零座位；自动出牌仍产生他家响应；非法命令与校验无副作用；完整既有测试与平台编译。
