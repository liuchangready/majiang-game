# 交付记录：任务 U（吃碰杠牌组选择表现层与交互集成）

- **任务名称**：吃碰杠副露牌组选择表现层 UI 交互集成 (Task U)
- **负责 Agent**：Antigravity（生产开发）
- **协同与审查**：Codex（设计、测试编写、构建、全量验证与代码检视）
- **交付日期**：2026-09-29

---

## 1. 交付文件清单

根据任务契约与并行边界，任务 U 仅修改一个生产源文件并新增本文档，严格不侵入 Task M 所有的文件与任何测试/Gradle 配置：

1. `core/src/main/kotlin/com/majiang/screen/GameScreen.kt`
   - 公开暴露 `val meldSelectionModel = MeldSelectionModel()` 供验收断言与无头测试；
   - 彻底移除对 `canHumanPon()` / `canHumanChi()` / `canHumanKan()` 的 `firstOrNull` 直接提交，所有副露入口（包含立直后的暗杠/加杠）均路由至 `meldSelectionModel.begin(kind)`；
   - 通用化杠按钮显示标签：普通操作栏上若有杠动作统一显示 `"KAN"`，具体细分类型（`CLOSED KAN` / `ADDED KAN` / `OPEN KAN`）在候选面板内卡片上展示；
   - 完整落地统一布局契约：
     - 面板：`Rectangle(230f, 210f, 800f, 320f)`；
     - 每页 3 张卡片：`Rectangle(250f + i * 255f, 280f, 240f, 190f)`（i = 0..2）；
     - 翻页按钮：PREV `Rectangle(250f, 225f, 90f, 36f)`，NEXT `Rectangle(920f, 225f, 90f, 36f)`，页码居中显示；
     - 确认与取消按钮：CONFIRM `Rectangle(670f, 145f, 140f, 44f)`，CANCEL `Rectangle(1030f, 145f, 170f, 44f)`；
     - 快捷动作按钮：WIN `Rectangle(490f, 145f, 160f, 44f)`（按合法动作显示 `"RON (WIN)"` 或 `"TSUMO (WIN)"`），PASS `Rectangle(840f, 145f, 150f, 44f)`（仅合法 Pass 时展示与响应）；
   - 卡片多维内容呈现：
     - 吃/碰展示 3 张真实牌面，明杠/暗杠/加杠展示 4 张真实牌面；
     - 暗杠预览面向本人展示 4 张正面牌；
     - 外来牌使用橙红双层高亮线框与 `[CALLED]` 标记，标明 `FROM Pn`；
     - 加杠精细区分原碰副露与 `[ADD]` 补入的第 4 张（青蓝高亮线框）；
     - 选中状态顶部展示 `[SELECTED]`；
   - 手牌联动与高亮/暗化反馈：
     - `currentMeldHandTiles()` 精确计算参与副露的物理手牌（加杠仅高亮补入的 1 张）；
     - 悬停候选卡片临时高亮对应手牌，但绝不改变当前确认动作；
     - 参与手牌抬高 12px 叠加高亮轮廓，未参与手牌绘制半透明暗化遮罩；
     - 选牌期间点击手牌或面板空白处拦截并消费，严禁误触发普通打牌；
   - 确认与防重复提交安全屏障：
     - 提交前先核验 `session.view()` 与动作合法性，视图变化取消选择并刷新；
     - 确认时先执行 `meldSelectionModel.cancel()`，只提交选中动作原始内容一次，防止未及时接收事件的假会话重复提交；
     - 若提交被拒绝（`ActionResult.Rejected`），重新同步 `session.view()` 并刷新模型；
   - 状态互斥与生命周期安全清理：
     - 与立直选牌 (`riichiSelectionModel`) 互斥；
     - 结算、流局、开局、换局、`hide()`、`dispose()` 与返回主菜单均清理重置模型；
     - CANCEL 按钮、键盘 `ESC`、移动端 `Android BACK` 键仅退出副露面板退回普通响应状态，不提交 Pass，不关闭游戏。
