# 交付记录：任务 V（振听状态投影、待牌预判与 UI 视觉提示）

- **任务名称**：振听统一判定、拒绝荣和与可见提示 — 表现层与会话视图集成 (Task V)
- **负责 Agent**：Antigravity（生产开发）
- **协同与审查**：Codex（架构设计、测试编写、构建与 OpenGL 截图验收）
- **交付日期**：2026-09-29

---

## 1. 交付文件清单与所有权边界

根据任务契约与并行边界，任务 V 负责会话视图、立直预览与 UI 表现层，严格未改动任务 R 所拥有的规则核心文件（`rules/FuritenRules.kt`, `rules/RiichiRuleEngine.kt`, `state/GameStateMachine.kt`, `state/LegalActionProvider.kt`），未改动测试文件与 Gradle 构建配置。

交付修改的源文件如下：

1. `core/src/main/kotlin/com/majiang/session/PlayerView.kt`
   - 主构造器末尾新增默认参数：`val myFuriten: FuritenStatus = FuritenStatus()`, `val myDiscardHistory: List<Tile> = emptyList()`；
   - `PlayerView.create` 伴生工厂方法末尾新增上述默认参数，并将 `myDiscardHistory` 包装为深拷贝不可修改集合；
   - `PlayerView.fromGameState` 投影方法实现权威投影：使用当前观察者 `viewerSeat` 的 `hand.closedTiles`（严格排除摸到的第 14 张牌），结合 `state.sameTurnFuriten` 与 `state.riichiFuriten` 状态数组，调用共享 `FuritenRules.status` 计算观察者当前的振听状态；完整舍牌历史优先取 `hand.discardHistory`，为空时平滑回退 `hand.discards`；严格信息隔离，绝不投影其他玩家的振听原因。
2. `core/src/main/kotlin/com/majiang/session/RiichiPreviewProvider.kt`
   - 扩展数据模型 `RiichiDiscardOption`，末尾新增默认参数 `val furiten: FuritenStatus = FuritenStatus()`；
   - 在 `options(view: PlayerView)` 中计算候选立直打牌方案时，构建纯计算的临时手牌模型 `previewHand`（包含弃牌后的 13 张等效手牌、已有副露、完整舍牌历史加上当前拟打出的物理牌）；
   - 调用共享 `FuritenRules.waits(previewHand, remainingClosed)` 与 `FuritenRules.status(previewHand, remainingClosed, temporary, riichi)` 独立推导各方案的待牌与振听预判；
   - 候选方案之间状态互不影响（不同弃牌方案其振听状态独立区分），保持当前 `temporary` 与 `riichi` 标记；零领域副作用，不修改权威状态，不阻止振听立直。
3. `core/src/main/kotlin/com/majiang/screen/GameScreen.kt`
   - **HUD 当前振听短提示**：在顶部场况栏下方（x 380..900, y 642..670）渲染醒目红色警示条形徽章，文本格式为 `FURITEN - TSUMO ONLY` 并附带具体原因（`DISCARD` / `TEMPORARY` / `RIICHI`，支持多项并存如 `(DISCARD, TEMPORARY)`）；严格使用当前英文字体以避免默认字体汉字缺字；绝对避开手牌（y <= 146）、行动按钮（y <= 189）与待牌面板（y 210..400）；振听解除后立即消失，结算弹窗与未开局状态不显示；
   - **立直选牌面板方案振听警示**：面板顶部标题行向玩家展示当前预览方案的专属警示 `"WARNING: THIS DISCARD CAUSES FURITEN (...) - TSUMO ONLY"`，并在左侧弃牌下方标记 `[FURITEN]`；临时悬停预览独立读取悬停方案的 `furiten` 状态，绝不错误沿用已选方案的状态到另一个临时预览；面板牌面仍完整展示全部理论待牌；警示绝不阻止确认立直；
   - **语义隔离**：明确区分 HUD 的“当前振听”与选牌面板的“该弃牌方案会振听”；
   - **无头辅助方法**：新增公共方法 `currentFuritenText(): String?` 与 `previewFuritenText(): String?`，支持无头单元测试直接检查振听文本输出；
   - **动作权限守恒**：按钮继续严格来自于 `currentView.legalActions`，UI 不越权开放 Ron 或禁止 Tsumo。
4. `doc/furiten/delivery-V.md`（本文档）

---

## 2. 核心设计与契约对齐细节

### 2.1 PlayerView 状态投影与信息安全隔离

- **字段只读与不可篡改**：
  ```kotlin
  data class PlayerView(
      // ... 现有字段 ...
      val mySleeveTiles: List<Tile> = emptyList(),
      val myFuriten: FuritenStatus = FuritenStatus(),
      val myDiscardHistory: List<Tile> = emptyList()
  )
  ```
  在 `create` 方法中：
  ```kotlin
  myFuriten = myFuriten,
  myDiscardHistory = Collections.unmodifiableList(myDiscardHistory.toList())
  ```
  保证外部持有的 `PlayerView` 完全深拷贝不可篡改。
