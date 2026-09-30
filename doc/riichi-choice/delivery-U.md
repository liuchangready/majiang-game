# 交付记录：任务 U（立直选牌与待牌预览 UI 表现层集成）

- **任务名称**：立直选牌与待牌预览表现层 UI 交互集成 (Task U)
- **负责 Agent**：Antigravity（生产开发）
- **协同与审查**：Codex（设计、测试编写、构建、OpenGL 截图验收）
- **交付日期**：2026-09-29

---

## 1. 交付文件清单

根据任务契约与并行边界，任务 U 仅修改一个生产源文件并新增本文档：

1. `core/src/main/kotlin/com/majiang/screen/GameScreen.kt`
   - 集成 Task P 交付的 `RiichiSelectionModel` 与 `RiichiPreviewProvider` 契约；
   - 实现立直选牌模式生命周期：点击 "RIICHI" 仅开启选牌模式，不触发命令提交与点数扣除；
   - 实现手牌选牌态视觉反馈：合法立直弃牌金色双层边框轮廓、非法手牌半透明暗化、选中牌抬高 12px 叠加高亮标记与 `[SELECTED]` 提示；
   - 实现待牌预览面板（Waits Preview Panel）：左侧显示所选物理弃牌牌面，右侧以当前 `TileSkin` 展示全部对应和牌待牌，最多 13 种待牌自适应双行无截断不裁剪，未选中时展示引导提示；
   - 实现悬停预览机制：鼠标悬停在合法弃牌上临时预览对应待牌方案，移开即恢复已选方案，悬停绝不修改已选方案或触发提交；
   - 实现了 CONFIRM RIICHI（仅选中时激活）与 CANCEL 按钮（x 1030..1200, y 145..189），支持 ESC 与 Android BACK 键无副作用取消退回普通回合；
   - 理牌开关（SORT ON/OFF）无缝兼容，选择态保持物理 `Tile` 身份绑定；
   - 优化渲染与 GPU 批次顺序：桌面牌河与手牌先 `flush`，随后绘制手牌轮廓暗化及待牌面板，确保面板完全覆盖中央牌河无穿帮；
   - 提取共享手牌布局计算 `computeHandSlots()`，使 `touchDown` 在首帧渲染前即可获得即时精准的命中矩形。
2. `doc/riichi-choice/delivery-U.md`（本文档）

---

## 2. 核心功能与契约实现细节

### 2.1 选牌模式生命周期与零副作用保证

- **点击 RIICHI 仅开启模式**：
  在非选牌态下，点击 `btnRiichiRect` (x 670..810, y 145..189) 仅触发 `riichiSelectionModel.begin()`：
  - 内部判定当前合法立直动作集合非空后置 `isSelecting = true`；
  - 初始 `selectedTile = null`，绝不自动选择第一项；
  - 绝不向 `session` 提交任何动作，不扣除 1000 点立直棒，零领域层副作用。
- **取消与返回普通回合**：
  - 点击 CANCEL 按钮 (`btnCancelRiichiRect` x 1030..1200, y 145..189)；
  - 按下键盘 `ESC` 键；
  - 按下移动端 `Android BACK` 键（通过 `Gdx.input.setCatchKey(Input.Keys.BACK, true)` 捕获）；
  以上三种方式均调用 `riichiSelectionModel.cancel()`，将 `isSelecting` 与 `selectedTile` 安全重置，界面无缝恢复为普通手动出牌回合，不打牌不扣点。
- **二次点击防误触**：
  点击已处于选中状态的同一张手牌，仅继续刷新选中状态，绝不触发宣言或提交。
- **清理与防残留**：
  在 `hide()`, `dispose()`, 返回主菜单 `< MENU` 时均执行 `riichiSelectionModel.cancel()`，杜绝脏状态残留。

### 2.2 手牌视觉呈现与交互反馈

- **合法弃牌与非法牌差异化**：
  - 通过 `riichiSelectionModel.options.map { it.action.discardTile }` 收集所有合法的物理立直打牌；
  - **合法弃牌**：在手牌牌面批次 `flush` 后，绘制高饱和金色双层线框 (`Color(1.0f, 0.84f, 0.0f, 1f)`)；
  - **非法手牌**：绘制半透明黑色遮罩 (`Color(0f, 0f, 0f, 0.58f)`) 进行暗化，清晰指引玩家操作；
  - **选中弃牌**：选中牌向上抬高 12px (`baseY + 12f`)，外围绘制青白/明金高亮框 (`Color(0.2f, 1.0f, 0.85f, 1f)`)，并在牌面上方展示 `[SELECTED]` 标签。
- **手牌点击与改选**：
  - 点击合法手牌：调用 `riichiSelectionModel.select(slot.tile)`，精确绑定选中的物理牌，右侧待牌面板即刻刷新；
  - 点击非法手牌：`select(...)` 返回 `false`，界面不出牌、不修改当前已有选择；
  - 允许玩家在确认前反复改选不同的合法打牌。

### 2.3 待牌预览面板 (Waits Preview Panel)

- **固定坐标与尺寸**：
  - 面板矩形定义为 `waitsPanelRect = Rectangle(230f, 210f, 800f, 190f)`（世界坐标 x 230..1030, y 210..400）；
  - 空间位置位于手牌上方 (y > 132f)、操作按钮上方 (y > 189f)，向右延伸至 x 1030f，完全避开 `SORT: ON/OFF` 按钮 (x 1080f) 与 `< MENU` 按钮；
  - 临时覆盖中央牌河区域，退出选牌模式后立即消失。