2. `doc/meld-choice/delivery-U.md`（本文档）

---

## 2. 详细实现说明与契约对齐

### 2.1 统一布局与交互契约

| 界面元素 | 矩形区域 (Rectangle) | 交互与视觉规范 |
|:---|:---|:---|
| **副露选择面板** | `Rectangle(230, 210, 800, 320)` | 半透明红木/黑墨底色，双层金线边框，顶部显示标题 `CHOOSE [CHI/PON/KAN] COMBINATION` |
| **候选卡片 (i=0..2)** | `Rectangle(250 + i * 255, 280, 240, 190)` | 悬停淡绿微亮，选中青绿高光边框；显示类型标签、`FROM Pn`、牌面组合与 `[SELECTED]` |
| **翻页 PREV** | `Rectangle(250, 225, 90, 36)` | `pageIndex > 0` 激活，点击翻至上一页并清空已选项；第 1 页置灰禁用 |
| **翻页 NEXT** | `Rectangle(920, 225, 90, 36)` | `pageIndex < pageCount - 1` 激活，点击翻至下一页并清空已选项；末页置灰禁用 |
| **页码指示** | 居中于 x=630, y=249 | 格式为 `PAGE X / Y`，根据字体自适应居中测量 |
| **CONFIRM 确认** | `Rectangle(670, 145, 140, 44)` | 未选中时暗灰禁用；已选中时橙金激活，点击提交所选合法动作 |
| **CANCEL 取消** | `Rectangle(1030, 145, 170, 44)` | 暗红背景，点击立即退出选牌模式退回普通响应状态，不提交 Pass，不产生副作用 |
| **WIN 快捷按钮** | `Rectangle(490, 145, 160, 44)` | 选牌期间保留；若可荣和显示 `"RON (WIN)"`，若可自摸显示 `"TSUMO (WIN)"`，点击直接提交 |
| **PASS 跳过按钮** | `Rectangle(840, 145, 150, 44)` | 选牌期间保留；仅当当前允许 Pass 响应时展示，点击提交 `GameAction.Pass` |
| **SORT 理牌开关** | `Rectangle(1080, 58, 140, 44)` | 选牌期间保持可用，由于基于物理 `Tile` 身份，理牌重排后选中状态与手牌高亮精准保留 |

### 2.2 渲染批次与 GPU Flush 顺序保证

在 `GameScreen.render(delta: Float)` 中严格遵循以下渲染流水线：
1. 绘制台面绿呢与中央装饰框；
2. 收集各家牌河、台面信息与观察者手牌到 `tileDrawBuffer`；
3. 绘制操作按钮（若处于副露选牌模式，绘制专用的 CONFIRM/CANCEL/WIN/PASS 组合，原动作按钮不重叠绘制）；
4. **`tileDrawBuffer.flush(batch, shape, font)`**：在此处完成全场桌面牌面批次提交；
5. **绘制手牌遮罩与副露候选面板**：
   - 处于立直选牌时绘制 `renderRiichiSelectionOverlay` 与 `renderWaitsPreviewPanel`；
   - 处于副露选牌时绘制 `renderMeldSelectionOverlay` 与 `renderMeldChoicePanel`；
   - 候选面板内部牌面单独放入 `tileDrawBuffer` 并即时 `flush`；
   - **完全避免中央牌河覆盖候选面板**。
6. 绘制结算弹窗（若有）；
7. 绘制顶部场况 HUD 与振听警示栏。

### 2.3 手牌物理身份高亮与悬停机制

- **高亮手牌解析器 `currentMeldHandTiles()`**：
  1. 若鼠标当前悬停在某候选卡片上 (`hoveredMeldCardIndex != -1`)，优先返回该卡片的 `choice.handTiles`；
  2. 若未悬停卡片，则返回当前已选方案的 `choice.handTiles`；
  3. 未选择且未悬停时返回 `emptyList()`；
  4. 加杠方案中依据 Task M 契约只包含补入的 1 张牌，故加杠时手牌仅高亮补入的 1 张牌。
