# 交付记录：任务 M（吃碰杠牌组选择提供器与状态模型）

- **任务名称**：吃碰杠牌组选择设计 (Meld Choice Provider & Selection Model - Task M)
- **负责 Agent**：Antigravity（生产开发）
- **协同与审查**：Codex（设计、测试、构建、审查）
- **交付日期**：2026-09-29

---

## 1. 交付文件清单

根据任务契约与并行边界，任务 M 仅新增以下两个生产源文件及本文档：

1. `core/src/main/kotlin/com/majiang/session/MeldChoiceProvider.kt`
   - 定义 `enum class MeldChoiceKind { CHI, PON, KAN }`
   - 定义 `data class MeldChoice(...)`
   - 实现 `object MeldChoiceProvider { fun options(view: PlayerView, kind: MeldChoiceKind): List<MeldChoice> }`
2. `core/src/main/kotlin/com/majiang/screen/MeldSelectionModel.kt`
   - 实现 `class MeldSelectionModel`（零 libGDX 依赖的纯表现层选牌模型）
3. `doc/meld-choice/delivery-M.md`（本文档）

---

## 2. 接口实现细节与契约对应

### 2.1 `MeldChoiceProvider` (数据与合法方案适配)

- **纯只读消费与零领域副作用**：
  - 严格只消费 `view.legalActions` 中属于观察者本人（`action.player == view.viewerSeat`）的动作；
  - 绝不重新计算或猜测麻将规则，不做第二套合法性校验；
  - 严格验证 `view.viewerSeat in 0..3`，非 0 号人类座位完全隔离与适配。
- **不可变性保证**：
  - 对 `action` 内部的 `tiles`（如 `DeclarePon`、`DeclareKan`）以及 `meld.tiles`（`DeclareChi`）统一封装为不可修改集合 (`Collections.unmodifiableList`)；
  - `MeldChoice` 内的 `tiles` 与 `handTiles` 均为不可修改列表；
  - `options` 返回的外层列表亦为不可修改列表。
- **动作去重与顺序保持**：
  - 保持 `view.legalActions` 的原始遍历顺序；
  - 针对完全重复的动作（相同的动作参数、相同的精确物理手牌组合与公开目标）进行去重；
  - 相同牌种但不同物理副本（不同 `copyIndex`）或不同组合的动作完整保留，不丢失任何不同选项。
- **五种副露类型的物理身份与组合适配**：
  1. **吃 (CHI)**：
     - 从 `action.meld` 获取完整的 3 张牌、`calledTile` 与 `fromPlayer`；
     - `handTiles` 为从 `meld.tiles` 中剔除 `calledTile` 后的 2 张手牌；
     - 校验 2 张手牌的物理身份必须属于观察者当前暗牌 (`view.myAllClosedTiles`)；
     - 校验 `fromPlayer` 属于合法对手座位 (`fromPlayer in 0..3 && fromPlayer != viewerSeat`)。
  2. **碰 (PON)**：
     - `action.tiles` 提供本人 2 张手牌，必须存在于 `view.myAllClosedTiles`；
     - 公开目标来自 `view.lastDiscard` 与 `view.lastDiscardPlayer`；
     - 若 `lastDiscard` 缺失、`lastDiscardPlayer` 缺失或为本人时，视为非法公开目标，不生成伪方案；
     - `tiles` 为本人 2 张手牌拼接 `view.lastDiscard`（完整 3 张）；
     - `handTiles` 为本人 2 张手牌；`calledTile` 为 `view.lastDiscard`；`fromPlayer` 为 `view.lastDiscardPlayer`；`addedTile = null`。
  3. **大明杠 (OPEN_KAN)**：
     - `action.tiles` 提供本人 3 张手牌，必须存在于 `view.myAllClosedTiles`；
     - 公开目标来自 `view.lastDiscard` 与 `view.lastDiscardPlayer`（缺失或来源本人时不生成）；
     - `tiles` 为本人 3 张手牌拼接 `view.lastDiscard`（完整 4 张）；
     - `handTiles` 为本人 3 张手牌；`calledTile` 为 `view.lastDiscard`；`fromPlayer` 为 `view.lastDiscardPlayer`；`addedTile = null`。
  4. **暗杠 (CLOSED_KAN)**：
     - `action.tiles` 提供本人 4 张手牌，涵盖 `closedTiles` 与 `drawnTile`（通过 `view.myAllClosedTiles` 物理匹配）；
     - `tiles` 与 `handTiles` 均为本人 4 张牌；`calledTile`、`addedTile` 与 `fromPlayer` 均设为 `null`。
  5. **加杠 (ADDED_KAN)**：
     - 现有领域协议 `action.tiles` 包含 4 张牌（原碰 3 张 + 补入 1 张）；
     - 在 `view.melds[viewerSeat]` 中精确匹配原 `PON` 副露；
     - 识别出本人补入的第 4 张牌作为 `addedTile`，并严格校验其存在于 `view.myAllClosedTiles`；
     - `handTiles` 仅包含补入的 1 张牌（使得 UI 高亮仅点亮补入的 1 张手牌）；
     - `tiles` 包含原碰 3 张牌 + `addedTile`（完整 4 张，不重复追加原碰变成 7 张）；
     - `calledTile` 与 `fromPlayer` 沿用原 `PON` 的对应字段。