- **左侧弃牌 + 右侧待牌全景展示**：
  - **左侧**：展示所选弃牌的真实牌面（通过当前激活的 `TileSkin.appendTileDraw` 渲染，尺寸 50x72）；
  - **右侧**：展示所有听牌牌面（同样使用 `TileSkin` 真实牌面纹理，非纯文字）；
  - **13 面待牌自适应无截断**：
    - 待牌张数 `<= 7` 时：单行水平排列（44x64 牌面，间距 52px）；
    - 待牌张数 `> 7` 时（如国士无双十三面听、九莲宝灯九面听等）：采用两行网格布局（40x58 牌面，每行至多 7 张），保证 13 种牌面完全在面板内展示，绝对不发生截断或越界；
  - **信息隔离**：绝不展示未知牌山剩余张数、和牌概率或预估番数，只展示精确的和牌面。
- **无选中引导提示**：
  未选择任何牌时，面板居中展示操作提示：
  `"RIICHI: SELECT A DISCARD TILE"`
  `"Click any golden-bordered tile in your hand to choose your Riichi discard."`
  `"Hover over a tile to preview waits. Click CONFIRM RIICHI to declare."`
- **悬停临时预览机制**：
  - 鼠标悬停在某张合法弃牌上时，面板临时展示该牌对应的和牌待牌列表，标题变为 `"PREVIEW WAITS (N):"`；
  - 鼠标移开时，立即平滑恢复当前已选牌的方案；
  - 悬停行为为纯只读预览，不修改模型的 `selectedTile`，亦不可通过悬停提交；
  - 移动端无鼠标悬停场景下，单指点击选择手牌同样具备完整的即时视觉响应与预览效果。

### 2.4 确认提交与结果仲裁响应

- **CONFIRM RIICHI 按钮**：
  - 沿用固定坐标 `btnRiichiRect` (x 670..810, y 145..189)；
  - 未选中牌时：按钮呈暗灰禁用状态 (`Color(0.30f, 0.30f, 0.30f, 0.7f)`)，文字置灰，点击无任何响应；
  - 已选中牌时：按钮高亮激活为橙金底色，白色文字 `"CONFIRM RIICHI"`；
  - 点击确认后，调用 `riichiSelectionModel.confirmationAction()` 获取精准动作 `GameAction.DeclareRiichi(session.viewerSeat, chosenPhysicalTile)` 并提交至 `session.submit(action)`。
- **Accepted / Rejected 响应机制**：
  - **Accepted**：动作被领域层接受，调用 `riichiSelectionModel.cancel()` 退出选牌模式，后续通过 `session.subscribe` 接收正式立直事件与扣点通知；
  - **Rejected**：动作被规则拒绝，立即调用 `riichiSelectionModel.cancel()` 退出选牌模式，重新拉取 `session.view()` 并刷新 `riichiSelectionModel.update(currentView)`，防止脏动作重复提交。

### 2.5 布局计算与理牌开关协同

- **渲染与触摸共享布局计算**：
  定义纯数据结构 `HandTileSlot(val rect: Rectangle, val tile: Tile, val isDrawn: Boolean)`，由 `computeHandSlots()` 统一生成；
  - `isAutoSort == false`（默认 OFF）：按 `220 + index * 58` 布局，摸牌额外偏移 24px；
  - `isAutoSort == true`（ON）：委托 `TileArranger.sortForDisplay` 生成排序后的展示牌列与摸牌索引；
  - `refreshHandTileRects()` 在场景初始化、`touchDown` 触控分发、悬停计算和渲染帧均被同步调用，消除上一帧过期矩形引发的命中偏差。
- **理牌与物理身份正交绑定**：
  - 在立直选牌过程中点击 `SORT: ON / OFF` 按钮，由于选择通过 `Tile` 实例（`copyIndex`）物理绑定，理牌重排后已选中的牌依然能在新位置精确高亮抬高，待牌面板内容保持不变。

---

## 3. 并行与权限边界遵循

1. **严格生产文件边界**：
   - 本任务仅修改 `core/src/main/kotlin/com/majiang/screen/GameScreen.kt`；
   - 未修改 Task P 所属的 `RiichiPreviewProvider.kt` 与 `RiichiSelectionModel.kt`；
   - 未修改 Task L 所属的 `RiichiRuleEngine.kt`；
   - 未修改任何测试文件、Gradle 构建配置或引擎依赖。
2. **环境权限遵循**：
   - 本轮全部工作纯粹使用内置文件读取与编辑工具完成；
   - 未执行任何 `run_command`、终端 shell 或跨代理调用；
   - 所有单测与构建工作交由协同 Agent Codex 独立执行。

---

## 4. 验收委托说明 (Delegated to Codex)

依据无头开发模式与任务分工约定，以下验证项目由 Codex 独立执行：
1. **自动化单测与流程断言**：
   - 验证首次点击立直无提交、未选择确认无提交；
   - 验证改选、取消、非法牌点击零领域副作用；
   - 验证理牌开关切换不丢失已选中的物理牌；
   - 验证七对子、国士无双 13 面听、已有暗杠情况下的面板待牌完整性；
   - 验证确认后扣除 1000 点与正常立直舍牌推进。
2. **图形与回归验证**：
   - 执行 `./gradlew test` 全量回归；
   - 通过 libGDX 无头截图或桌面端 OpenGL 检视金色轮廓、半透明暗化、待牌面板排版及按钮状态。