- **视觉反馈**：
  - `computeHandSlots()`：凡在 `currentMeldHandTiles()` 中的手牌 `isSelected = true`，向上抬高 12px；
  - `renderMeldSelectionOverlay()`：参与手牌绘制青绿高亮线框 (`Color(0.2f, 1.0f, 0.85f, 1f)`)，其余手牌覆盖半透明黑色遮罩 (`Color(0f, 0f, 0f, 0.58f)`)；
  - 悬停只改变临时高亮的手牌，绝不改变 `meldSelectionModel.selectedAction`，确认时依然提交真正选中的方案。

### 2.4 确认与防重复提交安全屏障

在 `touchDown` 针对 `btnMeldConfirmRect`：
1. 调用 `meldSelectionModel.confirmationAction()` 获取精准动作；
2. 若为空（未选中方案），直接返回 `true`，点击无效；
3. 若非空，先调用 `session.view()` 核验：
   - 验证最新视图是否与当前视图结构一致且仍包含该动作；
   - 若不合法或视图已发生变化，调用 `cancel()` 取消选择并刷新视图；
4. **先行取消选择**：调用 `meldSelectionModel.cancel()`，将 `isSelecting` 置为 `false`，彻底避免由于会话异步推迟推送视图导致用户双击产生第二次提交；
5. 调用 `session.submit(action)`；
6. 若提交结果为 `ActionResult.Rejected`，拉取最新 `session.view()` 并刷新模型。

---

## 3. 并行与权限边界遵循

1. **严格生产文件边界**：
   - 本任务仅修改 `core/src/main/kotlin/com/majiang/screen/GameScreen.kt`；
   - 严格不修改 Task M 所有的 `MeldChoiceProvider.kt` 与 `MeldSelectionModel.kt`；
   - 严格不修改任何单元测试代码、Gradle 构建配置与引擎外部依赖。
2. **环境权限遵循**：
   - 全过程纯使用内置文件读写与替换工具；
   - 未执行任何 `run_command`、终端终端调用或跨 Agent 工具；
   - 完整构建、单测套件运行与视觉审查委托协同 Agent Codex 独立验收。

---

## 4. 验收委托说明 (Delegated to Codex)

依据分工约定，以下验证项目由 Codex 独立执行：
1. **吃牌方案选择验收**：
   - 同一张打牌存在左吃、中吃、右吃等多种组合时，点击 CHI 打开面板，多方案初始不选中，CONFIRM 禁用；
   - 点击各卡片分别选中，底部手牌抬高对应 2 张牌；
   - 悬停卡片手牌临时切换高亮，移开恢复，确认提交所选精准动作。
2. **碰与明杠方案验收**：
   - 目标牌标记 `[CALLED]` 与 `FROM Pn`，使用橙红高亮框，来自对手的牌面完整清晰；
   - 确认后手牌与副露区精准物理转移。
3. **暗杠与加杠多方案分列验收**：
   - 多个暗杠或加杠同时合法时，在面板中分页分列展示；
   - 暗杠 4 张牌全部正面展示；
   - 加杠区分原碰与 `[ADD]` 牌，底部手牌仅高亮补入的 1 张牌。
4. **立直状态下暗杠选择验收**：
   - 人类立直后摸到暗杠牌，点击 KAN 开启副露选择面板；
   - 面板保留 TSUMO 按钮，自摸与暗杠并存时不丢失自摸选项。
5. **取消与安全回退**：
   - 点击 CANCEL、按 ESC 键、按 Android BACK 键退回普通操作，不提交 Pass，不改变振听；
   - 选牌态点击手牌与面板空白不出牌。
6. **全量回归与构建**：
   - 执行 `./gradlew test` 与双端构建检查。