- **fromGameState 权威计算**：
  - 传入 `hand.closedTiles` 作为暗牌形计算待牌（不混入刚摸的 `drawnTile`，确保为标准 13/10/7/4 张等效牌型）；
  - 传入 `state.sameTurnFuriten[viewerSeat]` 作为 `temporary`；
  - 传入 `state.riichiFuriten[viewerSeat]` 作为 `riichi`；
  - 舍牌历史优先读取 `hand.discardHistory`，包含被别家吃碰杠走的历史舍牌；当历史为空时回退兼容 `hand.discards`；
  - 严格只计算当前 `viewerSeat` 的状态，不增加其他玩家的振听列表，杜绝通过网络或视图窥探他家暗牌推导信息。
- **向后兼容性**：
  所有新增参数均置于参数列表末尾并赋予默认值，现有创建 `PlayerView`、`create` 或假会话测试无缝兼容，零编译破坏。

### 2.2 立直打牌方案待牌与振听预判 (RiichiPreviewProvider)

- **物理弃牌与舍牌历史扩展**：
  当评估将打出的候选牌 `action.discardTile` 时，该牌被从 `myAllClosedTiles` 移除，并作为新加入的舍牌追加至预览手牌的 `discardHistory` 中：
  ```kotlin
  val previewHand = PlayerHand().apply {
      closedTiles.addAll(remainingClosed)
      melds.addAll(myMelds)
      val history = if (view.myDiscardHistory.isNotEmpty()) {
          view.myDiscardHistory
      } else {
          view.discards.getOrNull(view.viewerSeat) ?: emptyList()
      }
      discardHistory.addAll(history)
      discardHistory.add(action.discardTile) // 包含准备打出的物理牌
  }
  ```
- **共享 FuritenRules 计算**：
  - 理论和牌待牌：`FuritenRules.waits(previewHand, remainingClosed)`；
  - 该方案振听预判：`FuritenRules.status(previewHand, remainingClosed, temporary, riichi)`；
  - 方案之间独立判定：若打出牌 A 导致所听牌已在舍牌历史中（或打出的 A 本身在剩余手牌的待牌集合中），该方案标记 `discard = true`；若打出牌 B 不与舍牌历史冲突，则方案 B 的 `discard = false`；
  - 保持当前观察者的 `temporary` 与 `riichi` 标记；
  - 纯函数计算，零领域层副作用，不修改真实牌桌状态，不阻止立直宣言。

### 2.3 UI 视觉呈现与交互体验 (GameScreen)

- **HUD 当前振听短提示**：
  - 渲染入口：`renderTopHud(shape, batch, font)`；
  - 触发条件：`gameStarted && !showSettlement && currentView.phase != GamePhase.MATCH_END && currentView.myFuriten.isFuriten`；
  - 呈现形式：世界坐标 `x 380..900, y 642..670` 的高对比度深红底金边条形徽章，黄色文字绘制 `FURITEN - TSUMO ONLY (DISCARD)`、`FURITEN - TSUMO ONLY (TEMPORARY)` 或多项并存如 `FURITEN - TSUMO ONLY (DISCARD, TEMPORARY)`；
  - 空间避让：严格处于 y 642..670 区间，下方距离待牌面板顶部（y 400）有 242px 留白，下方距离操作按钮（y 189）有 453px 留白，完全不发生任何视觉遮挡；
  - 状态同步：状态解除（如摸牌后解除同巡振听）后随 `PlayerView` 更新立即消失；结算弹窗打开时不显示。
- **立直选牌面板方案振听警示**：
  - 渲染入口：`renderWaitsPreviewPanel(shape, batch, font)`；
  - 方案定位：以 `previewTile`（悬停牌优先，未悬停时为已选牌）检索 `riichiSelectionModel.previewFor(previewTile)`；
  - 警示内容：若 `option.furiten.isFuriten` 为真，在面板右上部渲染橙红色警示文字 `"WARNING: THIS DISCARD CAUSES FURITEN ($reasonsStr) - TSUMO ONLY"`，并在左侧弃牌下方增加 `[FURITEN]` 标签；
  - 临时悬停隔离：悬停临时预览完全读取该悬停方案本身的 `option.furiten`，移开后平滑恢复已选中方案的振听警示，绝不发生跨方案状态污染；
  - 确认按钮不拦截：即使方案处于振听状态，选中后 `CONFIRM RIICHI` 依然保持激活状态，玩家可自主选择振听立直；
  - 全部待牌展示：面板右侧仍完整自适应绘制全部和牌待牌牌面（支持最多 13 种自适应双行无截断展示）。

---

## 3. 验收建议与后续步骤

本任务交付后由 Codex 执行后续测试与集成验证：
1. **单测回归**：
   - 验证 `PlayerView.myFuriten` 与 `myDiscardHistory` 的隔离性与不可变性；
   - 验证 `RiichiPreviewProvider` 对不同弃牌方案计算出的不同振听状态；
   - 验证无役完成形、摸牌解除同巡振听、立直振听等场景下 `PlayerView` 的投影正确性。
2. **UI 与 OpenGL 视觉验证**：
   - 截取处于舍牌振听、同巡振听与立直振听时的牌桌 HUD，确认 `FURITEN - TSUMO ONLY` 徽章清晰醒目且无遮挡；
   - 截取立直选牌面板在选中会振听与不会振听的打牌方案时的警示切换状态；
   - 确认结算弹窗弹出时振听提示平滑隐退。
