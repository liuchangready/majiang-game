# 交付记录：任务 P（立直选牌与待牌预览）

- **任务名称**：立直选牌与待牌预览 (Riichi Choice & Waits Preview - Task P)
- **负责 Agent**：Antigravity（生产开发）
- **协同与审查**：Codex（设计、测试、构建、审查）
- **交付日期**：2026-09-29

---

## 1. 交付文件清单

根据任务契约与并行边界，任务 P 仅新增以下两个生产源文件及本文档：

1. `core/src/main/kotlin/com/majiang/session/RiichiPreviewProvider.kt`
   - 定义 `data class RiichiDiscardOption(val action: GameAction.DeclareRiichi, val waits: List<Tile>)`
   - 实现 `object RiichiPreviewProvider { fun options(view: PlayerView): List<RiichiDiscardOption> }`
2. `core/src/main/kotlin/com/majiang/screen/RiichiSelectionModel.kt`
   - 实现 `class RiichiSelectionModel`（零 libGDX 依赖纯表现层模型）
3. `doc/riichi-choice/delivery-P.md`（本文档）

---

## 2. 接口实现细节与契约对应

### 2.1 `RiichiPreviewProvider`

- **前置条件断言**：
  - 必须是观察者自己的回合：`view.isMyTurn`（即 `view.currentTurnPlayer == view.viewerSeat`）；
  - 必须处于出牌或千术窗口：`view.phase == GamePhase.DISCARD || view.phase == GamePhase.CHEAT_WINDOW`；
  - 尚未宣告立直：`view.riichiStatus.getOrNull(view.viewerSeat) != true`；
  - 不满足上述条件时直接返回不可修改的空列表 `emptyList()`。
- **合法动作来源与物理牌绑定**：
  - 仅从 `view.legalActions` 中筛选属于 `view.viewerSeat` 的 `GameAction.DeclareRiichi` 动作；
  - 对每个动作，按精确物理身份 (`==`) 从 `view.myAllClosedTiles` 移除一张；若移除失败（手牌中无此物理牌）则跳过该选项；
  - 相同牌种的不同物理副本（不同 `copyIndex`）保留独立 option，确保物理身份与动作精准绑定。
- **待牌计算与第 5 张牌过滤**：
  - 调用既有 `HandDecomposer.INSTANCE.calculateWaits(remainingClosed)` 计算基础理论待牌（支持两面/双碰/边张/嵌张/单骑、七对子、国士无双13面听、已有暗杠等）；
  - 统计剩余暗牌 (`remainingClosed`) 与观察者已有副露 (`view.melds[viewerSeat]`) 中各牌种已占张数；
  - 排除两者合计已满 4 张的第 5 张候选（`closedCounts[typeIndex] + meldCounts[typeIndex] < 4`）；
  - 绝不根据他家暗牌、牌山顺序、未公开里宝牌或公开牌河过滤理论待牌，不泄露私有信息与剩余张数。
- **结果不可变性与展示规范**：
  - 待牌列表去重并按 `typeIndex` (0..33) 升序排列；
  - 待牌统一使用 `Tile.fromTypeIndex(it)` 创建（纯展示牌，默认 `copyIndex = 0`）；
  - 内部 `waits` 及最终 `options` 均使用 `Collections.unmodifiableList` 包装，输入 `PlayerView` 不受任何修改。

### 2.2 `RiichiSelectionModel`

- **纯表现层解耦**：
  - 无参构造函数；
  - 仅引入领域和会话值对象，零 `com.badlogic.gdx.*` 依赖，可在任何单元测试环境下无头运行。
- **只读状态属性**：
  - `val isSelecting: Boolean`：是否处于立直选牌模式；
  - `val selectedTile: Tile?`：当前选中的物理弃牌；
  - `val options: List<RiichiDiscardOption>`：缓存的立直弃牌与待牌选项。
- **状态流转与事件响应**：
  - `update(view: PlayerView)`：
    - 首次初始化记录 `lastView`，不自动进入选牌模式；
    - 视图值结构相等（`previous == view`）时保留选牌模式与已选项；
    - 视图实质变化（`previous != view`）时退出选牌模式并清空选中项，刷新 options 并更新 `lastView`；
    - 排序开关等纯 UI 状态不影响 `PlayerView` 结构值，因而不会重置选牌。
  - `begin(): Boolean`：
    - 仅在 `options` 非空时成功进入选牌模式（返回 `true`）；
    - 首次进入不自动选中任何牌（`selectedTile = null`）；
    - 已处于选牌模式时再次调用 `begin()` 不重置已有选择。
  - `select(tile: Tile): Boolean`：
    - 仅在 `isSelecting == true` 且 `tile` 为 `options` 中的合法弃牌时选中；
    - 非法弃牌或未进入选牌模式返回 `false`，且保留已有选择（不出牌、不改选）；
    - 重复点击已选牌仍返回 `true`，维持选中状态，避免误宣言。
  - `previewFor(tile: Tile): RiichiDiscardOption?`：
    - 纯只读查询，无任何状态副作用，用于悬停预览等交互。
  - `confirmationAction(): GameAction.DeclareRiichi?`：
    - 仅在选牌模式内且已选中物理牌时返回对应的精确 `DeclareRiichi` 动作；
    - 纯动作获取，不自行向会话提交。
  - `cancel()`：
    - 退出选牌模式 (`isSelecting = false`) 并清空选择 (`selectedTile = null`)。

---

## 3. 并行与权限边界遵循

1. **严格文件边界**：
   - 本任务仅新建 `session/RiichiPreviewProvider.kt` 与 `screen/RiichiSelectionModel.kt`；
   - 未修改 `screen/GameScreen.kt`（留给任务 U 接入）；
   - 未修改任何测试文件、Gradle 配置、构建脚本或领域已有规则。
2. **环境权限遵循**：
   - 本轮全部工作纯粹使用内置文件读写工具完成；
   - 未执行任何 `run_command`、终端或子代理调用；
   - 无头构建与测试由协同 Agent Codex 独立执行与验收。