---

### 2.2 `MeldSelectionModel` (纯表现层选牌与分页状态机)

- **纯 Kotlin 解耦**：
  - 零 libGDX 依赖（不导入 `com.badlogic.gdx.*`），支持完全无头的单元测试。
- **状态属性契约**：
  - `isSelecting: Boolean`：是否处于选牌模式（默认 `false`）；
  - `kind: MeldChoiceKind?`：当前选牌类别（`CHI`、`PON`、`KAN`，模式外为 `null`）；
  - `options: List<MeldChoice>`：当前类别的所有候选方案（不可变列表）；
  - `selectedAction: GameAction?`：当前选中的合法动作（未选中时为 `null`）；
  - `pageIndex: Int`：当前分页索引（从 0 开始）；
  - `pageCount: Int`：总页数（计算属性，每页最多 3 个方案；无方案时为 0）；
  - `visibleOptions: List<MeldChoice>`：当前可见页的方案列表（最多 3 个，不可变切片）；
  - `selectedChoice: MeldChoice?`：当前选中动作对应的方案对象便捷查询。
- **行为与状态转换规范**：
  - `update(view: PlayerView)`：
    - 初次调用记录 `lastView`，不主动开启选牌模式；
    - 视图值结构相等（`previous == view`）时保留选牌模式、当前选项与选中状态；
    - 视图发生实质变化（`previous != view`）时立即退出选牌模式并清空选项与选择，防止提交过期或失效动作；
    - 理牌等纯表现层状态不改变 `PlayerView` 结构值，不会引发重置。
  - `begin(kind: MeldChoiceKind): Boolean`：
    - 仅在有对应方案（`options` 非空）时才进入选牌模式并返回 `true`；无方案返回 `false`；
    - 若已在选牌模式且为相同 `kind`，重复调用不会清空已选动作或重置页码；
    - 若换用不同 `kind`，重置页码为 0 并重新计算方案；
    - **预选规则**：单方案自动选中（`selectedAction = options[0].action`），多方案初始不选中（`selectedAction = null`），确认禁用。
  - `select(action: GameAction): Boolean`：
    - 仅允许选择当前可见页（`visibleOptions`）中的合法动作；
    - 跨页动作或非法动作返回 `false`，且不破坏当前已有选择。
  - `setPage(index: Int): Boolean`：
    - 仅在索引有效（`index in 0 until pageCount`）且发生页码变化（`index != pageIndex`）时翻页并清空当前选中项，返回 `true`；
    - 非法页码或相同页码返回 `false`，不改动当前状态。
  - `confirmationAction(): GameAction?`：
    - 仅在选牌模式内且已选中合法方案时返回精确原始动作；
    - 模式外或未选中时返回 `null`；
    - 本模型不自行提交动作，提交由表现层/调用方统一调度。
  - `cancel()`：
    - 退出选牌模式，清空 `kind`、`options`、`selectedAction` 并重置 `pageIndex = 0`；
    - 保留 `lastView` 引用，允许用户在同一视图下重新点击开启其他类别的选择。

---

## 3. 并行与权限边界遵循

1. **文件所有权边界**：
   - 任务 M 严格仅新增 `core/src/main/kotlin/com/majiang/session/MeldChoiceProvider.kt` 与 `core/src/main/kotlin/com/majiang/screen/MeldSelectionModel.kt` 以及本文档；
   - 未修改 `core/src/main/kotlin/com/majiang/screen/GameScreen.kt`（由任务 U 独立接入）；
   - 未修改任何测试文件、Gradle 构建配置、领域规则或状态机代码。
2. **环境权限遵循**：
   - 本任务完全采用原生文件读写工具（`view_file`、`write_to_file`）交付；
   - 零 `run_command`、零终端/shell、零 MCP 工具调用、零权限修改；
   - 无头编译与全量单测由协同 Agent Codex 独立执行与验收。
