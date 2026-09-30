# 振听：统一判定、拒绝荣和与可见提示

Codex 设计/测试/审查，Antigravity 开发。现有基础：舍牌历史振听、sameTurnFuriten、riichiFuriten 已用于 LegalActionProvider 的荣和拦截，正常自摸入口不受限制。主要缺口是缺少状态投影/UI，以及无役完成形的隐式过牌未记临时振听。

用户给出的百度百科页面本次工具无法读取。规则核对参考 [WRC 2022 §8.11.2](https://www.worldriichi.org/s/WRC_Rules_2022_20220708_site.pdf)：任一待牌形成振听时所有待牌均不能荣和或抢杠和；自摸仍可；无役完成形被放过也产生振听。本项目继续沿用既有“同巡振听到本人下次实际摸牌解除（含岭上）；吃/碰不直接清除”的规则，不整体改成 WRC 的本人鸣牌解除变体。

## 规则契约

- 舍牌振听按当前13张等效手牌的全部理论待牌，与本人完整 discardHistory 求交，包含已被别人吃碰杠走的历史舍牌；历史为空兼容回退 discards。手牌改听且无交集可解除。不能只禁止曾丢过的那一个待牌。
- 临时振听：放过他人打出的完成形（即便无役不能荣和），直到本人下次普通/岭上摸牌。别家摸牌/鸣牌以及本人吃碰不清除。
- 立直振听：立直后放过和牌机会，持续本局；普通/岭上摸牌不能解除。新一局清空两个状态数组。
- 三种原因可以并存；任一种成立即禁止 DeclareRon，包括抢杠。只能通过合法自摸和牌，仍需满足完成形、有役及当前回合等原有条件。不能自动替玩家自摸；已有立直自摸暂停保持。
- 人类/AI 同源，非法或重复命令不记振听、不改事件/分数/手牌。已有允许和牌的 Pass/吃碰替代逻辑保留。
- 无役完成形不得为了记录振听而强迫用户点击无意义的 Pass。创建响应窗口时，对数学完成形但没有合法 Ron 的旁家记相应振听；该座位是否有吃/碰等动作仍按原窗口规则。真正有合法 Ron 的座位直到选择非 Ron 才记振听，不能提前禁止其 Ron。

## 固定共享 API

任务 R 新增 `rules/FuritenRules.kt`：

```kotlin
data class FuritenStatus(val discard: Boolean = false, val temporary: Boolean = false, val riichi: Boolean = false) {
    val isFuriten: Boolean get() = discard || temporary || riichi
}
object FuritenRules {
    fun waits(hand: PlayerHand, closedTiles: List<Tile> = hand.closedTiles): Set<Int>
    fun status(hand: PlayerHand, closedTiles: List<Tile> = hand.closedTiles,
               temporary: Boolean = false, riichi: Boolean = false): FuritenStatus
    fun completesHand(hand: PlayerHand, tile: Tile): Boolean
}
```

纯函数、无状态副作用。waits 用既有分解器，排除剩余闭手与本人副露占满4张后的第五张牌，不按公开河或牌山过滤空听。completesHand 只检查有效13张等效手牌+候选是否完成，不检查役、不检查振听。FuritenStatus 不带内部手牌/牌山信息。

`RiichiRuleEngine.isFuriten` 保留原 API，转调 FuritenRules.status。不要散落第三套算法。

任务 V 扩展 `PlayerView`（主构造器、create 和 fromGameState，末尾默认参数保持兼容）：

```kotlin
val myFuriten: FuritenStatus = FuritenStatus()
val myDiscardHistory: List<Tile> = emptyList()
```

只投影观察者自己的状态与历史，深拷贝/不可修改。不增加其他玩家振听列表。使用 `hand.closedTiles`（不把刚摸牌混入13张待牌形）与两状态数组计算当前原因。会话与假会话照旧兼容。

`RiichiDiscardOption` 新增末尾默认 `val furiten: FuritenStatus = FuritenStatus()`：预览弃牌后的剩余手牌、本人副露、完整舍牌历史 **加上准备打出的牌**，保留当前 temporary/riichi 标记。候选之间状态可能不同。用共享 FuritenRules；不修改权威状态，不禁止振听立直。

## UI

振听期间向本人显示醒目短提示 `FURITEN - TSUMO ONLY`，附原因 `DISCARD` / `TEMPORARY` / `RIICHI`，可显示多项。沿用当前英文字体以避免汉字缺字。不遮挡手牌、行动按钮或待牌面板，不对他家显示暗牌推导的状态。状态解除后立即消失；结算/未开局不显示无效提示。

立直选牌面板对被选/悬停的方案显示其振听警示，不能错误沿用已选方案的状态到另一个临时预览。面板待牌牌面仍展示全部待牌，警示不阻止确认立直。HUD 的“当前振听”与面板的“该弃牌方案会振听”区分。按钮继续来自合法动作，UI 不得绕过引擎自行开放 Ron 或禁止 Tsumo。

## 并行所有权

- R：`rules/FuritenRules.kt`、`rules/RiichiRuleEngine.kt`（仅 isFuriten）、`state/GameStateMachine.kt`（漏记无役完成形补齐/统一状态记录）、必要的 `state/LegalActionProvider.kt`。不要改 views/screens/preview/tests。
- V：`session/PlayerView.kt`、`session/RiichiPreviewProvider.kt`、`screen/GameScreen.kt`，依赖上述固定API；不要改 R 文件、测试和构建。
- 各自交付 `doc/furiten/delivery-R.md` 或 `delivery-V.md`。
- 只用原生文件读取/搜索/编辑工具。禁止 MCP 工具调用、RunCommand、shell、Gradle，headless 权限无法交互授权；不要绕过权限。所有测试和截图由 Codex 执行。不得修改测试、依赖或本设计。

## 验收

舍牌振听挡住所有待牌的荣和/抢杠但保留自摸；历史被叫走仍有效；改听解除；临时标记本人摸牌解除/他家动作不解除；立直标记摸牌仍保留与新局重置；无役完成形隐式过牌不弹无意义窗口并标记；正常合法 Ron 不提前标记；非法命令原子拒绝；非零观察者投影隔离与不可变；待牌预览候选的振听不同且不阻止立直；自摸暂停和结算；全量234项回归、平台构建、实际HUD和预览截图。
